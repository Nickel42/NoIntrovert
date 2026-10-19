using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using LibChdman;
using NoIntrovert.Core;
using NoIntrovert.Core.Archives;
using NoIntrovert.Core.Cd;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Tests;

// ---------------------------------------------------------------------------
// NoIntrovert self-test. Reads discs from test-roms (never writes there),
// builds every supported input format in a scratch folder, and checks that
// each converts to a CHD holding exactly the original data. With --synthetic,
// or when test-roms isn't there, the quick tests run on generated stand-ins
// for the discs they slice (see SyntheticRoms) and the full-disc ones skip.
// ---------------------------------------------------------------------------

var quick = args.Contains("--quick");
var keep = args.Contains("--keep");
var roms = ArgValue("--roms") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-roms"));
var upstream = ArgValue("--upstream") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "libchdman", "build-x64", "Release", "chdman-upstream.exe"));
var scratch = Path.Combine(Path.GetTempPath(), "NoIntrovert-tests", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(scratch);
var synthetic = args.Contains("--synthetic") || !Directory.Exists(roms);
if (synthetic)
{
    roms = Path.Combine(scratch, "synthetic-roms");
    SyntheticRoms.Write(roms);
}

var log = new ConsoleLog();
var failures = new List<string>();
var passes = 0;
var total = Stopwatch.StartNew();

Console.WriteLine($"libchdman {Chdman.Version} · roms {roms} · scratch {scratch}{(quick ? " · quick" : "")}{(synthetic ? " · synthetic discs" : "")}");

string Rom(params string[] parts) => Path.Combine([roms, .. parts]);
var crashBin = Rom("PS1", "Crash Bandicoot (USA)", "Crash Bandicoot (USA).bin");
var roarAudio = Rom("PS1", "Bloody Roar (USA)", "Bloody Roar (USA) (Track 2).bin");
var saturnMode1 = Rom("Saturn", "Golden Axe - The Duel (USA)", "Golden Axe - The Duel (USA) (Track 01).bin");
var pspIso = Rom("PSP", "Corpse Party PSP (English)", "Corpse Party PSP (English).iso");

// ---- Shared fixtures ------------------------------------------------------

// A small two-track PS1-style disc: 3000 Mode 2 sectors, then 300 audio sectors (the first 150 are the pregap).
const int DataSectors = 3000, AudioSectors = 300;
var synthDir = Dir("synth");
var synthBin = Path.Combine(synthDir, "synth.bin");
using (var o = File.Create(synthBin))
{
    CopyRange(crashBin, o, 0, DataSectors * 2352L);
    CopyRange(roarAudio, o, 0, AudioSectors * 2352L);
}
var synthCue = Path.Combine(synthDir, "synth.cue");
File.WriteAllText(synthCue,
    "FILE \"synth.bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00\r\n" +
    $"  TRACK 02 AUDIO\r\n    INDEX 00 {CdSector.FormatMsf(DataSectors - 150)}\r\n    INDEX 01 {CdSector.FormatMsf(DataSectors)}\r\n");

// A 32 MB slice of a PSP ISO, as a small DVD.
var isoDir = Dir("iso");
var isoSlice = Path.Combine(isoDir, "slice.iso");
using (var o = File.Create(isoSlice)) CopyRange(pspIso, o, 0, 32L << 20);

var synthRef = CreateChd(new PreparedDisc("synth", DiscKind.Cd, synthCue, "ref"), Path.Combine(Dir("ref"), "synth.chd"));
var isoRef = CreateChd(new PreparedDisc("slice", DiscKind.Dvd, isoSlice, "ref"), Path.Combine(Dir("ref"), "slice.chd"));
Console.WriteLine($"reference CHDs: synth raw SHA-1 {synthRef[..12]}…, iso slice {isoRef[..12]}…");

// ---- Sector maths ----------------------------------------------------------

Test("EDC/ECC rebuild Mode 1 sectors (Saturn)", () =>
{
    using var fs = File.OpenRead(saturnMode1);
    var sector = new byte[2352];
    for (var i = 0; i < 64; i++)
    {
        fs.Position = (16 + i) * 2352L;
        fs.ReadExactly(sector);
        var copy = (byte[])sector.Clone();
        copy.AsSpan(0x810).Clear();
        CdSector.WriteEdc(copy, 0, 0x810, 0x810);
        CdSector.WriteEcc(copy, false);
        if (!copy.AsSpan().SequenceEqual(sector)) return $"sector {16 + i} rebuilt differently";
    }
    return null;
});

Test("EDC/ECC rebuild Mode 2 sectors (PS1)", () =>
{
    using var fs = File.OpenRead(crashBin);
    var sector = new byte[2352];
    int form1 = 0, form2 = 0;
    for (var i = 16; i < 2000; i++)
    {
        fs.Position = i * 2352L;
        fs.ReadExactly(sector);
        var copy = (byte[])sector.Clone();
        if ((sector[18] & 0x20) == 0)
        {
            copy.AsSpan(0x818).Clear();
            CdSector.WriteEdc(copy, 0x10, 0x808, 0x818);
            CdSector.WriteEcc(copy, true);
            form1++;
        }
        else
        {
            if (BitConverter.ToUInt32(sector, 0x92C) == 0) continue; // EDC is optional in Form 2
            copy.AsSpan(0x92C).Clear();
            CdSector.WriteEdc(copy, 0x10, 0x91C, 0x92C);
            form2++;
        }
        if (!copy.AsSpan().SequenceEqual(sector)) return $"sector {i} rebuilt differently";
    }
    return form1 > 0 ? null : "found no Form 1 sectors";
});

// ---- Format decoders --------------------------------------------------------

Test("ECM decode (Mode 2 + audio)", () =>
{
    var ecm = Path.Combine(Dir("ecm"), "synth.bin.ecm");
    Encoders.WriteEcm(synthBin, ecm);
    var outBin = Path.Combine(Dir("ecm-out"), "synth.bin");
    EcmDecoder.Decode(ecm, outBin, null, CancellationToken.None);
    var ratio = (double)new FileInfo(ecm).Length / new FileInfo(synthBin).Length;
    return SameFile(synthBin, outBin) ?? Note($"ECM {ratio:P0} of the bin");
});

Test("ECM decode (Mode 1)", () =>
{
    var dir = Dir("ecm1");
    var bin = Path.Combine(dir, "mode1.bin");
    using (var o = File.Create(bin)) CopyRange(saturnMode1, o, 0, 2000 * 2352L);
    Encoders.WriteEcm(bin, bin + ".ecm");
    var outBin = Path.Combine(dir, "decoded.bin");
    EcmDecoder.Decode(bin + ".ecm", outBin, null, CancellationToken.None);
    return SameFile(bin, outBin);
});

Test("ECM detects damage", () =>
{
    var ecm = Path.Combine(Dir("ecm"), "synth.bin.ecm");
    var damaged = Path.Combine(Dir("ecm-bad"), "bad.bin.ecm");
    var bytes = File.ReadAllBytes(ecm);
    bytes[bytes.Length / 2] ^= 0x55;
    File.WriteAllBytes(damaged, bytes);
    try { EcmDecoder.Decode(damaged, damaged + ".out", null, CancellationToken.None); return "damaged file decoded without an error"; }
    catch (InvalidDataException) { return null; }
    catch (EndOfStreamException) { return null; }
});

Test("CSO decode", () =>
{
    var cso = Path.Combine(Dir("cso"), "slice.cso");
    Encoders.WriteCso(isoSlice, cso, lz4: false);
    var outIso = Path.Combine(Dir("cso-out"), "slice.iso");
    CsoDecoder.Decode(cso, outIso, null, CancellationToken.None);
    return SameFile(isoSlice, outIso);
});

Test("ZSO decode", () =>
{
    var zso = Path.Combine(Dir("zso"), "slice.zso");
    Encoders.WriteCso(isoSlice, zso, lz4: true);
    var outIso = Path.Combine(Dir("zso-out"), "slice.iso");
    CsoDecoder.Decode(zso, outIso, null, CancellationToken.None);
    return SameFile(isoSlice, outIso);
});

Test("ISZ decode (zeros, raw, zlib, bzip2 blocks)", () =>
{
    var isz = Path.Combine(Dir("isz"), "slice.isz");
    Encoders.WriteIsz(isoSlice, isz);
    var outIso = Path.Combine(Dir("isz-out"), "slice.iso");
    IszDecoder.Decode(isz, outIso, null, CancellationToken.None);
    return SameFile(isoSlice, outIso);
});

Test("PBP rebuild (bin and track layout)", () =>
{
    var pbp = Path.Combine(Dir("pbp"), "EBOOT.PBP");
    Encoders.WritePbp(synthBin, pbp, "_SCUS_94900", [new(false, 0), new(true, DataSectors)]);
    var discs = PbpExtractor.Extract(pbp, Dir("pbp-out"), "synth", null, CancellationToken.None);
    if (discs.Count != 1) return $"{discs.Count} discs";
    var cue = CueSheet.Load(discs[0].CuePath);
    var t2 = cue.Tracks.ElementAt(1);
    if (!t2.IsAudio || t2.Indexes.Last().Frames != DataSectors) return "track 2 is in the wrong place";
    return SameFile(synthBin, discs[0].BinPath) ?? Note($"disc id {discs[0].DiscId}");
});

// ---- Every input format through the Convert job ------------------------------

ConvertTest("cue + bin", synthCue, synthRef);

ConvertTest("cue whose bin is .ecm", () =>
{
    var dir = Dir("in-ecmcue");
    File.Copy(synthCue, Path.Combine(dir, "synth.cue"));
    Encoders.WriteEcm(synthBin, Path.Combine(dir, "synth.bin.ecm"));
    return Path.Combine(dir, "synth.cue");
}, synthRef);

ConvertTest("standalone .bin.ecm with no cue", () =>
{
    var dir = Dir("in-ecm");
    var bin = Path.Combine(dir, "data.bin");
    using (var o = File.Create(bin)) CopyRange(crashBin, o, 0, DataSectors * 2352L);
    Encoders.WriteEcm(bin, bin + ".ecm");
    File.Delete(bin);
    return bin + ".ecm";
}, null);

ConvertTest("CloneCD (.ccd + .img + .sub)", () =>
{
    var dir = Dir("in-ccd");
    File.Copy(synthBin, Path.Combine(dir, "synth.img"));
    File.WriteAllBytes(Path.Combine(dir, "synth.sub"), new byte[96 * 10]);
    Encoders.WriteCcd(Path.Combine(dir, "synth.ccd"), [(2, null, 0), (0, DataSectors - 150, DataSectors)]);
    return Path.Combine(dir, "synth.ccd");
}, synthRef);

ConvertTest("Alcohol .mds/.mdf, 2352-byte sectors", () =>
{
    var dir = Dir("in-mds");
    File.Copy(synthBin, Path.Combine(dir, "synth.mdf"));
    Encoders.WriteMds(Path.Combine(dir, "synth.mds"), [new(0xB, 0, 0, DataSectors), new(0x9, DataSectors, 150, AudioSectors - 150)], 2352);
    return Path.Combine(dir, "synth.mds");
}, synthRef);

ConvertTest("Alcohol .mds/.mdf, 2448-byte sectors with subchannel", () =>
{
    var dir = Dir("in-mds-sub");
    var bin = File.ReadAllBytes(synthBin);
    using (var o = File.Create(Path.Combine(dir, "synth.mdf")))
        for (var i = 0; i < bin.Length / 2352; i++) { o.Write(bin, i * 2352, 2352); o.Write(new byte[96]); }
    Encoders.WriteMds(Path.Combine(dir, "synth.mds"), [new(0xB, 0, 0, DataSectors), new(0x9, DataSectors, 150, AudioSectors - 150)], 2448);
    return Path.Combine(dir, "synth.mds");
}, synthRef);

ConvertTest("Alcohol DVD (.mds medium type DVD)", () =>
{
    var dir = Dir("in-mds-dvd");
    File.Copy(isoSlice, Path.Combine(dir, "slice.mdf"));
    Encoders.WriteMds(Path.Combine(dir, "slice.mds"), [new(0xA, 0, 0, new FileInfo(isoSlice).Length / 2048)], 2048, dvd: true);
    return Path.Combine(dir, "slice.mds");
}, isoRef);

ConvertTest("PBP (PS1 EBOOT)", () =>
{
    var dir = Dir("in-pbp");
    Encoders.WritePbp(synthBin, Path.Combine(dir, "synth.pbp"), "_SCUS_94900", [new(false, 0), new(true, DataSectors)]);
    return Path.Combine(dir, "synth.pbp");
}, synthRef);

ConvertTest("ISO (DVD)", isoSlice, isoRef, s => s.IsoMode = IsoMode.Dvd);

ConvertTest("CSO", () =>
{
    var dir = Dir("in-cso");
    Encoders.WriteCso(isoSlice, Path.Combine(dir, "slice.cso"), lz4: false);
    return Path.Combine(dir, "slice.cso");
}, isoRef);

ConvertTest("ZSO", () =>
{
    var dir = Dir("in-zso");
    Encoders.WriteCso(isoSlice, Path.Combine(dir, "slice.zso"), lz4: true);
    return Path.Combine(dir, "slice.zso");
}, isoRef);

ConvertTest("ISZ", () =>
{
    var dir = Dir("in-isz");
    Encoders.WriteIsz(isoSlice, Path.Combine(dir, "slice.isz"));
    return Path.Combine(dir, "slice.isz");
}, isoRef, s => s.IsoMode = IsoMode.Dvd);

ConvertTest("zip holding cue + bin", () =>
{
    var dir = Dir("in-zip");
    var zip = Path.Combine(dir, "Synth Game (USA).zip");
    using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
    {
        z.CreateEntryFromFile(synthCue, "synth.cue", CompressionLevel.Fastest);
        z.CreateEntryFromFile(synthBin, "synth.bin", CompressionLevel.Fastest);
    }
    return zip;
}, synthRef, expectName: "Synth Game (USA).chd");

ConvertTest("tar.gz holding a folder with cue + bin", () =>
{
    var dir = Dir("in-tgz");
    var tgz = Path.Combine(dir, "synth.tar.gz");
    using (var fs = File.Create(tgz))
    using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
    using (var tar = new TarWriter(gz))
    {
        tar.WriteEntry(synthCue, "disc/synth.cue");
        tar.WriteEntry(synthBin, "disc/synth.bin");
    }
    return tgz;
}, synthRef, expectName: "synth.chd");

ConvertTest("split .iso.001/.002/.003", () =>
{
    var dir = Dir("in-split");
    var bytes = File.ReadAllBytes(isoSlice);
    var third = bytes.Length / 3;
    File.WriteAllBytes(Path.Combine(dir, "slice.iso.001"), bytes[..third]);
    File.WriteAllBytes(Path.Combine(dir, "slice.iso.002"), bytes[third..(2 * third)]);
    File.WriteAllBytes(Path.Combine(dir, "slice.iso.003"), bytes[(2 * third)..]);
    return Path.Combine(dir, "slice.iso.001");
}, isoRef, s => s.IsoMode = IsoMode.Dvd);

ConvertTest("loose .bin with no cue", () =>
{
    var dir = Dir("in-loose");
    var bin = Path.Combine(dir, "loose.bin");
    using (var o = File.Create(bin)) CopyRange(crashBin, o, 0, DataSectors * 2352L);
    return bin;
}, null);

// ---- Behaviour around conversions -------------------------------------------

Test("ISO Auto mode: PSP image is a DVD, small Mode 1 data is a CD", () =>
{
    var preparer = new InputPreparer(scratch, IsoMode.Auto, log);
    using (var p = preparer.Prepare(pspIso, null, CancellationToken.None))
        if (p.Discs[0].Kind != DiscKind.Dvd) return "PSP ISO was not treated as a DVD";
    var cdIso = Path.Combine(Dir("iso-cd"), "pc.iso");
    using (var o = File.Create(cdIso))
    {
        var sector = new byte[2352];
        using var src = File.OpenRead(saturnMode1);
        for (var i = 0; i < 2000; i++) { src.ReadExactly(sector); o.Write(sector, 16, 2048); }
    }
    using (var p = preparer.Prepare(cdIso, null, CancellationToken.None))
        if (p.Discs[0].Kind != DiscKind.Cd) return "small Mode 1 ISO was not treated as a CD";
    return null;
});

Test("Folder scan picks descriptors, not the files they use", () =>
{
    var dir = Dir("scan");
    foreach (var f in new[] { "a.cue", "a (Track 1).bin", "a (Track 2).bin", "b.ccd", "b.img", "b.sub", "c.mds", "c.mdf",
                 "d.iso.001", "d.iso.002", "e.cso", "f.zip", "g.part1.rar", "g.part2.rar", "notes.txt" })
        File.WriteAllText(Path.Combine(dir, f), "");
    File.WriteAllText(Path.Combine(dir, "a.cue"),
        "FILE \"a (Track 1).bin\" BINARY\n TRACK 01 MODE2/2352\n  INDEX 01 00:00:00\nFILE \"a (Track 2).bin\" BINARY\n TRACK 02 AUDIO\n  INDEX 01 00:00:00\n");
    var picked = DiscFiles.SelectPrimaries(Directory.EnumerateFiles(dir)).Select(Path.GetFileName).ToList();
    var expected = new[] { "a.cue", "b.ccd", "c.mds", "d.iso.001", "e.cso", "f.zip", "g.part1.rar" };
    return picked.SequenceEqual(expected) ? null : $"picked {string.Join(", ", picked)}";
});

Test("Existing CHD is skipped unless Overwrite is on", () =>
{
    var dir = Dir("overwrite");
    var outDir = Dir("overwrite-out");
    File.Copy(synthCue, Path.Combine(dir, "synth.cue"));
    File.Copy(synthBin, Path.Combine(dir, "synth.bin"));
    File.WriteAllText(Path.Combine(outDir, "synth.chd"), "placeholder");
    var o = RunJob(c => ChdJobs.Convert(c, Path.Combine(dir, "synth.cue")), s => s.OutputFolder = outDir);
    if (o.Status != JobStatus.Skipped) return $"expected Skipped, got {o.Status}";
    o = RunJob(c => ChdJobs.Convert(c, Path.Combine(dir, "synth.cue")), s => { s.OutputFolder = outDir; s.Overwrite = true; });
    return o.Status == JobStatus.Succeeded && RawSha1(Path.Combine(outDir, "synth.chd")) == synthRef ? null : $"overwrite gave {o.Status}: {o.Summary}";
});

Test("Remove originals after a verified conversion", () =>
{
    var dir = Dir("delete");
    var cue = Path.Combine(dir, "synth.cue");
    File.Copy(synthCue, cue);
    File.Copy(synthBin, Path.Combine(dir, "synth.bin"));
    var o = RunJob(c => ChdJobs.Convert(c, cue), s => { s.DeleteOriginals = true; s.UseRecycleBin = false; s.VerifyAfterCreate = false; });
    if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
    var left = Directory.GetFiles(dir).Select(Path.GetFileName).ToList();
    return left.SequenceEqual(["synth.chd"]) ? null : $"folder holds {string.Join(", ", left)}";
});

if (OperatingSystem.IsLinux())
    Test("Remove to the Trash (freedesktop.org, under a scratch XDG_DATA_HOME)", () =>
    {
        var dir = Dir("trash");
        var xdg = Dir("trash-xdg");
        var saved = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdg);
        try
        {
            var file = Path.Combine(dir, "a b%.bin");
            var folder = Path.Combine(dir, "Game");
            File.WriteAllText(file, "x");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "game.cue"), "y");
            FileRemover.Remove([file, folder], useBin: true, log);
            File.WriteAllText(file, "z"); // a second file of the same name
            FileRemover.Remove([file], useBin: true, log);
            var trash = Path.Combine(xdg, "Trash");
            if (File.Exists(file) || Directory.Exists(folder)) return "originals still there";
            var files = Directory.GetFileSystemEntries(Path.Combine(trash, "files")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
            if (!files.SequenceEqual(["Game", "a b% 2.bin", "a b%.bin"])) return $"trash holds {string.Join(", ", files)}";
            var info = File.ReadAllText(Path.Combine(trash, "info", "a b%.bin.trashinfo"));
            var expected = "Path=" + file.Replace("%", "%25").Replace(" ", "%20");
            return info.StartsWith("[Trash Info]\n", StringComparison.Ordinal) && info.Contains(expected + "\n") ? null : $"trashinfo: {info}";
        }
        finally { Environment.SetEnvironmentVariable("XDG_DATA_HOME", saved); }
    });

