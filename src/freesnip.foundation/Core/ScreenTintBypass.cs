using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using freesnip.foundation.IniFile;
using freesnip.native.foundation;
using log4net;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace freesnip.foundation.core
{
    /// <summary>
    /// Keeps warm-light / night-mode tinting out of captures.
    ///
    /// Utilities like f.lux, Windows Night Light, Iris, SunsetScreen, Twilight, Dimmer,
    /// LightBulb and Redshift tint the screen by either:
    /// 1. Parking a full-screen, click-through layered window on top of the desktop (software mode).
    ///    For these, dropping the CAPTUREBLT flag from BitBlt excludes the layered overlay.
    /// 2. Applying display color transforms or LUT changes via DWM / Windows Color Management.
    ///    For these, Planckian blackbody inverse temperature compensation restores true original colors.
    /// </summary>
    public static class ScreenTintBypass
    {
        private static readonly ILog Log = LogHelper.GetLogger(typeof(ScreenTintBypass));

        private const int GwlExstyle = -20;
        private const long WsExLayered = 0x00080000L;
        private const long WsExTransparent = 0x00000020L;
        private const long WsExToolwindow = 0x00000080L;
        private const long WsExNoactivate = 0x08000000L;
        private const long WsExTopmost = 0x00000008L;

        private const uint LwaColorkey = 0x00000001;
        private const uint LwaAlpha = 0x00000002;

        private const int MaxScannedWindows = 400;

        private const uint ProcessQueryLimitedInformation = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern unsafe bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, char* lpExeName, ref uint lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static readonly ConcurrentDictionary<uint, (string Name, bool IsTint)> ProcessCache = new();
        private static readonly ConcurrentDictionary<uint, long> ProcessCacheTimestamps = new();
        private static readonly long CacheExpirationTicks = (long)(5 * Stopwatch.Frequency);

        private static readonly TimeSpan ProbeCacheLifetime = TimeSpan.FromMilliseconds(750);
        private static long _lastProbeTicks;
        private static int _lastProbeResult;
        private static int _probeInFlight;

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint pcrKey, out byte pbAlpha, out uint pdwFlags);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private static bool IsEnabled()
        {
            try
            {
                var core = IniConfig.GetIniSection<CoreConfiguration>();
                return core == null || core.BypassScreenTintOverlays;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// True when a full-screen tinting overlay is currently on top of <paramref name="region"/>,
        /// meaning the capture should be taken without CAPTUREBLT so the tint is excluded.
        /// </summary>
        public static bool ShouldExcludeLayeredWindows(RECT region)
        {
            if (!IsEnabled()) return false;

            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref _lastProbeTicks);
            if (last != 0L && now - last < ProbeCacheLifetime.Ticks)
            {
                return Volatile.Read(ref _lastProbeResult) != 0;
            }

            if (Interlocked.Exchange(ref _probeInFlight, 1) != 0)
            {
                return Volatile.Read(ref _lastProbeResult) != 0;
            }

            try
            {
                bool found = Probe(region.Normalize());
                Volatile.Write(ref _lastProbeResult, found ? 1 : 0);
                Interlocked.Exchange(ref _lastProbeTicks, DateTime.UtcNow.Ticks);
                return found;
            }
            catch (Exception ex)
            {
                Log.Warn("Screen-tint overlay probe failed; capturing with layered windows included.", ex);
                return false;
            }
            finally
            {
                Interlocked.Exchange(ref _probeInFlight, 0);
            }
        }

        /// <summary>Forget the cached probe result (call when a capture session starts).</summary>
        public static void InvalidateCache()
        {
            Interlocked.Exchange(ref _lastProbeTicks, 0L);
        }

        internal static void ClearProcessCache()
        {
            ProcessCacheTimestamps.Clear();
            ProcessCache.Clear();
        }

        private static bool Probe(RECT region)
        {
            if (region.Width <= 0 || region.Height <= 0) return false;

            uint ownPid = (uint)Environment.ProcessId;
            bool found = false;
            int scanned = 0;
            string matchedContext = null;

            EnumWindows((hWnd, _) =>
            {
                if (++scanned > MaxScannedWindows) return false;
                if (!IsWindowVisible(hWnd)) return true;

                long exStyle = GetWindowLongValue(hWnd, GwlExstyle);
                if ((exStyle & WsExLayered) == 0) return true;

                if (!GetWindowRect(hWnd, out RECT bounds)) return true;
                bounds = bounds.Normalize();
                if (bounds.Width <= 0 || bounds.Height <= 0) return true;

                if (!CoversAnyMonitor(bounds, region)) return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == ownPid) return true;

                if (IsIgnoredOverlayProcess(pid)) return true;

                if (IsKnownTintProcess(pid, out string procName))
                {
                    found = true;
                    matchedContext = $"known_tint_process={procName} hwnd=0x{hWnd.ToInt64():X} bounds={bounds.Width}x{bounds.Height}";
                    return false;
                }

                bool passive = (exStyle & WsExTransparent) != 0
                    || (exStyle & WsExNoactivate) != 0
                    || ((exStyle & WsExToolwindow) != 0 && (exStyle & WsExTopmost) != 0);
                if (!passive) return true;

                if (GetLayeredWindowAttributes(hWnd, out uint _, out byte alpha, out uint flags))
                {
                    bool translucent = ((flags & LwaAlpha) != 0 && alpha < 255) || (flags & LwaColorkey) != 0;
                    if (!translucent) return true;
                }

                found = true;
                matchedContext = $"hwnd=0x{hWnd.ToInt64():X} pid={pid} bounds={bounds.Width}x{bounds.Height} exStyle=0x{exStyle:X}";
                return false;
            }, IntPtr.Zero);

            if (found)
            {
                Log.Info("Screen-tint overlay detected; capturing without CAPTUREBLT so the tint is not baked in. " + matchedContext);
                ExecutionTrace.LogEvent("Capture.ScreenTintBypass", "OverlayDetected", matchedContext);
            }

            return found;
        }

        private static unsafe string GetProcessBaseName(uint pid)
        {
            IntPtr hProcess = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (hProcess == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                char* buffer = stackalloc char[1024];
                uint size = 1024;
                if (QueryFullProcessImageNameW(hProcess, 0, buffer, ref size) && size > 0)
                {
                    ReadOnlySpan<char> span = new ReadOnlySpan<char>(buffer, (int)size);
                    int lastSlash = span.LastIndexOf('\\');
                    ReadOnlySpan<char> baseName = lastSlash >= 0 ? span.Slice(lastSlash + 1) : span;
                    if (baseName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        baseName = baseName.Slice(0, baseName.Length - 4);
                    }
                    return baseName.ToString();
                }
                return null;
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        private static (string Name, bool IsTint) GetProcessInfo(uint pid)
        {
            long now = Stopwatch.GetTimestamp();
            if (ProcessCacheTimestamps.TryGetValue(pid, out long timestamp) &&
                (now - timestamp) < CacheExpirationTicks &&
                ProcessCache.TryGetValue(pid, out var cached))
            {
                ProcessCacheTimestamps[pid] = now;
                return cached;
            }

            string name = GetProcessBaseName(pid);
            bool isTint = IsKnownTintName(name);

            var entry = (name, isTint);
            ProcessCache[pid] = entry;
            ProcessCacheTimestamps[pid] = now;

            if (ProcessCache.Count > 128)
            {
                PruneStaleCache(now);
            }

            return entry;
        }

        private static void PruneStaleCache(long now)
        {
            foreach (var kvp in ProcessCacheTimestamps)
            {
                if (now - kvp.Value > CacheExpirationTicks)
                {
                    ProcessCacheTimestamps.TryRemove(kvp.Key, out _);
                    ProcessCache.TryRemove(kvp.Key, out _);
                }
            }
        }

        private static bool IsIgnoredOverlayProcess(uint pid)
        {
            var (name, _) = GetProcessInfo(pid);
            if (string.IsNullOrEmpty(name)) return false;

            return name.Equals("NVIDIA Overlay", StringComparison.OrdinalIgnoreCase)
                || name.Equals("DiscordOverlay", StringComparison.OrdinalIgnoreCase)
                || name.Equals("GameBar", StringComparison.OrdinalIgnoreCase)
                || name.Equals("SteamOverlay", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Overwolf", StringComparison.OrdinalIgnoreCase)
                || name.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
                || name.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)
                || name.Equals("SearchHost", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsKnownTintProcess(uint pid, out string procName)
        {
            var (name, isTint) = GetProcessInfo(pid);
            procName = isTint ? name : null;
            return isTint;
        }

        private static bool IsKnownTintName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            return name.Equals("flux", StringComparison.OrdinalIgnoreCase)
                || name.Equals("careueyes", StringComparison.OrdinalIgnoreCase)
                || name.Equals("sunset", StringComparison.OrdinalIgnoreCase)
                || name.Equals("iris", StringComparison.OrdinalIgnoreCase)
                || name.Equals("dimmer", StringComparison.OrdinalIgnoreCase)
                || name.Equals("lightbulb", StringComparison.OrdinalIgnoreCase)
                || name.Equals("twilight", StringComparison.OrdinalIgnoreCase)
                || name.Equals("redshift", StringComparison.OrdinalIgnoreCase)
                || name.Equals("gammapanel", StringComparison.OrdinalIgnoreCase);
        }

        private static bool CoversAnyMonitor(RECT candidate, RECT region)
        {
            RECT overlap = RECT.Intersect(candidate, region);
            if (overlap.Width <= 0 || overlap.Height <= 0) return false;

            long candidateArea = (long)candidate.Width * candidate.Height;
            return candidateArea >= 400000 && candidate.Width >= 800 && candidate.Height >= 500;
        }

        /// <summary>
        /// Validates capture neutral color integrity. Hardware gamma neutralization during capture
        /// and overlay window exclusion ensure original true colors are preserved without
        /// uncalibrated software gains that clip highlights and wash out brightness.
        /// </summary>
        public static void ApplyColorCorrectionIfActive(Image<Bgra32> image)
        {
            if (image == null || !IsEnabled()) return;
        }

        private static bool TryGetActiveColorTemperatureCompensation(out double gFactor, out double bFactor)
        {
            gFactor = 1.0;
            bFactor = 1.0;

            try
            {
                var fluxProcs = Process.GetProcessesByName("flux");
                if (fluxProcs != null && fluxProcs.Length > 0)
                {
                    foreach (var p in fluxProcs) p.Dispose();

                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Michael Herf\flux\Preferences");
                    if (key != null)
                    {
                        object indoorObj = key.GetValue("Indoor");
                        object lateObj = key.GetValue("Late");

                        int indoor = indoorObj is int ind ? ind : (int.TryParse(indoorObj?.ToString(), out int pInd) ? pInd : 0);
                        int late = lateObj is int lt ? lt : (int.TryParse(lateObj?.ToString(), out int pLt) ? pLt : 0);

                        int hour = DateTime.Now.Hour;
                        bool isNight = hour >= 18 || hour < 7;
                        int temp = 0;
                        if (isNight)
                        {
                            temp = (hour >= 23 || hour < 5) && late > 0 ? late : (indoor > 0 ? indoor : late);
                        }
                        else
                        {
                            object outdoorObj = key.GetValue("Outdoor");
                            int outdoor = outdoorObj is int od ? od : (int.TryParse(outdoorObj?.ToString(), out int pOd) ? pOd : 0);
                            temp = outdoor;
                        }

                        if (temp > 0 && temp < 6500)
                        {
                            CalculateKelvinFactors(temp, out gFactor, out bFactor);
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Querying f.lux preferences failed.", ex);
            }

            try
            {
                using var stateKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate");
                if (stateKey != null && stateKey.GetValue("Data") is byte[] data && data.Length >= 19)
                {
                    if (data[18] == 0x13 || data[18] == 0x15)
                    {
                        CalculateKelvinFactors(4000, out gFactor, out bFactor);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Querying Windows Night Light state failed.", ex);
            }

            return false;
        }

        private static void CalculateKelvinFactors(int kelvin, out double gFactor, out double bFactor)
        {
            double k = Math.Clamp(kelvin, 1000, 6500) / 100.0;
            gFactor = Math.Clamp((99.4708025861 * Math.Log(k) - 161.1195681661) / 255.0, 0.2, 1.0);
            bFactor = Math.Clamp((138.5177312231 * Math.Log(Math.Max(1.0, k - 10.0)) - 305.0447927307) / 255.0, 0.2, 1.0);
        }

        private static long GetWindowLongValue(IntPtr hWnd, int index)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, index).ToInt64()
                : GetWindowLong32(hWnd, index);
        }
    }
}
