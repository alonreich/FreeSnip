#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using freesnip.foundation.interfaces.Ocr;
using freesnip.helpers;
using ImageSharpImage = SixLabors.ImageSharp.Image;
using AvaloniaColor = Avalonia.Media.Color;

namespace freesnip.editor.Services
{
    public sealed class OcrOverlayController
    {
        public static readonly IBrush OcrSelectedFillBrush = new SolidColorBrush(AvaloniaColor.FromArgb(140, 0, 191, 255));
        public static readonly IBrush OcrSelectedStrokeBrush = new SolidColorBrush(AvaloniaColor.FromArgb(200, 0, 191, 255));
        public static readonly IBrush OcrUnselectedFillBrush = new SolidColorBrush(AvaloniaColor.FromArgb(90, 0, 0, 0));
        public static readonly IBrush OcrUnselectedStrokeBrush = new SolidColorBrush(AvaloniaColor.FromArgb(140, 255, 255, 255));

        public bool IsInteractiveMode { get; set; }
        public OcrInformation? InteractiveOcrInfo { get; private set; }
        public List<OcrWord> SelectedOcrWords { get; } = new List<OcrWord>();
        public List<Control> Visuals { get; } = new List<Control>();
        public OcrWordSpatialIndex? SpatialIndex { get; private set; }

        public int SelectionStartIndex { get; set; } = -1;
        public int SelectionEndIndex { get; set; } = -1;

        private int _lastSelectionMin = -1;
        private int _lastSelectionMax = -1;

        public void SetOcrInfo(OcrInformation? info)
        {
            InteractiveOcrInfo = info;
            SpatialIndex = info?.Words != null ? OcrWordSpatialIndex.Create(info.Words) : null;
            SelectedOcrWords.Clear();
            SelectionStartIndex = -1;
            SelectionEndIndex = -1;
            _lastSelectionMin = -1;
            _lastSelectionMax = -1;
        }

        public int FindClosestWordIndex(Avalonia.Point pos, double maxDistanceSq = 10000)
        {
            if (InteractiveOcrInfo?.Words == null || InteractiveOcrInfo.Words.Count == 0) return -1;
            SpatialIndex ??= OcrWordSpatialIndex.Create(InteractiveOcrInfo.Words);
            return SpatialIndex.FindClosestIndex((int)Math.Round(pos.X), (int)Math.Round(pos.Y), maxDistanceSq);
        }

        public void PaintWords(Canvas? canvas)
        {
            ClearVisuals(canvas);
            if (InteractiveOcrInfo?.Words == null || canvas == null) return;

            foreach (var word in InteractiveOcrInfo.Words)
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;

                var rect = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = word.Bounds.Width + 2,
                    Height = word.Bounds.Height + 2,
                    Fill = OcrUnselectedFillBrush,
                    Stroke = OcrUnselectedStrokeBrush,
                    StrokeThickness = 1,
                    Tag = word,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(rect, word.Bounds.X);
                Canvas.SetTop(rect, word.Bounds.Y);
                canvas.Children.Add(rect);
                Visuals.Add(rect);
            }

            _lastSelectionMin = -2;
            _lastSelectionMax = -2;
            UpdateSelectionVisuals(true);
        }

        public void ClearVisuals(Canvas? canvas)
        {
            if (canvas != null)
            {
                foreach (var visual in Visuals)
                {
                    canvas.Children.Remove(visual);
                }
            }
            Visuals.Clear();
            SelectedOcrWords.Clear();
            SpatialIndex = null;
            SelectionStartIndex = -1;
            SelectionEndIndex = -1;
            _lastSelectionMin = -1;
            _lastSelectionMax = -1;
        }

        public void ClearSelection()
        {
            SelectedOcrWords.Clear();
            _lastSelectionMin = -1;
            _lastSelectionMax = -1;
            UpdateSelectionVisuals(true);
        }

        public void CollectSelectedWords()
        {
            SelectedOcrWords.Clear();
            if (SelectionStartIndex == -1 || InteractiveOcrInfo?.Words == null) return;

            int minIdx = Math.Min(SelectionStartIndex, SelectionEndIndex);
            int maxIdx = Math.Max(SelectionStartIndex, SelectionEndIndex);

            if (minIdx != -1 && maxIdx != -1)
            {
                for (int i = minIdx; i <= maxIdx; i++)
                {
                    if (i >= 0 && i < InteractiveOcrInfo.Words.Count)
                    {
                        SelectedOcrWords.Add(InteractiveOcrInfo.Words[i]);
                    }
                }
            }
        }

