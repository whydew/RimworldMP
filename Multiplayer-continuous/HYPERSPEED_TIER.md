# Multiplayer — New "Hyperspeed" tier (25×), above Ultrafast

**Date:** 2026-08-21
**Mod:** `Multiplayer-continuous`
**Goal:** Bake a 6th game-speed tier, above vanilla Ultrafast, directly into Multiplayer so players no longer need Smart Speed (or any speed mod) for faster-than-vanilla multiplayer.

---

## 1. What was added

A new time speed called **Hyperspeed**, one step above Ultrafast, running at **25×** normal speed (for reference: Normal = 1×, Fast = 3×, Superfast = 6×, Ultrafast = 15×). It behaves like any other Multiplayer speed: fully synchronized, deterministic, host/vote-aware, and selectable from the normal time-control row and the faster/slower hotkeys.

Because RimWorld's `TimeSpeed` is a vanilla enum (`Paused=0, Normal=1, Fast=2, Superfast=3, Ultrafast=4`) that can't be extended, Hyperspeed reuses the next integer value, **`(TimeSpeed)5`**, and is handled explicitly everywhere a speed maps to a tick rate or is drawn. The multiplier is a single shared constant, so every client computes the same value → it stays deterministic **as long as all players run this same Multiplayer build.**

---

## 2. Files changed

### New files

| File | Purpose |
|---|---|
| `Source/Client/AsyncTime/MpTimeSpeed.cs` | Defines `MpTimeSpeed.Hyperspeed = (TimeSpeed)5`, `HyperspeedMultiplier = 25f`, and `Highest`. **This is the one place to change the multiplier.** |
| `Source/Client/AsyncTime/HyperspeedTextures.cs` | `[StaticConstructorOnStartup]` that appends a 6th icon to `TexButton.SpeedButtonTextures` (index 5) so the new button renders and no speed-indexed texture lookup can go out of range. Falls back to the Ultrafast icon if the texture is missing. |
| `Textures/UI/TimeControls/TimeSpeedButton_Hyperspeed.png` | The button icon (a "quadruple fast-forward" glyph). |

### Modified files

| File | Method / member | Change |
|---|---|---|
| `Source/Common/TimeVote.cs` | `enum TimeVote` | Inserted `Hyperspeed` at value **5** (so it matches `(TimeSpeed)5`); the reset markers shift to 6–9. `TimeVote` is serialized as a byte, so this needs all clients on the same build. |
| `Source/Client/AsyncTime/TickRatePatch.cs` | `Prefix` (patches `TickManager.TickRateMultiplier` getter) | Added `case (MpTimeSpeed.Hyperspeed, _) → 25`. |
| `Source/Client/AsyncTime/AsyncTimeComp.cs` | `TickRateMultiplier(TimeSpeed)` | Added `case MpTimeSpeed.Hyperspeed → 25`. This is what actually paces per-map ticking. |
| `Source/Client/AsyncTime/AsyncWorldTimeComp.cs` | `TickRateMultiplier(TimeSpeed)` | Added `MpTimeSpeed.Hyperspeed => 25` to the switch. Paces world/global ticking. |
| `Source/Client/AsyncTime/TimeControlUI.cs` | `TimeControlPatch.GameSpeeds` | Now `{Paused, Normal, Fast, Superfast, Ultrafast, Hyperspeed}` — Ultrafast and Hyperspeed are now clickable buttons (vanilla MP only showed up to Superfast here). |
| " | `TimeControlPatch.ModifyRect` | Widens the time-control row leftward by two button widths in MP so the two extra buttons fit. |
| " | `DoTimeControlsHotkeys` (faster key) | Cap raised from `< Superfast` to `< MpTimeSpeed.Highest`, so the "faster" hotkey/arrow steps up through Ultrafast into Hyperspeed. (The "slower" key already steps back down.) |
| " | `VoteCountDetailed` | Vote-summary loop now runs through `Hyperspeed`. |
| " | `SendTimeVote` | The click/vote sound is clamped to Ultrafast (Hyperspeed has no vanilla speed sound). |
| " | `MpTimeControls.TimeControlButton` | The async per-map/world toggle button now cycles through all six speeds (modulus `Highest + 1`) instead of stopping at Superfast. |

