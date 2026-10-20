using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using DeadlineCalendar.Core;
using NodaTime;

namespace DeadlineCalendar.App;

/// <summary>Immediate, per-user settings. Editing a text field never rebuilds the active control.</summary>
public sealed partial class SettingsWindow : Window
{
    private readonly AppState _state;
    private readonly Dictionary<string, bool> _editionExpansion = new();
    private readonly HashSet<double> _settingsTextSizes = [];
    private readonly List<Action> _refreshTypographyEditors = [];
    private readonly List<Action> _refreshColorEditors = [];
    private List<string>? _installedFontFamilies;
    private static readonly string[] ConferenceColors = ["#4F8FE8", "#E78667", "#A78BFA", "#36B5A0", "#E5AC42", "#DB79B0", "#5FBCD3", "#96B85B"];
    private TabControl _tabs = null!;
    private ListBox _catalogList = null!;
    private StackPanel _followedPanel = null!;
    private StackPanel _detailPanel = null!;
    private ScrollViewer _detailScroll = null!;
    private StackPanel _dataPanel = null!;
    private TextBlock _count = null!;
    private TextBlock _saved = null!;
    private TextBox? _windowWidthInput, _windowHeightInput;
    private string _renderedWidth = "", _renderedHeight = "";
    private string _query = "";
    private string _category = "AI";
    private string? _selectedKey;
    private string? _renderedDetailKey;
    private bool _ownUpdate;
    private bool _renderingCatalog;
    private bool _closed;
    private CatalogSnapshot? _renderedCatalog;
    private string _renderedLanguage = "";

    public SettingsWindow(AppState state)
    {
        _state = state;
        Icon = AppIcon.SettingsWindowImage;
        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(1, workArea.Width - 24);
        var availableHeight = Math.Max(1, workArea.Height - 24);
        MinWidth = Math.Min(760, availableWidth);
        MinHeight = Math.Min(620, availableHeight);
        Width = Math.Min(1040, availableWidth);
        Height = Math.Min(790, availableHeight);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = _state.TextFont("Settings");
        FontSize = _state.TextStyle("Settings").FontSize;
        UseLayoutRounding = true;
        InstallStyles();
        Build();
        _state.Changed += OnStateChanged;
        _state.WindowBoundsChanged += OnWindowBoundsChanged;
        Loaded += (_, _) => FitToCurrentWorkArea();
        Closed += (_, _) => { _closed = true; _state.Changed -= OnStateChanged; _state.WindowBoundsChanged -= OnWindowBoundsChanged; };
    }

    private string T(string zh, string en) => _state.T(zh, en);