        public void UpdateSelectionVisuals(bool refreshAll = false)
        {
            if (InteractiveOcrInfo?.Words == null) return;

            int minIdx = Math.Min(SelectionStartIndex, SelectionEndIndex);
            int maxIdx = Math.Max(SelectionStartIndex, SelectionEndIndex);

            if (refreshAll || _lastSelectionMin == -2 || _lastSelectionMax == -2)
            {
                UpdateVisualRange(0, Visuals.Count - 1, minIdx, maxIdx);
            }
            else
            {
                UpdateVisualRange(_lastSelectionMin, _lastSelectionMax, minIdx, maxIdx);
                UpdateVisualRange(minIdx, maxIdx, minIdx, maxIdx);
            }

            _lastSelectionMin = minIdx;
            _lastSelectionMax = maxIdx;
        }

        public void UpdateVisualRange(int start, int end, int minIdx, int maxIdx)
        {
            if (start < 0 || end < 0) return;
            int min = Math.Max(0, Math.Min(start, end));
            int max = Math.Min(Visuals.Count - 1, Math.Max(start, end));
            for (int i = min; i <= max; i++)
            {
                UpdateVisual(i, minIdx, maxIdx);
            }
        }

        public void UpdateVisual(int index, int minIdx, int maxIdx)
        {
            if (index < 0 || index >= Visuals.Count || InteractiveOcrInfo?.Words == null || index >= InteractiveOcrInfo.Words.Count) return;
            if (Visuals[index] is not Avalonia.Controls.Shapes.Rectangle r) return;

            bool isSelected = minIdx != -1 && maxIdx != -1 && index >= minIdx && index <= maxIdx;
            bool isPersisted = SelectedOcrWords.Contains(InteractiveOcrInfo.Words[index]);

            if (isSelected || isPersisted)
            {
                r.Fill = OcrSelectedFillBrush;
                r.Stroke = OcrSelectedStrokeBrush;
                r.StrokeThickness = 1.5;
            }
            else
            {
                r.Fill = OcrUnselectedFillBrush;
                r.Stroke = OcrUnselectedStrokeBrush;
                r.StrokeThickness = 1;
            }
        }