Test("Verify flags a damaged CHD", () =>
{
    var damaged = Path.Combine(Dir("damaged"), "synth.chd");
    File.Copy(Path.Combine(scratch, "ref", "synth.chd"), damaged);
    var bytes = File.ReadAllBytes(damaged);
    for (var i = bytes.Length / 2; i < bytes.Length / 2 + 64; i++) bytes[i] ^= 0xFF;
    File.WriteAllBytes(damaged, bytes);
    var o = RunJob(c => ChdJobs.Verify(c, damaged));
    return o.Status == JobStatus.Failed ? Note(o.Summary) : $"expected Failed, got {o.Status}: {o.Summary}";
});

Test("Verify passes a good CHD", () =>
{
    var o = RunJob(c => ChdJobs.Verify(c, Path.Combine(scratch, "ref", "synth.chd")));
    return o.Status == JobStatus.Succeeded ? null : $"{o.Status}: {o.Summary}";
});

Test("Extract cue/bin (one bin per track) gives back the disc", () =>
{
    var outDir = Dir("extract-split");
    var o = RunJob(c => ChdJobs.Extract(c, Path.Combine(scratch, "ref", "synth.chd")),
        s => { s.OutputFolder = outDir; s.ExtractFormat = ExtractFormat.CueBin; s.SplitBin = true; });
    if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
    var bins = DiscFiles.CueReferences(Path.Combine(outDir, "synth.cue")).Select(n => Path.Combine(outDir, n)).ToList();
    if (bins.Count != 2) return $"{bins.Count} bins written";
    var joined = Path.Combine(outDir, "joined.bin");
    using (var j = File.Create(joined)) foreach (var b in bins) using (var f = File.OpenRead(b)) f.CopyTo(j);
    return SameFile(synthBin, joined);
});

