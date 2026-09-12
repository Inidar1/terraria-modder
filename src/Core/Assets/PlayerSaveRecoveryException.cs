using System;
using System.IO;

namespace TerrariaModder.Core.Assets
{
    internal sealed class PlayerSaveRecoveryException : IOException
    {
        internal PlayerSaveRecoveryException(string path, Exception inner)
            : base("Player save recovery could not complete for " + Path.GetFileName(path) + "; files were preserved", inner) { }
    }
}
