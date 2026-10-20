param(
    [switch]$SkipTests,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]*$')]
    [string]$PackageName = 'DeadlineCalendar-win-x64'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$outputRoot = Join-Path $projectRoot 'outputs'

function Assert-NoReparsePoints([string]$Path) {
    $cursor = $Path
    while ($true) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Packaging does not follow links or junctions: $cursor"
            }
        }
        if ([StringComparer]::OrdinalIgnoreCase.Equals($cursor, $projectRoot)) { break }
        $parent = Split-Path -Parent $cursor
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $cursor) { throw 'Path is outside the repository.' }
        $cursor = $parent
    }
}

function Assert-ManagedOutputPath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $prefix = $outputRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to change a path outside the repository outputs directory: $fullPath"
    }
    Assert-NoReparsePoints $fullPath
    return $fullPath
}

function Remove-ManagedArtifact([string]$Path) {
    $safePath = Assert-ManagedOutputPath $Path
    if (-not (Test-Path -LiteralPath $safePath)) { return }
    $item = Get-Item -LiteralPath $safePath -Force
    if ($item.PSIsContainer) {
        $linkedChild = Get-ChildItem -LiteralPath $safePath -Recurse -Force |
            Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
            Select-Object -First 1
        if ($null -ne $linkedChild) { throw "Refusing to remove an output directory containing a link: $safePath" }
    }
    Remove-Item -LiteralPath $safePath -Recurse -Force
}

# Use the Git index as the source-package boundary, including files staged before
# the first commit. An extracted source ZIP can build, but packaging requires a
# Git checkout; never fall back to recursively collecting untracked local files.
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Packaging requires Git and a Git checkout.' }
try { $gitRoot = & git -C $projectRoot rev-parse --show-toplevel 2>$null }
catch { throw 'Packaging requires this repository Git checkout. An extracted source ZIP can run build.ps1; use a Git checkout to create packages.' }
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitRoot) -or
    -not [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($gitRoot), $projectRoot)) {
    throw 'Packaging requires this repository Git checkout. An extracted source ZIP can run build.ps1; use a Git checkout to create packages.'
}
$trackedOutput = & git -c core.quotepath=false -C $projectRoot ls-files --cached -z
if ($LASTEXITCODE -ne 0) { throw 'Could not read the Git tracked-file list.' }
$trackedPaths = @(($trackedOutput -join "`n").Split([char]0) | Where-Object { $_ } | Sort-Object -Unique)
if ($trackedPaths.Count -eq 0) { throw 'No tracked or staged source files were found. Stage the intended source files before packaging.' }
$sourceFiles = @()
foreach ($relativeName in $trackedPaths) {
    if ($relativeName -match '(?i)(^|/)(\.git|\.tools|bin|obj|outputs|work|TestResults|coverage)(/|$)|\.(zip|exe|dll|pdb|log|tmp|trx)$') { continue }
    $fullPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $relativeName))
    if (-not $fullPath.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Tracked path is outside the repository: $relativeName"
    }
    Assert-NoReparsePoints $fullPath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Tracked source file is missing or is not a regular file: $relativeName. Update the Git index before packaging."
    }
    $sourceFiles += [pscustomobject]@{ RelativeName = $relativeName; FullName = $fullPath }
}
if ($sourceFiles.Count -eq 0) { throw 'No source files remain after excluding generated artifacts.' }

$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
$publishDirectory = Assert-ManagedOutputPath (Join-Path $outputRoot $PackageName)
$binaryZip = Assert-ManagedOutputPath (Join-Path $outputRoot ($PackageName + '.zip'))
$sourceZip = Assert-ManagedOutputPath (Join-Path $outputRoot 'DeadlineCalendar-source.zip')
if ([StringComparer]::OrdinalIgnoreCase.Equals($binaryZip, $sourceZip)) { throw 'PackageName must not be DeadlineCalendar-source.' }
$runId = [Guid]::NewGuid().ToString('N')
$stagingDirectory = Assert-ManagedOutputPath (Join-Path $outputRoot ('.publish-' + $runId))
$temporaryBinaryZip = Assert-ManagedOutputPath (Join-Path $outputRoot ('.binary-' + $runId + '.zip'))
$temporarySourceZip = Assert-ManagedOutputPath (Join-Path $outputRoot ('.source-' + $runId + '.zip'))
foreach ($temporaryPath in @($stagingDirectory, $temporaryBinaryZip, $temporarySourceZip)) {
    if (Test-Path -LiteralPath $temporaryPath) { throw 'A unique packaging temporary path already exists.' }
}
Push-Location $projectRoot
try {
    & (Join-Path $PSScriptRoot 'build.ps1') -SkipTests:$SkipTests
    [IO.Directory]::CreateDirectory($stagingDirectory) | Out-Null
    & $dotnetCommand publish (Join-Path $projectRoot 'src\DeadlineCalendar.App\DeadlineCalendar.App.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $stagingDirectory --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    # Only tracked documentation and licenses may accompany the runtime. In
    # particular, a local README draft is never copied into a release package.
    foreach ($file in $sourceFiles) {
        if ($file.RelativeName -match '^(docs|licenses)/' -or
            $file.RelativeName -in @('README.md', 'THIRD-PARTY-NOTICES.md', 'LICENSE', 'LICENSE.txt', 'LICENSE.md', 'src/DeadlineCalendar.App/Assets/logo.png')) {
            $destination = Assert-ManagedOutputPath (Join-Path $stagingDirectory $file.RelativeName)
            [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
    }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stagingDirectory, $temporaryBinaryZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    $stream = [IO.File]::Open($temporarySourceZip, [IO.FileMode]::CreateNew)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in $sourceFiles) {
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $file.RelativeName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }

    # Replace managed outputs only after publishing and both ZIP creations pass.
    Remove-ManagedArtifact $publishDirectory
    [void](Assert-ManagedOutputPath $stagingDirectory)
    [void](Assert-ManagedOutputPath $publishDirectory)
    Move-Item -LiteralPath $stagingDirectory -Destination $publishDirectory
    foreach ($pair in @(@($temporaryBinaryZip, $binaryZip), @($temporarySourceZip, $sourceZip))) {
        [void](Assert-ManagedOutputPath $pair[0])
        [void](Assert-ManagedOutputPath $pair[1])
        Move-Item -LiteralPath $pair[0] -Destination $pair[1] -Force
    }
    Get-FileHash -LiteralPath $binaryZip,$sourceZip -Algorithm SHA256 | Format-Table -AutoSize
} finally {
    try {
        foreach ($temporaryPath in @($stagingDirectory, $temporaryBinaryZip, $temporarySourceZip)) {
            Remove-ManagedArtifact $temporaryPath
        }
    } finally { Pop-Location }
}
