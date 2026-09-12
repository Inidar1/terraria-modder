using System;
using TerrariaModder.Core.Manifest;

namespace TerrariaModder.Core
{
    /// <summary>
    /// Optional base for mods whose identity comes exclusively from manifest.json.
    /// Metadata is bound by the loader before Initialize; it is unavailable in constructors.
    /// Existing IMod implementations remain binary compatible.
    /// </summary>
    public abstract class ModBase : IMod
    {
        private ModManifest _manifest;
        private ModManifest Metadata => _manifest ?? throw new InvalidOperationException(
            "Mod metadata is available after the loader binds manifest.json, before Initialize.");

        public string Id => Metadata.Id;
        public string Name => Metadata.Name;
        public string Version => Metadata.Version;

        internal void BindManifest(ModManifest manifest)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (_manifest != null) throw new InvalidOperationException("Mod manifest is already bound.");
            _manifest = manifest;
        }

        public abstract void Initialize(ModContext context);
        public abstract void Unload();
    }
}
