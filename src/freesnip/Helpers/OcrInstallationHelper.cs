using freesnip.native;
using freesnip.native.foundation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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
        private static readonly SemaphoreSlim InitializationGate = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim DataInitializationGate = new SemaphoreSlim(1, 1);
        private static readonly List<IntPtr> NativeHandles = new List<IntPtr>();
        private static int _initialized;
        private static int _dataInitialized;
        private static string _nativeRuntimeDirectory;

        public static async Task EnsureTesseractReadyAsync(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _initialized) == 1) return;

            await InitializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _initialized) == 1) return;

                await EnsureBinariesExtractedAsync(cancellationToken).ConfigureAwait(false);
                await EnsureTessDataReadyAsync(cancellationToken).ConfigureAwait(false);

                Volatile.Write(ref _initialized, 1);
            }
            finally
            {
                InitializationGate.Release();
            }
        }

        internal static async Task EnsureTessDataReadyAsync(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _dataInitialized) == 1) return;

            await DataInitializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _dataInitialized) == 1) return;
                await EnsureOfflineTessDataExtractedAsync(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _dataInitialized, 1);
            }
            finally
            {
                DataInitializationGate.Release();
            }
        }

        private static async Task EnsureBinariesExtractedAsync(CancellationToken cancellationToken)
        {
            try
            {
                string runtimeDirectory = Path.Combine(
                    DeploymentFootprint.TempAppFolder,
                    "OcrRuntime",
                    Environment.ProcessId.ToString() + "_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(runtimeDirectory);

                var assembly = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(OcrInstallationHelper).Assembly;
                string[] resources = assembly.GetManifestResourceNames();
                string[] libs = { "leptonica-1.82.0.dll", "tesseract50.dll" };

                foreach (string lib in libs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string resourceName = resources.FirstOrDefault(r => r.EndsWith(lib, StringComparison.OrdinalIgnoreCase));
                    if (string.IsNullOrEmpty(resourceName))
                    {
                        throw new FileNotFoundException("Embedded Tesseract dependency is missing.", lib);
                    }

                    string destination = Path.Combine(runtimeDirectory, lib);
                    await using Stream source = assembly.GetManifestResourceStream(resourceName)
                        ?? throw new FileNotFoundException("Embedded Tesseract dependency stream is missing.", lib);
                    await using (var target = new FileStream(
                        destination,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        81920,
                        FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                        target.Flush(flushToDisk: true);
                    }
                }

                NativeDllSearchPath.Register(runtimeDirectory);
                foreach (string lib in libs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IntPtr handle = NativeLibrary.Load(Path.Combine(runtimeDirectory, lib));
                    if (handle == IntPtr.Zero)
                    {
                        throw new DllNotFoundException("Could not load isolated Tesseract dependency: " + lib);
                    }
                    NativeHandles.Add(handle);
                }

                _nativeRuntimeDirectory = runtimeDirectory;
                ExecutionTrace.LogEvent("TesseractOcr", "NativeRuntimeReady", _nativeRuntimeDirectory);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("Binary extraction failed", ex);
                ExecutionTrace.LogException("TesseractOcr.ExtractBinaries", ex, _nativeRuntimeDirectory);
                throw;
            }
        }

        private static async Task EnsureOfflineTessDataExtractedAsync(CancellationToken cancellationToken)
        {
            string tessDataPath = null;
            try
            {
                tessDataPath = GetTessDataDirectory();
                Directory.CreateDirectory(tessDataPath);
                var assembly = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(OcrInstallationHelper).Assembly;
                string[] resources = assembly.GetManifestResourceNames();
                foreach (string languageFile in new[] { "heb.traineddata", "eng.traineddata" })
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
                        throw new FileNotFoundException("Embedded Tesseract language data is missing.", languageFile);
                    }

                    string temporaryPath = destinationPath + ".tmp_" + Guid.NewGuid().ToString("N");
                    try
                    {
                        await using Stream resourceStream = assembly.GetManifestResourceStream(resourceName)
                            ?? throw new FileNotFoundException("Embedded Tesseract language stream is missing.", languageFile);
                        await using (var fileStream = new FileStream(
                            temporaryPath,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            81920,
                            FileOptions.Asynchronous | FileOptions.WriteThrough))
                        {
                            await resourceStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                            await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                            fileStream.Flush(flushToDisk: true);
                        }

                        File.Move(temporaryPath, destinationPath, overwrite: true);
                    }
                    finally
                    {
                        try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
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
                throw;
            }
        }
#endif

        public static Task InstallHebrewOcrAsync(CancellationToken cancellationToken = default)
        {
#if USE_TESSERACT
            // The desktop host only prepares language data. Native Tesseract code is loaded solely
            // inside the supervised OCR worker process so a native hang cannot poison the UI host.
            return EnsureTessDataReadyAsync(cancellationToken);
#else
            return native.Win10OcrProvider.EnsureWindowsOcrInstalled();
#endif
        }
    }
}
