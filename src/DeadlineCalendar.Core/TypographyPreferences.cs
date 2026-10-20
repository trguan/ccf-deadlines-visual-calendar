namespace DeadlineCalendar.Core;

public sealed class TextStylePreference
{
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 13;
    public bool? Bold { get; set; }
    public bool Italic { get; set; }
}

public static class TypographyDefaults
{
    public static IReadOnlyList<string> Roles { get; } = Array.AsReadOnly(new[]
    {
        "Headline", "Deadline", "Countdown", "TooltipTitle", "TooltipBody", "DetailTitle", "DetailBody", "Badge", "Settings"
    });

    public static TextStylePreference Get(string role) => new()
    {
        FontFamily = "Segoe UI",
        FontSize = role switch
        {
            "Headline" => 27,
            "Deadline" => 18,
            "Countdown" => 17,
            "TooltipTitle" => 14,
            "TooltipBody" => 11,
            "DetailTitle" => 22,
            "DetailBody" => 13,
            "Badge" => 9,
            "Settings" => 13,
            _ => 13
        }
    };

    public static TextStylePreference Normalize(string role, TextStylePreference? style)
    {
        var defaults = Get(role);
        var family = style?.FontFamily?.Trim();
        return new TextStylePreference
        {
            FontFamily = !string.IsNullOrEmpty(family) && family.Length <= 128 && !family.Any(char.IsControl) ? family : defaults.FontFamily,
            FontSize = style is not null && double.IsFinite(style.FontSize) ? Math.Clamp(style.FontSize, 8, 64) : defaults.FontSize,
            Bold = style?.Bold,
            Italic = style?.Italic ?? false
        };
    }
}
