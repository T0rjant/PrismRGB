// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PrismRGB.Core.Smbus;

public enum PawnIoStatus
{
    Unsupported,
    /// <summary>Pilote PawnIO non installé.</summary>
    NotInstalled,
    /// <summary>Installé, mais PrismRGB doit être lancé en administrateur.</summary>
    NeedsAdmin,
    Ready,
}

/// <summary>
/// Accès au pilote PawnIO (https://pawnio.eu, signé, open source GPL-2.0) qui exécute des modules
/// vérifiés et signés par son auteur. PrismRGB n'embarque aucun pilote noyau : il utilise uniquement
/// les modules officiels SmbusPIIX4 (AMD) et SmbusI801 (Intel), sous licence LGPL-2.1.
/// Interface identique à PawnIOLib (IOCTL_PIO_LOAD_BINARY / IOCTL_PIO_EXECUTE_FN).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PawnIo : IDisposable
{
    private const string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";
    // CTL_CODE(41394, fonction, METHOD_BUFFERED, FILE_ANY_ACCESS)
    private const uint IoctlLoadBinary = (41394u << 16) | (0x821u << 2);
    private const uint IoctlExecuteFn = (41394u << 16) | (0x841u << 2);
    private const int FnNameLength = 32;

    private readonly SafeFileHandle _h;

    private PawnIo(SafeFileHandle h) => _h = h;

    public static PawnIoStatus Probe()
    {
        if (!OperatingSystem.IsWindows()) return PawnIoStatus.Unsupported;
        using var h = Open();
        if (!h.IsInvalid) return PawnIoStatus.Ready;
        int err = Marshal.GetLastWin32Error();
        return err == 5 ? PawnIoStatus.NeedsAdmin : PawnIoStatus.NotInstalled;
    }

    private static SafeFileHandle Open() =>
        CreateFileW(DevicePath, 0xC0000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);

    /// <summary>Ouvre un exécuteur et charge un module embarqué (ex. "SmbusPIIX4.bin").</summary>
    public static PawnIo? Load(string module, out string? error)
    {
        error = null;
        var h = Open();
        if (h.IsInvalid)
        {
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            h.Dispose();
            return null;
        }
        var blob = ReadModule(module);
        if (blob is null)
        {
            error = $"Module {module} introuvable";
            h.Dispose();
            return null;
        }
        if (!DeviceIoControl(h, IoctlLoadBinary, blob, (uint)blob.Length, null, 0, out _, IntPtr.Zero))
        {
            // Le module refuse de se charger si le matériel ne correspond pas (ex. PIIX4 sur un PC Intel).
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            h.Dispose();
            return null;
        }
        return new PawnIo(h);
    }

    private static byte[]? ReadModule(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("." + name, StringComparison.Ordinal));
        if (res is null) return null;
        using var s = asm.GetManifestResourceStream(res)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Exécute une fonction du module. Retourne false si le pilote renvoie une erreur.</summary>
    public bool Execute(string function, ReadOnlySpan<ulong> input, Span<ulong> output, out int written)
    {
        written = 0;
        var inBuf = new byte[FnNameLength + input.Length * 8];
        Encoding.ASCII.GetBytes(function, inBuf.AsSpan(0, Math.Min(function.Length, FnNameLength - 1)));
        for (int i = 0; i < input.Length; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(inBuf.AsSpan(FnNameLength + i * 8), input[i]);
        var outBuf = new byte[output.Length * 8];
        if (!DeviceIoControl(_h, IoctlExecuteFn, inBuf, (uint)inBuf.Length, outBuf, (uint)outBuf.Length, out uint ret, IntPtr.Zero))
            return false;
        written = (int)(ret / 8);
        for (int i = 0; i < written && i < output.Length; i++)
            output[i] = BinaryPrimitives.ReadUInt64LittleEndian(outBuf.AsSpan(i * 8));
        return true;
    }

    public void Dispose() => _h.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr sa, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[]? inBuf, uint inLen, byte[]? outBuf, uint outLen, out uint returned, IntPtr overlapped);
}
