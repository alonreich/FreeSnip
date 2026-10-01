#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using freesnip.foundation.core;
using freesnip.foundation.IniFile;
using freesnip.foundation.interfaces.Ocr;
using freesnip.helpers;

namespace freesnip.Services
{
    public class SettingsService : ISettingsService
    {
        public IReadOnlyList<string> GlobalHotkeyBoxNames { get; } = new string[]
        {
            "TxtRegionKey",
            "TxtWindowKey",
            "TxtFullscreenKey",
            "TxtLastRegionKey",
            "TxtClipboardKey"
        };

        public IReadOnlyDictionary<string, string> DefaultHotkeys { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TxtRegionKey"] = "PrintScreen",
            ["TxtWindowKey"] = "Alt + PrintScreen",
            ["TxtFullscreenKey"] = "Ctrl + PrintScreen",
            ["TxtLastRegionKey"] = "None",
            ["TxtClipboardKey"] = "None",
            ["TxtScrollCaptureDelimiterKey"] = "Space",
            ["TxtArrowKey"] = "A",
            ["TxtLineKey"] = "L",
            ["TxtTextKey"] = "T",
            ["TxtResizeKey"] = "R",
            ["TxtFreehandKey"] = "D",
            ["TxtEmojiKey"] = "E",
            ["TxtCounterKey"] = "I",
            ["TxtHighlightKey"] = "H",
            ["TxtPixelate1Key"] = "O",
            ["TxtPixelate2Key"] = "P",
            ["TxtCropKey"] = "C",
            ["TxtRotateCwKey"] = "Right",
            ["TxtRotateCcwKey"] = "Left",
            ["TxtDuplicateObjectKey"] = "Ctrl + D",
            ["TxtDeleteObjectKey"] = "Delete"
        };

        public IReadOnlyList<string> OverlayDurationChoices { get; } = new string[]
        {
            "0.5 s (fast)",
            "1 s (normal)",
            "2 s (slow)",
            "3 s (slower)",
            "5 s (longest)"
        };

        public string BuildFingerprint(IEnumerable<KeyValuePair<string, object?>> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            var parts = new List<string>();
            foreach (var kvp in values)
            {
                parts.Add($"{kvp.Key}={kvp.Value?.ToString() ?? string.Empty}");
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join("|", parts);
        }

        public HotkeyValidationResult ValidateGlobalHotkeys(
            IReadOnlyDictionary<string, string?> hotkeys,
            Func<string, bool>? availabilityChecker = null)
        {
            ArgumentNullException.ThrowIfNull(hotkeys);
            var checkAvailability = availabilityChecker ?? (key =>
            {
                try
                {
                    return HotkeyManager.IsHotkeyAvailable(key);
                }
                catch
                {
                    return false;
                }
            });

            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            var conflicted = new HashSet<string>(StringComparer.Ordinal);
            string? firstMessage = null;

            foreach (var name in GlobalHotkeyBoxNames)
            {
                if (!hotkeys.TryGetValue(name, out var hotkey) ||
                    string.IsNullOrWhiteSpace(hotkey) ||
                    string.Equals(hotkey, "None", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? message = null;
                string normalized = HotkeyManager.NormalizeHotkey(hotkey);
                if (normalized.Length == 0)
                {
                    message = $"\"{hotkey}\" is not a shortcut Windows can register.";
                }
                else if (seen.ContainsKey(normalized))
                {
                    message = $"\"{hotkey}\" is already assigned to another FreeSnip shortcut on this tab.";
                }
                else
                {
                    seen[normalized] = name;
                    bool available = checkAvailability(hotkey);
                    if (!available)
                    {
                        message = $"\"{hotkey}\" is already in use by another application.";
                    }
                }

                if (message != null)
                {
                    conflicted.Add(name);
                    firstMessage ??= message;
                }
            }

            return new HotkeyValidationResult(conflicted.Count == 0, firstMessage, conflicted);
        }

        public int ParseOverlayDurationMs(string? choice)
        {
            if (string.IsNullOrWhiteSpace(choice)) return 2000;
            if (choice.StartsWith("0.5", StringComparison.Ordinal)) return 500;
            if (choice.StartsWith("1 ", StringComparison.Ordinal)) return 1000;
            if (choice.StartsWith("2 ", StringComparison.Ordinal)) return 2000;
            if (choice.StartsWith("3 ", StringComparison.Ordinal)) return 3000;
            return 5000;
        }

        public string GetOverlayChoiceForDuration(int durationMs)
        {
            return durationMs <= 500 ? OverlayDurationChoices[0]
                : durationMs <= 1000 ? OverlayDurationChoices[1]
                : durationMs <= 2000 ? OverlayDurationChoices[2]
                : durationMs <= 3000 ? OverlayDurationChoices[3]
                : OverlayDurationChoices[4];
        }

        public string NormalizeHexColor(string? hex, string defaultColor = "#434343")
        {
            if (string.IsNullOrWhiteSpace(hex)) return defaultColor;
            string trimmed = hex.Trim();
            return trimmed.StartsWith('#') ? trimmed : "#" + trimmed;
        }

        public bool HasRequiredLanguagesSafe(IOcrProvider? provider)
        {
            try
            {
                return provider != null && provider.HasRequiredLanguages();
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> IsAdminStartupConfiguredAsync()
        {
            return await StartupTaskHelper.HasElevatedStartupTaskAsync().ConfigureAwait(false);
        }

        public async Task<bool> ConfigureAdminStartupAsync(bool enable, CoreConfiguration config)
        {
            ArgumentNullException.ThrowIfNull(config);
            if (enable)
            {
                bool configured = await StartupTaskHelper.ConfigureElevatedStartupTaskAsync().ConfigureAwait(false);
                if (configured && await StartupTaskHelper.HasElevatedStartupTaskAsync().ConfigureAwait(false))
                {
                    config.RunAsAdministratorOnStartup = true;
                    IniConfig.Save();
                    return true;
                }
                return false;
            }
            else
            {
                bool removed = await StartupTaskHelper.DeleteElevatedStartupTaskAsync().ConfigureAwait(false);
                if (removed && !await StartupTaskHelper.HasElevatedStartupTaskAsync().ConfigureAwait(false))
                {
                    config.RunAsAdministratorOnStartup = false;
                    IniConfig.Save();
                    return true;
                }
                return false;
            }
        }

        public async Task SaveSettingsAsync(CoreConfiguration config, bool globalHotkeysChanged)
        {
            ArgumentNullException.ThrowIfNull(config);
            IniConfig.Save();
            if (globalHotkeysChanged)
            {
                await HotkeyManager.RestartAsync().ConfigureAwait(false);
            }
            freesnip.App.UpdateTrayMenuHotkeys();
        }
    }
}
