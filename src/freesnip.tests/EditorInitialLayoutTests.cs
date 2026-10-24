using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using freesnip.editor.forms;
using freesnip.foundation.core;
using freesnip.foundation.IniFile;
using freesnip.native.foundation;
using SixLabors.ImageSharp.PixelFormats;
using Xunit.Abstractions;
using SharpImage32 = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace freesnip.tests;

/// <summary>
/// Headless layout checks for the editor's initial open. The Win32-only stale-Grid-width bug fixed in
/// ImageEditorWindow.OnWindowSizeChanged does not reproduce under the headless platform, so these guard
/// centering and toolbar clipping logic only.
/// </summary>
public class EditorInitialLayoutTests
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly ITestOutputHelper _output;

    public EditorInitialLayoutTests(ITestOutputHelper output) => _output = output;

    private static T Field<T>(ImageEditorWindow editor, string name)
        => (T)typeof(ImageEditorWindow).GetField(name, Private)!.GetValue(editor)!;

    private static void Settle(int rounds = 20)
    {
        for (int i = 0; i < rounds; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task<ImageEditorWindow> OpenEditor(int imageWidth, int imageHeight)
    {
        var config = IniConfig.GetIniSection<CoreConfiguration>(allowSave: false);
        config.AddFrameBorders = true;
        config.FrameBorderThickness = 4;
        config.WarnBeforeClosingEditor = false;
        var editor = new ImageEditorWindow();
        await editor.SetImageAsync(new SharpImage32(imageWidth, imageHeight, new Rgba32(40, 60, 90)), RECT.FromXYWH(100, 100, imageWidth, imageHeight));
        editor.Show();
        Settle();
        return editor;
    }

    private (double left, double right, double viewport) MeasureHorizontalPadding(ImageEditorWindow editor)
    {
        var scroller = Field<ScrollViewer>(editor, "_editorScrollViewer");
        var mainGrid = Field<Control>(editor, "_mainGrid");
        var topLeft = mainGrid.TranslatePoint(new Point(0, 0), scroller)!.Value;
        double left = topLeft.X;
        double right = scroller.Viewport.Width - (topLeft.X + mainGrid.Bounds.Width);
        _output.WriteLine($"window={editor.Bounds.Width:0.#}x{editor.Bounds.Height:0.#} viewport={scroller.Viewport.Width:0.#}x{scroller.Viewport.Height:0.#} extent={scroller.Extent.Width:0.#} offset={scroller.Offset.X:0.#} image={mainGrid.Bounds.Width:0.#} padLeft={left:0.#} padRight={right:0.#}");
        return (left, right, scroller.Viewport.Width);
    }

    [AvaloniaTheory]
    [InlineData(1044, 320)]
    [InlineData(400, 300)]
    [InlineData(1400, 500)]
    public async Task InitialOpen_ImageIsHorizontallyCentered(int width, int height)
    {
        var editor = await OpenEditor(width, height);
        try
        {
            var (left, right, viewport) = MeasureHorizontalPadding(editor);
            if (width + 2 * 24 <= viewport)
            {
                Assert.True(Math.Abs(left - right) <= 1.0, $"Image not centered: left padding {left:0.#}, right padding {right:0.#}.");
            }
        }
        finally { editor.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1044, 320)]
    [InlineData(400, 300)]
    public async Task InitialOpen_TopToolbarFitsWindow_ClearAllNotClipped(int width, int height)
    {
        var editor = await OpenEditor(width, height);
        try
        {
            var clearAll = editor.FindControl<Button>("ClearAllButton")!;
            var right = clearAll.TranslatePoint(new Point(clearAll.Bounds.Width, 0), editor)!.Value.X;
            var toolbar = editor.FindControl<StackPanel>("TopToolbarStackPanel")!;
            _output.WriteLine($"window={editor.Bounds.Width:0.#} toolbarDesired={toolbar.DesiredSize.Width:0.#} clearAllRight={right:0.#} clearAllWidth={clearAll.Bounds.Width:0.#}");
            Assert.True(right <= editor.Bounds.Width + 0.5, $"Clear All ends at {right:0.#} but the window is {editor.Bounds.Width:0.#} wide.");
        }
        finally { editor.Close(); }
    }
}
