using System.IO;
using System.Windows;
using Skua.Core.Interfaces;
using Skua.Core.Options;

namespace ButlerV5;

public class ButlerV5Plugin : ISkuaPlugin
{
    private const string MenuButtonText = "Butler V5";

    public string Name => "Butler V5";
    public string Author => "l0newolf";
    public string Description =>
        "Every account with this plugin broadcasts its location to a shared file. " +
        "Open the Butler V5 window to see who's online, follow someone, or order your alts to follow you.";

    public List<IOption>? Options { get; } = new()
    {
        new Option<bool>("broadcast", "Enable broadcasting",
            "Write this account's location file so other ButlerV5 accounts can see and follow it.", true),
        new Option<bool>("passiveAttack", "Only attack when master attacks",
            "While following: only fight while the master is fighting.\n" +
            "Off = always attack, like Butler v3 did. Default: Off.", false),
        new Option<bool>("antiLagSummoned", "AntiLag while summoned",
            "Turn on Skua's AntiLag (Lag Killer, animation cuts, hidden monsters) while\n" +
            "this account is summoned by a master. Great for background alts when you're\n" +
            "running several at once. Note: it hides monsters and animations in this\n" +
            "client until the follow ends. Default: On.",
            true),
        new Option<bool>("debugLogs", "Enable debug logs",
            "Record diagnostics into the in-plugin log viewer (Settings window). Turn off\n" +
            "to stop all logging entirely. Default: On.", true),
        new Option<bool>("autoSaveLog", "Auto-save logs to file",
            "Also write every log line to a file in %APPDATA%\\Skua\\butlerv5_logs as it\n" +
            "happens (one file per account). Off = logs live only in the viewer until you\n" +
            "click Save. This is a global option - applies to all accounts. Default: Off.", false),
        new Option<int>("gotoDelay", "Goto delay (milliseconds)",
            "Milliseconds between goto attempts while chasing the master. Default: 250\n" +
            "(= one loop tick, so goto fires every tick like Butler v3 - as fast as it gets).\n" +
            "Raise it to chase more gently (fewer goto packets); too high and the butler\n" +
            "lags behind you on map changes. Below ~250 does nothing - the loop is the floor.",
            250),
        new Option<ButlerClassType>("classType", "Class type",
            "Which CoreBots class this butler equips when it starts following:\n" +
            "Farm / Solo / Dodge / Boss as selected in CoreBots options, or None to\n" +
            "keep current gear. Saved per account - after login, each account shows\n" +
            "and keeps its own choice. Default: None.",
            ButlerClassType.None),
        new Option<ParkSpot>("parkLocation", "Park location",
            "Where a butler goes when it's released or its master goes offline. Default: House.\n" +
            "House = its own house (safest, quietest). Whitemap = the classic empty map.\n" +
            "Stay = don't move at all - careful, Stay can leave a butler standing\n" +
            "in a boss room eating hits after you release it.",
            ParkSpot.House),
        new Option<bool>("obeySummons", "Obey summons",
            "If off, this account ignores every Summon / 'Follow me' order from your\n" +
            "other accounts. Turn this OFF on the account you play by hand, so a\n" +
            "misclick in another client can never yank it across the map mid-fight.\n" +
            "Default: On.",
            true),
        new Option<bool>("leechMode", "Leech mode (wait at Enter)",
            "While following, stay in the master's room but wait at the Enter cell\n" +
            "instead of fighting beside them. Good for dragging a low-level alt through\n" +
            "content without it dying every pull. The butler does NOT attack at all\n" +
            "in this mode - don't expect damage from it. Default: Off.",
            false),
        new Option<int>("rescueThreshold", "Failed gotos before rescue",
            "How many goto attempts without reaching the master before the butler joins\n" +
            "the master's exact map-room from the sync file (how it enters locked maps).\n" +
            "Default: 3. Lower reacts faster but can false-trigger on normal map-change\n" +
            "lag; don't set it too high or it will wait forever before rescuing itself.",
            3),
        new Option<int>("rosterRefresh", "Butler window refresh (seconds)",
            "How often the Butler V5 window re-reads everyone's status files. Default: 2.\n" +
            "Lower feels snappier but reads the disk more; don't set it too high\n" +
            "or the list and incoming Summon orders will feel laggy.",
            2),
        new Option<bool>("questBypass", "Quest bypasses (UpdateQuest)",
            "When the butler lands on a map with quest-locked cells or mobs, fake the\n" +
            "gate quest CLIENT-SIDE so the cell opens and mobs are targetable - only\n" +
            "on maps in the built-in table, never as a blanket at startup. The server\n" +
            "is never told anything. Default: On.",
            true),
        new Option<string>("questBypassCustom", "Custom bypass quest IDs",
            "Extra quest IDs to fake when the follow starts, separated by commas\n" +
            "(example: 1234, 5678). Use this when a map's cells stay locked for\n" +
            "butlers and it isn't in the built-in table yet. Empty = none.",
            ""),
        new Option<bool>("questClone", "EXPERIMENTAL: clone master quest state",
            "While following, copy the master's quest progress CLIENT-SIDE so every\n" +
            "quest-locked cell/mob the master can see, the butler can see too - no\n" +
            "outdated bypass list. Both sides must have this enabled. Fast: one game\n" +
            "read per sync, timing shown in the debug log. Default: On.",
            true),
        new Option<bool>("fakeLevel", "Fake level 100 (client-side)",
            "Shows this client as level 100 so level-gated maps (e.g. icestormunder)\n" +
            "let the butler in. Purely visual/client-side, but your character will\n" +
            "display as level 100 to yourself until relog. Default: Off.",
            false),
        new Option<bool>("instantWarnings", "Instant warning detection",
            "Reads the raw game packet stream to catch 'Locked zone' / 'room is full' /\n" +
            "'ignoring goto' warnings the moment they arrive - near-instant locked-map\n" +
            "rescue, same detection Butler v3 used. The failed-goto counter still runs\n" +
            "underneath as a fallback. Turn off if following misbehaves. Default: On.",
            true),
        new Option<bool>("antiLagFollowing", "AntiLag while following",
            "Same as 'AntiLag while summoned', but only while MANUALLY following someone\n" +
            "(you clicked Follow in this client's window), not when summoned. Default: Off.",
            false),
    };

