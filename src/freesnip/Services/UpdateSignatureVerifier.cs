using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;
using freesnip.foundation.core;

namespace freesnip.services;

/// <summary>
/// Fail-closed Authenticode verification for update payloads.
/// Releases are signed with a self-signed "CN=FreeSnip" certificate, so chain trust cannot be the
/// authority. Instead the file digest is verified by WinVerifyTrust and the signer certificate is
/// pinned to the certificate that signed the currently running executable (trust-on-install).
/// An unsigned running build (developer build) can never accept an update.
/// </summary>
internal static class UpdateSignatureVerifier
{
    private static readonly log4net.ILog Log = LogHelper.GetLogger(typeof(UpdateSignatureVerifier));

    private const int S_OK = 0;
    private const int CERT_E_UNTRUSTEDROOT = unchecked((int)0x800B0109);
    private const int CERT_E_CHAINING = unchecked((int)0x800B010A);
    private const int CERT_E_UNTRUSTEDTESTROOT = unchecked((int)0x800B010D);

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x10;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

    private static readonly Guid WintrustActionGenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private static readonly Lazy<string> CurrentSignerThumbprint = new Lazy<string>(() =>
    {
        string exe = RuntimePathHelper.ExecutablePath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return null;
        try
        {
            using var handle = File.OpenHandle(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TryGetVerifiedSignerThumbprint(exe, handle);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read signer of running executable: {ex.Message}");
            return null;
        }
    });

    public static bool IsRunningBuildSigned => CurrentSignerThumbprint.Value != null;

    /// <summary>
    /// True only when <paramref name="filePath"/> carries an intact Authenticode signature whose signer
    /// certificate is identical to the running executable's signer. The caller should hold
    /// <paramref name="lockedHandle"/> open (FileShare.Read) across verification and use of the file.
    /// </summary>
    public static bool IsSignedBySameSigner(string filePath, SafeFileHandle lockedHandle)
    {
        string expected = CurrentSignerThumbprint.Value;
        if (expected == null)
        {
            Log.Warn("Update signature rejected: running build is unsigned, no signer pin available.");
            return false;
        }

        string actual = TryGetVerifiedSignerThumbprint(filePath, lockedHandle);
        if (actual == null) return false;

        bool match = CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(expected),
            System.Text.Encoding.ASCII.GetBytes(actual));
        if (!match)
        {
            Log.Warn($"Update signature rejected: signer {actual} does not match pinned signer {expected}.");
        }
        return match;
    }

    public static bool IsSignedBySameSigner(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;
        try
        {
            using var handle = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return IsSignedBySameSigner(filePath, handle);
        }
        catch (Exception ex)
        {
            Log.Warn($"Update signature rejected for {filePath}: {ex.Message}");
            return false;
        }
    }

    private static string TryGetVerifiedSignerThumbprint(string filePath, SafeFileHandle handle)
    {
        int status = VerifyEmbeddedSignature(filePath, handle);
        if (status != S_OK && status != CERT_E_UNTRUSTEDROOT && status != CERT_E_CHAINING && status != CERT_E_UNTRUSTEDTESTROOT)
        {
            Log.Warn($"WinVerifyTrust rejected {filePath}: 0x{status:X8}");
            return null;
        }

        try
        {
#pragma warning disable SYSLIB0057
            using X509Certificate cert = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            return Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
        }
        catch (Exception ex)
        {
            Log.Warn($"Signer certificate could not be read from {filePath}: {ex.Message}");
            return null;
        }
    }

    private static unsafe int VerifyEmbeddedSignature(string filePath, SafeFileHandle handle)
    {
        bool addedRef = false;
        IntPtr pathPtr = Marshal.StringToHGlobalUni(filePath);
        try
        {
            handle.DangerousAddRef(ref addedRef);
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)sizeof(WINTRUST_FILE_INFO),
                pcwszFilePath = pathPtr,
                hFile = handle.DangerousGetHandle(),
                pgKnownSubject = IntPtr.Zero
            };
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(WINTRUST_DATA),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = (IntPtr)(&fileInfo),
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            Guid action = WintrustActionGenericVerifyV2;
            int result = WinVerifyTrust(new IntPtr(-1), &action, &data);
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(new IntPtr(-1), &action, &data);
            return result;
        }
        finally
        {
            if (addedRef) handle.DangerousRelease();
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern unsafe int WinVerifyTrust(IntPtr hwnd, Guid* pgActionID, WINTRUST_DATA* pWVTData);
}
