// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/Torjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PrismRGB.Core.Hid;

/// <summary>
/// Accès HID natif Windows (setupapi.dll + hid.dll), sans pilote ni logiciel tiers.
/// Même approche que hidapi : énumération des interfaces HID, lecture des caps, feature reports.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinHidProvider : IHidProvider
{
    public IReadOnlyList<HidDeviceInfo> Enumerate()
    {
        var list = new List<HidDeviceInfo>();
        Native.HidD_GetHidGuid(out var guid);
        IntPtr set = Native.SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (set == Native.INVALID_HANDLE_VALUE) return list;
        try
        {
            var data = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                string? path = GetPath(set, ref data);
                if (path is null) continue;
                var info = Describe(path);
                if (info is not null) list.Add(info);
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(set);
        }
        return list;
    }

    private static string? GetPath(IntPtr set, ref Native.SP_DEVICE_INTERFACE_DATA data)
    {
        Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
        if (required == 0) return null;
        IntPtr buf = Marshal.AllocHGlobal((int)required);
        try
        {
            // cbSize de SP_DEVICE_INTERFACE_DETAIL_DATA_W : 8 en 64 bits, 6 en 32 bits.
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
            if (!Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, buf, required, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(buf + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static HidDeviceInfo? Describe(string path)
    {
        // Accès 0 : permet de lire les attributs même des claviers/souris verrouillés par Windows.
        using var h = Native.CreateFileW(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;

        var attr = new Native.HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
        if (!Native.HidD_GetAttributes(h, ref attr)) return null;

        ushort usagePage = 0, usage = 0;
        int inLen = 0, outLen = 0, featLen = 0;
        if (Native.HidD_GetPreparsedData(h, out IntPtr pp))
        {
            try
            {
                if (Native.HidP_GetCaps(pp, out var caps) == Native.HIDP_STATUS_SUCCESS)
                {
                    usagePage = caps.UsagePage;
                    usage = caps.Usage;
                    inLen = caps.InputReportByteLength;
                    outLen = caps.OutputReportByteLength;
                    featLen = caps.FeatureReportByteLength;
                }
            }
            finally
            {
                Native.HidD_FreePreparsedData(pp);
            }
        }

        return new HidDeviceInfo(path, attr.VendorID, attr.ProductID, usagePage, usage,
            ReadString(h, Native.HidD_GetManufacturerString),
            ReadString(h, Native.HidD_GetProductString),
            ReadString(h, Native.HidD_GetSerialNumberString),
            inLen, outLen, featLen);
    }

    private delegate bool StringGetter(SafeFileHandle h, byte[] buffer, uint length);

    private static string ReadString(SafeFileHandle h, StringGetter getter)
    {
        var buf = new byte[512];
        if (!getter(h, buf, (uint)buf.Length)) return "";
        var s = Encoding.Unicode.GetString(buf);
        int nul = s.IndexOf('\0');
        return (nul >= 0 ? s[..nul] : s).Trim();
    }

    public IHidDevice? Open(HidDeviceInfo info)
    {
        // Mode "overlapped" (comme hidapi) : permet des lectures avec délai maximal, sans bloquer un thread.
        var h = Native.CreateFileW(info.Path, Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid)
        {
            h.Dispose();
            return null;
        }
        return new WinHidDevice(info, h);
    }

    private sealed class WinHidDevice : IHidDevice
    {
        private const int OverlappedSize = 32;
        private readonly SafeFileHandle _h;
        private readonly object _lock = new();
        private readonly ManualResetEvent _event = new(false);
        private readonly IntPtr _ov = Marshal.AllocHGlobal(OverlappedSize);
        private bool _disposed;

        public WinHidDevice(HidDeviceInfo info, SafeFileHandle h)
        {
            Info = info;
            _h = h;
        }

        public HidDeviceInfo Info { get; }

        /// <summary>Lance une E/S asynchrone et attend sa fin (ou l'annule après le délai).</summary>
        private int Io(byte[] buffer, Func<IntPtr, IntPtr, bool> start, int timeoutMs)
        {
            var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                for (int i = 0; i < OverlappedSize; i++) Marshal.WriteByte(_ov, i, 0);
                _event.Reset();
                Marshal.WriteIntPtr(_ov, 2 * IntPtr.Size + 8, _event.SafeWaitHandle.DangerousGetHandle()); // OVERLAPPED.hEvent
                if (!start(pin.AddrOfPinnedObject(), _ov))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 997) // ERROR_IO_PENDING
                    {
                        if (err is 1167 or 6 or 22) throw new IOException($"HID déconnecté ({new Win32Exception(err).Message})");
                        return -1;
                    }
                    if (!_event.WaitOne(timeoutMs))
                    {
                        Native.CancelIoEx(_h, _ov);
                        Native.GetOverlappedResult(_h, _ov, out _, true);
                        return 0;
                    }
                }
                return Native.GetOverlappedResult(_h, _ov, out uint done, false) ? (int)done : -1;
            }
            finally
            {
                pin.Free();
            }
        }

        public int GetFeature(byte[] buffer, int length)
        {
            lock (_lock)
            {
                // IOCTL direct (comme hidapi) : contrairement à HidD_GetFeature, renvoie la taille réelle du report.
                return Io(buffer, (p, ov) => Native.DeviceIoControl(_h, Native.IOCTL_HID_GET_FEATURE, p, (uint)length, p, (uint)length, IntPtr.Zero, ov), 1000);
            }
        }

        public bool SetFeature(ReadOnlySpan<byte> data)
        {
            lock (_lock)
            {
                int len = Math.Max(data.Length, Info.FeatureReportLength);
                var buf = new byte[len];
                data.CopyTo(buf);
                if (Native.HidD_SetFeature(_h, buf, (uint)len)) return true;
                int err = Marshal.GetLastWin32Error();
                // Appareil débranché ou handle invalide (veille) : on remonte pour reconnexion.
                if (err is 1167 or 6 or 22) throw new IOException($"HID déconnecté ({new Win32Exception(err).Message})");
                return false;
            }
        }

        public bool Write(ReadOnlySpan<byte> data)
        {
            lock (_lock)
            {
                var buf = new byte[Math.Max(data.Length, Info.OutputReportLength)];
                data.CopyTo(buf);
                return Io(buf, (p, ov) => Native.WriteFile(_h, p, (uint)buf.Length, IntPtr.Zero, ov), 1000) > 0;
            }
        }

        public int Read(byte[] buffer, int timeoutMs)
        {
            lock (_lock)
            {
                var buf = new byte[Math.Max(Info.InputReportLength, 1)];
                int n = Io(buf, (p, ov) => Native.ReadFile(_h, p, (uint)buf.Length, IntPtr.Zero, ov), timeoutMs);
                if (n <= 0) return n;
                // Comme hidapi : pour un appareil sans identifiant de report (0), l'octet d'identifiant est retiré.
                int skip = buf[0] == 0 ? 1 : 0;
                int count = Math.Min(n - skip, buffer.Length);
                Array.Copy(buf, skip, buffer, 0, count);
                return count;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _h.Dispose();
                _event.Dispose();
                Marshal.FreeHGlobal(_ov);
            }
        }
    }

    private static class Native
    {
        public const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        public const int HIDP_STATUS_SUCCESS = 0x00110000;
        // CTL_CODE(FILE_DEVICE_KEYBOARD, 100, METHOD_OUT_DIRECT, FILE_ANY_ACCESS)
        public const uint IOCTL_HID_GET_FEATURE = 0x000B0192;
        public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public UIntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public uint Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Explicit, Size = 64)]
        public struct HIDP_CAPS
        {
            [FieldOffset(0)] public ushort Usage;
            [FieldOffset(2)] public ushort UsagePage;
            [FieldOffset(4)] public ushort InputReportByteLength;
            [FieldOffset(6)] public ushort OutputReportByteLength;
            [FieldOffset(8)] public ushort FeatureReportByteLength;
        }

        [DllImport("hid.dll")]
        public static extern void HidD_GetHidGuid(out Guid guid);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, string? enumerator, IntPtr hwnd, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint required, IntPtr devInfo);

        [DllImport("setupapi.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr sa, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inLen, IntPtr outBuf, uint outLen, IntPtr returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadFile(SafeFileHandle h, IntPtr buffer, uint length, IntPtr read, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WriteFile(SafeFileHandle h, IntPtr buffer, uint length, IntPtr written, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr overlapped, out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attr);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_FreePreparsedData(IntPtr data);

        [DllImport("hid.dll")]
        public static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_GetManufacturerString(SafeFileHandle h, byte[] buffer, uint length);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buffer, uint length);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_GetSerialNumberString(SafeFileHandle h, byte[] buffer, uint length);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, uint length);
    }
}