Test("Extract a Mode 1 CD to ISO", () =>
{
    var dir = Dir("cd-iso");
    var bin = Path.Combine(dir, "m1.bin");
    using (var o = File.Create(bin)) CopyRange(saturnMode1, o, 0, 2000 * 2352L);
    File.WriteAllText(Path.Combine(dir, "m1.cue"), "FILE \"m1.bin\" BINARY\r\n  TRACK 01 MODE1/2352\r\n    INDEX 01 00:00:00\r\n");
    CreateChd(new PreparedDisc("m1", DiscKind.Cd, Path.Combine(dir, "m1.cue"), ""), Path.Combine(dir, "m1.chd"));
    var outDir = Dir("cd-iso-out");
    var r = RunJob(c => ChdJobs.Extract(c, Path.Combine(dir, "m1.chd")), s => { s.OutputFolder = outDir; s.ExtractFormat = ExtractFormat.Iso; });
    if (r.Status != JobStatus.Succeeded) return $"{r.Status}: {r.Summary}";
    var expected = Path.Combine(dir, "expected.iso");
    using (var o = File.Create(expected))
    using (var src = File.OpenRead(bin))
    {
        var sector = new byte[2352];
        while (src.Read(sector) == 2352) o.Write(sector, 16, 2048);
    }
    return SameFile(expected, Path.Combine(outDir, "m1.iso"));
});

