// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/T0rjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace PrismRGB.Core.Smbus;

/// <summary>
/// Bus SMBus de la carte mère (celui des barrettes de RAM). Toutes les méthodes renvoient -1 / false en cas d'échec
/// (adresse absente, bus occupé...) et ne lèvent pas d'exception.
/// </summary>
public interface ISmbus : IDisposable
{
    string Name { get; }

    /// <summary>"Receive byte" : lecture d'un octet sans registre (sert aussi de test de présence).</summary>
    int ReadByte(byte address);
    int ReadByteData(byte address, byte command);
    int ReadWordData(byte address, byte command);
    bool WriteByte(byte address, byte value);
    bool WriteByteData(byte address, byte command, byte value);
    bool WriteWordData(byte address, byte command, ushort value);
    /// <summary>Écriture bloc SMBus (1 à 32 octets).</summary>
    bool WriteBlockData(byte address, byte command, ReadOnlySpan<byte> data);
}

/// <summary>
/// Garde-fou : refuse toute écriture vers les puces SPD des barrettes (0x50-0x57), à l'exception du registre
/// de page (MR11) des hubs DDR5, et vers les adresses de page DDR4 réservées à la lecture du SPD.
/// Même un pilote bogué ne peut donc pas modifier la mémoire de configuration de la RAM.
/// </summary>
public sealed class SafeSmbus(ISmbus inner) : ISmbus
{
    public const byte Ddr5PageRegister = 0x0B;
    private readonly object _lock = new();

    public ISmbus Inner => inner;
    public string Name => inner.Name;

    private static bool IsSpd(byte address) => address is >= 0x50 and <= 0x57;

    public int ReadByte(byte address) { lock (_lock) return inner.ReadByte(address); }
    public int ReadByteData(byte address, byte command) { lock (_lock) return inner.ReadByteData(address, command); }
    public int ReadWordData(byte address, byte command) { lock (_lock) return inner.ReadWordData(address, command); }

    public bool WriteByte(byte address, byte value)
    {
        // 0x36 / 0x37 : sélection de page SPD DDR4 (standard JEDEC, sans donnée).
        if (IsSpd(address)) return false;
        lock (_lock) return inner.WriteByte(address, value);
    }

    public bool WriteByteData(byte address, byte command, byte value)
    {
        if (IsSpd(address) && !(command == Ddr5PageRegister && value < 8)) return false;
        lock (_lock) return inner.WriteByteData(address, command, value);
    }

    public bool WriteWordData(byte address, byte command, ushort value)
    {
        if (IsSpd(address)) return false;
        lock (_lock) return inner.WriteWordData(address, command, value);
    }

    public bool WriteBlockData(byte address, byte command, ReadOnlySpan<byte> data)
    {
        if (IsSpd(address)) return false;
        lock (_lock) return inner.WriteBlockData(address, command, data);
    }

    public void Dispose() => inner.Dispose();
}
