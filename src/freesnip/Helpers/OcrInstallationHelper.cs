using freesnip.native;
using freesnip.native.foundation;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using freesnip.foundation.core.AvaloniaShims;
using System.Linq;
using freesnip.foundation.core;
using log4net;

namespace freesnip.helpers
{
    public static class OcrInstallationHelper
    {
        private static readonly ILog Log = freesnip.foundation.core.LogHelper.GetLogger(typeof(OcrInstallationHelper));

        public static string GetTessDataDirectory(string installFolder = null)
        {
            return Path.Combine(ResolveOcrStorageRoot(installFolder), "tessdata");
        }

        private static string ResolveOcrStorageRoot(string explicitRoot)
        {
            if (!string.IsNullOrWhiteSpace(explicitRoot))
            {
                return explicitRoot;
            }

            if (StartupTaskHelper.IsRunningFromInstallPath())
            {
                return StartupTaskHelper.ConfigurationFolder;
            }

            return Path.Combine(DeploymentFootprint.TempAppFolder, "Brain");
        }

#if USE_TESSERACT
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetDllDirectory(string lpPathName);

        private static readonly SemaphoreSlim InitializationGate = new SemaphoreSlim(1, 1);
        private static int _initialized;

        public static async Task EnsureTesseractReadyAsync(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _initialized) == 1) return;

            await InitializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _initialized) == 1) return;
                
                string installFolder = AppDomain.CurrentDomain.BaseDirectory;
                await EnsureBinariesExtractedAsync(installFolder, cancellationToken).ConfigureAwait(false);
                await EnsureOfflineTessDataExtractedAsync(installFolder, cancellationToken).ConfigureAwait(false);

                Volatile.Write(ref _initialized, 1);
            }
            finally
            {
                InitializationGate.Release();
            }
        }

        private static async Task EnsureBinariesExtractedAsync(string installFolder, CancellationToken cancellationToken)
        {
            try
            {
                string targetPath = ResolveOcrStorageRoot(installFolder);
                SetDllDirectory(targetPath);
                
                var asm = System.Reflection.Assembly.GetEntryAssembly();
                if (asm == null) asm = typeof(OcrInstallationHelper).Assembly;
                var resources = asm.GetManifestResourceNames();
                string[] libs = { "leptonica-1.82.0.dll", "tesseract50.dll" };

                if (!Directory.Exists(targetPath)) Directory.CreateDirectory(targetPath);
                foreach (var lib in libs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string destPath = Path.Combine(targetPath, "x64", lib); Directory.CreateDirectory(Path.Combine(targetPath, "x64"));
                    if (File.Exists(destPath)) continue;

                    string resName = resources.FirstOrDefault(r => r.EndsWith(lib, StringComparison.OrdinalIgnoreCase));

                    if (resName != null)
                    {
                        using var s = asm.GetManifestResourceStream(resName);
                        if (s != null)
                        {
                            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, FileOptions.Asynchronous);
                            await s.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                foreach (var lib in libs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    LoadLibrary(Path.Combine(targetPath, "x64", lib));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) { Log.Error("Binary extraction failed", ex); }
        }

        private static async Task EnsureOfflineTessDataExtractedAsync(string installFolder, CancellationToken cancellationToken)
        {
            string tessDataPath = null;
            try
            {
                tessDataPath = Path.Combine(ResolveOcrStorageRoot(installFolder), "tessdata");
                Directory.CreateDirectory(tessDataPath);
                var assembly = System.Reflection.Assembly.GetEntryAssembly();
                if (assembly == null) assembly = typeof(OcrInstallationHelper).Assembly;
                var resources = assembly.GetManifestResourceNames();
                foreach (var languageFile in new[] { "heb.traineddata", "eng.traineddata" })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string destinationPath = Path.Combine(tessDataPath, languageFile);
                    if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length >= 128 * 1024)
                    {
                        continue;
                    }

                    string resourceName = resources.FirstOrDefault(resource => resource.EndsWith(languageFile, StringComparison.OrdinalIgnoreCase));
                    if (string.IsNullOrEmpty(resourceName))
                    {
                        ExecutionTrace.LogEvent("TesseractOcr", "MissingEmbeddedData", languageFile);
                        continue;
                    }

                    using (var resourceStream = assembly.GetManifestResourceStream(resourceName))
                    {
                        if (resourceStream == null)
                        {
                            ExecutionTrace.LogEvent("TesseractOcr", "MissingResourceStream", languageFile);
                            continue;
                        }

                        using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, FileOptions.Asynchronous))
                        {
                            await resourceStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    ExecutionTrace.LogEvent("TesseractOcr", "ExtractedData", destinationPath);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("Offline OCR data extraction failed", ex);
                ExecutionTrace.LogException("TesseractOcr.ExtractData", ex, tessDataPath);
            }
        }
#endif

        public static Task InstallHebrewOcrAsync(CancellationToken cancellationToken = default)
        {
#if USE_TESSERACT
            return EnsureTesseractReadyAsync(cancellationToken);
#else
            return native.Win10OcrProvider.EnsureWindowsOcrInstalled();
#endif
        }
    }
}

