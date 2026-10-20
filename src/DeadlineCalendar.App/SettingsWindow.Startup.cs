using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeadlineCalendar.App;

public sealed partial class SettingsWindow
{
    private void AddStartupSection(StackPanel panel)
    {
        panel.Children.Add(Section(T("启动", "Startup")));
        var enabled = new CheckBox
        {
            Content = T("登录 Windows 时启动", "Start With Windows"),
            IsChecked = _state.Preferences.StartWithWindows,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Automation(enabled, T("登录 Windows 时启动", "Start With Windows"));
        var error = Text(_state.StartupRegistrationError ?? "", 11);
        error.Foreground = Brushes.IndianRed;
        error.Visibility = _state.StartupRegistrationError is null ? Visibility.Collapsed : Visibility.Visible;
        var restoring = false;
        void SaveStartup()
        {
            if (restoring) return;
            var requested = enabled.IsChecked == true;
            if (requested == _state.Preferences.StartWithWindows) return;
            _ownUpdate = true;
            try
            {
                _state.SetStartWithWindows(requested);
                error.Text = "";
                error.Visibility = Visibility.Collapsed;
                _saved.Text = T("设置已保存", "Settings Saved");
            }
            catch (Exception exception)
            {
                restoring = true;
                try { enabled.IsChecked = _state.Preferences.StartWithWindows; }
                finally { restoring = false; }
                error.Text = T("无法更改启动设置：", "Could not change startup settings: ") + exception.Message;
                error.Visibility = Visibility.Visible;
                _saved.Text = T("启动设置保存失败", "Could not save startup settings");
            }
            finally { _ownUpdate = false; }
        }
        enabled.Checked += (_, _) => SaveStartup();
        enabled.Unchecked += (_, _) => SaveStartup();
        panel.Children.Add(enabled);
        panel.Children.Add(error);
    }
}
