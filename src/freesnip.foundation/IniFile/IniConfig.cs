using freesnip.native;
using freesnip.native.foundation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using log4net;

namespace freesnip.foundation.IniFile
{
    public class IniConfig
    {
        private static readonly ILog Log = freesnip.foundation.core.LogHelper.GetLogger(typeof(IniConfig));
        private const string IniExtension = ".ini";
        private const string DefaultsPostfix = "-defaults";
        private const string FixedPostfix = "-fixed";
        private static readonly object IniLock = new object();
        private static readonly object SectionMapLock = new object();
        private static readonly object WriteLock = new object();
        private static readonly object CommitLock = new object();
        private static readonly Channel<string> SaveChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        private static volatile int _isSaveQueued;
        private static long _requestedVersion;
        private static long _committedVersion;
        internal static int SimulatedDiskLatencyMs { get; set; }
        private static string _applicationName;
        private static string _configName;
        private static readonly IDictionary<string, IniSection> SectionMap = new Dictionary<string, IniSection>();
        private static IDictionary<string, IDictionary<string, string>> _sections = new Dictionary<string, IDictionary<string, string>>();
        private static IDictionary<string, IDictionary<string, string>> _fixedProperties;
        private static bool _portableCheckMade;
        public static bool IsPortable { get; private set; }
        private static string _iniDirectory;
        private static bool _iniDirectoryExplicitlySet;

        public static string IniDirectory
        {
            get => _iniDirectoryExplicitlySet ? _iniDirectory : null;
            set { _iniDirectory = value; _iniDirectoryExplicitlySet = !string.IsNullOrWhiteSpace(value); }
        }

        public static void Init(string appName, string configName)
        {
            _applicationName = appName;
            _configName = configName;
            Reload();
        }

        public static bool IsInitialized => _applicationName != null && _configName != null && SectionMap.Count > 0;

        public static void Init()
        {
            var attr = Assembly.GetEntryAssembly().GetCustomAttributes(typeof(AssemblyProductAttribute), false) as AssemblyProductAttribute[];
            if (attr is { Length: > 0 }) { Log.InfoFormat("Using ProductName {0}", attr[0].Product); Init(attr[0].Product, attr[0].Product); }
            else throw new InvalidOperationException("Assembly ProductName not set.");
        }

