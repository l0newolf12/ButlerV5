using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Skua.WPF;

namespace ButlerV5;

/// <summary>
/// Roster window, card layout: every online ButlerV5 account is a card with per-row
/// actions (Follow / Stop, Summon / Release). Inherits Skua's CustomWindow chrome and
/// pulls colors/button styles from the client's Material Design theme so it follows
/// the user's theme colors; hardcoded brushes are only fallbacks outside Skua.
/// Rows are rebuilt only when the underlying data changes, so hover states and
/// scroll position survive the 2s auto-refresh.
/// </summary>
public class RosterWindow : CustomWindow
{
    // Fallbacks if a theme resource is missing (e.g. running outside Skua).
    private static readonly Brush FallbackBg = Hex("#1B1B1F");
    private static readonly Brush FallbackCard = Hex("#26262C");
    private static readonly Brush FallbackText = Hex("#F0F0F3");
    private static readonly Brush FallbackSubText = Hex("#9A9AA5");
    private static readonly Brush FallbackAccent = Hex("#4F8CFF");

    // Semantic colors kept regardless of theme.
    private static readonly Brush GreenBrush = Hex("#3FBF6F");
    private static readonly Brush OrangeBrush = Hex("#FFA23E");
    private static readonly Brush OrangeButtonBrush = Hex("#E8862D");
    private static readonly Brush RedBrush = Hex("#E05252");

    private readonly ButlerV5Plugin _plugin;
    private readonly DispatcherTimer _timer;
    private readonly StackPanel _rows;
    private readonly TextBlock _meText;
    private readonly TextBlock _stateText;
    private readonly Button _stopButton;
    private readonly Button _releaseAllButton;
    private readonly Button _summonAllButton;
    private readonly TextBlock _footerText;
    private string _lastSnapshot = "";