Test("ISO extraction refuses a disc with audio", () =>
{
    var o = RunJob(c => ChdJobs.Extract(c, Path.Combine(scratch, "ref", "synth.chd")),
        s => { s.OutputFolder = Dir("extract-iso-refuse"); s.ExtractFormat = ExtractFormat.Iso; });
    return o.Status == JobStatus.Failed ? Note(o.Summary) : $"expected Failed, got {o.Status}";
});

Test("Extract a DVD CHD to ISO", () =>
{
    var outDir = Dir("extract-dvd");
    var o = RunJob(c => ChdJobs.Extract(c, Path.Combine(scratch, "ref", "slice.chd")), s => s.OutputFolder = outDir);
    return o.Status == JobStatus.Succeeded ? SameFile(isoSlice, Path.Combine(outDir, "slice.iso")) : $"{o.Status}: {o.Summary}";
});

Test("Info reports media and track metadata", () =>
{
    using var chdman = new Chdman();
    var info = chdman.Info(Path.Combine(scratch, "ref", "synth.chd"), hunkStats: true);
    var tracks = info.Metadata.Count(m => m.TagString.StartsWith("CHT", StringComparison.Ordinal));
    if (info.MediaType != "cd") return $"media type {info.MediaType}";
    if (tracks != 2) return $"{tracks} track metadata entries";
    if (info.HunkStats.Count == 0) return "no hunk stats";
    return Note($"{info.Compression}; {info.Metadata[0].Text}");
});

