using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using freesnip.foundation.core;

namespace freesnip.helpers;

internal enum SettingsCandidateOrigin
{
    CanonicalUserProfile = 0,
    LegacyUserProfile = 1,
    ProgramFilesTemplate = 2
}

internal sealed record SettingsCandidate(string Path, SettingsCandidateOrigin Origin);

internal sealed record SettingsCandidateEvaluation(
    string Path,
    SettingsCandidateOrigin Origin,
    int Order,
    bool Exists,
    int ValidKeyCount,
    bool HasRecognizedSection,
    int NonDefaultValueCount,
    DateTime LastWriteTimeUtc,
    string Rejection)
{
    public bool Eligible => Exists && Rejection == null && ValidKeyCount > 0;

    public override string ToString() =>
        $"{Origin}:'{Path}' exists={Exists} keys={ValidKeyCount} section={HasRecognizedSection} nonDefault={NonDefaultValueCount} lastWrite={LastWriteTimeUtc:O}" +
        (Rejection != null ? " rejected=" + Rejection : string.Empty);
}

internal sealed record SettingsSelection(SettingsCandidateEvaluation Selected, IReadOnlyList<SettingsCandidateEvaluation> Evaluated, string Reason);

/// <summary>
/// Deterministic authoritative-settings selection (project_structure Sections 79/89).
/// Rules:
///  1. The canonical user-profile freesnip.ini wins whenever it holds at least one genuine key=value entry.
///  2. Files with zero valid key=value entries (empty/template stubs, header-only files) are never eligible.
///  3. Without a valid canonical file, legacy USER-PROFILE candidates are evaluated before any Program Files file.
///  4. Program Files files are only used when no user-profile candidate is eligible.
///  5. Within one tier: ValidKeyCount, recognised [Core]/[FreeSnip]/[SnapVox] section, count of non-default
///     values, LastWriteTimeUtc (tie-breaker only), then candidate order.
/// </summary>
internal static class SettingsCandidateSelector
{
    private static readonly string[] RecognizedSections = { "Core", "FreeSnip", "SnapVox" };
    private const string AdminStartupKey = "RunAsAdministratorOnStartup";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> CoreDefaults = new(BuildCoreDefaults);

    public static int CountValidIniKeys(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int count = 0;
        foreach (string raw in text.Split('\n'))
        {
            if (TryParseKeyValue(raw, out _, out _)) count++;
        }
        return count;
    }

