namespace ButlerV5;

/// <summary>Who is currently using the plugin, based on the shared sync-file folder.</summary>
public static class Roster
{
    /// <summary>
    /// All accounts whose file says loggedin=1 and whose Skua process is still alive,
    /// excluding the given username (normally our own account).
    /// </summary>
    public static List<SyncData> GetOnline(string? exceptUsername = null)
    {
        return SyncFile.ReadAll()
            .Where(SyncFile.IsOnline)
            .Where(d => exceptUsername == null || !d.Username.Equals(exceptUsername, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The online master (if any) whose followers list contains the given username.</summary>
    public static SyncData? FindMasterOrdering(string username)
        => FindMasterOrdering(username, GetOnline(username));

    /// <summary>
    /// Overload that reuses an already-fetched online snapshot, so the order watcher
    /// can read every sync file once per tick instead of 2-3 times.
    /// </summary>
    public static SyncData? FindMasterOrdering(string username, IEnumerable<SyncData> online)
        => online.FirstOrDefault(d =>
            d.Followers.Any(f => f.Equals(username, StringComparison.OrdinalIgnoreCase)));
}
