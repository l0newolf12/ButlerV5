using Skua.Core.Interfaces;

namespace ButlerV5;

public static class GameServer
{
    /// <summary>
    /// The current game SERVER NAME (e.g. "Artix", "Twilight") - NOT the socket IP.
    /// AQW multiplexes several game worlds onto one socket host (many share
    /// sock7.aq.com), so Player.ServerIP can't tell those worlds apart. The game stores
    /// the connected server object at game.objServerInfo (set in the AS3 connect path);
    /// its sName is the real server name. Falls back to Servers.LastName, then the IP.
    /// </summary>
    public static string CurrentName(IScriptInterface bot)
    {
        try
        {
            // GetGameObject returns the value JSON-encoded, so a string comes back
            // wrapped in literal quotes ("Twilly") - strip them.
            string? name = bot.Flash.GetGameObject("objServerInfo.sName");
            if (!string.IsNullOrEmpty(name) && name != "null")
            {
                name = name.Trim().Trim('"').Trim();
                if (name.Length > 0)
                    return name;
            }
        }
        catch
        {
        }

        try
        {
            string? lastName = bot.Servers?.LastName;
            if (!string.IsNullOrEmpty(lastName))
                return lastName;
        }
        catch
        {
        }

        return bot.Player?.ServerIP ?? "";
    }

    /// <summary>One-time diagnostic: log every candidate so we can see which holds the name.</summary>
    public static void LogCandidates(IScriptInterface bot)
    {
        string flash;
        try { flash = bot.Flash.GetGameObject("objServerInfo.sName") ?? "(null)"; } catch (Exception ex) { flash = $"(err {ex.Message})"; }
        string lastName;
        try { lastName = bot.Servers?.LastName ?? "(null)"; } catch (Exception ex) { lastName = $"(err {ex.Message})"; }
        string ip;
        try { ip = bot.Player?.ServerIP ?? "(null)"; } catch (Exception ex) { ip = $"(err {ex.Message})"; }
        DebugLog.Log("GameServer", $"candidates: objServerInfo.sName='{flash}' Servers.LastName='{lastName}' ServerIP='{ip}' -> using '{CurrentName(bot)}'");
    }
}
