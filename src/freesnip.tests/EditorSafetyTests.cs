using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using freesnip.editor.forms;
using freesnip.foundation.core;
using freesnip.foundation.IniFile;
using freesnip.native.foundation;
using SixLabors.ImageSharp.PixelFormats;
using Avalonia.Interactivity;
using SharpImage = SixLabors.ImageSharp.Image;
using SharpImage32 = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

[assembly: AvaloniaTestApplication(typeof(freesnip.tests.SafetyTestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace freesnip.tests;

public class SafetyTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<SafetyTestApp>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class SafetyTestApp : Application
{
    public override void Initialize()
    {
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://freesnip.editor/"))
            { Source = new Uri("avares://freesnip.editor/Themes/DesignSystem.axaml") });
        Styles.Add(new FluentTheme());
    }
}

public class EditorSafetyTests
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Call(ImageEditorWindow editor, string method, params object[] args)
        => typeof(ImageEditorWindow).GetMethod(method, Private)!.Invoke(editor, args);
    private static T Field<T>(ImageEditorWindow editor, string name)
        => (T)typeof(ImageEditorWindow).GetField(name, Private)!.GetValue(editor)!;
    private static Task<SharpImage> Export(ImageEditorWindow editor)
        => (Task<SharpImage>)Call(editor, "GetFlattenedImageAsync")!;

    private static async Task<ImageEditorWindow> CreateEditor()
    {
        var config = IniConfig.GetIniSection<CoreConfiguration>(allowSave: false);
        config.AddFrameBorders = false;
        config.WarnBeforeClosingEditor = false;
        var editor = new ImageEditorWindow();
        await editor.SetImageAsync(new SharpImage32(64, 64, new Rgba32(255, 255, 255)), RECT.FromXYWH(0, 0, 64, 64));
        return editor;
    }

    [AvaloniaFact]
    public async Task HighlightOverBlackout_DoesNotRestoreUnderlyingWhitePixels()
    {
        var editor = await CreateEditor();
        try
        {
            Call(editor, "AddAnnotation", new Avalonia.Controls.Shapes.Rectangle { Width = 32, Height = 32, Fill = Brushes.Black });
            var highlight = Call(editor, "CreateHighlightAnnotation", new Point(0, 0), new Point(32, 32))!;
            Call(editor, "AddAnnotation", highlight);
            using var exported = await Export(editor);
            using var pixels = exported.CloneAs<Rgba32>();
            Assert.True(pixels[16, 16].R < 120 && pixels[16, 16].G < 120 && pixels[16, 16].B < 120,
                "Highlight must tint the blackout, not recover the original white pixels.");
            Assert.True(pixels[48, 48].R > 240);
        }
        finally { editor.Close(); }
    }

    [AvaloniaFact]
    public async Task RenderFailure_ThrowsInsteadOfExportingUneditedOriginal()
    {
        var editor = await CreateEditor();
        try
        {
            Field<Canvas>(editor, "_canvas").Children.Add(new BrokenRender { Width = 32, Height = 32 });
            await Assert.ThrowsAsync<InvalidOperationException>(() => Export(editor));
            Assert.False(Field<bool>(editor, "_forceClose"));
        }
        finally { editor.Close(); }
    }

    [AvaloniaFact]
    public async Task Export_WaitsForPixelation_AndForAReplacementRequest()
    {
        var editor = await CreateEditor();
        try
        {
            var pixelation = (Control)Call(editor, "CreatePixelateAnnotation", new Point(0, 0), new Point(32, 32))!;
            Call(editor, "AddAnnotation", pixelation);
            await (Task)pixelation.Resources["PixelateTask"]!;
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var replacement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pixelation.Resources["PixelateTask"] = first.Task;
            var exporting = Export(editor);
            Assert.False(exporting.IsCompleted);
            pixelation.Resources["PixelateTask"] = replacement.Task;
            first.SetResult();
            await Task.Yield();
            Assert.False(exporting.IsCompleted);
            replacement.SetResult();
            using var result = await exporting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(result);
        }
        finally { editor.Close(); }
    }

    [AvaloniaFact]
    public async Task FailedPixelation_PreventsExport()
    {
        var editor = await CreateEditor();
        try
        {
            var pixelation = (Control)Call(editor, "CreatePixelateAnnotation", new Point(0, 0), new Point(32, 32))!;
            Call(editor, "AddAnnotation", pixelation);
            await (Task)pixelation.Resources["PixelateTask"]!;
            pixelation.Resources["PixelateTask"] = Task.FromException(new IOException("Injected render failure"));
            await Assert.ThrowsAsync<IOException>(() => Export(editor));
            Assert.False(Field<bool>(editor, "_forceClose"));
        }
        finally { editor.Close(); }
    }

    [AvaloniaFact]
    public async Task SliderAndColorPreviewLayout_MetricsAreAccurate()
    {
        var editor = await CreateEditor();
        try
        {
            var preview = editor.FindControl<Border>("CurrentColorPreview");
            Assert.NotNull(preview);
            Assert.Equal(24.0, preview.Width);
            Assert.Equal(24.0, preview.Height);

            var slider = editor.FindControl<Slider>("ThicknessFlyoutSlider");
            Assert.NotNull(slider);
            Assert.False(slider.ClipToBounds);
            Assert.True(slider.Resources.ContainsKey("SliderPreContentMargin"));
            Assert.Equal(new GridLength(0.0), slider.Resources["SliderPreContentMargin"]);
            Assert.True(slider.Resources.ContainsKey("SliderPostContentMargin"));
            Assert.Equal(new GridLength(0.0), slider.Resources["SliderPostContentMargin"]);
        }
        finally { editor.Close(); }
    }

    [Fact]
    public void CoreConfiguration_CloseEditorOnAction_DefaultsToTrue()
    {
        var config = new CoreConfiguration();
        Assert.True(config.CloseEditorOnAction);
    }

    [AvaloniaFact]
    public async Task ImageEditorWindow_ContextualCopyAndPaste_DuplicatesAnnotationWith20DipOffset()
    {
        var editor = await CreateEditor();
        try
        {
            var rect = new Avalonia.Controls.Shapes.Rectangle { Width = 30, Height = 30, Fill = Brushes.Blue };
            Canvas.SetLeft(rect, 10);
            Canvas.SetTop(rect, 10);
            Call(editor, "AddAnnotation", rect);

            typeof(ImageEditorWindow).GetField("_selectedControl", Private)!.SetValue(editor, rect);

            Call(editor, "HandleContextualCopy", false);

            var clipboardModel = typeof(ImageEditorWindow).GetField("_annotationClipboard", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
            Assert.NotNull(clipboardModel);

            Call(editor, "HandleContextualPaste");

            var selected = (Control?)typeof(ImageEditorWindow).GetField("_selectedControl", Private)!.GetValue(editor);
            Assert.NotNull(selected);
            Assert.NotSame(rect, selected);
            Assert.Equal(30.0, Canvas.GetLeft(selected));
            Assert.Equal(30.0, Canvas.GetTop(selected));
        }
        finally { editor.Close(); }
    }

    [AvaloniaFact]
    public async Task ImageEditorWindow_SaveAndCopy_BypassesCloseWhenCloseEditorOnActionIsFalse()
    {
        var editor = await CreateEditor();
        try
        {
            editor.Show();
            var config = IniConfig.GetIniSection<CoreConfiguration>(allowSave: false);
            config.CloseEditorOnAction = false;

            Call(editor, "OnCopyClick", new object(), new RoutedEventArgs());
            Assert.False(Field<bool>(editor, "_forceClose"));
            Assert.True(editor.IsVisible);
        }
        finally { editor.Close(); }
    }

    [AvaloniaFact]
    public async Task ImageEditorWindow_ShortcutKeys_SwitchToolsWhenShapeIsSelected()
    {
        var editor = await CreateEditor();
        try
        {
            editor.Show();
            var arrow = new Canvas
            {
                Width = 40,
                Height = 40,
                Tag = new ImageEditorWindow.ArrowProperties { Start = new Point(10, 10), End = new Point(50, 50) }
            };
            Call(editor, "AddAnnotation", arrow);
            Assert.Same(arrow, Field<Control?>(editor, "_selectedControl"));
            var focused = editor.FocusManager?.GetFocusedElement();
            Assert.Same(Field<Control>(editor, "_canvas"), focused);

            Call(editor, "OnWindowKeyDown", editor, new Avalonia.Input.KeyEventArgs { Key = Avalonia.Input.Key.C, RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent });
            Assert.Equal(EditorTool.Crop, Field<EditorTool>(editor, "_currentTool"));
            var cropPopup = Field<Avalonia.Controls.Primitives.Popup>(editor, "_cropModePopup");
            Assert.True(cropPopup.IsOpen);

            Call(editor, "OnPopupKeyDown", cropPopup, new Avalonia.Input.KeyEventArgs { Key = Avalonia.Input.Key.L, RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent });
            Assert.False(cropPopup.IsOpen);
            Assert.Equal(EditorTool.Line, Field<EditorTool>(editor, "_currentTool"));

            Call(editor, "OnWindowKeyDown", editor, new Avalonia.Input.KeyEventArgs { Key = Avalonia.Input.Key.A, RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent });
            Assert.Equal(EditorTool.Arrow, Field<EditorTool>(editor, "_currentTool"));
        }
        finally { editor.Close(); }
    }

    [Fact]
    public void CoreConfiguration_WarnBeforeClosingEditor_DefaultsToFalse()
    {
        var config = new CoreConfiguration();
        Assert.False(config.WarnBeforeClosingEditor);
    }

    [Fact]
    public void CoreConfiguration_LastPixelateStrength_DefaultsToTen()
    {
        var config = new CoreConfiguration();
        Assert.Equal(10, config.LastPixelateStrength);
    }

    [Fact]
    public void ImageEditorWindow_PixelateStrength_ThirtyPercentMath()
    {
        const int min = 2;
        const int max = 29;
        const int val = 10;
        double pct = (double)(val - min) / (max - min);
        int displayPercent = (int)Math.Round(pct * 100);
        Assert.Equal(30, displayPercent);
    }

    [AvaloniaFact]
    public async Task ImageEditorWindow_HandleContextualCopy_WithNoSelection_DoesNotCopy()
    {
        var editor = await CreateEditor();
        try
        {
            typeof(ImageEditorWindow).GetField("_annotationClipboard", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
            typeof(ImageEditorWindow).GetField("_selectedControl", Private)!.SetValue(editor, null);

            Call(editor, "HandleContextualCopy", false);

            var clipboardModel = typeof(ImageEditorWindow).GetField("_annotationClipboard", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
            Assert.Null(clipboardModel);
        }
        finally { editor.Close(); }
    }

    [AvaloniaFact]
    public async Task MultiSelectGroup_VectorCoordinateRetention_UngroupsWithoutPointDrift()
    {
        var editor = await CreateEditor();
        try
        {
            var line = new Avalonia.Controls.Shapes.Line
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(50, 50),
                StrokeThickness = 2
            };
            Canvas.SetLeft(line, 100);
            Canvas.SetTop(line, 100);
            Call(editor, "AddAnnotation", line);

            Assert.True(ImageEditorWindow.TryGetVectorAbsolutePoints(line, out var initialStart, out var initialEnd));
            Assert.Equal(new Point(100, 100), initialStart);
            Assert.Equal(new Point(150, 150), initialEnd);

            // Create a multi-select group containing the line
            var group = new Canvas { Tag = "MultiSelectGroup", Width = 100, Height = 100 };
            Canvas.SetLeft(group, 50);
            Canvas.SetTop(group, 50);

            var canvas = Field<Canvas>(editor, "_canvas");
            canvas.Children.Remove(line);

            // Coordinate adjustment into group
            Call(editor, "SetVectorAbsolutePoints", line, new Point(initialStart.X - 50, initialStart.Y - 50), new Point(initialEnd.X - 50, initialEnd.Y - 50));
            group.Children.Add(line);
            canvas.Children.Add(group);

            // Now ungroup
            Call(editor, "UngroupMultiSelectGroup", group);

            Assert.True(ImageEditorWindow.TryGetVectorAbsolutePoints(line, out var finalStart, out var finalEnd));
            Assert.Equal(initialStart.X, finalStart.X, 0.001);
            Assert.Equal(initialStart.Y, finalStart.Y, 0.001);
            Assert.Equal(initialEnd.X, finalEnd.X, 0.001);
            Assert.Equal(initialEnd.Y, finalEnd.Y, 0.001);
        }
        finally { editor.Close(); }
    }

    [AvaloniaFact]
    public async Task SelectionIndicators_ClearWithoutNullPoints_DoesNotCrashAvaloniaMeasureOrRender()
    {
        var editor = await CreateEditor();
        try
        {
            // Initial state: no selection
            Call(editor, "UpdateSelectionIndicator");

            var lineInd = Field<Avalonia.Controls.Shapes.Polygon>(editor, "_lineSelectionIndicator");
            var arrowInd = Field<Avalonia.Controls.Shapes.Polygon>(editor, "_arrowSelectionIndicator");
            var lineHov = Field<Avalonia.Controls.Shapes.Polygon>(editor, "_lineHoverIndicator");
            var arrowHov = Field<Avalonia.Controls.Shapes.Polygon>(editor, "_arrowHoverIndicator");

            Assert.NotNull(lineInd.Points);
            Assert.NotNull(arrowInd.Points);
            Assert.NotNull(lineHov.Points);
            Assert.NotNull(arrowHov.Points);

            // Measuring or arranging should not throw NullReferenceException
            lineInd.Measure(new Size(100, 100));
            lineInd.Arrange(new Rect(0, 0, 100, 100));
            arrowInd.Measure(new Size(100, 100));
            arrowInd.Arrange(new Rect(0, 0, 100, 100));

            // Select a line then clear selection
            var line = new Avalonia.Controls.Shapes.Line
            {
                StartPoint = new Point(10, 10),
                EndPoint = new Point(40, 40),
                StrokeThickness = 2
            };
            Canvas.SetLeft(line, 0);
            Canvas.SetTop(line, 0);
            Call(editor, "AddAnnotation", line);

            typeof(ImageEditorWindow).GetField("_selectedControl", Private)!.SetValue(editor, line);
            Call(editor, "UpdateSelectionIndicator");

            Assert.NotNull(lineInd.Points);
            Assert.NotEmpty(lineInd.Points);

            // Clear selection
            typeof(ImageEditorWindow).GetField("_selectedControl", Private)!.SetValue(editor, null);
            Call(editor, "UpdateSelectionIndicator");

            Assert.NotNull(lineInd.Points);
            Assert.Empty(lineInd.Points);
            Assert.NotNull(arrowInd.Points);
            Assert.Empty(arrowInd.Points);

            // Verify Avalonia layout pass does not throw NullReferenceException
            lineInd.Measure(new Size(100, 100));
            lineInd.Arrange(new Rect(0, 0, 100, 100));
        }
        finally { editor.Close(); }
    }

    [Fact]
    public async Task LogInstallationElevationState_ConcurrentAccess_DoesNotThrowSharingViolation()
    {
        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            int threadId = i;
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 5; j++)
                {
                    freesnip.helpers.StartupTaskHelper.LogInstallationElevationState($"Thread {threadId} iteration {j}");
                }
            }));
        }

        // Must complete without unhandled sharing violation IOException
        await Task.WhenAll(tasks);
    }

    private sealed class BrokenRender : Control
    {
        public override void Render(DrawingContext context) => throw new InvalidOperationException("Injected renderer failure");
    }
}



