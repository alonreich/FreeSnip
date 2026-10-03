using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace freesnip.helpers;

internal static class UpgradeSettingsBackup
{
    public static async Task<string> CreateAsync(IEnumerable<string> candidates, string backupRoot, CancellationToken ct = default)
    {
        string folder = Path.Combine(backupRoot, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N"));
        var manifest = new List<string>();
        foreach (string destination in candidates.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(destination, ct).ConfigureAwait(false); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            Directory.CreateDirectory(folder);
            string name = "settings_" + manifest.Count + ".ini";
            string copy = Path.Combine(folder, name);
            await WriteDurablyAsync(copy, bytes, ct).ConfigureAwait(false);
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (await HashAsync(copy, ct).ConfigureAwait(false) != hash)
                throw new IOException("Settings backup verification failed: " + copy);
            manifest.Add(destination + "\t" + name + "\t" + hash);
        }
        if (manifest.Count == 0) return null;
        await WriteDurablyAsync(Path.Combine(folder, "manifest.txt"),
            System.Text.Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, manifest)), ct).ConfigureAwait(false);
        return folder;
    }

    /// <summary>
    /// Durably backs up exactly one authoritative configuration payload, bound to exactly one restore destination
    /// (the original user's canonical freesnip.ini). Uses the same verified manifest format as <see cref="CreateAsync"/>.
    /// </summary>
    public static async Task<(string Folder, string Sha256)> CreateForDestinationAsync(string destination, byte[] bytes, string backupRoot, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(destination)) throw new ArgumentException("A restore destination is required.", nameof(destination));
        if (bytes == null) throw new ArgumentNullException(nameof(bytes));
        ct.ThrowIfCancellationRequested();

        string folder = Path.Combine(backupRoot, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        const string name = "settings_0.ini";
        string copy = Path.Combine(folder, name);
        await WriteDurablyAsync(copy, bytes, ct).ConfigureAwait(false);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (await HashAsync(copy, ct).ConfigureAwait(false) != hash)
            throw new IOException("Settings backup verification failed: " + copy);
        await WriteDurablyAsync(Path.Combine(folder, "manifest.txt"),
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(destination) + "\t" + name + "\t" + hash), ct).ConfigureAwait(false);
        return (folder, hash);
    }

    internal static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
        => await HashAsync(path, ct).ConfigureAwait(false);

    public static async Task RestoreAsync(string folder, IEnumerable<string> allowedDestinations, CancellationToken ct = default)
    {
        var allowed = allowedDestinations.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] lines = await File.ReadAllLinesAsync(Path.Combine(folder, "manifest.txt"), ct).ConfigureAwait(false);
        if (lines.Length == 0) throw new IOException("The settings recovery manifest is empty.");
        var entries = new List<(string Destination, string Copy, string Hash)>();
        foreach (string line in lines)
        {
            string[] parts = line.Split('\t');
            if (parts.Length != 3 || !allowed.Contains(parts[0]) || Path.GetFileName(parts[1]) != parts[1])
                throw new IOException("Invalid settings recovery manifest.");
            string copy = Path.Combine(folder, parts[1]);
            if (await HashAsync(copy, ct).ConfigureAwait(false) != parts[2])
                throw new IOException("Settings backup is missing or damaged: " + copy);
            entries.Add((parts[0], copy, parts[2]));
        }
        foreach (var entry in entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(entry.Destination));
            string temporary = entry.Destination + ".restore-" + Guid.NewGuid().ToString("N");
            try
            {
                await WriteDurablyAsync(temporary, await File.ReadAllBytesAsync(entry.Copy, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                if (await HashAsync(temporary, ct).ConfigureAwait(false) != entry.Hash)
                    throw new IOException("Restored settings verification failed.");
                File.Move(temporary, entry.Destination, overwrite: true);
                if (await HashAsync(entry.Destination, ct).ConfigureAwait(false) != entry.Hash)
                    throw new IOException("Restored settings verification failed: " + entry.Destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
        => Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false)));

    private static async Task WriteDurablyAsync(string path, byte[] data, CancellationToken ct)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await file.WriteAsync(data, ct).ConfigureAwait(false);
        file.Flush(flushToDisk: true);
    }
}
