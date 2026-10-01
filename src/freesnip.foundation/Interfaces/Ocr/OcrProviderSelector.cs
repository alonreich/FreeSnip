using System;
using System.Collections.Generic;
using System.Linq;

namespace freesnip.foundation.interfaces.Ocr
{
    public static class OcrProviderSelector
    {
        public static IOcrProvider Select(IEnumerable<IOcrProvider> providers, string configuredEngine)
        {
            var availableProviders = providers?.Where(provider => provider != null).ToList() ?? new List<IOcrProvider>();
            if (availableProviders.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(configuredEngine))
            {
                var configured = availableProviders.FirstOrDefault(provider =>
                    string.Equals(provider.DisplayName, configuredEngine, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(provider.EngineId, configuredEngine, StringComparison.OrdinalIgnoreCase));

                if (configured == null)
                {
                    freesnip.foundation.core.LogHelper.GetLogger(typeof(OcrProviderSelector)).Warn(
                        "[FAIL] Configured OCR engine '" + configuredEngine + "' matches no registered provider. Available: " +
                        string.Join(", ", availableProviders.Select(provider => provider.DisplayName)) + ". Falling back.");
                }
                else if (!configured.HasRequiredLanguages())
                {
                    freesnip.foundation.core.LogHelper.GetLogger(typeof(OcrProviderSelector)).Warn(
                        "[FAIL] Configured OCR engine '" + configuredEngine + "' is missing its required EN/HE language packs. Falling back.");
                }
                else
                {
                    return configured;
                }
            }

            return availableProviders.FirstOrDefault(provider => provider.HasRequiredLanguages()) ?? availableProviders[0];
        }
    }
}
