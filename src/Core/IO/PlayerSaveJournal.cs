using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace TerrariaModder.Core.IO
{
    // A flushed redo record owns all new bytes and their exact prior generation.
    // Recovery repeats publication; it never derives backups from partial output.
    internal static class PlayerSaveJournal
    {
        private const string Suffix = ".terrariamodder-transaction";
        private const int Format = 1;
        private const int MaxBytes = 64 * 1024 * 1024;
        private const int MaxRecordBytes = 256 * 1024 * 1024;
        private static readonly object[] Gates = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();
        private sealed class Entry { internal string Path; internal byte[] Before, After; }
        internal static object Gate(string playerPath) => Gates[(StringComparer.OrdinalIgnoreCase.GetHashCode(Path.GetFullPath(playerPath)) & int.MaxValue) % Gates.Length];

        internal static void Commit(string playerPath, string corePath, IDictionary<string, byte[]> files)
        {
            playerPath = Path.GetFullPath(playerPath); corePath = Path.GetFullPath(corePath);
            lock (Gate(playerPath))
            {
                Recover(playerPath, corePath);
                var entries = files.Select(pair => new Entry { Path = Path.GetFullPath(pair.Key),
                    After = pair.Value, Before = File.Exists(pair.Key) ? File.ReadAllBytes(pair.Key) : null }).ToList();
                Validate(playerPath, corePath, entries);
                byte[] record;
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(Format); writer.Write(entries.Count);
                    foreach (var entry in entries)
                    { writer.Write(entry.Path); WriteBlob(writer, entry.Before); WriteBlob(writer, entry.After); }
                    writer.Flush(); record = stream.ToArray();
                }
                if (record.Length > MaxRecordBytes) throw new IOException("Player save journal exceeds recovery limit");
                AtomicFile.WriteBytes(playerPath + Suffix, record);
                Publish(entries);
                File.Delete(playerPath + Suffix);
            }
        }
        internal static void Recover(string playerPath, string corePath)
        {
            playerPath = Path.GetFullPath(playerPath); corePath = Path.GetFullPath(corePath);
            lock (Gate(playerPath))
            {
                string path = playerPath + Suffix;
                if (!File.Exists(path)) return;
                var entries = new List<Entry>();
                using (var stream = File.OpenRead(path))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length > MaxRecordBytes || reader.ReadInt32() != Format) throw new IOException("Invalid player save journal");
                    int count = reader.ReadInt32();
                    if (count < 2 || count > 64) throw new IOException("Invalid player save journal entry count");
                    for (int i = 0; i < count; i++)
                        entries.Add(new Entry { Path = reader.ReadString(), Before = ReadBlob(reader), After = ReadBlob(reader) });
                    if (stream.Position != stream.Length) throw new IOException("Unexpected trailing player save journal data");
                }
                Validate(playerPath, corePath, entries);
                Publish(entries);
                File.Delete(path);
            }
        }
        private static void Validate(string playerPath, string corePath, List<Entry> entries)
        {
            if (entries.Count < 2 || entries.Count > 64) throw new IOException("Invalid player save journal entry count");
            long totalBytes = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                string path = Path.GetFullPath(entry.Path);
                string filename = Path.GetFileName(path);
                if (filename.EndsWith(".", StringComparison.Ordinal) || filename.EndsWith(" ", StringComparison.Ordinal)
                    || filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new IOException("Ambiguous player save journal filename");
                bool native = string.Equals(path, playerPath, StringComparison.OrdinalIgnoreCase);
                bool core = string.Equals(path, corePath, StringComparison.OrdinalIgnoreCase);
                bool sidecar = path.StartsWith(playerPath + ".", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetDirectoryName(path), Path.GetDirectoryName(playerPath), StringComparison.OrdinalIgnoreCase);
                if ((!native && !core && !sidecar) || path.Equals(playerPath + Suffix, StringComparison.OrdinalIgnoreCase)
                    || path.Equals(playerPath + ".bak", StringComparison.OrdinalIgnoreCase) || !seen.Add(path)
                    || entry.After == null || entry.After.Length > MaxBytes || (entry.Before?.Length ?? 0) > MaxBytes)
                    throw new IOException("Invalid player save journal destination or contents");
                totalBytes += entry.After.Length + (long)(entry.Before?.Length ?? 0);
                if (totalBytes > MaxRecordBytes) throw new IOException("Player save journal exceeds recovery limit");
                entry.Path = path;
            }
            if (entries.Any(entry => seen.Contains(entry.Path + ".bak")))
                throw new IOException("Player save journal destination overlaps another file backup");
            if (!seen.Contains(playerPath) || !seen.Contains(corePath)) throw new IOException("Incomplete player save journal");
        }
        private static void Publish(List<Entry> entries)
        {
            foreach (var entry in entries)
                if (entry.Before != null) AtomicFile.WriteBytes(entry.Path + ".bak", entry.Before);
            foreach (var entry in entries) AtomicFile.WriteBytes(entry.Path, entry.After);
        }
        private static void WriteBlob(BinaryWriter writer, byte[] bytes)
        {
            writer.Write(bytes?.Length ?? -1);
            if (bytes == null) return;
            using (var sha = SHA256.Create()) writer.Write(sha.ComputeHash(bytes));
            writer.Write(bytes);
        }
        private static byte[] ReadBlob(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length == -1) return null;
            if (length < 0 || length > MaxBytes) throw new IOException("Invalid player save journal payload length");
            byte[] hash = reader.ReadBytes(32), bytes = reader.ReadBytes(length);
            using (var sha = SHA256.Create())
                if (bytes.Length != length || !sha.ComputeHash(bytes).SequenceEqual(hash)) throw new IOException("Corrupt player save journal payload");
            return bytes;
        }
    }
}
