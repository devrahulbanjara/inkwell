using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Inkwell.Core;

/// <summary>
/// Encrypts and decrypts secrets using Windows DPAPI (CryptProtectData / CryptUnprotectData)
/// bound to the current Windows user account, with zero external NuGet dependencies.
/// </summary>
public static class DpapiVault
{
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Inkwell.Windows.Vault.v1");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static string? Read(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            var encrypted = File.ReadAllBytes(filePath);
            if (encrypted.Length == 0) return null;
            var plain = Unprotect(encrypted);
            return plain is null ? null : Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    public static void Write(string filePath, string value)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var plain = Encoding.UTF8.GetBytes(value);
        var encrypted = Protect(plain);
        var tmp = filePath + ".tmp";
        File.WriteAllBytes(tmp, encrypted);
        File.Move(tmp, filePath, overwrite: true);
    }

    private static byte[] Protect(byte[] data)
    {
        var inBlob = AllocBlob(data);
        var entropyBlob = AllocBlob(Entropy);
        var outBlob = new DATA_BLOB();
        try
        {
            if (!CryptProtectData(ref inBlob, "Inkwell API Keys", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob))
            {
                throw new InvalidOperationException($"DPAPI CryptProtectData failed ({Marshal.GetLastWin32Error()}).");
            }
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            FreeBlob(ref inBlob);
            FreeBlob(ref entropyBlob);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    private static byte[]? Unprotect(byte[] data)
    {
        var inBlob = AllocBlob(data);
        var entropyBlob = AllocBlob(Entropy);
        var outBlob = new DATA_BLOB();
        try
        {
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob))
            {
                return null;
            }
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            FreeBlob(ref inBlob);
            FreeBlob(ref entropyBlob);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    private static DATA_BLOB AllocBlob(byte[] bytes)
    {
        if (bytes.Length == 0) return new DATA_BLOB();
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return new DATA_BLOB { cbData = bytes.Length, pbData = ptr };
    }

    private static void FreeBlob(ref DATA_BLOB blob)
    {
        if (blob.pbData != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.pbData);
            blob.pbData = IntPtr.Zero;
            blob.cbData = 0;
        }
    }
}
