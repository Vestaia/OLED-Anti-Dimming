// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace OledCalibration;

static class BridgeBinary
{
    // Only linker timestamps are ignored. Code, exports, and every other byte
    // must still match before reusing an already loaded bridge.
    internal static byte[] Fingerprint(byte[] image)
    {
        image = (byte[])image.Clone();
        uint Read(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset, 4));
        int pe = checked((int)Read(60));
        if (Read(pe) != 0x4550 || BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 24, 2)) != 0x20b)
            throw new InvalidDataException("Invalid x64 bridge image.");
        image.AsSpan(pe + 8, 4).Clear();
        int MapRva(uint rva)
        {
            int sections = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 6, 2));
            int section = pe + 24 + BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 20, 2));
            for (int i = 0; i < sections; i++, section += 40)
            {
                uint start = Read(section + 12), size = Read(section + 16);
                if (rva >= start && rva - start < size)
                    return checked((int)(Read(section + 20) + rva - start));
            }
            throw new InvalidDataException("Bridge metadata directory is missing.");
        }
        image.AsSpan(MapRva(Read(pe + 136)) + 4, 4).Clear();
        uint debugRva = Read(pe + 136 + 6 * 8), debugSize = Read(pe + 140 + 6 * 8);
        if (debugRva != 0)
        {
            int debug = MapRva(debugRva);
            for (int i = 0; i < debugSize / 28; i++)
                image.AsSpan(debug + i * 28 + 4, 4).Clear();
        }
        return SHA256.HashData(image);
    }
    public static bool Equivalent(string first, string second) => Fingerprint(File.ReadAllBytes(first)).SequenceEqual(Fingerprint(File.ReadAllBytes(second)));
}
