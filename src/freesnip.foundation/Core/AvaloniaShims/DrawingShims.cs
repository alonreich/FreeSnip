using System;

namespace freesnip.foundation.core.AvaloniaShims
{
    public struct Point
    {
        public int X { get; set; }
        public int Y { get; set; }
        public Point(int x, int y) { X = x; Y = y; }
        public static readonly Point Empty = new Point(0, 0);
        public Point Offset(int dx, int dy) => new Point(X + dx, Y + dy);
    }

    public struct Size
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public Size(int width, int height) { Width = width; Height = height; }
        public static readonly Size Empty = new Size(0, 0);
    }

    public struct Rectangle
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public Rectangle(int x, int y, int width, int height) { X = x; Y = y; Width = width; Height = height; }
        public Rectangle(Point location, Size size) { X = location.X; Y = location.Y; Width = size.Width; Height = size.Height; }
        public Point Location => new Point(X, Y);
        public Size Size => new Size(Width, Height);
        public int Left => X;
        public int Top => Y;
        public int Right => X + Width;
        public int Bottom => Y + Height;
        public bool IsEmpty => Width <= 0 || Height <= 0;

        public static Rectangle Intersect(Rectangle a, Rectangle b)
        {
            int x1 = Math.Max(a.X, b.X);
            int x2 = Math.Min(a.X + a.Width, b.X + b.Width);
            int y1 = Math.Max(a.Y, b.Y);
            int y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

            if (x2 >= x1 && y2 >= y1) return new Rectangle(x1, y1, x2 - x1, y2 - y1);
            return new Rectangle(0, 0, 0, 0);
        }

        public static Rectangle Union(Rectangle a, Rectangle b)
        {
            int x1 = Math.Min(a.X, b.X);
            int x2 = Math.Max(a.X + a.Width, b.X + b.Width);
            int y1 = Math.Min(a.Y, b.Y);
            int y2 = Math.Max(a.Y + a.Height, b.Y + b.Height);
            return new Rectangle(x1, y1, x2 - x1, y2 - y1);
        }
        
        public bool IntersectsWith(Rectangle rect) => !Intersect(this, rect).IsEmpty;
    }
}