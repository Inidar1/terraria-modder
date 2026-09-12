using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Net
{
    /// <summary>Keep vanilla hosting and social-provider setup; substitute only the modded launcher.</summary>
    internal static class HostAndPlayLauncher
    {
        private static ILogger _log;
        private static bool _rewriteReady;
        private static int _launching;
        private static string _targetPath;
        private static DateTime _targetStamp;
        private static readonly FieldInfo ServerProcess = AccessTools.Field(typeof(Main), "tServer");

        internal static void Apply(Harmony harmony, ILogger log)
        {
            _log = log;
            var target = AccessTools.Method(typeof(Main), "HostAndPlay", Type.EmptyTypes)
                ?? throw new MissingMethodException("Main.HostAndPlay");
            // Install the guard first: a later transpiler mismatch must never silently launch vanilla.
            _rewriteReady = false;
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(HostAndPlayLauncher), nameof(BeforeLaunch)),
                finalizer: new HarmonyMethod(typeof(HostAndPlayLauncher), nameof(AfterLaunch)));
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(HostAndPlayLauncher), nameof(RewriteLauncher)));
            _rewriteReady = true;
            _log.Info("[H&P] Vanilla hosting preserved with modded server launcher");
        }

        private static IEnumerable<CodeInstruction> RewriteLauncher(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var setter = AccessTools.PropertySetter(typeof(ProcessStartInfo), nameof(ProcessStartInfo.CreateNoWindow));
            var replacement = AccessTools.Method(typeof(HostAndPlayLauncher), nameof(PrepareStartInfo));
            int count = 0;
            foreach (var instruction in code)
            {
                if (!instruction.Calls(setter)) continue;
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                count++;
            }
            if (count != 1) throw new InvalidOperationException("HostAndPlay launch boundary changed; expected one CreateNoWindow assignment.");
            return code;
        }

        private static bool BeforeLaunch(out bool __state)
        {
            __state = false;
            if (Interlocked.CompareExchange(ref _launching, 1, 0) != 0)
            {
                _log.Error("[H&P] Another launch is already in progress; no second server started");
                return false;
            }
            try
            {
                if (!_rewriteReady || ServerProcess == null)
                    throw new InvalidOperationException("Modded host launch hook is unavailable.");
                var old = ServerProcess.GetValue(null) as Process;
                if (old != null)
                {
                    bool alive;
                    try { alive = !old.HasExited; }
                    catch (InvalidOperationException) { alive = false; } // An unstarted Process has no child.
                    if (alive) throw new InvalidOperationException("The previous local server is still running.");
                }
                string folder = CoreConfig.Instance.GameFolder;
                if (!File.Exists(Path.Combine(folder, "TerrariaInjector.exe")))
                    throw new FileNotFoundException("TerrariaInjector.exe is required to host a modded world.");
                if (!File.Exists(Path.Combine(folder, "TerrariaServer.exe")))
                    throw new FileNotFoundException("TerrariaServer.exe is required to host a world.");
                _targetPath = null;
                __state = true;
                return true;
            }
            catch (Exception ex)
            {
                ReportFailure(ex);
                Interlocked.Exchange(ref _launching, 0);
                return false;
            }
        }

        // Replaces just the setter inside Main.HostAndPlay, after native argument construction and
        // before SocialAPI.Network.LaunchLocalServer. Steam/WeGame still append their own arguments,
        // update invitation state and start the same Process; native client connection logic is unchanged.
        private static void PrepareStartInfo(ProcessStartInfo info, bool createNoWindow)
        {
            var config = CoreConfig.Instance;
            info.CreateNoWindow = createNoWindow;
            info.FileName = Path.Combine(config.GameFolder, "TerrariaInjector.exe");
            info.WorkingDirectory = config.GameFolder;
            string path = Path.Combine(config.RootPath, "target");
            // Frozen injector protocol: it consumes/deletes this hint before loading the server.
            // Never overwrite an existing hint belonging to another launch.
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream)) writer.Write("TerrariaServer.exe");
            _targetPath = path;
            _targetStamp = File.GetLastWriteTimeUtc(path);
        }

        private static Exception AfterLaunch(Exception __exception, bool __state)
        {
            if (!__state) return __exception;
            try
            {
                var process = ServerProcess.GetValue(null) as Process;
                if (__exception != null)
                {
                    ReportFailure(__exception);
                    bool started = false;
                    try { started = process != null && process.Id > 0; } catch (InvalidOperationException) { }
                    if (!started) RemoveHint(_targetPath, _targetStamp);
                    else ObserveHint(process, _targetPath, _targetStamp);
                }
                else
                {
                    _log.Info("[H&P] Modded server process started, PID " + process.Id);
                    ObserveHint(process, _targetPath, _targetStamp);
                }
            }
            finally { Interlocked.Exchange(ref _launching, 0); }
            // A launch failure must not retry vanilla and risk an unmodded save or duplicate server.
            return null;
        }

        private static void ObserveHint(Process process, string path, DateTime stamp)
        {
            if (path == null) return;
            Task.Run(async () =>
            {
                try
                {
                    // No arbitrary five-second deletion while the injector may still be starting.
                    while (File.Exists(path) && !process.HasExited) await Task.Delay(100).ConfigureAwait(false);
                    if (process.HasExited) RemoveHint(path, stamp);
                }
                catch (Exception ex) { _log.Warn("[H&P] Target-hint observation: " + ex.Message); }
            });
        }

        private static void RemoveHint(string path, DateTime stamp)
        {
            if (path == null) return;
            try
            {
                if (File.Exists(path) && File.GetLastWriteTimeUtc(path) == stamp &&
                    File.ReadAllText(path).Trim() == "TerrariaServer.exe") File.Delete(path);
            }
            catch (Exception ex) { _log.Warn("[H&P] Could not clean own target hint: " + ex.Message); }
        }

        private static void ReportFailure(Exception ex)
        {
            _log.Error("[H&P] Modded server launch failed; no vanilla retry: " + ex.Message);
            if (Main.gameMenu)
            {
                Main.statusText = "Unable to start the modded server. " + ex.Message;
                Main.menuMode = 1000000; // Vanilla status/error page.
            }
        }
    }
}
