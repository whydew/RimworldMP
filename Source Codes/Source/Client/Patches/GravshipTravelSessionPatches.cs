using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Multiplayer.Client.Factions;
using Multiplayer.Client.Persistent;
using Multiplayer.Client.Util;
using RimWorld;
using RimWorld.Planet;
using Verse;
using UnityEngine;
using Verse.Sound;

namespace Multiplayer.Client.Patches
{
    #region Input

    [HarmonyPatch(typeof(GravshipUtility), nameof(GravshipUtility.PreLaunchConfirmation))]
    public static class PatchGravshipPreLaunchConfirmation
    {
        static void Prefix(Building_GravEngine engine, ref Action launchAction)
        {
            if (Multiplayer.Client == null) return;

            GravshipTravelUtils.OpenSessionAt(engine.Map.Tile);
        }
    }

    [HarmonyPatch]
    public static class PatchGravshipPreLaunchCancel
    {
        static MethodBase TargetMethod()
        {
            return MpMethodUtil.GetLambda(typeof(GravshipUtility), nameof(GravshipUtility.PreLaunchConfirmation), lambdaOrdinal: 4);
        }

        static void Postfix(Dialog_MessageBox __instance)
        {
            if (Multiplayer.Client == null) return;
            if (!Multiplayer.ExecutingCmds) return;

            GravshipTravelUtils.CloseSessionAt(Find.CurrentMap.Tile);
            GravshipTravelUtils.CloseGravshipPrelaunchDialog();
        }
    }

    [HarmonyPatch(typeof(CompPilotConsole), nameof(CompPilotConsole.StartChoosingDestination_NewTemp))]
    public static class PatchPilotConsoleStartChoosingDestination
    {
        static void Postfix(CompPilotConsole __instance, bool launching)
        {
            if (Multiplayer.Client == null) return;
            if (!launching) return;

            GravshipTravelUtils.CloseGravshipPrelaunchDialog();
            GravshipTravelUtils.OpenSessionAt(__instance.engine.Map.Tile);
        }
    }

    [HarmonyPatch]
    static class PatchTilePickerCancelLambda
    {
        static MethodBase TargetMethod()
        {
            return MpMethodUtil.GetLambda(typeof(CompPilotConsole), nameof(CompPilotConsole.StartChoosingDestination_NewTemp), lambdaOrdinal: 4);
        }

        static void Prefix(bool ___launching, ref bool __state)
        {
            if (Multiplayer.Client == null || ___launching || Multiplayer.dontSync)
                return;

            Multiplayer.dontSync = true;
            __state = true;
        }

        // TODO: Something in Feedback.cs seems to block the wantedMode switch.
        // For now, it's set manually - consider keeping it this way permanently.
        static void Finalizer(bool ___launching, PlanetTile ___curTile, bool __state)
        {
            if (Multiplayer.Client == null) return;
            if (__state) Multiplayer.dontSync = false;
            if (!Multiplayer.ExecutingCmds || !___launching) return;

            Find.World.renderer.wantedMode = WorldRenderMode.None;
            Find.TilePicker.StopTargetingInt();
            GravshipTravelUtils.CloseSessionAt(___curTile);
        }
    }

