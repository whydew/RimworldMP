using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using JetBrains.Annotations;
using Verse;

namespace Multiplayer.Client.Desyncs;

public class JittedMethod
{
    [CanBeNull] public MethodBase method;
    [CanBeNull] public MethodBase from;
    [CanBeNull] public int[] mapTicks;
    public int worldTicks;
    public int timer;
    public bool inInterface;

    public string TimeString()
    {
        return $"m:{mapTicks?.Join(delimiter: ",") ?? "[]"} w:{worldTicks} t:{timer}";
    }

    public override string ToString()
    {
        return $"{method?.DeclaringType}.{method?.Name} from {from?.DeclaringType}.{from?.Name} {TimeString()} i:{inInterface}";
    }
}

public static class JittedMethods
{
    private static Queue<JittedMethod> methodQueue = new();
    private static IntPtr profiler;
    private static bool adding;

    public static void Init()
    {
        profiler = Native.mono_profiler_create(IntPtr.Zero);
        Native.mono_profiler_set_jit_done_callback(profiler, (_, method, _) =>
        {
            if (adding) return;

            try
            {
                adding = true;

                if (UnityData.IsInMainThread && Multiplayer.settings != null && Native.mono_method_get_token(method) != 0)
                {
                    var methodBase = Native.GetMethodBaseFromRuntimePointer(method);

                    var game = Multiplayer.game;
                    var comps = game?.asyncTimeComps;
                    int mapCount = comps?.Count ?? 0;

                    // Recycle the oldest entry once the ring is full instead of allocating a new JittedMethod on
                    // every JIT. At steady state (queue == jittedMethodsInDesync, default 1500) this makes the hot
                    // path allocation-free apart from the single mapTicks snapshot below (itself reused when the
                    // map count is unchanged).
                    JittedMethod entry;
                    if (methodQueue.Count >= Multiplayer.settings.jittedMethodsInDesync && methodQueue.Count > 0)
                        entry = methodQueue.Dequeue();
                    else
                        entry = new JittedMethod();

                    entry.method = methodBase;
                    // Only the immediate caller frame is needed. new StackFrame(1, false) materializes that single
                    // frame without file/line (PDB) lookup, instead of new StackTrace() walking and symbolicating
                    // the entire managed stack just to read frame index 1.
                    entry.from = new StackFrame(1, false).GetMethod();

                    // Snapshot per-map ticks without a LINQ Select/ToArray. Reuse the recycled entry's array when it
                    // already has the right length (map count is stable during play).
                    var mapTicks = entry.mapTicks;
                    if (mapTicks == null || mapTicks.Length != mapCount)
                        mapTicks = entry.mapTicks = new int[mapCount];
                    for (int i = 0; i < mapCount; i++)
                        mapTicks[i] = comps[i].mapTicks;

                    entry.worldTicks = game?.asyncWorldTimeComp?.worldTicks ?? -1;
                    entry.timer = TickPatch.Timer;
                    entry.inInterface = Multiplayer.InInterface;

                    methodQueue.Enqueue(entry);
                }
            }
            finally
            {
                adding = false;
            }
        });
    }

    public static void OnApplicationQuit()
    {
        Native.mono_profiler_set_jit_done_callback(profiler, null);
    }

    public static string GetJittedMethodsString()
    {
        // Two ToArrays to prevent a compilation in between from causing a "Collection was modified" exception
        var jittedMethods = methodQueue.ToArray().Select((j, i) => (j, i)).ToArray();

        var builder = new StringBuilder();
        builder.Append("In simulation:"); // This will get a newline from the != comparison below
        var timeString = "";

        foreach (var j in jittedMethods.Where(j => !j.j.inInterface))
        {
            if (timeString != j.j.TimeString())
                builder.AppendLine();
            builder.AppendLine($"{j.i} {j.j}");
            timeString = j.j.TimeString();
        }

        builder.AppendLine();
        builder.AppendLine();

        timeString = "";

        builder.Append("In interface:");
        foreach (var j in jittedMethods.Where(j => j.j.inInterface))
        {
            if (timeString != j.j.TimeString())
                builder.AppendLine();
            builder.AppendLine($"{j.i} {j.j}");
            timeString = j.j.TimeString();
        }

        return builder.ToString();
    }
}