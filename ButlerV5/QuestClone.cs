using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Skua.Core.Interfaces;

namespace ButlerV5;

/// <summary>
/// EXPERIMENTAL quest-state cloning: the master publishes its quest value slots to
/// {username}_butlerv5_quests; a follower applies every slot where the master's value
/// is higher, via the client-side UpdateQuest(value, slot) fake. Whatever the master
/// can see, the follower can see - no per-map table maintenance. The static table in
/// QuestBypass remains the fallback. All logs use the "QuestClone" component so they
/// are clearly distinguishable from the fallback list's "QuestBypass" logs.
/// </summary>
public static class QuestClone
{
    /// <summary>Highest slot index published so far is ~557; headroom for new content.</summary>
    public const int MaxSlots = 800;

    public const string Suffix = "_butlerv5_quests";

    public static string PathFor(string username) =>
        Path.Combine(SyncFile.Directory, $"{username.ToLowerInvariant()}{Suffix}");

    /// <summary>Reads this client's quest value slots, logging how long it took.</summary>
    public static int[]? ReadOwnSlots(IScriptInterface bot)
    {
        try
        {
            Stopwatch sw = Stopwatch.StartNew();
            int[] values = new int[MaxSlots];
            for (int slot = 0; slot < MaxSlots; slot++)
            {
                try
                {
                    values[slot] = bot.Flash.CallGameFunction<int>("world.getQuestValue", slot);
                }
                catch
                {
                    values[slot] = 0;
                }
            }
            sw.Stop();
            DebugLog.Log("QuestClone", $"read {MaxSlots} own quest slots in {sw.ElapsedMilliseconds}ms " +
                                       $"({values.Count(v => v > 0)} non-zero)");
            return values;
        }
        catch (Exception ex)
        {
            DebugLog.Log("QuestClone", $"reading own slots failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Quest string fields in objData: 100 slots per string, 1 base-36 char per slot.</summary>
    private static readonly string[] QuestStringFields =
        { "strQuests", "strQuests2", "strQuests3", "strQuests4", "strQuests5", "strQuests6", "strQuests7", "strQuests8", "strQuests9" };

    /// <summary>
    /// Master side: publish quest state. Fast path: ONE flash call fetches objData and
    /// the strQuests/strQuests2..7 strings are extracted from it - each character is one
    /// quest slot, base-36. The old 800-call read remains only as a fallback. Both the
    /// raw strings and decoded slot=value lines are published, so either apply method
    /// can consume the file.
    /// </summary>
    public static bool Publish(IScriptInterface bot, string username)
    {
        try
        {
            string content;
            Stopwatch sw = Stopwatch.StartNew();

            string? json = null;
            try { json = bot.Flash.GetGameObject("world.myAvatar.objData"); } catch { }
            Dictionary<string, string>? strings = ExtractQuestStrings(json);

            if (strings != null)
            {
                sw.Stop();
                DebugLog.Log("QuestClone", $"read quest strings via objData in {sw.ElapsedMilliseconds}ms (1 flash call, {strings.Count} strings)");

                StringBuilder sb = new();
                foreach ((string field, string value) in strings)
                    sb.Append(field).Append('=').Append(value).Append('\n');
                foreach ((int slot, int value) in DecodeSlots(strings))
                    sb.Append(slot).Append('=').Append(value).Append('\n');
                content = sb.ToString();
            }
            else
            {
                DebugLog.Log("QuestClone", "objData quest strings not found - falling back to per-slot read");
                int[]? values = ReadOwnSlots(bot);
                if (values == null)
                    return false;

                StringBuilder sb = new();
                for (int slot = 0; slot < values.Length; slot++)
                {
                    if (values[slot] > 0)
                        sb.Append(slot).Append('=').Append(values[slot]).Append('\n');
                }
                content = sb.ToString();
            }

            string path = PathFor(username);
            try
            {
                if (File.Exists(path) && File.ReadAllText(path) == content)
                {
                    DebugLog.Log("QuestClone", "publish skipped (quest state unchanged)");
                    return false;
                }
            }
            catch
            {
            }

            Directory.CreateDirectory(SyncFile.Directory);
            File.WriteAllText(path, content);
            DebugLog.Log("QuestClone", $"published quest state ({content.Count(c => c == '\n')} slots) to {Path.GetFileName(path)}");
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Log("QuestClone", $"publish failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Last write time of the master's quest file, for change detection.</summary>
    public static DateTime GetFileTime(string masterUsername)
    {
        try
        {
            string path = PathFor(masterUsername);
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Follower side: clone the master's quest state. Fast path: write the master's
    /// quest strings straight into our own client (7 SetGameObject calls - the map
    /// gates read their values from exactly these strings). Falls back to per-slot
    /// UpdateQuest packets for files without strings.
    /// </summary>
    public static void ApplyFromMaster(IScriptInterface bot, string masterUsername)
    {
        try
        {
            string path = PathFor(masterUsername);
            if (!File.Exists(path))
            {
                DebugLog.Log("QuestClone", $"no quest file from {masterUsername} - is cloning enabled on their client?");
                return;
            }

            List<(string Field, string Value)> strings = new();
            List<(int Slot, int Value)> slots = new();
            foreach (string line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;

                string key = line[..eq];
                string value = line[(eq + 1)..];
                if (key.StartsWith("strQuests", StringComparison.OrdinalIgnoreCase))
                    strings.Add((key, value));
                else if (int.TryParse(key, out int slot) && int.TryParse(value, out int v) &&
                         slot >= 0 && slot < MaxSlots && v > 0)
                    slots.Add((slot, v));
            }

            Stopwatch sw = Stopwatch.StartNew();
            if (strings.Count > 0)
            {
                // Merge-max: a plain overwrite downgraded stronger followers (a strong
                // account following a weak master lost its own boss unlocks for the
                // session). Read our own strings (one flash call) and keep, per slot,
                // whichever value is higher.
                Dictionary<string, string>? own = null;
                try { own = ExtractQuestStrings(bot.Flash.GetGameObject("world.myAvatar.objData")); }
                catch { }
                if (own == null)
                    DebugLog.Log("QuestClone", "could not read own quest strings - applying master's as-is");

                int set = 0;
                foreach ((string field, string masterValue) in strings)
                {
                    string merged = own != null && own.TryGetValue(field, out string? mine)
                        ? MergeMax(mine, masterValue)
                        : masterValue;
                    try
                    {
                        bot.Flash.SetGameObject($"world.myAvatar.objData.{field}", merged);
                        set++;
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Log("QuestClone", $"SetGameObject({field}) failed: {ex.Message}");
                    }
                }
                sw.Stop();
                DebugLog.Log("QuestClone",
                    $"(experimental clone) merged & set {set}/{strings.Count} quest strings from {masterUsername} in {sw.ElapsedMilliseconds}ms (merge-max)");
            }
            else
            {
                int applied = 0;
                foreach ((int slot, int value) in slots)
                {
                    bot.Quests.UpdateQuest(value, slot);
                    applied++;
                }
                sw.Stop();
                DebugLog.Log("QuestClone",
                    $"(experimental clone) applied {applied} slots from {masterUsername} in {sw.ElapsedMilliseconds}ms (blind apply fallback)");
            }
        }
        catch (Exception ex)
        {
            DebugLog.Log("QuestClone", $"apply failed: {ex.Message}");
        }
    }

    private static Dictionary<string, string>? ExtractQuestStrings(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            Dictionary<string, string> result = new();
            foreach (string field in QuestStringFields)
            {
                if (doc.RootElement.TryGetProperty(field, out JsonElement el) &&
                    el.ValueKind == JsonValueKind.String)
                    result[field] = el.GetString() ?? "";
            }
            return result.Count > 0 ? result : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes the quest strings into slot values (one base-36 character per slot;
    /// strQuests = slots 0-99, strQuests2 = 100-199, ...). Published alongside the
    /// raw strings so the packet-based apply fallback stays usable.
    /// </summary>
    private static List<(int Slot, int Value)> DecodeSlots(Dictionary<string, string> strings)
    {
        List<(int, int)> result = new();
        foreach ((string field, string value) in strings)
        {
            int block = field == "strQuests" ? 0 : int.TryParse(field["strQuests".Length..], out int n) ? n - 1 : -1;
            if (block < 0)
                continue;

            for (int i = 0; i < value.Length; i++)
            {
                int v = DecodeChar(value[i]);
                if (v > 0)
                    result.Add((block * 100 + i, v));
            }
        }
        return result;
    }

    private static int DecodeChar(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'Z' => c - 'A' + 10,
        >= 'a' and <= 'z' => c - 'a' + 36,
        _ => 0,
    };

    /// <summary>Per-character (= per-slot) maximum of two quest strings.</summary>
    private static string MergeMax(string mine, string theirs)
    {
        int length = Math.Max(mine.Length, theirs.Length);
        StringBuilder sb = new(length);
        for (int i = 0; i < length; i++)
        {
            char a = i < mine.Length ? mine[i] : '0';
            char b = i < theirs.Length ? theirs[i] : '0';
            sb.Append(DecodeChar(a) >= DecodeChar(b) ? a : b);
        }
        return sb.ToString();
    }
}
