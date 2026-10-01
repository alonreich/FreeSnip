using System;
using System.Collections.Generic;
using freesnip.foundation.interfaces.Ocr;
using freesnip.Services;
using Xunit;

namespace freesnip.tests
{
    public class SettingsServiceTests
    {
        private readonly SettingsService _service = new();

        [Fact]
        public void DefaultHotkeys_ContainsExpectedKeys()
        {
            Assert.Equal(21, _service.DefaultHotkeys.Count);
            Assert.Equal("PrintScreen", _service.DefaultHotkeys["TxtRegionKey"]);
            Assert.Equal("Alt + PrintScreen", _service.DefaultHotkeys["TxtWindowKey"]);
            Assert.Equal("Ctrl + PrintScreen", _service.DefaultHotkeys["TxtFullscreenKey"]);
            Assert.Equal("None", _service.DefaultHotkeys["TxtLastRegionKey"]);
            Assert.Equal("None", _service.DefaultHotkeys["TxtClipboardKey"]);
        }

        [Fact]
        public void GlobalHotkeyBoxNames_ContainsFivePrimaryHotkeys()
        {
            Assert.Equal(5, _service.GlobalHotkeyBoxNames.Count);
            Assert.Contains("TxtRegionKey", _service.GlobalHotkeyBoxNames);
            Assert.Contains("TxtWindowKey", _service.GlobalHotkeyBoxNames);
            Assert.Contains("TxtFullscreenKey", _service.GlobalHotkeyBoxNames);
            Assert.Contains("TxtLastRegionKey", _service.GlobalHotkeyBoxNames);
            Assert.Contains("TxtClipboardKey", _service.GlobalHotkeyBoxNames);
        }

        [Fact]
        public void BuildFingerprint_FormatsAndSortsDeterministically()
        {
            var values = new List<KeyValuePair<string, object?>>
            {
                new("Z", "valueZ"),
                new("A", "valueA"),
                new("M", 42),
                new("NullKey", null)
            };

            string fingerprint = _service.BuildFingerprint(values);

            Assert.Equal("A=valueA|M=42|NullKey=|Z=valueZ", fingerprint);
        }

        [Fact]
        public void ValidateGlobalHotkeys_ValidDistinctHotkeys_ReturnsValid()
        {
            var dict = new Dictionary<string, string?>
            {
                ["TxtRegionKey"] = "PrintScreen",
                ["TxtWindowKey"] = "Alt + PrintScreen",
                ["TxtFullscreenKey"] = "Ctrl + PrintScreen",
                ["TxtLastRegionKey"] = "None",
                ["TxtClipboardKey"] = ""
            };

            var result = _service.ValidateGlobalHotkeys(dict, _ => true);

            Assert.True(result.IsValid);
            Assert.Null(result.FirstErrorMessage);
            Assert.Empty(result.ConflictedBoxNames);
        }

        [Fact]
        public void ValidateGlobalHotkeys_DuplicateHotkey_FlagsConflict()
        {
            var dict = new Dictionary<string, string?>
            {
                ["TxtRegionKey"] = "Ctrl + Shift + A",
                ["TxtWindowKey"] = "Ctrl + Shift + A",
                ["TxtFullscreenKey"] = "Ctrl + PrintScreen"
            };

            var result = _service.ValidateGlobalHotkeys(dict, _ => true);

            Assert.False(result.IsValid);
            Assert.NotNull(result.FirstErrorMessage);
            Assert.Contains("already assigned to another FreeSnip shortcut", result.FirstErrorMessage);
            Assert.Contains("TxtWindowKey", result.ConflictedBoxNames);
        }

        [Fact]
        public void ValidateGlobalHotkeys_UnavailableHotkey_FlagsConflict()
        {
            var dict = new Dictionary<string, string?>
            {
                ["TxtRegionKey"] = "Ctrl + Alt + X",
                ["TxtWindowKey"] = "None"
            };

            var result = _service.ValidateGlobalHotkeys(dict, _ => false);

            Assert.False(result.IsValid);
            Assert.NotNull(result.FirstErrorMessage);
            Assert.Contains("already in use by another application", result.FirstErrorMessage);
            Assert.Contains("TxtRegionKey", result.ConflictedBoxNames);
        }

        [Theory]
        [InlineData("0.5 s (fast)", 500)]
        [InlineData("1 s (normal)", 1000)]
        [InlineData("2 s (slow)", 2000)]
        [InlineData("3 s (slower)", 3000)]
        [InlineData("5 s (longest)", 5000)]
        [InlineData("unknown", 5000)]
        [InlineData(null, 2000)]
        public void ParseOverlayDurationMs_ParsesCorrectly(string? choice, int expectedMs)
        {
            int actual = _service.ParseOverlayDurationMs(choice);
            Assert.Equal(expectedMs, actual);
        }

        [Theory]
        [InlineData(500, "0.5 s (fast)")]
        [InlineData(1000, "1 s (normal)")]
        [InlineData(2000, "2 s (slow)")]
        [InlineData(3000, "3 s (slower)")]
        [InlineData(5000, "5 s (longest)")]
        public void GetOverlayChoiceForDuration_MapsCorrectly(int durationMs, string expectedChoice)
        {
            string actual = _service.GetOverlayChoiceForDuration(durationMs);
            Assert.Equal(expectedChoice, actual);
        }

        [Theory]
        [InlineData("434343", "#434343")]
        [InlineData("#AABBCC", "#AABBCC")]
        [InlineData(null, "#434343")]
        [InlineData("", "#434343")]
        public void NormalizeHexColor_NormalizesProperly(string? input, string expected)
        {
            string actual = _service.NormalizeHexColor(input);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void HasRequiredLanguagesSafe_NullProvider_ReturnsFalse()
        {
            Assert.False(_service.HasRequiredLanguagesSafe(null));
        }

        private class ThrowingProvider : IOcrProvider
        {
            public string EngineId => "throw";
            public string DisplayName => "Throw";
            public bool HasRequiredLanguages() => throw new InvalidOperationException("boom");
            public System.Threading.Tasks.Task<OcrInformation> DoOcrAsync(SixLabors.ImageSharp.Image image, System.Threading.CancellationToken cancellationToken, bool isAlreadyOwned = false) => throw new NotImplementedException();
            public System.Threading.Tasks.Task<OcrInformation> DoOcrAsync(SixLabors.ImageSharp.Image image) => throw new NotImplementedException();
        }

        [Fact]
        public void HasRequiredLanguagesSafe_ThrowingProvider_ReturnsFalseSafely()
        {
            var provider = new ThrowingProvider();
            Assert.False(_service.HasRequiredLanguagesSafe(provider));
        }
    }
}

