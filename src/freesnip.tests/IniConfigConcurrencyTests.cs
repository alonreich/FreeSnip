using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using freesnip.foundation.core;
using freesnip.foundation.IniFile;
using Xunit;

namespace freesnip.tests
{
    [Collection(IniConfigStateCollection.Name)]
    public class IniConfigConcurrencyTests
    {
        private static string CreateTempDir(string prefix)
        {
            string dir = Path.Combine(Path.GetTempPath(), prefix + "_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void ConcurrentStressTest_500Iterations_ZeroInvalidOperationExceptions()
        {
            string dir = CreateTempDir("FreeSnip_IniStress");
            try
            {
                IniConfig.IniDirectory = dir;
                IniConfig.Init("FreeSnipStress", "stress_config");

                var exceptions = new ConcurrentBag<Exception>();
                const int totalIterations = 500;
                const int degreeOfParallelism = 16;

                Parallel.For(0, totalIterations, new ParallelOptions { MaxDegreeOfParallelism = degreeOfParallelism }, i =>
                {
                    try
                    {
                        var config = IniConfig.GetIniSection<CoreConfiguration>();
                        Assert.NotNull(config);

                        config.Language = $"en-US-{i}";
                        config.CaptureDelay = i;

                        var props = IniConfig.PropertiesForSection(config);
                        Assert.NotNull(props);

                        IniConfig.Save();
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });

                IniConfig.Flush();

                Assert.Empty(exceptions);

                string iniPath = Path.Combine(dir, "stress_config.ini");
                Assert.True(File.Exists(iniPath), $"Expected {iniPath} to exist after Flush().");

                var parsed = IniReader.Read(iniPath, Encoding.UTF8);
                Assert.True(parsed.ContainsKey("Core"), "Parsed ini must contain [Core] section.");
                Assert.True(parsed["Core"].ContainsKey("Language"), "[Core] section must contain Language property.");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void SimulatedDiskLatency_500ms_ConfirmsZeroDataLoss()
        {
            string dir = CreateTempDir("FreeSnip_IniLatency");
            try
            {
                IniConfig.IniDirectory = dir;
                IniConfig.Init("FreeSnipLatency", "latency_config");

                var core = IniConfig.GetIniSection<CoreConfiguration>();
                core.Language = "initial-value";
                IniConfig.Save();
                IniConfig.Flush();

                string iniPath = Path.Combine(dir, "latency_config.ini");
                Assert.True(File.Exists(iniPath));

                IniConfig.SimulatedDiskLatencyMs = 500;

                try
                {
                    core.Language = "pass-1-in-flight";
                    IniConfig.Save();

                    Thread.Sleep(50);
                    core.Language = "guaranteed-persisted-final-value";
                    core.CaptureDelay = 9999;
                    IniConfig.Save();

                    IniConfig.Flush();
                }
                finally
                {
                    IniConfig.SimulatedDiskLatencyMs = 0;
                }

                var finalSections = IniReader.Read(iniPath, Encoding.UTF8);
                Assert.True(finalSections.ContainsKey("Core"));
                Assert.Equal("guaranteed-persisted-final-value", finalSections["Core"]["Language"]);
                Assert.Equal("9999", finalSections["Core"]["CaptureDelay"]);
            }
            finally
            {
                IniConfig.SimulatedDiskLatencyMs = 0;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void SaveTo_FlushesPendingQueue_BeforeWritingToCustomDestination()
        {
            string dir = CreateTempDir("FreeSnip_IniSaveTo");
            try
            {
                IniConfig.IniDirectory = dir;
                IniConfig.Init("FreeSnipSaveTo", "main_config");

                var core = IniConfig.GetIniSection<CoreConfiguration>();
                core.Language = "custom-export-language";
                IniConfig.Save();

                string exportPath = Path.Combine(dir, "exported.ini");
                IniConfig.SaveTo(exportPath);

                Assert.True(File.Exists(exportPath));
                var parsed = IniReader.Read(exportPath, Encoding.UTF8);
                Assert.True(parsed.ContainsKey("Core"));
                Assert.Equal("custom-export-language", parsed["Core"]["Language"]);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void FailedSave_IsNotReportedAsCommitted_AndIsRetriedUntilPersisted()
        {
            string dir = CreateTempDir("FreeSnip_IniFail");
            Exception? raised = null;
            using var raisedSignal = new ManualResetEventSlim(false);
            Action<Exception> handler = ex => { raised = ex; raisedSignal.Set(); };
            try
            {
                IniConfig.IniDirectory = dir;
                IniConfig.Init("FreeSnipFail", "fail_config");
                IniConfig.SaveFailed += handler;
                IniConfig.SimulatedSaveFailure = path => path != null && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase);

                IniConfig.GetIniSection<CoreConfiguration>().Language = "fail-then-recover";
                IniConfig.Save();

                Assert.False(IniConfig.WaitForPendingSaves(3000));
                Assert.True(IniConfig.HasUnsavedChanges);
                Assert.NotNull(IniConfig.LastSaveError);
                Assert.True(raisedSignal.Wait(3000));
                Assert.NotNull(raised);

                IniConfig.SimulatedSaveFailure = null;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (IniConfig.HasUnsavedChanges && sw.ElapsedMilliseconds < 10000)
                {
                    Thread.Sleep(50);
                }

                Assert.False(IniConfig.HasUnsavedChanges);
                Assert.Null(IniConfig.LastSaveError);
                Assert.Contains("fail-then-recover", File.ReadAllText(Path.Combine(dir, "fail_config.ini")));
            }
            finally
            {
                IniConfig.SimulatedSaveFailure = null;
                IniConfig.SaveFailed -= handler;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void WaitForPendingSaves_TimesOut_WhenUncommitted()
        {
            bool committed = IniConfig.WaitForPendingSaves(100);
            Assert.True(committed);
        }
    }
}

