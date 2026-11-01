using System.Buffers.Binary;
using System.Text;

namespace NoIntrovert.Tests;

/// <summary>
/// Small stand-in GameCube discs for the lib-wii-gamecube-kit tests: a real disc
/// header and FST with two files, and pseudo-random padding elsewhere.
/// (lib-wii-gamecube-kit's own tests cover Wii partitions and junk-data packing.)
/// </summary>
internal static class NintendoDiscs
{
    public static void WriteGameCube(string path, int sizeMiB = 6)
    {
        var d = new byte[sizeMiB << 20];
        Encoding.ASCII.GetBytes("GNIE01").CopyTo(d, 0);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(0x1C), 0xC2339F3D);
        Encoding.ASCII.GetBytes("NoIntrovert GameCube test disc").CopyTo(d, 0x20);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(0x420), 0x8000);  // DOL (empty header)
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(0x458), 1);       // region: NTSC-U

        // FST: root + two files.
        (string Name, int Offset, int Size)[] files = [("banner.bnr", 0x20000, 0x1960), ("data.bin", 0x100000, 0x200000)];
        const int fst = 0x10000;
        var strings = new MemoryStream();
        d[fst] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(fst + 8), (uint)files.Length + 1);
        for (var i = 0; i < files.Length; i++)
        {
            var e = fst + (i + 1) * 12;
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(e), (uint)strings.Length);
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(e + 4), (uint)files[i].Offset);
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(e + 8), (uint)files[i].Size);
            strings.Write(Encoding.ASCII.GetBytes(files[i].Name + "\0"));
            for (var j = 0; j < files[i].Size; j++)
                d[files[i].Offset + j] = (byte)((j * 7 + i) ^ (j >> 9));
        }
        strings.ToArray().CopyTo(d, fst + (files.Length + 1) * 12);
        var fstSize = (uint)((files.Length + 1) * 12 + strings.Length);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(0x424), fst);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(0x428), fstSize);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(0x42C), fstSize);

        // Some incompressible padding after the last file, like a real disc has.
        new Random(1234).NextBytes(d.AsSpan(0x400000, 0x40000));
        File.WriteAllBytes(path, d);
    }

    /// <summary>The start of a WBFS file: enough for lib-wii-gamecube-kit to see what it is.</summary>
    public static byte[] FakeWbfsHeader()
    {
        var d = new byte[0x10000];
        Encoding.ASCII.GetBytes("WBFS").CopyTo(d, 0);
        return d;
    }
}
