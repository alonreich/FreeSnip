using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace freesnip.helpers;

/// <summary>
/// Immutable identity of the ORIGINAL interactive FreeSnip user, captured by the non-elevated
/// launcher BEFORE the "runas" UAC boundary. Under alternate-credential elevation the elevated
/// worker's Environment.SpecialFolder values and WindowsIdentity belong to the administrator who
/// approved the prompt, so every original-user path and the startup-task SID must come from here.
/// </summary>
internal sealed class UpgradeUserContext
{
    public const string ArgumentName = "--upgrade-context";
    internal const string ManifestFileName = "upgrade_context.txt";
    internal const string FormatHeader = "FreeSnipUpgradeContext/1";
    internal static readonly TimeSpan MaxManifestAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    public UpgradeUserContext(string userSid, string appData, string localAppData, bool hadElevatedStartupTask, DateTime capturedUtc)
    {
        UserSid = userSid ?? throw new ArgumentNullException(nameof(userSid));
        AppData = appData ?? throw new ArgumentNullException(nameof(appData));
        LocalAppData = localAppData ?? throw new ArgumentNullException(nameof(localAppData));
        HadElevatedStartupTask = hadElevatedStartupTask;
        CapturedUtc = capturedUtc;
    }

    public string UserSid { get; }
    public string AppData { get; }
    public string LocalAppData { get; }
    public bool HadElevatedStartupTask { get; }
    public DateTime CapturedUtc { get; }

    public string ConfigurationFolder => Path.Combine(AppData, "FreeSnip");
    public string CanonicalSettingsPath => Path.Combine(ConfigurationFolder, "freesnip.ini");
    public string UpgradeBackupsRoot => Path.Combine(LocalAppData, "FreeSnipUpgradeBackups");

    /// <summary>Legacy USER-PROFILE settings locations of the original user (never Program Files).</summary>
    public IReadOnlyList<string> GetLegacyUserProfileCandidates() => new[]
    {
        Path.Combine(ConfigurationFolder, "snapvox.ini"),
        Path.Combine(AppData, "SnapVox", "snapvox.ini"),
        Path.Combine(LocalAppData, "SnapVox", "snapvox.ini"),
        Path.Combine(LocalAppData, "FreeSnip", "freesnip.ini")
    };

    public static UpgradeUserContext CaptureCurrent(bool hadElevatedStartupTask)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("Cannot identify the current FreeSnip user.");
        return new UpgradeUserContext(
            sid,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            hadElevatedStartupTask,
            DateTime.UtcNow);
    }

    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.Append(FormatHeader).Append("\r\n");
        sb.Append("UserSid=").Append(UserSid).Append("\r\n");
        sb.Append("AppData=").Append(AppData).Append("\r\n");
        sb.Append("LocalAppData=").Append(LocalAppData).Append("\r\n");
        sb.Append("HadElevatedStartupTask=").Append(HadElevatedStartupTask ? "True" : "False").Append("\r\n");
        sb.Append("CapturedUtc=").Append(CapturedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append("\r\n");
        return sb.ToString();
    }

    public static bool TryParse(string text, DateTime nowUtc, out UpgradeUserContext context, out string error)
    {
        context = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "manifest is empty"; return false; }

        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        if (!string.Equals(lines[0].Trim().TrimStart('\uFEFF'), FormatHeader, StringComparison.Ordinal))
        {
            error = "unknown manifest format";
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) { error = "malformed manifest line"; return false; }
            string key = line.Substring(0, eq);
            if (values.ContainsKey(key)) { error = "duplicate manifest key " + key; return false; }
            values[key] = line.Substring(eq + 1);
        }

        if (!values.TryGetValue("UserSid", out string sid) || !IsValidSid(sid)) { error = "missing or invalid UserSid"; return false; }
        if (!values.TryGetValue("AppData", out string appData) || !IsValidProfileDirectory(appData)) { error = "missing or invalid AppData"; return false; }
        if (!values.TryGetValue("LocalAppData", out string localAppData) || !IsValidProfileDirectory(localAppData)) { error = "missing or invalid LocalAppData"; return false; }
        if (!values.TryGetValue("HadElevatedStartupTask", out string hadTaskText) || !bool.TryParse(hadTaskText, out bool hadTask)) { error = "missing or invalid HadElevatedStartupTask"; return false; }
        if (!values.TryGetValue("CapturedUtc", out string capturedText)
            || !DateTime.TryParseExact(capturedText, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime capturedParsed))
        {
            error = "missing or invalid CapturedUtc";
            return false;
        }
        DateTime capturedUtc = capturedParsed.ToUniversalTime();

        if (capturedUtc > nowUtc + MaxClockSkew || nowUtc - capturedUtc > MaxManifestAge)
        {
            error = "manifest is stale or from the future";
            return false;
        }

        context = new UpgradeUserContext(sid, appData, localAppData, hadTask, capturedUtc);
        return true;
    }

    public async Task<string> WriteManifestAsync(string directory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, ManifestFileName);
        byte[] data = Encoding.UTF8.GetBytes(Serialize());
        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await file.WriteAsync(data, ct).ConfigureAwait(false);
            file.Flush(flushToDisk: true);
        }
        return path;
    }

    /// <summary>
    /// Loads and validates a manifest. When <paramref name="expectedDirectory"/> is supplied the manifest must sit
    /// beside the worker executable (the per-session lifecycle staging folder created by the launcher).
    /// </summary>
    public static UpgradeUserContext LoadManifest(string path, string expectedDirectory, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Upgrade context path is not fully qualified.");

        string fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetFileName(fullPath), ManifestFileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Upgrade context file name is not recognised.");

        if (!string.IsNullOrWhiteSpace(expectedDirectory))
        {
            string actualDir = Path.GetFullPath(Path.GetDirectoryName(fullPath) ?? string.Empty).TrimEnd('\\');
            string expectedDir = Path.GetFullPath(expectedDirectory).TrimEnd('\\');
            if (!string.Equals(actualDir, expectedDir, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Upgrade context is not located in the installer staging folder.");
        }

        if (!File.Exists(fullPath)) throw new InvalidDataException("Upgrade context file is missing.");
        string text = File.ReadAllText(fullPath, Encoding.UTF8);
        if (!TryParse(text, nowUtc, out var context, out string error))
            throw new InvalidDataException("Upgrade context is invalid: " + error);
        return context;
    }

    public static string GetManifestArgument(string[] args)
    {
        if (args == null) return null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(ArgumentName, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return null;
    }

    internal static bool IsValidSid(string sid)
    {
        if (string.IsNullOrWhiteSpace(sid) || !sid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)) return false;
        try { _ = new SecurityIdentifier(sid); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool IsValidProfileDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        try { return Path.IsPathFullyQualified(path) && Directory.Exists(path); }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException) { return false; }
    }
}
