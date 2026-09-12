using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Config
{
    /// <summary>
    /// Abstract base class for mod configuration.
    /// Each mod defines a subclass with typed properties and attributes.
    /// The framework reflects on the class to generate UI and serialise to JSON.
    /// </summary>
    public abstract class ModConfig
    {
        /// <summary>
        /// Config schema version. Declare in every subclass.
        /// The framework stores this in the JSON and calls Migrate() when the file is behind.
        /// </summary>
        public abstract int Version { get; }

        // Set by ConfigManager before calling any framework methods.
        public string FilePath { get; internal set; }
        internal string ModId { get; set; }
        internal ILogger Log { get; set; }

        // Baseline snapshot: property name -> boxed value captured after initial load.
        // Used by HasChangesFromBaseline() and RestartRequired tracking.
        private Dictionary<string, object> _baseline;

        // Cached property metadata (built once on first access).
        private IReadOnlyList<ConfigPropertyMeta> _propertyMetadata;

        /// <summary>
        /// Override to handle migration from older config versions.
        /// Called when the saved file's _version is less than Version.
        /// </summary>
        /// <param name="raw">Raw key-value pairs from the JSON file.</param>
        /// <param name="fromVersion">The version stored in the file (0 if absent).</param>
        protected virtual void Migrate(Dictionary<string, object> raw, int fromVersion) { }

        /// <summary>Save current property values to disk.</summary>
        public void Save() => ConfigManager.Save(this);

        /// <summary>Reload property values from disk.</summary>
        public void Reload() => ConfigManager.Reload(this);

        /// <summary>Reset all properties to their declared default values.</summary>
        public void ResetToDefaults() => ConfigManager.ResetToDefaults(this);

        /// <summary>
        /// True if any property value differs from its baseline (startup) value.
        /// Used to detect if a restart-required field has changed.
        /// </summary>
        public bool HasChangesFromBaseline()
        {
            if (_baseline == null) return false;
            foreach (var meta in GetPropertyMetadata())
            {
                object current = meta.GetValue(this);
                _baseline.TryGetValue(meta.Key, out object baseline);
                if (!ValuesEqual(current, baseline)) return true;
            }
            return false;
        }

        /// <summary>
        /// True if any [RestartRequired] property differs from its baseline value.
        /// </summary>
        public bool HasRestartRequiredChanges()
        {
            if (_baseline == null) return false;
            foreach (var meta in GetPropertyMetadata())
            {
                if (!meta.RestartRequired) continue;
                object current = meta.GetValue(this);
                _baseline.TryGetValue(meta.Key, out object baseline);
                if (!ValuesEqual(current, baseline)) return true;
            }
            return false;
        }

        /// <summary>
        /// Snapshot the current property values as the baseline.
        /// Called by ConfigManager after initial load.
        /// </summary>
        internal void SnapshotBaseline()
        {
            _baseline = new Dictionary<string, object>();
            foreach (var meta in GetPropertyMetadata())
            {
                _baseline[meta.Key] = meta.GetValue(this);
            }
        }

        /// <summary>
        /// Get cached metadata for all public settable config properties.
        /// </summary>
        public IReadOnlyList<ConfigPropertyMeta> GetPropertyMetadata()
        {
            if (_propertyMetadata != null) return _propertyMetadata;
            _propertyMetadata = BuildPropertyMetadata();
            return _propertyMetadata;
        }

        private IReadOnlyList<ConfigPropertyMeta> BuildPropertyMetadata()
        {
            var result = new List<ConfigPropertyMeta>();
            var props = GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var prop in props)
            {
                // Skip non-settable and inherited framework properties
                if (!prop.CanRead || !prop.CanWrite) continue;
                if (prop.Name == nameof(Version)) continue;
                if (prop.Name == nameof(FilePath)) continue;

                // Only supported types
                var t = prop.PropertyType;
                if (t != typeof(bool) && t != typeof(int) && t != typeof(float) &&
                    t != typeof(double) && t != typeof(string))
                    continue;

                var meta = new ConfigPropertyMeta
                {
                    Property = prop,
                    Label = prop.GetCustomAttribute<LabelAttribute>()?.Text ?? prop.Name,
                    Description = prop.GetCustomAttribute<DescriptionAttribute>()?.Text ?? "",
                    RestartRequired = prop.GetCustomAttribute<RestartRequiredAttribute>() != null,
                    Min = prop.GetCustomAttribute<RangeAttribute>()?.Min,
                    Max = prop.GetCustomAttribute<RangeAttribute>()?.Max,
                    Options = prop.GetCustomAttribute<OptionsAttribute>()?.Values
                };

                // Resolve the provider once when metadata is built, but invoke it later
                // whenever the UI or APIs need the current option list. This keeps the
                // reflection cost low without freezing dynamic values at startup.
                var optionProvider = prop.GetCustomAttribute<OptionProviderAttribute>();
                if (optionProvider != null)
                {
                    meta.HasOptionProvider = true;
                    meta.OptionsProvider = BuildOptionsProvider(prop, optionProvider);
                }

                // Scope: explicit [Server] or [Client]; untagged defaults to Client
                if (prop.GetCustomAttribute<ServerAttribute>() != null)
                    meta.Scope = ConfigScope.Server;
                else
                    meta.Scope = ConfigScope.Client;

                // FormerlySerializedAs — collect all
                var formerAttrs = prop.GetCustomAttributes<FormerlySerializedAsAttribute>();
                var formerNames = new List<string>();
                foreach (var attr in formerAttrs)
                    formerNames.Add(attr.OldName);
                meta.FormerNames = formerNames.ToArray();

                result.Add(meta);
            }

            return result.AsReadOnly();
        }

        private Func<ModConfig, string[]> BuildOptionsProvider(PropertyInfo prop, OptionProviderAttribute attr)
        {
            if (prop.PropertyType != typeof(string))
            {
                Log?.Warn($"[{ModId}] [OptionProvider] Property '{prop.Name}' must be a string to use dynamic options.");
                return null;
            }

            if (string.IsNullOrWhiteSpace(attr.MethodName))
            {
                Log?.Warn($"[{ModId}] [OptionProvider] Property '{prop.Name}' is missing a provider method name.");
                return null;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            var method = GetType().GetMethods(flags).FirstOrDefault(candidate =>
                candidate.Name == attr.MethodName && !candidate.ContainsGenericParameters &&
                candidate.GetParameters().Length == 0);
            if (method == null)
            {
                Log?.Warn($"[{ModId}] [OptionProvider] Method '{attr.MethodName}' was not found for property '{prop.Name}'.");
                return null;
            }

            if (!typeof(IEnumerable<string>).IsAssignableFrom(method.ReturnType))
            {
                Log?.Warn($"[{ModId}] [OptionProvider] Method '{attr.MethodName}' for property '{prop.Name}' must return IEnumerable<string>.");
                return null;
            }

            return config =>
            {
                try
                {
                    object target = method.IsStatic ? null : config;
                    var result = method.Invoke(target, null) as IEnumerable<string>;

                    // Normalize provider output so every caller gets the same contract:
                    // no null/blank values and no duplicates that would create noisy or
                    // ambiguous selector entries in the config UI.
                    return result?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray() ?? Array.Empty<string>();
                }
                catch (Exception ex)
                {
                    Log?.Warn($"[{ModId}] [OptionProvider] Method '{attr.MethodName}' for property '{prop.Name}' failed: {ex.Message}");
                    return Array.Empty<string>();
                }
            };
        }

        // Called by ConfigManager to apply migrated raw values.
        internal void ApplyMigration(Dictionary<string, object> raw, int fromVersion)
        {
            Migrate(raw, fromVersion);
        }

        private static bool ValuesEqual(object a, object b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;

            if (a is bool ba && b is bool bb) return ba == bb;
            if (a is string sa && b is string sb) return sa == sb;

            // Numeric comparison with tolerance
            try
            {
                double da = Convert.ToDouble(a);
                double db = Convert.ToDouble(b);
                return Math.Abs(da - db) < 0.0001;
            }
            catch { }

            return Equals(a, b);
        }
    }
}
