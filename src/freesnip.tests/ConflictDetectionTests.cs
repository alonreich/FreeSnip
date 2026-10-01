using freesnip.helpers;
using Xunit;

namespace freesnip.tests
{
    public class ConflictDetectionTests
    {
        [Theory]
        [InlineData("Obsidian")]
        [InlineData("obsidian")]
        [InlineData("Obs")]
        [InlineData("SnippingToolPlus")]
        [InlineData("FastStoneViewer")]
        [InlineData("greenshotter")]
        [InlineData("MyJingNotes")]
        public void ExactProcessMatch_RejectsSubstringLookalikes(string processName)
        {
            Assert.Null(DeploymentLifecycle.MatchConflictingProcessName(new[] { processName }));
        }

        [Theory]
        [InlineData("obs64")]
        [InlineData("OBS64")]
        [InlineData("greenshot")]
        [InlineData("sharex")]
        [InlineData("snagit")]
        public void ExactProcessMatch_AcceptsRealToolProcesses(string processName)
        {
            Assert.NotNull(DeploymentLifecycle.MatchConflictingProcessName(new[] { "notepad", processName }));
        }

        [Theory]
        [InlineData("OBS Studio")]
        [InlineData("FastStone Capture")]
        [InlineData("Camtasia Studio")]
        public void ExactDisplayMatch_AcceptsRealDisplayNames(string displayName)
        {
            Assert.NotNull(DeploymentLifecycle.MatchConflictingDisplayName(new[] { displayName }));
        }

        [Fact]
        public void ExactDisplayMatch_RejectsSubstringLookalikes()
        {
            Assert.Null(DeploymentLifecycle.MatchConflictingDisplayName(new[] { "Obsidian", "Snip & Sketch", "FastStone Image Viewer" }));
        }
    }
}

