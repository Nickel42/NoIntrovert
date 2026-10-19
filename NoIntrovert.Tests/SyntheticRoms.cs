using System.Text;
using NoIntrovert.Core.Cd;

namespace NoIntrovert.Tests;

/// <summary>
/// Stand-ins for the few test-roms files the quick tests slice their fixtures
/// from, for machines without that folder (CI). Each is a valid image of the
/// same kind: Mode 2 and Mode 1 data sectors with correct EDC/ECC, CD audio,
/// and a PSP-style ISO. They are written under the same relative paths, so the
/// tests only need a different roms folder.
/// </summary>
internal static class SyntheticRoms
{
    public static void Write(string root)
    {
        var random = new Random(4242);
        WriteBin(Path.Combine(root, "PS1", "Crash Bandicoot (USA)", "Crash Bandicoot (USA).bin"), 3200, random, Mode2Sector);
        WriteBin(Path.Combine(root, "PS1", "Bloody Roar (USA)", "Bloody Roar (USA) (Track 2).bin"), 400, random, AudioSector);
        WriteBin(Path.Combine(root, "Saturn", "Golden Axe - The Duel (USA)", "Golden Axe - The Duel (USA) (Track 01).bin"), 2200, random, Mode1Sector);
        WritePspIso(Path.Combine(root, "PSP", "Corpse Party PSP (English)", "Corpse Party PSP (English).iso"), 40L << 20, random);
    }

    private delegate void SectorWriter(byte[] sector, int lba, Random random);

    private static void WriteBin(string path, int sectors, Random random, SectorWriter write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var o = new BufferedStream(File.Create(path), 1 << 20);
        var sector = new byte[CdSector.RawSize];
        for (var lba = 0; lba < sectors; lba++)
        {
            Array.Clear(sector);
            write(sector, lba, random);
            o.Write(sector);
        }
    }

    /// <summary>Mostly-compressible user data: text-like runs mixed with noise and zeros.</summary>
    private static void Fill(Span<byte> data, Random random)
    {
        switch (random.Next(4))
        {
            case 0: break; // zeros
            case 1: random.NextBytes(data); break;
            default:
                for (var i = 0; i < data.Length; i++) data[i] = (byte)('a' + (i * 7 + random.Next(3)) % 26);
                break;
        }
    }

    private static void WriteHeader(byte[] sector, int lba, byte mode)
    {
        CdSector.WriteSync(sector);
        var frames = lba + 150;
        sector[12] = Bcd(frames / (60 * 75));
        sector[13] = Bcd(frames / 75 % 60);
        sector[14] = Bcd(frames % 75);
        sector[15] = mode;
    }

    private static byte Bcd(int value) => (byte)((value / 10 << 4) | (value % 10));

    private static void Mode1Sector(byte[] sector, int lba, Random random)
    {
        WriteHeader(sector, lba, 1);
        Fill(sector.AsSpan(0x10, 2048), random);
        CdSector.WriteEdc(sector, 0, 0x810, 0x810);
        CdSector.WriteEcc(sector, false);
    }

    private static void Mode2Sector(byte[] sector, int lba, Random random)
    {
        WriteHeader(sector, lba, 2);
        var form2 = lba >= 24 && lba % 5 == 0; // like XA streams interleaved with data
        byte submode = (byte)(form2 ? 0x20 | 0x04 : 0x08);
        sector[0x10] = sector[0x14] = 0;
        sector[0x11] = sector[0x15] = 0;
        sector[0x12] = sector[0x16] = submode;
        sector[0x13] = sector[0x17] = 0;
        if (form2)
        {
            Fill(sector.AsSpan(0x18, 2324), random);
            CdSector.WriteEdc(sector, 0x10, 0x91C, 0x92C);
        }
        else
        {
            Fill(sector.AsSpan(0x18, 2048), random);
            CdSector.WriteEdc(sector, 0x10, 0x808, 0x818);
            CdSector.WriteEcc(sector, true);
        }
    }

    private static void AudioSector(byte[] sector, int lba, Random random)
    {
        // A quiet tone with a little noise, as 16-bit stereo samples.
        for (var i = 0; i < CdSector.RawSize / 4; i++)
        {
            var t = lba * (CdSector.RawSize / 4) + i;
            var sample = (short)(Math.Sin(t * 0.03) * 3000 + random.Next(-64, 64));
            sector[i * 4] = sector[i * 4 + 2] = (byte)sample;
            sector[i * 4 + 1] = sector[i * 4 + 3] = (byte)(sample >> 8);
        }
    }

    private static void WritePspIso(string path, long length, Random random)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var o = new BufferedStream(File.Create(path), 1 << 20);
        var sector = new byte[2048];
        for (long s = 0; s < length / 2048; s++)
        {
            Array.Clear(sector);
            if (s == 16)
            {
                // Primary volume descriptor whose system identifier marks a UMD.
                sector[0] = 1;
                Encoding.ASCII.GetBytes("CD001").CopyTo(sector, 1);
                sector[6] = 1;
                Encoding.ASCII.GetBytes("PSP GAME".PadRight(32)).CopyTo(sector, 8);
                Encoding.ASCII.GetBytes("SYNTHETIC".PadRight(32)).CopyTo(sector, 40);
            }
            else if (s == 17)
            {
                sector[0] = 255;
                Encoding.ASCII.GetBytes("CD001").CopyTo(sector, 1);
                sector[6] = 1;
            }
            else if (s > 17)
            {
                Fill(sector, random);
            }
            o.Write(sector);
        }
    }
}
