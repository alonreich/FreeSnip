using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using freesnip.foundation.core;
using freesnip.foundation.core.AvaloniaShims;
using freesnip.helpers;
using Xunit;

namespace freesnip.tests
{
    public class InstallerConflictTests
    {
        [Fact]
        public async Task TwoSimultaneousMockInstallerProcesses_SecondProcessExitsWithCode2_WithoutSemaphoreFullException()
        {
            StartupTaskHelper.IsElevatedOverride = true;
            StartupTaskHelper.MessageBoxHook = (_, _, _, _, _) => DialogResult.OK;

            using var proc1Lock = new Semaphore(1, 1, DeploymentFootprint.InstallerMutexName + "_v2");
            Assert.True(proc1Lock.WaitOne(0), "Primary mock installer process should acquire the semaphore.");

            try
            {
                int installExitCode = await DeploymentLifecycle.RunInstallAsync(CancellationToken.None, isWorkerOverride: true);
                Assert.Equal(2, installExitCode);

                int uninstallExitCode = await DeploymentLifecycle.RunUninstallAsync(new[] { "--cleanup-worker" });
                Assert.Equal(2, uninstallExitCode);

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -Command \"$s = [System.Threading.Semaphore]::OpenExisting('" +
                                DeploymentFootprint.InstallerMutexName.Replace(@"\", @"\\") + "_v2'); " +
                                "$acq = $s.WaitOne(0); " +
                                "try { if (-not $acq) { exit 2 } } finally { if ($acq) { $s.Release() } }\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc2 = Process.Start(psi);
                Assert.NotNull(proc2);
                await proc2.WaitForExitAsync();
                Assert.Equal(2, proc2.ExitCode);
            }
            finally
            {
                proc1Lock.Release();
                StartupTaskHelper.IsElevatedOverride = null;
                StartupTaskHelper.MessageBoxHook = null;
            }

            using var postLock = new Semaphore(1, 1, DeploymentFootprint.InstallerMutexName + "_v2");
            Assert.True(postLock.WaitOne(0), "Semaphore should be cleanly acquirable after primary release.");
            postLock.Release();
        }

        [Fact]
        public void StateGuardedCleanup_WhenLockNotAcquired_DoesNotThrowSemaphoreFullException()
        {
            using var externalLock = new Semaphore(1, 1, DeploymentFootprint.InstallerMutexName + "_v2");
            Assert.True(externalLock.WaitOne(0));

            try
            {
                using var testGate = new Semaphore(1, 1, DeploymentFootprint.InstallerMutexName + "_v2");
                bool lockAcquired = false;

                Exception? caughtException = null;
                try
                {
                    lockAcquired = testGate.WaitOne(0);
                    Assert.False(lockAcquired);
                }
                finally
                {
                    try
                    {
                        if (lockAcquired)
                        {
                            testGate.Release();
                        }
                    }
                    catch (Exception ex)
                    {
                        caughtException = ex;
                    }
                }

                Assert.Null(caughtException);
            }
            finally
            {
                externalLock.Release();
            }
        }
    }
}

