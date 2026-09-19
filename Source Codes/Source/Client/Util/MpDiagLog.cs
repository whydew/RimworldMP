using System;
using System.IO;
using Verse;

namespace Multiplayer.Client.Util
{
    /// <summary>
    /// Opt-in diagnostic log (MP settings > "Diagnostic logging", dev mode only).
    /// Writes plain lines to MpLogs/MpDiagnostics.log instead of Verse.Log, so it
    /// never captures stack traces and never uses up RimWorld's 10,000-message
    /// log limit (after which Unity logging is switched off).
    /// </summary>
    public static class MpDiagLog
    {
        private const long MaxBytes = 64L * 1024 * 1024;

        private static readonly object Lock = new();
        private static StreamWriter writer;
        private static long written;
        private static int linesSinceFlush;
        private static bool failed;

        public static bool Enabled => Multiplayer.settings is { diagnosticLogging: true } && !failed;

        public static string FilePath => Path.Combine(Multiplayer.LogsDir, "MpDiagnostics.log");

        public static void Write(string line)
        {
            if (!Enabled) return;

            lock (Lock)
            {
                try
                {
                    if (writer == null)
                    {
                        Directory.CreateDirectory(Multiplayer.LogsDir);
                        var prev = Path.Combine(Multiplayer.LogsDir, "MpDiagnostics-prev.log");
                        if (File.Exists(FilePath))
                        {
                            if (File.Exists(prev)) File.Delete(prev);
                            File.Move(FilePath, prev);
                        }

                        writer = new StreamWriter(FilePath, false) { AutoFlush = false };
                        written = 0;
                        writer.WriteLine($"# MpDiagnostics started {DateTime.Now:O} user={Multiplayer.username}");
                    }

                    if (written > MaxBytes) return;

                    writer.WriteLine(line);
                    written += line.Length + 2;

                    // Flush in batches; also flushed on shutdown.
                    if (++linesSinceFlush >= 256)
                    {
                        linesSinceFlush = 0;
                        writer.Flush();
                    }
                }
                catch (Exception e)
                {
                    failed = true;
                    Log.Warning($"MP: diagnostic log disabled after write failure: {e.Message}");
                }
            }
        }

        public static void Flush()
        {
            lock (Lock)
            {
                try { writer?.Flush(); } catch { }
            }
        }
    }
}
