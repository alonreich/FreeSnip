using System;
using System.IO;
using freesnip.helpers;
using Xunit;

namespace freesnip.tests
{
    public class RetentionTests
    {
        [Fact]
        public void StorageMandate_FilesMustLandInTemp()
        {
            string expectedPath = Path.Combine(Path.GetTempPath(), "FreeSnip");

            Directory.CreateDirectory(expectedPath);
            Assert.True(Directory.Exists(expectedPath), "Storage directory %TEMP%\\FreeSnip must be accessible.");
        }

        [Fact]
        public void RetentionCycle_MustPurgeOldFiles()
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "FreeSnip");
            Directory.CreateDirectory(tempPath);

            string testFile = Path.Combine(tempPath, "test_purge.jpg");
            File.WriteAllText(testFile, "test data");

            File.SetCreationTime(testFile, DateTime.Now.AddHours(-25));
            File.SetLastWriteTime(testFile, DateTime.Now.AddHours(-25));

            RetentionHelper.RunCleanup();

            Assert.False(File.Exists(testFile), "Files older than 24 hours must be purged from the temporary directory.");
        }
    }
}
