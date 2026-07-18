using System.IO;

namespace ButlerV5;

/// <summary>
/// Watches a folder and raises <see cref="Changed"/> (debounced) when any matching
/// file is written. Lets consumers react to sync-file changes instead of polling.
/// Throttle-trailing debounce: fires at most once per window, coalesces bursts, and
/// never starves under continuous writes.
/// </summary>
public sealed class SyncWatcher : IDisposable
{
    private readonly FileSystemWatcher _fsw;
    private readonly System.Timers.Timer _debounce;
    private int _pending;

    /// <summary>Raised on the debounce timer's thread after a burst of file changes settles.</summary>
    public event Action? Changed;

    public SyncWatcher(string directory, string filter, double debounceMs = 150)
    {
        Directory.CreateDirectory(directory);

        _debounce = new System.Timers.Timer(debounceMs) { AutoReset = false };
        _debounce.Elapsed += (_, _) =>
        {
            // Reset the gate BEFORE firing, so writes arriving during the handler
            // schedule a fresh fire (no missed events, no starvation).
            Interlocked.Exchange(ref _pending, 0);
            try { Changed?.Invoke(); } catch { }
        };

        _fsw = new FileSystemWatcher(directory, filter)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            IncludeSubdirectories = false,
            InternalBufferSize = 64 * 1024, // headroom against overflow under churn
        };
        _fsw.Changed += OnEvent;
        _fsw.Created += OnEvent;
        _fsw.Deleted += OnEvent;
        _fsw.Renamed += OnEvent;
        _fsw.Error += OnError;
        _fsw.EnableRaisingEvents = true;

        DebugLog.Log("SyncWatcher", $"watching {directory} filter {filter}");
    }

    private void OnEvent(object sender, FileSystemEventArgs e) => Schedule();

    // Buffer overflow (too many changes at once) — force a re-evaluation.
    private void OnError(object sender, ErrorEventArgs e) => Schedule();

    private void Schedule()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 0)
            _debounce.Start();
    }

    public void Dispose()
    {
        try { _fsw.EnableRaisingEvents = false; } catch { }
        try { _fsw.Dispose(); } catch { }
        try { _debounce.Dispose(); } catch { }
    }
}