Test("Cancelling stops a conversion and leaves no CHD", () =>
{
    var outDir = Dir("cancel");
    using var cts = new CancellationTokenSource();
    var report = new CancelAtProgress(cts, 0.2);
    var o = RunJob(c => ChdJobs.Convert(c, isoSlice), s => { s.OutputFolder = outDir; s.IsoMode = IsoMode.Dvd; s.VerifyAfterCreate = false; }, cts.Token, report);
    var left = Directory.GetFiles(outDir);
    return o.Status == JobStatus.Cancelled && left.Length == 0 ? null : $"{o.Status}, {left.Length} file(s) left";
});

// ---- Archives (7z / zip through bit7z + 7-Zip) ---------------------------------

var sevenZipExe = new[] { @"C:\Program Files\7-Zip\7z.exe", "/opt/homebrew/bin/7zz", "/usr/local/bin/7zz", "/usr/bin/7zz", "/usr/bin/7z" }.FirstOrDefault(File.Exists);
var archiveSrc = Dir("archive-src");
File.Copy(synthBin, Path.Combine(archiveSrc, "disc.bin"));
using (var o = File.Create(Path.Combine(archiveSrc, "data.iso"))) CopyRange(isoSlice, o, 0, 8L << 20);
File.WriteAllText(Path.Combine(archiveSrc, "notes.txt"), string.Concat(Enumerable.Repeat("NoIntrovert archive test line\n", 2000)));
File.WriteAllBytes(Path.Combine(archiveSrc, "empty.dat"), []);
Directory.CreateDirectory(Path.Combine(archiveSrc, "Sub Folder"));
File.WriteAllText(Path.Combine(archiveSrc, "Sub Folder", "日本語 é.txt"), "unicode name");

