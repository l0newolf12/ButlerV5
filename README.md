# ButlerV5

A follow-bot ("butler") for [AdventureQuest Worlds](https://www.aq.com/) built as a
compiled **[Skua](https://github.com/auqw/Skua) / [VibeSkua](https://github.com/NinjaXz/VibeSkua) plugin** (not a script). Every
account running the plugin broadcasts its state to a shared file, so your accounts can
see each other and follow or summon one another — a plugin-native take on the classic
Butler v3 script, tuned to feel just as fast.

> Client-side automation for your own AQW accounts. Use at your own risk and within
> AQW's Terms of Service.

## Features

- **Master / follower following** — start a follow from the follower's own window, or
  summon your alts to you (master side). Multiple butlers per master.
- **Fast travel** — rate-limited `goto` every tick with a file-join rescue for locked
  maps, mirroring Butler v3's speed.
- **Quest handling** — per-map `UpdateQuest` bypasses for quest-locked cells/mobs, plus
  experimental client-side cloning of the master's quest state (merge-max, event-driven).
- **Class & skills** — equips the CoreBots Farm/Solo class per account and runs Skua's
  skill engine (lazy-started to avoid the auto-mode skill bug).
- **Smart parking** — when released or the master goes offline, butlers park in their
  house (or whitemap), with graceful handling of full rooms, locked maps, different
  servers, and auto-aggro rooms.
- **Conga-line prevention** — an account can't be both a butler and a master at once.
- **AntiLag** — optionally enable Skua's AntiLag (Lag Killer + animation cuts + hidden
  monsters) while summoned and/or while following, for lighter multi-boxing.
- **Roster window** — see who's online, their map/room/server, and follow/summon/release
  with per-row buttons that grey out when an action isn't valid.
- **Custom settings + log window** — all options with descriptions, plus a colored
  in-app debug log viewer.

## Building

The project references Skua's assemblies from the default install path via the
`SkuaAssemblies` property in [`ButlerV5/ButlerV5.csproj`](ButlerV5/ButlerV5.csproj)
(`C:\Program Files\Skua\Assemblies`). If your Skua is installed elsewhere, edit that
property.

```sh
dotnet build ButlerV5/ButlerV5.csproj -c Release
```

Or run [`deploy.ps1`](deploy.ps1), which builds and copies the DLL into Skua's plugins
folder. **Skua must be closed** (the DLL is locked while a client has the plugin loaded).

## Installing

Copy `ButlerV5.dll` into:

```
%APPDATA%\Skua\plugins\
```

Launch Skua and open the **Butler V5** window from the plugin menu.

> **Auto Relogin:** Skua's Auto Relogin is off by default and only auto-enables under a
> running script. Plugin-only butlers need it enabled manually in Skua's game settings
> to recover from disconnects.

> **Skua's Auto Attack/Hunt:** while an account is following, the plugin keeps Skua's
> built-in Auto Attack/Hunt turned off — it manages combat and skills itself, and running
> both fights for control. Don't rely on Auto on a butler; the plugin turns it off for you.

## Options

Set globally (and, for class type, per account) in the plugin's settings window.

| Option | Default | Notes |
|---|---|---|
| Only attack when master attacks | Off | Passive: only fight while the master fights. |
| AntiLag while summoned | On | Skua AntiLag while summoned (Ordered follow). |
| Enable debug logs | On | Record diagnostics to the in-app log viewer. |
| Auto-save logs to file | Off | Also write logs to `%APPDATA%\Skua\butlerv5_logs`. |
| Goto delay | 250 ms | Delay between goto attempts while chasing. |
| Class type | None | CoreBots Farm/Solo/Dodge/Boss to equip (per account). |
| Park location | House | House / Whitemap / Stay when released. |
| Obey summons | On | Off = ignore all summon orders (use on your hand-played account). |
| Leech mode | Off | Wait at Enter, never fight. |
| Failed gotos before rescue | 3 | Goto attempts before a file-join rescue. |
| Butler window refresh | 2 s | Roster re-read interval. |
| Quest bypasses (UpdateQuest) | On | Client-side fake gate quests on known maps. |
| Custom bypass quest IDs | (empty) | Extra quest IDs to fake. |
| Clone master quest state | On | Experimental client-side quest-state clone (merge-max). |
| Unlock all quests | On | Experimental "nuclear" bypass: maxes every quest slot client-side so all quest-gated content opens. Overrides clone + bypasses; restored on relogin. |
| Fake level 100 | Off | Client-side level display for level-gated maps. |
| Instant warning detection | On | Reads raw packets for locked/full-room warnings. |
| AntiLag while following | Off | Skua AntiLag while manually following (Manual follow). |
| Enable ButlerV5 | On | **Global master switch.** Off = the entire plugin stops on every account (no broadcasting/following/summoning) until turned back on. Prompts for confirmation. |

## How it works

Each account writes a `{username}_butlerv5` key=value file to
`%APPDATA%\Skua\character_locations`. A `FileSystemWatcher` reacts to changes so summon /
release / order handling is near-instant, with a slow poll as a safety net. Offline
detection is by `loggedin=0` or a dead PID (no time-based staleness). Quest state is
shared via a companion `{username}_butlerv5_quests` file.

## Disclaimer

This is a fan-made, client-side tool for personal use with your own accounts. It is not
affiliated with Artix Entertainment. You are responsible for how you use it.
