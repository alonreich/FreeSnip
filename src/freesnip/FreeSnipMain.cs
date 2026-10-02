using System;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using freesnip.foundation.core;
using freesnip.foundation.IniFile;
using freesnip.helpers;
using freesnip.native;
using log4net;
using Avalonia;

namespace freesnip;

public class FreeSnipMain
{
    private static ILog LOG;
    public static string LogFileLocation;
    private static ResourceMutex s_singleInstanceMutex;

    static FreeSnipMain()
    {
        AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
    }

    private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
    {
        Assembly ayResult = null;
        string sShortAssemblyName = args.Name.Split(',')[0];
        Assembly[] ayAssemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (Assembly ayAssembly in ayAssemblies)
        {
            if (sShortAssemblyName != ayAssembly.FullName.Split(',')[0]) continue;
            ayResult = ayAssembly;
            break;
        }
        return ayResult;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    [STAThread]
    public static async Task Main(string[] args)
    {
        args ??= Array.Empty<string>();
        if (args.Any(a => string.Equals(a, "--test-probe", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("FreeSnip binary probe succeeded.");
            Environment.Exit(0);
            return;
        }

        bool isAdminStartupCommand = StartupTaskHelper.IsAdminStartupCommand(args);
        bool isInstaller = !StartupTaskHelper.IsRunningFromInstallPath() && !DeploymentLifecycle.IsLifecycleCommand(args) && !isAdminStartupCommand;
        bool isLifecycle = DeploymentLifecycle.IsLifecycleCommand(args) || isAdminStartupCommand;

        bool hasFiles = args.Any(a => !a.StartsWith("-") && !a.StartsWith("/"));

        if (!isInstaller && !isLifecycle)
        {
            s_singleInstanceMutex = ResourceMutex.Create(ResourceMutex.SingleInstanceArbitratorName, "FreeSnip instance");
            if (!s_singleInstanceMutex.IsLocked)
            {
                s_singleInstanceMutex.Dispose();
                s_singleInstanceMutex = null;

                BootstrapDebug.Log("Secondary instance detected. Relaying command via IPC...");
                Win32WindowHelper.AllowSetForegroundWindow(Win32WindowHelper.ASFW_ANY);
                await InstanceIpcClient.SendRelayCommandAsync(args, timeoutMs: 500).ConfigureAwait(false);
                return;
            }

            if (!hasFiles && await TryRedirectToElevatedStartupAsync(args).ConfigureAwait(false))
            {
                s_singleInstanceMutex.Dispose();
                s_singleInstanceMutex = null;
                return;
            }

            App.SetInstanceArbitrator(s_singleInstanceMutex);
        }

        InstallHostContext.WriteEarlyTrace("ENTER Main PID=" + Environment.ProcessId + " exe=" + Environment.ProcessPath);
        InstallHostContext.WriteEarlyTrace("Args: " + string.Join(' ', args));

        BootstrapDebug.Clear();

        BootstrapDebug.Log("--- Application Bootstrap Starting ---");
        BootstrapDebug.Log("Args: " + string.Join(' ', args));
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += Task_UnhandledException;

        try
        {
            if (isAdminStartupCommand)
            {
                Environment.Exit(await StartupTaskHelper.RunAdminStartupCommandAsync(args).ConfigureAwait(false));
                return;
            }

            if (DeploymentLifecycle.IsLifecycleCommand(args))
            {
                BootstrapDebug.Log("Installer mode detected. Flowing to Avalonia for UI...");
            }

            if (!StartupTaskHelper.IsRunningFromInstallPath()
                && !DeploymentLifecycle.IsLifecycleCommand(args))
            {
                BootstrapDebug.Log("Stage 1: Relocating installer to temporary directory.");
                string tempInstallerPath = Path.Combine(DeploymentFootprint.DeploymentTempRoot, "Install", Path.GetFileName(RuntimePathHelper.ExecutablePath));
                string tempDir = Path.GetDirectoryName(tempInstallerPath);
                if (!string.IsNullOrEmpty(tempDir)) Directory.CreateDirectory(tempDir);

                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        if (File.Exists(tempInstallerPath))
                        {
                            File.SetAttributes(tempInstallerPath, FileAttributes.Normal);
                        }
                        File.Copy(RuntimePathHelper.ExecutablePath, tempInstallerPath, true);
                        File.SetAttributes(tempInstallerPath, FileAttributes.Normal);
                        break;
                    }
                    catch
                    {
                        if (attempt == 4) throw;
                        Thread.Sleep(150);
                    }
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = tempInstallerPath,
                    Arguments = "--install --install-worker",
                    UseShellExecute = true,
                    Verb = "runas"
                });
                return;
            }

            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.Name = RuntimePathHelper.ProductName;

            LogFileLocation = LogHelper.InitializeLog4Net();
            LOG = LogHelper.GetLogger("freesnip");

            if (PayloadExtractor.HasEmbeddedPayload())
            {
                BootstrapDebug.Log("Extracting Avalonia/Skia dependencies for desktop host...");
                PayloadExtractor.ExtractCriticalDependencies();
            }

            BootstrapDebug.Log("Launching Avalonia App...");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            string msg = "CRITICAL BOOTSTRAP ERROR: " + ex.ToString();
            BootstrapDebug.Log($"Main Exception: {ex}");
            InstallHostContext.WriteEarlyTrace("Main exception: " + ex.Message);
            if (LOG != null) LOG.Fatal(msg);
            ExecutionTrace.LogException("Bootstrap.Main", ex, msg);
        }
        finally
        {
            s_singleInstanceMutex?.Dispose();
            s_singleInstanceMutex = null;
        }
    }

