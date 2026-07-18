using Skua.Core.Interfaces;

namespace ButlerV5;

/// <summary>
/// Client-side quest fakes ("UpdateQuest"): some cells are locked and some mobs are
/// untargetable until a story quest is completed. Faking the quest completion on the
/// client unlocks them without doing the story - same technique CoreArmyLite uses.
/// Everything here is client-side only; the server is never told anything.
/// </summary>
public static class QuestBypass
{
    /// <summary>
    /// Per-map quest fakes needed to see cells/bosses without the story. Compiled from
    /// a sweep of every UpdateQuest call in the user's Scripts folder (user-reviewed).
    /// A map may need several fakes; all matching entries are applied.
    /// </summary>
    public static readonly (string Map, int QuestId)[] MapSpecific =
    {
        // ---- ultras / army bosses ----
        ("ultradage", 793),
        ("ultradarkon", 8733),
        ("ultradrago", 8395),
        ("ultraspeaker", 9125),
        ("ultradrakath", 3879),
        ("doomvault", 2954),
        ("doomvault", 3008),
        ("victormatsuri", 10294),   // Masakado
        ("astralshrine", 9802),     // Astral Empyrean
        ("astralshrine", 9803),
        ("novashrine", 9802),
        ("bocklincastle", 102520),
        ("bocklingrove", 10239),
        ("necroproject", 9901),

        // ---- former CoreArmyLite blanket list, mapped by the user ----
        ("wolfwing", 598),          // Wolfwing Evil End
        ("doomvaultb", 3004),       // Grim Underdungeon XXIX
        ("doomvaultb", 3008),       // I Command You, Help Me!
        ("towerofdoom10", 3484),    // Defeat Slugbutter
        ("finalbattle", 3799),      // Beat Death! (quest says shadowattack, used for finalbattle)
        ("mummies", 4616),          // removed from game's quest data; CruxShip.cs uses it here
        ("gluttony", 5915),         // Glutus, Take 2
        ("borgars", 7522),          // Burglinster's Revenge
        ("downbelow", 8107),        // removed from quest data; CoreBots "Bypass Banned" region
        ("manacradle", 9126),       // Once Upon Another Time
        ("liatarahill", 9814),      // Changeling

        // ---- story bosses / locked cells ----
        ("vath", 354),
        ("chaoscave", 567),
        ("chaoscave", 597),
        ("ledgermayne", 847),
        ("chaoslord", 3879),
        ("chaoslord", 3880),
        ("queenbattle", 8361),
        ("trygve", 8298),
        ("thunderfang", 1170),
        ("shadowattack", 3799),
        ("hakuwar", 9607),
        ("championdrakath", 2814),
        ("alteonbattle", 3824),

        // ---- farm / quest maps ----
        ("necrodungeon", 2059),
        ("battleunderc", 935),
        ("wanders", 976),
        ("wanders", 3773),
        ("titanattack", 8777),
        ("techfortress", 7646),
        ("stonewooddeep", 7650),
        ("blindingsnow", 899),
        ("mummies", 4614),
        ("pyramid", 4614),
        ("maloth", 6000),
        ("moonyardb", 1176),
        ("sandsea", 811),
        ("zorbaspalace", 7484),
        ("voidrefuge", 9531),
        ("darkoviagrave", 498),
        ("backroom", 8060),
        ("pyrewatch", 4077),
        ("astravia", 8000),
        ("firestorm", 1542),
        ("void", 904),
        ("rangda", 7622),
        ("starfest", 8094),
        ("badmoon", 9844),
        ("dawnfortress", 8297),
        ("dawnsanctum", 8297),
    };

    /// <summary>Achievement fake for doomvaultb (CoreArmyLite: SetAchievement(18)).</summary>
    private const string AchievementPacket =
        "{\"t\":\"xt\",\"b\":{\"r\":-1,\"o\":{\"cmd\":\"setAchievement\",\"field\":\"ia0\",\"index\":18,\"value\":1}}}";

    private const string LevelPacket =
        "{\"t\":\"xt\",\"b\":{\"r\":-1,\"o\":{\"cmd\":\"levelUp\",\"intExpToLevel\":\"0\",\"intLevel\":100}}}";

    /// <summary>Applies only the user's explicit custom quest IDs (option), if any.</summary>
    public static void ApplyCustom(IScriptInterface bot, List<int> customIds)
    {
        if (customIds.Count == 0)
        {
            DebugLog.Log("QuestBypass", "no custom quest IDs configured");
            return;
        }

        DebugLog.Log("QuestBypass", $"custom option has {customIds.Count} id(s): {string.Join(",", customIds)}");
        foreach (int id in customIds)
            FakeQuest(bot, id, "custom option");
    }

    /// <summary>Applies the quest fakes for the given map, when it has any.</summary>
    public static void ApplyForMap(IScriptInterface bot, string? mapName)
    {
        try
        {
            if (string.IsNullOrEmpty(mapName))
                return;

            int[] matches = MapSpecific
                .Where(e => e.Map.Equals(mapName, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.QuestId)
                .ToArray();

            if (matches.Length == 0)
            {
                DebugLog.Log("QuestBypass", $"map '{mapName}': no bypass entries");
            }
            else
            {
                DebugLog.Log("QuestBypass", $"map '{mapName}': {matches.Length} bypass entr{(matches.Length == 1 ? "y" : "ies")}: {string.Join(",", matches)}");
                foreach (int questId in matches)
                    FakeQuest(bot, questId, $"map {mapName}");
            }

            // doomvaultb is gated by an achievement rather than a quest.
            if (mapName.Equals("doomvaultb", StringComparison.OrdinalIgnoreCase))
            {
                DebugLog.Log("QuestBypass", "doomvaultb: sending achievement fake (ia0/18)");
                bot.Send.ClientPacket(AchievementPacket, "json");
            }
        }
        catch (Exception ex)
        {
            DebugLog.Log("QuestBypass", $"map apply failed: {ex.Message}");
        }
    }

    /// <summary>Loads quest data and sends the client-side fake, logging each step's outcome.</summary>
    private static void FakeQuest(IScriptInterface bot, int questId, string reason)
    {
        try
        {
            bot.Quests.Load(questId);
            bot.Quests.UpdateQuest(questId);
            DebugLog.Log("QuestBypass", $"UpdateQuest({questId}) sent ({reason})");
        }
        catch (Exception ex)
        {
            DebugLog.Log("QuestBypass", $"UpdateQuest({questId}) FAILED ({reason}): {ex.Message}");
        }
    }

    /// <summary>Client-side level-100 fake for level-gated maps (visual only).</summary>
    public static void ApplyLevelFake(IScriptInterface bot)
    {
        try
        {
            if (bot.Player?.Level is < 100)
            {
                DebugLog.Log("QuestBypass", "applying client-side level 100 fake");
                bot.Send.ClientPacket(LevelPacket, "json");
            }
        }
        catch (Exception ex)
        {
            DebugLog.Log("QuestBypass", $"level fake failed: {ex.Message}");
        }
    }

    /// <summary>Parses the custom-IDs option ("1234, 5678").</summary>
    public static List<int> ParseCustomIds(string? csv)
    {
        List<int> result = new();
        if (string.IsNullOrWhiteSpace(csv))
            return result;

        foreach (string part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out int id) && id > 0)
                result.Add(id);
        }
        return result;
    }
}