    private IScriptInterface? _bot;
    private IPluginHelper? _helper;
    private IPluginContainer? _container;
    private IScriptManager? _scriptManager;
    private Broadcaster? _broadcaster;
    private Follower? _follower;
    private RosterWindow? _window;
    private SettingsWindow? _settingsWindow;

    private CancellationTokenSource? _orderWatcherCts;
    private Task? _orderWatcher;
    private DateTime _orderGoneSince = DateTime.MinValue;
    private bool _busyOrderLogged;
    private string? _declinedMaster;
    private EventHandler? _processExitHandler;

    private SyncWatcher? _syncWatcher;
    private readonly AutoResetEvent _orderSignal = new(false);
    private const int OrderFallbackMs = 3000; // safety-net poll; file changes wake it instantly

    /// <summary>Raised (off-thread) when any account's sync file changes; windows refresh on it.</summary>
    public event Action? SyncFilesChanged;

    public string? OwnUsername => _bot?.Player?.LoggedIn == true ? _bot.Player.Username : null;
    public string MyServerName => _broadcaster?.CurrentServer ?? "";
    public IReadOnlyList<string> CurrentFollowers => _broadcaster?.Followers ?? new List<string>();
    public string? FollowingWho => _follower?.Master;
    public FollowSource FollowingHow => _follower?.Source ?? FollowSource.None;

    public bool IsScriptRunning => _scriptManager?.ScriptRunning == true;

    public string RunningScriptName
    {
        get
        {
            if (!IsScriptRunning)
                return "";
            string name = Path.GetFileName(_scriptManager?.LoadedScript ?? "");
            return string.IsNullOrEmpty(name) ? "script" : name;
        }
    }

