using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace TerrariaModder.Core.Logging
{
    /// <summary>One process/session owns one file. Readers may inspect it while it is open.</summary>
    internal sealed class SessionLogWriter : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly object _gate = new object();
        public string FilePath { get; }

        internal SessionLogWriter(string directory, bool server)
        {
            Directory.CreateDirectory(directory);
            string prefix = server ? "terrariamodder.server.session-" : "terrariamodder.client.session-";
            FilePath = Path.Combine(directory, prefix + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") +
                "-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N") + ".log");
            _writer = new StreamWriter(new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false)) { AutoFlush = true };
            // Keep 20 sessions per role, without touching active writers or unrelated files.
            foreach (var old in Directory.GetFiles(directory, prefix + "*.log")
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(20))
            {
                try
                {
                    using (new FileStream(old, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    File.Delete(old);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        internal void Write(string message)
        {
            lock (_gate) _writer.WriteLine(message);
        }

        public void Dispose()
        {
            lock (_gate) _writer.Dispose();
        }
    }
}
