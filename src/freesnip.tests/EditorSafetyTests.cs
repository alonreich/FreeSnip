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

            // Press C (Crop)
            Call(editor, "OnWindowKeyDown", editor, new Avalonia.Input.KeyEventArgs { Key = Avalonia.Input.Key.C, RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent });
            Assert.Equal(EditorTool.Crop, Field<EditorTool>(editor, "_currentTool"));
            var cropPopup = Field<Avalonia.Controls.Primitives.Popup>(editor, "_cropModePopup");
            Assert.True(cropPopup.IsOpen);

            // Press L (Line) while crop popup is open - routes via OnPopupKeyDown
            Call(editor, "OnPopupKeyDown", cropPopup, new Avalonia.Input.KeyEventArgs { Key = Avalonia.Input.Key.L, RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent });
            Assert.False(cropPopup.IsOpen);
            Assert.Equal(EditorTool.Line, Field<EditorTool>(editor, "_currentTool"));

            // Press A (Arrow)
            Call(editor, "OnWindowKeyDown", editor, new Avalonia.Input.KeyEventArgs { Key = Avalonia.Input.Key.A, RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent });
            Assert.Equal(EditorTool.Arrow, Field<EditorTool>(editor, "_currentTool"));
        }
        finally { editor.Close(); }
    }

    private sealed class BrokenRender : Control
    {
        public override void Render(DrawingContext context) => throw new InvalidOperationException("Injected renderer failure");
    }
}


