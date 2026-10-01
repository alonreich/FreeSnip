using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace freesnip.foundation.Interop;

[SupportedOSPlatform("windows")]
public static class ShellLinkGuids
{
    public const string ClsidShellLinkString = "00021401-0000-0000-C000-000000000046";
    public const string IidIShellLinkWString = "000214F9-0000-0000-C000-000000000046";
    public const string IidIPersistFileString = "0000010B-0000-0000-C000-000000000046";

    public static readonly Guid CLSID_ShellLink = new(ClsidShellLinkString);
    public static readonly Guid IID_IShellLinkW = new(IidIShellLinkWString);
    public static readonly Guid IID_IPersistFile = new(IidIPersistFileString);
}

[SupportedOSPlatform("windows")]
public interface IShellLinkW
{
    void SetPath(string path);
    void SetWorkingDirectory(string workingDirectory);
    void SetArguments(string arguments);
    void SetDescription(string description);
    void SetIconLocation(string iconPath, int iconIndex);
    string GetPath();
    string GetWorkingDirectory();
    string GetArguments();
    string GetDescription();
    (string iconPath, int iconIndex) GetIconLocation();
}

[SupportedOSPlatform("windows")]
public interface IPersistFile
{
    void Save(string filename, bool remember);
    void Load(string filename, uint mode);
}

