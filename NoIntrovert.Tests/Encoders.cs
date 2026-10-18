using System.IO.Compression;
using System.Text;
using K4os.Compression.LZ4;
using NoIntrovert.Core.Cd;
using NoIntrovert.Core.Formats;
using SharpCompress.Compressors.BZip2;

namespace NoIntrovert.Tests;

/// <summary>
/// Writers for the formats the app reads, used only to make test inputs.
/// Each follows the same layout the matching decoder documents.
/// </summary>
internal static class Encoders
{
    // ---- ECM ----

    public static void WriteEcm(string binPath, string ecmPath)
    {
        var data = File.ReadAllBytes(binPath);
        using var output = new BufferedStream(File.Create(ecmPath));
        output.Write("ECM\0"u8);
        var pending = new List<(int Type, byte[] Payload)>();
        uint edc = 0;

        void Flush()
        {
            var i = 0;
            while (i < pending.Count)
            {
                var type = pending[i].Type;
                var j = i;
                while (j < pending.Count && pending[j].Type == type) j++;
                if (type == 0)
                {
                    var bytes = pending.Skip(i).Take(j - i).SelectMany(p => p.Payload).ToArray();
                    WriteRecordHeader(output, 0, (uint)bytes.Length - 1);
                    output.Write(bytes);
                }
                else
                {
                    WriteRecordHeader(output, type, (uint)(j - i) - 1);
                    for (var k = i; k < j; k++) output.Write(pending[k].Payload);
                }
                i = j;
            }
            pending.Clear();
        }

        var sector = new byte[CdSector.RawSize];
        for (var pos = 0; pos < data.Length;)
        {
            if (data.Length - pos >= CdSector.RawSize)
            {
                Array.Copy(data, pos, sector, 0, sector.Length);
                if (CdSector.HasSync(sector) && sector[15] == 1 && Rebuilds(sector, 1))
                {
                    pending.Add((1, [.. sector.AsSpan(12, 3), .. sector.AsSpan(16, 2048)]));
                    edc = CdSector.ComputeEdc(sector, edc);
                    pos += CdSector.RawSize;
                    continue;
                }
                if (CdSector.HasSync(sector) && sector[15] == 2)
                {
                    pending.Add((0, sector[..16]));
                    edc = CdSector.ComputeEdc(sector.AsSpan(0, 16), edc);
                    var form2 = (sector[18] & 0x20) != 0;
                    var type = form2 ? 3 : 2;
                    if (Rebuilds(sector, type))
                    {
                        pending.Add((type, form2 ? sector[0x14..0x92C] : sector[0x14..0x818]));
                        edc = CdSector.ComputeEdc(sector.AsSpan(16), edc);
                        pos += CdSector.RawSize;
                        continue;
                    }
                    pending.Add((0, sector[16..]));
                    edc = CdSector.ComputeEdc(sector.AsSpan(16), edc);
                    pos += CdSector.RawSize;
                    continue;
                }
            }
            var n = Math.Min(CdSector.RawSize, data.Length - pos);
            pending.Add((0, data[pos..(pos + n)]));
            edc = CdSector.ComputeEdc(data.AsSpan(pos, n), edc);
            pos += n;
            if (pending.Count > 4096) Flush();
        }
        Flush();
        WriteRecordHeader(output, 0, uint.MaxValue);
        output.Write(BitConverter.GetBytes(edc));
    }

    private static bool Rebuilds(byte[] original, int type)
    {
        var copy = (byte[])original.Clone();
        var s = copy.AsSpan();
        switch (type)
        {
            case 1:
                s[0x810..].Clear();
                CdSector.WriteEdc(s, 0, 0x810, 0x810);
                CdSector.WriteEcc(s, false);
                return s.SequenceEqual(original);
            case 2:
                if (!s.Slice(0x10, 4).SequenceEqual(s.Slice(0x14, 4))) return false;
                s[0x818..].Clear();
                CdSector.WriteEdc(s, 0x10, 0x808, 0x818);
                CdSector.WriteEcc(s, true);
                return s.SequenceEqual(original);
            default:
                if (!s.Slice(0x10, 4).SequenceEqual(s.Slice(0x14, 4))) return false;
                s[0x92C..].Clear();
                CdSector.WriteEdc(s, 0x10, 0x91C, 0x92C);
                return s.SequenceEqual(original);
        }
    }