        public static (bool hasLink, bool hasEmail) DetectActions(string text)
        {
            bool hasLink = Regex.IsMatch(text, @"^https?://\S+$", RegexOptions.IgnoreCase) ||
                           Regex.IsMatch(text, @"^www\.\S+\.\S+$", RegexOptions.IgnoreCase);
            bool hasEmail = Regex.IsMatch(text, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
            return (hasLink, hasEmail);
        }

        public static void CalculateToolbarPosition(
            double wordX,
            double wordY,
            double wordW,
            double wordH,
            double tbW,
            double tbH,
            double maxW,
            double maxH,
            out double toolbarX,
            out double toolbarY)
        {
            toolbarX = wordX + wordW + 6;
            toolbarY = wordY;

            if (toolbarX + tbW > maxW)
            {
                toolbarX = Math.Max(6, wordX - tbW - 6);
                if (toolbarX < 6)
                {
                    toolbarX = Math.Max(6, maxW - tbW - 10);
                    toolbarY = wordY + wordH + 6;
                }
            }

            if (toolbarY + tbH > maxH)
            {
                toolbarY = Math.Max(6, maxH - tbH - 10);
            }
        }

        public static double SampleUnderlyingLuminance(ImageSharpImage? image, double x, double y, double width, double height)
        {
            if (image == null) return 0.2;
            try
            {
                int imgW = image.Width;
                int imgH = image.Height;
                int startX = Math.Clamp((int)x, 0, Math.Max(0, imgW - 1));
                int startY = Math.Clamp((int)y, 0, Math.Max(0, imgH - 1));
                int endX = Math.Clamp((int)(x + width), 0, imgW);
                int endY = Math.Clamp((int)(y + height), 0, imgH);
                int w = endX - startX;
                int h = endY - startY;
                if (w <= 0 || h <= 0) return 0.2;

                long totalLum = 0;
                int sampleCount = 0;
                int stepX = Math.Max(1, w / 10);
                int stepY = Math.Max(1, h / 10);

                if (image is Image<Bgra32> bgra)
                {
                    for (int py = startY; py < endY; py += stepY)
                    {
                        for (int px = startX; px < endX; px += stepX)
                        {
                            var pixel = bgra[px, py];
                            double lum = 0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B;
                            totalLum += (long)lum;
                            sampleCount++;
                        }
                    }
                }
                else if (image is Image<Rgba32> rgba)
                {
                    for (int py = startY; py < endY; py += stepY)
                    {
                        for (int px = startX; px < endX; px += stepX)
                        {
                            var pixel = rgba[px, py];
                            double lum = 0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B;
                            totalLum += (long)lum;
                            sampleCount++;
                        }
                    }
                }
                else if (image is Image<Rgb24> rgb)
                {
                    for (int py = startY; py < endY; py += stepY)
                    {
                        for (int px = startX; px < endX; px += stepX)
                        {
                            var pixel = rgb[px, py];
                            double lum = 0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B;
                            totalLum += (long)lum;
                            sampleCount++;
                        }
                    }
                }
                else
                {
                    var cropRect = new SixLabors.ImageSharp.Rectangle(startX, startY, w, h);
                    using var patch = image.Clone(ctx => ctx.Crop(cropRect)).CloneAs<Rgba32>();
                    int pStepX = Math.Max(1, patch.Width / 10);
                    int pStepY = Math.Max(1, patch.Height / 10);
                    for (int py = 0; py < patch.Height; py += pStepY)
                    {
                        for (int px = 0; px < patch.Width; px += pStepX)
                        {
                            var pixel = patch[px, py];
                            double lum = 0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B;
                            totalLum += (long)lum;
                            sampleCount++;
                        }
                    }
                }

                if (sampleCount == 0) return 0.2;
                return (totalLum / (double)sampleCount) / 255.0;
            }
            catch
            {
                return 0.2;
            }
        }

        public static void ApplyContrastTheme(Border ocrToolbar, double lum, IEnumerable<Button?> buttons)
        {
            if (ocrToolbar == null) return;
            bool isDarkBackground = lum < 0.45;

            var bgBrush = isDarkBackground
                ? new SolidColorBrush(AvaloniaColor.Parse("#F8F9FA"))
                : new SolidColorBrush(AvaloniaColor.Parse("#1A1C20"));

            var borderBrush = isDarkBackground
                ? new SolidColorBrush(AvaloniaColor.Parse("#007ACC"))
                : new SolidColorBrush(AvaloniaColor.Parse("#00B4D8"));

            var textBrush = isDarkBackground
                ? new SolidColorBrush(AvaloniaColor.Parse("#111111"))
                : new SolidColorBrush(AvaloniaColor.Parse("#FFFFFF"));

            var buttonBg = isDarkBackground
                ? new SolidColorBrush(AvaloniaColor.Parse("#E9ECEF"))
                : new SolidColorBrush(AvaloniaColor.Parse("#2D3139"));

            ocrToolbar.Background = bgBrush;
            ocrToolbar.BorderBrush = borderBrush;
            ocrToolbar.BorderThickness = new Thickness(2);
            ocrToolbar.BoxShadow = new BoxShadows(BoxShadow.Parse(isDarkBackground ? "0 6 20 0 #CC000000" : "0 6 20 0 #99000000"));

            foreach (var btn in buttons)
            {
                if (btn == null) continue;
                btn.Background = buttonBg;
                btn.BorderBrush = borderBrush;
                if (btn.Content is StackPanel sp)
                {
                    foreach (var child in sp.Children)
                    {
                        if (child is TextBlock tb) tb.Foreground = textBrush;
                    }
                }
            }
        }

        public void ShowContextToolbar(
            Border? ocrToolbar,
            double maxW,
            double maxH,
            Func<double, double, double, double, double>? sampleLuminance = null,
            Action<Border, double>? applyTheme = null,
            Action<string>? updateToolbarButtons = null)
        {
            if (ocrToolbar == null) return;

            if (SelectedOcrWords.Count == 0)
            {
                ocrToolbar.IsVisible = false;
                return;
            }

            var lastWord = SelectedOcrWords
                .OrderByDescending(w => w.Bounds.X + w.Bounds.Width)
                .ThenBy(w => w.Bounds.Y)
                .Last();

            try
            {
                ocrToolbar.Measure(new Avalonia.Size(double.PositiveInfinity, double.PositiveInfinity));
            }
            catch
            {
            }

            double tbW = Math.Max(120, ocrToolbar.DesiredSize.Width);
            double tbH = Math.Max(40, ocrToolbar.DesiredSize.Height);

            CalculateToolbarPosition(
                lastWord.Bounds.X,
                lastWord.Bounds.Y,
                lastWord.Bounds.Width,
                lastWord.Bounds.Height,
                tbW,
                tbH,
                maxW,
                maxH,
                out double toolbarX,
                out double toolbarY);

            Canvas.SetLeft(ocrToolbar, toolbarX);
            Canvas.SetTop(ocrToolbar, toolbarY);

            double lum = sampleLuminance != null ? sampleLuminance(toolbarX, toolbarY, tbW, tbH) : 0.2;
            applyTheme?.Invoke(ocrToolbar, lum);

            string text = OcrTextLayout.BuildVisualSelectionText(SelectedOcrWords).Trim();
            updateToolbarButtons?.Invoke(text);
            ocrToolbar.IsVisible = true;
        }

        public void HideContextToolbar(Border? ocrToolbar)
        {
            if (ocrToolbar != null) ocrToolbar.IsVisible = false;
        }
    }
}
