# AGENTS.md

This file provides guidance to AI agents (Claude Code and others) when working with code in this repository.

This is a fork of [rwmt/Multiplayer](https://github.com/rwmt/Multiplayer) (RimWorld Multiplayer mod).
Parent `Mods/CLAUDE.md` already covers repo boundaries, build toolchain, and the
multiplayer-determinism rules that apply to every mod in this `Mods/` folder — read that first.
This file covers only what's specific to this fork.

## Layout and deploy paths

The git repo root is `RimworldMP/` itself, and it is also the mod folder RimWorld loads (`About/`,
`Assemblies/`, `AssembliesCustom/`, `Defs/`, `Languages/`, `Textures/`). Source lives in
`RimworldMP/Source Codes/` (`Source Codes/Source/Multiplayer.sln`), which carries a second, also-committed
copy of the build outputs. Keep the two copies identical:

| Folder | Role |
|---|---|
| `RimworldMP/AssembliesCustom/` (`Multiplayer.dll`, `MultiplayerCommon.dll`) | **What RimWorld loads** — the two DLLs this fork modifies. |
| `RimworldMP/Assemblies/` | What RimWorld loads — supporting DLLs (loader, chat contracts, LiteNetLib, ...). |
| `RimworldMP/Source Codes/AssembliesCustom/`, `Source Codes/Assemblies/` | Where the build writes. |

`Multiplayer.csproj`/`Common.csproj` are SDK-style and restore RimWorld references from NuGet
(`Krafs.Rimworld.Ref`), so `dotnet build` needs no reference-path setup. But the `CopyToRimworld`
post-build step uses `ModOutputPath = ..\..\`, which resolves to `Source Codes/`, **not** the live mod
folder: a build alone never reaches the running game. After building, copy `Multiplayer.dll` and
`MultiplayerCommon.dll` into `RimworldMP/AssembliesCustom/`, compare hashes, and restart RimWorld —
DLLs are read at startup, and overwriting them mid-session silently does nothing.

Steam workshop item `2606448745` is upstream `rwmt.Multiplayer` with the same packageId. It must stay
disabled, or RimWorld may load it instead of this fork.

## Solution structure

`Source Codes/Source/Multiplayer.sln` — key projects:

- **`Client/`** — everything that runs in the RimWorld process: Harmony patches (`Patches/`), the
  synced-command execution pipeline (`Syncing/`), UI (`UI/`, `Windows/`), per-map/world async time
  (`AsyncTime/`), desync detection (`Desyncs/`, `MapLoadDiagnostic.cs`). Builds `Multiplayer.dll`.
- **`Common/`** — shared client/server code with no RimWorld dependency: networking primitives
  (`Networking/`, `LiteNetManager.cs`), the server loop (`MultiplayerServer.cs`), command
  serialization (`ByteReader.cs`/`ByteWriter.cs`, `ScheduledCommand.cs`, `CommandHandler.cs`),
  session/player state (`ServerPlayer.cs`, `PlayerManager.cs`). Builds `MultiplayerCommon.dll`.
- **`Server/`** — standalone dedicated-server executable wrapping `Common`.
- **`MultiplayerLoader/`**, **`ChatCommandContracts/`**, **`SourceGen/`** — loader shim, chat command
  interfaces, source generator.
- **`Tests/`** (NUnit, `dotnet test`) / **`TestsOnMono/`** — run against `Common`, no RimWorld process
  needed. They do not compile `Client/`, so they cannot catch Client or Harmony-patch regressions.

## Local changes vs. upstream

Design docs for these live at `Source Codes/*.md` — read the relevant one before touching related code:

- **Hyperspeed tier (25×)**, one step above vanilla Ultrafast. `Source/Client/AsyncTime/MpTimeSpeed.cs`
  is the *only* place the multiplier constant lives (`HyperspeedMultiplier`) — never hardcode 25
  elsewhere. Represented as `(TimeSpeed)5` since vanilla `TimeSpeed` is a non-extensible enum; every
  switch that maps a speed to a tick rate or icon must handle this explicitly (`TickRatePatch`,
  `AsyncTimeComp.TickRateMultiplier`, `AsyncWorldTimeComp.TickRateMultiplier`,
  `HyperspeedTextures.cs`). `TimeVote` is serialized as a byte and its reset markers shifted to make
  room for value 5 — all connected clients/server must be running the same build. Full detail:
  `HYPERSPEED_TIER.md`.
  The speed-button icons MP draws come from its own array, `HyperspeedTextures.Textures`. Never write
  a vanilla `static readonly` field (e.g. `TexButton.SpeedButtonTextures`) via reflection: Mono can
  bake the old value into already-compiled code, so some readers keep the original. Doing exactly
  that caused an intermittent `IndexOutOfRangeException` + "pushing more GUIClips" flood in the MP
  time controls.
- **Server-driven auto speed degrade**: when a player falls behind (`ExtrapolatedTicksBehind` past a
  threshold) for a sustained period, the host's server drops the global speed one notch at a time
  (never below Normal, never auto-raises). Decision logic lives in
  `Common/MultiplayerServer.cs` (`AutoDegradeSpeedIfBehind`, tunable constants there) and
  `Common/CommandHandler.cs` (tracks `lastGlobalTimeSpeed`). Only affects global time speed, not
  per-map async time, and is skipped in "lowest wins" vote mode. Full detail: `AUTO_DEGRADE_SPEED.md`.
  Follows the fork rule from the parent doc: the decision is server-side, but applied only via a
  normal synced `GlobalTimeSpeed` command — never by mutating client state directly.
- **Map-generation desync patches**: `Client/Patches/Determinism.cs`, `Client/Patches/MapSetup.cs`,
  `Client/Patches/MapLoadDiagnostic.cs`. There is an **open, unresolved desync investigation** — read
  `HYPERSPEED_DESYNC_INVESTIGATION.md` before changing anything speed- or determinism-related; it
  documents a known `Pawn_CallTracker.IdleCallVolumeFactor` exception storm that hasn't been traced to
  its source mod/DLC yet.

## Boundaries (extra, not in parent doc)

| Path | Access |
|---|---|
| `RimWorld\` (game files, `RimWorldWin64_Data\Managed\*.dll`) | **Read-only.** Decompile/inspect for vanilla behaviour and API signatures; never edit. |
| `C:\Users\user\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\` | **Read-only.** `Player.log` / `Player-prev.log` for crashes and Harmony errors, `Config/`, `Saves/`. |

## GABS and RimBridgeServer (AI agent testing)

Two projects by Andreas Pardeike (pardeike), external to this repo, available for live testing of this
fork against a running game:

- **GABS** (Game Agent Bridge Server, https://github.com/pardeike/GABS) — a configuration-driven MCP
  server. It launches/manages local game processes and translates the game-side GABP bridge protocol
  into MCP tools an AI agent can discover and call. Runs as its own external process, not inside
  RimWorld.
- **RimBridgeServer** (`Mods\RimBridgeServer`) — the in-game half: a RimWorld mod that opens a GABP
  bridge exposing game state, controls, and UI/debug actions (architect menu, context menus, input,
  mod settings, notifications, diagnostics) as callable capabilities.

**How they connect:** RimBridgeServer runs inside RimWorld and speaks GABP; GABS runs externally,
launches/attaches to the RimWorld process, and speaks GABP to RimBridgeServer over the bridge. An AI
agent talks MCP to GABS, which forwards calls through GABP into RimBridgeServer's capabilities —
letting the agent start the game, observe live state, and drive real UI/gameplay actions (e.g. loading
a save, starting a multiplayer session, stepping game speed, checking for desync symptoms) without a
human at the keyboard. The `mcp__gabs__*` tools available in this session are that MCP surface.

Both are vendored/external, same as this fork itself relative to the parent doc's mods — don't modify
RimBridgeServer unless the user asks or a change there is genuinely required to test this fork; read
its own `AGENTS.md`/`README.md` first.

## Testing

```bash
cd "Source Codes/Source" && dotnet build Client/Multiplayer.csproj -c Release   # then copy to live folder, see above
dotnet test Tests/Tests.csproj
dotnet test Tests/Tests.csproj --filter FullyQualifiedName~PacketTest   # single test
```
Needs **.NET SDK 10** (SDK 8 fails: `SourceGen` references Roslyn 5.3, so the generator does not run
and the build dies with `CS8795`). On this machine the SDK is installed per-user and is not on PATH —
call `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe` directly; see the parent doc.

Anything touching Client code needs an in-game check through GABS/RimBridgeServer: build, copy to the
live folder, restart, then host a session (ServerBrowser → Host tab → pick a save → Host) and watch
`Player.log`. A locally built `Multiplayer.dll` decompiles identically to the committed one at the
same commit, so an in-game difference between them is state, not the build.