    private void FitToCurrentWorkArea()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var area = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var working = new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY, area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY);
        var availableWidth = Math.Max(1, working.Width - 24);
        var availableHeight = Math.Max(1, working.Height - 24);
        MinWidth = Math.Min(760, availableWidth);
        MinHeight = Math.Min(620, availableHeight);
        Width = Math.Clamp(Width, MinWidth, availableWidth);
        Height = Math.Clamp(Height, MinHeight, availableHeight);
        Left = Math.Clamp(Left, working.Left + 12, Math.Max(working.Left + 12, working.Right - Width - 12));
        Top = Math.Clamp(Top, working.Top + 12, Math.Max(working.Top + 12, working.Bottom - Height - 12));
    }

    private void InstallStyles()
    {
        var dictionary = (ResourceDictionary)XamlReader.Parse("""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style TargetType="TextBlock"><Setter Property="FontFamily" Value="{DynamicResource SettingsFontFamily}"/></Style>
              <Style TargetType="Button">
                <Setter Property="Background" Value="{DynamicResource SurfaceBrush}"/><Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource SeparatorBrush}"/><Setter Property="BorderThickness" Value="1"/>
                <Setter Property="Padding" Value="12,7"/><Setter Property="Cursor" Value="Hand"/><Setter Property="VerticalContentAlignment" Value="Center"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
                  <Border x:Name="Surface" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6" Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
                  </Border><ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Surface" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Surface" Property="Opacity" Value="0.45"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="TextBox">
                <Setter Property="Background" Value="{DynamicResource SurfaceBrush}"/><Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/>
                <Setter Property="CaretBrush" Value="{DynamicResource ForegroundBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource SeparatorBrush}"/>
                <Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="8,7"/><Setter Property="VerticalContentAlignment" Value="Center"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox">
                  <Border x:Name="Border" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="5">
                    <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}"/>
                  </Border><ControlTemplate.Triggers><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Border" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger></ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="CheckBox"><Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="VerticalContentAlignment" Value="Center"/><Setter Property="Cursor" Value="Hand"/></Style>
              <Style TargetType="ComboBox">
                <Setter Property="Background" Value="{DynamicResource SurfaceBrush}"/><Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource SeparatorBrush}"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox">
                  <Grid>
                    <ToggleButton x:Name="DropDownToggle" Focusable="False" IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                      <ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border CornerRadius="5" BorderThickness="1" BorderBrush="{DynamicResource SeparatorBrush}" Background="{DynamicResource SurfaceBrush}"><TextBlock Text="⌄" Foreground="{DynamicResource MutedBrush}" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,10,3"/></Border></ControlTemplate></ToggleButton.Template>
                    </ToggleButton>
                    <ContentPresenter x:Name="SelectedContent" IsHitTestVisible="False" Margin="10,6,30,6" VerticalAlignment="Center" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"/>
                    <TextBox x:Name="PART_EditableTextBox" Visibility="Hidden" Margin="0,0,30,0" Padding="10,6,4,6" VerticalContentAlignment="Center" IsReadOnly="{TemplateBinding IsReadOnly}"/>
                    <Popup x:Name="PART_Popup" AllowsTransparency="True" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" Focusable="False" PopupAnimation="Fade">
                      <Border MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="320" Background="{DynamicResource SurfaceBrush}" BorderBrush="{DynamicResource SeparatorBrush}" BorderThickness="1" CornerRadius="5" Padding="3">
                        <ScrollViewer VerticalScrollBarVisibility="Auto"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer>
                      </Border>
                    </Popup>
                  </Grid>
                  <ControlTemplate.Triggers><Trigger Property="IsEditable" Value="True"><Setter TargetName="SelectedContent" Property="Visibility" Value="Hidden"/><Setter TargetName="PART_EditableTextBox" Property="Visibility" Value="Visible"/><Setter TargetName="DropDownToggle" Property="HorizontalAlignment" Value="Right"/><Setter TargetName="DropDownToggle" Property="Width" Value="30"/></Trigger></ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="ComboBoxItem"><Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="Padding" Value="9,6"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem"><Border x:Name="Item" Background="Transparent" Padding="{TemplateBinding Padding}" CornerRadius="4"><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Item" Property="Background" Value="{DynamicResource SelectionBrush}"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
              <Style TargetType="ListBox"><Setter Property="Background" Value="{DynamicResource SurfaceBrush}"/><Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="BorderThickness" Value="0"/><Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/></Style>
              <Style TargetType="ListBoxItem">
                <Setter Property="Padding" Value="10,8"/><Setter Property="Margin" Value="2,2"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem"><Border x:Name="Row" Background="Transparent" CornerRadius="6" Padding="{TemplateBinding Padding}"><ContentPresenter/></Border>
                  <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Row" Property="Background" Value="{DynamicResource HoverBrush}"/></Trigger><Trigger Property="IsSelected" Value="True"><Setter TargetName="Row" Property="Background" Value="{DynamicResource SelectionBrush}"/></Trigger></ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="TabControl"><Setter Property="Background" Value="{DynamicResource BackgroundBrush}"/><Setter Property="BorderThickness" Value="0"/><Setter Property="Padding" Value="0,16,0,0"/></Style>
              <Style TargetType="TabItem">
                <Setter Property="Foreground" Value="{DynamicResource MutedBrush}"/><Setter Property="Padding" Value="16,10"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabItem"><Border x:Name="Tab" Padding="{TemplateBinding Padding}" BorderThickness="0,0,0,2" BorderBrush="Transparent"><ContentPresenter ContentSource="Header"/></Border><ControlTemplate.Triggers><Trigger Property="IsSelected" Value="True"><Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter TargetName="Tab" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="Expander"><Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/></Style>
              <Style TargetType="ScrollViewer"><Setter Property="PanningMode" Value="VerticalOnly"/></Style>
            </ResourceDictionary>
            """);
        Resources.MergedDictionaries.Add(dictionary);
    }

    private void ApplyTheme()
    {
        ApplyTypography();
        foreach (var role in new[] { "Background", "Foreground", "Muted", "Past", "Separator", "Accent" })
            Resources[role + "Brush"] = _state.Theme.Brush(role);
        var baseColor = _state.Theme.Color("Background");
        Resources["SurfaceBrush"] = new SolidColorBrush(Mix(baseColor, Colors.White, _state.Theme.IsDark ? 0.035 : 0.7));
        Resources["HoverBrush"] = new SolidColorBrush(Mix(baseColor, _state.Theme.Color("Accent"), 0.09));
        Resources["SelectionBrush"] = new SolidColorBrush(Mix(baseColor, _state.Theme.Color("Accent"), 0.18));
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        SetResourceReference(ForegroundProperty, "ForegroundBrush");
        // Keep standard WPF popups and editors readable in both Windows color modes.
        Resources[SystemColors.WindowBrushKey] = Resources["SurfaceBrush"];
        Resources[SystemColors.WindowTextBrushKey] = Resources["ForegroundBrush"];
        Resources[SystemColors.ControlBrushKey] = Resources["BackgroundBrush"];
        Resources[SystemColors.ControlTextBrushKey] = Resources["ForegroundBrush"];
        foreach (var refresh in _refreshColorEditors) refresh();
    }

    private void ApplyTypography()
    {
        FontFamily = _state.TextFont("Settings");
        FontSize = _state.TextStyle("Settings").FontSize;
        Resources["SettingsFontFamily"] = FontFamily;
        foreach (var originalSize in _settingsTextSizes)
            Resources[SettingsSizeKey(originalSize)] = originalSize * FontSize / 13;
    }

    private static string SettingsSizeKey(double size) => "SettingsFontSize" + size.ToString(CultureInfo.InvariantCulture);

    private static Color Mix(Color first, Color second, double amount) => Color.FromRgb(
        (byte)Math.Round(first.R + (second.R - first.R) * amount),
        (byte)Math.Round(first.G + (second.G - first.G) * amount),
        (byte)Math.Round(first.B + (second.B - first.B) * amount));

    private void Build()
    {
        var activeTab = _tabs?.SelectedIndex ?? 0;
        _refreshTypographyEditors.Clear();
        _refreshColorEditors.Clear();
        ApplyTheme();
        Title = T("会议日历 · 设置", "Deadline Calendar · Settings");
        _renderedLanguage = _state.Preferences.Language;
        _renderedCatalog = _state.Catalog;
        var root = new Grid { Margin = new Thickness(24, 20, 24, 12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
        heading.Children.Add(Text(T("设置", "Settings"), 23, "Foreground", FontWeights.SemiBold));
        root.Children.Add(heading);
        _tabs = new TabControl();
        _tabs.Items.Add(new TabItem { Header = T("关注会议", "Conferences"), Content = BuildConferences() });
        _tabs.Items.Add(new TabItem { Header = T("外观与常规", "Appearance & General"), Content = BuildGeneral() });
        _dataPanel = new StackPanel { Margin = new Thickness(0, 0, 12, 16) };
        _tabs.Items.Add(new TabItem { Header = T("数据与同步", "Data & Sync"), Content = Scroll(_dataPanel) });
        _tabs.SelectedIndex = Math.Clamp(activeTab, 0, 2);
        Grid.SetRow(_tabs, 1);
        root.Children.Add(_tabs);
        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _saved = Text(T("设置已保存", "Settings Saved"), 11, "Muted");
        _saved.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_saved);
        var done = Button(T("完成", "Done"), () => Close());
        done.MinWidth = 90;
        Grid.SetColumn(done, 1);
        footer.Children.Add(done);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        // Keep the tab viewport usable even when large fonts make the heading and
        // footer taller than the window; the outer page can then scroll as well.
        var page = new ScrollViewer
        {
            Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false
        };
        void ResizeTabViewport()
        {
            var available = page.ActualHeight - root.Margin.Top - root.Margin.Bottom
                - heading.ActualHeight - heading.Margin.Top - heading.Margin.Bottom
                - footer.ActualHeight - footer.Margin.Top - footer.Margin.Bottom;
            _tabs.Height = Math.Max(Math.Max(320, FontSize * 9), available);
        }
        page.SizeChanged += (_, _) => ResizeTabViewport();
        heading.SizeChanged += (_, _) => ResizeTabViewport();
        footer.SizeChanged += (_, _) => ResizeTabViewport();
        Content = page;
        RenderCatalog();
        RenderFollowed();
        RenderDetails();
        RenderData();
    }

    private UIElement BuildGeneral()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 15, 12) };
        panel.Children.Add(Section(T("语言与日历", "Language & Calendar")));
        var language = Combo(["简体中文", "English"], _state.Preferences.Language == "zh-CN" ? 0 : 1);
        language.MinWidth = 180;
        language.SelectionChanged += (_, _) =>
        {
            _state.Preferences.Language = language.SelectedIndex == 0 ? "zh-CN" : "en";
            Commit();
            Build();
        };
        panel.Children.Add(SettingRow(T("语言", "Language"), "", language));
        var week = Combo([T("周一", "Monday"), T("周日", "Sunday")], _state.Preferences.WeekStartsMonday ? 0 : 1);
        week.MinWidth = 180;
        week.SelectionChanged += (_, _) => { _state.Preferences.WeekStartsMonday = week.SelectedIndex == 0; Commit(); };
        panel.Children.Add(SettingRow(T("每周起始日", "First Day Of The Week"), "", week));
        var calendarLimit = new CheckBox
        {
            Content = new TextBlock
            {
                Text = T("仅浏览 Today 前后一年", "Limit Calendar To One Year Before And After Today"),
                TextWrapping = TextWrapping.Wrap
            },
            IsChecked = _state.Preferences.LimitCalendarToOneYear,
            Margin = new Thickness(0, 0, 0, 13),
            VerticalAlignment = VerticalAlignment.Center
        };
        void SaveCalendarLimit()
        {
            var enabled = calendarLimit.IsChecked == true;
            if (_state.Preferences.LimitCalendarToOneYear == enabled) return;
            _state.Preferences.LimitCalendarToOneYear = enabled;
            Commit();
        }
        calendarLimit.Checked += (_, _) => SaveCalendarLimit();
        calendarLimit.Unchecked += (_, _) => SaveCalendarLimit();
        panel.Children.Add(calendarLimit);
        panel.Children.Add(Section(T("窗口", "Window")));
        _windowWidthInput = WindowSizeEditor(true);
        _windowHeightInput = WindowSizeEditor(false);
        RefreshWindowSizeEditors(true);
        var dimensions = new Grid { Margin = new Thickness(0, 0, 0, 13) };
        dimensions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dimensions.ColumnDefinitions.Add(new ColumnDefinition());
        dimensions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        dimensions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dimensions.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var (label, editor, column) in new[]
            { (T("宽度", "Width"), _windowWidthInput, 0), (T("高度", "Height"), _windowHeightInput, 3) })
        {
            var dimensionLabel = Text(label, margin: new Thickness(0, 0, 16, 0));
            dimensionLabel.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(dimensionLabel, column);
            dimensions.Children.Add(dimensionLabel);
            editor.MinWidth = 80;
            Grid.SetColumn(editor, column + 1);
            dimensions.Children.Add(editor);
        }
        panel.Children.Add(dimensions);
        var topmost = new CheckBox { Content = T("保持在其他窗口上方", "Keep Above Other Windows"), IsChecked = _state.Preferences.AlwaysOnTop, VerticalAlignment = VerticalAlignment.Center };
        topmost.Checked += (_, _) => { _state.Preferences.AlwaysOnTop = true; Commit(); };
        topmost.Unchecked += (_, _) => { _state.Preferences.AlwaysOnTop = false; Commit(); };
        panel.Children.Add(SettingRow(T("窗口置顶", "Always On Top"), "", topmost));
        AddStartupSection(panel);
        AddAutoReturnSection(panel);
        panel.Children.Add(Section(T("显示时区", "Display Timezone")));
        var zones = SettingsTimeZones.Create(_state.Preferences.Language == "zh-CN");
        var zonePicker = new ComboBox
        {
            ItemsSource = zones, MinWidth = 280, MinHeight = 33,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Automation(zonePicker, T("显示时区", "Display Timezone"));
        // Showing the current offset must not rewrite an existing regional or
        // fractional timezone. Only an explicit selection changes preferences.
        var currentOffset = _state.DisplayZone.GetUtcOffset(SystemClock.Instance.GetCurrentInstant());
        var currentId = "UTC" + currentOffset.ToString("+HH:mm", CultureInfo.InvariantCulture);
        zonePicker.SelectedItem = zones.FirstOrDefault(zone => zone.Id == currentId);
        if (zonePicker.SelectedItem is null)
        {
            zonePicker.IsEditable = true;
            zonePicker.IsReadOnly = true;
            zonePicker.Text = currentId;
        }
        zonePicker.SelectionChanged += (_, _) =>
        {
            if (zonePicker.SelectedItem is not SettingsTimeZoneOption selected) return;
            _state.Preferences.DisplayTimeZoneId = selected.Id;
            zonePicker.IsEditable = false;
            Commit();
            RenderDetails();
        };
        panel.Children.Add(zonePicker);
        AddTypographySection(panel);
        panel.Children.Add(Section(T("透明度", "Opacity")));
        panel.Children.Add(OpacityRow(T("背景", "Background"), _state.Preferences.BackgroundOpacity, value => _state.Preferences.BackgroundOpacity = value));
        panel.Children.Add(OpacityRow(T("文字与圆点", "Text & Dots"), _state.Preferences.ContentOpacity, value => _state.Preferences.ContentOpacity = value));
        panel.Children.Add(OpacityRow(T("过去日期", "Past Dates"), _state.Preferences.PastOpacity, value => _state.Preferences.PastOpacity = value));
        panel.Children.Add(OpacityRow(T("悬浮详情背景", "Hover Background"), _state.Preferences.TooltipBackgroundOpacity, value => _state.Preferences.TooltipBackgroundOpacity = value));
        panel.Children.Add(Section(T("界面颜色", "Interface Colors")));
        var roles = new[] { ("Accent", T("主题色", "Accent")), ("Background", T("背景色", "Background")), ("TooltipBackground", T("悬浮详情背景", "Hover Background")), ("Foreground", T("文字色", "Text")), ("Muted", T("次要文字", "Secondary Text")), ("Past", T("过去日期", "Past Dates")), ("Separator", T("分隔线", "Separators")) };
        foreach (var (role, label) in roles)
            panel.Children.Add(SettingRow(label, "", ColorEditor(_state.Theme.Color(role), color => { _state.Preferences.ColorOverrides[role] = Hex(color); Commit(); }, () => _state.Theme.Color(role))));
        var reset = Button(T("恢复默认颜色与透明度", "Reset Colors And Opacity"), () =>
        {
            var defaults = new UserPreferences();
            _state.Preferences.ColorOverrides.Clear();
            _state.Preferences.BackgroundOpacity = defaults.BackgroundOpacity;
            _state.Preferences.ContentOpacity = defaults.ContentOpacity;
            _state.Preferences.PastOpacity = defaults.PastOpacity;
            _state.Preferences.TooltipBackgroundOpacity = defaults.TooltipBackgroundOpacity;
            for (var i = 0; i < _state.Preferences.Followed.Count; i++) _state.Preferences.Followed[i].ColorHex = ConferenceColors[i % ConferenceColors.Length];
            Commit();
            Build();
        });
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        reset.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(reset);
        return Scroll(panel);
    }

    private void AddAutoReturnSection(StackPanel panel)
    {
        panel.Children.Add(Section(T("自动回到今天", "Return To Today")));
        var enabled = new CheckBox
        {
            Content = T("启用自动回到今天", "Enable Return To Today"),
            IsChecked = _state.Preferences.AutoReturnToTodayEnabled,
            Margin = new Thickness(0, 0, 0, 13)
        };
        Automation(enabled, T("启用自动回到今天", "Enable Return To Today"));
        var delay = new TextBox
        {
            Text = _state.Preferences.AutoReturnToTodayDelaySeconds.ToString(CultureInfo.InvariantCulture),
            MinWidth = 100, IsEnabled = _state.Preferences.AutoReturnToTodayEnabled,
            Opacity = _state.Preferences.AutoReturnToTodayEnabled ? 1 : .45,
            ToolTip = T("1–3600 秒；Enter 或离开输入框后保存", "1–3600 seconds; Enter or leave the field to save")
        };
        Automation(delay, T("空闲等待（秒）", "Idle Delay (Seconds)"));
        void SaveEnabled()
        {
            var value = enabled.IsChecked == true;
            delay.IsEnabled = value;
            delay.Opacity = value ? 1 : .45;
            if (_state.Preferences.AutoReturnToTodayEnabled == value) return;
            _state.Preferences.AutoReturnToTodayEnabled = value;
            Commit();
        }
        void SaveDelay()
        {
            if (!delay.IsEnabled) return;
            if (!int.TryParse(delay.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds is < 1 or > 3600)
            {
                delay.BorderBrush = Brushes.IndianRed;
                delay.ToolTip = T("请输入 1–3600 之间的整数秒数", "Enter a whole number of seconds from 1 to 3600");
                return;
            }
            delay.SetResourceReference(Control.BorderBrushProperty, "SeparatorBrush");
            delay.ToolTip = T("1–3600 秒；Enter 或离开输入框后保存", "1–3600 seconds; Enter or leave the field to save");
            delay.Text = seconds.ToString(CultureInfo.InvariantCulture);
            if (_state.Preferences.AutoReturnToTodayDelaySeconds == seconds) return;
            _state.Preferences.AutoReturnToTodayDelaySeconds = seconds;
            Commit();
        }
        enabled.Checked += (_, _) => SaveEnabled();
        enabled.Unchecked += (_, _) => SaveEnabled();
        delay.KeyDown += (_, args) => { if (args.Key == Key.Enter) { SaveDelay(); args.Handled = true; } };
        delay.LostKeyboardFocus += (_, _) => SaveDelay();
        panel.Children.Add(enabled);
        panel.Children.Add(SettingRow(T("空闲等待（秒）", "Idle Delay (Seconds)"), "", delay));
    }

    private TextBox WindowSizeEditor(bool isWidth)
    {
        var input = new TextBox { MinWidth = 130, ToolTip = T("输入尺寸，按 Enter 或离开输入框后应用", "Enter a size, then press Enter or leave the field to apply") };
        Automation(input, isWidth ? T("窗口宽度", "Window Width") : T("窗口高度", "Window Height"));
        void ApplySize()
        {
            // The displayed two-decimal value must not round a stored fractional size
            // merely because the user focused and then left the editor.
            if (input.Text == (isWidth ? _renderedWidth : _renderedHeight))
            {
                RefreshWindowSizeEditors(true);
                return;
            }
            if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var size)
                || !double.IsFinite(size) || size <= 0 || size > 32768)
            {
                input.BorderBrush = Brushes.IndianRed;
                input.ToolTip = T("请输入大于 0 且不超过 32768 的尺寸", "Enter a size greater than 0 and no larger than 32768");
                return;
            }
            try
            {
                var bounds = _state.Preferences.Bounds;
                _state.RequestWindowSize(isWidth ? size : bounds.Width, isWidth ? bounds.Height : size);
                RefreshWindowSizeEditors(true);
                _saved.Text = T("窗口尺寸已保存", "Window Size Saved");
            }
            catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
            {
                _saved.Text = T("窗口尺寸保存失败：", "Could not save window size: ") + exception.Message;
            }
        }
        input.KeyDown += (_, args) => { if (args.Key == Key.Enter) { ApplySize(); args.Handled = true; } };
        input.LostKeyboardFocus += (_, _) => ApplySize();
        return input;
    }

    private void OnWindowBoundsChanged()
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(OnWindowBoundsChanged); return; }
        RefreshWindowSizeEditors();
    }

    private void RefreshWindowSizeEditors(bool force = false)
    {
        void Refresh(TextBox? input, double value, ref string rendered)
        {
            if (input is null || (!force && input.IsKeyboardFocusWithin)) return;
            rendered = value.ToString("0.##", CultureInfo.InvariantCulture);
            input.Text = rendered;
            input.SetResourceReference(Control.BorderBrushProperty, "SeparatorBrush");
            input.ToolTip = T("输入尺寸，按 Enter 或离开输入框后应用", "Enter a size, then press Enter or leave the field to apply");
        }
        Refresh(_windowWidthInput, _state.Preferences.Bounds.Width, ref _renderedWidth);
        Refresh(_windowHeightInput, _state.Preferences.Bounds.Height, ref _renderedHeight);
    }

    private void AddTypographySection(StackPanel panel)
    {
        panel.Children.Add(Section(T("字体与字号", "Fonts & Text Sizes")));
        panel.Children.Add(Text(T("每类文字可单独设置。输入字体名称进行搜索，选择后立即预览；字号范围为 8–64。", "Configure each kind of text separately. Search installed fonts by name and select one to preview it. Sizes range from 8 to 64."), 11, "Muted", margin: new Thickness(0, 0, 0, 14)));
        _installedFontFamilies ??= Fonts.SystemFontFamilies.Select(family => family.Source).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var role in TypographyDefaults.Roles)
            panel.Children.Add(TypographyEditor(role));
        var reset = Button(T("恢复默认字体与字号", "Reset Fonts And Text Sizes"), () =>
        {
            _state.Preferences.TextStyles.Clear();
            Commit();
            foreach (var refresh in _refreshTypographyEditors) refresh();
        });
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        reset.Margin = new Thickness(0, 2, 0, 12);
        panel.Children.Add(reset);
    }

    private UIElement TypographyEditor(string role)
    {
        var label = role switch
        {
            "Headline" => T("顶部会议名", "Header Conference Name"),
            "Deadline" => T("顶部截止时间", "Header Deadline"),
            "Countdown" => T("顶部倒计时", "Header Countdown"),
            "TooltipTitle" => T("悬浮详情标题", "Hover Titles"),
            "TooltipBody" => T("悬浮详情正文", "Hover Body"),
            "DetailTitle" => T("详情窗口标题", "Detail Window Titles"),
            "DetailBody" => T("详情窗口正文", "Detail Window Body"),
            "Badge" => T("同日截止数量", "Same-Day Deadline Count"),
            "Settings" => T("设置界面", "Settings Interface"),
            _ => role
        };
        var current = _state.TextStyle(role);
        var content = new StackPanel();
        var editors = new Grid();
        editors.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        editors.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var fontBox = new ComboBox
        {
            IsEditable = true, IsTextSearchEnabled = false, StaysOpenOnEdit = true,
            ItemsSource = _installedFontFamilies, Text = current.FontFamily,
            MinHeight = 33, Margin = new Thickness(0, 0, 8, 0),
            ToolTip = T("输入已安装的字体名称，按 Enter 或移出焦点后保存", "Type an installed font name, then press Enter or leave the field to save")
        };
        Automation(fontBox, label + T("字体", " Font"));
        editors.Children.Add(fontBox);
        var sizeBox = new TextBox
        {
            Text = current.FontSize.ToString("0.##", CultureInfo.InvariantCulture), Width = 67,
            ToolTip = T("字号 8–64；Enter 或离开输入框后保存", "Text size 8–64; Enter or leave the field to save")
        };
        Automation(sizeBox, label + T("字号", " Size"));
        Grid.SetColumn(sizeBox, 1);
        editors.Children.Add(sizeBox);
        content.Children.Add(editors);
        CheckBox? boldBox = null, italicBox = null;
        if (role is "Headline" or "Deadline" or "Countdown")
        {
            var emphasis = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            boldBox = new CheckBox { Content = T("加粗", "Bold"), IsChecked = current.Bold ?? role == "Headline", Margin = new Thickness(0, 0, 20, 0) };
            italicBox = new CheckBox { Content = T("斜体", "Italic"), IsChecked = current.Italic };
            Automation(boldBox, label + T("加粗", " Bold"));
            Automation(italicBox, label + T("斜体", " Italic"));
            emphasis.Children.Add(boldBox);
            emphasis.Children.Add(italicBox);
            content.Children.Add(emphasis);
        }
        var sample = role switch
        {
            "Headline" => "CVPR 2027",
            "Deadline" => "2027-11-12 23:59:59",
            "Countdown" => T("28天 08:12:35", "28d 08:12:35"),
            "TooltipTitle" or "DetailTitle" => "CVPR 2027",
            "Badge" => "3",
            "Settings" => T("设置 · 字体预览", "Settings · Font Preview"),
            _ => T("全文截止 · 会议详情", "Full-Paper Deadline · Conference Details")
        };
        var preview = new TextBlock { Text = sample, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        preview.SetResourceReference(TextBlock.ForegroundProperty, "ForegroundBrush");
        var previewFrame = new Border { Child = preview, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 7, 0, 0), CornerRadius = new CornerRadius(5), MinHeight = 38 };
        previewFrame.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        content.Children.Add(previewFrame);
        var changing = false;
        void RefreshPreview()
        {
            preview.FontFamily = _state.TextFont(role);
            preview.FontSize = _state.TextStyle(role).FontSize;
            preview.FontWeight = _state.TextWeight(role);
            preview.FontStyle = _state.TextSlant(role);
        }
        void SaveFont()
        {
            if (changing) return;
            var typed = fontBox.Text.Trim();
            var selected = _installedFontFamilies!.FirstOrDefault(name => name.Equals(typed, StringComparison.OrdinalIgnoreCase));
            if (selected is null && string.Equals(typed, _state.TextStyle(role).FontFamily, StringComparison.OrdinalIgnoreCase)) return;
            if (selected is null)
            {
                fontBox.ToolTip = T("请选择已安装的字体，或输入完整字体名称", "Select an installed font or enter its complete name");
                return;
            }
            var existing = _state.TextStyle(role);
            if (existing.FontFamily == selected) return;
            existing.FontFamily = selected;
            _state.Preferences.TextStyles[role] = existing;
            Commit();
            RefreshPreview();
        }
        void SaveSize()
        {
            if (changing) return;
            if (!double.TryParse(sizeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) || !double.IsFinite(size) || size is < 8 or > 64)
            {
                sizeBox.BorderBrush = Brushes.IndianRed;
                sizeBox.ToolTip = T("请输入 8–64 之间的字号", "Enter a text size between 8 and 64");
                return;
            }
            sizeBox.SetResourceReference(Control.BorderBrushProperty, "SeparatorBrush");
            var existing = _state.TextStyle(role);
            if (existing.FontSize == size) return;
            existing.FontSize = size;
            _state.Preferences.TextStyles[role] = existing;
            Commit();
            RefreshPreview();
        }
        void SaveEmphasis(bool bold)
        {
            if (changing) return;
            var updated = _state.TextStyle(role);
            if (bold) updated.Bold = boldBox!.IsChecked == true;
            else updated.Italic = italicBox!.IsChecked == true;
            _state.Preferences.TextStyles[role] = updated;
            Commit();
            RefreshPreview();
        }
        if (boldBox is not null && italicBox is not null)
        {
            boldBox.Checked += (_, _) => SaveEmphasis(true);
            boldBox.Unchecked += (_, _) => SaveEmphasis(true);
            italicBox.Checked += (_, _) => SaveEmphasis(false);
            italicBox.Unchecked += (_, _) => SaveEmphasis(false);
        }
        fontBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, args) =>
        {
            if (changing || args.OriginalSource is not TextBox editor || !editor.IsKeyboardFocused) return;
            var typed = editor.Text;
            if (fontBox.SelectedItem is string chosen && chosen == typed) return;
            var caret = editor.SelectionStart;
            var selectionLength = editor.SelectionLength;
            changing = true;
            try
            {
                fontBox.ItemsSource = _installedFontFamilies!.Where(name => name.Contains(typed.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                fontBox.Text = typed;
                editor.SelectionStart = Math.Min(caret, typed.Length);
                editor.SelectionLength = Math.Min(selectionLength, typed.Length - editor.SelectionStart);
            }
            finally { changing = false; }
            fontBox.IsDropDownOpen = fontBox.Items.Count > 0;
        }));
        fontBox.SelectionChanged += (_, _) =>
        {
            if (changing || fontBox.SelectedItem is not string chosen) return;
            changing = true;
            fontBox.Text = chosen;
            changing = false;
            SaveFont();
        };
        fontBox.KeyDown += (_, args) => { if (args.Key == Key.Enter) { SaveFont(); fontBox.IsDropDownOpen = false; args.Handled = true; } };
        fontBox.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(() => { if (!fontBox.IsKeyboardFocusWithin) SaveFont(); }, DispatcherPriority.Background);
        sizeBox.KeyDown += (_, args) => { if (args.Key == Key.Enter) { SaveSize(); args.Handled = true; } };
        sizeBox.LostKeyboardFocus += (_, _) => SaveSize();
        _refreshTypographyEditors.Add(() =>
        {
            changing = true;
            var updated = _state.TextStyle(role);
            fontBox.ItemsSource = _installedFontFamilies;
            fontBox.Text = updated.FontFamily;
            sizeBox.Text = updated.FontSize.ToString("0.##", CultureInfo.InvariantCulture);
            sizeBox.SetResourceReference(Control.BorderBrushProperty, "SeparatorBrush");
            if (boldBox is not null) boldBox.IsChecked = updated.Bold ?? role == "Headline";
            if (italicBox is not null) italicBox.IsChecked = updated.Italic;
            changing = false;
            RefreshPreview();
        });
        RefreshPreview();
        var row = SettingRow(label, role == "Settings" ? T("按比例保留标题与正文层级。", "Scales headings and body text proportionally.") : "", content);
        content.HorizontalAlignment = HorizontalAlignment.Stretch;
        return row;
    }

    private void RenderData()
    {
        _dataPanel.Children.Clear();
        _dataPanel.Children.Add(Section(T("配置备份", "Configuration Backup")));
        var export = Button(T("导出配置为 JSON…", "Export Configuration As JSON…"), ExportConfiguration);
        export.HorizontalAlignment = HorizontalAlignment.Left;
        Automation(export, T("导出配置为 JSON", "Export Configuration As JSON"));
        _dataPanel.Children.Add(export);
        _dataPanel.Children.Add(Section(T("自动同步", "Automatic Synchronization")));
        var updated = Text(T("上次更新：", "Last Update: ") + (_state.Sync.LastSuccessUtc is { } last
            ? Instant.FromDateTimeOffset(last).InZone(_state.DisplayZone).LocalDateTime.ToString("uuuu-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : T("暂无", "None")), 12, "Muted");
        _dataPanel.Children.Add(updated);
        var refresh = Button(_state.Sync.IsSyncing ? T("正在更新…", "Refreshing…") : T("立即更新", "Refresh Now"), async () =>
        {
            await _state.RefreshAsync();
            if (!_closed && !string.IsNullOrWhiteSpace(_state.Sync.LastError))
                MessageBox.Show(this, _state.Sync.LastError, T("更新失败", "Update Failed"), MessageBoxButton.OK, MessageBoxImage.Warning);
        });
        refresh.HorizontalAlignment = HorizontalAlignment.Left;
        refresh.IsEnabled = !_state.Sync.IsSyncing;
        refresh.Margin = new Thickness(0, 14, 0, 0);
        _dataPanel.Children.Add(refresh);
        if (!string.IsNullOrWhiteSpace(_state.Sync.LastError))
        {
            updated.ToolTip = T("更新失败：", "Update failed: ") + _state.Sync.LastError;
            refresh.ToolTip = updated.ToolTip;
        }
        var diagnostics = _state.Catalog.Diagnostics.Concat(_state.Projection.Diagnostics).Distinct().ToList();
        if (_state.Projection.Pending.Count > 0 || diagnostics.Count > 0)
            _dataPanel.Children.Add(Section(T("待定与待核验", "Pending & Verification")));
        foreach (var pending in _state.Projection.Pending)
            _dataPanel.Children.Add(Text($"• {pending.Title} {pending.EditionYear} · {pending.Comment}\n  {TranslatePendingReason(pending.Reason)}", 12, "Foreground", margin: new Thickness(0, 0, 0, 9)));
        foreach (var diagnostic in diagnostics)
            _dataPanel.Children.Add(Text("• " + diagnostic, 11, "Muted", margin: new Thickness(0, 0, 0, 7)));
        _dataPanel.Children.Add(Section(T("数据来源与本地文件", "Sources & Local Files")));
        var links = new WrapPanel();
        foreach (var (label, url) in new[] { (T("源代码与数据 ↗", "Source & Data ↗"), "https://github.com/ccfddl/ccf-deadlines"), (T("原始 JSON ↗", "Original JSON ↗"), "https://ccfddl.com/conference/allconf.json"), ("CCF DDL ↗", "https://ccfddl.top/") })
        {
            var button = Button(label, () => OpenHttps(new Uri(url)));
            button.Margin = new Thickness(0, 0, 8, 8);
            links.Children.Add(button);
        }
        _dataPanel.Children.Add(links);
        _dataPanel.Children.Add(Text(T("数据目录", "Data Folder"), 12, "Muted", margin: new Thickness(0, 10, 0, 6)));
        _dataPanel.Children.Add(new TextBox { Text = _state.Storage.DirectoryPath, IsReadOnly = true, TextWrapping = TextWrapping.Wrap });
    }

    private void ExportConfiguration()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = T("导出配置", "Export Configuration"),
            FileName = "DeadlineCalendar-config-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json",
            Filter = T("JSON 配置文件 (*.json)|*.json", "JSON Configuration (*.json)|*.json"),
            DefaultExt = ".json", AddExtension = true, OverwritePrompt = true, CheckPathExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _state.Storage.ExportPreferences(_state.Preferences, dialog.FileName);
            _saved.Text = T("配置已导出：", "Configuration Exported: ") + dialog.FileName;
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Security.SecurityException or System.Text.Json.JsonException or NotSupportedException)
        {
            MessageBox.Show(this, T("配置导出失败：", "Could not export configuration: ") + exception.Message,
                T("导出配置", "Export Configuration"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Commit()
    {
        _ownUpdate = true;
        try
        {
            _state.UpdatePreferences();
            ApplyTheme();
            if (_saved is not null) _saved.Text = T("设置已保存", "Settings Saved");
            if (_dataPanel is not null) RenderData();
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
            if (_saved is not null) _saved.Text = T("设置保存失败：", "Could not save settings: ") + exception.Message;
        }
        finally { _ownUpdate = false; }
    }

    private string TranslatePendingReason(string reason)
    {
        if (_state.Preferences.Language != "zh-CN") return reason;
        const string unresolvedPrefix = "Estimated local time cannot be resolved: ";
        if (reason.StartsWith(unresolvedPrefix, StringComparison.Ordinal))
            return "推测的来源当地时间无法解析：" + TranslatePendingReason(reason[unresolvedPrefix.Length..]);
        return reason switch
        {
            "No submission timeline was published." => "上游尚未公布投稿时间线。",
            "Estimation is disabled for this conference." => "此会议已关闭日期推测。",
            "Published date or timezone is invalid; no estimate was substituted." => "已公布的日期或时区无效，未使用推测日期替代。",
            "No fully dated previous edition is available for estimation." => "没有日期完整有效的历史届次可用于推测。",
            "TBD round has no unique matching historical label." => "待定轮次的备注无法与历史记录唯一匹配。",
            "Estimated date is outside the supported calendar." => "推测日期超出支持的日期范围。",
            "Local time does not exist during a daylight-saving transition." => "该当地时间在夏令时切换时不存在。",
            "Local time is ambiguous during a daylight-saving transition." => "该当地时间因夏令时切换而对应多个时间点。",
            "Invalid local date or time." => "当地日期或时间无效。",
            _ => reason
        };
    }

    private void OnStateChanged()
    {
        if (_ownUpdate || _closed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(OnStateChanged); return; }
        if (_renderedLanguage != _state.Preferences.Language || !ReferenceEquals(_renderedCatalog, _state.Catalog)) { Build(); return; }
        ApplyTheme();
        RenderData();
    }

    private UIElement ColorEditor(Color initial, Action<Color> save, Func<Color>? readCurrentColor = null)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 6, 0, 2), VerticalAlignment = VerticalAlignment.Center };
        var preview = new Border { Width = 27, Height = 27, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(initial), VerticalAlignment = VerticalAlignment.Center };
        var picker = new Button { Content = preview, Padding = new Thickness(3), Margin = new Thickness(0, 0, 10, 0), ToolTip = T("打开调色板", "Open Color Palette") };
        Automation(picker, T("打开调色板", "Open Color Palette"));
        panel.Children.Add(picker);
        var boxes = new List<TextBox>();
        foreach (var (channel, value) in new[] { ("R", initial.R), ("G", initial.G), ("B", initial.B) })
        {
            panel.Children.Add(Text(channel, 10, "Muted", margin: new Thickness(0, 0, 4, 0)));
            var input = new TextBox { Text = value.ToString(CultureInfo.InvariantCulture), Width = 48, Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(0, 0, 8, 0), MaxLength = 3, ToolTip = T("0 至 255；Enter 或离开输入框后保存", "0–255; Enter or leave the field to save") };
            Automation(input, channel + " (0–255)");
            boxes.Add(input);
            panel.Children.Add(input);
        }
        var hex = Text(Hex(initial), 11, "Muted", margin: new Thickness(0, 0, 0, 0));
        hex.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(hex);
        var previous = initial;
        void RefreshColor()
        {
            if (readCurrentColor is null || boxes.Any(box => box.IsKeyboardFocusWithin)) return;
            var current = readCurrentColor();
            if (current == previous) return;
            previous = current;
            preview.Background = new SolidColorBrush(current);
            hex.Text = Hex(current);
            boxes[0].Text = current.R.ToString(CultureInfo.InvariantCulture);
            boxes[1].Text = current.G.ToString(CultureInfo.InvariantCulture);
            boxes[2].Text = current.B.ToString(CultureInfo.InvariantCulture);
        }
        if (readCurrentColor is not null) _refreshColorEditors.Add(RefreshColor);
        void TrySave(bool force = false)
        {
            var values = new byte[3];
            var valid = true;
            for (var i = 0; i < 3; i++)
            {
                if (!byte.TryParse(boxes[i].Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
                {
                    boxes[i].BorderBrush = Brushes.IndianRed;
                    boxes[i].ToolTip = T("请输入 0 至 255 的整数", "Enter an integer from 0 to 255");
                    valid = false;
                }
                else boxes[i].SetResourceReference(Control.BorderBrushProperty, "SeparatorBrush");
            }
            if (!valid) return;
            var color = Color.FromRgb(values[0], values[1], values[2]);
            if (color == previous && !force) { RefreshColor(); return; }
            previous = color;
            preview.Background = new SolidColorBrush(color);
            hex.Text = Hex(color);
            save(color);
        }
        foreach (var input in boxes)
        {
            input.LostKeyboardFocus += (_, _) => TrySave();
            input.KeyDown += (_, args) => { if (args.Key == Key.Enter) { TrySave(); args.Handled = true; } };
        }
        picker.Click += (_, _) =>
        {
            using var dialog = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true, AllowFullOpen = true, AnyColor = true,
                Color = System.Drawing.Color.FromArgb(previous.R, previous.G, previous.B)
            };
            if (dialog.ShowDialog(new NativeDialogOwner(new WindowInteropHelper(this).Handle)) != System.Windows.Forms.DialogResult.OK) return;
            boxes[0].Text = dialog.Color.R.ToString(CultureInfo.InvariantCulture);
            boxes[1].Text = dialog.Color.G.ToString(CultureInfo.InvariantCulture);
            boxes[2].Text = dialog.Color.B.ToString(CultureInfo.InvariantCulture);
            TrySave(true);
        };
        return panel;
    }

    private UIElement OpacityRow(string label, double initial, Action<double> save)
    {
        var panel = new Grid { Width = 290 };
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });
        var value = Text($"{initial:P0}", 12, "Muted");
        value.VerticalAlignment = VerticalAlignment.Center;
        value.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(value, 1);
        panel.Children.Add(value);
        var slider = new Slider { Minimum = 0, Maximum = 1, Value = Math.Clamp(initial, 0, 1), SmallChange = 0.01, LargeChange = 0.1, TickFrequency = 0.01, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        Automation(slider, label);
        slider.ValueChanged += (_, _) => { value.Text = $"{slider.Value:P0}"; save(slider.Value); Commit(); };
        panel.Children.Add(slider);
        return SettingRow(label, "", panel);
    }

    private UIElement SettingRow(string title, string help, UIElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 13) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 165 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.45, GridUnitType.Star) });
        var label = new StackPanel { Margin = new Thickness(0, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(Text(title, 13, "Foreground"));
        if (help.Length > 0) label.Children.Add(Text(help, 11, "Muted", margin: new Thickness(0, 4, 0, 0)));
        grid.Children.Add(label);
        if (control is FrameworkElement element) { element.HorizontalAlignment = HorizontalAlignment.Left; element.VerticalAlignment = VerticalAlignment.Center; }
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private TextBlock Section(string title) => Text(title, 16, "Foreground", FontWeights.SemiBold, new Thickness(0, 12, 0, 17));

    private TextBlock Text(string text, double size = 13, string role = "Foreground", FontWeight? weight = null, Thickness? margin = null)
    {
        _settingsTextSizes.Add(size);
        Resources[SettingsSizeKey(size)] = size * _state.TextStyle("Settings").FontSize / 13;
        var block = new TextBlock { Text = text, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0) };
        block.SetResourceReference(TextBlock.FontSizeProperty, SettingsSizeKey(size));
        block.SetResourceReference(TextBlock.ForegroundProperty, role + "Brush");
        return block;
    }

    private sealed class NativeDialogOwner(IntPtr handle) : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }

    private static Button Button(object content, Action action)
    {
        var button = new Button { Content = content };
        button.Click += (_, _) => action();
        return button;
    }

    private static ComboBox Combo(IEnumerable<string> items, int selected)
    {
        var combo = new ComboBox { ItemsSource = items.ToList(), SelectedIndex = selected, Padding = new Thickness(9, 5, 9, 5), MinHeight = 31 };
        return combo;
    }

    private static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    private string FormatInstant(Instant instant)
    {
        var zoned = instant.InZone(_state.DisplayZone);
        return zoned.LocalDateTime.ToString("uuuu-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC" + zoned.Offset.ToString("+HH:mm", CultureInfo.InvariantCulture) + " · " + _state.DisplayZone.Id;
    }

    private void OpenHttps(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        { MessageBox.Show(this, T("无法打开浏览器：", "Could not open the browser: ") + exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private static void Automation(DependencyObject element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);

    private static TVisual? FindVisualChild<TVisual>(DependencyObject parent) where TVisual : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TVisual matching) return matching;
            var descendant = FindVisualChild<TVisual>(child);
            if (descendant is not null) return descendant;
        }
        return null;
    }
}
