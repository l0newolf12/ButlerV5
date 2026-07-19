using Skua.Core.Interfaces;

namespace ButlerV5;

/// <summary>
/// Writes this account's {username}_butlerv5 file. Skua's MapChanged/CellChanged
/// events only fire while a script is running (confirmed by testing - a plugin alone
/// never receives them), so a 500ms watcher polls the game state instead. Disk writes
/// still only happen when the content actually changed; the poll just detects it.
/// </summary>
public class Broadcaster
{
    private readonly IScriptInterface _bot;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _questClone;
    private readonly Action<string> _log;
    private DateTime _lastQuestPublish = DateTime.MinValue;
    private DateTime _lastAttackingSeen = DateTime.MinValue;
    private volatile bool _publishing;
    private readonly object _writeLock = new();

    private CancellationTokenSource? _cts;
    private Task? _watcher;

    private string _lastUsername = "";
    private string _lastWritten = "";
    private string _lastKnownRoom = "1";
    private volatile string _lastServer = "";

    /// <summary>This account's current server name (cached from the last broadcast).</summary>
    public string CurrentServer => _lastServer;
    private bool _wasLoggedIn;
    private bool _wasEnabled = true;

    private List<string> _followers = new();

    private readonly Func<string?> _followingProvider;
    private readonly Func<string?> _scriptNameProvider; // null = no script running
    private readonly Func<string?> _declinedProvider;
    private readonly Func<bool> _parkingProvider;

    public Broadcaster(IScriptInterface bot, Func<bool> enabled, Func<bool> questClone,
        Func<string?> followingProvider, Func<string?> scriptNameProvider,
        Func<string?> declinedProvider, Func<bool> parkingProvider, Action<string> log)
    {
        _bot = bot;
        _enabled = enabled;
        _questClone = questClone;
        _followingProvider = followingProvider;
        _scriptNameProvider = scriptNameProvider;
        _declinedProvider = declinedProvider;
        _parkingProvider = parkingProvider;
        _log = log;
    }

    /// <summary>Usernames this account (as master) has ordered to follow it.</summary>
    public IReadOnlyList<string> Followers
    {
        get { lock (_writeLock) return _followers.ToList(); }
    }

    public void SetFollowers(IEnumerable<string> followers)
    {
        lock (_writeLock)
            _followers = followers.Select(f => f.Trim()).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        WriteNow();
    }

