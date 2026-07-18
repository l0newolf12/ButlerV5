using System.IO;

namespace ButlerV5;

/// <summary>Which CoreBots class the butler equips when it starts following.</summary>
public enum ButlerClassType { None, Farm, Solo, Dodge, Boss }

/// <summary>Where a butler goes when released / its master goes offline.</summary>
public enum ParkSpot { House, Whitemap, Stay }

/// <summary>
/// Per-ACCOUNT settings (the plugin options file is shared by every client on the PC,
/// so anything that must differ per account lives here instead), plus the lookup of
/// CoreBots' per-account class selections from its CBO storage files.
/// </summary>
public static class AccountConfig
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Skua",
        "butlerv5_options");

    private static string PathFor(string username) =>
        Path.Combine(Dir, $"{username.ToLowerInvariant()}.txt");

    public static ButlerClassType GetClassType(string username)
    {
        try
        {
            string path = PathFor(username);
            if (!File.Exists(path))
                return ButlerClassType.None;

            foreach (string line in File.ReadAllLines(path))
            {
                if (line.StartsWith("classtype=", StringComparison.OrdinalIgnoreCase) &&
                    Enum.TryParse(line["classtype=".Length..].Trim(), ignoreCase: true, out ButlerClassType type))
                    return type;
            }
        }
        catch
        {
        }
        return ButlerClassType.None;
    }

    public static void SetClassType(string username, ButlerClassType type)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(PathFor(username), $"classtype={type}\n");
        }
        catch
        {
        }
    }

    /// <summary>
    /// The class name CoreBots has configured for this account, read from
    /// %APPDATA%\Skua\options\CBO_Storage({username}).txt ("FarmClassSelect: X").
    /// </summary>
    public static string? GetCboClassName(string username, ButlerClassType type) => type switch
    {
        ButlerClassType.Farm => GetCboValue(username, "FarmClassSelect:"),
        ButlerClassType.Solo => GetCboValue(username, "SoloClassSelect:"),
        ButlerClassType.Dodge => GetCboValue(username, "DodgeClassSelect:"),
        ButlerClassType.Boss => GetCboValue(username, "BossClassSelect:"),
        _ => null,
    };

    /// <summary>The class use mode CoreBots has configured ("Base", "Atk", ...), if any.</summary>
    public static string? GetCboClassMode(string username, ButlerClassType type) => type switch
    {
        ButlerClassType.Farm => GetCboValue(username, "FarmModeSelect:"),
        ButlerClassType.Solo => GetCboValue(username, "SoloModeSelect:"),
        ButlerClassType.Dodge => GetCboValue(username, "DodgeModeSelect:"),
        ButlerClassType.Boss => GetCboValue(username, "BossModeSelect:"),
        _ => null,
    };

    private static string? GetCboValue(string username, string key)
    {
        try
        {
            string optionsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Skua",
                "options");
            string expected = $"CBO_Storage({username}).txt";

            string? file = Directory.Exists(optionsDir)
                ? Directory.EnumerateFiles(optionsDir, "CBO_Storage(*).txt")
                    .FirstOrDefault(f => Path.GetFileName(f).Equals(expected, StringComparison.OrdinalIgnoreCase))
                : null;
            if (file == null)
                return null;

            foreach (string line in File.ReadAllLines(file))
            {
                if (line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    string value = line[key.Length..].Trim();
                    return value.Length > 0 ? value : null;
                }
            }
        }
        catch
        {
        }
        return null;
    }
}
