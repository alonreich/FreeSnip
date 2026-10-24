using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace freesnip.tests;

/// <summary>
/// Runs freesnip.tests.windowprobe, which opens the real ImageEditorWindow on the Win32 platform, and checks
/// every sampled layout. The probe runs once per test run, so editor windows appear on screen for a few seconds.
/// </summary>
public sealed class RealWindowProbeFixture
{
    // (snip width, snip height): minimum window, the reported 1044x320 and 1265x486 cases,
    // taller than the screen, and larger than the screen in both directions.
    public static readonly (int W, int H)[] Sizes = { (400, 300), (1044, 320), (1265, 486), (600, 1400), (2600, 1500) };

    public string? SkipReason { get; }
    public string? Failure { get; }
    public IReadOnlyList<JsonElement> Samples { get; } = Array.Empty<JsonElement>();

    public RealWindowProbeFixture()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
        {
            SkipReason = "Real-window layout tests need an interactive Windows desktop.";
            return;
        }

        string? exe = LocateProbe();
        if (exe == null)
        {
            Failure = "freesnip.tests.windowprobe.exe was not found; build the solution first.";
            return;
        }

        string output = Path.Combine(Path.GetTempPath(), "freesnip_windowprobe_" + Guid.NewGuid().ToString("N") + ".json");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        start.ArgumentList.Add(output);
        foreach (var (w, h) in Sizes) start.ArgumentList.Add($"{w}x{h}");

        using var process = Process.Start(start)!;
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            Failure = "The window probe did not finish within 120 s.";
            return;
        }

        if (process.ExitCode == 3)
        {
            string detail = File.Exists(output + ".error.txt") ? File.ReadAllText(output + ".error.txt") : "no detail";
            SkipReason = "No editor window could be shown on this machine: " + detail.Split('\n')[0];
            return;
        }

        if (process.ExitCode != 0 || !File.Exists(output))
        {
            Failure = $"The window probe exited with code {process.ExitCode} and wrote no results.";
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(output));
        Samples = doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
        try { File.Delete(output); } catch { }
    }

    private static string? LocateProbe()
    {
        // Test output: src/freesnip.tests/bin/<Configuration>/<tfm>/<rid>/ ; probe: src/freesnip.tests.windowprobe/bin/<Configuration>/
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? configuration = null;
        for (var d = dir; d != null; d = d.Parent)
        {
            if (d.Parent != null && string.Equals(d.Parent.Name, "bin", StringComparison.OrdinalIgnoreCase)) configuration = d.Name;
            string candidate = Path.Combine(d.FullName, "freesnip.tests.windowprobe", "bin", configuration ?? "Debug", "freesnip.tests.windowprobe.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

public class EditorRealWindowLayoutTests : IClassFixture<RealWindowProbeFixture>
{
    private const double Tolerance = 1.0;
    private readonly RealWindowProbeFixture _probe;
    private readonly ITestOutputHelper _output;

    public EditorRealWindowLayoutTests(RealWindowProbeFixture probe, ITestOutputHelper output)
    {
        _probe = probe;
        _output = output;
    }

    public static IEnumerable<object[]> SnipSizes() => RealWindowProbeFixture.Sizes.Select(s => new object[] { s.W, s.H });

    [SkippableTheory]
    [MemberData(nameof(SnipSizes))]
    public void EditorOpens_NoSideCutOff_AndImageCentered(int imageW, int imageH)
    {
        Skip.If(_probe.SkipReason != null, _probe.SkipReason);
        Assert.True(_probe.Failure == null, _probe.Failure);

        var samples = _probe.Samples.Where(s => s.GetProperty("imageW").GetInt32() == imageW && s.GetProperty("imageH").GetInt32() == imageH).ToList();
        Assert.NotEmpty(samples);

        var problems = new List<string>();
        foreach (var s in samples)
        {
            string at = $"t+{s.GetProperty("atMs").GetInt32()}ms";
            double clientW = Num(s, "client", "w"), clientH = Num(s, "client", "h");
            _output.WriteLine($"{imageW}x{imageH} {at}: client={clientW:0.#}x{clientH:0.#} viewport={Num(s, "viewport", "w"):0.#}x{Num(s, "viewport", "h"):0.#} image@({Num(s, "imageInViewport", "x"):0.#},{Num(s, "imageInViewport", "y"):0.#})");

            // The layout root must match the window, or everything past the window edge is clipped.
            if (Math.Abs(Num(s, "root", "w") - clientW) > Tolerance || Math.Abs(Num(s, "root", "h") - clientH) > Tolerance)
            {
                problems.Add($"{at}: layout root {Num(s, "root", "w"):0.#}x{Num(s, "root", "h"):0.#} != window {clientW:0.#}x{clientH:0.#}");
            }

            foreach (var row in s.GetProperty("rows").EnumerateArray())
            {
                CheckInside(problems, at, "row " + row.GetProperty("name").GetString(), row, clientW, clientH);
            }

            CheckInside(problems, at, "left toolbar", s.GetProperty("leftToolbar"), clientW, clientH);

            foreach (var button in s.GetProperty("fixedButtons").EnumerateArray())
            {
                CheckInside(problems, at, "button " + button.GetProperty("name").GetString(), button, clientW, clientH);
            }

            CheckImageAxis(problems, at, "horizontal", Num(s, "imageInViewport", "x"), Num(s, "imageInViewport", "w"), Num(s, "viewport", "w"), Num(s, "extent", "w"));
            CheckImageAxis(problems, at, "vertical", Num(s, "imageInViewport", "y"), Num(s, "imageInViewport", "h"), Num(s, "viewport", "h"), Num(s, "extent", "h"));
        }

        Assert.True(problems.Count == 0, $"{imageW}x{imageH} snip: " + string.Join("; ", problems));
    }

    private static void CheckInside(List<string> problems, string at, string what, JsonElement r, double clientW, double clientH)
    {
        double x = r.GetProperty("x").GetDouble(), y = r.GetProperty("y").GetDouble();
        double right = x + r.GetProperty("w").GetDouble(), bottom = y + r.GetProperty("h").GetDouble();
        if (x < -Tolerance || y < -Tolerance || right > clientW + Tolerance || bottom > clientH + Tolerance)
        {
            problems.Add($"{at}: {what} spans ({x:0.#},{y:0.#})-({right:0.#},{bottom:0.#}) outside the {clientW:0.#}x{clientH:0.#} window");
        }
    }

    private static void CheckImageAxis(List<string> problems, string at, string axis, double start, double size, double viewport, double extent)
    {
        if (size <= viewport + Tolerance)
        {
            double before = start, after = viewport - (start + size);
            if (before < -Tolerance || after < -Tolerance)
            {
                problems.Add($"{at}: image cut off {axis}ly (padding {before:0.#} / {after:0.#})");
            }
            else if (Math.Abs(before - after) > Tolerance)
            {
                problems.Add($"{at}: image not centered {axis}ly (padding {before:0.#} / {after:0.#})");
            }
        }
        else if (extent + Tolerance < size)
        {
            problems.Add($"{at}: image larger than the viewport {axis}ly but only {extent:0.#} of {size:0.#} is scrollable");
        }
    }

    private static double Num(JsonElement s, string obj, string prop) => s.GetProperty(obj).GetProperty(prop).GetDouble();
}