The three tick-rate methods requested (points 2a–c) are exactly `TickRatePatch`, `AsyncTimeComp.TickRateMultiplier`, and `AsyncWorldTimeComp.TickRateMultiplier`.

No project file changes were needed — `Multiplayer.csproj` is SDK-style and globs all `.cs` files, so the two new sources compile automatically.

---

## 3. The multiplier

**Chosen default: 25×.** It's clearly faster than Ultrafast (15×) — about 1.7× — while staying bounded. To retune it, change one line:

```csharp
// Source/Client/AsyncTime/MpTimeSpeed.cs
public const float HyperspeedMultiplier = 25f;   // try 15–30
```

All three tick-rate paths read this constant, so one edit changes the whole tier. (Anything in the 15–30 range is reasonable; much higher and slower machines will simply throttle the whole session — see limitations.)

---

## 4. How to activate it in-game

Once the modified `Multiplayer.dll` / `MultiplayerCommon.dll` are built and loaded (section 5), in any multiplayer game:

- **Time-control buttons:** the speed row now has six buttons — Pause, Normal, Fast, Superfast, Ultrafast, and **Hyperspeed** (the four-chevron icon on the far right). Click it.
- **Faster/Slower hotkeys (the "speed arrows"):** press the **"Faster"** key (default is bound in Options → Keyboard configuration, "Time speed – faster") repeatedly; speed now climbs Superfast → Ultrafast → **Hyperspeed**. The "Slower" key steps back down.
- **Async-time button** (per-map button on the colonist bar / world button, when async time is on): left/right-click now cycles through Hyperspeed as well.

Speed changes go through Multiplayer's normal command/vote system, so they're synchronized across all players exactly like the existing speeds. In "lowest-wins" server mode, Hyperspeed only takes effect when everyone has voted for it (same rule as the other speeds).

---

## 5. Building & installing

The project restores its RimWorld references from NuGet (`Krafs.Rimworld.Ref`), so a normal build works — no manual assembly paths:

1. From `Multiplayer-continuous/Source/`, build the client project (Release):
   - `dotnet build Client/Multiplayer.csproj -c Release`
   - or open `Multiplayer.sln` in Visual Studio / Rider and build.
2. The project's `CopyToRimworld` post-build step automatically copies the outputs into the mod:
   - `Multiplayer.dll` and `MultiplayerCommon.dll` → `AssembliesCustom\`
   - supporting DLLs → `Assemblies\`
3. The new texture is already in `Textures/UI/TimeControls/` — it's loaded at runtime, no build step needed.
4. **Every player (and the dedicated server, if you run one) must use this same modified build.** A player on stock Multiplayer would either fail the version/mod check or desync, because their `TimeVote`/tick-rate tables wouldn't include the new tier.

If you run a standalone/dedicated server, rebuild it too (`Server/Server.csproj`) so its `MultiplayerCommon.dll` matches.

---

## 6. Why this is desync-safe (and its limits)

- **Deterministic:** the multiplier is a compile-time constant identical on every client, and speed changes still flow through Multiplayer's synced command system. Nothing here is per-client or unsynced (that was the exact problem with Smart Speed).
- **Self-governing performance:** Multiplayer runs in lockstep and the server already throttles the shared timeline (up to ~4× slower) when any client falls behind. Hyperspeed makes each client do more work per step, so a slow machine will simply drag the effective speed back down for everyone — you won't actually reach 25× unless every PC can keep up. This is a safety feature, not a bug.
- **Everyone must match:** this is the stated simple-approach limitation — all participants need the identical modified `Multiplayer.dll` + `MultiplayerCommon.dll`.
- **Save/vote state:** `TimeVote`'s numeric values shifted (reset markers moved from 5–8 to 6–9). Transient in-session vote state assumes the new build; this matters only if you tried to mix builds (don't).
- **Not compiled here:** I couldn't build in this environment (no .NET/NuGet access in the sandbox). The changes are small, self-contained, and reviewed against the current source, but do the build + a quick in-game test (host, click Hyperspeed, confirm no desync over a few in-game hours including a raid).