    public void Start()
    {
        DebugLog.Log("Broadcaster", "Start: polling watcher + Logout event");
        _cts = new CancellationTokenSource();
        _bot.Events.Logout += OnLogout;

        CancellationToken token = _cts.Token;
        _watcher = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    bool enabled = _enabled();
                    if (enabled != _wasEnabled)
                    {
                        _wasEnabled = enabled;
                        DebugLog.Log("Broadcaster", $"ButlerV5 enabled -> {enabled}");
                        if (!enabled)
                            WriteOffline(); // master switch off: mark offline, then go idle
                    }

                    // Master switch off: write nothing at all (no sync file, no quest
                    // publish) until it's turned back on.
                    if (!enabled)
                    {
                        token.WaitHandle.WaitOne(250);
                        continue;
                    }

                    bool loggedIn = _bot.Player?.LoggedIn == true;
                    if (loggedIn != _wasLoggedIn)
                    {
                        _wasLoggedIn = loggedIn;
                        DebugLog.Log("Broadcaster", $"login state flipped -> {(loggedIn ? "logged in as " + _bot.Player?.Username : "logged out")}");
                        if (!loggedIn)
                            WriteOffline();
                    }

                    if (loggedIn)
                    {
                        WriteNow(); // no-op unless something actually changed

                        // EXPERIMENTAL quest-state publishing: at login, then every 2
                        // minutes. Runs on a background task - the 800 flash-call read
                        // can take 5-20s and must never stall the sync-file writes.
                        if (_questClone() &&
                            !_publishing &&
                            _bot.Map?.Loaded == true &&
                            (DateTime.UtcNow - _lastQuestPublish).TotalSeconds >= 120)
                        {
                            _lastQuestPublish = DateTime.UtcNow;
                            _publishing = true;
                            string publishUser = _bot.Player!.Username;
                            Task.Run(() =>
                            {
                                try { QuestClone.Publish(_bot, publishUser); }
                                finally { _publishing = false; }
                            });
                        }
                    }
                }
                catch
                {
                }
                token.WaitHandle.WaitOne(250);
            }
        }, token);
    }

    public void Stop()
    {
        _bot.Events.Logout -= OnLogout;
        _cts?.Cancel();
        try { _watcher?.Wait(2000); } catch { }
        _cts?.Dispose();
        _cts = null;
    }

    private void OnLogout()
    {
        DebugLog.Log("Broadcaster", "Logout event");
        WriteOffline();
    }

    public void WriteNow()
    {
        try
        {
            if (!_enabled())
                return;

            IScriptPlayer? player = _bot.Player;
            if (player?.LoggedIn != true || string.IsNullOrEmpty(player.Username))
                return;

            // Mid-transition the map reports unloaded or a bogus name ("-1"); keep the
            // previous file contents until the new map has settled.
            IScriptMap? map = _bot.Map;
            string mapName = map?.Name?.ToLowerInvariant() ?? "";
            if (map?.Loaded != true || string.IsNullOrEmpty(mapName) || mapName == "-1")
                return;

            // Hysteresis: report attacking immediately (passive followers engage fast),
            // but only report NOT attacking after ~4s of calm. Farming is fight-pause-
            // fight with 1-3s gaps between pulls; a short window flipped attacking (and
            // rewrote the file) on every gap. 4s merges a farming streak into one
            // continuous "attacking", so it only drops on a genuine sustained stop.
            bool rawAttacking = player.InCombat || player.HasTarget;
            if (rawAttacking)
                _lastAttackingSeen = DateTime.UtcNow;
            bool attacking = rawAttacking || (DateTime.UtcNow - _lastAttackingSeen).TotalMilliseconds < 4000;

            string server = GameServer.CurrentName(_bot); // server NAME, not IP (sock7 is shared)
            _lastServer = server;

            lock (_writeLock)
            {
                _lastUsername = player.Username;

                SyncData data = new()
                {
                    Username = player.Username,
                    Pid = Environment.ProcessId,
                    Server = server,
                    Map = mapName,
                    Room = ResolveRoom(),
                    Cell = player.Cell ?? "Enter",
                    Pad = player.Pad ?? "Spawn",
                    LoggedIn = true,
                    Attacking = attacking,
                    Followers = _followers.ToList(),
                    Following = SafeGet(_followingProvider) ?? "",
                    ScriptOn = SafeGet(_scriptNameProvider) != null,
                    ScriptName = SafeGet(_scriptNameProvider) ?? "",
                    Declined = SafeGet(_declinedProvider) ?? "",
                    Parking = SafeGetBool(_parkingProvider),
                };

                WriteIfChanged(data);
            }
        }
        catch
        {
        }
    }

    /// <summary>Written on logout, in-client shutdown (ProcessExit) and plugin unload.</summary>
    public void WriteOffline()
    {
        try
        {
            lock (_writeLock)
            {
                if (string.IsNullOrEmpty(_lastUsername))
                {
                    DebugLog.Log("Broadcaster", "offline write skipped: never logged in this session");
                    return;
                }

                SyncData data = new()
                {
                    Username = _lastUsername,
                    Pid = Environment.ProcessId,
                    LoggedIn = false,
                    Followers = _followers.ToList(),
                };

                WriteIfChanged(data);
            }
        }
        catch
        {
        }
    }

    private static string? SafeGet(Func<string?> provider)
    {
        try { return provider(); }
        catch { return null; }
    }

    private static bool SafeGetBool(Func<bool> provider)
    {
        try { return provider(); }
        catch { return false; }
    }

    private void WriteIfChanged(SyncData data)
    {
        string serialized = data.Serialize();
        if (serialized == _lastWritten)
            return;

        if (SyncFile.Write(data))
        {
            _lastWritten = serialized;
            DebugLog.Log("Broadcaster", $"wrote: {DebugLog.Describe(data)}");
        }
        else
        {
            DebugLog.Log("Broadcaster", $"WRITE FAILED: {DebugLog.Describe(data)}");
        }
    }

    /// <summary>
    /// The joinable room number isn't a direct property; Map.FullName reads the room
    /// title from the game UI (e.g. "ultradage-100000"). Falls back to the last room
    /// we managed to read, so a mid-transition write doesn't corrupt the value.
    /// </summary>
    private string ResolveRoom()
    {
        try
        {
            string fullName = _bot.Map?.FullName ?? "";
            int dash = fullName.LastIndexOf('-');
            if (dash > 0 && dash < fullName.Length - 1)
            {
                string room = fullName[(dash + 1)..];
                if (room.All(char.IsDigit))
                {
                    _lastKnownRoom = room;
                    return room;
                }
            }
        }
        catch
        {
        }
        return _lastKnownRoom;
    }
}