    public void Load(IServiceProvider provider, IPluginHelper helper)
    {
        _helper = helper;
        _bot = provider.GetService(typeof(IScriptInterface)) as IScriptInterface ?? IScriptInterface.Instance;

        IPluginManager? manager = provider.GetService(typeof(IPluginManager)) as IPluginManager;
        _container = manager?.GetContainer(this);
        _scriptManager = provider.GetService(typeof(IScriptManager)) as IScriptManager;

        DebugLog.EnabledProvider = () => GetOption("debugLogs", true);
        DebugLog.AutoSaveProvider = () => GetOption("autoSaveLog", false);
        DebugLog.Log("Plugin", $"Load: pid={Environment.ProcessId} container={( _container != null ? "ok" : "NULL")} bot={( _bot != null ? "ok" : "NULL")}");

        _broadcaster = new Broadcaster(_bot!,
            () => GetOption("broadcast", true),
            () => GetOption("questClone", true),
            // Only report "following" while actively following. Parked (master offline
            // or on a different server) keeps the order but idles at home, so the master
            // sees a "summoned" chip instead of "following me".
            () => (_follower?.IsRunning == true && _follower.IsParked == false) ? _follower.Master : null,
            () => IsScriptRunning ? RunningScriptName : null,
            () => _declinedMaster,
            () => _follower?.IsParking == true,
            Log);
        _follower = new Follower(_bot!, new FollowerSettings
        {
            PassiveAttack = () => GetOption("passiveAttack", false),
            GotoDelayMs = () => GetOptionInt("gotoDelay", 250),
            RescueThreshold = () => GetOptionInt("rescueThreshold", 3),
            LeechMode = () => GetOption("leechMode", false),
            Park = () => GetOptionEnum("parkLocation", ParkSpot.House),
            InstantWarnings = () => GetOption("instantWarnings", false),
            QuestBypasses = () => GetOption("questBypass", true),
            CustomBypassIds = () => GetOptionString("questBypassCustom", ""),
            LevelFake = () => GetOption("fakeLevel", false),
            QuestClone = () => GetOption("questClone", true),
            ScriptRunning = () => IsScriptRunning,
            AntiLagSummoned = () => GetOption("antiLagSummoned", true),
            AntiLagFollowing = () => GetOption("antiLagFollowing", false),
            ScriptSuspendNotice = ShowSuspendNotice,
        }, Log);

        // React to sync-file changes instead of polling: the order watcher wakes
        // instantly on a change, and roster windows refresh. A slow fallback still
        // runs as a safety net for any events the OS coalesces or drops.
        try
        {
            _syncWatcher = new SyncWatcher(SyncFile.Directory, $"*{SyncFile.Suffix}");
            _syncWatcher.Changed += OnSyncFilesChanged;
        }
        catch (Exception ex)
        {
            DebugLog.Log("Plugin", $"SyncWatcher failed ({ex.Message}) - falling back to polling only");
        }

        _broadcaster.Start();
        StartOrderWatcher();

        // Skua never calls Unload() on app exit (verified in 1.4.3.0 PluginManager), so
        // hook process exit ourselves to leave a loggedin=0 file behind on normal close.
        _processExitHandler = (_, _) => _broadcaster?.WriteOffline();
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;

        helper.AddMenuButton(MenuButtonText, OpenWindow);
        Log("Loaded. Broadcasting is " + (GetOption("broadcast", true) ? "on" : "off") + ".");
    }

    public void Unload()
    {
        try
        {
            _helper?.RemoveMenuButton(MenuButtonText);

            if (_syncWatcher != null)
            {
                _syncWatcher.Changed -= OnSyncFilesChanged;
                _syncWatcher.Dispose();
                _syncWatcher = null;
            }

            _orderWatcherCts?.Cancel();
            _orderSignal.Set(); // wake the order watcher so it exits its wait promptly
            try { _orderWatcher?.Wait(2000); } catch { }
            _orderWatcherCts?.Dispose();
            _orderWatcherCts = null;

            _follower?.Stop();
            _broadcaster?.WriteOffline();
            _broadcaster?.Stop();

            if (_processExitHandler != null)
                AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;

            Application.Current?.Dispatcher.Invoke(() =>
            {
                _window?.Close();
                _window = null;
                _settingsWindow?.Close();
                _settingsWindow = null;
            });
        }
        catch
        {
        }
    }

