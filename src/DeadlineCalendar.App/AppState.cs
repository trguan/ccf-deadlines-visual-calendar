using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using NodaTime;
using DeadlineCalendar.Core;
using DeadlineCalendar.Infrastructure;

namespace DeadlineCalendar.App;

public sealed class AppState : IDisposable
{
    private bool disposed;
    private readonly Dictionary<string, FontFamily> fontFamilies = new(StringComparer.OrdinalIgnoreCase);
    public AppStorage Storage { get; }
    public SyncService Sync { get; }
    public UserPreferences Preferences { get; }
    public WindowsStartupRegistration StartupRegistration { get; }
    public string? StartupRegistrationError { get; private set; }
    public CatalogSnapshot Catalog => Sync.Catalog ?? new CatalogSnapshot();
    public ProjectionResult Projection { get; private set; } = new();
    public ThemePalette Theme { get; private set; }
    public event Action? Changed;
    public event Action? WindowBoundsChanged;
    public event Action<double, double>? WindowSizeRequested;
    public int Revision { get; private set; }
    public DateTimeZone DisplayZone => DeadlineTime.ResolveZone(Preferences.DisplayTimeZoneId) ?? DateTimeZone.Utc;
    public AppState(string? directory = null, WindowsStartupRegistration? startupRegistration = null)
    {
        Storage = new AppStorage(directory);
        Preferences = Storage.LoadPreferences();
        StartupRegistration = startupRegistration ?? new WindowsStartupRegistration(directory);
        Sync = new SyncService(Storage);
        Theme = new ThemePalette(Preferences);
        Sync.Updated += (_, _) => Application.Current.Dispatcher.BeginInvoke(Refresh);
        SystemEvents.UserPreferenceChanged += ThemeChanged;
        SystemParameters.StaticPropertyChanged += SystemParameterChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
    }
    public string T(string zh, string en) => Preferences.Language == "zh-CN" ? zh : en;
    public TextStylePreference TextStyle(string role) => TypographyDefaults.Normalize(role,
        Preferences.TextStyles.TryGetValue(role, out var style) ? style : null);
    public FontFamily TextFont(string role)
    {
        var name = TextStyle(role).FontFamily;
        if (fontFamilies.TryGetValue(name, out var family)) return family;
        try { family = new FontFamily(name); }
        catch (ArgumentException) { family = new FontFamily("Segoe UI"); }
        fontFamilies[name] = family;
        return family;
    }
    public FontWeight TextWeight(string role) => TextStyle(role).Bold switch
    {
        true => FontWeights.Bold,
        false => FontWeights.Normal,
        _ => role == "Headline" ? FontWeights.SemiBold : FontWeights.Normal
    };
    public FontStyle TextSlant(string role) => TextStyle(role).Italic ? FontStyles.Italic : FontStyles.Normal;
    public async Task InitializeAsync() { await Sync.InitializeAsync(); Refresh(); }
    public async Task RefreshAsync() => await Sync.RefreshAsync(true);
    public void SynchronizeStartupRegistration()
    {
        // An unchecked/default preference never registers the program. For a user
        // who opted in, launching a moved portable copy repairs its saved path.
        if (!Preferences.StartWithWindows) return;
        try
        {
            StartupRegistration.SetEnabled(true);
            StartupRegistrationError = null;
        }
        catch (Exception exception)
        {
            StartupRegistrationError = exception.Message;
            System.Diagnostics.Trace.TraceError("Could not update Windows startup registration: {0}", exception.Message);
        }
    }
    public void SetStartWithWindows(bool enabled)
    {
        var previousCommand = StartupRegistration.ReadCommand();
        var previousPreference = Preferences.StartWithWindows;
        try
        {
            StartupRegistration.SetEnabled(enabled);
            Preferences.StartWithWindows = enabled;
            Storage.SavePreferences(Preferences);
        }
        catch (Exception exception)
        {
            Preferences.StartWithWindows = previousPreference;
            StartupRegistrationError = exception.Message;
            try { StartupRegistration.WriteCommand(previousCommand); }
            catch (Exception rollbackException)
            {
                throw new AggregateException("Could not restore the previous startup registration.", exception, rollbackException);
            }
            throw;
        }
        StartupRegistrationError = null;
        Refresh();
    }
    public void UpdatePreferences()
    {
        Storage.SavePreferences(Preferences);
        Refresh();
    }
    public void Refresh()
    {
        Theme = new ThemePalette(Preferences);
        Projection = DeadlineEngine.Build(Catalog, Preferences, SystemClock.Instance.GetCurrentInstant());
        Revision++;
        Changed?.Invoke();
    }
    public void SaveBounds() => Storage.SavePreferences(Preferences);
    public void RequestWindowSize(double width, double height)
    {
        if (disposed || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return;
        WindowSizeRequested?.Invoke(width, height);
    }
    internal void UpdateWindowBounds(WindowBounds bounds)
    {
        var previous = Preferences.Bounds;
        if (previous.Left == bounds.Left && previous.Top == bounds.Top && previous.Width == bounds.Width && previous.Height == bounds.Height) return;
        Preferences.Bounds = bounds;
        WindowBoundsChanged?.Invoke();
    }
    public int Priority(string key) { var i = Preferences.Followed.FindIndex(x => x.Key == key); return i < 0 ? int.MaxValue : i; }
    public Color ConferenceColor(string key) => ThemePalette.Parse(Preferences.Followed.FirstOrDefault(x => x.Key == key)?.ColorHex, Theme.Color("Accent"));
    private void ThemeChanged(object sender, UserPreferenceChangedEventArgs args) => QueueThemeRefresh();
    private void SystemParameterChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName is nameof(SystemParameters.WindowGlassColor) or nameof(SystemParameters.WindowGlassBrush))
            QueueThemeRefresh();
    }
    private void QueueThemeRefresh()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (disposed || dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        // Let WPF process the native theme/color messages and invalidate its color cache first.
        dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() => { if (!disposed) Refresh(); }));
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Resume) Application.Current.Dispatcher.BeginInvoke(async () => { Refresh(); await Sync.CheckDueAsync(); });
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        SystemEvents.UserPreferenceChanged -= ThemeChanged;
        SystemParameters.StaticPropertyChanged -= SystemParameterChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        Sync.Dispose();
    }
}
