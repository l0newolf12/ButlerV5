using System.ComponentModel;
using System.IO;

namespace ButlerV5;

public enum LogLevel { Info, Success, Warning, Error }

/// <summary>One log entry: a formatted line plus a severity that drives its color.</summary>
public class LogLine : INotifyPropertyChanged
{
    public long Seq { get; init; }
    public LogLevel Level { get; init; }

    private string _text = "";
    public string Text
    {
        get => _text;
        set { if (_text != value) { _text = value; PropertyChanged?.Invoke(this, TextChangedArgs); } }
    }

    private static readonly PropertyChangedEventArgs TextChangedArgs = new(nameof(Text));
    public event PropertyChangedEventHandler? PropertyChanged;

    public LogLine Copy() => new() { Seq = Seq, Level = Level, Text = _text };
}

/// <summary>
/// Verbose diagnostics. Every entry goes into a capped, deduplicated in-memory ring
/// buffer (for the in-plugin log viewer, copy and export), and - when auto-save is on -
/// is also appended to %APPDATA%\Skua\butlerv5_logs\debug_pid{pid}.log.
/// </summary>
public static class DebugLog
{
    private static readonly object _fileLock = new();
    private static readonly object _memLock = new();
    private static readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Skua",
        "butlerv5_logs");
    private static readonly string _path = Path.Combine(_dir, $"debug_pid{Environment.ProcessId}.log");

    private const int MaxMem = 2000;
    private static readonly List<LogLine> _mem = new();
    private static long _seq;
    private static string? _lastNorm;
    private static long _lastSeq = -1;
    private static int _lastCount;

    public static string LogPath => _path;
    public static string LogDir => _dir;

    /// <summary>Master switch (debugLogs option): gates all logging.</summary>
    public static Func<bool> EnabledProvider = () => true;

    /// <summary>Auto-save option: when true, entries are also appended to the log file.</summary>
    public static Func<bool> AutoSaveProvider = () => true;

    public static void Log(string component, string message, LogLevel level = LogLevel.Info)
    {
        try
        {
            if (!EnabledProvider())
                return;

            string ts = DateTime.Now.ToString("HH:mm:ss.fff");
            string norm = $"[{component}] {message}";
            string line = $"{ts} {norm}";

            if (AutoSaveProvider())
            {
                lock (_fileLock)
                {
                    Directory.CreateDirectory(_dir);
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
            }

            lock (_memLock)
            {
                // Dedup: a repeat of the previous message rewrites the last entry with a
                // (xN) counter instead of adding a duplicate line.
                if (norm == _lastNorm && _mem.Count > 0 && _mem[^1].Seq == _lastSeq)
                {
                    _lastCount++;
                    _mem[^1].Text = $"{ts} {norm} (x{_lastCount})";
                }
                else
                {
                    _lastNorm = norm;
                    _lastCount = 1;
                    LogLine entry = new() { Seq = ++_seq, Level = level, Text = line };
                    _lastSeq = entry.Seq;
                    _mem.Add(entry);
                    while (_mem.Count > MaxMem)
                        _mem.RemoveAt(0);
                }
            }
        }
        catch
        {
        }
    }

    /// <summary>A copy of the current in-memory buffer (safe to read on the UI thread).</summary>
    public static List<LogLine> Snapshot()
    {
        lock (_memLock)
            return _mem.Select(e => e.Copy()).ToList();
    }

    public static void ClearMemory()
    {
        lock (_memLock)
        {
            _mem.Clear();
            _lastNorm = null;
            _lastSeq = -1;
            _lastCount = 0;
        }
    }

    /// <summary>Exports the current in-memory log to a timestamped file; returns its path.</summary>
    public static string? SaveExport(string? username)
    {
        try
        {
            List<LogLine> snap = Snapshot();
            Directory.CreateDirectory(_dir);
            string who = string.IsNullOrEmpty(username) ? $"pid{Environment.ProcessId}" : username;
            string path = Path.Combine(_dir, $"export_{who}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            File.WriteAllText(path, string.Join(Environment.NewLine, snap.Select(e => e.Text)));
            return path;
        }
        catch
        {
            return null;
        }
    }

    public static string Describe(SyncData? d) =>
        d == null
            ? "(no file)"
            : $"user={d.Username} pid={d.Pid} loggedin={(d.LoggedIn ? 1 : 0)} map={d.MapWithRoom} " +
              $"cell={d.Cell} pad={d.Pad} attacking={(d.Attacking ? 1 : 0)} offgoto={(d.OffGoto ? 1 : 0)} server={d.Server} " +
              $"followers=[{string.Join(",", d.Followers)}]";
}
