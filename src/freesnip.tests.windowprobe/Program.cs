using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using freesnip.editor.forms;
using freesnip.native.foundation;
using SixLabors.ImageSharp.PixelFormats;
using SharpImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace freesnip.tests.windowprobe;

/// <summary>
/// Usage: freesnip.tests.windowprobe.exe &lt;output.json&gt; &lt;W&gt;x&lt;H&gt; [&lt;W&gt;x&lt;H&gt; ...]
/// Opens one real editor window per snip size, samples the layout several times after it opens and writes
/// every sample to the JSON file. Exit code 0 = measured, 2 = bad arguments, 3 = no window could be shown.
/// </summary>
internal static class Program
{
    internal static string OutputPath;
    internal static List<(int W, int H)> Sizes = new();

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length < 2) return 2;
        OutputPath = args[0];
        foreach (string spec in args.Skip(1))
        {
            string[] parts = spec.Split('x');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int w) || !int.TryParse(parts[1], out int h)) return 2;
            Sizes.Add((w, h));
        }

        try
        {
            return AppBuilder.Configure<ProbeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            File.WriteAllText(OutputPath + ".error.txt", ex.ToString());
            return 3;
        }
    }
}

internal sealed class ProbeApp : Application
{
    // Sample points after Show(): the first one is what the user sees when the editor appears.
    private static readonly int[] SampleDelaysMs = { 300, 1000, 2500 };

    public override void Initialize()
    {
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://freesnip.editor/"))
            { Source = new Uri("avares://freesnip.editor/Themes/DesignSystem.axaml") });
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://freesnip.tests.windowprobe/"))
            { Source = new Uri("avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml") });
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        int exitCode = 0;
        var results = new List<object>();
        try
        {
            foreach (var (w, h) in Program.Sizes)
            {
                var editor = new ImageEditorWindow();
                await editor.SetImageAsync(new SharpImage(w, h, new Rgba32(30, 50, 80)), RECT.FromXYWH(100, 100, w, h));
                editor.Show();
                int elapsed = 0;
                foreach (int delay in SampleDelaysMs)
                {
                    await Task.Delay(delay - elapsed);
                    elapsed = delay;
                    results.Add(Measure(editor, w, h, delay));
                }
                editor.Close();
            }
            File.WriteAllText(Program.OutputPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Program.OutputPath + ".error.txt", ex.ToString());
            exitCode = 3;
        }
        desktop.Shutdown(exitCode);
    }

    private static object Rect(Control c, Visual relativeTo)
    {
        var p = c.TranslatePoint(new Point(0, 0), relativeTo) ?? new Point(double.NaN, double.NaN);
        return new { name = string.IsNullOrEmpty(c.Name) ? c.GetType().Name : c.Name, x = p.X, y = p.Y, w = c.Bounds.Width, h = c.Bounds.Height };
    }

    private static object Measure(ImageEditorWindow editor, int imageW, int imageH, int atMs)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var scroller = (ScrollViewer)typeof(ImageEditorWindow).GetField("_editorScrollViewer", flags).GetValue(editor);
        var image = (Control)typeof(ImageEditorWindow).GetField("_mainGrid", flags).GetValue(editor);
        var root = editor.FindControl<Grid>("EditorContentRoot");

        // Buttons that must always be on screen: everything not hosted inside a ScrollViewer (scrolled toolbars
        // may legitimately hide items) and not inside a popup or a hidden context toolbar.
        var fixedButtons = editor.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0 && b.Bounds.Height > 0
                        && !b.GetVisualAncestors().OfType<ScrollViewer>().Any())
            .Select(b => Rect(b, editor))
            .ToList();

        return new
        {
            imageW,
            imageH,
            atMs,
            scaling = editor.RenderScaling,
            client = new { w = editor.ClientSize.Width, h = editor.ClientSize.Height },
            root = new { w = root.Bounds.Width, h = root.Bounds.Height },
            rows = root.Children.OfType<Control>().Select(r => Rect(r, editor)).ToList(),
            leftToolbar = Rect(editor.FindControl<Control>("LeftToolbarBorder"), editor),
            viewport = new { w = scroller.Viewport.Width, h = scroller.Viewport.Height },
            extent = new { w = scroller.Extent.Width, h = scroller.Extent.Height },
            imageInViewport = Rect(image, scroller),
            fixedButtons
        };
    }
}
