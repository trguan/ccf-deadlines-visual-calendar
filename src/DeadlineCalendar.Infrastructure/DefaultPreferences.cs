using System.Text.Json;
using DeadlineCalendar.Core;

namespace DeadlineCalendar.Infrastructure;

/// <summary>Creates independent preferences from the application's embedded initial configuration.</summary>
public static class DefaultPreferences
{
    private const string ResourceName = "DeadlineCalendar.Infrastructure.default-preferences.json";

    /// <summary>Returns a fresh object graph. Existing user files are never merged with these defaults.</summary>
    public static UserPreferences Create()
    {
        using var stream = typeof(DefaultPreferences).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The embedded default preferences are missing.");
        return JsonSerializer.Deserialize<UserPreferences>(stream)
            ?? throw new InvalidDataException("The embedded default preferences must contain a configuration object.");
    }
}
