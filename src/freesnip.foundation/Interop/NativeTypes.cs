using System;
using System.Runtime.InteropServices;
using freesnip.foundation.core.AvaloniaShims;

namespace freesnip.native.foundation
{
    [StructLayout(LayoutKind.Sequential)]
    public struct HWND { public IntPtr Value; public static implicit operator IntPtr(HWND hwnd) => hwnd.Value; public static implicit operator HWND(IntPtr value) => new HWND { Value = value }; public static HWND Zero => default; }
    [StructLayout(LayoutKind.Sequential)]
    public struct HANDLE { public IntPtr Value; public static implicit operator IntPtr(HANDLE handle) => handle.Value; public static implicit operator HANDLE(IntPtr value) => new HANDLE { Value = value }; public static HANDLE Zero => default; }
    [StructLayout(LayoutKind.Sequential)]
    public struct HDC { public IntPtr Value; public static implicit operator IntPtr(HDC hdc) => hdc.Value; public static implicit operator HDC(IntPtr value) => new HDC { Value = value }; public static HDC Zero => default; }
    [StructLayout(LayoutKind.Sequential)]
    public struct HICON { public IntPtr Value; public static implicit operator IntPtr(HICON hicon) => hicon.Value; public static implicit operator HICON(IntPtr value) => new HICON { Value = value }; public static HICON Zero => default; }
    [StructLayout(LayoutKind.Sequential)]
    public struct HRGN { public IntPtr Value; public static implicit operator IntPtr(HRGN hrgn) => hrgn.Value; public static implicit operator HRGN(IntPtr value) => new HRGN { Value = value }; public static HRGN Zero => default; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left, top, right, bottom;
        public int X { get => left; set { int w = Width; left = value; right = value + w; } }
        public int Y { get => top; set { int h = Height; top = value; bottom = value + h; } }
        public int Width { get => right - left; set => right = left + value; }
        public int Height { get => bottom - top; set => bottom = top + value; }
        public bool IsEmpty => left == 0 && top == 0 && right == 0 && bottom == 0;
        public Point Location => new Point(left, top);
        public Size Size => new Size(right - left, bottom - top);
        public int Left => left; public int Top => top; public int Right => right; public int Bottom => bottom;
        public RECT(int left, int top, int right, int bottom) { this.left = left; this.top = top; this.right = right; this.bottom = bottom; }
        public RECT(POINT p, SIZE s) : this(p.x, p.y, p.x + s.cx, p.y + s.cy) { }

        public static RECT FromXYWH(int x, int y, int width, int height)
        {
            if (width <= 0 || height <= 0) return Empty;
            return new RECT(x, y, x + width, y + height);
        }

        public static RECT Empty => default;
        public Rectangle ToRectangle() => new Rectangle(left, top, right - left, bottom - top);
        public static implicit operator Rectangle(RECT r) => r.ToRectangle();
        public static implicit operator RECT(Rectangle r) => new RECT(r.Left, r.Top, r.Right, r.Bottom);
        
