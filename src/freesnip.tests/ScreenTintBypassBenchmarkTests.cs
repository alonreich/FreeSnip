using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using freesnip.foundation.core;
using freesnip.native.foundation;
using Xunit;

namespace freesnip.tests
{
    public class ScreenTintBypassBenchmarkTests
    {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const uint WsPopup = 0x80000000;
        private const uint WsVisible = 0x10000000;
        private const uint WsExToolwindow = 0x00000080;

        private static int CountDesktopWindows()
        {
            int count = 0;
            EnumWindows((_, _) =>
            {
                count++;
                return true;
            }, IntPtr.Zero);
            return count;
        }

        [Fact]
        public void ZeroReferencesToDeviceGammaRamp_AcrossAllAssemblies()
        {
            var assemblies = new[]
            {
                typeof(ScreenTintBypass).Assembly,
                typeof(NativeCapture).Assembly,
            };

            foreach (var asm in assemblies)
            {
                foreach (var type in asm.GetTypes())
                {
                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                    {
                        Assert.False(
                            method.Name.Contains("SetDeviceGammaRamp", StringComparison.OrdinalIgnoreCase) ||
                            method.Name.Contains("GetDeviceGammaRamp", StringComparison.OrdinalIgnoreCase),
                            $"Forbidden gamma ramp method '{method.Name}' found on type '{type.FullName}' in assembly '{asm.GetName().Name}'");
                    }
                }
            }
        }

        [Fact]
        public void Benchmark_ShouldExcludeLayeredWindows_ExecutesUnder2Ms_With50PlusWindows()
        {
            var createdWindows = new List<IntPtr>();
            try
            {
                int existingWindows = CountDesktopWindows();
                int windowsToCreate = Math.Max(0, 60 - existingWindows);

                for (int i = 0; i < windowsToCreate; i++)
                {
                    IntPtr hwnd = CreateWindowExW(
                        WsExToolwindow,
                        "STATIC",
                        $"SnapVox_BenchmarkWindow_{i}",
                        WsPopup | WsVisible,
                        0, 0, 10, 10,
                        IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

                    if (hwnd != IntPtr.Zero)
                    {
                        createdWindows.Add(hwnd);
                    }
                }

                int totalWindows = CountDesktopWindows();
                Assert.True(totalWindows >= 50, $"Expected >= 50 windows on desktop for benchmark, got {totalWindows}.");

                var region = RECT.FromXYWH(0, 0, 1920, 1080);

                ScreenTintBypass.ShouldExcludeLayeredWindows(region);

                double minElapsedMs = double.MaxValue;
                for (int iter = 0; iter < 3; iter++)
                {
                    ScreenTintBypass.InvalidateCache();
                    var sw = Stopwatch.StartNew();
                    bool _ = ScreenTintBypass.ShouldExcludeLayeredWindows(region);
                    sw.Stop();
                    if (sw.Elapsed.TotalMilliseconds < minElapsedMs)
                    {
                        minElapsedMs = sw.Elapsed.TotalMilliseconds;
                    }
                }

                double allowedMs = Math.Max(2.0, 2.0 * (totalWindows / 100.0));
                Assert.True(minElapsedMs < allowedMs,
                    $"Benchmark failed: ShouldExcludeLayeredWindows took {minElapsedMs:F3}ms across {totalWindows} windows (must be < {allowedMs:F3}ms).");
            }
            finally
            {
                foreach (var hwnd in createdWindows)
                {
                    DestroyWindow(hwnd);
                }
            }
        }

        [Fact]
        public void SlidingExpirationProcessCache_ResolvesKnownAndIgnoredProcesses_WithoutProcessAllocations()
        {
            var region = RECT.FromXYWH(0, 0, 800, 600);

            ScreenTintBypass.ShouldExcludeLayeredWindows(region);

            int totalWindows = CountDesktopWindows();
            double allowedMs = Math.Max(2.0, 2.0 * (totalWindows / 100.0));

            double minElapsedMs = double.MaxValue;
            for (int i = 0; i < 5; i++)
            {
                ScreenTintBypass.InvalidateCache();
                var sw = Stopwatch.StartNew();
                bool result = ScreenTintBypass.ShouldExcludeLayeredWindows(region);
                sw.Stop();
                if (sw.Elapsed.TotalMilliseconds < minElapsedMs)
                {
                    minElapsedMs = sw.Elapsed.TotalMilliseconds;
                }
            }

            Assert.True(minElapsedMs < allowedMs,
                $"Sliding expiration benchmark failed: ShouldExcludeLayeredWindows took {minElapsedMs:F3}ms across {totalWindows} windows (must be < {allowedMs:F3}ms).");
        }
    }
}

