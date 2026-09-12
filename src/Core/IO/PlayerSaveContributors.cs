using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Runtime.CompilerServices;
using Terraria;
using Terraria.IO;

namespace TerrariaModder.Core.IO
{
    /// <summary>Mod-owned player sidecar capture, coordinated by Core's save lifecycle.</summary>
    public static class PlayerSaveContributors
    {
        private sealed class Contributor
        {
            internal string Suffix;
            internal Func<PlayerFileData, string> Capture;
        }
        internal sealed class CapturedSidecar
        {
            internal string Path;
            internal string Text;
        }
        private static readonly Dictionary<string, Contributor> Contributors = new Dictionary<string, Contributor>(StringComparer.Ordinal);
        private static readonly object Gate = new object();
        private sealed class LoadFailureState
        {
            internal readonly Dictionary<string, string> Errors = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        private static readonly ConditionalWeakTable<Player, LoadFailureState> LoadFailures = new ConditionalWeakTable<Player, LoadFailureState>();

        /// <summary>Retain a sidecar load failure for this player instance. Array resets and mod unloading cannot make it safe to save.</summary>
        public static void ReportLoadFailure(Player player, string modId, string message)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));
            if (string.IsNullOrWhiteSpace(modId)) throw new ArgumentException("Mod ID required", nameof(modId));
            var state = LoadFailures.GetValue(player, _ => new LoadFailureState());
            lock (state.Errors) state.Errors[modId] = message ?? "Sidecar could not be loaded";
        }
        /// <summary>Snapshot of failures for UI/reporting. A fresh successfully loaded Player has no failures.</summary>
        public static IReadOnlyList<string> GetLoadErrors(Player player)
        {
            if (player == null || !LoadFailures.TryGetValue(player, out var state)) return Array.Empty<string>();
            lock (state.Errors) return state.Errors.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ": " + p.Value).ToArray();
        }


        /// <summary>Register a game-thread capture callback. Return null when no sidecar applies.
        /// Capture must not write files. Exceptions abort the save before native I/O.</summary>
        public static void Register(string modId, string suffix, Func<PlayerFileData, string> capture)
        {
            if (string.IsNullOrWhiteSpace(modId)) throw new ArgumentException("Mod ID required", nameof(modId));
            if (string.IsNullOrEmpty(suffix) || suffix.Length < 2 || suffix[0] != '.'
                || suffix.IndexOfAny(new[] { '/', '\\', ':' }) >= 0
                || suffix.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0
                || suffix.EndsWith(".", StringComparison.Ordinal) || suffix.EndsWith(" ", StringComparison.Ordinal)
                || suffix.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) || suffix.Equals(".tmp", StringComparison.OrdinalIgnoreCase)
                || suffix.Equals(".terrariamodder-transaction", StringComparison.OrdinalIgnoreCase)
                || suffix.StartsWith(".capturing-", StringComparison.OrdinalIgnoreCase) || suffix.StartsWith(".writing-", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A non-reserved sidecar filename suffix is required", nameof(suffix));
            if (capture == null) throw new ArgumentNullException(nameof(capture));
            lock (Gate)
            {
                if (Contributors.Any(p => p.Key != modId && string.Equals(p.Value.Suffix, suffix, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Player sidecar suffix already registered: " + suffix);
                Contributors[modId] = new Contributor { Suffix = suffix, Capture = capture };
            }
        }
        public static void Unregister(string modId) { lock (Gate) Contributors.Remove(modId); }
        internal static List<CapturedSidecar> Capture(PlayerFileData file)
        {
            var errors = GetLoadErrors(file.Player);
            if (errors.Count > 0) throw new IOException("Character equipment failed to load. Repair the sidecar and reload this character before saving. " + string.Join("; ", errors));
            Contributor[] contributors;
            lock (Gate) contributors = Contributors.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToArray();
            var result = new List<CapturedSidecar>();
            if (string.IsNullOrEmpty(file.Path) || file.ServerSideCharacter) return result;
            foreach (var contributor in contributors)
            {
                string text = contributor.Capture(file);
                if (text != null) result.Add(new CapturedSidecar { Path = file.Path + contributor.Suffix, Text = text });
            }
            return result;
        }
        internal static void Publish(IEnumerable<CapturedSidecar> sidecars)
        {
            foreach (var sidecar in sidecars)
                AtomicFile.WriteText(sidecar.Path, sidecar.Text, Encoding.UTF8);
        }
    }
}
