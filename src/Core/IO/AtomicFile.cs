using System;
using System.IO;
using System.Text;

namespace TerrariaModder.Core.IO
{
    /// <summary>Same-directory publication with an intact previous file on failure.</summary>
    public static class AtomicFile
    {
        // Bounded locks serialize same-path writers without retaining every save path.
        private static readonly object[] Gates = CreateGates();
        private static object[] CreateGates()
        {
            var gates = new object[64];
            for (int i = 0; i < gates.Length; i++) gates[i] = new object();
            return gates;
        }
        public static void WriteBytes(string path, byte[] contents)
        {
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".writing-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(contents, 0, contents.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null, true);
                else File.Move(temporary, path);
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }

        public static void WriteText(string path, string contents, Encoding encoding, bool recoverLegacyTemporary = false)
        {
            path = Path.GetFullPath(path);
            int hash = StringComparer.OrdinalIgnoreCase.GetHashCode(path) & int.MaxValue;
            lock (Gates[hash % Gates.Length])
            {
                string temporary = path + ".writing-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        using (var writer = new StreamWriter(stream, encoding, 4096, leaveOpen: true))
                        { writer.Write(contents); writer.Flush(); }
                        stream.Flush(flushToDisk: true);
                    }
                    if (recoverLegacyTemporary)
                    {
                        string legacy = path + ".tmp";
                        // Preserve the historical recovery candidate if it is the only
                        // remaining primary. Otherwise it is unpublished staging
                        // and must not supersede a later backup during recovery.
                        if (File.Exists(legacy))
                        {
                            if (!File.Exists(path)) File.Move(legacy, path);
                            else File.Delete(legacy);
                        }
                    }
                    if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
                    else File.Move(temporary, path);
                }
                finally
                {
                    // Never fall back to delete-then-move. An orphan staging file is
                    // safer than deleting the last published save or masking its error.
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch { }
                }
            }
        }
    }
}
