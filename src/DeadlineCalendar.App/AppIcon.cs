using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeadlineCalendar.App;

internal static class AppIcon
{
    private static readonly Uri ResourceUri = new("/DeadlineCalendar;component/Assets/app.ico", UriKind.Relative);
    private static readonly Uri SettingsResourceUri = new("/DeadlineCalendar;component/Assets/settings.ico", UriKind.Relative);

    public static ImageSource WindowImage { get; } = LoadWindowImage(ResourceUri);
    public static ImageSource SettingsWindowImage { get; } = LoadWindowImage(SettingsResourceUri);

    public static System.Drawing.Icon CreateTrayIcon()
    {
        using var stream = OpenResource(ResourceUri);
        using var icon = new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        // Return an independent icon that the caller can dispose when the tray exits.
        return (System.Drawing.Icon)icon.Clone();
    }

    private static ImageSource LoadWindowImage(Uri uri)
    {
        using var stream = OpenResource(uri);
        var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        image.Freeze();
        return image;
    }

    private static Stream OpenResource(Uri uri) => Application.GetResourceStream(uri)?.Stream
        ?? throw new InvalidOperationException("The application icon resource is missing.");
}