    private static void WriteRecordHeader(Stream s, int type, uint count)
    {
        var b = (byte)((uint)(type & 3) | ((count & 0x1F) << 2));
        count >>= 5;
        if (count != 0) b |= 0x80;
        s.WriteByte(b);
        while (count != 0)
        {
            b = (byte)(count & 0x7F);
            count >>= 7;
            if (count != 0) b |= 0x80;
            s.WriteByte(b);
        }
    }

    // ---- CSO / ZSO ----

    public static void WriteCso(string isoPath, string csoPath, bool lz4, int blockSize = 2048)
    {
        var iso = File.ReadAllBytes(isoPath);
        var blocks = (iso.Length + blockSize - 1) / blockSize;
        var index = new uint[blocks + 1];
        using var output = File.Create(csoPath);
        var header = new byte[24];
        Encoding.ASCII.GetBytes(lz4 ? "ZISO" : "CISO").CopyTo(header, 0);
        BitConverter.GetBytes(24u).CopyTo(header, 4);
        BitConverter.GetBytes((long)iso.Length).CopyTo(header, 8);
        BitConverter.GetBytes(blockSize).CopyTo(header, 16);
        header[20] = 1;
        header[21] = 0;
        output.Write(header);
        output.Write(new byte[index.Length * 4]);

        for (var i = 0; i < blocks; i++)
        {
            index[i] = (uint)output.Position;
            var block = iso.AsSpan(i * blockSize, Math.Min(blockSize, iso.Length - i * blockSize));
            byte[] packed;
            if (lz4)
            {
                packed = new byte[LZ4Codec.MaximumOutputSize(block.Length)];
                packed = packed[..LZ4Codec.Encode(block, packed)];
            }
            else
            {
                using var ms = new MemoryStream();
                using (var d = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) d.Write(block);
                packed = ms.ToArray();
            }
            if (packed.Length >= block.Length)
            {
                index[i] |= 0x80000000;
                output.Write(block);
                if (block.Length < blockSize) output.Write(new byte[blockSize - block.Length]);
            }
            else
            {
                output.Write(packed);
            }
        }
        index[blocks] = (uint)output.Position;
        output.Position = 24;
        foreach (var v in index) output.Write(BitConverter.GetBytes(v));
    }

    // ---- ISZ ----

    /// <summary>Writes a single-part ISZ using all four block kinds (zeros, raw, zlib, bzip2).</summary>
    public static void WriteIsz(string isoPath, string iszPath, int blockSize = 64 * 1024)
    {
        var iso = File.ReadAllBytes(isoPath);
        const int sectorSize = 2048;
        var blocks = (iso.Length + blockSize - 1) / blockSize;
        const int pointerLength = 3;
        const int headerSize = 64;
        var pointerOffset = headerSize;
        var dataOffset = pointerOffset + blocks * pointerLength;

        var payloads = new List<(int Kind, byte[] Data)>();
        for (var i = 0; i < blocks; i++)
        {
            var block = iso.AsSpan(i * blockSize, Math.Min(blockSize, iso.Length - i * blockSize)).ToArray();
            if (block.All(b => b == 0)) { payloads.Add((0, [])); continue; }
            if (i % 7 == 3) { payloads.Add((1, block)); continue; }
            if (i % 2 == 0)
            {
                using var ms = new MemoryStream();
                using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(block);
                payloads.Add((2, ms.ToArray()));
            }
            else
            {
                using var ms = new MemoryStream();
                using (var bz = BZip2Stream.Create(ms, SharpCompress.Compressors.CompressionMode.Compress, false, leaveOpen: true))
                    bz.Write(block);
                var packed = ms.ToArray();
                packed[0] = packed[1] = packed[2] = 0; // ISZ blanks the "BZh" signature
                payloads.Add((3, packed));
            }
        }

        using var output = File.Create(iszPath);
        using var w = new BinaryWriter(output);
        w.Write("IsZ!"u8);
        w.Write((byte)headerSize);
        w.Write((byte)1);           // version
        w.Write(0x12345678u);       // volume serial
        w.Write((ushort)sectorSize);
        w.Write((uint)(iso.Length / sectorSize));
        w.Write((byte)0);           // no password
        w.Write(0L);                // segment size (single part)
        w.Write((uint)blocks);
        w.Write((uint)blockSize);
        w.Write((byte)pointerLength);
        w.Write((sbyte)0);
        w.Write((uint)pointerOffset);
        w.Write(0u);                // no segment table
        w.Write((uint)dataOffset);
        w.Write((byte)0);
        while (output.Position < headerSize) w.Write((byte)0);

        var table = new byte[blocks * pointerLength];
        for (var i = 0; i < blocks; i++)
        {
            var value = ((ulong)payloads[i].Kind << (pointerLength * 8 - 2)) | (uint)payloads[i].Data.Length;
            for (var b = 0; b < pointerLength; b++) table[i * pointerLength + b] = (byte)(value >> (8 * b));
        }
        IszDecoder.Scramble(table);
        w.Write(table);
        foreach (var p in payloads) w.Write(p.Data);
    }

