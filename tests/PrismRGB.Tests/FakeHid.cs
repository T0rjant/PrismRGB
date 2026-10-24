// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using PrismRGB.Core.Hid;

namespace PrismRGB.Tests;

/// <summary>Carte MSI simulée : répond aux feature reports comme le vrai contrôleur et enregistre tout ce qu'elle reçoit.</summary>
public sealed class FakeMsiBoard
{
    public FakeMsiBoard(ushort vid, ushort pid, string serial, int report52Length, int featureLength)
    {
        Info = new HidDeviceInfo($@"\\?\hid#vid_{vid:x4}&pid_{pid:x4}#{serial}", vid, pid, 0x0001, 0x00, "MSI", "MYSTIC LIGHT", serial, 64, 64, featureLength);
        Report52Length = report52Length;
    }

    public HidDeviceInfo Info { get; }
    public int Report52Length { get; }
    public ConcurrentQueue<byte[]> Sent { get; } = new();
    public volatile bool Unplugged;
    public int Opens;

    public IEnumerable<byte[]> SentWithId(byte id) => Sent.Where(p => p[0] == id);
}

public sealed class FakeHidProvider : IHidProvider
{
    public List<FakeMsiBoard> Boards { get; } = [];
    public List<HidDeviceInfo> Others { get; } = [];

    public IReadOnlyList<HidDeviceInfo> Enumerate() =>
        [.. Boards.Where(b => !b.Unplugged).Select(b => b.Info), .. Others];

    public IHidDevice? Open(HidDeviceInfo info)
    {
        var b = Boards.FirstOrDefault(x => x.Info.Path == info.Path && !x.Unplugged);
        if (b is null) return null;
        Interlocked.Increment(ref b.Opens);
        return new Dev(b);
    }

    private sealed class Dev(FakeMsiBoard board) : IHidDevice
    {
        public HidDeviceInfo Info => board.Info;

        public int GetFeature(byte[] buffer, int length)
        {
            if (board.Unplugged) return -1;
            if (buffer[0] == 0x52)
            {
                Array.Clear(buffer, 1, length - 1);
                return Math.Min(board.Report52Length, length);
            }
            return length;
        }

        public bool SetFeature(ReadOnlySpan<byte> data)
        {
            if (board.Unplugged) throw new IOException("débranché");
            board.Sent.Enqueue(data.ToArray());
            return true;
        }

        public bool Write(ReadOnlySpan<byte> data) => SetFeature(data);
        public int Read(byte[] buffer, int timeoutMs) => 0;

        public void Dispose() { }
    }
}
