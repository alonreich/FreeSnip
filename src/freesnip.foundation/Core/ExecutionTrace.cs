using freesnip.native;
using freesnip.native.foundation;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using freesnip.foundation.Interop;
using log4net;

namespace freesnip.foundation.core
{
    public static class ExecutionTrace
    {
        private static readonly ILog Log = freesnip.foundation.core.LogHelper.GetLogger(typeof(ExecutionTrace));
        private static readonly Process CurrentProcess = Process.GetCurrentProcess();
        private static readonly object SyncRoot = new object();
        private static readonly ConcurrentDictionary<string, int> QueueDepths = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly double TickToMilliseconds = 1000d / Stopwatch.Frequency;
        private const int MaxQueuedEntries = 8192;


        private const int MaxRetainedLogFiles = 5;
        private const int MaxLogAgeDays = 7;
        private const long MaxLogFileSizeBytes = 2L * 1024L * 1024L;
        private const long MaxProgramDataLogBudgetBytes = 10L * 1024L * 1024L;
        private static readonly TimeSpan LogBudgetSweepInterval = TimeSpan.FromMinutes(10);
        private static Channel<string> _entries;
        private static Task _writerTask;
        private static System.Timers.Timer _healthTimer;
        private static string _logPath;
        private static string _logDirectory;
        private static int _started;
        private static int _shutdown;
        private static long _droppedEntries;
        private static long _entriesSinceLastPulse;
        private const double HealthPulseIntervalMs = 60000d;


        private static DateTime _lastBudgetSweepUtc = DateTime.MinValue;

        public static string LogPath
        {
            get
            {
                EnsureInitialized();
                return Volatile.Read(ref _logPath);
            }
        }

        public static void Start()
        {
            Interlocked.Exchange(ref _shutdown, 0);
            EnsureInitialized();
            LogEvent("ExecutionTrace", "Start", AppDomain.CurrentDomain.FriendlyName);
        }

        public static void Stop()
        {
            Stop(false, 0);
        }

        private static void Stop(bool waitForWriter, int millisecondsTimeout)
        {
            Task writerTask;
            lock (SyncRoot)
            {
                Interlocked.Exchange(ref _shutdown, 1);

                if (Volatile.Read(ref _started) == 0)
                {
                    return;
                }

                try
                {
                    LogEvent("ExecutionTrace", "Stop", AppDomain.CurrentDomain.FriendlyName);
                }
                catch
                {
                }

                Interlocked.Exchange(ref _started, 0);

                try
                {
                    if (_healthTimer != null)
                    {
                        _healthTimer.Stop();
                        _healthTimer.Dispose();
                        _healthTimer = null;
                    }
                }
                catch
                {
                }

                try
                {
                    _entries?.Writer.TryComplete();
                }
                catch
                {
                }

                writerTask = _writerTask;
                _writerTask = null;
                _entries = null;
            }

            if (writerTask != null)
            {
                Task.Run(async delegate
                {
                    try
                    {
                        await writerTask.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("ExecutionTrace writer shutdown failed.", ex);
                    }
                });
            }
        }

        public static void SetQueueDepth(string queueName, int depth)
        {
            EnsureInitialized();
            if (string.IsNullOrWhiteSpace(queueName))
            {
                return;
            }

            QueueDepths[queueName] = Math.Max(0, depth);
            LogEvent("QueueDepth", queueName, BuildQueueSnapshot());
        }

        public static void LogEvent(string operation, string stage, string context = null)
        {
            WriteEntry(operation, stage, 0L, null, context);
        }

        public static void LogOperation(string operation, Stopwatch stopwatch, string context = null)
        {
            if (stopwatch == null)
            {
                LogOperation(operation, 0L, context);
                return;
            }

            LogOperation(operation, stopwatch.ElapsedTicks, context);
        }

        public static void LogOperation(string operation, long elapsedTicks, string context = null)
        {
            WriteEntry(operation, "Completed", elapsedTicks, null, context);
        }

        public static void LogException(string operation, Exception exception, string context = null)
        {
            if (exception == null)
            {
                return;
            }

            WriteEntry(operation, "Exception", 0L, exception, context);
        }

        private static int _gdiCount;
        private static int _userCount;
        private static long _workingSetMb;
        private static long _gcMemoryMb;

