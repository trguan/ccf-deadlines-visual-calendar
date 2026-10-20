using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using DeadlineCalendar.Core;

namespace DeadlineCalendar.App;

public sealed class ThemePalette
{
    private readonly UserPreferences prefs;
    private readonly Color systemAccent;
    public bool IsDark { get; }
    public ThemePalette(UserPreferences preferences)
    {
        prefs = preferences;
        systemAccent = ReadSystemAccent();
        try { IsDark = (int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0; }
        catch { IsDark = false; }
    }
    public Color Color(string role)
    {
        string? hex = role switch
        {
            "Background" => IsDark ? "#171D28" : "#F8FAFC",
            "Foreground" => IsDark ? "#F1F5F9" : "#172033",
            "Muted" => IsDark ? "#9CAAC0" : "#66768A",
            "Past" => IsDark ? "#94A3B8" : "#64748B",
            "Separator" => IsDark ? "#364155" : "#DDE5EF",
            _ => null
        };
        var fallback = role == "TooltipBackground" ? Color("Background") : hex is null ? systemAccent : Parse(hex, systemAccent);
        return prefs.ColorOverrides.TryGetValue(role, out var custom) ? Parse(custom, fallback) : fallback;
    }
    private static Color ReadSystemAccent()
    {
        try
        {
            var accent = SystemColors.AccentColor;
            // Use the modern Windows accent; legacy DWM glass colors can differ from it.
            // Window opacity is a separate preference, so the color itself remains opaque.
            return System.Windows.Media.Color.FromRgb(accent.R, accent.G, accent.B);
        }
        catch
        {
            Color accent;
            try { accent = SystemParameters.WindowGlassColor; }
            catch { accent = SystemColors.HighlightColor; }
            return System.Windows.Media.Color.FromRgb(accent.R, accent.G, accent.B);
        }
    }
    public SolidColorBrush Brush(string role) => new(Color(role));
    public static Color Parse(string? hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex ?? ""); } catch { return fallback; }
    }
}