    // ---- PBP ----

    public sealed record PbpTrack(bool Audio, long StartLba);

    /// <summary>Wraps a raw 2352-byte image in a PS1 EBOOT.PBP (PSISOIMG0000 layout).</summary>
    public static void WritePbp(string binPath, string pbpPath, string discId, IReadOnlyList<PbpTrack> tracks)
    {
        var bin = File.ReadAllBytes(binPath);
        var sectors = bin.Length / CdSector.RawSize;
        const int blockBytes = 16 * CdSector.RawSize;
        const long psar = 0x28;

        using var output = File.Create(pbpPath);
        using var w = new BinaryWriter(output);
        w.Write("\0PBP"u8);
        w.Write(0x00010000u);
        for (var i = 0; i < 7; i++) w.Write((uint)psar); // empty PARAM.SFO, icons, etc.
        w.Write((uint)psar);                             // DATA.PSAR

        var disc = new byte[0x100000];
        Encoding.ASCII.GetBytes("PSISOIMG0000").CopyTo(disc, 0);
        Encoding.ASCII.GetBytes(discId).CopyTo(disc, 0x400);

        // TOC: A0 (first track), A1 (last track), A2 (lead-out), then the tracks.
        var toc = 0x800;
        void Entry(byte control, byte point, long frames)
        {
            var abs = frames + CdSector.LeadInFrames;
            disc[toc] = control;
            disc[toc + 2] = point;
            disc[toc + 7] = Bcd((int)(abs / 4500));
            disc[toc + 8] = Bcd((int)(abs / 75 % 60));
            disc[toc + 9] = Bcd((int)(abs % 75));
            toc += 10;
        }
        void Pointer(byte control, byte point, int value)
        {
            disc[toc] = control;
            disc[toc + 2] = point;
            disc[toc + 7] = Bcd(value);
            toc += 10;
        }
        Pointer(0x41, 0xA0, 1);
        Pointer(tracks[^1].Audio ? (byte)0x01 : (byte)0x41, 0xA1, tracks.Count);
        Entry(0x01, 0xA2, sectors);
        for (var i = 0; i < tracks.Count; i++)
            Entry(tracks[i].Audio ? (byte)0x01 : (byte)0x41, Bcd(i + 1), tracks[i].StartLba);

        var blockList = new List<byte[]>();
        for (var off = 0; off < bin.Length; off += blockBytes)
        {
            var block = new byte[blockBytes];
            Array.Copy(bin, off, block, 0, Math.Min(blockBytes, bin.Length - off));
            using var ms = new MemoryStream();
            using (var d = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) d.Write(block);
            var packed = ms.ToArray();
            blockList.Add(packed.Length < blockBytes ? packed : block);
        }
        uint dataOffset = 0;
        for (var i = 0; i < blockList.Count; i++)
        {
            var e = 0x4000 + i * 32;
            BitConverter.GetBytes(dataOffset).CopyTo(disc, e);
            BitConverter.GetBytes((ushort)blockList[i].Length).CopyTo(disc, e + 4);
            dataOffset += (uint)blockList[i].Length;
        }
        w.Write(disc);
        foreach (var b in blockList) w.Write(b);
    }

    private static byte Bcd(int v) => (byte)((v / 10 << 4) | (v % 10));

    // ---- CloneCD ----