        private static string CreateIniLocation(string configFilename, bool isReadOnly)
        {
            if (_applicationName == null || _configName == null) throw new InvalidOperationException("IniConfig.Init not called!");
            
            if (_iniDirectoryExplicitlySet && !string.IsNullOrWhiteSpace(_iniDirectory))
            {
                return Path.Combine(_iniDirectory, configFilename);
            }

            string appPath = GetApplicationStartupPath();
            string iniFilePath = null;
            if (appPath != null)
            {
                string pafPath = Path.Combine(appPath, @"App\" + _applicationName);
                if (!IsPortable && !_portableCheckMade) { Log.Info("Checking for portable mode."); _portableCheckMade = true; if (Directory.Exists(pafPath)) { IsPortable = true; Log.Info("Portable mode active!"); } }
                if (IsPortable)
                {
                    string pafConfigPath = Path.Combine(appPath, @"Data\Settings");
                    try { if (!Directory.Exists(pafConfigPath)) Directory.CreateDirectory(pafConfigPath); iniFilePath = Path.Combine(pafConfigPath, configFilename); }
                    catch (Exception e) { Log.InfoFormat("Portable mode failed: {0}", e.Message); }
                }
            }
            if (iniFilePath == null)
            {
                iniFilePath = Path.Combine(appPath ?? ".", configFilename);
                if (!File.Exists(iniFilePath))
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), _applicationName);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    iniFilePath = Path.Combine(dir, configFilename);
                }
            }
            Log.InfoFormat("Using ini file {0}", iniFilePath);
            return iniFilePath;
        }

        public static void Reload()
        {
            Interlocked.Increment(ref _sectionEpoch);
            lock (IniLock)
            {
                lock (SectionMapLock)
                {
                    _sections = new Dictionary<string, IDictionary<string, string>>();
                }
                Read(CreateIniLocation(_configName + DefaultsPostfix + IniExtension, true));
                Read(CreateIniLocation(_configName + IniExtension, false));
                _fixedProperties = Read(CreateIniLocation(_configName + FixedPostfix + IniExtension, true));
                lock (SectionMapLock)
                {
                    foreach (var section in SectionMap.Values)
                    {
                        if (section == null) continue;
                        try 
                        { 
                            section.Fill(PropertiesForSection(section)); 
                            FixProperties(section); 
                        }
                        catch (Exception ex) 
                        { 
                            Log.WarnFormat("Problem reading ini section {0}: {1}", section?.IniSectionAttribute?.Name ?? "unknown", ex.Message); 
                        }
                    }
                }
            }
        }

        private static void FixProperties(IniSection section)
        {
            if (section?.IniSectionAttribute == null || _fixedProperties == null) return;
            if (!_fixedProperties.TryGetValue(section.IniSectionAttribute.Name, out var fixedProps)) return;
            if (fixedProps == null) return;
            foreach (string key in fixedProps.Keys) 
            {
                if (key != null && section.Values.TryGetValue(key, out var val) && val != null) val.IsFixed = true;
            }
        }

        private static bool IsOptionalIniFile(string iniLocation)
        {
            return iniLocation.EndsWith(DefaultsPostfix + IniExtension, StringComparison.OrdinalIgnoreCase)
                || iniLocation.EndsWith(FixedPostfix + IniExtension, StringComparison.OrdinalIgnoreCase);
        }

        private static IDictionary<string, IDictionary<string, string>> Read(string iniLocation)
        {
            if (!File.Exists(iniLocation))
            {
                if (IsOptionalIniFile(iniLocation))
                {
                    Log.Debug("Optional ini file not present: " + iniLocation);
                }
                else
                {
                    Log.Info("Can't find file: " + iniLocation);
                }

                return null;
            }
            Log.InfoFormat("Loading ini-file: {0}", iniLocation);
            var newSections = IniReader.Read(iniLocation, Encoding.UTF8);
            lock (SectionMapLock)
            {
                foreach (string section in newSections.Keys)
                {
                    if (!_sections.ContainsKey(section)) _sections.Add(section, newSections[section]);
                    else
                    {
                        var curr = _sections[section];
                        foreach (var kv in newSections[section]) if (curr.ContainsKey(kv.Key)) curr[kv.Key] = kv.Value; else curr.Add(kv.Key, kv.Value);
                    }
                }
            }
            return newSections;
        }

        public static T GetIniSection<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)] T>() where T : IniSection => GetIniSection<T>(true);

        private static int _sectionEpoch;

        private static class SectionCache<T> where T : IniSection
        {
            public static T Instance;
            public static int Epoch = -1;
        }

        public static T GetIniSection<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)] T>(bool allowSave) where T : IniSection
        {
            int epoch = Volatile.Read(ref _sectionEpoch);
            T cached = SectionCache<T>.Instance;
            if (cached != null && SectionCache<T>.Epoch == epoch)
            {
                if (allowSave && cached.IsDirty) Save();
                return cached;
            }

            Type type = typeof(T);
            string name = IniSection.GetIniSectionAttribute(type).Name;
            T section;
            lock (SectionMapLock)
            {
                if (SectionMap.TryGetValue(name, out var s)) section = (T)s;
                else { section = Activator.CreateInstance<T>(); SectionMap.Add(name, section); section.Fill(PropertiesForSection(section)); FixProperties(section); }
            }

            SectionCache<T>.Instance = section;
            SectionCache<T>.Epoch = epoch;

            if (allowSave && section.IsDirty) { Log.DebugFormat("Section {0} dirty, saving!", name); Save(); }
            return section;
        }

        static IniConfig()
        {
            Task.Run(ProcessSaveQueueAsync);
        }

        private static async Task ProcessSaveQueueAsync()
        {
            try
            {
                var reader = SaveChannel.Reader;
                while (await reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (reader.TryRead(out string path))
                    {
                        try
                        {
                            ProcessPendingSaves(path);
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Error processing pending ini saves", ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("ProcessSaveQueueAsync terminated unexpectedly", ex);
            }
        }

        private static void ProcessPendingSaves(string defaultPath)
        {
            while (true)
            {
                long targetVersion = Interlocked.Read(ref _requestedVersion);
                if (Interlocked.Read(ref _committedVersion) >= targetVersion)
                {
                    Interlocked.Exchange(ref _isSaveQueued, 0);
                    if (Interlocked.Read(ref _committedVersion) >= Interlocked.Read(ref _requestedVersion))
                    {
                        break;
                    }
                    Interlocked.Exchange(ref _isSaveQueued, 1);
                    continue;
                }

                string path = defaultPath;
                if (string.IsNullOrEmpty(path))
                {
                    if (_applicationName == null || _configName == null)
                    {
                        lock (CommitLock)
                        {
                            if (targetVersion > _committedVersion) _committedVersion = targetVersion;
                            Monitor.PulseAll(CommitLock);
                        }
                        break;
                    }
                    try
                    {
                        path = CreateIniLocation(_configName + IniExtension, false);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Failed to determine ini save path", ex);
                        lock (CommitLock)
                        {
                            if (targetVersion > _committedVersion) _committedVersion = targetVersion;
                            Monitor.PulseAll(CommitLock);
                        }
                        break;
                    }
                }

                try
                {
                    SaveInternally(path);
                }
                catch (Exception ex)
                {
                    Log.Error("Failed to save ini", ex);
                }

                lock (CommitLock)
                {
                    if (targetVersion > _committedVersion)
                    {
                        _committedVersion = targetVersion;
                    }
                    Monitor.PulseAll(CommitLock);
                }
            }
        }

        public static IDictionary<string, string> PropertiesForSection(IniSection section)
        {
            string name = section.IniSectionAttribute.Name;
            lock (SectionMapLock)
            {
                if (!_sections.TryGetValue(name, out var props))
                {
                    props = new Dictionary<string, string>();
                    _sections.Add(name, props);
                }
                return props;
            }
        }

        public static void Save()
        {
            if (_applicationName == null || _configName == null) return;

            Interlocked.Increment(ref _requestedVersion);
            if (Interlocked.Exchange(ref _isSaveQueued, 1) == 0)
            {
                string location;
                try
                {
                    location = CreateIniLocation(_configName + IniExtension, false);
                }
                catch (Exception ex)
                {
                    Log.Error("Failed to resolve ini location in Save", ex);
                    Interlocked.Exchange(ref _isSaveQueued, 0);
                    return;
                }

                if (!SaveChannel.Writer.TryWrite(location))
                {
                    Interlocked.Exchange(ref _isSaveQueued, 0);
                }
            }
        }

        public static bool WaitForPendingSaves(int timeoutMs = 5000)
        {
            long targetVersion = Interlocked.Read(ref _requestedVersion);
            if (Interlocked.Read(ref _committedVersion) >= targetVersion)
            {
                return true;
            }

            if (Interlocked.Exchange(ref _isSaveQueued, 1) == 0)
            {
                if (_applicationName != null && _configName != null)
                {
                    try
                    {
                        string location = CreateIniLocation(_configName + IniExtension, false);
                        SaveChannel.Writer.TryWrite(location);
                    }
                    catch
                    {
                        Interlocked.Exchange(ref _isSaveQueued, 0);
                    }
                }
            }

            var sw = Stopwatch.StartNew();
            lock (CommitLock)
            {
                while (Interlocked.Read(ref _committedVersion) < targetVersion)
                {
                    long remaining = timeoutMs - sw.ElapsedMilliseconds;
                    if (remaining <= 0)
                    {
                        Log.WarnFormat("WaitForPendingSaves timed out after {0}ms waiting for version {1}. Committed: {2}",
                            timeoutMs, targetVersion, Interlocked.Read(ref _committedVersion));
                        return false;
                    }
                    Monitor.Wait(CommitLock, (int)Math.Min(remaining, int.MaxValue));
                }
            }
            return true;
        }

        public static void Flush()
        {
            bool dirty = false;
            lock (SectionMapLock)
            {
                foreach (var s in SectionMap.Values)
                {
                    if (s != null && s.IsDirty)
                    {
                        dirty = true;
                        break;
                    }
                }
            }
            if (dirty)
            {
                Save();
            }
            WaitForPendingSaves(5000);
        }

        public static void SaveTo(string path)
        {
            WaitForPendingSaves(5000);
            SaveInternally(path);
        }

        private static void SaveInternally(string iniLocation)
        {
            if (string.IsNullOrEmpty(iniLocation)) return;
            Log.Info("Saving configuration to: " + iniLocation);
            string dir = Path.GetDirectoryName(iniLocation);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            IniSection[] sectionSnapshots;
            List<KeyValuePair<string, Dictionary<string, string>>> unclaimedSectionsSnapshot;

            lock (SectionMapLock)
            {
                sectionSnapshots = SectionMap.Values.ToArray();
                foreach (var s in sectionSnapshots)
                {
                    if (s != null) s.IsDirty = false;
                }

                unclaimedSectionsSnapshot = new List<KeyValuePair<string, Dictionary<string, string>>>();
                foreach (var kvp in _sections)
                {
                    if (!SectionMap.ContainsKey(kvp.Key))
                    {
                        unclaimedSectionsSnapshot.Add(new KeyValuePair<string, Dictionary<string, string>>(
                            kvp.Key,
                            new Dictionary<string, string>(kvp.Value)));
                    }
                }
            }

            lock (WriteLock)
            {
                string tempFile = iniLocation + ".tmp";
                try
                {
                    using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(fs, Encoding.UTF8))
                    {
                        foreach (var section in sectionSnapshots)
                        {
                            if (section == null) continue;
                            section.Write(writer, false);
                            writer.WriteLine();
                        }
                        writer.WriteLine();
                        foreach (var unclaimed in unclaimedSectionsSnapshot)
                        {
                            writer.WriteLine("; Section {0} unclaimed", unclaimed.Key);
                            writer.WriteLine("[{0}]", unclaimed.Key);
                            foreach (var kv in unclaimed.Value)
                            {
                                writer.WriteLine("{0}={1}", kv.Key, kv.Value);
                            }
                            writer.WriteLine();
                        }
                        writer.Flush();
                        fs.Flush(true);
                    }

                    if (SimulatedDiskLatencyMs > 0)
                    {
                        Thread.Sleep(SimulatedDiskLatencyMs);
                    }

                    if (File.Exists(iniLocation)) File.Replace(tempFile, iniLocation, iniLocation + ".bak");
                    else File.Move(tempFile, iniLocation);
                }
                finally
                {
                    try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                }
            }
        }

        private static string GetApplicationStartupPath() => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
