using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace DeadlineCalendar.App;

/// <summary>Manages only this application's per-user startup value. Construction has no registry side effects.</summary>
public sealed class WindowsStartupRegistration
{
    public const string ValueName = "DeadlineCalendar";
    public const string RunSubKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string registrySubKeyPath;
    private readonly string? dataDirectory;

    public WindowsStartupRegistration(string? dataDirectory = null, string? registrySubKeyPath = null)
    {
        this.dataDirectory = dataDirectory;
        this.registrySubKeyPath = registrySubKeyPath ?? RunSubKeyPath;
        if (string.IsNullOrWhiteSpace(this.registrySubKeyPath) || this.registrySubKeyPath.StartsWith('\\'))
            throw new ArgumentException("The registry subkey must be a nonempty path relative to HKEY_CURRENT_USER.", nameof(registrySubKeyPath));
        RejectControlCharacters(this.registrySubKeyPath, nameof(registrySubKeyPath));
    }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(ReadCommand());

    public string? ReadCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(registrySubKeyPath, writable: false);
        return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    /// <summary>Writes an exact command as REG_SZ; null removes only this application's value.</summary>
    public void WriteCommand(string? command)
    {
        if (command is null)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(registrySubKeyPath, writable: true);
            existing?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("The startup command must not be empty.", nameof(command));
        RejectControlCharacters(command, nameof(command));
        using var key = Registry.CurrentUser.CreateSubKey(registrySubKeyPath, writable: true)
            ?? throw new IOException("The per-user startup registry key could not be opened.");
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    public string BuildCurrentCommand() => BuildCommand(
        Environment.ProcessPath ?? throw new InvalidOperationException("The current executable path is unavailable."),
        Assembly.GetEntryAssembly()?.Location,
        dataDirectory);

    public void SetEnabled(bool enabled) => WriteCommand(enabled ? BuildCurrentCommand() : null);

    /// <summary>Builds a startup command from explicit paths, without copying transient process arguments.</summary>
    public static string BuildCommand(string processPath, string? entryAssemblyPath = null, string? dataDirectory = null)
    {
        var executable = NormalizePath(processPath, nameof(processPath));
        if (!string.IsNullOrEmpty(entryAssemblyPath)) ValidatePathText(entryAssemblyPath, nameof(entryAssemblyPath));
        var command = QuotePath(executable);
        var executableName = Path.GetFileName(executable);
        if (StringComparer.OrdinalIgnoreCase.Equals(executableName, "dotnet.exe") ||
            StringComparer.OrdinalIgnoreCase.Equals(executableName, "dotnet"))
        {
            var assembly = NormalizePath(entryAssemblyPath, nameof(entryAssemblyPath));
            command += " " + QuotePath(assembly);
        }

        if (dataDirectory is not null)
            command += " --data-dir " + QuotePath(NormalizePath(dataDirectory, nameof(dataDirectory)));
        return command;
    }

    private static string NormalizePath(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The path must not be empty.", parameterName);
        ValidatePathText(value, parameterName);
        return Path.GetFullPath(value);
    }

    private static void ValidatePathText(string value, string parameterName)
    {
        if (value.Contains('"'))
            throw new ArgumentException("Paths must not contain embedded quotation marks.", parameterName);
        RejectControlCharacters(value, parameterName);
    }

    private static void RejectControlCharacters(string value, string parameterName)
    {
        foreach (var character in value)
            if (char.IsControl(character))
                throw new ArgumentException("The value must not contain control characters.", parameterName);
    }

    private static string QuotePath(string value)
    {
        // Embedded quotes were rejected above. Windows argument parsing therefore
        // requires escaping only the backslashes immediately before our closing quote.
        var trailingBackslashes = 0;
        for (var index = value.Length - 1; index >= 0 && value[index] == '\\'; index--)
            trailingBackslashes++;
        return "\"" + value + new string('\\', trailingBackslashes) + "\"";
    }
}
