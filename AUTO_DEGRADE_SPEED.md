# Auto-degrade: drop game speed when a player can't keep up

**Date:** 2026-08-22
**Mod:** `Multiplayer-continuous`
**Why:** Last night's desync session showed the host (Whydew) repeatedly "too far behind" at Hyperspeed (25×). This feature makes the game automatically step the speed down a notch when any player can't keep up, so it settles at a speed everyone's hardware can sustain instead of stalling/desyncing.

---

## What it does

The server (which runs in the host's process for a hosted game, and already tracks how far behind each player is) watches for a player that stays "behind" for a short sustained period. When that happens it **lowers the global game speed by one notch** — e.g. Hyperspeed → Ultrafast → Superfast → … → Normal — and keeps stepping down (with a cooldown between steps) until everyone can keep up. It never drops below Normal and never auto-raises the speed again (raising stays a player choice).

**Why it's desync-safe:** the *decision* to slow down is made in one place (the host's server, from real performance data), but the actual speed change is sent as a **normal `GlobalTimeSpeed` command through the synced command stream** — the exact same path a human clicking the speed button uses. Every client applies it at the same scheduled tick, so the simulation stays deterministic. This mirrors how the server already injects synced commands (e.g. `ServerPlayer.ResetTimeVotes`, `CommandHandler.PauseAll`).

---

## Files changed

| File | Change |
|---|---|
| `Source/Common/MultiplayerServer.cs` | Added the auto-degrade fields/constants, an `AutoDegradeSpeedIfBehind(elapsedMs)` method, and a call to it once per server loop iteration (right after the tick-advance loop in `Run`). |
| `Source/Common/CommandHandler.cs` | Tracks the current global speed (`server.lastGlobalTimeSpeed`) whenever a `GlobalTimeSpeed` command passes through `Send`, so auto-degrade knows what to step down from. |

Both are in the `Common` project → rebuilding produces `MultiplayerCommon.dll` (the build's `CopyToRimworld` step drops it into `AssembliesCustom\`). No client-side or tier files were touched. As before, **all players must run the same rebuilt build.**

---

## Tunable settings (in `MultiplayerServer.cs`)

```csharp
public bool autoDegradeEnabled = true;              // master on/off
public const int    AutoDegradeBehindThreshold = 60;   // ExtrapolatedTicksBehind that counts as "not keeping up"
public const double AutoDegradeSustainMs       = 1500; // must stay behind this long before dropping (avoids reacting to spikes)
public const double AutoDegradeCooldownMs      = 5000; // wait this long after a drop before dropping again
public const byte   AutoDegradeFloorSpeed      = 1;    // never auto-drop below Normal (1x)
```

- The hard "simulation paused because a player is too far behind" threshold in vanilla MP is **90**; auto-degrade triggers earlier at **60** so it slows *before* the game hard-stalls.
- Raise `AutoDegradeBehindThreshold` to be more tolerant of lag before slowing; lower it to react sooner.
- `AutoDegradeCooldownMs` stops it from dropping several notches instantly — it drops one, waits 5s to see if that fixed it, then drops again if still behind.

---

## How it behaves in-game

- Play at Hyperspeed. If a machine can't sustain it, after ~1.5s of falling behind the speed drops to Ultrafast automatically; if still behind, to Superfast ~5s later, and so on down to Normal.
- Each drop is logged server-side: `Auto-lowered game speed to <Speed> because a player can't keep up: <name>`.
- Players can manually bump the speed back up whenever they like; if the lag returns, it'll step down again.

---

## Limitations / notes

- **Global time speed only.** It targets the normal (shared) game speed. In **async-time** games (per-map speeds) a `GlobalTimeSpeed` command only affects the world timer, not individual maps, so per-map speeds wouldn't be auto-lowered. Extending to per-map would require issuing per-map `MapTimeSpeed` commands (possible later if you use async time).
- **Skipped in "Lowest wins" time-control mode**, which is vote-based and has no single global speed to lower — there, a lagging player can simply vote a lower speed.
- `lastGlobalTimeSpeed` starts at Normal and is learned from speed commands during the session. In practice, being behind requires having set a high speed (which sets it), so this is reliable; a freshly loaded save left untouched at a high speed would only auto-degrade after the next speed change.
- This is a **mitigation for the performance/keep-up cause** of the desync. It does **not** address the separate `Pawn_CallTracker.IdleCallVolumeFactor` exception storm found in the log — that should still be tracked to its mod/DLC source (see `HYPERSPEED_DESYNC_INVESTIGATION.md`).
- Not compiled in this environment (no .NET/NuGet here); the change is small and isolated, but do a build + quick host/client test.
