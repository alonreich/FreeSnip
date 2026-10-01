using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using freesnip.foundation.core;
using System;
using System.Threading.Tasks;

namespace freesnip.editor.helpers
{
    public static partial class ScreenColorSampler
    {
        public static async Task<Color?> PickColorAsync()
        {
            var bounds = NativeCapture.GetVirtualDesktopBounds();
            using var screenShot = NativeCapture.CaptureRegion(bounds);
            if (screenShot == null) return null;

            var tcs = new TaskCompletionSource<Color?>();
            Bitmap magnifierBitmap = null;
            Avalonia.Controls.Image magnifierImage = null;
            var samplerCursor = new Cursor(StandardCursorType.Cross);
            var samplerWindow = new Window
            {
                SystemDecorations = SystemDecorations.None,
                Background = Brushes.Transparent,
                Topmost = true,
                Cursor = samplerCursor,
                WindowStartupLocation = WindowStartupLocation.Manual
            };

            samplerWindow.Position = new PixelPoint(bounds.Left, bounds.Top);

            double scaling = 1.0;
            var screen = samplerWindow.Screens.ScreenFromPoint(new PixelPoint(bounds.Left, bounds.Top));
            if (screen != null) scaling = screen.Scaling;

            samplerWindow.Width = bounds.Width / scaling;
            samplerWindow.Height = bounds.Height / scaling;
            freesnip.foundation.core.UiLayoutDirection.Apply(samplerWindow);

            var canvas = new Canvas { Background = Brushes.Transparent };
            samplerWindow.Content = canvas;

            var instruction = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(220, 30, 30, 30)),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 5),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = "Click a color. Esc cancels.",
                    Foreground = Brushes.White,
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold
                }
            };

            var magnifierBorder = new Border
            {
                Width = 120,
                Height = 120,
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(60),
                BoxShadow = new BoxShadows(new BoxShadow { Blur = 10, Color = Colors.Black }),
                ClipToBounds = true,
                IsVisible = false,
                Background = Brushes.Black
            };

            magnifierBitmap = ImageSharpAvaloniaHelper.ToAvaloniaBitmap(screenShot);
            magnifierImage = new Avalonia.Controls.Image
            {
                Width = screenShot.Width,
                Height = screenShot.Height,
                Source = magnifierBitmap,
                Stretch = Stretch.None,
                RenderTransform = new ScaleTransform(8, 8),
                RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative)
            };

            var magnifierCanvas = new Canvas { Width = 120, Height = 120 };
            magnifierCanvas.Children.Add(magnifierImage);
            magnifierBorder.Child = magnifierCanvas;

            magnifierCanvas.Children.Add(new Avalonia.Controls.Shapes.Line { StartPoint = new Point(60, 0), EndPoint = new Point(60, 120), Stroke = Brushes.Red, StrokeThickness = 1 });
            magnifierCanvas.Children.Add(new Avalonia.Controls.Shapes.Line { StartPoint = new Point(0, 60), EndPoint = new Point(120, 60), Stroke = Brushes.Red, StrokeThickness = 1 });

            var colorLabel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                Padding = new Thickness(5),
                CornerRadius = new CornerRadius(4),
                Child = new TextBlock { Foreground = Brushes.White, FontSize = 10 },
                IsVisible = false
            };

            canvas.Children.Add(magnifierBorder);
            canvas.Children.Add(colorLabel);
            canvas.Children.Add(instruction);
            Canvas.SetLeft(instruction, 20);
            Canvas.SetTop(instruction, 20);

            Color lastLabelColor = default;

            samplerWindow.PointerMoved += (s, e) =>
            {
                Point clientPos;
                int px, py;
                if (GetCursorPos(out POINT cursor))
                {
                    px = cursor.X - bounds.Left;
                    py = cursor.Y - bounds.Top;
                    clientPos = samplerWindow.PointToClient(new PixelPoint(cursor.X, cursor.Y));
                }
                else
                {
                    clientPos = e.GetPosition(samplerWindow);
                    var screenPoint = samplerWindow.PointToScreen(clientPos);
                    px = screenPoint.X - bounds.Left;
                    py = screenPoint.Y - bounds.Top;
                }

                if (px >= 0 && px < screenShot.Width && py >= 0 && py < screenShot.Height)
                {
                    var pixel = screenShot[px, py];
                    var color = Color.FromArgb(pixel.A, pixel.R, pixel.G, pixel.B);

                    magnifierBorder.IsVisible = true;
                    colorLabel.IsVisible = true;

                    double winW = samplerWindow.Bounds.Width > 0 ? samplerWindow.Bounds.Width : (bounds.Width / scaling);
                    double winH = samplerWindow.Bounds.Height > 0 ? samplerWindow.Bounds.Height : (bounds.Height / scaling);

                    double magX = (clientPos.X + 20 + 120 > winW - 10) ? (clientPos.X - 120 - 20) : (clientPos.X + 20);
                    double magY = (clientPos.Y + 20 + 160 > winH - 10) ? (clientPos.Y - 160 - 20) : (clientPos.Y + 20);

                    magX = Math.Clamp(magX, 10, Math.Max(10, winW - 130));
                    magY = Math.Clamp(magY, 10, Math.Max(10, winH - 170));

                    Canvas.SetLeft(magnifierBorder, magX);
                    Canvas.SetTop(magnifierBorder, magY);

                    Canvas.SetLeft(colorLabel, magX);
                    Canvas.SetTop(colorLabel, magY + 125);

                    if (lastLabelColor != color)
                    {
                        lastLabelColor = color;
                        ((TextBlock)colorLabel.Child).Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
                    }

                    Canvas.SetLeft(magnifierImage, (-px * 8) + 60);
                    Canvas.SetTop(magnifierImage, (-py * 8) + 60);
                }
            };

            samplerWindow.PointerPressed += (s, e) =>
            {
                int px, py;
                if (GetCursorPos(out POINT cursor))
                {
                    px = cursor.X - bounds.Left;
                    py = cursor.Y - bounds.Top;
                }
                else
                {
                    var screenPoint = samplerWindow.PointToScreen(e.GetPosition(samplerWindow));
                    px = screenPoint.X - bounds.Left;
                    py = screenPoint.Y - bounds.Top;
                }

                if (px >= 0 && px < screenShot.Width && py >= 0 && py < screenShot.Height)
                {
                    var pixel = screenShot[px, py];
                    tcs.SetResult(Color.FromArgb(pixel.A, pixel.R, pixel.G, pixel.B));
                }
                else
                {
                    tcs.SetResult(null);
                }

                samplerWindow.Close();
            };

            samplerWindow.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    tcs.TrySetResult(null);
                    samplerWindow.Close();
                }
            };
            samplerWindow.Closed += (s, e) => tcs.TrySetResult(null);

            try
            {
                samplerWindow.Show();
                var result = await tcs.Task;
                samplerWindow.Close();
                return result;
            }
            finally
            {
                if (magnifierImage != null)
                {
                    magnifierImage.Source = null;
                    magnifierImage.RenderTransform = null;
                }

                magnifierBitmap?.Dispose();
                samplerWindow.Cursor = null;
                samplerCursor.Dispose();
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static partial bool GetCursorPos(out POINT lpPoint);
    }
}