    public RosterWindow(ButlerV5Plugin plugin)
    {
        _plugin = plugin;

        Title = "Butler V5";
        TitleText = "Butler V5";
        Width = 540;
        Height = 470;
        MinWidth = 430;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // CustomWindow's chrome is a *keyed* style ("CustomWindow") that Skua's own
        // windows apply explicitly; without a template containing PART_Close etc. its
        // Loaded handler null-crashes the whole client. Apply the style when present,
        // otherwise install a minimal template that satisfies the required parts.
        if (Application.Current?.TryFindResource("CustomWindow") is Style customChrome &&
            customChrome.TargetType.IsAssignableFrom(typeof(CustomWindow)))
        {
            Style = customChrome;
            DebugLog.Log("UI", "RosterWindow: applied Skua CustomWindow style");
        }
        else
        {
            Template = BuildSafetyTemplate();
            DebugLog.Log("UI", "RosterWindow: CustomWindow style not found, using safety template");
        }

        SetThemeBrush(this, BackgroundProperty, "MaterialDesignPaper", FallbackBg);

        DockPanel root = new() { Margin = new Thickness(14) };

        // ----- header -----
        Grid header = new() { Margin = new Thickness(2, 0, 2, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        StackPanel headerLeft = new();
        _meText = MakeText(16, "MaterialDesignBody", FallbackText);
        _meText.FontWeight = FontWeights.SemiBold;
        _stateText = MakeText(12, "MaterialDesignBodyLight", FallbackSubText);
        _stateText.Margin = new Thickness(0, 2, 0, 0);
        headerLeft.Children.Add(_meText);
        headerLeft.Children.Add(_stateText);
        header.Children.Add(headerLeft);

        StackPanel headerButtons = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        _stopButton = MakeButton("Stop following", RedBrush, (_, _) => { _plugin.StopFollow(); Refresh(force: true); });
        _stopButton.Visibility = Visibility.Collapsed;
        _stopButton.Margin = new Thickness(0, 0, 6, 0);
        headerButtons.Children.Add(_stopButton);

        _releaseAllButton = MakeButton("Release all", OrangeButtonBrush, (_, _) => { _plugin.ReleaseFollowers(); Refresh(force: true); });
        _releaseAllButton.Visibility = Visibility.Collapsed;
        _releaseAllButton.ToolTip = "Clear this account's followers list; released butlers park in their house.";
        headerButtons.Children.Add(_releaseAllButton);

        Grid.SetColumn(headerButtons, 1);
        header.Children.Add(headerButtons);

        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // ----- footer -----
        Grid footer = new() { Margin = new Thickness(2, 10, 2, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _footerText = MakeText(11, "MaterialDesignBodyLight", FallbackSubText);
        _footerText.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_footerText);

        Brush footerButtonBrush = Application.Current?.TryFindResource("MaterialDesignPaper") as Brush ?? Hex("#303030");

        _summonAllButton = MakeButton("Summon all", footerButtonBrush, (_, _) => { _plugin.SummonAll(); Refresh(force: true); });
        _summonAllButton.Margin = new Thickness(0, 0, 6, 0);
        _summonAllButton.ToolTip = "Order every online ButlerV5 account to follow this one.";
        Grid.SetColumn(_summonAllButton, 1);
        footer.Children.Add(_summonAllButton);

        Button settings = MakeButton("Settings", footerButtonBrush, (_, _) => _plugin.OpenSettings());
        settings.ToolTip = "Open the ButlerV5 plugin options.";
        Grid.SetColumn(settings, 2);
        footer.Children.Add(settings);

        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // ----- roster list, in its own bordered panel so it reads as a separate area -----
        _rows = new StackPanel();
        ScrollViewer scroll = new()
        {
            Content = _rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        Border listPanel = new()
        {
            Child = scroll,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 4, 6, 4),
        };
        SetThemeBrush(listPanel, Border.BorderBrushProperty, "MaterialDesignDivider", Hex("#3A3A44"));
        // Fill with the theme's paper brush: that's the exact color of the title bar
        // (#303030 in the default theme), while the window's visible content area is
        // painted lighter by Skua's window template - so the panel matches the chrome
        // and still contrasts with the content background.
        SetThemeBrush(listPanel, Border.BackgroundProperty, "MaterialDesignPaper", Hex("#303030"));
        root.Children.Add(listPanel);

        Content = root;

        // Instant refresh on any sync-file change; the timer is now just a backstop.
        _plugin.SyncFilesChanged += OnSyncFilesChanged;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh(force: true);

        Closed += (_, _) =>
        {
            _timer.Stop();
            _plugin.SyncFilesChanged -= OnSyncFilesChanged;
        };
    }

    private void OnSyncFilesChanged()
    {
        // Fired off-thread by the file watcher; marshal to the UI thread.
        try { Dispatcher.BeginInvoke(() => Refresh()); }
        catch { }
    }

    public void RefreshRoster() => Refresh(force: true);

    private void Refresh(bool force = false)
    {
        List<SyncData> online = Roster.GetOnline(_plugin.OwnUsername);
        HashSet<string> myFollowers = new(_plugin.CurrentFollowers, StringComparer.OrdinalIgnoreCase);
        string? followingWho = _plugin.FollowingWho;

        // ----- header / footer always update (cheap text) -----
        _meText.Text = _plugin.OwnUsername ?? "(not logged in)";
        _stateText.Text = followingWho != null
            ? $"Following {followingWho} ({(_plugin.FollowingHow == FollowSource.Ordered ? "summoned" : "manual")})"
            : "Not following anyone";
        if (followingWho != null)
            _stateText.Foreground = GreenBrush;
        else
            SetThemeBrush(_stateText, TextBlock.ForegroundProperty, "MaterialDesignBodyLight", FallbackSubText);
        _stopButton.Visibility = followingWho != null ? Visibility.Visible : Visibility.Collapsed;
        _releaseAllButton.Visibility = myFollowers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // While I'm a butler (following someone), I can't summon anyone (conga guard).
        _summonAllButton.IsEnabled = followingWho == null;
        _summonAllButton.ToolTip = followingWho == null
            ? "Order every online ButlerV5 account to follow this one."
            : $"You're following {followingWho} - stop that before summoning anyone.";

        // Honor the configurable refresh rate (option can change while we're open).
        TimeSpan interval = TimeSpan.FromSeconds(Math.Clamp(_plugin.RosterRefreshSeconds, 1, 30));
        if (_timer.Interval != interval)
            _timer.Interval = interval;
        _footerText.Text = online.Count == 1
            ? "1 plugin account online - refreshes every 2s"
            : $"{online.Count} plugin accounts online - refreshes every 2s";

        // ----- rows rebuild only when data actually changed -----
        string myServer = _plugin.MyServerName;
        string snapshot = string.Join(";", online.Select(d =>
            $"{d.Username}|{d.MapWithRoom}|{(d.Attacking ? 1 : 0)}|{(myFollowers.Contains(d.Username) ? 1 : 0)}" +
            $"|{d.Following}|{(d.ScriptOn ? 1 : 0)}|{d.ScriptName}|{(d.Parking ? 1 : 0)}|{d.Followers.Count}|{d.Server}")) +
            $"@{followingWho}@{myServer}";
        if (!force && snapshot == _lastSnapshot)
            return;
        _lastSnapshot = snapshot;

        _rows.Children.Clear();

        if (online.Count == 0)
        {
            TextBlock empty = MakeText(12, "MaterialDesignBodyLight", FallbackSubText);
            empty.Text = "No other ButlerV5 accounts online.\nAccounts appear here once they log in with the plugin installed.";
            empty.TextAlignment = TextAlignment.Center;
            empty.TextWrapping = TextWrapping.Wrap;
            empty.Margin = new Thickness(0, 40, 0, 0);
            _rows.Children.Add(empty);
            return;
        }

        bool firstRow = true;
        foreach (SyncData d in online)
        {
            if (!firstRow)
                _rows.Children.Add(MakeSeparator());
            firstRow = false;
            _rows.Children.Add(BuildRow(d, myFollowers.Contains(d.Username), d.Username.Equals(followingWho, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private static Border MakeSeparator()
    {
        Border separator = new() { Height = 1, Margin = new Thickness(4, 0, 4, 0) };
        SetThemeBrush(separator, Border.BackgroundProperty, "MaterialDesignDivider", Hex("#3A3A44"));
        return separator;
    }

    private Border BuildRow(SyncData d, bool followsMe, bool amFollowing)
    {
        // While THIS account is itself following someone (a "butler"), it must not
        // start new follows or summons - otherwise you get a conga line (A summons B,
        // B summons C, ...). Only the Stop/Release (untangle) actions stay live.
        bool iAmButler = _plugin.FollowingWho != null;

        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // name + location
        StackPanel info = new() { VerticalAlignment = VerticalAlignment.Center };

        StackPanel nameLine = new() { Orientation = Orientation.Horizontal };
        TextBlock name = MakeText(14, "MaterialDesignBody", FallbackText);
        name.Text = d.Username;
        name.FontWeight = FontWeights.SemiBold;
        nameLine.Children.Add(name);
        if (d.Attacking)
        {
            nameLine.Children.Add(new TextBlock
            {
                Text = "  ⚔ fighting",
                FontSize = 11,
                Foreground = OrangeBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        // Actually following me (manual or summoned) - from their own broadcast.
        bool followingMe = d.Following.Equals(_plugin.OwnUsername ?? "", StringComparison.OrdinalIgnoreCase) &&
                           !string.IsNullOrEmpty(d.Following);
        // In service to some OTHER master. Following or summoning them would chain
        // (me -> them -> their master = a conga line), so both actions are blocked below.
        bool followingOther = !string.IsNullOrEmpty(d.Following) && !followingMe;
        if (followingMe)
            nameLine.Children.Add(MakeChip("following me", GreenBrush));
        else if (followingOther)
            nameLine.Children.Add(MakeChip($"following {d.Following}", FallbackAccent));
        else if (followsMe)
            nameLine.Children.Add(MakeChip("summoned", FallbackSubText)); // ordered but not here yet (busy/parked/traveling)

        if (d.ScriptOn)
            nameLine.Children.Add(MakeChip($"busy: {(string.IsNullOrEmpty(d.ScriptName) ? "script" : d.ScriptName)}", OrangeBrush));

        if (d.Parking)
            nameLine.Children.Add(MakeChip("parking", FallbackSubText));

        // On a different game world from me -> can't be followed until same server.
        string myServer = _plugin.MyServerName;
        bool differentServer = !string.IsNullOrEmpty(d.Server) && !string.IsNullOrEmpty(myServer) &&
                               !d.Server.Equals(myServer, StringComparison.OrdinalIgnoreCase);
        if (differentServer)
            nameLine.Children.Add(MakeChip("on a different server", RedBrush));

        info.Children.Add(nameLine);

        TextBlock location = MakeText(11, "MaterialDesignBodyLight", FallbackSubText);
        location.Text = string.IsNullOrEmpty(d.Server) ? d.MapWithRoom : $"{d.MapWithRoom}   ·   {d.Server}";
        location.Margin = new Thickness(0, 2, 0, 0);
        info.Children.Add(location);
        grid.Children.Add(info);

        // actions
        Button followButton = amFollowing
            ? MakeButton("Stop", RedBrush, (_, _) => { _plugin.StopFollow(); Refresh(force: true); })
            : MakeButton("Follow", null, (_, _) =>
            {
                if (_plugin.IsScriptRunning)
                {
                    MessageBox.Show(this,
                        $"A script is running on this account ({_plugin.RunningScriptName}).\nStop it before following someone.",
                        "Butler V5");
                    return;
                }
                _plugin.StartFollow(d.Username, FollowSource.Manual);
                Refresh(force: true);
            });
        followButton.Margin = new Thickness(8, 0, 0, 0);
        followButton.VerticalAlignment = VerticalAlignment.Center;

        // Can't follow an account that's serving me - I summoned them, or they're
        // already following me. And can't start a new follow while I'm already a butler,
        // or reach an account on a different game world.
        if (!amFollowing && (followsMe || followingMe || followingOther || iAmButler || differentServer))
        {
            followButton.IsEnabled = false;
            followButton.ToolTip =
                iAmButler ? $"You're following {_plugin.FollowingWho} - stop that first."
                : followsMe ? $"You summoned {d.Username} - release them before you can follow them."
                : followingMe ? $"{d.Username} is following you - can't follow them back."
                : followingOther ? $"{d.Username} is following {d.Following} - can't follow a butler (would make a conga line)."
                : $"{d.Username} is on a different server ({d.Server}) - can't follow across servers.";
        }
        else
        {
            followButton.ToolTip = amFollowing
                ? "Stop following this account."
                : $"Make this account ({_plugin.OwnUsername ?? "me"}) follow {d.Username}.";
        }
        Grid.SetColumn(followButton, 1);
        grid.Children.Add(followButton);

        // Summon/Release. An account already following me MANUALLY (but not summoned)
        // gets a disabled Summon - you can't summon and manual-follow at once, which
        // is what produced the funky dual-state. Stop a manual follow on its own client.
        Button summonButton = followsMe
            ? MakeButton("Release", OrangeButtonBrush, (_, _) => { _plugin.ToggleFollower(d.Username); Refresh(force: true); })
            : MakeButton("Summon", GreenBrush, (_, _) => { _plugin.ToggleFollower(d.Username); Refresh(force: true); });
        summonButton.Margin = new Thickness(6, 0, 0, 0);
        summonButton.VerticalAlignment = VerticalAlignment.Center;

        if (followsMe)
        {
            summonButton.ToolTip = $"Stop summoning {d.Username}; they park in their house.";
        }
        else if (iAmButler)
        {
            // I'm a butler myself - can't summon others (prevents conga lines).
            summonButton.IsEnabled = false;
            summonButton.ToolTip = $"You're following {_plugin.FollowingWho} - stop that before summoning anyone.";
        }
        else if (amFollowing)
        {
            // I'm following them - summoning them would make us chase each other.
            summonButton.IsEnabled = false;
            summonButton.ToolTip = $"You're following {d.Username} - stop first before summoning them.";
        }
        else if (followingMe)
        {
            summonButton.IsEnabled = false;
            summonButton.ToolTip = $"{d.Username} is already following you manually - can't also summon them.";
        }
        else if (followingOther)
        {
            // Already serving another master - summoning would contest it and risk a chain.
            summonButton.IsEnabled = false;
            summonButton.ToolTip = $"{d.Username} is already following {d.Following} - they must be released from that first.";
        }
        else if (differentServer)
        {
            // Can't reach across game worlds. (An existing summon stays enabled above as
            // "Release" so you can still call a parked-on-another-server butler home.)
            summonButton.IsEnabled = false;
            summonButton.ToolTip = $"{d.Username} is on a different server ({d.Server}) - can't summon across servers.";
        }
        else if (d.ScriptOn)
        {
            summonButton.IsEnabled = false;
            summonButton.ToolTip = $"{d.Username} is running a script - can't be summoned.";
        }
        else if (d.Followers.Count > 0)
        {
            // Target is itself a master - summoning it would make it butler+master
            // (the other way a conga line forms). Release its followers first.
            summonButton.IsEnabled = false;
            summonButton.ToolTip = $"{d.Username} has its own summoned butlers - it must release them before it can be summoned.";
        }
        else if (d.Parking)
        {
            summonButton.IsEnabled = false;
            summonButton.ToolTip = $"{d.Username} is parking (walking home) - summonable once it settles.";
        }
        else
        {
            summonButton.ToolTip = $"Order {d.Username} to follow me (their plugin obeys automatically).";
        }
        Grid.SetColumn(summonButton, 2);
        grid.Children.Add(summonButton);

        // Transparent row on the contrasting list panel; hairline separators divide rows.
        return new Border
        {
            Padding = new Thickness(8, 9, 8, 9),
            Child = grid,
        };
    }

    private TextBlock MakeText(double size, string themeKey, Brush fallback)
    {
        TextBlock text = new() { FontSize = size };
        SetThemeBrush(text, TextBlock.ForegroundProperty, themeKey, fallback);
        return text;
    }

    private static Border MakeChip(string text, Brush color)
    {
        return new Border
        {
            BorderBrush = color,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 1, 7, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 10, Foreground = color },
        };
    }

    /// <summary>
    /// Buttons use Skua's Material Design styles when available: raised (theme accent
    /// color), raised with a semantic color override, or outlined for low-emphasis
    /// actions. Falls back to a simple flat template outside Skua.
    /// </summary>
    private Button MakeButton(string text, Brush? colorOverride, RoutedEventHandler onClick, bool outlined = false)
    {
        Button button = new()
        {
            Content = text,
            FontSize = 12,
            Cursor = Cursors.Hand,
            Padding = new Thickness(12, 3, 12, 3),
            Height = 28,
        };

        string styleKey = outlined ? "MaterialDesignOutlinedButton" : "MaterialDesignRaisedButton";
        if (Application.Current?.TryFindResource(styleKey) is Style style)
        {
            button.Style = style;
            if (colorOverride != null)
            {
                button.Background = colorOverride;
                button.BorderBrush = colorOverride;
                button.Foreground = Brushes.White;
            }
        }
        else
        {
            ApplyFallbackTemplate(button, colorOverride ?? (outlined ? Hex("#3A3A44") : FallbackAccent));
        }

        button.Click += onClick;
        return button;
    }

    private static void ApplyFallbackTemplate(Button button, Brush background)
    {
        button.Background = background;
        button.Foreground = Brushes.White;
        button.BorderThickness = new Thickness(0);

        FrameworkElementFactory border = new(typeof(Border));
        border.Name = "border";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));

        FrameworkElementFactory content = new(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        ControlTemplate template = new(typeof(Button)) { VisualTree = border };
        Trigger hover = new() { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(OpacityProperty, 0.82, "border"));
        template.Triggers.Add(hover);
        button.Template = template;
    }

    /// <summary>
    /// Plain content template that still contains the three named buttons (collapsed)
    /// CustomWindow's code-behind wires up, so it cannot null-crash. The window keeps
    /// normal OS chrome in this mode.
    /// </summary>
    private static ControlTemplate BuildSafetyTemplate()
    {
        FrameworkElementFactory root = new(typeof(Grid));

        foreach (string part in new[] { "PART_Close", "PART_Maximize", "PART_Minimize" })
        {
            FrameworkElementFactory button = new(typeof(Button), part);
            button.SetValue(VisibilityProperty, Visibility.Collapsed);
            root.AppendChild(button);
        }

        FrameworkElementFactory adorner = new(typeof(System.Windows.Documents.AdornerDecorator));
        FrameworkElementFactory presenter = new(typeof(ContentPresenter));
        adorner.AppendChild(presenter);
        root.AppendChild(adorner);

        return new ControlTemplate(typeof(CustomWindow)) { VisualTree = root };
    }

    /// <summary>Use a live theme resource when Skua provides it; otherwise a fixed fallback.</summary>
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