if (!ArchiveEngine.IsAvailable(out var archiveProblem))
{
    Test("Archive engine loads", () => archiveProblem);
}
else
{
    foreach (var format in new[] { ArchiveFormat.SevenZip, ArchiveFormat.Zip })
    {
        var ext = ArchiveJobs.Extension(format);
        Test($"Archive {ext}: compress a folder, test, extract, compare", () =>
        {
            var dir = Dir("arc-" + ext.TrimStart('.'));
            var archive = Path.Combine(dir, "src" + ext);
            using var engine = new ArchiveEngine();
            double last = 0;
            engine.Compress(format, ArchiveLevel.Normal, 0, solid: true, [archiveSrc], archive, contentsOnly: false, f => last = f, CancellationToken.None);
            engine.Test(archive, null, CancellationToken.None);
            var outDir = Path.Combine(dir, "out");
            engine.Extract(archive, outDir, null, CancellationToken.None);
            var problem = SameTree(archiveSrc, Path.Combine(outDir, "archive-src"));
            var ratio = (double)new FileInfo(archive).Length / ArchiveJobs.SizeOf(archiveSrc);
            return problem ?? Note($"{ratio:P1} of the input; progress reached {last:P0}");
        });

        if (sevenZipExe is not null)
            Test($"Archive {ext}: 7-Zip itself accepts what we wrote", () =>
            {
                var archive = Path.Combine(scratch, "arc-" + ext.TrimStart('.'), "src" + ext);
                var psi = new ProcessStartInfo(sevenZipExe, ["t", archive]) { RedirectStandardOutput = true, UseShellExecute = false };
                using var p = Process.Start(psi)!;
                var text = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode == 0 && text.Contains("Everything is Ok") ? Note(Path.GetFileName(sevenZipExe)) : $"7-Zip exit {p.ExitCode}";
            });
    }

    Test("Archive: convert zip → 7z → zip keeps every file", () =>
    {
        var dir = Dir("arc-convert");
        using (var engine = new ArchiveEngine())
            engine.Compress(ArchiveFormat.Zip, ArchiveLevel.Fast, 0, false, [archiveSrc], Path.Combine(dir, "set.zip"), true, null, CancellationToken.None);
        var o = RunJob(c => ArchiveJobs.Convert(c, Path.Combine(dir, "set.zip")), s => s.ArchiveConvertTarget = ConvertTarget.Swap);
        if (o.Status != JobStatus.Succeeded || !File.Exists(Path.Combine(dir, "set.7z"))) return $"zip→7z: {o.Status} {o.Summary}";
        File.Delete(Path.Combine(dir, "set.zip"));
        o = RunJob(c => ArchiveJobs.Convert(c, Path.Combine(dir, "set.7z")), s => s.ArchiveConvertTarget = ConvertTarget.Swap);
        if (o.Status != JobStatus.Succeeded || !File.Exists(Path.Combine(dir, "set.zip"))) return $"7z→zip: {o.Status} {o.Summary}";
        using (var engine = new ArchiveEngine())
            engine.Extract(Path.Combine(dir, "set.zip"), Path.Combine(dir, "out"), null, CancellationToken.None);
        return SameTree(archiveSrc, Path.Combine(dir, "out"));
    });

    Test("Archive: converting to the format it already has is skipped", () =>
    {
        var o = RunJob(c => ArchiveJobs.Convert(c, Path.Combine(scratch, "arc-convert", "set.zip")), s => s.ArchiveConvertTarget = ConvertTarget.ToZip);
        return o.Status == JobStatus.Skipped ? null : $"{o.Status}: {o.Summary}";
    });

    Test("Archive: one archive per file or subfolder in a folder", () =>
    {
        var outDir = Dir("arc-entries");
        var o = RunJob(c => ArchiveJobs.CompressEntries(c, archiveSrc, outDir), s => s.ArchiveFormat = ArchiveFormat.SevenZip);
        var made = Directory.GetFiles(outDir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var expected = new[] { "Sub Folder.7z", "data.7z", "disc.7z", "empty.7z", "notes.7z" }.OrderBy(n => n, StringComparer.Ordinal);
        return o.Status == JobStatus.Succeeded && made.SequenceEqual(expected) ? Note(o.Summary) : $"{o.Status} {o.Summary}: {string.Join(", ", made)}";
    });

    Test("Archive: several items into one archive", () =>
    {
        var outDir = Dir("arc-single");
        var o = RunJob(c => ArchiveJobs.CompressOne(c, [Path.Combine(archiveSrc, "notes.txt"), Path.Combine(archiveSrc, "Sub Folder")], "Combined", outDir));
        if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
        using var engine = new ArchiveEngine();
        engine.Extract(Path.Combine(outDir, "Combined.7z"), Path.Combine(outDir, "x"), null, CancellationToken.None);
        return File.Exists(Path.Combine(outDir, "x", "notes.txt")) && File.Exists(Path.Combine(outDir, "x", "Sub Folder", "日本語 é.txt")) ? null : "missing entries";
    });

    Test("Archive: name clashes keep the extension (game.bin / game.cue)", () =>
    {
        var names = ArchiveJobs.UniqueNames(["a/game.bin", "a/game.cue", "a/other.iso"]);
        return names.SequenceEqual(["game.bin", "game.cue", "other"]) ? null : string.Join(", ", names);
    });

    Test("Archive: remove originals after the archive tests OK", () =>
    {
        var dir = Dir("arc-delete");
        var folder = Path.Combine(dir, "Game Folder");
        Directory.CreateDirectory(folder);
        File.Copy(synthBin, Path.Combine(folder, "game.bin"));
        var o = RunJob(c => ArchiveJobs.CompressOne(c, [folder], "Game Folder", dir), s => { s.ArchiveDeleteOriginals = true; s.UseRecycleBin = false; s.ArchiveTestAfter = false; });
        return o.Status == JobStatus.Succeeded && !Directory.Exists(folder) && File.Exists(Path.Combine(dir, "Game Folder.7z"))
            ? null : $"{o.Status} {o.Summary}; folder still there: {Directory.Exists(folder)}";
    });

    Test("Archive: a damaged archive fails its test", () =>
    {
        var damaged = Path.Combine(Dir("arc-damaged"), "bad.7z");
        File.Copy(Path.Combine(scratch, "arc-7z", "src.7z"), damaged);
        var bytes = File.ReadAllBytes(damaged);
        for (var i = bytes.Length / 2; i < bytes.Length / 2 + 256; i++) bytes[i] ^= 0xA5;
        File.WriteAllBytes(damaged, bytes);
        using var engine = new ArchiveEngine();
        try { engine.Test(damaged, null, CancellationToken.None); return "damaged archive passed its test"; }
        catch (ArchiveException ex) { return Note(ex.Message.Split('\n')[0]); }
    });

    Test("Archive: cancelling leaves no partial archive", () =>
    {
        var outDir = Dir("arc-cancel");
        using var cts = new CancellationTokenSource();
        var report = new CancelAtProgress(cts, 0.1);
        var o = RunJob(c => ArchiveJobs.CompressOne(c, [isoSlice], "slice", outDir),
            s => { s.ArchiveLevel = ArchiveLevel.Ultra; s.ThreadsPerJob = 1; }, cts.Token, report);
        var left = Directory.GetFiles(outDir);
        return o.Status == JobStatus.Cancelled && left.Length == 0 ? null : $"{o.Status} {o.Summary}, {left.Length} file(s) left";
    });

    // Extract jobs read the src.7z / src.zip written by the round-trip tests above.
    var src7z = Path.Combine(scratch, "arc-7z", "src.7z");
    var srcZip = Path.Combine(scratch, "arc-zip", "src.zip");

    Test("Extract: a 7z into its own folder", () =>
    {
        var outDir = Dir("ext-own");
        var o = RunJob(c => ArchiveJobs.Extract(c, src7z), s => { s.OutputFolder = outDir; s.ArchiveExtractToOwnFolder = true; });
        if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
        var stray = Directory.GetDirectories(outDir).Where(d => Path.GetFileName(d).StartsWith(".noi-extract-")).ToList();
        return stray.Count > 0 ? "staging folder left behind" : SameTree(archiveSrc, Path.Combine(outDir, "src", "archive-src")) ?? Note(o.Summary);
    });

    Test("Extract: a zip straight into the output folder", () =>
    {
        var outDir = Dir("ext-flat");
        var o = RunJob(c => ArchiveJobs.Extract(c, srcZip), s => { s.OutputFolder = outDir; s.ArchiveExtractToOwnFolder = false; });
        return o.Status == JobStatus.Succeeded ? SameTree(archiveSrc, Path.Combine(outDir, "archive-src")) : $"{o.Status}: {o.Summary}";
    });

    Test("Extract: an existing folder is skipped unless Overwrite is on", () =>
    {
        var outDir = Dir("ext-own");
        var o = RunJob(c => ArchiveJobs.Extract(c, src7z), s => { s.OutputFolder = outDir; s.ArchiveExtractToOwnFolder = true; });
        if (o.Status != JobStatus.Skipped) return $"expected Skipped, got {o.Status}";
        o = RunJob(c => ArchiveJobs.Extract(c, src7z), s => { s.OutputFolder = outDir; s.ArchiveExtractToOwnFolder = true; s.Overwrite = true; });
        return o.Status == JobStatus.Succeeded ? null : $"with Overwrite: {o.Status} {o.Summary}";
    });

    Test("Extract: existing files are kept, or replaced with Overwrite", () =>
    {
        var outDir = Dir("ext-conflict");
        var existing = Path.Combine(outDir, "archive-src", "notes.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllText(existing, "mine");
        var o = RunJob(c => ArchiveJobs.Extract(c, src7z), s => { s.OutputFolder = outDir; s.ArchiveExtractToOwnFolder = false; });
        if (o.Status != JobStatus.Warning || File.ReadAllText(existing) != "mine") return $"without Overwrite: {o.Status} {o.Summary}";
        if (!File.Exists(Path.Combine(outDir, "archive-src", "disc.bin"))) return "files that didn't clash weren't extracted";
        o = RunJob(c => ArchiveJobs.Extract(c, src7z), s => { s.OutputFolder = outDir; s.ArchiveExtractToOwnFolder = false; s.Overwrite = true; });
        if (o.Status != JobStatus.Succeeded) return $"with Overwrite: {o.Status} {o.Summary}";
        return SameTree(archiveSrc, Path.Combine(outDir, "archive-src"));
    });

    Test("Extract: remove the archive afterwards", () =>
    {
        var dir = Dir("ext-delete");
        var archive = Path.Combine(dir, "copy.7z");
        File.Copy(src7z, archive);
        var o = RunJob(c => ArchiveJobs.Extract(c, archive), s => { s.ArchiveDeleteOriginals = true; s.UseRecycleBin = false; });
        return o.Status == JobStatus.Succeeded && !File.Exists(archive) && Directory.Exists(Path.Combine(dir, "copy"))
            ? null : $"{o.Status} {o.Summary}; archive still there: {File.Exists(archive)}";
    });

    Test("Extract: a damaged archive fails and leaves nothing behind", () =>
    {
        var dir = Dir("ext-damaged");
        var damaged = Path.Combine(dir, "bad.7z");
        File.Copy(Path.Combine(scratch, "arc-damaged", "bad.7z"), damaged);
        var o = RunJob(c => ArchiveJobs.Extract(c, damaged), s => { s.ArchiveDeleteOriginals = true; s.UseRecycleBin = false; });
        var leftovers = Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Where(n => n != "bad.7z").ToList();
        return o.Status == JobStatus.Failed && File.Exists(damaged) && leftovers.Count == 0
            ? Note(o.Summary.Split('\n')[0])
            : $"{o.Status} {o.Summary}; archive kept: {File.Exists(damaged)}; left: {string.Join(", ", leftovers)}";
    });
}

// ---- Library tests tab -------------------------------------------------------

if (File.Exists(upstream))
{
    foreach (var (label, path, mode) in new[] { ("cue/bin", synthCue, IsoMode.Auto), ("iso as DVD", isoSlice, IsoMode.Dvd) })
        Test($"Compare with upstream chdman ({label})", () =>
        {
            var records = new List<TestRecord>();
            var o = RunJob(c => TestJobs.Run(c, path, TestKind.CompareWithChdman, records.Add), s => { s.ChdmanExePath = upstream; s.IsoMode = mode; });
            return o.Status == JobStatus.Succeeded ? Note(records[0].Notes) : $"{o.Summary}: {string.Join("; ", records.Select(r => r.Notes))}";
        });
}
else
{
    Console.WriteLine($"  (skipping chdman comparison: {upstream} not found)");
}

Test("Round trip test job (cue/bin)", () =>
{
    var records = new List<TestRecord>();
    var o = RunJob(c => TestJobs.Run(c, synthCue, TestKind.RoundTrip, records.Add));
    return o.Status == JobStatus.Succeeded ? Note(records[0].Notes) : string.Join("; ", records.Select(r => r.Notes));
});

// ---- Full discs from test-roms ------------------------------------------------

if (!quick)
{
    var discs = new (string Label, string Path)[]
    {
        ("PS1 single bin", Rom("PS1", "Crash Bandicoot (USA)", "Crash Bandicoot (USA).cue")),
        ("PS1 two bins", Rom("PS1", "Bloody Roar (USA)", "Bloody Roar (USA).cue")),
        ("Saturn 25 tracks", Rom("Saturn", "Golden Axe - The Duel (USA)", "Golden Axe - The Duel (USA).cue")),
        ("Dreamcast gdi", Rom("Dreamcast", "Legacy of Kain - Soul Reaver", "Legacy of Kain - Soul Reaver v1.000 (1999)(Eidos)(US)[!].gdi")),
        ("PSP iso (Auto → DVD)", pspIso),
    };
    foreach (var (label, path) in discs)
    {
        if (!File.Exists(path)) { Console.WriteLine($"  (skipping {label}: not found)"); continue; }
        Test($"Round trip, full disc: {label}", () =>
        {
            var records = new List<TestRecord>();
            var o = RunJob(c => TestJobs.Run(c, path, TestKind.RoundTrip, records.Add), s => s.ParallelJobs = 1);
            return o.Status == JobStatus.Succeeded ? Note(records[0].Notes + $"; {records[0].LibMbps:F0} MB/s") : string.Join("; ", records.Select(r => r.Notes));
        });
    }

    if (File.Exists(upstream))
        Test("Compare with upstream chdman, full disc (Saturn 25 tracks)", () =>
        {
            var records = new List<TestRecord>();
            var o = RunJob(c => TestJobs.Run(c, discs[2].Path, TestKind.CompareWithChdman, records.Add), s => s.ChdmanExePath = upstream);
            return o.Status == JobStatus.Succeeded ? Note(records[0].Notes) : string.Join("; ", records.Select(r => r.Notes));
        });
}

// ---- Summary -------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine($"{passes} passed, {failures.Count} failed in {total.Elapsed:mm\\:ss}");
foreach (var f in failures) Console.WriteLine($"  FAILED: {f}");
if (failures.Count == 0 && !keep)
{
    try { Directory.Delete(scratch, true); } catch (IOException) { }
}
else
{
    Console.WriteLine($"Test files kept in {scratch}");
}
return failures.Count == 0 ? 0 : 1;

// ---- Helpers ---------------------------------------------------------------------

string? ArgValue(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

string Dir(string name)
{
    var d = Path.Combine(scratch, name);
    Directory.CreateDirectory(d);
    return d;
}

void Test(string name, Func<string?> body)
{
    var sw = Stopwatch.StartNew();
    string? problem;
    try { problem = body(); }
    catch (Exception ex) { problem = $"{ex.GetType().Name}: {ex.Message}"; }
    sw.Stop();
    var note = problem is not null && problem.StartsWith('\u0001') ? problem[1..] : null;
    if (problem is null || note is not null)
    {
        passes++;
        Console.WriteLine($"PASS  {name} ({sw.Elapsed.TotalSeconds:F1}s){(note is null ? "" : " — " + note)}");
    }
    else
    {
        failures.Add($"{name}: {problem}");
        Console.WriteLine($"FAIL  {name} ({sw.Elapsed.TotalSeconds:F1}s) — {problem}");
    }
}

static string Note(string text) => "\u0001" + text;

void ConvertTest(string name, object input, string? expectedRawSha1, Action<AppSettings>? configure = null, string? expectName = null)
{
    Test($"Convert {name}", () =>
    {
        var source = input switch
        {
            string s => s,
            Func<string> make => make(),
            _ => throw new ArgumentException("bad input"),
        };
        var outDir = Dir("out-" + Guid.NewGuid().ToString("N")[..6]);
        var outcome = RunJob(c => ChdJobs.Convert(c, source), s => { s.OutputFolder = outDir; configure?.Invoke(s); });
        if (outcome.Status != JobStatus.Succeeded) return $"{outcome.Status}: {outcome.Summary}";
        var chds = Directory.GetFiles(outDir, "*.chd");
        if (chds.Length != 1) return $"{chds.Length} CHDs written";
        if (expectName is not null && Path.GetFileName(chds[0]) != expectName) return $"named {Path.GetFileName(chds[0])}";
        if (expectedRawSha1 is null) return Note(outcome.Summary);
        var sha = RawSha1(chds[0]);
        return sha == expectedRawSha1 ? Note(outcome.Summary) : $"data SHA-1 {sha} ≠ reference {expectedRawSha1}";
    });
}

JobOutcome RunJob(JobWork work, Action<AppSettings>? configure = null, CancellationToken token = default, IJobReport? report = null)
{
    var settings = new AppSettings { TempFolder = Path.Combine(scratch, "_temp"), VerifyAfterCreate = true, ParallelJobs = 1 };
    configure?.Invoke(settings);
    JobOutcome? result = null;
    var job = new BatchJob("test", report ?? new NullReport(), work, _ => { }, o => result = o);
    BatchRunner.RunAsync([job], settings, log, token).GetAwaiter().GetResult();
    return result ?? new JobOutcome(JobStatus.Failed, "no result");
}

string CreateChd(PreparedDisc disc, string output)
{
    using var chdman = new Chdman();
    ChdService.Create(chdman, disc, output, new ChdOutputSettings { Force = true }, CancellationToken.None);
    return chdman.Info(output).RawSha1;
}

static string RawSha1(string chd)
{
    using var chdman = new Chdman();
    return chdman.Info(chd).RawSha1;
}

static string? SameFile(string expected, string actual)
{
    var a = new FileInfo(expected).Length;
    var b = new FileInfo(actual).Length;
    if (a != b) return $"length {b:N0}, expected {a:N0}";
    var at = TestJobs.FirstDifference(expected, actual, CancellationToken.None);
    return at < 0 ? null : $"differs at byte {at:N0}";
}

/// <summary>Compares two folders: the same relative file names, each with the same bytes.</summary>
static string? SameTree(string expected, string actual)
{
    if (!Directory.Exists(actual)) return $"{actual} does not exist";
    static List<string> Files(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(f => Path.GetRelativePath(root, f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
    var a = Files(expected);
    var b = Files(actual);
    if (!a.SequenceEqual(b)) return $"file lists differ: [{string.Join(", ", a)}] vs [{string.Join(", ", b)}]";
    foreach (var f in a)
        if (SameFile(Path.Combine(expected, f), Path.Combine(actual, f)) is { } p) return $"{f}: {p}";
    return null;
}

static void CopyRange(string source, Stream destination, long offset, long count)
{
    using var src = File.OpenRead(source);
    src.Position = offset;
    var buffer = new byte[1 << 20];
    while (count > 0)
    {
        var n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
        if (n <= 0) throw new EndOfStreamException($"{source} is shorter than expected");
        destination.Write(buffer, 0, n);
        count -= n;
    }
}

sealed class ConsoleLog : ILog
{
    public void Write(LogLevel level, string message)
    {
        if (level is LogLevel.Warning or LogLevel.Error)
            Console.WriteLine($"      [{level}] {message}");
    }
}

sealed class NullReport : IJobReport
{
    public void SetStage(string stage) { }
    public void SetProgress(double fraction) { }
}

/// <summary>Cancels the batch once compression passes a given fraction.</summary>
sealed class CancelAtProgress(CancellationTokenSource cts, double at) : IJobReport
{
    private string _stage = "";
    public void SetStage(string stage) => _stage = stage;
    public void SetProgress(double fraction)
    {
        if (_stage.StartsWith("Compressing", StringComparison.Ordinal) && fraction >= at) cts.Cancel();
    }
}
