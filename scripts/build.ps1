param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
Push-Location $projectRoot
try {
    & $dotnetCommand build DeadlineCalendar.slnx -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $testRoot = Join-Path $projectRoot 'tests'
    if (-not $SkipTests -and (Test-Path -LiteralPath $testRoot -PathType Container)) {
        # Tests are kept locally and are optional in public source checkouts.
        foreach ($testProject in (Get-ChildItem -LiteralPath $testRoot -Filter '*.csproj' -Recurse -File | Sort-Object FullName)) {
            & $dotnetCommand test $testProject.FullName -c Release --nologo
            if ($LASTEXITCODE -ne 0) { throw "Tests failed: $($testProject.Name)" }
        }
    }
} finally { Pop-Location }