    [HarmonyPatch]
    public static class PatchGravshipMapArriveMethods
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GravshipUtility), nameof(GravshipUtility.ArriveExistingMap));
            yield return AccessTools.Method(typeof(GravshipUtility), nameof(GravshipUtility.ArriveNewMap));
        }
        static void Postfix(Gravship gravship)
        {
            if (Multiplayer.Client == null) return;

            GravshipTravelUtils.OpenSessionAt(gravship.destinationTile);
        }
    }

    [HarmonyPatch(typeof(Designator_MoveGravship), nameof(Designator_MoveGravship.DesignateSingleCell))]
    public static class PatchGravshipDesignatorDeselectForAllClients
    {
        static void Prefix() => CancelDesignatorDeselection.EnableCanceling();

        static void Finalizer() => CancelDesignatorDeselection.DisableCanceling();
    }

    // TODO: Is there a better way to synchronize this method?
    [HarmonyPatch(typeof(GravshipLandingMarker), nameof(GravshipLandingMarker.BeginLanding))]
    public static class PatchBeginLandingToSyncWithClients
    {
        static bool Prefix(GravshipLandingMarker __instance)
        {
            if (Multiplayer.Client == null) return true;
            if (Multiplayer.ExecutingCmds) return true;

            SyncBeginLanding(__instance);

            return false;
        }

        [SyncMethod]
        public static void SyncBeginLanding(GravshipLandingMarker landingMarker)
        {
            var gravshipController = Find.GravshipController;

            if (landingMarker == null || landingMarker.Tile == null)
            {
                MpLog.Error($"[MP] SyncConfirmGravshipLanding: Marker [{landingMarker != null}] Tile [{landingMarker?.Tile != null}].");
                return;
            }

            gravshipController.landingMarker = landingMarker;
            landingMarker.BeginLanding(gravshipController);
            gravshipController.landingMarker = null;

            if (!TickPatch.currentExecutingCmdIssuedBySelf)
                SoundDefOf.Gravship_Land.PlayOneShotOnCamera();
        }
    }

    [HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.AbortLanding))]
    public static class PatchAbortLandingToCloseSession
    {
        static void Prefix(WorldComponent_GravshipController __instance, ref bool __state)
        {
            if (Multiplayer.Client == null) return;
            if (!Multiplayer.ExecutingCmds) return;

            GravshipTravelUtils.CloseSessionAt(__instance.landingTile);
        }
    }

    [HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.PlaceGravship))]
    public static class PatchPlaceGravshipToUpdateSystemsAfterTheySpawn
    {
        static void Postfix(Map map)
        {
            if (Multiplayer.Client == null) return;

            map.glowGrid.GlowGridUpdate_First();
        }
    }

    #endregion

    #region Landing/Takeoff freeze

    [HarmonyPatch]
    public static class PatchGravshipCutsceneToFreeze
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.InitiateTakeoff));
            yield return AccessTools.Method(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.InitiateLanding));
        }

        static void Postfix(WorldComponent_GravshipController __instance)
        {
            if (Multiplayer.Client == null) return;

            GravshipCutsceneSync.OnCutsceneStarted(__instance);
            GravshipTravelUtils.StartFreeze();
        }
    }

    // If the synced end already arrived while this client was still preparing, skip the animation.
    [HarmonyPatch]
    public static class PatchGravshipBeginCutscene
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.BeginTakeoffCutscene));
            yield return AccessTools.Method(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.BeginLandingCutscene));
        }

        static void Postfix(WorldComponent_GravshipController __instance)
        {
            if (Multiplayer.Client == null) return;
            GravshipCutsceneSync.OnCutsceneBegan(__instance);
        }
    }

    [HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.TakeoffEnded))]
    public static class PatchGravshipTakeoffEnded
    {
        static bool Prefix(WorldComponent_GravshipController __instance)
        {
            if (Multiplayer.Client == null) return true;
            if (!GravshipCutsceneSync.OnLocalCutsceneEnded(__instance)) return false;

            GravshipTravelUtils.CloseSessionAt(__instance.takeoffTile);
            return true;
        }
    }

    [HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.LandingEnded))]
    public static class PatchGravshipLandingEnded
    {
        static bool Prefix(WorldComponent_GravshipController __instance, ref bool __state)
        {
            if (Multiplayer.Client == null) return true;
            if (!GravshipCutsceneSync.OnLocalCutsceneEnded(__instance)) return false;

            // TODO: Is the random pushing here necessary? This might be related to issue #638.
            Rand.PushState();
            Rand.StateCompressed = __instance.map.AsyncTime().randState;
            __state = true;

            GravshipTravelUtils.CloseSessionAt(__instance.gravship.destinationTile);

            // LandingEnded calls Find.TickManager.CurTimeSpeed = TimeSpeed.Normal, but that doesn't work in MP as we
            // have our own way of changing the map speed.
            __instance.map.AsyncTime().DesiredTimeSpeed = TimeSpeed.Normal;
            return true;
        }

        static void Finalizer(bool __state)
        {
            if (__state) Rand.PopState();
        }
    }

    /// <summary>
    /// Makes the end of the gravship takeoff/landing cutscene a synchronized event.
    ///
    /// Vanilla finishes the flight (TakeoffEnded/LandingEnded: abandon map, travel, landing outcome roll,
    /// scenario hooks) from WorldComponentUpdate, i.e. per frame, after a GPU capture, a camera pan and a
    /// 10 s animation that is skipped when the local "gravship cutscenes" option is off. Players therefore
    /// reached that point at different moments, and the server lifted the freeze 10 s after the host was
    /// done even if others weren't, so they applied the result at different ticks (desync).
    ///
    /// Now:
    ///  - While a cutscene is running and this client hasn't reached its end yet, this client executes no
    ///    commands and no ticks (TickPatch.RunCmds). The state changes vanilla makes during the cutscene
    ///    (removing/placing the ship in long events) therefore happen at the same point on every client.
    ///  - When a client reaches the end, it doesn't finish the flight itself; it sends a synced
    ///    "cutscene ended" command (every client does; duplicates are ignored).
    ///  - The first such command finishes the flight on every client, inside the command's deterministic
    ///    context, under the faction that started the flight. Only then does the client unfreeze.
    ///  - Join points requested during a flight are deferred until the flight has finished, so nobody joins
    ///    or reloads in the middle of it (the cutscene state isn't saved). Flights are identified by their start step so a stale
    ///    "ended" command can't end a later flight.
    /// </summary>
    public static class GravshipCutsceneSync
    {
        private const float MaxHoldSeconds = 90f;

        private static int flightMapId = -1;
        private static int flightId = -1; // timer step the flight started at (same on every client)
        private static Action deferredJoinPoint;
        private static Faction flightFaction;
        private static int localReadyMapId = -1;
        private static int endSentForMapId = -1;
        private static bool endRequested;
        private static bool applying;
        private static float holdStartedAt = -1f;
        private static bool holdTimedOut;

        /// <summary>True between the start and the synced end of a flight (same on every client).</summary>
        public static bool FlightActive => flightMapId != -1;

        /// <summary>Runs the join point now, or after the current flight has ended.</summary>
        public static void RunOrDeferJoinPoint(Action createJoinPoint)
        {
            if (FlightActive)
                deferredJoinPoint += createJoinPoint;
            else
                createJoinPoint();
        }

        public static void Reset()
        {
            flightMapId = -1;
            flightId = -1;
            flightFaction = null;
            localReadyMapId = -1;
            endSentForMapId = -1;
            endRequested = false;
            deferredJoinPoint = null;
            holdStartedAt = -1f;
            holdTimedOut = false;
        }

        public static void OnCutsceneStarted(WorldComponent_GravshipController controller)
        {
            flightId = TickPatch.Timer;
            flightMapId = controller.map?.uniqueID ?? -1;
            flightFaction = Faction.OfPlayer;
            localReadyMapId = -1;
            endSentForMapId = -1;
            endRequested = false;
            holdStartedAt = -1f;
            holdTimedOut = false;
        }

        public static void OnCutsceneBegan(WorldComponent_GravshipController controller)
        {
            if (endRequested)
                controller.timeLeft = 0f;
        }

        /// <summary>
        /// Called from the TakeoffEnded/LandingEnded prefixes. Returns true when vanilla should finish the
        /// flight now (inside the synced command), false to wait.
        /// </summary>
        public static bool OnLocalCutsceneEnded(WorldComponent_GravshipController controller)
        {
            if (applying) return true;

            var mapId = controller.map?.uniqueID ?? -1;
            localReadyMapId = mapId;

            if (endSentForMapId != mapId && !Multiplayer.IsReplay && FlightActive)
            {
                endSentForMapId = mapId;
                SyncCutsceneEnded(mapId, flightId);
            }

            return false;
        }

        /// <summary>True while commands and ticks must wait for this client's cutscene to catch up.</summary>
        public static bool HoldingSimulation
        {
            get
            {
                if (!WorldComponent_GravshipController.cutsceneInProgress || holdTimedOut)
                    return false;

                var map = Current.Game?.World != null ? Find.GravshipController?.map : null;
                if (map == null || map.uniqueID != flightMapId || localReadyMapId == map.uniqueID)
                    return false;

                if (holdStartedAt < 0f)
                    holdStartedAt = Time.realtimeSinceStartup;
                else if (Time.realtimeSinceStartup - holdStartedAt > MaxHoldSeconds)
                {
                    holdTimedOut = true;
                    Log.Error($"MP: gravship cutscene on map {map.uniqueID} didn't finish within {MaxHoldSeconds} s; resuming the simulation without it (this can desync)");
                    return false;
                }

                return true;
            }
        }

        [SyncMethod]
        public static void SyncCutsceneEnded(int mapId, int startedAt)
        {
            if (startedAt != flightId || mapId != flightMapId)
                return; // Stale (an earlier flight) or duplicate.

            var controller = Find.GravshipController;
            if (controller?.map == null || controller.map.uniqueID != mapId || !WorldComponent_GravshipController.cutsceneInProgress)
                return; // Already finished (duplicate from another player) or stale.

            if (localReadyMapId != mapId)
            {
                // Only possible if the hold timed out. Finish as soon as the local flow allows.
                MpLog.Error($"Gravship cutscene end for map {mapId} executed before this client finished preparing it");
                endRequested = true;
                controller.timeLeft = 0f;
                return;
            }

            Apply(controller);
        }

        private static void Apply(WorldComponent_GravshipController controller)
        {
            var faction = flightFaction;
            if (faction != null)
                FactionExtensions.PushFaction(null, faction, force: true);

            applying = true;
            try
            {
                if (controller.isTakeoff)
                    controller.TakeoffEnded();
                else
                    controller.LandingEnded();
            }
            finally
            {
                applying = false;
                endRequested = false;
                flightMapId = -1;
                if (faction != null)
                    FactionExtensions.PopFaction();
            }

            GravshipTravelUtils.StopFreeze();

            var joinPoint = deferredJoinPoint;
            deferredJoinPoint = null;
            joinPoint?.Invoke();
        }
    }
    #endregion

    // TODO: Check what this actually does and whether it’s still necessary
    // Stop the landing co message from showing every game tick
    [HarmonyPatch(typeof(TickManager), nameof(TickManager.PlayerCanControl), MethodType.Getter)]
    public static class PatchTickmanagerPlayerCanControlGetter
    {
        private static bool shownLandingMessage = false;

        static bool Prefix(ref AcceptanceReport __result)
        {
            if (Multiplayer.Client == null) return true;

            WorldComponent_GravshipController gravshipController = Find.GravshipController;
            if (gravshipController != null && gravshipController.LandingAreaConfirmationInProgress)
            {
                __result = shownLandingMessage ? false : "MessageConfirmLandingAreaFirst".Translate();
                shownLandingMessage = true;
                return false;
            }

            // Not in a landing session, use vanilla logic for player control
            __result = Current.Game.PlayerHasControl;
            return false;
        }

        // Call this when the landing session ends (e.g., in your session cleanup)
        public static void ResetLandingMessageFlag() => shownLandingMessage = false;
    }
}
