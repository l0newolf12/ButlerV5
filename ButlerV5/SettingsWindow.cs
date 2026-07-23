using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Skua.Core.Interfaces;
using Skua.WPF;

namespace ButlerV5;

/// <summary>Colors a log line by its severity.</summary>
public class LevelToBrushConverter : IValueConverter
{
    public static readonly Brush Info = Freeze("#C4C4CC");
    public static readonly Brush Success = Freeze("#3FBF6F");
    public static readonly Brush Warning = Freeze("#FFA23E");
    public static readonly Brush Error = Freeze("#E05252");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        LogLevel.Success => Success,
        LogLevel.Warning => Warning,
        LogLevel.Error => Error,
        _ => Info,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

    private static Brush Freeze(string hex)
    {
        SolidColorBrush b = new((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

/// <summary>
/// ButlerV5's own settings + log page (replaces Skua's native option editor).
/// Layout: left column = settings (top 2/3) over the selected setting's description
/// (bottom 1/3); right column = this account's live debug log. Changing a setting
/// saves immediately (and the fleet's live-reload picks it up).
/// </summary>
public class SettingsWindow : CustomWindow
{
    private static readonly Brush FallbackBg = Hex("#1B1B1F");
    private static readonly Brush FallbackPanel = Hex("#303030");
    private static readonly Brush FallbackText = Hex("#F0F0F3");
    private static readonly Brush FallbackSubText = Hex("#9A9AA5");
    private static readonly Brush AccentBrush = Hex("#4F8CFF");
    private static readonly Brush RowSelectedBrush = Hex("#3A3A44");

    private readonly ButlerV5Plugin _plugin;
    private readonly TextBlock _descTitle;
    private readonly TextBlock _descBody;
    private readonly ListBox _logList;
    private readonly ObservableCollection<LogLine> _logEntries = new();
    private readonly CheckBox _followTail;
    private readonly DispatcherTimer _logTimer;
    private Border? _selectedRow;

    public SettingsWindow(ButlerV5Plugin plugin)
    {
        _plugin = plugin;

        Title = "Butler V5 - Settings";
        TitleText = "Butler V5 - Settings";
        Width = 840;
        Height = 600;
        MinWidth = 700;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        if (Application.Current?.TryFindResource("CustomWindow") is Style chrome &&
            chrome.TargetType.IsAssignableFrom(typeof(CustomWindow)))
            Style = chrome;
        else
            Template = BuildSafetyTemplate();

        SetThemeBrush(this, BackgroundProperty, "MaterialDesignPaper", FallbackBg);

        // ---- root: left (settings+desc) | splitter | right (logs) ----
        Grid root = new() { Margin = new Thickness(12) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.7, GridUnitType.Star), MinWidth = 300 });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });

        // ===== LEFT: settings (2/3) over description (1/3) =====
        Grid left = new();
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        StackPanel settingsPanel = new();
        bool firstRow = true;
        foreach (IOption option in _plugin.OptionContainer?.Options ?? new List<IOption>())
        {
            if (!firstRow)
                settingsPanel.Children.Add(MakeSeparator());
            firstRow = false;
            settingsPanel.Children.Add(BuildSettingRow(option));
        }

        ScrollViewer settingsScroll = new()
        {
            Content = settingsPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Border settingsBox = new()
        {
            Child = settingsScroll,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 6),
            BorderThickness = new Thickness(1),
        };
        SetThemeBrush(settingsBox, Border.BackgroundProperty, "MaterialDesignPaper", FallbackPanel);
        SetThemeBrush(settingsBox, Border.BorderBrushProperty, "MaterialDesignDivider", RowSelectedBrush);
        Grid.SetRow(settingsBox, 0);
        left.Children.Add(settingsBox);

        Border descPanel = new()
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 6, 0, 0),
            BorderThickness = new Thickness(1),
        };
        SetThemeBrush(descPanel, Border.BackgroundProperty, "MaterialDesignPaper", FallbackPanel);
        SetThemeBrush(descPanel, Border.BorderBrushProperty, "MaterialDesignDivider", RowSelectedBrush);

        StackPanel descStack = new();
        _descTitle = MakeText(14, FallbackText);
        _descTitle.FontWeight = FontWeights.SemiBold;
        _descTitle.Text = "Click a setting";
        _descBody = MakeText(12, FallbackSubText);
        _descBody.TextWrapping = TextWrapping.Wrap;
        _descBody.Margin = new Thickness(0, 6, 0, 0);
        _descBody.Text = "Its description shows here. Changes save instantly and every other logged-in butler picks them up within a couple of seconds.";
        descStack.Children.Add(_descTitle);
        descStack.Children.Add(_descBody);

        // Author + version, shown after the description (version pulled from the assembly so
        // it always matches the .csproj, no hardcoded string to forget on a bump).
        var asmVer = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        string verText = asmVer != null ? $"{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}" : "?";
        TextBlock meta = MakeText(11, FallbackSubText);
        meta.Margin = new Thickness(0, 16, 0, 0);
        meta.Text = $"Author: l0newolf12\nVersion: {verText}";
        descStack.Children.Add(meta);

        ScrollViewer descScroll = new() { Content = descStack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        descPanel.Child = descScroll;
        Grid.SetRow(descPanel, 1);
        left.Children.Add(descPanel);

        Grid.SetColumn(left, 0);
        root.Children.Add(left);

        // ===== splitter =====
        GridSplitter splitter = new()
        {
            Width = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
        };
        Grid.SetColumn(splitter, 1);
        root.Children.Add(splitter);

        // ===== RIGHT: log viewer =====
        Grid right = new() { Margin = new Thickness(6, 0, 0, 0) };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });          // header
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // list
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });          // buttons

        Grid logHeader = new() { Margin = new Thickness(0, 0, 0, 6) };
        logHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        logHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock logTitle = MakeText(13, FallbackText);
        logTitle.FontWeight = FontWeights.SemiBold;
        logTitle.Text = $"Logs - {_plugin.OwnUsername ?? "this account"}";
        logHeader.Children.Add(logTitle);
        _followTail = new CheckBox { Content = "Follow tail", IsChecked = true, Foreground = FallbackSubText, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_followTail, 1);
        logHeader.Children.Add(_followTail);
        Grid.SetRow(logHeader, 0);
        right.Children.Add(logHeader);

        _logList = new ListBox
        {
            ItemsSource = _logEntries,
            BorderThickness = new Thickness(1),
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            ItemTemplate = BuildLogItemTemplate(),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_logList, ScrollBarVisibility.Auto);
        SetThemeBrush(_logList, BackgroundProperty, "MaterialDesignPaper", FallbackPanel);
        SetThemeBrush(_logList, Border.BorderBrushProperty, "MaterialDesignDivider", RowSelectedBrush);
        Grid.SetRow(_logList, 1);
        right.Children.Add(_logList);

        Grid logButtons = new() { Margin = new Thickness(0, 6, 0, 0) };
        logButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        logButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        StackPanel leftButtons = new() { Orientation = Orientation.Horizontal };
        leftButtons.Children.Add(MakeSmallButton("Copy", "Copy all log lines to the clipboard.", CopyLog));
        leftButtons.Children.Add(MakeSmallButton("Save", "Write the current log to a timestamped file in the butler-logs folder.", SaveLog));
        leftButtons.Children.Add(MakeSmallButton("Clear", "Clear the log view + in-memory buffer (does not delete files on disk).", ClearLog));
        logButtons.Children.Add(leftButtons);

        Button openFolder = MakeSmallButton("Open folder", "Open %APPDATA%\\Skua\\butlerv5_logs in Explorer.", OpenLogFolder);
        openFolder.Margin = new Thickness(0);
        Grid.SetColumn(openFolder, 1);
        logButtons.Children.Add(openFolder);

        Grid.SetRow(logButtons, 2);
        right.Children.Add(logButtons);

        Grid.SetColumn(right, 2);
        root.Children.Add(right);

        Content = root;

        RefreshLog();
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _logTimer.Tick += (_, _) => RefreshLog();
        _logTimer.Start();
        Closed += (_, _) => _logTimer.Stop();
    }

    private static DataTemplate BuildLogItemTemplate()
    {
        FrameworkElementFactory tb = new(typeof(TextBlock));
        tb.SetBinding(TextBlock.TextProperty, new Binding(nameof(LogLine.Text)));
        tb.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(LogLine.Level)) { Converter = new LevelToBrushConverter() });
        tb.SetValue(TextBlock.FontSizeProperty, 11.0);
        tb.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
        return new DataTemplate(typeof(LogLine)) { VisualTree = tb };
    }

    private Button MakeSmallButton(string text, string tooltip, Action onClick)
    {
        Button b = new()
        {
            Content = text,
            ToolTip = tooltip,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(10, 3, 10, 3),
            FontSize = 12,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private Border BuildSettingRow(IOption option)
    {
        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        TextBlock label = MakeText(13, FallbackText);
        label.Text = option.DisplayName;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.TextWrapping = TextWrapping.Wrap;
        label.Margin = new Thickness(0, 0, 10, 0);
        grid.Children.Add(label);

        FrameworkElement control = BuildControl(option);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.GotFocus += (_, _) => SelectOption(option);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);

        Border row = new()
        {
            Child = grid,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 2),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
        };
        row.MouseLeftButtonDown += (_, _) => SelectOption(option, row);
        return row;
    }

    private Border MakeSeparator()
    {
        Border sep = new() { Height = 1, Margin = new Thickness(4, 1, 4, 1) };
        SetThemeBrush(sep, Border.BackgroundProperty, "MaterialDesignDivider", RowSelectedBrush);
        return sep;
    }

    private FrameworkElement BuildControl(IOption option)
    {
        IOptionContainer oc = _plugin.OptionContainer!;
        string raw = SafeGetDirect(oc, option);

        if (option.Type == typeof(bool))
        {
            bool value = raw.Equals("True", StringComparison.OrdinalIgnoreCase) || raw == "1";
            CheckBox cb = new() { IsChecked = value };
            cb.Checked += (_, _) => _plugin.SaveSetting(option.Name, true);

            if (option.Name == "enabled")
            {
                // Master switch: confirm before disabling the whole plugin; revert if declined.
                cb.Unchecked += (s, _) =>
                {
                    MessageBoxResult r = MessageBox.Show(
                        "Turning this off stops ButlerV5 from working on ALL accounts - no following, " +
                        "summoning, or broadcasting until you turn it back on.\n\nDisable ButlerV5?",
                        "ButlerV5", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (r == MessageBoxResult.Yes)
                        _plugin.SaveSetting(option.Name, false);
                    else
                        ((CheckBox)s).IsChecked = true; // revert (re-fires Checked -> re-saves true, harmless)
                };
            }
            else
            {
                cb.Unchecked += (_, _) => _plugin.SaveSetting(option.Name, false);
            }
            return cb;
        }

        if (option.Type.IsEnum)
        {
            ComboBox combo = new() { MinWidth = 120 };
            foreach (object v in Enum.GetValues(option.Type))
                combo.Items.Add(v);
            object? current = TryParseEnum(option.Type, raw) ?? option.DefaultValue;
            combo.SelectedItem = combo.Items.Cast<object>().FirstOrDefault(i => i.Equals(current)) ?? combo.Items[0];
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem != null)
                    _plugin.SaveSetting(option.Name, combo.SelectedItem);
            };
            return combo;
        }

        // int and string both use a text box; int validates on commit.
        bool isInt = option.Type == typeof(int);
        TextBox tb = new() { Text = raw, MinWidth = isInt ? 80 : 150, MaxWidth = 220 };
        tb.LostFocus += (_, _) =>
        {
            if (isInt)
            {
                if (int.TryParse(tb.Text.Trim(), out int n))
                    _plugin.SaveSetting(option.Name, n);
                else
                    tb.Text = SafeGetDirect(oc, option); // revert bad input
            }
            else
            {
                _plugin.SaveSetting(option.Name, tb.Text);
            }
        };
        return tb;
    }

    private void SelectOption(IOption option, Border? row = null)
    {
        _descTitle.Text = option.DisplayName;
        _descBody.Text = string.IsNullOrWhiteSpace(option.Description) ? "(no description)" : option.Description;

        if (row != null)
        {
            if (_selectedRow != null)
                _selectedRow.Background = Brushes.Transparent;
            row.Background = RowSelectedBrush;
            _selectedRow = row;
        }
    }

    /// <summary>
    /// Syncs the bound collection to the in-memory buffer by sequence number: drop
    /// rolled-off lines, append new ones, update the last line if dedup rewrote it -
    /// only touching what actually changed, so there's no flicker.
    /// </summary>
    private void RefreshLog()
    {
        try
        {
            List<LogLine> snap = DebugLog.Snapshot();

            // Fast path: nothing changed.
            if (snap.Count == _logEntries.Count &&
                (snap.Count == 0 || (snap[^1].Seq == _logEntries[^1].Seq && snap[^1].Text == _logEntries[^1].Text)))
                return;

            int i = 0;
            foreach (LogLine s in snap)
            {
                while (i < _logEntries.Count && _logEntries[i].Seq < s.Seq)
                    _logEntries.RemoveAt(i);

                if (i < _logEntries.Count && _logEntries[i].Seq == s.Seq)
                {
                    if (_logEntries[i].Text != s.Text)
                        _logEntries[i].Text = s.Text; // dedup (xN) update, in place
                    i++;
                }
                else
                {
                    _logEntries.Insert(i, s.Copy());
                    i++;
                }
            }
            while (_logEntries.Count > i)
                _logEntries.RemoveAt(_logEntries.Count - 1);

            if (_followTail.IsChecked == true && _logEntries.Count > 0)
                _logList.ScrollIntoView(_logEntries[^1]);
        }
        catch
        {
        }
    }

    private void CopyLog()
    {
        try
        {
            string text = string.Join(Environment.NewLine, _logEntries.Select(e => e.Text));
            if (text.Length > 0)
            {
                Clipboard.SetText(text);
                DebugLog.Log("UI", $"copied {_logEntries.Count} log lines to clipboard", LogLevel.Success);
            }
        }
        catch (Exception ex)
        {
            DebugLog.Log("UI", $"copy failed: {ex.Message}", LogLevel.Error);
        }
    }

    private void SaveLog()
    {
        string? path = DebugLog.SaveExport(_plugin.OwnUsername);
        if (path != null)
            DebugLog.Log("UI", $"saved log to {System.IO.Path.GetFileName(path)}", LogLevel.Success);
        else
            DebugLog.Log("UI", "save log failed", LogLevel.Error);
    }

    private void OpenLogFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(DebugLog.LogDir);
            Process.Start(new ProcessStartInfo { FileName = DebugLog.LogDir, UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            DebugLog.Log("UI", $"open folder failed: {ex.Message}", LogLevel.Error);
        }
    }

    private void ClearLog()
    {
        DebugLog.ClearMemory();
        _logEntries.Clear();
    }

    private static string SafeGetDirect(IOptionContainer oc, IOption option)
    {
        try { return oc.GetDirect(option) ?? option.DefaultValue?.ToString() ?? ""; }
        catch { return option.DefaultValue?.ToString() ?? ""; }
    }

    private static object? TryParseEnum(Type type, string raw)
    {
        try { return Enum.Parse(type, raw, ignoreCase: true); }
        catch { return null; }
    }

    private TextBlock MakeText(double size, Brush fallback)
    {
        TextBlock t = new() { FontSize = size, Foreground = fallback };
        return t;
    }

    private static ControlTemplate BuildSafetyTemplate()
    {
        FrameworkElementFactory rootF = new(typeof(Grid));
        foreach (string part in new[] { "PART_Close", "PART_Maximize", "PART_Minimize" })
        {
            FrameworkElementFactory b = new(typeof(Button), part);
            b.SetValue(VisibilityProperty, Visibility.Collapsed);
            rootF.AppendChild(b);
        }
        FrameworkElementFactory adorner = new(typeof(System.Windows.Documents.AdornerDecorator));
        adorner.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        rootF.AppendChild(adorner);
        return new ControlTemplate(typeof(CustomWindow)) { VisualTree = rootF };
    }

    private static void SetThemeBrush(FrameworkElement element, DependencyProperty property, string themeKey, Brush fallback)
    {
        if (Application.Current?.TryFindResource(themeKey) is Brush)
            element.SetResourceReference(property, themeKey);
        else
            element.SetValue(property, fallback);
    }

    private static SolidColorBrush Hex(string hex)
    {
        SolidColorBrush brush = new((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