[SupportedOSPlatform("windows")]
public unsafe class ShellLink : IShellLinkW, IPersistFile, IDisposable
{
    private IntPtr _shellLink;
    private IntPtr _persistFile;
    private bool _disposed;

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        in Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out IntPtr ppv);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    private const uint CLSCTX_INPROC_SERVER = 0x1;
    private const uint COINIT_APARTMENTTHREADED = 0x2;
    private const int CO_E_NOTINITIALIZED = unchecked((int)0x800401F0);

    public ShellLink()
    {
        int hr = CoCreateInstance(
            in ShellLinkGuids.CLSID_ShellLink,
            IntPtr.Zero,
            CLSCTX_INPROC_SERVER,
            in ShellLinkGuids.IID_IShellLinkW,
            out _shellLink);

        if (hr == CO_E_NOTINITIALIZED)
        {
            CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
            hr = CoCreateInstance(
                in ShellLinkGuids.CLSID_ShellLink,
                IntPtr.Zero,
                CLSCTX_INPROC_SERVER,
                in ShellLinkGuids.IID_IShellLinkW,
                out _shellLink);
        }

        Marshal.ThrowExceptionForHR(hr);

        IntPtr* vtable = *(IntPtr**)_shellLink;
        var queryInterface = (delegate* unmanaged[Stdcall]<IntPtr, in Guid, out IntPtr, int>)vtable[0];
        hr = queryInterface(_shellLink, in ShellLinkGuids.IID_IPersistFile, out _persistFile);
        Marshal.ThrowExceptionForHR(hr);
    }

    public void SetPath(string path)
    {
        ThrowIfDisposed();
        IntPtr* vtable = *(IntPtr**)_shellLink;
        var setPath = (delegate* unmanaged[Stdcall]<IntPtr, char*, int>)vtable[20];
        fixed (char* p = path)
        {
            int hr = setPath(_shellLink, p);
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    public void SetWorkingDirectory(string workingDirectory)
    {
        ThrowIfDisposed();
        IntPtr* vtable = *(IntPtr**)_shellLink;
        var setDir = (delegate* unmanaged[Stdcall]<IntPtr, char*, int>)vtable[9];
        fixed (char* p = workingDirectory)
        {
            int hr = setDir(_shellLink, p);
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    public void SetArguments(string arguments)
    {
        ThrowIfDisposed();
        IntPtr* vtable = *(IntPtr**)_shellLink;
        var setArgs = (delegate* unmanaged[Stdcall]<IntPtr, char*, int>)vtable[11];
        fixed (char* p = arguments)
        {
            int hr = setArgs(_shellLink, p);
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    public void SetDescription(string description)
    {
        ThrowIfDisposed();
        IntPtr* vtable = *(IntPtr**)_shellLink;
        var setDesc = (delegate* unmanaged[Stdcall]<IntPtr, char*, int>)vtable[7];
        fixed (char* p = description)
        {
            int hr = setDesc(_shellLink, p);
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    public void SetIconLocation(string iconPath, int iconIndex)
    {
        ThrowIfDisposed();
        IntPtr* vtable = *(IntPtr**)_shellLink;
        var setIcon = (delegate* unmanaged[Stdcall]<IntPtr, char*, int, int>)vtable[17];
        fixed (char* p = iconPath)
        {
            int hr = setIcon(_shellLink, p, iconIndex);
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    public void Save(string filename, bool remember = true)
    {
        ThrowIfDisposed();
        IntPtr* vtable = *(IntPtr**)_persistFile;
        var save = (delegate* unmanaged[Stdcall]<IntPtr, char*, int, int>)vtable[6];
        fixed (char* p = filename)
        {
            int hr = save(_persistFile, p, remember ? 1 : 0);
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    public void Load(string filename, uint mode = 0)
    {
        ThrowIfDisposed();
        IntPtr* vtable = *(IntPtr**)_persistFile;
        var load = (delegate* unmanaged[Stdcall]<IntPtr, char*, uint, int>)vtable[5];
        fixed (char* p = filename)
        {
            int hr = load(_persistFile, p, mode);
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    public string GetPath()
    {
        ThrowIfDisposed();
        IntPtr* vtable = *(IntPtr**)_shellLink;
        var getPath = (delegate* unmanaged[Stdcall]<IntPtr, char*, int, IntPtr, uint, int>)vtable[3];
        char* buf = stackalloc char[1024];
        int hr = getPath(_shellLink, buf, 1024, IntPtr.Zero, 0);
        Marshal.ThrowExceptionForHR(hr);
        return new string(buf);
    }

    public string GetWorkingDirectory()
    {
        ThrowIfDisposed();
        IntPtr* vtable = *(IntPtr**)_shellLink;
        var getDir = (delegate* unmanaged[Stdcall]<IntPtr, char*, int, int>)vtable[8];
        char* buf = stackalloc char[1024];
        int hr = getDir(_shellLink, buf, 1024);
        Marshal.ThrowExceptionForHR(hr);
        return new string(buf);
    }

    public string GetArguments()
    {
        ThrowIfDisposed();
        IntPtr* vtable = *(IntPtr**)_shellLink;
        var getArgs = (delegate* unmanaged[Stdcall]<IntPtr, char*, int, int>)vtable[10];
        char* buf = stackalloc char[2048];
        int hr = getArgs(_shellLink, buf, 2048);
        Marshal.ThrowExceptionForHR(hr);
        return new string(buf);
    }

    public string GetDescription()
    {
        ThrowIfDisposed();
        IntPtr* vtable = *(IntPtr**)_shellLink;
        var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, char*, int, int>)vtable[6];
        char* buf = stackalloc char[2048];
        int hr = getDesc(_shellLink, buf, 2048);
        Marshal.ThrowExceptionForHR(hr);
        return new string(buf);
    }

    public (string iconPath, int iconIndex) GetIconLocation()
    {
        ThrowIfDisposed();
        IntPtr* vtable = *(IntPtr**)_shellLink;
        var getIcon = (delegate* unmanaged[Stdcall]<IntPtr, char*, int, out int, int>)vtable[16];
        char* buf = stackalloc char[1024];
        int hr = getIcon(_shellLink, buf, 1024, out int iconIndex);
        Marshal.ThrowExceptionForHR(hr);
        return (new string(buf), iconIndex);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ShellLink));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_persistFile != IntPtr.Zero)
        {
            IntPtr* vtable = *(IntPtr**)_persistFile;
            var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtable[2];
            release(_persistFile);
            _persistFile = IntPtr.Zero;
        }

        if (_shellLink != IntPtr.Zero)
        {
            IntPtr* vtable = *(IntPtr**)_shellLink;
            var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtable[2];
            release(_shellLink);
            _shellLink = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }

    ~ShellLink()
    {
        Dispose();
    }
}
