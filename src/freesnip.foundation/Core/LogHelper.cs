using freesnip.native;
using freesnip.native.foundation;
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using freesnip.foundation.IniFile;
using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Core;
using log4net.Repository.Hierarchy;
using log4net.Util;

namespace freesnip.foundation.core
{

    public class LogHelper
    {
        private static bool _isLog4NetConfigured;
        private static string _fallbackLogFile;
        private static readonly object FallbackLogLock = new object();

        public static bool IsInitialized => _isLog4NetConfigured || !string.IsNullOrEmpty(_fallbackLogFile);

        public static ILog GetLogger(Type type)
        {
            return GetLogger(type?.FullName ?? RuntimePathHelper.ProductName);
        }

        public static ILog GetLogger(string name)
        {
            if (!RuntimeFeature.IsDynamicCodeSupported)
            {
                EnsureFallbackLogFile();
                return new FallbackLog(name);
            }

            try
            {
                return LogManager.GetLogger(name);
            }
            catch
            {
                EnsureFallbackLogFile();
                return new FallbackLog(name);
            }
        }

        public static string InitializeLog4Net()
        {
            if (!RuntimeFeature.IsDynamicCodeSupported)
            {
                return EnsureFallbackLogFile();
            }

            if (RuntimeFeature.IsDynamicCodeSupported)
            {
                foreach (var logName in new[]
                {
                    "log4net.xml", @"App\FreeSnip\log4net-portable.xml"
                })
                {
                    string log4NetFilename = Path.Combine(RuntimePathHelper.StartupPath, logName);
                    if (!File.Exists(log4NetFilename))
                    {
                        continue;
                    }

                    try
                    {
                        XmlConfigurator.Configure(new FileInfo(log4NetFilename));
                        _isLog4NetConfigured = true;
                        break;
                    }
                    catch
                    {
                    }
                }

                if (!_isLog4NetConfigured)
                {
                    try
                    {
                        Assembly assembly = typeof(LogHelper).Assembly;
                        string[] resourceNames = { "freesnip.foundation.log4net-embedded.xml", "FreeSnipPlugin.log4net-embedded.xml", "log4net-embedded.xml" };
                        Stream stream = null;
                        foreach (var name in resourceNames)
                        {
                            stream = assembly.GetManifestResourceStream(name);
                            if (stream != null) break;
                        }

                        if (stream != null)
                        {
                            using (stream)
                            {
                                XmlConfigurator.Configure(stream);
                                _isLog4NetConfigured = true;
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }

            if (!_isLog4NetConfigured)
            {
                string defaultLogFile = ConfigureDefaultFileAppender();
                if (!string.IsNullOrEmpty(defaultLogFile))
                {
                    return defaultLogFile;
                }
            }

            if (_isLog4NetConfigured)
            {
                string configuredLogFile = ConfigureFileAppenders();
                if (!string.IsNullOrEmpty(configuredLogFile))
                {
                    return configuredLogFile;
                }

                try
                {
                    if (((Hierarchy) LogManager.GetRepository()).Root.Appenders.Count > 0)
                    {
                        foreach (IAppender appender in ((Hierarchy) LogManager.GetRepository()).Root.Appenders)
                        {
                            var fileAppender = appender as FileAppender;
                            if (fileAppender != null)
                            {
                                return fileAppender.File;
                            }
                        }
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static string ConfigureDefaultFileAppender()
        {
            try
            {
                string logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FreeSnip");
                Directory.CreateDirectory(logDirectory);
                string logFile = Path.Combine(logDirectory, "FreeSnip.log");
                var layout = new log4net.Layout.PatternLayout("%date [%thread] %-5level - [%logger] %message%newline%exception");
                layout.ActivateOptions();
                var appender = new FileAppender
                {
                    AppendToFile = true,
                    File = logFile,
                    Layout = layout,
                    LockingModel = new FileAppender.MinimalLock()
                };
                appender.ActivateOptions();
                BasicConfigurator.Configure(appender);
                _isLog4NetConfigured = true;
                return logFile;
            }
            catch
            {
                return null;
            }
        }

        private static string EnsureFallbackLogFile()
        {
            if (!string.IsNullOrEmpty(_fallbackLogFile))
            {
                return _fallbackLogFile;
            }

            lock (FallbackLogLock)
            {
                if (!string.IsNullOrEmpty(_fallbackLogFile))
                {
                    return _fallbackLogFile;
                }

                try
                {
                    string logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FreeSnip");
                    Directory.CreateDirectory(logDirectory);
                    _fallbackLogFile = Path.Combine(logDirectory, "FreeSnip.log");
                }
                catch
                {
                    _fallbackLogFile = null;
                }

                return _fallbackLogFile;
            }
        }

        private sealed class FallbackLog : ILog
        {
            private readonly string _name;

            public FallbackLog(string name)
            {
                _name = string.IsNullOrWhiteSpace(name) ? RuntimePathHelper.ProductName : name;
            }

            public ILogger Logger => null;
            public bool IsDebugEnabled => false;
            public bool IsInfoEnabled => true;
            public bool IsWarnEnabled => true;
            public bool IsErrorEnabled => true;
            public bool IsFatalEnabled => true;

            public void Debug(object message) { }
            public void Debug(object message, Exception exception) { }
            public void DebugFormat(string format, params object[] args) { }
            public void DebugFormat(string format, object arg0) { }
            public void DebugFormat(string format, object arg0, object arg1) { }
            public void DebugFormat(string format, object arg0, object arg1, object arg2) { }
            public void DebugFormat(IFormatProvider provider, string format, params object[] args) { }

            public void Info(object message) => Write("INFO", message, null);
            public void Info(object message, Exception exception) => Write("INFO", message, exception);
            public void InfoFormat(string format, params object[] args) => Write("INFO", Format(CultureInfo.InvariantCulture, format, args), null);
            public void InfoFormat(string format, object arg0) => InfoFormat(format, new[] { arg0 });
            public void InfoFormat(string format, object arg0, object arg1) => InfoFormat(format, new[] { arg0, arg1 });
            public void InfoFormat(string format, object arg0, object arg1, object arg2) => InfoFormat(format, new[] { arg0, arg1, arg2 });
            public void InfoFormat(IFormatProvider provider, string format, params object[] args) => Write("INFO", Format(provider, format, args), null);

            public void Warn(object message) => Write("WARN", message, null);
            public void Warn(object message, Exception exception) => Write("WARN", message, exception);
            public void WarnFormat(string format, params object[] args) => Write("WARN", Format(CultureInfo.InvariantCulture, format, args), null);
            public void WarnFormat(string format, object arg0) => WarnFormat(format, new[] { arg0 });
            public void WarnFormat(string format, object arg0, object arg1) => WarnFormat(format, new[] { arg0, arg1 });
            public void WarnFormat(string format, object arg0, object arg1, object arg2) => WarnFormat(format, new[] { arg0, arg1, arg2 });
            public void WarnFormat(IFormatProvider provider, string format, params object[] args) => Write("WARN", Format(provider, format, args), null);

            public void Error(object message) => Write("ERROR", message, null);
            public void Error(object message, Exception exception) => Write("ERROR", message, exception);
            public void ErrorFormat(string format, params object[] args) => Write("ERROR", Format(CultureInfo.InvariantCulture, format, args), null);
            public void ErrorFormat(string format, object arg0) => ErrorFormat(format, new[] { arg0 });
            public void ErrorFormat(string format, object arg0, object arg1) => ErrorFormat(format, new[] { arg0, arg1 });
            public void ErrorFormat(string format, object arg0, object arg1, object arg2) => ErrorFormat(format, new[] { arg0, arg1, arg2 });
            public void ErrorFormat(IFormatProvider provider, string format, params object[] args) => Write("ERROR", Format(provider, format, args), null);

            public void Fatal(object message) => Write("FATAL", message, null);
            public void Fatal(object message, Exception exception) => Write("FATAL", message, exception);
            public void FatalFormat(string format, params object[] args) => Write("FATAL", Format(CultureInfo.InvariantCulture, format, args), null);
            public void FatalFormat(string format, object arg0) => FatalFormat(format, new[] { arg0 });
            public void FatalFormat(string format, object arg0, object arg1) => FatalFormat(format, new[] { arg0, arg1 });
            public void FatalFormat(string format, object arg0, object arg1, object arg2) => FatalFormat(format, new[] { arg0, arg1, arg2 });
            public void FatalFormat(IFormatProvider provider, string format, params object[] args) => Write("FATAL", Format(provider, format, args), null);

            private static string Format(IFormatProvider provider, string format, object[] args)
            {
                try
                {
                    return string.Format(provider ?? CultureInfo.InvariantCulture, format, args);
                }
                catch
                {
                    return format;
                }
            }

            private void Write(string level, object message, Exception exception)
            {
                string logFile = EnsureFallbackLogFile();
                if (string.IsNullOrEmpty(logFile))
                {
                    return;
                }

                try
                {
                    lock (FallbackLogLock)
                    {
                        File.AppendAllText(
                            logFile,
                            DateTime.Now.ToString("O", CultureInfo.InvariantCulture) + " " + level + " [" + _name + "] " + message + (exception == null ? string.Empty : Environment.NewLine + exception) + Environment.NewLine);
                    }
                }
                catch
                {
                }
            }
        }

        private static string ConfigureFileAppenders()
        {
            try
            {
                string logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FreeSnip");
                Directory.CreateDirectory(logDirectory);
                string configuredLogFile = null;
                var hierarchy = (Hierarchy)LogManager.GetRepository();
                foreach (IAppender appender in hierarchy.Root.Appenders)
                {
                    if (appender is FileAppender fileAppender)
                    {
                        string fileName = Path.GetFileName(fileAppender.File);
                        if (string.IsNullOrWhiteSpace(fileName))
                        {
                            fileName = "freesnip.log";
                        }

                        fileAppender.File = Path.Combine(logDirectory, fileName);
                        fileAppender.LockingModel = new FileAppender.MinimalLock();
                        fileAppender.ActivateOptions();
                        configuredLogFile = fileAppender.File;
                    }
                }

                return configuredLogFile;
            }
            catch
            {
                return null;
            }
        }

        public static void LogCrash(string source, Exception ex, object rawObject = null)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                string detail = (ex != null ? ex.ToString() : rawObject?.ToString()) ?? "Unknown Exception Details";
                string formatted = $"==================== CRASH REPORT [{source}] ===================={Environment.NewLine}" +
                                   $"Timestamp: {timestamp}{Environment.NewLine}" +
                                   $"Source: {source}{Environment.NewLine}" +
                                   $"Message: {ex?.Message ?? rawObject?.ToString()}{Environment.NewLine}" +
                                   $"Details:{Environment.NewLine}{detail}{Environment.NewLine}" +
                                   $"================================================================={Environment.NewLine}{Environment.NewLine}";

                try
                {
                    string commonDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FreeSnip");
                    Directory.CreateDirectory(commonDir);
                    File.AppendAllText(Path.Combine(commonDir, "crash.log"), formatted);
                    File.AppendAllText(Path.Combine(commonDir, "freesnip.log"), formatted);
                }
                catch { }

                try
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), "FreeSnip");
                    Directory.CreateDirectory(tempDir);
                    File.AppendAllText(Path.Combine(tempDir, "crash.log"), formatted);
                }
                catch { }

                try
                {
                    GetLogger("CrashHandler").Fatal(formatted, ex);
                }
                catch { }
            }
            catch { }
        }

        public static void Shutdown()
        {
            try
            {
                LogManager.Shutdown();
            }
            catch
            {
            }
            finally
            {
                _isLog4NetConfigured = false;
                _fallbackLogFile = null;
            }
        }
    }
}