        private static void RefreshHealthCounters()
        {
            try
            {
                _gdiCount = unchecked((int)User32Api.GetGuiResourcesGdiCount());
                _userCount = unchecked((int)User32Api.GetGuiResourcesUserCount());
                CurrentProcess.Refresh();
                _workingSetMb = CurrentProcess.WorkingSet64 / 1024L / 1024L;
                _gcMemoryMb = GC.GetTotalMemory(false) / 1024L / 1024L;
            }
            catch { }
        }

        private static void LogHealth()
        {
            RefreshHealthCounters();
            WriteEntry("Health", "Pulse", 0L, null, BuildQueueSnapshot());
        }

        private static void OnHealthTimerElapsed()
        {
            try
            {
                if (Volatile.Read(ref _shutdown) != 0) return;

                if (Interlocked.Exchange(ref _entriesSinceLastPulse, 0L) > 0L)
                {
                    LogHealth();
                }
                else
                {
                    RefreshHealthCounters();
                }

                TrySweepLogBudget();
            }
            catch (Exception ex)
            {
                Log.Warn("ExecutionTrace health pulse failed.", ex);
            }
        }

        private static void EnsureInitialized()
        {
            if (Volatile.Read(ref _started) == 1 || Volatile.Read(ref _shutdown) != 0)
            {
                return;
            }

            lock (SyncRoot)
            {
                if (Volatile.Read(ref _started) == 1 || Volatile.Read(ref _shutdown) != 0)
                {
                    return;
                }

                string appData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                string logDir = Path.Combine(appData, "FreeSnip");
                Directory.CreateDirectory(logDir);
                CleanupOldLogs(logDir);
                EnforceProgramDataLogBudget(logDir);
                _lastBudgetSweepUtc = DateTime.UtcNow;
                Volatile.Write(ref _logDirectory, logDir);
                Volatile.Write(ref _logPath, CreateLogPath());
                var entries = Channel.CreateBounded<string>(new BoundedChannelOptions(MaxQueuedEntries)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
                _entries = entries;
                _writerTask = Task.Run(delegate { return ProcessQueueAsync(entries); });

                Interlocked.Exchange(ref _started, 1);
                LogHealth();

                _healthTimer = new System.Timers.Timer(HealthPulseIntervalMs);
                _healthTimer.AutoReset = true;
                _healthTimer.Elapsed += delegate { OnHealthTimerElapsed(); };
                _healthTimer.Start();
            }
        }

        private static async Task ProcessQueueAsync(Channel<string> entries)
        {
            FileStream stream = null;
            StreamWriter writer = null;
            try
            {
                await foreach (var line in entries.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    EnsureWriter(ref stream, ref writer);
                    WriteDroppedSummary(writer);
                    writer.WriteLine(line);

                    if (stream != null && stream.Length >= MaxLogFileSizeBytes)
                    {
                        writer.Flush();
                        writer.Dispose();
                        stream.Dispose();
                        writer = null;
                        stream = null;
                        string newPath = CreateLogPath();
                        Volatile.Write(ref _logPath, newPath);
                        try { CleanupOldLogs(Volatile.Read(ref _logDirectory)); EnforceProgramDataLogBudget(Volatile.Read(ref _logDirectory)); } catch (Exception cleanupEx) { Log.Warn("ExecutionTrace cleanup on rotation failed.", cleanupEx); }
                    }
                }

                if (writer != null)
                {
                    WriteDroppedSummary(writer);
                }
            }
            catch (Exception ex)
            {
                Log.Error("ExecutionTrace writer failed.", ex);
            }
            finally
            {
                if (writer != null)
                {
                    writer.Dispose();
                }

                if (stream != null)
                {
                    stream.Dispose();
                }
            }
        }

        private static void WriteEntry(string operation, string stage, long elapsedTicks, Exception exception, string context)
        {
            EnsureInitialized();

            try
            {
                var entries = _entries;
                if (entries == null)
                {
                    return;
                }

                string line = string.Format(
                    "{0:O}|Thread={1}|Operation={2}|Stage={3}|ElapsedMs={4:F4}|ElapsedTicks={5}|WorkingSetMB={6}|GCMemoryMB={7}|GDI={8}|USER={9}|QueueDepths={10}|Context={11}|Exception={12}",
                    DateTime.Now,
                    Thread.CurrentThread.ManagedThreadId,
                    Sanitize(operation),
                    Sanitize(stage),
                    elapsedTicks * TickToMilliseconds,
                    elapsedTicks,
                    _workingSetMb,
                    _gcMemoryMb,
                    _gdiCount,
                    _userCount,
                    Sanitize(BuildQueueSnapshot()),
                    Sanitize(context),
                    Sanitize(exception == null ? string.Empty : exception.ToString()));

                if (entries.Writer.TryWrite(line))
                {
                    Interlocked.Increment(ref _entriesSinceLastPulse);
                }
                else
                {
                    Interlocked.Increment(ref _droppedEntries);
                }
            }
            catch (Exception ex)
            {
                Log.Error("ExecutionTrace entry write failed.", ex);
            }
        }

        private static void EnsureWriter(ref FileStream stream, ref StreamWriter writer)
        {
            if (stream != null && writer != null)
            {
                return;
            }

            string path = Volatile.Read(ref _logPath);
            if (string.IsNullOrEmpty(path))
            {
                path = CreateLogPath();
                Volatile.Write(ref _logPath, path);
            }

            stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            writer = new StreamWriter(stream, Encoding.UTF8);
            writer.AutoFlush = true;
        }

        private static void WriteDroppedSummary(StreamWriter writer)
        {
            long dropped = Interlocked.Exchange(ref _droppedEntries, 0);
            if (dropped <= 0 || writer == null)
            {
                return;
            }

            writer.WriteLine(string.Format(
                "{0:O}|Thread={1}|Operation=ExecutionTrace|Stage=QueueOverflow|ElapsedMs=0.0000|ElapsedTicks=0|WorkingSetMB=0|GCMemoryMB=0|GDI=0|USER=0|QueueDepths={2}|Context=DroppedEntries={3}|Exception=",
                DateTime.Now,
                Thread.CurrentThread.ManagedThreadId,
                Sanitize(BuildQueueSnapshot()),
                dropped));
        }

        private static string CreateLogPath()
        {
            string logDir = Volatile.Read(ref _logDirectory);
            if (string.IsNullOrEmpty(logDir))
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                logDir = Path.Combine(appData, "FreeSnip");
                Directory.CreateDirectory(logDir);
                Volatile.Write(ref _logDirectory, logDir);
            }

            return Path.Combine(logDir, string.Format("ExecutionTrace_{0:yyyyMMdd_HHmmss_fff}.log", DateTime.Now));
        }

