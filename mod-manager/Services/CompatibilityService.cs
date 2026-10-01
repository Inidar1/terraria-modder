using System.Diagnostics;
using System.Text.RegularExpressions;
using TerrariaModManager.Models;

namespace TerrariaModManager.Services;

public sealed record CompatibilityWarning(string Code, string Message);

/// <summary>
/// Explains known compatibility mismatches before install. Its results are advisory:
/// users may continue with any mod after seeing the warnings.
/// </summary>
public sealed class CompatibilityService
{
    private readonly ModStateService _modState;

    public CompatibilityService(ModStateService modState) => _modState = modState;

    public IReadOnlyList<CompatibilityWarning> Analyze(ModManifest manifest, string terrariaPath)
    {
        var warnings = new List<CompatibilityWarning>();
        var installed = _modState.ScanInstalledMods(terrariaPath);
        var enabledIds = installed.Where(mod => mod.IsEnabled)
            .Select(mod => mod.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installedIds = installed.Select(mod => mod.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        CheckConstraint("terraria-version", "Terraria", manifest.TerrariaVersion,
            ReadTerrariaVersion(terrariaPath), warnings);
        CheckConstraint("framework-version", "TerrariaModder Core", manifest.FrameworkVersion,
            _modState.GetCoreInfo(terrariaPath).CoreVersion, warnings);

        foreach (var dependency in manifest.Dependencies ?? [])
        {
            if (!installedIds.Contains(dependency))
                warnings.Add(new("missing-dependency", $"Required mod '{dependency}' is not installed."));
            else if (!enabledIds.Contains(dependency))
                warnings.Add(new("disabled-dependency", $"Required mod '{dependency}' is installed but disabled."));
        }

        foreach (var incompatible in manifest.IncompatibleWith ?? [])
        {
            if (enabledIds.Contains(incompatible) &&
                !string.Equals(incompatible, manifest.Id, StringComparison.OrdinalIgnoreCase))
                warnings.Add(new("incompatible-mod", $"'{incompatible}' is enabled and this mod declares it incompatible."));
        }

        return warnings;
    }

    private static void CheckConstraint(
        string code,
        string label,
        string? constraintText,
        string? actualVersion,
        List<CompatibilityWarning> warnings)
    {
        if (string.IsNullOrWhiteSpace(constraintText)) return;
        var constraint = VersionConstraint.TryParse(constraintText);
        if (constraint == null)
        {
            warnings.Add(new(code, $"{label} requirement '{constraintText}' could not be interpreted."));
            return;
        }
        if (string.IsNullOrWhiteSpace(actualVersion))
        {
            warnings.Add(new(code, $"Requires {label} {constraintText}, but the installed version could not be determined."));
            return;
        }
        if (!constraint.IsSatisfiedBy(actualVersion))
            warnings.Add(new(code, $"Requires {label} {constraintText}; detected {actualVersion}."));
    }

    private static string? ReadTerrariaVersion(string terrariaPath)
    {
        var executable = Path.Combine(terrariaPath, "Terraria.exe");
        if (!File.Exists(executable)) return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executable);
            return info.ProductVersion ?? info.FileVersion;
        }
        catch { return null; }
    }

    internal sealed class VersionConstraint
    {
        private readonly string _operator;
        private readonly Version _lower;
        private readonly Version? _upper;

        private VersionConstraint(string op, Version lower, Version? upper = null)
        {
            _operator = op;
            _lower = lower;
            _upper = upper;
        }

        public static VersionConstraint? TryParse(string text)
        {
            text = text.Trim();
            if (text.Contains(','))
            {
                var parts = text.Split(',');
                if (parts.Length != 2) return null;
                var lower = TryParse(parts[0]);
                var upper = TryParse(parts[1]);
                if (lower == null || upper == null || lower._operator is not (">=" or ">") ||
                    upper._operator is not ("<" or "<=")) return null;
                return new VersionConstraint("range", lower._lower, upper._lower);
            }

            var match = Regex.Match(text, @"^(>=|>|<=|<|=|~|\^)?(\d+(?:\.\d+(?:\.\d+)?)?)$");
            if (!match.Success || !TryVersion(match.Groups[2].Value, out var version)) return null;
            var op = string.IsNullOrEmpty(match.Groups[1].Value) ? ">=" : match.Groups[1].Value;
            if (op == "~") return new VersionConstraint(">=", version, new Version(version.Major, version.Minor + 1, 0));
            if (op == "^") return new VersionConstraint(">=", version, new Version(version.Major + 1, 0, 0));
            return new VersionConstraint(op, version);
        }

        public bool IsSatisfiedBy(string text)
        {
            var clean = text.Split('+', 2)[0].Split('-', 2)[0].TrimStart('v', 'V');
            if (!TryVersion(clean, out var actual)) return false;
            var lowerMatches = _operator switch
            {
                ">=" => actual >= _lower,
                ">" => actual > _lower,
                "<=" => actual <= _lower,
                "<" => actual < _lower,
                "=" => actual == _lower,
                "range" => actual >= _lower,
                _ => false
            };
            return lowerMatches && (_upper == null || actual < _upper);
        }

        private static bool TryVersion(string text, out Version version)
        {
            var parts = text.Split('.');
            if (parts.Length == 1) text += ".0.0";
            else if (parts.Length == 2) text += ".0";
            return Version.TryParse(text, out version!);
        }
    }
}
