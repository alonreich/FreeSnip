using freesnip.native;
using freesnip.native.foundation;
using System;
using System.Runtime.InteropServices;
using freesnip.foundation.core.AvaloniaShims;
using freesnip.foundation.core;

namespace freesnip.foundation.Interop
{
    public static class User32Api
    {
        public static uint GetGuiResources(HANDLE hProcess, uint uiFlags) => PInvoke.GetGuiResources(hProcess, uiFlags);
        public static uint GetGuiResourcesGdiCount() => GetGuiResources(PInvoke.GetCurrentProcess(), 0);
        public static uint GetGuiResourcesUserCount() => GetGuiResources(PInvoke.GetCurrentProcess(), 1);
    }

    public static class Kernel32Api
    {
        public static bool AttachConsole(uint dwProcessId) => PInvoke.AttachConsole(dwProcessId);
        public static bool AttachConsole() => AttachConsole(unchecked((uint)-1));
        public static bool AllocConsole() => PInvoke.AllocConsole();
    }
}
