// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace PrismRGB.Core.Smbus;

public enum DramType { Unknown, Ddr4, Ddr5 }

/// <summary>Barrette détectée par sa puce SPD (adresse 0x50 + index).</summary>
public sealed record DramSlot(int Index, DramType Type, ushort JedecId, string PartNumber, byte[] ManufacturerData)
{
    public byte SpdAddress => (byte)(0x50 + Index);
    public string Vendor => Jedec.Name(JedecId);
}

public static class Jedec
{
    public const ushort Kingston = 0x0117, Kingston2 = 0x300F, Kingston3 = 0x3011;
    public const ushort Corsair = 0x021D;
    public const ushort Adata = 0x044A, GSkill = 0x044C, TeamGroup = 0x046E;
    public const ushort Mushkin = 0x8313, Gigabyte = 0x8971, Thermaltake = 0x8A41, Patriot = 0x8501;

    public static bool IsKingston(ushort id) => id is Kingston or Kingston2 or Kingston3;

    public static string Name(ushort id) => id switch
    {
        Kingston or Kingston2 or Kingston3 => "Kingston",
        Corsair => "Corsair",
        Adata => "ADATA / XPG",
        GSkill => "G.Skill",
        TeamGroup => "TeamGroup",
        Mushkin => "Mushkin",
        Gigabyte => "Gigabyte",
        Thermaltake => "Thermaltake",
        Patriot => "Patriot",
        0 => "inconnu",
        _ => $"JEDEC {id:X4}",
    };
}

/// <summary>
/// Lecture des puces SPD des barrettes (identification fabricant / référence), d'après les accesseurs
/// DDR4/DDR5 d'OpenRGB (GPL-2.0-or-later). Lecture seule, hormis la sélection de page standard JEDEC
/// (registre MR11 des hubs DDR5, adresses 0x36/0x37 en DDR4) ; la page 0 est restaurée à la fin.
/// </summary>
public static class SpdReader
{
    private const int Ddr5JedecAddr = 0x200, Ddr5PartStart = 0x209, Ddr5PartLen = 30, Ddr5ManufStart = 0x22B, Ddr5ManufLen = 0x55;
    private const int Ddr4JedecAddr = 0x140, Ddr4PartStart = 0x149, Ddr4PartLen = 20, Ddr4ManufStart = 0x161, Ddr4ManufLen = 0x1D;

    public static List<DramSlot> ReadAll(ISmbus bus)
    {
        var slots = new List<DramSlot>();
        for (int i = 0; i < 8; i++)
        {
            byte addr = (byte)(0x50 + i);
            try
            {
                if (IsDdr5(bus, addr)) slots.Add(ReadDdr5(bus, i));
                else if (IsDdr4(bus, addr)) slots.Add(ReadDdr4(bus, i));
            }
            catch
            {
                // une barrette illisible ne doit pas empêcher les autres
            }
            Thread.Sleep(1);
        }
        return slots;
    }

    private static void Delay() => Thread.Sleep(1);

    private static bool IsDdr5(ISmbus bus, byte addr)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int magic = bus.ReadByteData(addr, 0x00); Delay();
            int sensor = bus.ReadByteData(addr, 0x01); Delay();
            if (magic < 0 || sensor < 0) return false;
            if (magic == 0x51 && (sensor & 0xEF) == 0x08) return true;
            int page = bus.ReadByteData(addr, SafeSmbus.Ddr5PageRegister); Delay();
            // Un autre logiciel a pu laisser le hub sur une autre page : on revient en page 0 et on réessaie.
            if (attempt == 0 && page is > 0 and < 16)
            {
                bus.WriteByteData(addr, SafeSmbus.Ddr5PageRegister, 0); Delay();
                continue;
            }
            return false;
        }
        return false;
    }

    private static bool IsDdr4(ISmbus bus, byte addr)
    {
        bus.WriteByte(0x36, 0x00); Delay();
        return bus.ReadByteData(addr, 0x00) == 0x23;
    }

    private static DramSlot ReadDdr5(ISmbus bus, int index)
    {
        byte addr = (byte)(0x50 + index);
        int currentPage = -1;
        byte At(int a)
        {
            int page = a >> 7;
            if (page != currentPage)
            {
                bus.WriteByteData(addr, SafeSmbus.Ddr5PageRegister, (byte)page);
                currentPage = page;
                Delay();
            }
            int v = bus.ReadByteData(addr, (byte)((a & 0x7F) | 0x80));
            Delay();
            return (byte)Math.Max(0, v);
        }

        try
        {
            ushort jedec = (ushort)(((At(Ddr5JedecAddr) << 8) + (At(Ddr5JedecAddr + 1) & 0x7F) - 1) & 0xFFFF);
            var part = new byte[Ddr5PartLen];
            for (int i = 0; i < part.Length; i++) part[i] = At(Ddr5PartStart + i);
            var manuf = new byte[Ddr5ManufLen];
            for (int i = 0; i < manuf.Length; i++) manuf[i] = At(Ddr5ManufStart + i);
            return new DramSlot(index, DramType.Ddr5, jedec, Clean(part), manuf);
        }
        finally
        {
            bus.WriteByteData(addr, SafeSmbus.Ddr5PageRegister, 0);
            Delay();
        }
    }

    private static DramSlot ReadDdr4(ISmbus bus, int index)
    {
        byte addr = (byte)(0x50 + index);
        int currentPage = -1;
        byte At(int a)
        {
            int page = a >= 0x100 ? 1 : 0;
            if (page != currentPage)
            {
                bus.WriteByte(page == 0 ? (byte)0x36 : (byte)0x37, 0x00);
                currentPage = page;
                Delay();
            }
            int v = bus.ReadByteData(addr, (byte)(a & 0xFF));
            return (byte)Math.Max(0, v);
        }

        try
        {
            ushort jedec = (ushort)(((At(Ddr4JedecAddr) << 8) + (At(Ddr4JedecAddr + 1) & 0x7F) - 1) & 0xFFFF);
            var part = new byte[Ddr4PartLen];
            for (int i = 0; i < part.Length; i++) part[i] = At(Ddr4PartStart + i);
            var manuf = new byte[Ddr4ManufLen];
            for (int i = 0; i < manuf.Length; i++) manuf[i] = At(Ddr4ManufStart + i);
            return new DramSlot(index, DramType.Ddr4, jedec, Clean(part), manuf);
        }
        finally
        {
            bus.WriteByte(0x36, 0x00);
            Delay();
        }
    }

    private static string Clean(byte[] b) =>
        new string(b.Select(c => c is >= 0x20 and < 0x7F ? (char)c : ' ').ToArray()).Trim();
}
