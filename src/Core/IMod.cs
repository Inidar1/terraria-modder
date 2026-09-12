namespace TerrariaModder.Core
{
    /// <summary>
    /// Interface that all mods must implement.
    /// The framework will discover and load classes implementing this interface.
    /// </summary>
    public interface IMod
    {
        /// <summary>
        /// Legacy identity property retained for binary compatibility. Core uses manifest.json.
        /// New mods can inherit ModBase to avoid duplicating metadata.
        /// </summary>
        string Id { get; }

        /// <summary>
        /// Legacy display name. Core uses the manifest name.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Legacy version string. Core uses the manifest version.
        /// </summary>
        string Version { get; }

        /// <summary>
        /// Called when the mod is loaded. The ModContext provides access to
        /// logging, configuration, and other framework services.
        /// </summary>
        void Initialize(ModContext context);

        /// <summary>
        /// Called when the mod is being unloaded. Clean up resources here.
        /// </summary>
        void Unload();
    }
}