        public RECT Offset(int dx, int dy) => new RECT(left + dx, top + dy, right + dx, bottom + dy);
        public bool Contains(POINT p) => p.X >= left && p.X < right && p.Y >= top && p.Y < bottom;
        public bool Contains(int x, int y) => x >= left && x < right && y >= top && y < bottom;
        public RECT Inflate(int dw, int dh) => new RECT(left - dw, top - dh, right + dw, bottom + dh);
        public RECT Inflate(int d) => Inflate(d, d);
        public RECT Intersect(RECT b) => (RECT)Rectangle.Intersect(this.ToRectangle(), b.ToRectangle());
        public RECT Union(RECT b) => (RECT)Rectangle.Union(this.ToRectangle(), b.ToRectangle());
        public static RECT Union(RECT a, RECT b) => (RECT)Rectangle.Union(a.ToRectangle(), b.ToRectangle());
        public static RECT Intersect(RECT a, RECT b) => (RECT)Rectangle.Intersect(a.ToRectangle(), b.ToRectangle());
        public RECT Normalize() { return new RECT(Math.Min(left, right), Math.Min(top, bottom), Math.Max(left, right), Math.Max(top, bottom)); }
        public bool IntersectsWith(RECT rect) => ToRectangle().IntersectsWith(rect.ToRectangle());
        public override bool Equals(object obj) => obj is RECT r && r.left == left && r.top == top && r.right == right && r.bottom == bottom;
        public override int GetHashCode() => HashCode.Combine(left, top, right, bottom);
        public static bool operator ==(RECT a, RECT b) => a.Equals(b);
        public static bool operator !=(RECT a, RECT b) => !a.Equals(b);
        public RECT Offset(POINT p) => Offset(p.x, p.y);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x, y;
        public int X { get => x; set => x = value; }
        public int Y { get => y; set => y = value; }
        public POINT(int x, int y) { this.x = x; this.y = y; }
        public static POINT Empty => default;
        public static implicit operator Point(POINT p) => new Point(p.x, p.y);
        public static implicit operator POINT(Point p) => new POINT { x = p.X, y = p.Y };
        public POINT Offset(int dx, int dy) => new POINT(x + dx, y + dy);
        public POINT Offset(POINT p) => new POINT(x + p.x, y + p.y);
        public override bool Equals(object obj) => obj is POINT p && p.x == x && p.y == y;
        public override int GetHashCode() => HashCode.Combine(x, y);
        public static bool operator ==(POINT a, POINT b) => a.Equals(b);
        public static bool operator !=(POINT a, POINT b) => !a.Equals(b);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx, cy;
        public int Width { get => cx; set => cx = value; }
        public int Height { get => cy; set => cy = value; }
        public SIZE(int w, int h) { cx = w; cy = h; }
        public static SIZE Empty => default;
        public static implicit operator Size(SIZE s) => new Size(s.cx, s.cy);
        public static implicit operator SIZE(Size s) => new SIZE { cx = s.Width, cy = s.Height };
        public static implicit operator SIZE(int v) => new SIZE(v, v);
        public static implicit operator int(SIZE s) => s.cx;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WPARAM { public nuint Value; public static implicit operator nuint(WPARAM w) => w.Value; public static implicit operator WPARAM(nuint v) => new WPARAM { Value = v }; public static implicit operator WPARAM(int v) => new WPARAM { Value = (nuint)v }; }
    [StructLayout(LayoutKind.Sequential)]
    public struct LPARAM { public nint Value; public static implicit operator nint(LPARAM l) => l.Value; public static implicit operator LPARAM(nint v) => new LPARAM { Value = v }; public static implicit operator LPARAM(int v) => new LPARAM { Value = (nint)v }; }
    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfo { public BitmapInfoHeader bmiHeader; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)] public uint[] bmiColors; }
    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader {
        public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes; public ushort biBitCount; public uint biCompression; public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter; public uint biClrUsed; public uint biClrImportant;
    }
}

namespace freesnip.native
{
    public static class PInvoke
    {
        [DllImport("user32.dll")] public static extern foundation.HWND GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(foundation.HWND hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(foundation.HWND hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(foundation.HWND hWnd);
        [DllImport("user32.dll")] public static extern foundation.HWND GetDesktopWindow();
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out foundation.POINT lpPoint);
        [DllImport("user32.dll")] public static extern bool PrintWindow(foundation.HWND hWnd, foundation.HDC hdcBlt, uint nFlags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(foundation.HWND hWnd, global::System.Text.StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(foundation.HWND hWnd, global::System.Text.StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] public static extern uint GetGuiResources(foundation.HANDLE hProcess, uint uiFlags);
        [DllImport("kernel32.dll")] public static extern bool AttachConsole(uint dwProcessId);
        [DllImport("kernel32.dll")] public static extern bool AllocConsole();
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(foundation.HANDLE hObject);
        [DllImport("kernel32.dll")] public static extern foundation.HANDLE GetCurrentProcess();
        [DllImport("kernel32.dll")] public static extern void SetLastError(uint dwErrCode);
        [DllImport("gdi32.dll")] public static extern foundation.HDC CreateCompatibleDC(foundation.HDC hdc);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(foundation.HDC hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, foundation.HDC hdcSrc, int nXSrc, int nYSrc, uint dwRop);
        [DllImport("user32.dll")] public static extern foundation.HWND WindowFromPoint(foundation.POINT Point);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(foundation.HWND hWnd, out foundation.RECT lpRect);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out foundation.RECT pvAttribute, int cbAttribute);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
        [DllImport("gdi32.dll")] public static extern foundation.HDC CreateDIBSection(foundation.HDC hdc, ref foundation.BitmapInfo pbmi, uint iUsage, out IntPtr ppvBits, foundation.HANDLE hSection, uint dwOffset);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] public static extern IntPtr GetWindowLongPtr(foundation.HWND hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] public static extern IntPtr SetWindowLongPtr(foundation.HWND hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtr")] public static extern IntPtr GetClassLongPtr(foundation.HWND hWnd, int nIndex);
        [DllImport("user32.dll")] public static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll")] public static extern bool CloseClipboard();
        [DllImport("user32.dll")] public static extern bool EmptyClipboard();
        [DllImport("user32.dll")] public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
        [DllImport("user32.dll")] public static extern IntPtr GetClipboardData(uint uFormat);
        [DllImport("user32.dll")] public static extern foundation.HDC GetDC(foundation.HWND hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(foundation.HWND hWnd, foundation.HDC hDC);
    }
}
