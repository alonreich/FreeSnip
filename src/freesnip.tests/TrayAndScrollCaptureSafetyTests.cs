using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using freesnip;
using freesnip.forms;
using freesnip.foundation.core;
using Xunit;

namespace freesnip.tests
{
    public class TrayAndScrollCaptureSafetyTests
    {
        [Fact]
        public void TrayIcon_NamedSource_IdempotentAcquisitionAndRelease()
        {
            App.ResetTrayHoldStateForTesting();
            Assert.Equal(0, App.ActiveRedHoldCount);

            // First acquisition
            App.ForceRedTrayIcon(true, "ScrollCapture");
            Assert.Equal(1, App.ActiveRedHoldCount);

            // Duplicate acquisition with same source must be strictly idempotent
            App.ForceRedTrayIcon(true, "ScrollCapture");
            Assert.Equal(1, App.ActiveRedHoldCount);

            // Release
            App.ForceRedTrayIcon(false, "ScrollCapture");
            Assert.Equal(0, App.ActiveRedHoldCount);

            // Duplicate release must be a no-op and never drive count negative
            App.ForceRedTrayIcon(false, "ScrollCapture");
            Assert.Equal(0, App.ActiveRedHoldCount);
        }

        [Fact]
        public void TrayIcon_MultiSource_HoldIsolation()
        {
            App.ResetTrayHoldStateForTesting();

            App.ForceRedTrayIcon(true, "ScrollCapture");
            App.ForceRedTrayIcon(true, "OcrCapture");
            Assert.Equal(2, App.ActiveRedHoldCount);

            // Releasing ScrollCapture leaves OcrCapture intact
            App.ForceRedTrayIcon(false, "ScrollCapture");
            Assert.Equal(1, App.ActiveRedHoldCount);

            // Releasing OcrCapture clears all holds
            App.ForceRedTrayIcon(false, "OcrCapture");
            Assert.Equal(0, App.ActiveRedHoldCount);
        }

        [Fact]
        public void TrayIcon_AnonymousAndNamed_Interoperability()
        {
            App.ResetTrayHoldStateForTesting();

            // Anonymous hold (e.g. legacy or general capture)
            App.ForceRedTrayIcon(true, null);
            Assert.Equal(1, App.ActiveRedHoldCount);

            // Named hold
            App.ForceRedTrayIcon(true, "ScrollCapture");
            Assert.Equal(2, App.ActiveRedHoldCount);

            // Releasing named hold does not clear anonymous hold
            App.ForceRedTrayIcon(false, "ScrollCapture");
            Assert.Equal(1, App.ActiveRedHoldCount);

            // Releasing anonymous hold clears all
            App.ForceRedTrayIcon(false, null);
            Assert.Equal(0, App.ActiveRedHoldCount);

            // Extra anonymous release does not drive negative
            App.ForceRedTrayIcon(false, null);
            Assert.Equal(0, App.ActiveRedHoldCount);
        }

        [Fact]
        public void RestoreTrayIcon_IsIdempotentAndSafe()
        {
            // RestoreTrayIcon must execute without throwing or recreating native shell icon
            App.RestoreTrayIcon();
            App.RestoreTrayIcon();
        }

        [Fact]
        public void CoreConfiguration_ScrollCaptureDelimiterHotkey_DefaultsToEnter()
        {
            var config = new CoreConfiguration();
            Assert.Equal("Enter", config.ScrollCaptureDelimiterHotkey);
        }

        [AvaloniaFact]
        public void ScrollCaptureBarWindow_HudStates_AdvertiseEnterForFinishing()
        {
            // Verify HUD hint text strings across states
            var bar = new ScrollCaptureBarWindow();

            bar.UpdateHudState(ScrollHudState.Initial);
            var initialHint = bar.FindControl<TextBlock>("SubtextHint")?.Text ?? string.Empty;
            Assert.Contains("Enter", initialHint);

            bar.UpdateHudState(ScrollHudState.Active, 2.5, 1800);
            var activeHint = bar.FindControl<TextBlock>("SubtextHint")?.Text ?? string.Empty;
            Assert.Contains("Enter", activeHint);

            bar.UpdateHudState(ScrollHudState.Idle, 3.0, 2400);
            var idleHint = bar.FindControl<TextBlock>("SubtextHint")?.Text ?? string.Empty;
            Assert.Contains("Enter", idleHint);

            bar.UpdateHudState(ScrollHudState.LimitReached);
            var limitHint = bar.FindControl<TextBlock>("SubtextHint")?.Text ?? string.Empty;
            Assert.Contains("Enter", limitHint);
            Assert.DoesNotContain("Space", limitHint);

            bar.Close();
        }

        [Fact]
        public void AppAxaml_ScrollCaptureHeader_DoesNotHardcodeSpace()
        {
            // Read App.axaml content to verify tray menu text
            string baseDir = AppContext.BaseDirectory;
            string solutionDir = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\.."));
            string appAxamlPath = Path.Combine(solutionDir, @"src\freesnip\App.axaml");
            if (File.Exists(appAxamlPath))
            {
                string text = File.ReadAllText(appAxamlPath);
                Assert.Contains("Header=\"Scroll Capture\"", text);
                Assert.DoesNotContain("Header=\"Scroll Capture (Space)\"", text);
            }
        }

        [Fact]
        public void TrayIcon_ClearAllTrayHolds_ResetsBothAnonymousAndNamed()
        {
            App.ResetTrayHoldStateForTesting();

            App.ForceRedTrayIcon(true, null);
            App.ForceRedTrayIcon(true, "scroll capture editor handoff");
            App.ForceRedTrayIcon(true, "RubberbandDrag");
            Assert.Equal(3, App.ActiveRedHoldCount);

            App.ClearAllTrayHolds();
            Assert.Equal(0, App.ActiveRedHoldCount);
        }

        [Fact]
        public void TrayIcon_RubberbandAndOcrHold_Transitions()
        {
            App.ResetTrayHoldStateForTesting();

            // When dragging rubberband
            App.ForceRedTrayIcon(true, "RubberbandDrag");
            Assert.Equal(1, App.ActiveRedHoldCount);

            // Releasing drag
            App.ForceRedTrayIcon(false, "RubberbandDrag");
            Assert.Equal(0, App.ActiveRedHoldCount);

            // When entering OCR mode
            App.ForceRedTrayIcon(true, "CaptureOcr");
            Assert.Equal(1, App.ActiveRedHoldCount);

            // Exiting OCR mode
            App.ForceRedTrayIcon(false, "CaptureOcr");
            Assert.Equal(0, App.ActiveRedHoldCount);
        }
    }
}
