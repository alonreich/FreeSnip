#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using freesnip.foundation.core;
using freesnip.foundation.interfaces.Ocr;

namespace freesnip.Services
{
    public sealed record HotkeyValidationResult(
        bool IsValid,
        string? FirstErrorMessage,
        IReadOnlySet<string> ConflictedBoxNames);

    public interface ISettingsService
    {
        IReadOnlyList<string> GlobalHotkeyBoxNames { get; }
        IReadOnlyDictionary<string, string> DefaultHotkeys { get; }
        IReadOnlyList<string> OverlayDurationChoices { get; }

        string BuildFingerprint(IEnumerable<KeyValuePair<string, object?>> values);
        HotkeyValidationResult ValidateGlobalHotkeys(IReadOnlyDictionary<string, string?> hotkeys, Func<string, bool>? availabilityChecker = null);

        int ParseOverlayDurationMs(string? choice);
        string GetOverlayChoiceForDuration(int durationMs);
        string NormalizeHexColor(string? hex, string defaultColor = "#434343");
        bool HasRequiredLanguagesSafe(IOcrProvider? provider);

        Task<bool> IsAdminStartupConfiguredAsync();
        Task<bool> ConfigureAdminStartupAsync(bool enable, CoreConfiguration config);
        Task SaveSettingsAsync(CoreConfiguration config, bool globalHotkeysChanged);
    }
}
