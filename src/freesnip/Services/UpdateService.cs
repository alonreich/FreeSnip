using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using freesnip.forms;
using freesnip.foundation.core;
using freesnip.foundation.core.AvaloniaShims;
using freesnip.foundation.IniFile;
using freesnip.helpers;
using freesnip.native;

namespace freesnip.services;

public static class UpdateService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/alonreich/FreeSnip/releases/latest";
    private static readonly string[] AllowedAssetHosts =
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com"
    };

    private const string UserAgent = "FreeSnip-Updater";
    private const string DownloadFolderRootName = "FreeSnip_AutoUpdate";
    private const string LastCheckFile = "update_last_check_utc.txt";
    private const string SkippedTagFile = "update_skipped_tag.txt";

    private static readonly TimeSpan StartupGracePeriod = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinimumIntervalBetweenChecks = TimeSpan.FromHours(24);
    private static readonly TimeSpan MinimumManualInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    private static readonly HttpClient Http = CreateHttpClient();
    private static int _checkInProgress;

    public sealed record UpdateRelease(
        string Tag,
        string DownloadUrl,
        string Sha256Hex,
        long Size,
        string ReleaseNotes);

    public static string GetExpectedAssetName()
    {
#if USE_TESSERACT
        return "FreeSnip_tesseract.exe";
#else
        try
        {
            string exe = RuntimePathHelper.ExecutablePath;
            if (!string.IsNullOrEmpty(exe) && exe.Contains("tesseract", StringComparison.OrdinalIgnoreCase))
            {
                return "FreeSnip_tesseract.exe";
            }
        }
        catch { }
        return "FreeSnip.exe";
#endif
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = DownloadTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task RunStartupCheckAsync(Window owner = null)
    {
        try
        {
            var config = IniConfig.GetIniSection<CoreConfiguration>();
            if (config != null && !config.AutoUpdateChecks)
            {
                return;
            }

            await Task.Delay(StartupGracePeriod).ConfigureAwait(false);

            if (!ThrottlePermitsCheck(MinimumIntervalBetweenChecks))
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _checkInProgress, 1, 0) != 0)
            {
                return;
            }

            try
            {
                UpdateRelease release = await QueryLatestReleaseAsync().ConfigureAwait(false);
                if (release == null) return;

                if (!RuntimePathHelper.TryParseVersion(release.Tag, out Version remote) ||
                    !RuntimePathHelper.TryParseVersion(RuntimePathHelper.ProductVersion, out Version local))
                {
                    return;
                }

                if (remote.CompareTo(local) <= 0)
                {
                    return;
                }

                string skipped = ReadStateFile(SkippedTagFile).Trim();
                if (string.Equals(skipped, release.Tag, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                UpdateChoice choice = await Dispatcher.UIThread.InvokeAsync(() =>
                    UpdateAvailableWindow.AskAsync(owner, local, release.Tag, release.ReleaseNotes));

                switch (choice)
                {
                    case UpdateChoice.SkipThisVersion:
                        WriteStateFile(SkippedTagFile, release.Tag);
                        break;

                    case UpdateChoice.NeverTellMeAgain:
                        if (config != null)
                        {
                            config.AutoUpdateChecks = false;
                            IniConfig.Save();
                        }
                        break;

                    case UpdateChoice.UpdateNow:
                        await DownloadVerifyLaunchAsync(owner, release).ConfigureAwait(false);
                        break;
                }
            }
            finally
            {
                _ = Interlocked.Exchange(ref _checkInProgress, 0);
            }
        }
        catch { }
    }

    public static async Task CheckManualAsync(Window owner = null, Action<string> statusCallback = null)
    {
        if (Interlocked.CompareExchange(ref _checkInProgress, 1, 0) != 0)
        {
            statusCallback?.Invoke("An update check is already in progress...");
            return;
        }

        try
        {
            statusCallback?.Invoke("Checking GitHub for updates...");

            UpdateRelease release = await QueryLatestReleaseAsync().ConfigureAwait(false);
            if (release == null)
            {
                statusCallback?.Invoke("Could not retrieve update information from GitHub.");
                StartupTaskHelper.ShowForegroundMessageBox(
                    "Could not retrieve update information from GitHub.\r\nPlease check your network connection and try again.",
                    "Update Check Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (!RuntimePathHelper.TryParseVersion(release.Tag, out Version remote) ||
                !RuntimePathHelper.TryParseVersion(RuntimePathHelper.ProductVersion, out Version local))
            {
                statusCallback?.Invoke($"Could not compare versions ({release.Tag}).");
                StartupTaskHelper.ShowForegroundMessageBox(
                    $"Could not determine version compatibility (installed v{RuntimePathHelper.ProductVersion} vs release {release.Tag}).",
                    "Update Check Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (remote.CompareTo(local) <= 0)
            {
                statusCallback?.Invoke($"Up to date (v{local}).");
                StartupTaskHelper.ShowForegroundMessageBox(
                    $"You are already running the latest version of FreeSnip (v{local}).",
                    "FreeSnip is Up to Date",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            statusCallback?.Invoke($"Update available: {release.Tag}");

            UpdateChoice choice = await Dispatcher.UIThread.InvokeAsync(() =>
                UpdateAvailableWindow.AskAsync(owner, local, release.Tag, release.ReleaseNotes));

            switch (choice)
            {
                case UpdateChoice.SkipThisVersion:
                    WriteStateFile(SkippedTagFile, release.Tag);
                    statusCallback?.Invoke($"Skipped {release.Tag}");
                    break;

                case UpdateChoice.NeverTellMeAgain:
                    var config = IniConfig.GetIniSection<CoreConfiguration>();
                    if (config != null)
                    {
                        config.AutoUpdateChecks = false;
                        IniConfig.Save();
                    }
                    statusCallback?.Invoke("Auto updates disabled in Settings.");
                    break;

                case UpdateChoice.UpdateNow:
                    statusCallback?.Invoke("Starting download...");
                    await DownloadVerifyLaunchAsync(owner, release).ConfigureAwait(false);
                    break;

                case UpdateChoice.NotNow:
                case UpdateChoice.Dismissed:
                    statusCallback?.Invoke("Update postponed.");
                    break;
            }
        }
        catch (Exception ex)
        {
            statusCallback?.Invoke($"Check failed: {ex.Message}");
            StartupTaskHelper.ShowForegroundMessageBox(
                $"Update check failed: {ex.Message}",
                "Update Check Failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            _ = Interlocked.Exchange(ref _checkInProgress, 0);
        }
    }

    private static bool ThrottlePermitsCheck(TimeSpan interval)
    {
        try
        {
            string last = ReadStateFile(LastCheckFile);
            if (DateTime.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime when) &&
                DateTime.UtcNow - when < interval)
            {
                return false;
            }

            WriteStateFile(LastCheckFile, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            return true;
        }
        catch
        {
            return true;
        }
    }

    private static string ReadStateFile(string fileName)
    {
        try
        {
            string path = Path.Combine(DeploymentFootprint.LocalAppDataFolder, fileName);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        catch { }
        return string.Empty;
    }

    private static void WriteStateFile(string fileName, string text)
    {
        try
        {
            Directory.CreateDirectory(DeploymentFootprint.LocalAppDataFolder);
            string path = Path.Combine(DeploymentFootprint.LocalAppDataFolder, fileName);
            File.WriteAllText(path, text);
        }
        catch { }
    }

    private static async Task<UpdateRelease> QueryLatestReleaseAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(ProbeTimeout);
            using var response = await Http.GetAsync(LatestReleaseApiUrl, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var root = JsonNode.Parse(json)?.AsObject();

            string tag = root?["tag_name"]?.GetValue<string>();
            string releaseNotes = root?["body"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(tag)) return null;

            if (root?["draft"]?.GetValue<bool>() == true || root?["prerelease"]?.GetValue<bool>() == true)
            {
                return null;
            }

            string expectedAsset = GetExpectedAssetName();
            JsonNode asset = null;

            foreach (JsonNode candidate in root?["assets"]?.AsArray() ?? [])
            {
                string name = candidate?["name"]?.GetValue<string>();
                if (name != null && name.Equals(expectedAsset, StringComparison.OrdinalIgnoreCase))
                {
                    asset = candidate;
                    break;
                }
            }

            // Fallback to FreeSnip.exe if specific asset wasn't found
            if (asset == null)
            {
                foreach (JsonNode candidate in root?["assets"]?.AsArray() ?? [])
                {
                    string name = candidate?["name"]?.GetValue<string>();
                    if (name != null && name.Equals("FreeSnip.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        asset = candidate;
                        break;
                    }
                }
            }

            if (asset == null) return null;

            string url = asset["browser_download_url"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(url) || !IsAllowedAssetUrl(url)) return null;

            if (!TrySanitizeTagForPath(tag, out _)) return null;

            string digest = asset["digest"]?.GetValue<string>();
            string sha256 = null;
            if (!string.IsNullOrEmpty(digest))
            {
                int colon = digest.IndexOf(':');
                sha256 = colon >= 0 ? digest[(colon + 1)..].ToLowerInvariant() : digest.ToLowerInvariant();
            }

            // Fallback: extract SHA-256 from release notes body if digest not populated by GitHub API
            if (string.IsNullOrEmpty(sha256) && !string.IsNullOrEmpty(releaseNotes))
            {
                string pattern = expectedAsset.Replace(".", "\\.") + @"\s+SHA256\s+([0-9a-fA-F]{64})";
                var match = Regex.Match(releaseNotes, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    sha256 = match.Groups[1].Value.ToLowerInvariant();
                }
                else
                {
                    var fallbackMatch = Regex.Match(releaseNotes, @"[0-9a-fA-F]{64}");
                    if (fallbackMatch.Success) sha256 = fallbackMatch.Value.ToLowerInvariant();
                }
            }

            long size = asset["size"]?.GetValue<long>() ?? 0;
            return new UpdateRelease(tag, url, sha256, size, releaseNotes);
        }
        catch
        {
            return null;
        }
    }

    private static async Task DownloadVerifyLaunchAsync(Window owner, UpdateRelease release)
    {
        PurgeOldDownloadFolders();

        if (!TrySanitizeTagForPath(release.Tag, out string tagFolderName))
        {
            throw new InvalidOperationException($"Invalid release tag format: {release.Tag}");
        }

        string downloadRoot = Path.Combine(Path.GetTempPath(), DownloadFolderRootName);
        string folder = Path.Combine(downloadRoot, tagFolderName, Guid.NewGuid().ToString("N"));
        string targetFileName = GetExpectedAssetName();
        string finalPath = Path.Combine(folder, targetFileName);
        string partPath = finalPath + ".part";
        Directory.CreateDirectory(folder);

        using var cts = new CancellationTokenSource();
        UpdateDownloadWindow progressWindow = null;
        Task dialogTask = Task.CompletedTask;
        var dialogShown = new TaskCompletionSource<object>();

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                progressWindow = new UpdateDownloadWindow();
                progressWindow.CancelRequested += () => cts.Cancel();
                if (owner != null && owner.IsVisible)
                {
                    dialogTask = progressWindow.ShowDialog(owner);
                }
                else
                {
                    progressWindow.Show();
                    dialogTask = Task.CompletedTask;
                }
                dialogShown.SetResult(null);
            }
            catch (Exception ex)
            {
                dialogShown.SetException(ex);
            }
        });

        await dialogShown.Task.ConfigureAwait(false);

        try
        {
            using var response = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? release.Size;

            await using Stream source = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            await using var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024);
            byte[] buffer = new byte[1024 * 1024];
            long copied = 0;
            DateTime lastReport = DateTime.MinValue;

            while (true)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                readCts.CancelAfter(TimeSpan.FromSeconds(45));

                int read = await source.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
                if (read <= 0) break;

                await target.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                copied += read;
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 150)
                {
                    lastReport = DateTime.UtcNow;
                    ReportDownloadProgress(progressWindow, copied, total);
                }
            }

            await target.FlushAsync(cts.Token).ConfigureAwait(false);
            await target.DisposeAsync().ConfigureAwait(false);
            ReportDownloadProgress(progressWindow, copied, total > 0 ? total : copied);

            if (!string.IsNullOrWhiteSpace(release.Sha256Hex))
            {
                string actualHash;
                await using (FileStream verifyStream = File.OpenRead(partPath))
                {
                    actualHash = Convert.ToHexString(SHA256.HashData(verifyStream)).ToLowerInvariant();
                }

                if (!string.Equals(actualHash, release.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteFile(partPath);
                    throw new InvalidOperationException("Downloaded update failed SHA-256 fingerprint verification. The download was discarded.");
                }
            }

            File.Move(partPath, finalPath, overwrite: true);

            cts.Token.ThrowIfCancellationRequested();

            Dispatcher.UIThread.Post(() => progressWindow?.MarkHandoffToInstaller());

            var installer = new ProcessStartInfo(finalPath)
            {
                UseShellExecute = true
            };
            installer.ArgumentList.Add("--install");
            installer.ArgumentList.Add("--auto-update");
            installer.ArgumentList.Add("--wait-pid");
            installer.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

            Process.Start(installer);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                progressWindow?.Close();
                StartupTaskHelper.ShowForegroundMessageBox(
                    "Your update is ready.\r\n\r\nClose FreeSnip when you are ready; it will automatically install and restart with your settings preserved.",
                    "FreeSnip Update Ready",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            });
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(partPath);
            TryDeleteFile(finalPath);
        }
        catch (Exception ex)
        {
            TryDeleteFile(partPath);
            TryDeleteFile(finalPath);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StartupTaskHelper.ShowForegroundMessageBox(
                    $"The update could not be downloaded.\r\n\r\nReason: {ex.Message}\r\n\r\nYour current version was not changed.",
                    "Update Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            });
        }
        finally
        {
            Dispatcher.UIThread.Post(() => { try { progressWindow?.Close(); } catch { } });
        }

        await dialogTask.ConfigureAwait(false);
    }

    private static void ReportDownloadProgress(UpdateDownloadWindow window, long copied, long total)
    {
        if (window == null) return;
        double fraction = total > 0 ? Math.Clamp((double)copied / total, 0, 1) : 0;
        string text = total > 0
            ? $"{fraction:P0}  —  {copied / (1024 * 1024)} MB of {total / (1024 * 1024)} MB"
            : $"{copied / (1024 * 1024)} MB downloaded";
        Dispatcher.UIThread.Post(() => window.SetProgress(fraction, text));
    }

    private static bool IsAllowedAssetUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        string host = uri.Host;
        foreach (string allowed in AllowedAssetHosts)
        {
            if (string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase)) return true;
            if (host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool TrySanitizeTagForPath(string tag, out string folderName)
    {
        folderName = string.Empty;
        if (string.IsNullOrWhiteSpace(tag)) return false;

        string candidate = tag.Trim();
        if (candidate.Length == 0 || candidate.Length > 64) return false;
        if (candidate.StartsWith('.') || candidate.EndsWith('.')) return false;
        if (candidate.Contains("..", StringComparison.Ordinal)) return false;

        foreach (char c in candidate)
        {
            bool ok = (c >= 'a' && c <= 'z')
                   || (c >= 'A' && c <= 'Z')
                   || (c >= '0' && c <= '9')
                   || c == '.' || c == '-' || c == '_';
            if (!ok) return false;
        }

        folderName = candidate;
        return true;
    }

    private static void PurgeOldDownloadFolders()
    {
        try
        {
            string root = Path.Combine(Path.GetTempPath(), DownloadFolderRootName);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch { }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
