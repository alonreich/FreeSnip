using System;
using System.IO;
using System.Linq;
using freesnip.helpers;
using Xunit;

namespace freesnip.tests
{
    /// <summary>
    /// These lists feed an elevated recursive deleter (DeploymentLifecycle.PurgeDirectoryRecursiveAsync).
    /// Every assertion here is a guardrail against deleting anything FreeSnip does not own.
    /// </summary>
    public class DeploymentFootprintTests
    {
        private static string Normalize(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        [Fact]
        public void DirectoryPurgeTargets_AllLiveInsideFreeSnipOwnedRoots()
        {
            string programData = Normalize(DeploymentFootprint.ProgramDataFolder);
            string roaming = Normalize(DeploymentFootprint.RoamingAppDataFolder);
            string local = Normalize(DeploymentFootprint.LocalAppDataFolder);
            string install = Normalize(DeploymentFootprint.InstallFolder);
            string tempApp = Normalize(DeploymentFootprint.TempAppFolder);
            string deploymentTemp = Normalize(DeploymentFootprint.DeploymentTempRoot);

            foreach (string target in DeploymentFootprint.GetDirectoryPurgeTargets(includeInstallFolder: true))
            {
                string t = Normalize(target);
                Assert.True(
                    t.StartsWith(install, StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith(programData, StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith(roaming, StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith(local, StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith(tempApp, StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith(deploymentTemp, StringComparison.OrdinalIgnoreCase),
                    $"Purge target escapes owned roots: {target}");
            }
        }

        [Fact]
        public void DirectoryPurgeTargets_IncludeInstallFolder_SwitchWorks()
        {
            string install = Normalize(DeploymentFootprint.InstallFolder);
            Assert.Contains(DeploymentFootprint.GetDirectoryPurgeTargets(true), t => Normalize(t) == install);
            Assert.DoesNotContain(DeploymentFootprint.GetDirectoryPurgeTargets(false), t => Normalize(t) == install);
        }

        [Fact]
        public void DirectoryPurgeTargets_NeverTargetBareTempOrUserProfileRoots()
        {
            string tempRoot = Normalize(Path.GetTempPath());
            string userProfile = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            string localAppDataRoot = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

            foreach (string target in DeploymentFootprint.GetDirectoryPurgeTargets(true))
            {
                string t = Normalize(target);
                Assert.NotEqual(tempRoot, t);
                Assert.NotEqual(userProfile, t);
                Assert.NotEqual(localAppDataRoot, t);
                Assert.True(t.Length > Path.GetPathRoot(t)!.Length, $"Purge target is a root directory: {target}");
            }
        }

        [Fact]
        public void UserExportsAndUnrelatedTempFiles_AreNeverUninstallTargets()
        {
            Assert.Empty(DeploymentFootprint.GetUserArtifactPatterns());
            string downloads = Normalize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
            string recovery = Normalize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeSnipUpgradeBackups"));
            foreach (string target in DeploymentFootprint.GetDirectoryPurgeTargets(true))
            {
                string root = Normalize(target) + Path.DirectorySeparatorChar;
                Assert.False(downloads.StartsWith(root, StringComparison.OrdinalIgnoreCase));
                Assert.False(recovery.StartsWith(root, StringComparison.OrdinalIgnoreCase));
            }
        }

        [Fact]
        public void UninstallRegistryPurgeTargets_OnlyContainFreeSnipKeys()
        {
            foreach (var target in DeploymentFootprint.GetUninstallRegistryPurgeTargets())
            {
                Assert.True(
                    target.SubKeyPath.Contains("FreeSnip", StringComparison.OrdinalIgnoreCase) ||
                    target.SubKeyPath.Contains("freesnip", StringComparison.OrdinalIgnoreCase),
                    $"Unexpected uninstall target: {target.SubKeyPath}");
                Assert.Contains("Uninstall", target.SubKeyPath, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void AppRegistryPurgeTargets_OnlyContainFreeSnipKeys()
        {
            foreach (var target in DeploymentFootprint.GetAppRegistryPurgeTargets())
            {
                Assert.True(
                    target.Path.Contains("FreeSnip", StringComparison.OrdinalIgnoreCase) ||
                    target.Path.Contains("freesnip", StringComparison.OrdinalIgnoreCase),
                    $"Unexpected app registry target: {target.Path}");
            }
        }

        [Fact]
        public void RunKeyRelativePaths_AllAreSoftwareRunKeys()
        {
            foreach (string path in DeploymentFootprint.RunKeyRelativePaths)
            {
                Assert.StartsWith("SOFTWARE", path, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Run", path, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void ShortcutSearchFolders_ReturnsNonEmptyKnownFolders()
        {
            var folders = DeploymentFootprint.GetShortcutSearchFolders().ToList();
            Assert.True(folders.Count >= 6);
            Assert.DoesNotContain(folders, f => string.IsNullOrWhiteSpace(f));
        }
    }
}
