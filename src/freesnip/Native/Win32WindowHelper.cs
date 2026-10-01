using System;
using System.Runtime.InteropServices;
using System.Text;
using freesnip.native.foundation;

namespace freesnip.native
{
    public static class Win32WindowHelper
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        
        public static string GetActiveWindowTitle()
        {
            return GetWindowTitle(GetForegroundWindow());
        }

        public static string GetWindowTitle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            var sb = new StringBuilder(256);
            if (GetWindowText(hwnd, sb, sb.Capacity) > 0)
            {
                string title = sb.ToString().Trim();
                if (!string.IsNullOrEmpty(title)) return title;
            }
            IntPtr root = GetAncestor(hwnd, GA_ROOT);
            if (root != IntPtr.Zero && root != hwnd)
            {
                if (GetWindowText(root, sb, sb.Capacity) > 0)
                {
                    string rootTitle = sb.ToString().Trim();
                    if (!string.IsNullOrEmpty(rootTitle)) return rootTitle;
                }
            }
            return null;
        }


        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT Point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        public const uint GA_ROOT = 2;

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        public static POINT GetCursorPosition()
        {
            if (GetCursorPos(out POINT point)) return point;
            return new POINT(0, 0);
        }

        [DllImport("user32.dll")]
        public static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AllowSetForegroundWindow(int dwProcessId);

        public const int ASFW_ANY = -1;

        [DllImport("user32.dll")]
        public static extern IntPtr GetShellWindow();

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        private const int DWMWA_CLOAKED = 13;

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        
        private static bool IsCloakedWindow(IntPtr hWnd)
        {
            try
            {
                if (DwmGetWindowAttributeInt(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0) return false;
                return cloaked != 0;
            }
            catch
            {
                return false;
            }
        }

        public static bool GetWindowRectActual(IntPtr hWnd, out RECT rect)
        {
            if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<RECT>()) == 0)
            {
                return true;
            }
            return GetWindowRect(hWnd, out rect);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

        public static RECT GetMonitorBounds(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return RECT.Empty;
            IntPtr monitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return RECT.Empty;
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfoW(monitor, ref info)) return RECT.Empty;
            return info.rcMonitor;
        }

        
        public static RECT ClampRectToMonitor(IntPtr hWnd, RECT rect)
        {
            RECT monitor = GetMonitorBounds(hWnd);
            if (monitor.IsEmpty) return rect;
            int left = Math.Max(rect.Left, monitor.Left);
            int top = Math.Max(rect.Top, monitor.Top);
            int right = Math.Min(rect.Right, monitor.Right);
            int bottom = Math.Min(rect.Bottom, monitor.Bottom);
            if (right <= left || bottom <= top) return rect;
            return RECT.FromXYWH(left, top, right - left, bottom - top);
        }

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
        private static extern unsafe int GetClassNameSpan(IntPtr hWnd, char* lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        private const int MaxClassNameLength = 256;

        private static unsafe int ReadClassName(IntPtr hWnd, Span<char> destination)
        {
            fixed (char* pointer = destination)
            {
                int length = GetClassNameSpan(hWnd, pointer, destination.Length);
                return length < 0 ? 0 : length;
            }
        }

        private static bool IsShellSurfaceClass(ReadOnlySpan<char> className)
        {
            return className.SequenceEqual("Progman")
                || className.SequenceEqual("WorkerW")
                || className.SequenceEqual("Shell_TrayWnd")
                || className.SequenceEqual("Shell_SecondaryTrayWnd");
        }

        private static bool IsNonSnappableClass(ReadOnlySpan<char> className)
        {
            return className.SequenceEqual("DummyDWMTargetWindow")
                || className.SequenceEqual("ThumbnailDeviceHelperWnd")
                || className.SequenceEqual("Windows.UI.Composition.DesktopWindowContentBridge")
                || className.SequenceEqual("EdgeUiInputTopWnd");
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, IntPtr TokenInformation, uint TokenInformationLength, out uint ReturnLength);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenElevation = 20;

        public static bool IsProcessElevated(uint pid)
        {
            IntPtr processHandle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (processHandle == IntPtr.Zero) return true; 

            try
            {
                if (OpenProcessToken(processHandle, TOKEN_QUERY, out IntPtr tokenHandle))
                {
                    try
                    {
                        IntPtr elevationPtr = Marshal.AllocHGlobal(sizeof(int));
                        try
                        {
                            if (GetTokenInformation(tokenHandle, TokenElevation, elevationPtr, sizeof(int), out _))
                            {
                                return Marshal.ReadInt32(elevationPtr) != 0;
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(elevationPtr);
                        }
                    }
                    finally
                    {
                        CloseHandle(tokenHandle);
                    }
                }
            }
            finally
            {
                CloseHandle(processHandle);
            }
            return false;
        }

        public static bool IsWindowElevated(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == (uint)Environment.ProcessId) return false;
            return IsProcessElevated(pid);
        }

        public static RECT GetRootWindowRect(POINT point) => GetSnappableWindow(point, out _);

        
        public static RECT GetSnappableWindow(POINT point, out IntPtr rootWindowHandle)
        {
            rootWindowHandle = IntPtr.Zero;
            IntPtr childHWnd = ResolveWindowAtPoint(point);
            if (childHWnd == IntPtr.Zero) return RECT.Empty;

            IntPtr rootHWnd = GetAncestor(childHWnd, GA_ROOT);
            if (rootHWnd == IntPtr.Zero) rootHWnd = childHWnd;

            Span<char> classBuffer = stackalloc char[MaxClassNameLength];
            ReadOnlySpan<char> rootClassName = classBuffer.Slice(0, ReadClassName(rootHWnd, classBuffer));
            if (IsShellSurfaceClass(rootClassName) || IsNonSnappableClass(rootClassName) || IsCloakedWindow(rootHWnd))
            {
                return RECT.Empty;
            }

            if (rootHWnd == GetDesktopWindow() || rootHWnd == GetShellWindow()) return RECT.Empty;

            RECT rootRect = RECT.Empty;
            bool hasRootRect = GetWindowRectActual(rootHWnd, out rootRect);
            if (hasRootRect)
            {
                rootRect = ClampRectToMonitor(rootHWnd, rootRect);
                int virtualW = GetSystemMetrics(78);
                int virtualH = GetSystemMetrics(79);
                if (rootRect.Width >= virtualW && rootRect.Height >= virtualH) return RECT.Empty;
                if (IsDesktopSurfaceWindow(rootHWnd, rootRect)) return RECT.Empty;
            }

            if (childHWnd != rootHWnd && childHWnd != IntPtr.Zero && IsWindowVisible(childHWnd))
            {
                ReadOnlySpan<char> childClassName = classBuffer.Slice(0, ReadClassName(childHWnd, classBuffer));
                if (!IsShellSurfaceClass(childClassName) && !IsNonSnappableClass(childClassName) && !IsCloakedWindow(childHWnd))
                {
                    if (GetWindowRect(childHWnd, out RECT childRect))
                    {
                        childRect = ClampRectToMonitor(childHWnd, childRect);
                        if (childRect.Width >= 16 && childRect.Height >= 12 &&
                            point.X >= childRect.Left && point.X < childRect.Right &&
                            point.Y >= childRect.Top && point.Y < childRect.Bottom)
                        {
                            if (!hasRootRect || ((long)childRect.Width * childRect.Height < (long)rootRect.Width * rootRect.Height * 98 / 100))
                            {
                                rootWindowHandle = childHWnd;
                                return childRect;
                            }
                        }
                    }
                }
            }

            if (hasRootRect)
            {
                rootWindowHandle = rootHWnd;
                return rootRect;
            }

            return RECT.Empty;
        }

        private static bool IsDesktopSurfaceWindow(IntPtr hWnd, RECT rect)
        {
            RECT monitor = GetMonitorBounds(hWnd);
            if (monitor.IsEmpty) return false;
            if ((long)rect.Width * rect.Height < ((long)monitor.Width * monitor.Height * 95) / 100) return false;

            
            uint currentPid = (uint)Environment.ProcessId;
            Span<char> belowClass = stackalloc char[MaxClassNameLength];
            IntPtr below = GetWindow(hWnd, 2);
            while (below != IntPtr.Zero)
            {
                if (IsWindowVisible(below))
                {
                    GetWindowThreadProcessId(below, out uint pid);
                    if (pid != currentPid && GetWindowRect(below, out RECT r) && !r.IsEmpty
                        && r.Left < monitor.Right && r.Right > monitor.Left
                        && r.Top < monitor.Bottom && r.Bottom > monitor.Top)
                    {
                        if (!IsShellSurfaceClass(belowClass.Slice(0, ReadClassName(below, belowClass))))
                        {
                            return false;
                        }
                    }
                }
                below = GetWindow(below, 2);
            }
            return true;
        }

        public static IntPtr GetRootWindowHandle(POINT point)
        {
            IntPtr hWnd = ResolveWindowAtPoint(point);
            if (hWnd == IntPtr.Zero) return IntPtr.Zero;

            IntPtr rootHWnd = GetAncestor(hWnd, GA_ROOT);
            return rootHWnd == IntPtr.Zero ? hWnd : rootHWnd;
        }

        private static IntPtr ResolveWindowAtPoint(POINT point)
        {
            IntPtr hWnd = WindowFromPoint(point);
            uint currentProcessId = (uint)Environment.ProcessId;
            while (hWnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(hWnd, out uint processId);
                if (processId != currentProcessId)
                {
                    if (IsWindowVisible(hWnd) && GetWindowRect(hWnd, out RECT r) && point.X >= r.Left && point.X < r.Right && point.Y >= r.Top && point.Y < r.Bottom) return hWnd;
                }
                hWnd = GetWindow(hWnd, 2);
            }
            return IntPtr.Zero;
        }

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
    }
}