    private static async Task<bool> TryRedirectToElevatedStartupAsync(string[] args)
    {
        try
        {
            if (StartupTaskHelper.IsElevated())
            {
                return false;
            }

            if (args.Any(a => a.Equals("--autorun", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (!StartupTaskHelper.IsRunningFromInstallPath())
            {
                return false;
            }

            Directory.CreateDirectory(StartupTaskHelper.ConfigurationFolder);
            IniConfigurationDeployer.EnsureDefaultsFile(StartupTaskHelper.ConfigurationFolder);
            IniConfigurationDeployer.EnsureUserConfiguration(StartupTaskHelper.ConfigurationFolder);
            IniConfig.IniDirectory = StartupTaskHelper.ConfigurationFolder;
            IniConfig.Init("FreeSnip", IniConfigurationDeployer.ConfigBaseName);

            var core = IniConfig.GetIniSection<CoreConfiguration>();
            if (!core.RunAsAdministratorOnStartup)
            {
                return false;
            }

            if (!await StartupTaskHelper.HasElevatedStartupTaskAsync().ConfigureAwait(false))
            {
                return false;
            }

            return await StartupTaskHelper.TryRunElevatedStartupTaskAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try { ExecutionTrace.LogException("FreeSnipMain.TryRedirectToElevatedStartup", ex, string.Empty); } catch { }
            return false;
        }
    }

    internal static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Exception ex = e.ExceptionObject as Exception;
        LogHelper.LogCrash("AppDomain.UnhandledException", ex, e.ExceptionObject);
        LOG?.Fatal("UnhandledException: " + (ex?.Message ?? e.ExceptionObject?.ToString() ?? "Unknown"), ex);
        BootstrapDebug.Log($"UnhandledException: {ex ?? (object)e.ExceptionObject}");
        InstallHostContext.WriteEarlyTrace("UnhandledException: " + (ex ?? (object)e.ExceptionObject));
        ExecutionTrace.LogException("AppDomain.UnhandledException", ex, string.Empty);
    }

    internal static void Task_UnhandledException(object sender, UnobservedTaskExceptionEventArgs args)
    {
        Exception ex = args.Exception;
        LogHelper.LogCrash("TaskScheduler.UnobservedTaskException", ex, args.Exception);
        LOG?.Fatal("TaskException: " + ex?.Message, ex);
        BootstrapDebug.Log($"TaskException: {ex}");
        ExecutionTrace.LogException("Task.UnobservedTaskException", ex, string.Empty);
        args.SetObserved();
    }
}
