using System;
using System.Collections.Generic;
using System.Reflection;

namespace TerrariaModder.Core.Config
{
    /// <summary>
    /// Scope of a config property — determines which inner tab it appears in.
    /// </summary>
    public enum ConfigScope
    {
        Client,
        Server
    }

    /// <summary>
    /// Derived metadata for one config property (reflected from attributes).
    /// Used by the UI to render and edit config fields.
    /// </summary>
    public class ConfigPropertyMeta
    {
        /// <summary>The reflected property on the ModConfig subclass.</summary>
        public PropertyInfo Property { get; internal set; }

        /// <summary>Property name (used as JSON key and fallback label).</summary>
        public string Key => Property.Name;

        /// <summary>Display label from [Label] attribute, or property name.</summary>
        public string Label { get; internal set; }

        /// <summary>Tooltip text from [Description] attribute.</summary>
        public string Description { get; internal set; }

        /// <summary>Client or Server scope from [Client]/[Server] attributes.</summary>
        public ConfigScope Scope { get; internal set; }

        /// <summary>True if changing this value requires a game restart.</summary>
        public bool RestartRequired { get; internal set; }

        /// <summary>Minimum value for numeric types (from [Range]).</summary>
        public double? Min { get; internal set; }

        /// <summary>Maximum value for numeric types (from [Range]).</summary>
        public double? Max { get; internal set; }

        /// <summary>Allowed options for [Options] string fields.</summary>
        public string[] Options { get; internal set; }

        /// <summary>
        /// Optional provider for dynamic string options.
        /// When present, callers should prefer <see cref="GetOptions"/> over reading
        /// <see cref="Options"/> directly so late-bound values stay up to date.
        /// </summary>
        internal Func<ModConfig, string[]> OptionsProvider { get; set; }

        /// <summary>Legacy names mapped to this property (from [FormerlySerializedAs]).</summary>
        public string[] FormerNames { get; internal set; }

        /// <summary>Property type (bool, int, float, string).</summary>
        public Type PropertyType => Property.PropertyType;

        /// <summary>Get the current value from a config instance.</summary>
        public object GetValue(ModConfig config) => Property.GetValue(config);

        /// <summary>
        /// Whether the property declares a choice constraint, even when no choices are available.
        /// </summary>
        public bool HasOptions => Options != null || HasOptionProvider;

        internal bool HasOptionProvider { get; set; }

        /// <summary>Resolve current choices without modifying a saved unavailable selection.</summary>
        public string[] GetOptions(ModConfig config)
        {
            if (HasOptionProvider)
            {
                // A missing/invalid/empty provider must not turn a constrained field into free text.
                return OptionsProvider?.Invoke(config) ?? Array.Empty<string>();
            }

            return Options ?? Array.Empty<string>();
        }

        /// <summary>Set a value on a config instance (clamps numerics to range).</summary>
        public void SetValue(ModConfig config, object value)
        {
            object coerced = CoerceValue(value);
            Property.SetValue(config, coerced);
        }

        private object CoerceValue(object value)
        {
            if (value == null) return GetDefault();

            var t = Property.PropertyType;

            // Clamp numerics to range
            if (t == typeof(int))
            {
                int v = Convert.ToInt32(value);
                if (Min.HasValue && v < Min.Value) v = (int)Min.Value;
                if (Max.HasValue && v > Max.Value) v = (int)Max.Value;
                return v;
            }
            if (t == typeof(float))
            {
                float v = Convert.ToSingle(value);
                if (Min.HasValue && v < (float)Min.Value) v = (float)Min.Value;
                if (Max.HasValue && v > (float)Max.Value) v = (float)Max.Value;
                return v;
            }
            if (t == typeof(double))
            {
                double v = Convert.ToDouble(value);
                if (Min.HasValue && v < Min.Value) v = Min.Value;
                if (Max.HasValue && v > Max.Value) v = Max.Value;
                return v;
            }
            if (t == typeof(bool))
                return Convert.ToBoolean(value);
            if (t == typeof(string))
                return value.ToString();

            return value;
        }

        private object GetDefault()
        {
            if (Property.PropertyType == typeof(bool)) return false;
            if (Property.PropertyType == typeof(int)) return 0;
            if (Property.PropertyType == typeof(float)) return 0f;
            if (Property.PropertyType == typeof(double)) return 0.0;
            if (Property.PropertyType == typeof(string)) return "";
            return null;
        }
    }
}