    public static SettingsSelection Select(IEnumerable<SettingsCandidate> candidates)
    {
        var evaluated = new List<SettingsCandidateEvaluation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int order = 0;
        foreach (var candidate in candidates ?? Array.Empty<SettingsCandidate>())
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.Path)) continue;
            string full;
            try { full = System.IO.Path.GetFullPath(candidate.Path); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { continue; }
            if (!seen.Add(full)) continue;
            evaluated.Add(Evaluate(full, candidate.Origin, order++));
        }

        foreach (SettingsCandidateOrigin tier in new[] { SettingsCandidateOrigin.CanonicalUserProfile, SettingsCandidateOrigin.LegacyUserProfile, SettingsCandidateOrigin.ProgramFilesTemplate })
        {
            var winner = evaluated
                .Where(e => e.Origin == tier && e.Eligible)
                .OrderByDescending(e => e.ValidKeyCount)
                .ThenByDescending(e => e.HasRecognizedSection)
                .ThenByDescending(e => e.NonDefaultValueCount)
                .ThenByDescending(e => e.LastWriteTimeUtc)
                .ThenBy(e => e.Order)
                .FirstOrDefault();
            if (winner == null) continue;

            string reason = tier switch
            {
                SettingsCandidateOrigin.CanonicalUserProfile => $"canonical original-user configuration is authoritative ({winner.ValidKeyCount} valid keys)",
                SettingsCandidateOrigin.LegacyUserProfile => $"canonical configuration absent or empty; highest-scoring legacy user-profile configuration ({winner.ValidKeyCount} keys, section={winner.HasRecognizedSection}, nonDefault={winner.NonDefaultValueCount})",
                _ => $"no eligible user-profile configuration; highest-scoring install-folder configuration ({winner.ValidKeyCount} keys, section={winner.HasRecognizedSection}, nonDefault={winner.NonDefaultValueCount})"
            };
            return new SettingsSelection(winner, evaluated, reason);
        }

        return new SettingsSelection(null, evaluated, "no candidate contains any valid key=value configuration entry");
    }

    private static SettingsCandidateEvaluation Evaluate(string path, SettingsCandidateOrigin origin, int order)
    {
        if (!File.Exists(path))
            return new SettingsCandidateEvaluation(path, origin, order, false, 0, false, 0, DateTime.MinValue, null);

        try
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            DateTime lastWrite = File.GetLastWriteTimeUtc(path);
            int keys = CountValidIniKeys(text);
            return new SettingsCandidateEvaluation(
                path, origin, order, true, keys, HasRecognizedSection(text), CountNonDefaultValues(text), lastWrite,
                keys == 0 ? "no valid key=value entries (empty or template stub)" : null);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return new SettingsCandidateEvaluation(path, origin, order, true, 0, false, 0, DateTime.MinValue, "unreadable: " + ex.Message);
        }
    }

    public static bool HasRecognizedSection(string text)
    {
        foreach (var (section, _, _) in EnumerateEntries(text, includeHeaders: true))
        {
            if (RecognizedSections.Any(s => string.Equals(s, section, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    public static int CountNonDefaultValues(string text)
    {
        var defaults = CoreDefaults.Value;
        if (defaults.Count == 0) return 0;
        int count = 0;
        foreach (var (section, key, value) in EnumerateEntries(text, includeHeaders: false))
        {
            if (!RecognizedSections.Any(s => string.Equals(s, section, StringComparison.OrdinalIgnoreCase))) continue;
            if (defaults.TryGetValue(key, out string def) && !string.Equals(def?.Trim(), value?.Trim(), StringComparison.OrdinalIgnoreCase)) count++;
        }
        return count;
    }

    /// <summary>Flattened "section\u001Fkey" -> value map for semantic comparison.</summary>
    public static Dictionary<string, string> ParseIni(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (section, key, value) in EnumerateEntries(text, includeHeaders: false))
        {
            map[section + "\u001F" + key] = value;
        }
        return map;
    }

    public static bool? ReadRunAsAdministrator(string text)
    {
        bool? result = null;
        foreach (var (_, key, value) in EnumerateEntries(text, includeHeaders: false))
        {
            if (string.Equals(key, AdminStartupKey, StringComparison.OrdinalIgnoreCase) && bool.TryParse(value?.Trim(), out bool parsed))
                result = parsed;
        }
        return result;
    }

    public static string MigrateLegacyContent(string text)
        => text?.Replace("[SnapVox]", "[Core]", StringComparison.OrdinalIgnoreCase);

    public static byte[] MigrateLegacyBytes(byte[] raw)
    {
        string text = Encoding.UTF8.GetString(raw);
        if (text.IndexOf("[SnapVox]", StringComparison.OrdinalIgnoreCase) < 0) return raw;
        return Encoding.UTF8.GetBytes(MigrateLegacyContent(text));
    }

    /// <summary>Sets one key inside <paramref name="section"/> while leaving every other byte of the file untouched.</summary>
    public static string SetIniValue(string text, string section, string key, string value)
    {
        text ??= string.Empty;
        string nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = new List<string>(text.Split('\n'));
        string current = string.Empty;
        int headerIndex = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            string trimmed = lines[i].Trim().TrimStart('\uFEFF');
            if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                current = trimmed.Substring(1, trimmed.Length - 2).Trim();
                if (headerIndex < 0 && string.Equals(current, section, StringComparison.OrdinalIgnoreCase)) headerIndex = i;
                continue;
            }
            if (!string.Equals(current, section, StringComparison.OrdinalIgnoreCase)) continue;
            if (TryParseKeyValue(lines[i], out string k, out _) && string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                string cr = lines[i].EndsWith("\r", StringComparison.Ordinal) ? "\r" : string.Empty;
                lines[i] = key + "=" + value + cr;
                return string.Join("\n", lines);
            }
        }

        if (headerIndex >= 0)
        {
            string cr = nl == "\r\n" ? "\r" : string.Empty;
            lines.Insert(headerIndex + 1, key + "=" + value + cr);
            return string.Join("\n", lines);
        }

        string prefix = text.Length == 0 || text.EndsWith("\n", StringComparison.Ordinal) ? text : text + nl;
        return prefix + "[" + section + "]" + nl + key + "=" + value + nl;
    }

    private static IEnumerable<(string Section, string Key, string Value)> EnumerateEntries(string text, bool includeHeaders)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        string section = string.Empty;
        foreach (string raw in text.Split('\n'))
        {
            string trimmed = raw.Trim().TrimStart('\uFEFF').Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal) && trimmed.Length >= 2)
            {
                section = trimmed.Substring(1, trimmed.Length - 2).Trim();
                if (includeHeaders) yield return (section, null, null);
                continue;
            }
            if (TryParseKeyValue(raw, out string key, out string value)) yield return (section, key, value);
        }
    }

    private static bool TryParseKeyValue(string raw, out string key, out string value)
    {
        key = null;
        value = null;
        if (raw == null) return false;
        string t = raw.Trim().TrimStart('\uFEFF').Trim();
        if (t.Length == 0 || t[0] == ';' || t[0] == '#' || t[0] == '[') return false;
        int eq = t.IndexOf('=');
        if (eq <= 0) return false;
        key = t.Substring(0, eq).Trim();
        if (key.Length == 0) { key = null; return false; }
        value = t.Substring(eq + 1).Trim();
        return true;
    }

    private static IReadOnlyDictionary<string, string> BuildCoreDefaults()
    {
        try
        {
            var core = new CoreConfiguration();
            core.Fill(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            using var writer = new StringWriter();
            core.Write(writer, onlyProperties: false);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, key, value) in EnumerateEntries(writer.ToString(), includeHeaders: false)) map[key] = value;
            return map;
        }
        catch (Exception ex)
        {
            BootstrapDebug.Log("SettingsCandidateSelector: default map unavailable :: " + ex.Message);
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
