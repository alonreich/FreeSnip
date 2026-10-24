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

    private static readonly TimeSpan StartupGracePeriod = TimeSpan.FromMinutes(5);
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
        string ReleaseNotes,
        bool IsDelta = false,
        string TargetFileName = null);

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

    public static string GetExpectedDeltaAssetName()
    {
#if USE_TESSERACT
        return "FreeSnip_tesseract_delta.zip";
#else
        try
        {
            string exe = RuntimePathHelper.ExecutablePath;
            if (!string.IsNullOrEmpty(exe) && exe.Contains("tesseract", StringComparison.OrdinalIgnoreCase))
            {
                return "FreeSnip_tesseract_delta.zip";
            }
        }
        catch { }
        return "FreeSnip_delta.zip";
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

            if (IsCheckThrottled(MinimumIntervalBetweenChecks))
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

                RecordCheckTimestamp();

                if (!RuntimePathHelper.TryParseVersion(release.Tag, out Version remote) ||
                    !RuntimePathHelper.TryParseVersion(RuntimePathHelper.ProductVersion, out Version local))
                {
                    return;
                }

                if (!IsRemoteVersionNewer(remote, local))
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
                        SettingsWindow.NotifyAutoUpdateSettingChanged(false);
                        ToastHelper.ShowToast("Updates Disabled", "Automatic update checks have been turned off in Settings.");
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

            RecordCheckTimestamp();

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

            if (!IsRemoteVersionNewer(remote, local))
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
                    SettingsWindow.NotifyAutoUpdateSettingChanged(false);
                    statusCallback?.Invoke("Auto updates disabled in Settings.");
                    ToastHelper.ShowToast("Updates Disabled", "Automatic update checks have been turned off in Settings.");
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

    public static bool IsCheckThrottled(TimeSpan interval)
    {
        try
        {
            string last = ReadStateFile(LastCheckFile);
            if (DateTime.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime when) &&
                DateTime.UtcNow - when < interval)
            {
                return true;
            }
        }
        catch { }
        return false;
    }

    public static void RecordCheckTimestamp()
    {
        try
        {
            WriteStateFile(LastCheckFile, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        catch { }
    }

    public static bool ThrottlePermitsCheck(TimeSpan interval)
    {
        return !IsCheckThrottled(interval);
    }

    public static bool IsRemoteVersionNewer(Version remote, Version local)
    {
        if (remote == null || local == null) return false;
        if (local.Major >= 2000 && remote.Major < 100)
        {
            return remote >= new Version(3, 0, 1);
        }
        if (local.Major < 100 && remote.Major >= 2000)
        {
            return true;
        }
        return remote.CompareTo(local) > 0;
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
            string deltaAsset = GetExpectedDeltaAssetName();
            bool canUseDelta = StartupTaskHelper.IsRunningFromInstallPath();
            JsonNode asset = null;
            bool isDelta = false;

            if (canUseDelta)
            {
                foreach (JsonNode candidate in root?["assets"]?.AsArray() ?? [])
                {
                    string name = candidate?["name"]?.GetValue<string>();
                    if (name != null && name.Equals(deltaAsset, StringComparison.OrdinalIgnoreCase))
                    {
                        asset = candidate;
                        isDelta = true;
                        break;
                    }
                }
            }

            if (asset == null)
            {
                foreach (JsonNode candidate in root?["assets"]?.AsArray() ?? [])
                {
                    string name = candidate?["name"]?.GetValue<string>();
                    if (name != null && name.Equals(expectedAsset, StringComparison.OrdinalIgnoreCase))
                    {
                        asset = candidate;
                        isDelta = false;
                        break;
                    }
                }
            }

            if (asset == null)
            {
                foreach (JsonNode candidate in root?["assets"]?.AsArray() ?? [])
                {
                    string name = candidate?["name"]?.GetValue<string>();
                    if (name != null && name.Equals("FreeSnip.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        asset = candidate;
                        isDelta = false;
                        break;
                    }
                }
            }

            if (asset == null) return null;

            string url = asset["browser_download_url"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(url) || !IsAllowedAssetUrl(url)) return null;

            if (!TrySanitizeTagForPath(tag, out _)) return null;

            string targetName = isDelta ? deltaAsset : (asset["name"]?.GetValue<string>() ?? expectedAsset);
            string digest = asset["digest"]?.GetValue<string>();
            string sha256 = null;
            if (!string.IsNullOrEmpty(digest))
            {
                int colon = digest.IndexOf(':');
                sha256 = colon >= 0 ? digest[(colon + 1)..].ToLowerInvariant() : digest.ToLowerInvariant();
            }

            if (string.IsNullOrEmpty(sha256) && !string.IsNullOrEmpty(releaseNotes))
            {
                string escapedTarget = Regex.Escape(targetName);
                string pattern = $@"{escapedTarget}[^0-9a-fA-F]*?(?:SHA256|sha256|hash)?[:\s]+([0-9a-fA-F]{{64}})";
                var match = Regex.Match(releaseNotes, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    sha256 = match.Groups[1].Value.ToLowerInvariant();
                }
                else
                {
                    string reversePattern = $@"(?:SHA256|sha256)[:\s]+([0-9a-fA-F]{{64}})[^\n\r]*?{escapedTarget}";
                    var revMatch = Regex.Match(releaseNotes, reversePattern, RegexOptions.IgnoreCase);
                    if (revMatch.Success)
                    {
                        sha256 = revMatch.Groups[1].Value.ToLowerInvariant();
                    }
                }
            }

            if (string.IsNullOrEmpty(sha256))
            {
                foreach (JsonNode candidate in root?["assets"]?.AsArray() ?? [])
                {
                    string name = candidate?["name"]?.GetValue<string>();
                    if (name != null && (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) ||
                                         name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) ||
                                         name.Equals("checksums.txt", StringComparison.OrdinalIgnoreCase)))
                    {
                        string checksumUrl = candidate["browser_download_url"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(checksumUrl) && IsAllowedAssetUrl(checksumUrl))
                        {
                            try
                            {
                                using var sumCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                                string sumsText = await Http.GetStringAsync(checksumUrl, sumCts.Token).ConfigureAwait(false);
                                string escapedTarget = Regex.Escape(targetName);
                                var sumMatch = Regex.Match(sumsText, $@"([0-9a-fA-F]{{64}})\s+[\*]?[^\n\r]*?{escapedTarget}", RegexOptions.IgnoreCase);
                                if (sumMatch.Success)
                                {
                                    sha256 = sumMatch.Groups[1].Value.ToLowerInvariant();
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                }
            }

            long size = asset["size"]?.GetValue<long>() ?? 0;
            return new UpdateRelease(tag, url, sha256, size, releaseNotes, isDelta, targetName);
        }
        catch
        {
            return null;
        }
    }

    public sealed record PendingUpdateInfo(string InstallerPath, string Tag, string Sha256Hex, bool IsDelta);
    private const string PendingUpdateFile = "pending_update.txt";

    public static void SavePendingUpdate(string installerPath, string tag, string sha256Hex, bool isDelta)
    {
        try
        {
            string content = $"{installerPath}\n{tag}\n{sha256Hex}\n{isDelta}";
            WriteStateFile(PendingUpdateFile, content);
        }
        catch { }
    }

    public static PendingUpdateInfo GetPendingUpdate()
    {
        try
        {
            string raw = ReadStateFile(PendingUpdateFile);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string[] lines = raw.Split('\n', StringSplitOptions.TrimEntries);
            if (lines.Length >= 4 && File.Exists(lines[0]))
            {
                bool.TryParse(lines[3], out bool isDelta);
                return new PendingUpdateInfo(lines[0], lines[1], lines[2], isDelta);
            }
        }
        catch { }
        return null;
    }

    public static void ClearPendingUpdate()
    {
        try
        {
            string path = Path.Combine(DeploymentFootprint.LocalAppDataFolder, PendingUpdateFile);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    public static bool ApplyPendingUpdateIfStaged()
    {
        try
        {
            var pending = GetPendingUpdate();
            if (pending == null) return false;

            ClearPendingUpdate();

            using FileStream payloadLock = TryLockVerifiedPayload(pending.InstallerPath, pending.Sha256Hex, pending.IsDelta);
            if (payloadLock == null) return false;

            string launcherExe = pending.IsDelta ? StartupTaskHelper.InstallPath : pending.InstallerPath;
            var installer = new ProcessStartInfo(launcherExe)
            {
                UseShellExecute = true
            };
            installer.ArgumentList.Add("--install");
            installer.ArgumentList.Add("--auto-update");
            if (pending.IsDelta)
            {
                installer.ArgumentList.Add("--delta");
                installer.ArgumentList.Add(pending.InstallerPath);
            }
            installer.ArgumentList.Add("--wait-pid");
            installer.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

            Process.Start(installer);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Fail-closed: the file must carry an intact Authenticode signature from the same certificate that
    /// signed the running executable. The SHA-256 digest comes from the same release as the payload, so it
    /// only proves transport integrity, never authenticity.
    /// </summary>
    public static bool VerifyAuthenticodeSignature(string filePath) => UpdateSignatureVerifier.IsSignedBySameSigner(filePath);

    /// <summary>
    /// Opens the staged payload with FileShare.Read (no writers, no rename/delete), then checks its SHA-256 and,
    /// for full installers, its pinned Authenticode signer on that same handle. The caller keeps the returned
    /// stream open until the installer has been started so verified bytes cannot be swapped before launch.
    /// Delta packages are zip archives; their executables are signature-checked when the installer extracts them.
    /// Returns null (and discards the payload) on any failure.
    /// </summary>
    internal static FileStream TryLockVerifiedPayload(string path, string expectedSha256, bool isDelta)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256) || !IsUnderDownloadRoot(path) || !File.Exists(path)) return null;

        FileStream fs = null;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            string actualHash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            bool valid = string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase)
                && (isDelta || UpdateSignatureVerifier.IsSignedBySameSigner(path, fs.SafeFileHandle));
            if (valid) return fs;
        }
        catch (Exception ex)
        {
            LogHelper.GetLogger(typeof(UpdateService)).Warn($"Update payload verification failed for {path}: {ex.Message}");
        }

        fs?.Dispose();
        TryDeleteFile(path);
        return null;
    }

    private static bool IsUnderDownloadRoot(string path)
    {
        try
        {
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), DownloadFolderRootName)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task DownloadVerifyLaunchAsync(Window owner, UpdateRelease release)
    {
        if (release == null) return;

        if (!TrySanitizeTagForPath(release.Tag, out string tagFolderName))
        {
            throw new InvalidOperationException($"Invalid release tag format: {release.Tag}");
        }

        PurgeOldDownloadFolders(keepTag: tagFolderName);

        string downloadRoot = Path.Combine(Path.GetTempPath(), DownloadFolderRootName);
        string folder = Path.Combine(downloadRoot, tagFolderName);
        string targetFileName = release.TargetFileName ?? (release.IsDelta ? GetExpectedDeltaAssetName() : GetExpectedAssetName());
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
            long existingBytes = 0;
            if (File.Exists(partPath))
            {
                try
                {
                    existingBytes = new FileInfo(partPath).Length;
                    if (release.Size > 0 && existingBytes >= release.Size)
                    {
                        TryDeleteFile(partPath);
                        existingBytes = 0;
                    }
                }
                catch { existingBytes = 0; }
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl);
            if (existingBytes > 0 && (release.Size <= 0 || existingBytes < release.Size))
            {
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingBytes, null);
            }
            else
            {
                existingBytes = 0;
            }

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            bool isPartial = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (!response.IsSuccessStatusCode && !isPartial)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    TryDeleteFile(partPath);
                    existingBytes = 0;
                    using var retryReq = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl);
                    using var retryResp = await Http.SendAsync(retryReq, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                    retryResp.EnsureSuccessStatusCode();
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                }
            }

            long total;
            if (isPartial)
            {
                total = response.Content.Headers.ContentRange?.Length ?? (response.Content.Headers.ContentLength.HasValue ? response.Content.Headers.ContentLength.Value + existingBytes : release.Size);
            }
            else
            {
                existingBytes = 0;
                total = response.Content.Headers.ContentLength ?? release.Size;
            }

            FileMode mode = (isPartial && existingBytes > 0) ? FileMode.Append : FileMode.Create;
            await using Stream source = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            await using var target = new FileStream(partPath, mode, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024);
            byte[] buffer = new byte[1024 * 1024];
            long copied = existingBytes;
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

            if (string.IsNullOrWhiteSpace(release.Sha256Hex))
            {
                TryDeleteFile(partPath);
                throw new InvalidOperationException($"Cannot verify update integrity: no authoritative SHA-256 checksum was provided for {targetFileName}. The download was discarded for safety.");
            }

            string actualHash;
            await using (FileStream verifyStream = File.OpenRead(partPath))
            {
                actualHash = Convert.ToHexString(SHA256.HashData(verifyStream)).ToLowerInvariant();
            }

            if (!string.Equals(actualHash, release.Sha256Hex, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(partPath);
                throw new InvalidOperationException($"Downloaded update failed SHA-256 integrity verification (expected {release.Sha256Hex}, computed {actualHash}). The corrupted payload was discarded.");
            }

            File.Move(partPath, finalPath, overwrite: true);

            if (!release.IsDelta && !VerifyAuthenticodeSignature(finalPath))
            {
                TryDeleteFile(finalPath);
                throw new InvalidOperationException(UpdateSignatureVerifier.IsRunningBuildSigned
                    ? "Downloaded update is not signed by the FreeSnip publisher certificate. The unverified binary was discarded."
                    : "This FreeSnip build is unsigned, so automatic updates cannot be verified and are disabled. Install the update manually.");
            }

            cts.Token.ThrowIfCancellationRequested();

            UpdateReadyAction action = UpdateReadyAction.ContinueWorking;
            if (progressWindow != null)
            {
                action = await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    return await progressWindow.PromptReadyActionAsync().ConfigureAwait(true);
                }).ConfigureAwait(false);
            }

            if (action == UpdateReadyAction.RestartNow)
            {
                ClearPendingUpdate();
                Dispatcher.UIThread.Post(() => progressWindow?.MarkHandoffToInstaller());

                string launcherExe = release.IsDelta ? StartupTaskHelper.InstallPath : finalPath;
                var installer = new ProcessStartInfo(launcherExe)
                {
                    UseShellExecute = true
                };
                installer.ArgumentList.Add("--install");
                installer.ArgumentList.Add("--auto-update");
                if (release.IsDelta)
                {
                    installer.ArgumentList.Add("--delta");
                    installer.ArgumentList.Add(finalPath);
                }
                installer.ArgumentList.Add("--wait-pid");
                installer.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

                // The user may have sat on the "ready" prompt; re-verify and hold the lock until the
                // installer starts. ShutdownForUpdate ends the process, which releases the handle.
                FileStream payloadLock = TryLockVerifiedPayload(finalPath, release.Sha256Hex, release.IsDelta)
                    ?? throw new InvalidOperationException("The downloaded update changed on disk after verification and was discarded.");

                Dispatcher.UIThread.Post(() =>
                {
                    try { progressWindow?.Close(); } catch { }
                    try
                    {
                        App.ShutdownForUpdate(installer);
                    }
                    finally
                    {
                        payloadLock.Dispose();
                    }
                });
            }
            else
            {
                SavePendingUpdate(finalPath, release.Tag, release.Sha256Hex, release.IsDelta);
                Dispatcher.UIThread.Post(() =>
                {
                    try { progressWindow?.Close(); } catch { }
                    ToastHelper.ShowToast("Update Ready", "FreeSnip will complete the upgrade automatically when restarted.");
                });
            }
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(finalPath);
        }
        catch (Exception ex)
        {
            TryDeleteFile(finalPath);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StartupTaskHelper.ShowForegroundMessageBox(
                    $"The update could not be completed.\r\n\r\nReason: {ex.Message}\r\n\r\nYour current version was not changed.",
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

    private static void PurgeOldDownloadFolders(string keepTag = null)
    {
        try
        {
            string root = Path.Combine(Path.GetTempPath(), DownloadFolderRootName);
            if (!Directory.Exists(root)) return;
            if (string.IsNullOrEmpty(keepTag))
            {
                Directory.Delete(root, recursive: true);
                return;
            }
            foreach (var dir in Directory.GetDirectories(root))
            {
                string dirName = Path.GetFileName(dir);
                if (!string.Equals(dirName, keepTag, StringComparison.OrdinalIgnoreCase))
                {
                    try { Directory.Delete(dir, recursive: true); } catch { }
                }
            }
        }
        catch { }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
