using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;

namespace DeadlineCalendar.App;

public partial class App : Application
{
    private Mutex? mutex;
    private EventWaitHandle? activation;
    private RegisteredWaitHandle? activationWait;
    private AppState? state;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var index = Array.IndexOf(e.Args, "--data-dir");
        string? directory = index >= 0 && index + 1 < e.Args.Length ? Path.GetFullPath(e.Args[index + 1]) : null;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory ?? "default")))[..16];
        mutex = new Mutex(true, "Local\\DeadlineCalendar-" + key, out bool first);
        activation = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\DeadlineCalendar-Activate-" + key);
        if (!first) { activation.Set(); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            try { File.AppendAllText(Path.Combine(state?.Storage.DirectoryPath ?? Path.GetTempPath(), "app-errors.log"), DateTimeOffset.Now + " " + args.Exception + Environment.NewLine); } catch { }
            MessageBox.Show(args.Exception.Message, "Deadline Calendar");
            args.Handled = true;
        };
        state = new AppState(directory);
        state.SynchronizeStartupRegistration();
        var window = new MainWindow(state);
        if (e.Args.Contains("--show-taskbar")) window.ShowInTaskbar = true;
        MainWindow = window;
        activationWait = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => Dispatcher.BeginInvoke(window.Restore), null, Timeout.Infinite, false);
        window.Show();
        await state.InitializeAsync();
        if (!state.Preferences.FirstRunCompleted)
        {
            state.Preferences.FirstRunCompleted = true;
            state.UpdatePreferences();
            window.ShowSettings();
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        activationWait?.Unregister(null);
        activation?.Dispose();
        state?.Dispose();
        mutex?.Dispose();
        base.OnExit(e);
    }
}
