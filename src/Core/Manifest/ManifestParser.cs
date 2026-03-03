using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Manifest
{
    /// <summary>
    /// Parses manifest.json files.
    /// Uses simple JSON parsing to avoid external dependencies.
    /// </summary>
    public static class ManifestParser
    {
        /// <summary>
        /// Parse a manifest.json file.
        /// </summary>
        public static ModManifest Parse(string jsonPath)
        {
            string fileName = Path.GetFileName(Path.GetDirectoryName(jsonPath) ?? jsonPath);

            var manifest = new ModManifest
            {
                FolderPath = Path.GetDirectoryName(jsonPath)
            };

            if (!File.Exists(jsonPath))
            {
                manifest.IsValid = false;
                manifest.ValidationErrors.Add($"Manifest file not found: {jsonPath}");
                return manifest;
            }

            try
            {
                string json = File.ReadAllText(jsonPath);

                if (string.IsNullOrWhiteSpace(json))
                {
                    manifest.IsValid = false;
                    manifest.ValidationErrors.Add($"[{fileName}] Manifest file is empty");
                    return manifest;
                }

                ParseJson(json, manifest);
                ValidateManifest(manifest, fileName);
            }
            catch (IOException ex)
            {
                manifest.IsValid = false;
                manifest.ValidationErrors.Add($"[{fileName}] Failed to read manifest file: {ex.Message}");
            }
            catch (Exception ex)
            {
                manifest.IsValid = false;
                manifest.ValidationErrors.Add($"[{fileName}] Failed to parse manifest: {ex.Message}");
            }

            return manifest;
        }

        /// <summary>
        /// Create a default manifest for mods without manifest.json.
        /// </summary>
        public static ModManifest CreateDefault(string folderPath, string dllName)
        {
            string id = Path.GetFileNameWithoutExtension(dllName).ToLowerInvariant();

            return new ModManifest
            {
                Id = id,
                Name = Path.GetFileNameWithoutExtension(dllName),
                Version = "0.0.0",
                Author = "Unknown",
                Description = "No manifest provided",
                FolderPath = folderPath,
                DllPath = Path.Combine(folderPath, dllName),
                IsValid = true,
                ValidationErrors = { "Warning: No manifest.json found, using defaults" }
            };
        }

        private static void ParseJson(string json, ModManifest manifest)
        {
            // Simple JSON parsing without external dependencies
            // This handles the flat structure of mod.json

            manifest.Id = ExtractString(json, "id");
            manifest.Name = ExtractString(json, "name");
            manifest.Version = ExtractString(json, "version");
            manifest.Author = ExtractString(json, "author");
            manifest.Description = ExtractString(json, "description");
            manifest.TerrariaVersion = ExtractString(json, "terraria_version");
            manifest.FrameworkVersion = ExtractString(json, "framework_version");
            manifest.EntryDll = ExtractString(json, "entry_dll");
            manifest.Homepage = ExtractString(json, "homepage");
            manifest.Icon = ExtractString(json, "icon");

            manifest.Dependencies = ExtractStringArray(json, "dependencies");
            manifest.OptionalDependencies = ExtractStringArray(json, "optional_dependencies");
            manifest.IncompatibleWith = ExtractStringArray(json, "incompatible_with");
            manifest.LoadAfter = ExtractStringArray(json, "load_after");
            manifest.LoadBefore = ExtractStringArray(json, "load_before");
            manifest.Tags = ExtractStringArray(json, "tags");
            manifest.ConfigSchemaJson = ExtractObject(json, "config_schema");
            manifest.Keybinds = ExtractKeybinds(json);

            // Determine DLL path
            string dllName = manifest.EntryDll;
            if (string.IsNullOrEmpty(dllName) && !string.IsNullOrEmpty(manifest.Id))
            {
                // Default: ModId.dll (with proper casing)
                dllName = manifest.Id.Replace("-", "") + ".dll";
            }

            if (!string.IsNullOrEmpty(dllName) && !string.IsNullOrEmpty(manifest.FolderPath))
            {
                manifest.DllPath = Path.Combine(manifest.FolderPath, dllName);
            }
        }

        private static void ValidateManifest(ModManifest manifest, string fileName)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(manifest.Id))
                errors.Add($"[{fileName}] Missing required field: id");
            else if (!Regex.IsMatch(manifest.Id, @"^[a-z0-9\-]+$"))
                errors.Add($"[{fileName}] Invalid id '{manifest.Id}': must be lowercase letters, numbers, and hyphens only");

            if (string.IsNullOrWhiteSpace(manifest.Name))
                errors.Add($"[{fileName}] Missing required field: name");
            else if (manifest.Name.Length > 50)
                errors.Add($"[{fileName}] Mod name '{manifest.Name}' exceeds 50 character limit ({manifest.Name.Length} chars)");

            if (string.IsNullOrWhiteSpace(manifest.Version))
                errors.Add($"[{fileName}] Missing required field: version");
            else if (!Regex.IsMatch(manifest.Version, @"^\d+\.\d+\.\d+"))
                errors.Add($"[{fileName}] Invalid version '{manifest.Version}': must be semantic version (e.g., 1.0.0)");

            if (string.IsNullOrWhiteSpace(manifest.Author))
                errors.Add($"[{fileName}] Missing required field: author");

            if (string.IsNullOrWhiteSpace(manifest.Description))
                errors.Add($"[{fileName}] Missing required field: description");

            // Validate version constraints if present
            if (!string.IsNullOrEmpty(manifest.TerrariaVersion))
            {
                var constraint = VersionConstraint.Parse(manifest.TerrariaVersion);
                if (!constraint.IsValid)
                    errors.Add($"[{fileName}] Invalid terraria_version: {constraint.Error}");
            }

            if (!string.IsNullOrEmpty(manifest.FrameworkVersion))
            {
                var constraint = VersionConstraint.Parse(manifest.FrameworkVersion);
                if (!constraint.IsValid)
                    errors.Add($"[{fileName}] Invalid framework_version: {constraint.Error}");
            }

            // Validate keybinds
            foreach (var keybind in manifest.Keybinds)
            {
                if (string.IsNullOrWhiteSpace(keybind.Name))
                    errors.Add($"[{fileName}] Keybind '{keybind.Id}' is missing 'label' field");
                if (string.IsNullOrWhiteSpace(keybind.DefaultKey))
                    errors.Add($"[{fileName}] Keybind '{keybind.Id}' is missing 'default' field");
            }

            manifest.ValidationErrors.AddRange(errors);
            manifest.IsValid = errors.Count == 0;
        }

        private static string ExtractString(string json, string key)
        {
            // Pattern: "key": "value" or "key":"value"
            var pattern = $"\"{key}\"\\s*:\\s*\"([^\"\\\\]*(?:\\\\.[^\"\\\\]*)*)\"";
            var match = Regex.Match(json, pattern);
            if (match.Success)
            {
                return UnescapeJson(match.Groups[1].Value);
            }
            return null;
        }

        private static List<string> ExtractStringArray(string json, string key)
        {
            var result = new List<string>();

            // Pattern: "key": [...] or "key":[...]
            var pattern = $"\"{key}\"\\s*:\\s*\\[([^\\]]*)\\]";
            var match = Regex.Match(json, pattern);
            if (match.Success)
            {
                string arrayContent = match.Groups[1].Value;
                // Extract individual strings from array
                var stringPattern = "\"([^\"\\\\]*(?:\\\\.[^\"\\\\]*)*)\"";
                var stringMatches = Regex.Matches(arrayContent, stringPattern);
                foreach (Match stringMatch in stringMatches)
                {
                    result.Add(UnescapeJson(stringMatch.Groups[1].Value));
                }
            }

            return result;
        }

        private static string UnescapeJson(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            return value
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t");
        }

        private static string ExtractObject(string json, string key)
        {
            // Find "key": { and then extract the balanced braces
            string searchFor = $"\"{key}\"";
            int keyIndex = json.IndexOf(searchFor);
            if (keyIndex == -1) return null;

            // Find the opening brace after the key
            int colonIndex = json.IndexOf(':', keyIndex + searchFor.Length);
            if (colonIndex == -1) return null;

            int braceIndex = json.IndexOf('{', colonIndex);
            if (braceIndex == -1) return null;

            // Extract balanced braces, skipping characters inside strings
            int depth = 1;
            int i = braceIndex + 1;
            bool inString = false;

            while (i < json.Length && depth > 0)
            {
                char c = json[i];
                if (inString)
                {
                    if (c == '\\' && i + 1 < json.Length) { i++; } // skip escaped char
                    else if (c == '"') { inString = false; }
                }
                else
                {
                    if (c == '"') { inString = true; }
                    else if (c == '{') { depth++; }
                    else if (c == '}') { depth--; }
                }
                i++;
            }

            if (depth == 0)
            {
                return json.Substring(braceIndex, i - braceIndex);
            }

            return null;
        }

        private static List<KeybindDefinition> ExtractKeybinds(string json)
        {
            var result = new List<KeybindDefinition>();

            // Find "keybinds": [ ... ]
            string searchFor = "\"keybinds\"";
            int keyIndex = json.IndexOf(searchFor);
            if (keyIndex == -1) return result;

            int colonIndex = json.IndexOf(':', keyIndex + searchFor.Length);
            if (colonIndex == -1) return result;

            int bracketIndex = json.IndexOf('[', colonIndex);
            if (bracketIndex == -1) return result;

            // Extract balanced brackets, skipping characters inside strings
            int depth = 1;
            int i = bracketIndex + 1;
            bool inString = false;

            while (i < json.Length && depth > 0)
            {
                char c = json[i];
                if (inString)
                {
                    if (c == '\\' && i + 1 < json.Length) { i++; }
                    else if (c == '"') { inString = false; }
                }
                else
                {
                    if (c == '"') { inString = true; }
                    else if (c == '[') { depth++; }
                    else if (c == ']') { depth--; }
                }
                i++;
            }

            if (depth != 0) return result;

            string arrayContent = json.Substring(bracketIndex + 1, i - bracketIndex - 2);

            // Parse each keybind object using balanced brace extraction
            int objStart = 0;
            while ((objStart = arrayContent.IndexOf('{', objStart)) != -1)
            {
                // Find matching closing brace, respecting strings
                int objDepth = 1;
                int objPos = objStart + 1;
                bool objInString = false;

                while (objPos < arrayContent.Length && objDepth > 0)
                {
                    char c = arrayContent[objPos];
                    if (objInString)
                    {
                        if (c == '\\' && objPos + 1 < arrayContent.Length) { objPos++; }
                        else if (c == '"') { objInString = false; }
                    }
                    else
                    {
                        if (c == '"') { objInString = true; }
                        else if (c == '{') { objDepth++; }
                        else if (c == '}') { objDepth--; }
                    }
                    objPos++;
                }

                if (objDepth != 0) break;

                string objJson = arrayContent.Substring(objStart, objPos - objStart);

                var keybind = new KeybindDefinition
                {
                    Id = ExtractString(objJson, "id"),
                    Name = ExtractString(objJson, "label"),
                    Description = ExtractString(objJson, "description"),
                    DefaultKey = ExtractString(objJson, "default")
                };

                if (!string.IsNullOrEmpty(keybind.Id))
                {
                    result.Add(keybind);
                }

                objStart = objPos;
            }

            return result;
        }
    }
}
