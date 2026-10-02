using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using freesnip.foundation.core;
using freesnip.foundation.IniFile;

namespace freesnip.helpers
{
    internal static class IniConfigurationDeployer
    {
        public const string ConfigBaseName = "freesnip";

        public static void EnsureDefaultsFile(string configurationFolder)
        {
            if (string.IsNullOrWhiteSpace(configurationFolder))
            {
                return;
            }

            Directory.CreateDirectory(configurationFolder);
            string defaultsPath = Path.Combine(configurationFolder, ConfigBaseName + "-defaults.ini");
            if (File.Exists(defaultsPath))
            {
                return;
            }

            WriteDefaultsFile(defaultsPath);
        }

        public static void EnsureUserConfiguration(string configurationFolder)
        {
            if (string.IsNullOrWhiteSpace(configurationFolder))
            {
                return;
            }

            Directory.CreateDirectory(configurationFolder);
            EnsureDefaultsFile(configurationFolder);

            string userPath = Path.Combine(configurationFolder, ConfigBaseName + ".ini");
            if (File.Exists(userPath))
            {
                return;
            }

            foreach (var cand in StartupTaskHelper.GetSettingsCandidates())
            {
                if (File.Exists(cand) && !string.Equals(cand, userPath, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string content = File.ReadAllText(cand);
                        content = content.Replace("[SnapVox]", "[Core]", StringComparison.OrdinalIgnoreCase);
                        File.WriteAllText(userPath, content, Encoding.UTF8);
                        return;
                    }
                    catch { }
                }
            }

            IniConfig.IniDirectory = configurationFolder;
            IniConfig.Init("FreeSnip", ConfigBaseName);
            var coreConfiguration = IniConfig.GetIniSection<CoreConfiguration>(allowSave: false);
            if (string.IsNullOrWhiteSpace(coreConfiguration.Language))
            {
                coreConfiguration.Language = "en-US";
            }

            IniConfig.SaveTo(userPath);
        }

        private static void WriteDefaultsFile(string defaultsPath)
        {
            var core = new CoreConfiguration();
            core.Fill(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            string directory = Path.GetDirectoryName(defaultsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var writer = new StreamWriter(defaultsPath, append: false, Encoding.UTF8);
            core.Write(writer, onlyProperties: false);
            writer.Flush();
        }
    }
}
