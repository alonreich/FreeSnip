using System;
using System.IO;
using System.Text.RegularExpressions;
using freesnip.foundation.core;
using Xunit;

namespace freesnip.tests;

public class ChangelogDisciplineTests
{
    private static string FindChangelogPath()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, "docs", "CHANGELOG.md");
            if (File.Exists(candidate)) return candidate;
            string? parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir) break;
            dir = parent;
        }
        return Path.Combine(RuntimePathHelper.StartupPath, "docs", "CHANGELOG.md");
    }

    [Fact]
    public void Changelog_ExistsAndIsNonEmpty()
    {
        string changelogPath = FindChangelogPath();
        Assert.True(File.Exists(changelogPath), $"CHANGELOG.md was not found at expected path: {changelogPath}");
        string content = File.ReadAllText(changelogPath);
        Assert.False(string.IsNullOrWhiteSpace(content), "CHANGELOG.md must not be empty.");
    }

    [Fact]
    public void Changelog_EnforcesRollingCapOfFiveEntries()
    {
        string changelogPath = FindChangelogPath();
        Assert.True(File.Exists(changelogPath));
        string content = File.ReadAllText(changelogPath);

        var matches = Regex.Matches(content, @"^###\s+\[(.*?)\]\s*-\s*([0-9]{4}-[0-9]{2}-[0-9]{2})", RegexOptions.Multiline);
        Assert.True(matches.Count > 0, "CHANGELOG.md must have at least one release entry.");
        Assert.True(matches.Count <= 5, $"CHANGELOG.md exceeds the rolling buffer limit of 5 entries (found {matches.Count}).");
    }

    [Fact]
    public void Changelog_EveryEntryHasBulletPointsAndCleanFormat()
    {
        string changelogPath = FindChangelogPath();
        Assert.True(File.Exists(changelogPath));
        string content = File.ReadAllText(changelogPath);

        string[] sections = content.Split(new[] { "\n---", "\r\n---" }, StringSplitOptions.RemoveEmptyEntries);
        int validSectionCount = 0;

        foreach (string section in sections)
        {
            var headerMatch = Regex.Match(section, @"###\s+\[(.*?)\]\s*-\s*([0-9]{4}-[0-9]{2}-[0-9]{2})");
            if (headerMatch.Success)
            {
                validSectionCount++;
                var bullets = Regex.Matches(section, @"^\s*-\s+\*\*(.*?)\*\*:", RegexOptions.Multiline);
                Assert.True(bullets.Count > 0, $"Release entry {headerMatch.Groups[1].Value} must contain at least one bullet point.");
            }
        }

        Assert.True(validSectionCount > 0 && validSectionCount <= 5,
            $"Expected between 1 and 5 valid release sections, found {validSectionCount}.");
    }
}