    public static void WriteCcd(string ccdPath, IReadOnlyList<(int Mode, long? Index0, long Index1)> tracks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[CloneCD]").AppendLine("Version=3").AppendLine("[Disc]").AppendLine($"TocEntries={tracks.Count + 3}").AppendLine("Sessions=1");
        for (var i = 0; i < tracks.Count; i++)
        {
            sb.AppendLine($"[TRACK {i + 1}]").AppendLine($"MODE={tracks[i].Mode}");
            if (tracks[i].Index0 is { } i0) sb.AppendLine($"INDEX 0={i0}");
            sb.AppendLine($"INDEX 1={tracks[i].Index1}");
        }
        File.WriteAllText(ccdPath, sb.ToString());
    }

    // ---- Alcohol 120% ----

    public sealed record MdsTrack(int Mode, long StartSector, long Pregap, long Length);

    /// <summary>Writes an .mds for a CD whose .mdf holds <paramref name="sectorSize"/>-byte sectors.</summary>
    public static void WriteMds(string mdsPath, IReadOnlyList<MdsTrack> tracks, int sectorSize, bool dvd = false)
    {
        var trackBlocks = 3 + tracks.Count;
        const int sessionOffset = 0x58;
        const int trackOffset = sessionOffset + 24;
        var extraOffset = trackOffset + trackBlocks * 80;
        var nameOffset = extraOffset + tracks.Count * 8;
        var data = new byte[nameOffset + 16 + 8];

        Encoding.ASCII.GetBytes("MEDIA DESCRIPTOR").CopyTo(data, 0);
        data[0x10] = 1; data[0x11] = 3;
        BitConverter.GetBytes((ushort)(dvd ? 0x10 : 0x00)).CopyTo(data, 0x12);
        BitConverter.GetBytes((ushort)1).CopyTo(data, 0x14);
        BitConverter.GetBytes((uint)sessionOffset).CopyTo(data, 0x50);

        BitConverter.GetBytes(-150).CopyTo(data, sessionOffset);
        BitConverter.GetBytes((int)(tracks[^1].StartSector + tracks[^1].Length)).CopyTo(data, sessionOffset + 4);
        BitConverter.GetBytes((ushort)1).CopyTo(data, sessionOffset + 8);
        data[sessionOffset + 10] = (byte)trackBlocks;
        data[sessionOffset + 11] = 3;
        BitConverter.GetBytes((ushort)1).CopyTo(data, sessionOffset + 12);
        BitConverter.GetBytes((ushort)tracks.Count).CopyTo(data, sessionOffset + 14);
        BitConverter.GetBytes((uint)trackOffset).CopyTo(data, sessionOffset + 20);

        for (var b = 0; b < 3; b++)
        {
            var tb = trackOffset + b * 80;
            data[tb + 2] = 0x14;
            data[tb + 4] = (byte)(0xA0 + b);
        }
        for (var i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            var tb = trackOffset + (3 + i) * 80;
            data[tb] = (byte)(0xA0 | t.Mode);
            data[tb + 1] = (byte)(sectorSize > CdSector.RawSize ? 0x08 : 0x00);
            data[tb + 2] = (byte)(t.Mode == 9 ? 0x10 : 0x14);
            data[tb + 4] = (byte)(i + 1);
            BitConverter.GetBytes((uint)(extraOffset + i * 8)).CopyTo(data, tb + 12);
            BitConverter.GetBytes((ushort)sectorSize).CopyTo(data, tb + 16);
            BitConverter.GetBytes((uint)t.StartSector).CopyTo(data, tb + 36);
            BitConverter.GetBytes((ulong)(t.StartSector * sectorSize)).CopyTo(data, tb + 40);
            BitConverter.GetBytes(1u).CopyTo(data, tb + 48);
            BitConverter.GetBytes((uint)nameOffset).CopyTo(data, tb + 52);
            BitConverter.GetBytes((uint)t.Pregap).CopyTo(data, extraOffset + i * 8);
            BitConverter.GetBytes((uint)t.Length).CopyTo(data, extraOffset + i * 8 + 4);
        }
        BitConverter.GetBytes((uint)(nameOffset + 16)).CopyTo(data, nameOffset);
        Encoding.ASCII.GetBytes("*.mdf").CopyTo(data, nameOffset + 16);
        File.WriteAllBytes(mdsPath, data);
    }
}
