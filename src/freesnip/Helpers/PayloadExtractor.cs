using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace freesnip.helpers
{
    public static class PayloadExtractor
    {
        private const string PayloadName = "payload.zip";
        private static string _nativeDependencyDirectory;
        private static readonly List<IntPtr> LoadedNativeHandles = new();

        public static bool HasEmbeddedPayload()
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceNames().Any(n => n.EndsWith(PayloadName, StringComparison.OrdinalIgnoreCase));
        }

        public static string GetNativeDependencyDirectory()
        {
            if (!string.IsNullOrEmpty(_nativeDependencyDirectory))
            {
                return _nativeDependencyDirectory;
            }

            if (StartupTaskHelper.IsRunningFromInstallPath())
            {
                _nativeDependencyDirectory = AppContext.BaseDirectory;
                return _nativeDependencyDirectory;
            }

            try
            {
                string installDir = StartupTaskHelper.InstallFolder;
                Directory.CreateDirectory(installDir);
                _nativeDependencyDirectory = installDir;
                NativeDllSearchPath.Register(_nativeDependencyDirectory);
                return _nativeDependencyDirectory;
            }
            catch
            {
                _nativeDependencyDirectory = Path.Combine(
                    DeploymentFootprint.TempAppFolder,
                    "Runtime",
                    $"{Process.GetCurrentProcess().Id}_{Guid.NewGuid():N}");
                Directory.CreateDirectory(_nativeDependencyDirectory);
                NativeDllSearchPath.Register(_nativeDependencyDirectory);
                return _nativeDependencyDirectory;
            }
        }

        public static void ExtractCriticalDependencies()
        {
            string[] criticalDlls = { "av_libglesv2.dll", "libHarfBuzzSharp.dll", "libSkiaSharp.dll" };
            string targetDir = GetNativeDependencyDirectory();

            if (StartupTaskHelper.IsRunningFromInstallPath())
            {
                return;
            }

            string resourceName = Assembly.GetExecutingAssembly().GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(PayloadName, StringComparison.OrdinalIgnoreCase));
            if (resourceName == null) return;

            using Stream zipStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (zipStream == null) throw new InvalidDataException("Embedded payload stream is unavailable.");

            using ZipArchive archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false);
            foreach (string dll in criticalDlls)
            {
                ZipArchiveEntry entry = archive.GetEntry(dll);
                if (entry == null) continue;
                string destination = Path.Combine(targetDir, dll);
                if (File.Exists(destination)) continue;

                using Stream input = entry.Open();
                using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, FileOptions.WriteThrough);
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            NativeDllSearchPath.Register(targetDir);
            foreach (string dll in criticalDlls)
            {
                string fullPath = Path.Combine(targetDir, dll);
                if (File.Exists(fullPath))
                {
                    IntPtr handle = NativeLibrary.Load(fullPath);
                    lock (LoadedNativeHandles) LoadedNativeHandles.Add(handle);
                }
            }
        }

        public static async System.Threading.Tasks.Task ExtractToAsync(string targetDirectory, CancellationToken cancellationToken = default)
        {
            string resourceName = Assembly.GetExecutingAssembly().GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(PayloadName, StringComparison.OrdinalIgnoreCase));
            if (resourceName == null)
            {
                return;
            }

            Directory.CreateDirectory(targetDirectory);
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                return;
            }

            using ZipArchive archive = new ZipArchive(stream);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string destinationPath = Path.GetFullPath(Path.Combine(targetDirectory, entry.FullName));
                if (!destinationPath.StartsWith(Path.GetFullPath(targetDirectory), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destinationPath);
                }
                else
                {
                    string parent = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }

                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            if (File.Exists(destinationPath))
                            {
                                File.SetAttributes(destinationPath, FileAttributes.Normal);
                            }

                            entry.ExtractToFile(destinationPath, true);
                            File.SetAttributes(destinationPath, FileAttributes.Normal);
                            break;
                        }
                        catch (IOException)
                        {
                            if (attempt == 4) throw;
                            await System.Threading.Tasks.Task.Delay(100, cancellationToken).ConfigureAwait(false);
                        }
                        catch (UnauthorizedAccessException)
                        {
                            if (attempt == 4) throw;
                            await System.Threading.Tasks.Task.Delay(100, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }
        }
    }
}