        private static void CleanupOldLogs(string logDir)
        {
            try
            {
                var files = new DirectoryInfo(logDir)
                    .GetFiles("ExecutionTrace_*.log")
                    .OrderByDescending(file => file.CreationTimeUtc)
                    .ToList();

                DateTime cutoff = DateTime.UtcNow.AddDays(-MaxLogAgeDays);
                for (int i = 0; i < files.Count; i++)
                {
                    if (i >= MaxRetainedLogFiles || files[i].CreationTimeUtc < cutoff)
                    {
                        try
                        {
                            files[i].Delete();
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("ExecutionTrace cleanup failed for " + files[i].FullName, ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("ExecutionTrace cleanup scan failed.", ex);
            }
        }

        private static void TrySweepLogBudget()
        {
            try
            {
                if (DateTime.UtcNow - _lastBudgetSweepUtc < LogBudgetSweepInterval) return;
                _lastBudgetSweepUtc = DateTime.UtcNow;
                EnforceProgramDataLogBudget(Volatile.Read(ref _logDirectory));
            }
            catch (Exception ex)
            {
                Log.Warn("ExecutionTrace budget sweep timer failed.", ex);
            }
        }


        private static void EnforceProgramDataLogBudget(string logDir)
        {
            try
            {
                if (string.IsNullOrEmpty(logDir) || !Directory.Exists(logDir)) return;
                var files = new DirectoryInfo(logDir)
                    .GetFiles("*.log")
                    .OrderBy(file => file.LastWriteTimeUtc)
                    .ToList();
                long total = 0;
                foreach (var file in files)
                {
                    total += file.Length;
                }

                foreach (var file in files)
                {
                    if (total <= MaxProgramDataLogBudgetBytes) break;
                    long size = file.Length;
                    try
                    {
                        file.Delete();
                        total -= size;
                        Log.Info("Log budget sweep deleted " + file.Name);
                    }
                    catch
                    {

                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("ExecutionTrace budget sweep failed.", ex);
            }
        }

        private static string BuildQueueSnapshot()
        {
            if (QueueDepths.IsEmpty)
            {
                return "None";
            }

            return string.Join(",", QueueDepths.OrderBy(kvp => kvp.Key).Select(kvp => string.Format("{0}={1}", kvp.Key, kvp.Value)));
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value.Replace("\r", " ").Replace("\n", " ").Replace("|", "/").Trim();
        }
    }
}
