using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using DeadlineCalendar.Core;
using NodaTime;

namespace DeadlineCalendar.App;

public sealed partial class SettingsWindow
{
    private double _conferenceWidthShare = .444;
    private double _followedHeightShare = .36;
    private Style? _conferenceOptionStyle;
    private Style? _conferenceCheckboxStyle;
    private Style? _followedOptionStyle;
    private bool _updatingHeaderConference;

    private UIElement BuildConferences()
    {
        EnsureConferenceOptionStyles();
        var grid = new Grid();
        var catalogColumn = new ColumnDefinition { Width = new GridLength(_conferenceWidthShare, GridUnitType.Star), MinWidth = 240 };
        var detailColumn = new ColumnDefinition { Width = new GridLength(1 - _conferenceWidthShare, GridUnitType.Star), MinWidth = 280 };
        grid.ColumnDefinitions.Add(catalogColumn);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(detailColumn);
        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var search = new TextBox { Text = _query, ToolTip = T("搜索简称、全称或分类", "Search acronym, full name, or category"), Margin = new Thickness(0, 0, 0, 8) };
        Automation(search, T("搜索会议", "Search Conferences"));
        search.TextChanged += (_, _) => { _query = search.Text; RenderCatalog(); };
        var searchPanel = new StackPanel();
        searchPanel.Children.Add(Text(T("搜索会议", "Search Conferences"), 12, "Muted", margin: new Thickness(0, 0, 0, 6)));
        searchPanel.Children.Add(search);
        left.Children.Add(searchPanel);
        var filter = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var categories = new List<string> { T("全部分类", "All Categories") };
        categories.AddRange(_state.Catalog.Series.Select(s => s.Category).Append("AI").Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().OrderBy(s => s));
        var category = Combo(categories, _category == "*" ? 0 : Math.Max(1, categories.IndexOf(_category)));
        category.Width = 135;
        category.SelectionChanged += (_, _) => { _category = category.SelectedIndex == 0 ? "*" : (category.SelectedItem as string ?? "AI"); RenderCatalog(); };
        DockPanel.SetDock(category, Dock.Left);
        filter.Children.Add(category);
        _count = Text("", 11, "Muted", margin: new Thickness(12, 0, 0, 0));
        _count.VerticalAlignment = VerticalAlignment.Center;
        filter.Children.Add(_count);
        Grid.SetRow(filter, 1);
        left.Children.Add(filter);
        _catalogList = new ListBox();
        Automation(_catalogList, T("会议目录", "Conference Catalog"));
        _catalogList.SelectionChanged += (_, _) =>
        {
            if (_renderingCatalog) return;
            if (_catalogList.SelectedItem is ListBoxItem item && item.Tag is string key) { _selectedKey = key; RenderDetails(); UpdateFollowedSelection(); }
        };
        Grid.SetRow(_catalogList, 2);
        left.Children.Add(_catalogList);
        grid.Children.Add(left);
        var verticalDivider = ConferenceDivider(true, T("调整会议目录与详情的宽度", "Resize Catalog And Details"), () =>
        {
            var width = catalogColumn.ActualWidth + detailColumn.ActualWidth;
            if (width > 0) _conferenceWidthShare = Math.Clamp(catalogColumn.ActualWidth / width, .01, .99);
        });
        Grid.SetColumn(verticalDivider, 1);
        grid.Children.Add(verticalDivider);
        var right = new Grid();
        var followedRow = new RowDefinition { Height = new GridLength(_followedHeightShare, GridUnitType.Star), MinHeight = 96 };
        var detailsRow = new RowDefinition { Height = new GridLength(1 - _followedHeightShare, GridUnitType.Star), MinHeight = 144 };
        right.RowDefinitions.Add(followedRow);
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        right.RowDefinitions.Add(detailsRow);
        var followedSection = new Grid();
        followedSection.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        followedSection.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var followedHeading = new StackPanel();
        followedHeading.Children.Add(Text(T("关注顺序", "Followed Priority"), 14, "Foreground", FontWeights.SemiBold));
        followedHeading.Children.Add(Text(T("排序决定同日颜色与同时截止的优先级。勾选“固定”可固定一个会议；取消后恢复最近截止。", "Order determines same-day colors and deadline tie priority. Check Pinned to keep one conference in the header; uncheck it to show the nearest deadline."), 11, "Muted", margin: new Thickness(0, 4, 0, 8)));
        followedSection.Children.Add(followedHeading);
        _followedPanel = new StackPanel();
        var followedScroll = new ScrollViewer { Content = _followedPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(followedScroll, 1);
        followedSection.Children.Add(followedScroll);
        right.Children.Add(followedSection);
        var divider = ConferenceDivider(false, T("调整关注顺序与会议详情的高度", "Resize Followed Priority And Conference Details"), () =>
        {
            var height = followedRow.ActualHeight + detailsRow.ActualHeight;
            if (height > 0) _followedHeightShare = Math.Clamp(followedRow.ActualHeight / height, .01, .99);
        });
        Grid.SetRow(divider, 1);
        right.Children.Add(divider);
        _detailPanel = new StackPanel { Margin = new Thickness(0, 0, 10, 10) };
        _detailScroll = Scroll(_detailPanel);
        Grid.SetRow(_detailScroll, 2);
        right.Children.Add(_detailScroll);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    private GridSplitter ConferenceDivider(bool vertical, string accessibleName, Action rememberRatio)
    {
        var splitter = new GridSplitter
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = vertical ? GridResizeDirection.Columns : GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext, ShowsPreview = false,
            Cursor = vertical ? Cursors.SizeWE : Cursors.SizeNS, Focusable = true,
            KeyboardIncrement = 12, DragIncrement = 1
        };
        Automation(splitter, accessibleName);
        var surface = new FrameworkElementFactory(typeof(Grid));
        surface.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
        var line = new FrameworkElementFactory(typeof(Border), "DividerLine");
        line.SetResourceReference(Border.BackgroundProperty, "SeparatorBrush");
        line.SetValue(vertical ? FrameworkElement.WidthProperty : FrameworkElement.HeightProperty, 1.0);
        line.SetValue(vertical ? FrameworkElement.HorizontalAlignmentProperty : FrameworkElement.VerticalAlignmentProperty,
            vertical ? (object)HorizontalAlignment.Center : VerticalAlignment.Center);
        surface.AppendChild(line);
        var template = new ControlTemplate(typeof(GridSplitter)) { VisualTree = surface };
        var focused = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("AccentBrush"), "DividerLine"));
        focused.Setters.Add(new Setter(vertical ? FrameworkElement.WidthProperty : FrameworkElement.HeightProperty, 2.0, "DividerLine"));
        template.Triggers.Add(focused);
        splitter.Template = template;
        splitter.DragCompleted += (_, _) => Dispatcher.BeginInvoke(rememberRatio, DispatcherPriority.Loaded);
        splitter.KeyUp += (_, args) =>
        {
            if (args.Key is Key.Left or Key.Right or Key.Up or Key.Down)
                Dispatcher.BeginInvoke(rememberRatio, DispatcherPriority.Loaded);
        };
        return splitter;
    }

    private void EnsureConferenceOptionStyles()
    {
        _conferenceOptionStyle ??= (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ListBoxItem">
              <Setter Property="Padding" Value="10,8"/><Setter Property="Margin" Value="2,2"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
              <Setter Property="ToolTipService.IsEnabled" Value="False"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem"><Border x:Name="Row" Background="Transparent" BorderBrush="Transparent" BorderThickness="1" CornerRadius="6" Padding="{TemplateBinding Padding}"><ContentPresenter/></Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsSelected" Value="True"><Setter TargetName="Row" Property="Background" Value="{DynamicResource SelectionBrush}"/></Trigger>
                  <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Row" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Row" Property="Opacity" Value="0.6"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """);
        _followedOptionStyle ??= (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
              <Setter Property="Background" Value="{DynamicResource SurfaceBrush}"/><Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource SeparatorBrush}"/><Setter Property="BorderThickness" Value="1"/>
              <Setter Property="Cursor" Value="Hand"/><Setter Property="VerticalContentAlignment" Value="Center"/><Setter Property="ToolTipService.IsEnabled" Value="False"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button"><Border x:Name="Row" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6" Padding="{TemplateBinding Padding}"><ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/></Border>
                <ControlTemplate.Triggers><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Row" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger></ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """);
        _conferenceCheckboxStyle ??= (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="CheckBox">
              <Setter Property="Cursor" Value="Hand"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/><Setter Property="ToolTipService.IsEnabled" Value="False"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CheckBox"><Grid><Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions><Grid Width="16" Height="16" VerticalAlignment="Center">
                <Border x:Name="Box" CornerRadius="3" BorderThickness="1" BorderBrush="{DynamicResource SeparatorBrush}" Background="{DynamicResource SurfaceBrush}"/>
                <Path x:Name="Check" Data="M 3,8 L 6.5,11.5 L 13,4.5" Stroke="White" StrokeThickness="1.7" StrokeStartLineCap="Round" StrokeEndLineCap="Round" Visibility="Collapsed"/>
              </Grid><ContentPresenter x:Name="Label" Grid.Column="1" Margin="6,0,0,0" VerticalAlignment="Center"/></Grid><ControlTemplate.Triggers>
                <Trigger Property="HasContent" Value="False"><Setter TargetName="Label" Property="Visibility" Value="Collapsed"/></Trigger>
                <Trigger Property="IsChecked" Value="True"><Setter TargetName="Box" Property="Background" Value="{DynamicResource AccentBrush}"/><Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/><Setter TargetName="Check" Property="Visibility" Value="Visible"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/><Setter TargetName="Box" Property="BorderThickness" Value="2"/></Trigger>
              </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
            </Style>
            """);
    }

    private void RenderCatalog()
    {
        if (_catalogList is null) return;
        var offset = FindVisualChild<ScrollViewer>(_catalogList)?.VerticalOffset ?? 0;
        _renderingCatalog = true;
        try
        {
            _catalogList.Items.Clear();
            var query = _query.Trim();
            var series = _state.Catalog.Series.Where(s => (_category == "*" || string.Equals(s.Category, _category, StringComparison.OrdinalIgnoreCase)) &&
                (query.Length == 0 || $"{s.Title} {s.Description} {s.Category} {s.Key}".Contains(query, StringComparison.OrdinalIgnoreCase))).OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToList();
            _count.Text = T($"{series.Count} 个会议", $"{series.Count} conferences");
            foreach (var conference in series)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                var checkbox = new CheckBox { IsChecked = _state.Preferences.Followed.Any(x => x.Key == conference.Key), Style = _conferenceCheckboxStyle, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                Automation(checkbox, T("关注 ", "Follow ") + conference.Title);
                checkbox.Checked += (_, _) => SetFollowed(conference, true);
                checkbox.Unchecked += (_, _) => SetFollowed(conference, false);
                row.Children.Add(checkbox);
                var label = new StackPanel();
                label.Children.Add(Text(conference.Title + "  ·  " + conference.Category, 13, "Foreground", FontWeights.SemiBold));
                var description = Text(conference.Description, 11, "Muted", margin: new Thickness(0, 3, 0, 0));
                description.MaxHeight = 32;
                description.TextTrimming = TextTrimming.CharacterEllipsis;
                label.Children.Add(description);
                Grid.SetColumn(label, 1);
                row.Children.Add(label);
                var item = new ListBoxItem { Content = row, Tag = conference.Key, Style = _conferenceOptionStyle };
                _catalogList.Items.Add(item);
                if (conference.Key == _selectedKey) item.IsSelected = true;
            }
            if (series.Count == 0)
                _catalogList.Items.Add(new ListBoxItem { IsEnabled = false, Style = _conferenceOptionStyle, Content = Text(_state.Catalog.Series.Count == 0 ? T("目录尚未载入。请在“数据与同步”中更新。", "Catalog is not loaded. Refresh it in Data & Sync.") : T("没有匹配的会议。", "No matching conferences."), 12, "Muted") });
        }
        finally { _renderingCatalog = false; }
        Dispatcher.BeginInvoke(() => FindVisualChild<ScrollViewer>(_catalogList)?.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
    }

    private void SetFollowed(ConferenceSeries series, bool followed)
    {
        var existing = _state.Preferences.Followed.FirstOrDefault(x => x.Key == series.Key);
        if (followed && existing is null)
            _state.Preferences.Followed.Add(new FollowedConference { Key = series.Key, ColorHex = ConferenceColors[_state.Preferences.Followed.Count % ConferenceColors.Length], RecurrenceYears = series.Title.Equals("ICCV", StringComparison.OrdinalIgnoreCase) || series.Title.Equals("ECCV", StringComparison.OrdinalIgnoreCase) ? 2 : 1 });
        else if (!followed)
        {
            if (existing is not null) _state.Preferences.Followed.Remove(existing);
            if (string.Equals(_state.Preferences.HeaderConferenceKey?.Trim(), series.Key, StringComparison.OrdinalIgnoreCase))
                _state.Preferences.HeaderConferenceKey = null;
        }
        _selectedKey = series.Key;
        Commit();
        RenderFollowed();
        RenderDetails();
    }

    private void RenderFollowed()
    {
        _followedPanel.Children.Clear();
        if (_state.Preferences.Followed.Count == 0)
        {
            _followedPanel.Children.Add(Text(T("在左侧勾选会议，即可开始。关注系列会自动包含后续届次。", "Check conferences on the left to begin. Following a series includes future editions."), 12, "Muted", margin: new Thickness(0, 4, 0, 8)));
            return;
        }
        for (var index = 0; index < _state.Preferences.Followed.Count; index++)
        {
            var followed = _state.Preferences.Followed[index];
            var title = _state.Catalog.Series.FirstOrDefault(x => x.Key == followed.Key)?.Title ?? followed.Key;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new DockPanel();
            var color = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(_state.ConferenceColor(followed.Key)), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(color, Dock.Left);
            label.Children.Add(color);
            var titleLabel = Text($"{index + 1:00}  {title}", 12);
            titleLabel.TextWrapping = TextWrapping.NoWrap;
            titleLabel.TextTrimming = TextTrimming.CharacterEllipsis;
            label.Children.Add(titleLabel);
            var select = Button(label, () => { _selectedKey = followed.Key; RenderDetails(); RenderCatalog(); UpdateFollowedSelection(); });
            Automation(select, title);
            select.Style = _followedOptionStyle;
            select.Tag = followed.Key;
            if (_selectedKey == followed.Key) select.SetResourceReference(Control.BackgroundProperty, "SelectionBrush");
            select.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            select.Padding = new Thickness(9, 5, 9, 5);
            row.Children.Add(select);
            var header = new CheckBox
            {
                Content = Text(T("固定", "Pinned"), 11),
                IsChecked = string.Equals(_state.Preferences.HeaderConferenceKey, followed.Key, StringComparison.OrdinalIgnoreCase),
                Tag = followed.Key, Style = _conferenceCheckboxStyle,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 5, 0)
            };
            Automation(header, T("在顶部固定显示 ", "Pin To Header: ") + title);
            header.Checked += (_, _) => SetHeaderConference(followed.Key, true);
            header.Unchecked += (_, _) => SetHeaderConference(followed.Key, false);
            Grid.SetColumn(header, 1);
            row.Children.Add(header);
            var up = Button("↑", () => MoveFollowed(followed.Key, -1));
            up.IsEnabled = index > 0;
            up.ToolTip = T("提高优先级", "Move Up");
            Automation(up, T("提高 ", "Move Up ") + title);
            up.Margin = new Thickness(5, 0, 0, 0);
            up.Padding = new Thickness(9, 4, 9, 4);
            Grid.SetColumn(up, 2);
            row.Children.Add(up);
            var down = Button("↓", () => MoveFollowed(followed.Key, 1));
            down.IsEnabled = index < _state.Preferences.Followed.Count - 1;
            down.ToolTip = T("降低优先级", "Move Down");
            Automation(down, T("降低 ", "Move Down ") + title);
            down.Margin = new Thickness(5, 0, 0, 0);
            down.Padding = new Thickness(9, 4, 9, 4);
            Grid.SetColumn(down, 3);
            row.Children.Add(down);
            _followedPanel.Children.Add(row);
        }
    }

    private void SetHeaderConference(string key, bool pinned)
    {
        if (_updatingHeaderConference) return;
        var previous = _state.Preferences.HeaderConferenceKey;
        var selected = pinned ? key : string.Equals(previous, key, StringComparison.OrdinalIgnoreCase) ? null : previous;
        if (string.Equals(previous, selected, StringComparison.Ordinal)) return;
        _state.Preferences.HeaderConferenceKey = selected;
        _updatingHeaderConference = true;
        try
        {
            foreach (var row in _followedPanel.Children.OfType<Grid>())
            foreach (var option in row.Children.OfType<CheckBox>())
                option.IsChecked = option.Tag is string optionKey && string.Equals(optionKey, selected, StringComparison.OrdinalIgnoreCase);
        }
        finally { _updatingHeaderConference = false; }
        Commit();
    }

    private void UpdateFollowedSelection()
    {
        if (_followedPanel is null) return;
        foreach (var row in _followedPanel.Children.OfType<Grid>())
        foreach (var option in row.Children.OfType<Button>().Where(button => button.Tag is string))
            option.SetResourceReference(Control.BackgroundProperty, Equals(option.Tag, _selectedKey) ? "SelectionBrush" : "SurfaceBrush");
    }

    private void MoveFollowed(string key, int direction)
    {
        var list = _state.Preferences.Followed;
        var oldIndex = list.FindIndex(x => x.Key == key);
        var newIndex = oldIndex + direction;
        if (oldIndex < 0 || newIndex < 0 || newIndex >= list.Count) return;
        (list[oldIndex], list[newIndex]) = (list[newIndex], list[oldIndex]);
        Commit();
        RenderFollowed();
    }

    private void RenderDetails()
    {
        var offset = _renderedDetailKey == _selectedKey ? _detailScroll.VerticalOffset : 0;
        _renderedDetailKey = _selectedKey;
        _detailPanel.Children.Clear();
        var series = _state.Catalog.Series.FirstOrDefault(x => x.Key == _selectedKey);
        if (series is null)
        {
            _detailPanel.Children.Add(Text(T("会议详情", "Conference Details"), 17, "Foreground", FontWeights.SemiBold));
            _detailPanel.Children.Add(Text(T("选择一个会议，调整颜色、推测周期和投稿轮次。", "Select a conference to edit its color, recurrence, and submission rounds."), 12, "Muted", margin: new Thickness(0, 8, 0, 0)));
            return;
        }
        _detailPanel.Children.Add(Text(series.Title, 22, "Foreground", FontWeights.SemiBold));
        _detailPanel.Children.Add(Text(series.Description, 12, "Muted", margin: new Thickness(0, 4, 0, 4)));
        _detailPanel.Children.Add(Text($"{series.Category} · CCF {series.Rank} · {series.Key}", 11, "Muted", margin: new Thickness(0, 0, 0, 12)));
        var followed = _state.Preferences.Followed.FirstOrDefault(x => x.Key == series.Key);
        if (followed is null)
        {
            var follow = Button(T("关注此会议", "Follow This Conference"), () => { SetFollowed(series, true); RenderCatalog(); });
            follow.HorizontalAlignment = HorizontalAlignment.Left;
            _detailPanel.Children.Add(follow);
            _detailPanel.Children.Add(Text(T("关注后可设置颜色、周期和轮次筛选。", "Follow this series to configure color, recurrence, and round filters."), 11, "Muted", margin: new Thickness(0, 8, 0, 10)));
        }
        else
        {
            _detailPanel.Children.Add(Text(T("会议颜色", "Conference Color"), 12, "Foreground", FontWeights.SemiBold));
            _detailPanel.Children.Add(ColorEditor(_state.ConferenceColor(series.Key), color => { followed.ColorHex = Hex(color); Commit(); RenderFollowed(); }));
            _detailPanel.Children.Add(Text(T("下一届推测周期", "Next-Edition Estimation"), 12, "Foreground", FontWeights.SemiBold, new Thickness(0, 13, 0, 6)));
            var recurrence = Combo([T("每年 · 1 年", "Annual · 1 Year"), T("隔年 · 2 年", "Biennial · 2 Years"), T("关闭推测", "Estimates Off")], followed.RecurrenceYears == 0 ? 2 : followed.RecurrenceYears == 2 ? 1 : 0);
            recurrence.HorizontalAlignment = HorizontalAlignment.Left;
            recurrence.MinWidth = 180;
            recurrence.SelectionChanged += (_, _) => { followed.RecurrenceYears = recurrence.SelectedIndex == 2 ? 0 : recurrence.SelectedIndex + 1; Commit(); };
            _detailPanel.Children.Add(recurrence);
        }
        _detailPanel.Children.Add(Text(T("投稿轮次与来源记录", "Submission Rounds & Source Records"), 14, "Foreground", FontWeights.SemiBold, new Thickness(0, 18, 0, 4)));
        _detailPanel.Children.Add(Text(T("勾选表示显示。保留来源备注；空白或无法唯一匹配的轮次不会跨届继承排除。", "Checked rounds are displayed. Original notes are preserved; blank or ambiguous notes cannot inherit exclusions across editions."), 11, "Muted", margin: new Thickness(0, 0, 0, 12)));
        var editions = series.Editions.OrderByDescending(e => e.Year).ThenBy(e => e.Id).ToList();
        foreach (var edition in editions)
        {
            var expansionKey = series.Key + "/" + edition.Id;
            var body = new StackPanel { Margin = new Thickness(4, 8, 5, 12) };
            body.Children.Add(Text(T("来源时区：", "Source Zone: ") + edition.TimeZone, 11, "Muted", margin: new Thickness(0, 0, 0, 8)));
            foreach (var round in edition.Timeline)
            {
                var content = new StackPanel();
                content.Children.Add(Text(string.IsNullOrWhiteSpace(round.Comment) ? T("全文截止（来源未备注轮次）", "Full-Paper Deadline (No Round Note)") : round.Comment, 12, "Foreground", FontWeights.SemiBold));
                content.Children.Add(Text(round.DeadlineText + "  " + edition.TimeZone, 11, "Muted", margin: new Thickness(0, 4, 0, 0)));
                if (DeadlineTime.TryParse(round.DeadlineText, edition.TimeZone, out var instant, out _))
                    content.Children.Add(Text(T("显示时区：", "Display Zone: ") + FormatInstant(instant), 11, "Muted", margin: new Thickness(0, 3, 0, 0)));
                else content.Children.Add(Text(string.Equals(round.DeadlineText.Trim(), "TBD", StringComparison.OrdinalIgnoreCase) ? T("日期待定；有可靠依据时将显示空心预计日期。", "Date pending; an outlined estimate appears when a reliable source exists.") : T("待核验：来源日期或时区无法唯一解释。", "Needs verification: source date or timezone cannot be uniquely interpreted."), 11, "Accent", margin: new Thickness(0, 3, 0, 0)));
                if (RoundSelection.NeedsReview(series, edition, round, _state.Preferences))
                    content.Children.Add(Text(T("待检查 · 未能继承之前的轮次选择", "Review needed · Previous round selection could not be inherited"), 11, "Accent", margin: new Thickness(0, 4, 0, 0)));
                var checkbox = new CheckBox { Content = content, IsChecked = !RoundSelection.IsExcluded(series, edition, round, _state.Preferences), IsEnabled = followed is not null, Margin = new Thickness(0, 0, 0, 12), VerticalContentAlignment = VerticalAlignment.Top };
                Automation(checkbox, series.Title + " " + edition.Year + " " + round.Comment);
                void SetRound(bool include)
                {
                    RoundSelection.SetExcluded(series, edition, round, _state.Preferences, !include);
                    Commit();
                    Dispatcher.BeginInvoke(RenderDetails, DispatcherPriority.Background);
                }
                checkbox.Checked += (_, _) => SetRound(true);
                checkbox.Unchecked += (_, _) => SetRound(false);
                body.Children.Add(checkbox);
            }
            if (edition.Timeline.Count == 0) body.Children.Add(Text(T("上游尚未提供截止记录。", "No deadline records have been provided upstream."), 11, "Muted"));
            if (Uri.TryCreate(edition.Link, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps)
            {
                var link = Button(T("会议网站 ↗", "Conference Website ↗"), () => OpenHttps(url));
                link.HorizontalAlignment = HorizontalAlignment.Left;
                link.Padding = new Thickness(9, 5, 9, 5);
                body.Children.Add(link);
            }
            var expander = new Expander { Header = $"{series.Title} {edition.Year}", Content = body, IsExpanded = _editionExpansion.TryGetValue(expansionKey, out var expanded) ? expanded : edition == editions.First(), Margin = new Thickness(0, 0, 0, 8) };
            expander.Expanded += (_, _) => _editionExpansion[expansionKey] = true;
            expander.Collapsed += (_, _) => _editionExpansion[expansionKey] = false;
            _detailPanel.Children.Add(expander);
        }
        Dispatcher.BeginInvoke(() => _detailScroll.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
    }

}
