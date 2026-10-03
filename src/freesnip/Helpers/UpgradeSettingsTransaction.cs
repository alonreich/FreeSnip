using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace freesnip.helpers;

/// <summary>
/// KEEP-SETTINGS upgrade transaction for exactly ONE authoritative original-user configuration:
///   PrepareAsync  - select authoritative source (before any destructive cleanup) and create a SHA-256 verified backup
///   RestoreAsync  - atomically restore those exact bytes to &lt;original AppData&gt;\FreeSnip\freesnip.ini
///   VerifyAsync   - prove the restored file matches (SHA-256, or semantically when only RunAsAdministratorOnStartup was reconciled)
///   Commit        - delete the recovery backup; refuses unless VerifyAsync succeeded
/// Any failure before Commit leaves the recovery backup on disk.
/// </summary>
internal sealed class UpgradeSettingsTransaction
{
    private const string AdminStartupKey = "RunAsAdministratorOnStartup";
    private byte[] _expectedBytes;

    private UpgradeSettingsTransaction(UpgradeUserContext context, SettingsSelection selection)
    {
        Context = context;
        Selection = selection;
    }

    public UpgradeUserContext Context { get; }
    public SettingsSelection Selection { get; }
    public string BackupFolder { get; private set; }
    public string ExpectedSha256 { get; private set; }
    public bool SourceRequestsAdminStartup { get; private set; }
    public bool IsVerified { get; private set; }
    public bool IsCommitted { get; private set; }

    public static async Task<UpgradeSettingsTransaction> PrepareAsync(UpgradeUserContext context, System.Collections.Generic.IEnumerable<SettingsCandidate> candidates, CancellationToken ct = default)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        var selection = SettingsCandidateSelector.Select(candidates);
        var txn = new UpgradeSettingsTransaction(context, selection);
        if (selection.Selected == null) return txn;

        byte[] raw = await File.ReadAllBytesAsync(selection.Selected.Path, ct).ConfigureAwait(false);
        byte[] bytes = selection.Selected.Origin == SettingsCandidateOrigin.CanonicalUserProfile
            ? raw
            : SettingsCandidateSelector.MigrateLegacyBytes(raw);

        if (SettingsCandidateSelector.CountValidIniKeys(Encoding.UTF8.GetString(bytes)) == 0)
            throw new IOException("Authoritative settings changed while being backed up: " + selection.Selected.Path);

        var (folder, hash) = await UpgradeSettingsBackup.CreateForDestinationAsync(context.CanonicalSettingsPath, bytes, context.UpgradeBackupsRoot, ct).ConfigureAwait(false);
        txn._expectedBytes = bytes;
        txn.BackupFolder = folder;
        txn.ExpectedSha256 = hash;
        txn.SourceRequestsAdminStartup = SettingsCandidateSelector.ReadRunAsAdministrator(Encoding.UTF8.GetString(bytes)) == true;
        return txn;
    }

    public async Task RestoreAsync(CancellationToken ct = default)
    {
        if (BackupFolder == null) return;
        IsVerified = false;
        await UpgradeSettingsBackup.RestoreAsync(BackupFolder, new[] { Context.CanonicalSettingsPath }, ct).ConfigureAwait(false);
    }

    public async Task VerifyAsync(bool expectedRunAsAdministrator, CancellationToken ct = default)
    {
        if (BackupFolder == null) { IsVerified = true; return; }

        string path = Context.CanonicalSettingsPath;
        if (!File.Exists(path)) throw new IOException("Restored settings are missing: " + path);
        byte[] actual = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (string.Equals(Convert.ToHexString(SHA256.HashData(actual)), ExpectedSha256, StringComparison.Ordinal))
        {
            IsVerified = true;
            return;
        }

        var expected = SettingsCandidateSelector.ParseIni(Encoding.UTF8.GetString(_expectedBytes));
        var restored = SettingsCandidateSelector.ParseIni(Encoding.UTF8.GetString(actual));
        static bool IsAdminKey(string flatKey) => flatKey.EndsWith("\u001F" + AdminStartupKey, StringComparison.OrdinalIgnoreCase);

        foreach (var kv in expected)
        {
            if (IsAdminKey(kv.Key)) continue;
            if (!restored.TryGetValue(kv.Key, out string value) || !string.Equals(value, kv.Value, StringComparison.Ordinal))
                throw new IOException("Restored settings verification failed for '" + kv.Key.Replace('\u001F', '.') + "'.");
        }
        foreach (var key in restored.Keys)
        {
            if (!IsAdminKey(key) && !expected.ContainsKey(key))
                throw new IOException("Restored settings contain unexpected key '" + key.Replace('\u001F', '.') + "'.");
        }
        if (SettingsCandidateSelector.ReadRunAsAdministrator(Encoding.UTF8.GetString(actual)) != expectedRunAsAdministrator)
            throw new IOException("Restored administrator-startup flag does not match the configured startup mode.");

        IsVerified = true;
    }

    public void Commit()
    {
        if (BackupFolder == null) { IsCommitted = true; return; }
        if (!IsVerified)
            throw new InvalidOperationException("Settings restore was not verified. Recovery backup retained at: " + BackupFolder);

        try
        {
            if (Directory.Exists(BackupFolder))
            {
                Directory.Delete(BackupFolder, true);
                string parent = Path.GetDirectoryName(BackupFolder);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                    Directory.Delete(parent, false);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            BootstrapDebug.Log("UpgradeSettingsTransaction: verified recovery copy could not be deleted :: " + BackupFolder + " :: " + ex.Message);
        }
        IsCommitted = true;
    }
}