    /// <summary>Warning box when a script suspends an active follow (user's request).</summary>
    private void ShowSuspendNotice(string master)
    {
        try
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                MessageBoxResult result = MessageBox.Show(
                    $"A script started while this account was following {master}.\n\n" +
                    "Following is now SUSPENDED and will resume automatically when the script stops.\n\n" +
                    "OK - keep it suspended (resume after the script)\n" +
                    "Cancel - stop following entirely",
                    "Butler V5",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Cancel)
                    StopFollow();
            });
        }
        catch
        {
        }
    }

    public void StartFollow(string master, FollowSource source)
    {
        DebugLog.Log("UI", $"StartFollow({master}, {source})");

        if (IsScriptRunning)
        {
            Log($"Can't start following {master}: a script is running ({RunningScriptName}). Stop it first.");
            return;
        }

        // Can't follow someone who is ordered to follow me - release them first.
        if (CurrentFollowers.Any(f => f.Equals(master, StringComparison.OrdinalIgnoreCase)))
        {
            Log($"Releasing {master} (can't follow an account that follows me).");
            ToggleFollower(master);
        }

        _follower?.Start(master, source);
    }

    public void StopFollow()
    {
        DebugLog.Log("UI", "StopFollow clicked");

        // Stopping an ORDERED follow must also decline the summon, or the order
        // watcher re-obeys the still-listed order within seconds (this was why
        // Cancel on the suspend box didn't stick). The master auto-releases us
        // when it sees the decline.
        if (_follower?.Source == FollowSource.Ordered && _follower.Master is { } orderedMaster)
        {
            _declinedMaster = orderedMaster;
            DebugLog.Log("UI", $"declining {orderedMaster}'s summon (stop on an ordered follow)");
        }

        _follower?.Stop();
    }

    /// <summary>Master side: publish the list of accounts that should follow this one.</summary>
    public void OrderFollowers(IEnumerable<string> usernames)
    {
        _broadcaster?.SetFollowers(usernames);
        Log("Ordered to follow me: " + string.Join(", ", CurrentFollowers));
    }

    /// <summary>Add or remove one account from this master's followers list.</summary>
    public void ToggleFollower(string username)
    {
        List<string> current = CurrentFollowers.ToList();
        int index = current.FindIndex(f => f.Equals(username, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            current.RemoveAt(index);
        }
        else
        {
            // Can't summon anyone while I'm a butler myself (conga guard).
            if (FollowingWho is { } iAmFollowing)
            {
                Log($"Can't summon {username} while following {iAmFollowing} - stop that first.");
                return;
            }
            current.Add(username);
        }

        DebugLog.Log("UI", $"ToggleFollower({username}) -> {(index >= 0 ? "release" : "summon")}, list now [{string.Join(",", current)}]");
        _broadcaster?.SetFollowers(current);
        Log(index >= 0 ? $"Released {username}." : $"Summoned {username} to follow me.");
    }

    /// <summary>Order every online plugin account to follow this one.</summary>
    public void SummonAll()
    {
        DebugLog.Log("UI", "Summon all clicked");

        // I can't summon anyone while I'm a butler myself (conga guard).
        if (FollowingWho is { } iAmFollowing)
        {
            Log($"Can't summon while following {iAmFollowing} - stop that first.");
            return;
        }

        string me = OwnUsername ?? "";
        List<SyncData> roster = Roster.GetOnline(OwnUsername);

        // Skip anyone whose individual Summon button would be greyed out, so "Summon all"
        // never does what the per-row button forbids: busy (script) accounts; anyone
        // already following me manually (dual-state); accounts that are themselves masters
        // (summoning them makes them butler+master, the other way a conga line forms);
        // accounts on a different game world (unreachable); and accounts mid-park.
        string myServer = MyServerName;
        bool AlreadyManual(SyncData d) =>
            d.Following.Equals(me, StringComparison.OrdinalIgnoreCase) &&
            !CurrentFollowers.Any(f => f.Equals(d.Username, StringComparison.OrdinalIgnoreCase));
        bool IsMaster(SyncData d) => d.Followers.Count > 0;
        bool DifferentServer(SyncData d) =>
            !string.IsNullOrEmpty(d.Server) && !string.IsNullOrEmpty(myServer) &&
            !d.Server.Equals(myServer, StringComparison.OrdinalIgnoreCase);
        // In service to some other master already - summoning would contest it / chain.
        bool FollowingAnother(SyncData d) =>
            !string.IsNullOrEmpty(d.Following) && !d.Following.Equals(me, StringComparison.OrdinalIgnoreCase);
        bool Unavailable(SyncData d) =>
            d.ScriptOn || AlreadyManual(d) || IsMaster(d) || DifferentServer(d) || d.Parking || FollowingAnother(d);

        List<string> skipped = roster.Where(Unavailable).Select(d => d.Username).ToList();
        List<string> online = roster.Where(d => !Unavailable(d)).Select(d => d.Username).ToList();

        if (skipped.Count > 0)
            Log("Skipped (script / already following me / following another / is a master / different server / parking): " + string.Join(", ", skipped));
        if (online.Count == 0)
        {
            Log("No available butlers to summon.");
            return;
        }

        // Can't follow someone who's ordered to follow me.
        if (FollowingWho is { } master &&
            online.Any(u => u.Equals(master, StringComparison.OrdinalIgnoreCase)))
        {
            Log($"Stopped following {master} (summoning everyone).");
            _follower?.Stop();
        }

        // Additive: keep everyone already summoned - including a butler parked on another
        // server (its per-row button shows an enabled "Release", not a greyed "Summon", so
        // Summon all must not drop it) - and add the newly-available accounts on top.
        List<string> finalList = CurrentFollowers.Concat(online)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _broadcaster?.SetFollowers(finalList);
        Log("Summoned all: " + string.Join(", ", online.Count > 0 ? online : new[] { "(nobody new available)" }));
    }

    public void ReleaseFollowers()
    {
        DebugLog.Log("UI", "Release all clicked");
        _broadcaster?.SetFollowers(Array.Empty<string>());
        Log("Released all followers.");
    }

    /// <summary>The plugin's option container, for the custom settings page.</summary>
    public IOptionContainer? OptionContainer => _container?.OptionContainer;

    /// <summary>Sets an option and persists it (which the fleet's live-reload picks up).</summary>
    public void SaveSetting(string name, object value)
    {
        try
        {
            IOptionContainer? oc = _container?.OptionContainer;
            if (oc == null)
                return;
            oc.Set(name, value);
            oc.Save();
            DebugLog.Log("UI", $"setting changed: {name} = {value}");
        }
        catch (Exception ex)
        {
            Log($"Could not save setting {name}: {ex.Message}");
        }
    }

    /// <summary>Opens ButlerV5's own settings + log-viewer page.</summary>
    public void OpenSettings()
    {
        DebugLog.Log("UI", "Settings clicked");
        try
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (_settingsWindow == null)
                {
                    _settingsWindow = new SettingsWindow(this);
                    _settingsWindow.Closed += (_, _) => _settingsWindow = null;
                    _settingsWindow.Show();
                }
                else
                {
                    _settingsWindow.Activate();
                }
            });
        }
        catch (Exception ex)
        {
            Log($"Could not open settings: {ex.Message}");
        }
    }

    private void OpenWindow()
    {
        try
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (_window == null)
                {
                    _window = new RosterWindow(this);
                    _window.Closed += (_, _) => _window = null;
                    _window.Show();
                }
                else
                {
                    _window.RefreshRoster();
                    _window.Activate();
                }
            });
        }
        catch (Exception ex)
        {
            Log($"Could not open window: {ex.Message}");
        }
    }

    private void OnSyncFilesChanged()
    {
        _orderSignal.Set();                 // wake the order watcher immediately
        try { SyncFilesChanged?.Invoke(); }  // let open windows refresh
        catch { }
    }

    /// <summary>Wait for a sync-file change, cancellation, or the fallback timeout.</summary>
    private void WaitForOrderTick(CancellationToken token)
    {
        try { WaitHandle.WaitAny(new[] { token.WaitHandle, _orderSignal }, OrderFallbackMs); }
        catch { token.WaitHandle.WaitOne(OrderFallbackMs); }
    }

    /// <summary>
    /// Follower side of "Follow me": watches other accounts' files and obeys when an
    /// online master lists this account. A manual follow always wins over orders.
    /// </summary>
    private void StartOrderWatcher()
    {
        _orderWatcherCts = new CancellationTokenSource();
        CancellationToken token = _orderWatcherCts.Token;

        _orderWatcher = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    string? me = OwnUsername;

                    // Order matters: the class-type bridge must persist any change made
                    // in THIS client's settings window before a reload re-reads the file.
                    if (me != null)
                        SyncClassTypeOption(me);
                    else
                        _classTypePushed = false;

                    CheckOptionsReload();

                    // Read every sync file ONCE per tick and share the snapshot with the
                    // handshake + order-matching steps (was 2-3 ReadAll + pid checks/tick).
                    List<SyncData> online = me != null ? Roster.GetOnline(me) : new();
                    ProcessServingHandshakes(me, online);

                    if (me != null && _follower != null && _follower.Source != FollowSource.Manual)
                    {
                        // "Obey summons" off: never accept orders; drop an active ordered
                        // follow as if the order was revoked.
                        if (!GetOption("obeySummons", true))
                        {
                            if (_follower.Source == FollowSource.Ordered)
                            {
                                DebugLog.Log("OrderWatcher", "obeySummons turned off - dropping ordered follow");
                                _follower.Stop(park: true);
                            }
                            WaitForOrderTick(token);
                            continue;
                        }

                        SyncData? master = Roster.FindMasterOrdering(me, online);

                        // Never obey an account that is in MY followers list - two
                        // accounts summoning each other would follow-loop forever.
                        if (master != null &&
                            CurrentFollowers.Any(f => f.Equals(master.Username, StringComparison.OrdinalIgnoreCase)))
                        {
                            DebugLog.Log("OrderWatcher", $"ignoring order from {master.Username}: they are in my own followers list");
                            master = null;
                        }

                        // A master who ignores goto stays blocked until a manual follow.
                        if (master != null &&
                            master.Username.Equals(_follower.GotoBlockedMaster, StringComparison.OrdinalIgnoreCase))
                        {
                            master = null;
                        }

                        // A summon this account declined stays declined until the
                        // master releases us (list drops our name).
                        if (master != null &&
                            master.Username.Equals(_declinedMaster, StringComparison.OrdinalIgnoreCase))
                        {
                            master = null;
                        }

                        if (master != null)
                        {
                            _orderGoneSince = DateTime.MinValue;
                            if (IsScriptRunning)
                            {
                                // Busy: don't obey while a script runs. The order stays
                                // listed on the master, so we pick it up on script stop.
                                if (!_busyOrderLogged)
                                {
                                    _busyOrderLogged = true;
                                    DebugLog.Log("OrderWatcher", $"order from {master.Username} on hold - script running here");
                                }
                            }
                            else if (!master.Username.Equals(_follower.Master, StringComparison.OrdinalIgnoreCase) || !_follower.IsRunning)
                            {
                                _busyOrderLogged = false;
                                DebugLog.Log("OrderWatcher", $"{master.Username} ordered {me} to follow - obeying");
                                _follower.Start(master.Username, FollowSource.Ordered);
                            }
                        }
                        else if (_follower.Source == FollowSource.Ordered && _follower.Master is { } curMaster)
                        {
                            // Distinguish a real release from a transient bad read:
                            //  - master is online but its file no longer lists me = an
                            //    explicit release -> stop immediately (snappy).
                            //  - master missing/offline/unreadable = possibly a mid-rewrite
                            //    glitch -> require it gone for ~2.5s before parking.
                            SyncData? cur = online.FirstOrDefault(d => d.Username.Equals(curMaster, StringComparison.OrdinalIgnoreCase));
                            bool cleanlyReleased = cur != null &&
                                !cur.Followers.Any(f => f.Equals(me, StringComparison.OrdinalIgnoreCase));

                            if (cleanlyReleased)
                            {
                                _orderGoneSince = DateTime.MinValue;
                                DebugLog.Log("OrderWatcher", $"{curMaster} released me - stopping and parking");
                                _follower.Stop(park: true);
                            }
                            else if (_orderGoneSince == DateTime.MinValue)
                                _orderGoneSince = DateTime.UtcNow;
                            else if ((DateTime.UtcNow - _orderGoneSince).TotalSeconds >= 2.5)
                            {
                                _orderGoneSince = DateTime.MinValue;
                                DebugLog.Log("OrderWatcher", "order gone 2.5s (master offline/unreadable) - stopping and parking");
                                _follower.Stop(park: true);
                            }
                        }
                        else
                        {
                            _orderGoneSince = DateTime.MinValue;
                        }
                    }
                }
                catch
                {
                }
                WaitForOrderTick(token);
            }
        }, token);
    }

    /// <summary>Sets an option's in-memory value without persisting the shared file.</summary>
    private void SetOptionQuiet(string name, string value)
    {
        try
        {
            IOptionContainer? oc = _container?.OptionContainer;
            IOption? option = oc?.Options.FirstOrDefault(o => o.Name == name);
            if (oc != null && option != null)
                oc.OptionValues[option] = value;
        }
        catch (Exception ex)
        {
            DebugLog.Log("Plugin", $"quiet option set failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The decline handshake (butler → master), run every watcher tick: clear my
    /// decline once the master acknowledges it, and auto-release butlers that
    /// declined my summon so the "summoned" chip and stale order both disappear.
    /// </summary>
    private void ProcessServingHandshakes(string? me, List<SyncData> online)
    {
        try
        {
            if (me == null)
                return;

            // Butler side: decline is acknowledged once my master is gone from the
            // online set or its followers list no longer names me.
            if (_declinedMaster != null)
            {
                SyncData? masterFile = online.FirstOrDefault(d => d.Username.Equals(_declinedMaster, StringComparison.OrdinalIgnoreCase));
                if (masterFile == null ||
                    !masterFile.Followers.Any(f => f.Equals(me, StringComparison.OrdinalIgnoreCase)))
                {
                    DebugLog.Log("OrderWatcher", $"decline of {_declinedMaster} acknowledged - cleared");
                    _declinedMaster = null;
                }
            }

            // Master side: release any butler that declined my summon.
            if (CurrentFollowers.Count > 0)
            {
                foreach (SyncData d in online)
                {
                    if (d.Declined.Equals(me, StringComparison.OrdinalIgnoreCase) &&
                        CurrentFollowers.Any(f => f.Equals(d.Username, StringComparison.OrdinalIgnoreCase)))
                    {
                        Log($"{d.Username} declined the summon - released.");
                        ToggleFollower(d.Username);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Log("OrderWatcher", $"handshake processing failed: {ex.Message}");
        }
    }

    private bool GetOption(string name, bool fallback)
    {
        try
        {
            return _container?.OptionContainer?.Get<bool>(name) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private int GetOptionInt(string name, int fallback)
    {
        try
        {
            int value = _container?.OptionContainer?.Get<int>(name) ?? fallback;
            return value > 0 ? value : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private string GetOptionString(string name, string fallback)
    {
        try
        {
            return _container?.OptionContainer?.Get<string>(name) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private T GetOptionEnum<T>(string name, T fallback) where T : struct, Enum
    {
        try
        {
            return _container?.OptionContainer?.Get<T>(name) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public int RosterRefreshSeconds => GetOptionInt("rosterRefresh", 2);

    // ----- per-account class type -----
    // The Class type option lives in the shared options window, but its true store is
    // per-account (AccountConfig): on login the account's saved value is pushed INTO
    // the option so the window shows it; when the user changes it in the window, the
    // new value is saved back for that account only.

    private bool _classTypePushed;
    private ButlerClassType _lastUiClassType;
    private DateTime _lastOptionsFileTime = DateTime.MinValue;

    /// <summary>
    /// Live settings sync: every client watches the shared options file and reloads
    /// when any client saves - change a setting once and the whole fleet picks it up
    /// within a couple of seconds. The per-account class type is re-pushed after each
    /// reload so another account's choice can never clobber this one's.
    /// </summary>
    private void CheckOptionsReload()
    {
        try
        {
            string? path = _container?.OptionsFile;
            if (path == null || !File.Exists(path))
                return;

            DateTime fileTime = File.GetLastWriteTimeUtc(path);
            if (_lastOptionsFileTime == DateTime.MinValue)
            {
                _lastOptionsFileTime = fileTime;
                return;
            }
            if (fileTime <= _lastOptionsFileTime)
                return;
            _lastOptionsFileTime = fileTime;

            _container!.OptionContainer.Load();
            // Restore our per-account class type WITHOUT saving: using Set() here
            // persisted the file, which re-triggered every other client's reload -
            // an endless save/reload ping-pong across the fleet.
            if (_classTypePushed)
                SetOptionQuiet("classType", _lastUiClassType.ToString());

            DebugLog.Log("Plugin", "options file changed on disk - reloaded (live settings sync)");
            Log("Settings reloaded (changed from another client).");
        }
        catch (Exception ex)
        {
            DebugLog.Log("Plugin", $"options reload failed: {ex.Message}");
        }
    }

    private void SyncClassTypeOption(string me)
    {
        try
        {
            if (!_classTypePushed)
            {
                _lastUiClassType = AccountConfig.GetClassType(me);
                SetOptionQuiet("classType", _lastUiClassType.ToString());
                _classTypePushed = true;
                DebugLog.Log("Plugin", $"class type for {me} loaded: {_lastUiClassType}");
                return;
            }

            ButlerClassType ui = GetOptionEnum("classType", _lastUiClassType);
            if (ui != _lastUiClassType)
            {
                _lastUiClassType = ui;
                AccountConfig.SetClassType(me, ui);
                DebugLog.Log("Plugin", $"class type for {me} -> {ui} (changed in options window)");
            }
        }
        catch
        {
        }
    }

    private void Log(string message)
    {
        try
        {
            _bot?.Log($"[ButlerV5] {message}");
        }
        catch
        {
        }
    }
}
