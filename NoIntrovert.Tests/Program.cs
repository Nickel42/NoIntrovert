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
var roms = ArgValue("--roms") ?? Environment.GetEnvironmentVariable("NOINTROVERT_TEST_ROMS") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-roms"));
var upstream = ArgValue("--upstream") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "native", "libchdman", "build-x64", "Release", "chdman-upstream.exe"));
var scratch = Path.Combine(Path.GetTempPath(), "NoIntrovert-tests", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(scratch);
var synthetic = args.Contains("--synthetic") || !Directory.Exists(roms);
if (synthetic)
{
    roms = Path.Combine(scratch, "synthetic-roms");
    SyntheticRoms.Write(roms);
}

// Nothing here may touch the real settings.json in %AppData%\NoIntrovert.
AppSettings.SettingsDirectoryOverride = Path.Combine(scratch, "settings");

var log = new ConsoleLog();
var failures = new List<string>();
var passes = 0;
var total = Stopwatch.StartNew();
if (ArgValue("--wua-sample") is { } wuaSampleDir) return WriteWuaSample(Path.GetFullPath(wuaSampleDir));
// --unpack-zar ARCHIVE DIR: checks an archive's SHA-256 and unpacks it with NoIntrovert's reader (CI reads the official tool's output with this).
if (Array.IndexOf(args, "--unpack-zar") is var zi and >= 0 && zi + 2 < args.Length)
{
    using (var zr = NoIntrovert.Core.WiiU.ZArchiveReader.Open(args[zi + 1]))
        if (!zr.CheckIntegrity()) { Console.WriteLine($"FAIL  {args[zi + 1]}: SHA-256 doesn't match"); return 1; }
    WiiUSample.Unpack(args[zi + 1], args[zi + 2]);
    Console.WriteLine($"unpacked {args[zi + 1]} to {args[zi + 2]}");
    return 0;
}

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
    var expected = new[] { "a.cue", "b.ccd", "c.mds", "d.iso.001", "e.cso", "f.zip" };
    if (!picked.SequenceEqual(expected)) return $"picked {string.Join(", ", picked)}";
    // The same folder given with forward slashes (command line, some drag sources).
    var slashed = DiscFiles.SelectPrimaries(Directory.EnumerateFiles(dir.Replace('\\', '/'))).Select(Path.GetFileName).ToList();
    return slashed.SequenceEqual(expected) ? null : $"with / separators picked {string.Join(", ", slashed)}";
});

Test("Format registry: what each path is", () =>
{
    var dir = Dir("registry-detect");
    var title = Path.Combine(dir, "0005000010101a00_v0");
    Directory.CreateDirectory(Path.Combine(title, "meta"));
    File.WriteAllText(Path.Combine(title, "meta", "meta.xml"), "<menu/>");
    var nus = Path.Combine(dir, "nus");
    Directory.CreateDirectory(nus);
    File.WriteAllText(Path.Combine(nus, "title.tmd"), "");
    File.WriteAllText(Path.Combine(nus, "title.tik"), "");
    foreach (var f in new[] { "g.wua", "g.wux", "g_part1.wud", "g_part2.wud", "h.wuhb" }) File.WriteAllText(Path.Combine(dir, f), "");
    var expected = new Dictionary<string, string?>
    {
        ["a.cue"] = "cue", ["a.GDI"] = "gdi", ["a.iso"] = "iso", ["a.rvz"] = "rvz", ["a.chd"] = "chd", ["a.zip"] = "zip",
        ["a.tar.gz"] = "gz", ["a.iso.001"] = "split", ["a.iso.002"] = null, ["a.rar"] = null, ["a.txt"] = null,
        [Path.Combine(dir, "g.wua")] = "wua", [Path.Combine(dir, "g.wux")] = "wiiu-disc", [Path.Combine(dir, "g_part1.wud")] = "wiiu-disc",
        [Path.Combine(dir, "g_part2.wud")] = null, [Path.Combine(dir, "h.wuhb")] = "wiiu-unsupported",
        [title] = "wiiu-folder", [nus] = "nus", [dir] = null,
    };
    foreach (var (path, id) in expected)
    {
        var got = FormatRegistry.Default.Detect(path)?.Id;
        if (got != id) return $"{Path.GetFileName(path)}: expected {id ?? "nothing"}, got {got ?? "nothing"}";
    }
    return null;
});

Test("Format registry: scans claim in priority order, never inside a title", () =>
{
    var dir = Dir("registry-scan");
    void Make(string rel, string text = "")
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }
    Make("a.cue", "FILE \"a.bin\" BINARY\n TRACK 01 MODE1/2352\n  INDEX 01 00:00:00\n");
    Make("a.bin");
    Make("loose.bin");
    Make("x.chd");
    Make("notes.txt");
    Make(Path.Combine("sub", "d.iso.001"));
    Make(Path.Combine("sub", "d.iso.002"));
    Make(Path.Combine("sub", "e.rvz"));
    Make(Path.Combine("sub", "deep", "f.7z"));
    Make(Path.Combine("Game [0005000010101a00]", "code", "app.xml"));
    Make(Path.Combine("Game [0005000010101a00]", "content", "inside.iso"));
    Make(Path.Combine("sub", "g.wua"));

    string Show(List<FileSet> sets) => string.Join(", ", sets.Select(s => $"{Path.GetRelativePath(dir, s.Primary).Replace('\\', '/')}:{s.Format.Id}"));
    var all = Show(FormatRegistry.Default.Scan(dir.Replace('\\', '/'), recursive: true));
    const string expectAll = "a.cue:cue, loose.bin:bin, x.chd:chd, Game [0005000010101a00]:wiiu-folder, sub/d.iso.001:split, sub/e.rvz:rvz, sub/g.wua:wua, sub/deep/f.7z:7z";
    if (all != expectAll) return $"recursive scan gave {all}";
    var top = Show(FormatRegistry.Default.Scan(dir, recursive: false));
    const string expectTop = "a.cue:cue, loose.bin:bin, x.chd:chd, Game [0005000010101a00]:wiiu-folder";
    if (top != expectTop) return $"top-level scan gave {top}";
    var shallow = Show(FormatRegistry.Default.Scan(dir, recursive: true, maxDepth: 2));
    if (shallow.Contains("f.7z")) return $"depth 2 looked too deep: {shallow}";
    var cue = FormatRegistry.Default.Scan(dir, false)[0];
    if (cue.Members.Count != 2 || !cue.Members[1].EndsWith("a.bin")) return $"the cue's members are {string.Join(", ", cue.Members)}";
    return null;
});

Test("Format registry: tabs keep their file-picker patterns", () =>
{
    var convert = DiscFiles.Descriptors.Concat(DiscFiles.Images).Concat(ArchiveExtractor.Extensions).Append(".001").Select(e => "*" + e);
    var extract = new[] { "*.chd" }.Concat(NintendoDisc.Decodable.Select(e => "*" + e));
    var checks = new (string Tab, IEnumerable<string> Old, IReadOnlyList<FormatInfo> Formats)[]
    {
        ("Convert", convert, KnownFormats.DiscInputs),
        ("Verify", ["*.chd"], [KnownFormats.Chd]),
        ("Extract", extract, KnownFormats.Extractable),
        ("Archive", ["*.zip", "*.7z"], KnownFormats.ConvertibleArchives),
    };
    foreach (var (tab, old, formats) in checks)
    {
        var now = KnownFormats.Patterns(formats);
        if (!now.SequenceEqual(old)) return $"{tab}: {string.Join(" ", now)} instead of {string.Join(" ", old)}";
    }
    return null;
});

Test("Format registry: Convert finds what it did before in test-roms", () =>
{
    if (!Directory.Exists(roms)) return Note("no test-roms folder");
    // The scan as it was before the registry: every file, grouped by folder,
    // then descriptors and .001s claim the files they use.
    List<string> OldScan(string folder) => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
        .GroupBy(f => Path.GetDirectoryName(f)!)
        .SelectMany(g =>
        {
            var files = g.Where(f => DiscFiles.Descriptors.Concat(DiscFiles.Images).Concat(ArchiveExtractor.Extensions)
                    .Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) || ArchiveExtractor.IsFirstSplitPart(f))
                .Select(Path.GetFullPath).ToList();
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files.Where(f => DiscFiles.Descriptors.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) || f.EndsWith(".001")))
                foreach (var m in DiscFiles.SetFiles(f).Skip(1)) claimed.Add(Path.GetFullPath(m));
            return files.Where(f => !claimed.Contains(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        }).ToList();
    var old = OldScan(roms).Order(StringComparer.OrdinalIgnoreCase).ToList();
    var now = FormatRegistry.Default.Scan(roms, recursive: true).Where(s => KnownFormats.DiscInputs.Contains(s.Format))
        .Select(s => s.Primary).Order(StringComparer.OrdinalIgnoreCase).ToList();
    if (!old.SequenceEqual(now, StringComparer.OrdinalIgnoreCase))
        return $"before: {old.Count} inputs, now: {now.Count}; first difference: {old.Except(now).Concat(now.Except(old)).FirstOrDefault()}";
    return Note($"{now.Count} inputs, same as before");
});

Test("Job queue: one concurrency budget across every group", () =>
{
    var settings = new AppSettings();
    var queue = new JobQueue(() => 2);
    var running = 0;
    var peak = 0;
    var ran = 0;
    BatchJob Sleepy(string name) => new(name, new NullReport(), _ =>
    {
        var now = Interlocked.Increment(ref running);
        int seen;
        while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
        Thread.Sleep(60);
        Interlocked.Decrement(ref running);
        Interlocked.Increment(ref ran);
        return new JobOutcome(JobStatus.Succeeded, "ok");
    }, _ => { }, _ => { });
    var a = queue.Submit("Convert", [Sleepy("a1"), Sleepy("a2"), Sleepy("a3")], settings, log);
    var b = queue.Submit("Verify", [Sleepy("b1"), Sleepy("b2"), Sleepy("b3")], settings, log);
    if (!Task.WhenAll(a.Completion, b.Completion).Wait(TimeSpan.FromSeconds(20))) return "the groups never finished";
    if (ran != 6) return $"{ran} of 6 jobs ran";
    if (peak > 2) return $"{peak} jobs ran at once with a budget of 2";
    if (peak < 2) return "never used both workers";
    return queue.IsBusy ? "queue still busy after everything finished" : null;
});

Test("Job queue: a step waits for the one before it, and is skipped if that failed", () =>
{
    var settings = new AppSettings();
    var queue = new JobQueue(() => 3);
    var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
    JobOutcome? skipped = null;
    BatchJob Step(string name, JobStatus result, IReadOnlyList<BatchJob>? after = null, Action<JobOutcome>? done = null) =>
        new(name, new NullReport(), _ =>
        {
            Thread.Sleep(name == "convert" ? 150 : 10);
            order.Enqueue(name);
            return new JobOutcome(result, name);
        }, _ => { }, o => done?.Invoke(o), After: after);
    var convert = Step("convert", JobStatus.Succeeded);
    var remove = Step("remove originals", JobStatus.Succeeded, [convert]);
    var bad = Step("bad convert", JobStatus.Failed);
    var never = Step("never", JobStatus.Succeeded, [bad], o => skipped = o);
    var g = queue.Submit("Convert", [remove, convert, bad, never], settings, log);
    if (!g.Completion.Wait(TimeSpan.FromSeconds(20))) return "never finished";
    var ran = order.ToList();
    if (ran.IndexOf("remove originals") < ran.IndexOf("convert")) return $"ran in the order {string.Join(", ", ran)}";
    if (ran.Contains("never")) return "a step ran after the one it depends on failed";
    return skipped?.Status == JobStatus.Skipped ? null : $"the dependent step ended as {skipped?.Status}";
});

Test("Job queue: cancelling stops the running job and drops the queued ones", () =>
{
    var settings = new AppSettings();
    var queue = new JobQueue(() => 1);
    var started = new ManualResetEventSlim();
    var outcomes = new System.Collections.Concurrent.ConcurrentDictionary<string, JobStatus>();
    BatchJob Long(string name) => new(name, new NullReport(), c =>
    {
        started.Set();
        c.Token.WaitHandle.WaitOne();
        c.Token.ThrowIfCancellationRequested();
        return new JobOutcome(JobStatus.Succeeded, "should not finish");
    }, _ => { }, o => outcomes[name] = o.Status);
    var g = queue.Submit("Convert", [Long("one"), Long("two"), Long("three")], settings, log);
    if (!started.Wait(TimeSpan.FromSeconds(10))) return "the first job never started";
    g.Cancel();
    if (!g.Completion.Wait(TimeSpan.FromSeconds(10))) return "cancel didn't finish the group";
    var all = string.Join(", ", outcomes.OrderBy(o => o.Key).Select(o => $"{o.Key} {o.Value}"));
    return outcomes.Count == 3 && outcomes.Values.All(s => s == JobStatus.Cancelled) ? null : all;
});

Test("Job queue: overall progress is weighted by size, and bands keep one bar per job", () =>
{
    var settings = new AppSettings();
    var queue = new JobQueue(() => 2);
    var release = new ManualResetEventSlim();
    var halfway = new ManualResetEventSlim();
    var reported = new List<double>();
    var bigReport = new RecordingReport(reported);
    var small = new BatchJob("small", new NullReport(), _ => new JobOutcome(JobStatus.Succeeded, "ok"), _ => { }, _ => { }, CostBytes: 100);
    var big = new BatchJob("big", bigReport, c =>
    {
        // Two steps, each filling its own part of the bar: compress 0..75%, verify 75..100%.
        c.Band(0, 0.75);
        c.Report.SetProgress(0.5);
        c.Report.SetProgress(1);
        c.Band(0.75, 0.25);
        c.Report.SetProgress(0);
        c.Report.SetProgress(0.4); // 0.75 + 0.1 = 0.85 of the job
        halfway.Set();
        release.Wait(c.Token);
        return new JobOutcome(JobStatus.Succeeded, "ok");
    }, _ => { }, _ => { }, CostBytes: 900);
    var g = queue.Submit("Convert", [small, big], settings, log);
    if (!halfway.Wait(TimeSpan.FromSeconds(10))) return "the big job never got halfway";
    var smallJob = g.Jobs.First(j => j.Name == "small");
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (smallJob.State != QueuedState.Finished && DateTime.UtcNow < deadline) Thread.Sleep(10);
    var done = g.Jobs.Sum(j => j.DoneBytes) / g.Jobs.Sum(j => (double)j.Cost);
    release.Set();
    if (!g.Completion.Wait(TimeSpan.FromSeconds(10))) return "never finished";
    var expectedSeries = new[] { 0.375, 0.75, 0.75, 0.85 };
    if (!reported.Select(r => Math.Round(r, 3)).SequenceEqual(expectedSeries))
        return $"the bar went {string.Join(" → ", reported.Select(r => r.ToString("F3")))}";
    // 100 bytes done + 85% of 900 = 865 of 1000.
    return Math.Abs(done - 0.865) < 0.001 ? null : $"overall {done:P1} instead of 86.5%";
});

Test("Settings: writing back the current values saves nothing", () =>
{
    var dir = Dir("settings-roundtrip");
    var file = Path.Combine(dir, "settings.json");
    var previous = AppSettings.SettingsDirectoryOverride;
    AppSettings.SettingsDirectoryOverride = dir;
    try
    {
        // Non-default values throughout, including a compression string that isn't in the list.
        var settings = new AppSettings
        {
            Compression = "cdzs,cdfl", HunkSize = 2048 * 8, IsoMode = IsoMode.Cd, ExtractFormat = ExtractFormat.Iso,
            NintendoTarget = NintendoTarget.Chd, RvzCompression = LibDolphinRvz.RvzCompression.Lzma2, RvzCompressionLevel = 7,
            RvzBlockSize = 0x80000, RvzScrub = true, OutputFolder = @"D:\out", Overwrite = true, ScanSubfolders = false,
            ArchiveAction = ArchiveAction.Convert, ArchiveFormat = ArchiveFormat.Zip, ArchiveLevel = ArchiveLevel.Ultra,
            ArchiveGrouping = ArchiveGrouping.Single, ArchiveConvertTarget = ConvertTarget.ToZip, ArchiveSingleName = "All",
            WiiUKeysPath = @"D:\keys.txt", ParallelJobs = 3, ThreadsPerJob = 2, TempFolder = @"D:\tmp", VerboseLog = true,
        };
        var before = settings.ToJson();
        var vm = new NoIntrovert.ViewModels.SettingsViewModel(settings, new NoDialogs());
        if (File.Exists(file)) return "constructing the view model saved the settings";
        if (settings.ToJson() != before) return $"constructing the view model changed them (compression is now \"{settings.Compression}\")";

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var bound = typeof(NoIntrovert.ViewModels.SettingsViewModel).GetProperties()
            .Where(p => p.CanRead && p.GetSetMethod() is not null && p.GetIndexParameters().Length == 0).ToList();
        if (bound.Count < 30) return $"only {bound.Count} settable properties found";
        foreach (var p in bound) p.SetValue(vm, p.GetValue(vm));
        if (File.Exists(file)) return "writing back the current values saved the settings";
        if (changed.Count > 0) return $"writing back the current values raised PropertyChanged for {string.Join(", ", changed)}";
        if (settings.ToJson() != before) return "writing back the current values changed them";

        // A real change still saves, once, with its event.
        vm.Overwrite = false;
        if (!File.Exists(file)) return "changing Overwrite didn't save";
        if (AppSettings.Load().Overwrite) return "the saved file still has Overwrite on";
        if (!changed.SequenceEqual(["Overwrite"])) return $"changing Overwrite raised {string.Join(", ", changed)}";

        // And the window's Save on close leaves an unchanged file alone.
        var stamp = File.GetLastWriteTimeUtc(file).AddMinutes(-5);
        File.SetLastWriteTimeUtc(file, stamp);
        vm.Save();
        return File.GetLastWriteTimeUtc(file) == stamp ? null : "an explicit Save of unchanged settings rewrote the file";
    }
    finally { AppSettings.SettingsDirectoryOverride = previous; }
});

Test("RAR inputs are refused", () =>
{
    foreach (var name in new[] { "g.rar", "g.part1.rar", "g.r00", "G.RAR" })
    {
        if (!ArchiveExtractor.IsRar(name)) return $"{name} not seen as RAR";
        if (DiscFiles.IsSupported(name)) return $"Convert accepts {name}";
        if (ArchiveJobs.ConvertibleExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()))
            return $"Archive tab accepts {name}";
    }
    if (ArchiveExtractor.IsRar("d.iso.001") || ArchiveExtractor.IsRar("f.zip")) return "non-RAR seen as RAR";
    // By name, and by content (a RAR renamed to .zip).
    foreach (var name in new[] { "g.rar", "h.zip" })
    {
        var rar = Path.Combine(Dir("rar"), name);
        File.WriteAllBytes(rar, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00]);
        try
        {
            ArchiveExtractor.Extract(rar, Dir("rar-out"), null, CancellationToken.None);
            return $"{name} was extracted";
        }
        catch (NotSupportedException ex) when (ex.Message.Contains("RAR"))
        {
        }
    }
    return null;
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

// ---- Wii U (.wua: ZArchive) ------------------------------------------------------

Test("ZArchive: writer and reader round trip", () =>
{
    var dir = Dir("zar-roundtrip");
    var archive = Path.Combine(dir, "t.zar");
    var rng = new Random(7);
    var files = new Dictionary<string, byte[]>
    {
        ["b/Zeta.bin"] = RandomBytes(rng, 300_000),                     // incompressible: raw blocks
        ["b/alpha.txt"] = TextBytes(400_000),                           // compressible
        ["B/sub/empty.dat"] = [],
        ["a.txt"] = TextBytes(10),
        ["big/many.bin"] = TextBytes(17 * 65536 + 123),                // more than 16 blocks: two offset records
    };
    for (var i = 0; i < 40; i++) files[$"many/f{i:00}.bin"] = RandomBytes(rng, rng.Next(0, 5000));
    using (var w = new NoIntrovert.Core.WiiU.ZArchiveWriter(archive))
    {
        foreach (var (path, data) in files)
        {
            var slash = path.LastIndexOf('/');
            if (slash > 0) w.AddDirectory(path[..slash]);
            w.AddFile(path, new MemoryStream(data));
        }
        w.AddDirectory("emptyfolder");
        w.Finish();
    }
    using var r = NoIntrovert.Core.WiiU.ZArchiveReader.Open(archive);
    if (!r.CheckIntegrity()) return "SHA-256 doesn't match";
    var rootNames = r.Children(r.Root).Select(e => e.Name).ToList();
    if (!rootNames.SequenceEqual(["a.txt", "b", "big", "emptyfolder", "many"])) return $"root order {string.Join(",", rootNames)}";
    foreach (var (path, data) in files)
    {
        var e = r.Find(path.Replace("B/", "b/"));
        if (e is null || !e.IsFile) return $"{path} missing";
        var ms = new MemoryStream();
        r.CopyFile(e, ms);
        if (!ms.ToArray().AsSpan().SequenceEqual(data)) return $"{path} reads back differently";
    }
    return Note($"{files.Count} files, {new FileInfo(archive).Length:N0} bytes");
});

Test("ZArchive: damage is caught by the integrity check", () =>
{
    var src = Path.Combine(scratch, "zar-roundtrip", "t.zar");
    var bad = Path.Combine(Dir("zar-damaged"), "bad.zar");
    var bytes = File.ReadAllBytes(src);
    bytes[1000] ^= 0x40;
    File.WriteAllBytes(bad, bytes);
    using var r = NoIntrovert.Core.WiiU.ZArchiveReader.Open(bad);
    return r.CheckIntegrity() ? "damaged archive passed" : null;
});

var wiiuDir = Dir("wiiu");
var baseTitle = WiiUSample.MakeTitle(Path.Combine(wiiuDir, "Game"), 0x0005000010101C00, 0, "Test Game", 1);
var updateTitle = WiiUSample.MakeTitle(Path.Combine(wiiuDir, "Update v32"), 0x0005000E10101C00, 32, "Test Game", 2);
var dlcTitle = WiiUSample.MakeTitle(Path.Combine(wiiuDir, "DLC"), 0x0005000C10101C00, 16, "Test Game", 3);

Test("Wii U: title folders are recognised", () =>
{
    var t = NoIntrovert.Core.WiiU.WiiUTitles.FromFolder(updateTitle);
    return t.TitleId == 0x0005000E10101C00 && t.Version == 32 && t.Kind == NoIntrovert.Core.WiiU.WiiUTitleKind.Update
        && t.BaseId == 0x0005000010101C00 && t.ArchiveFolder == "0005000e10101c00_v32" && t.Name == "Test Game"
        ? null : t.ToString();
});

Test("Wii U: base + update + DLC folders become one .wua", () =>
{
    var outDir = Dir("wiiu-out");
    var o = RunJob(c => NoIntrovert.Core.WiiU.WuaJobs.Build(c, 0x0005000010101C00, [baseTitle, updateTitle, dlcTitle]),
        s => s.OutputFolder = outDir);
    if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
    var wua = Path.Combine(outDir, "Test Game [0005000010101C00].wua");
    if (!File.Exists(wua)) return $"missing {wua}: {string.Join(", ", Directory.GetFiles(outDir).Select(Path.GetFileName))}";
    var unpacked = Path.Combine(outDir, "unpacked");
    WiiUSample.Unpack(wua, unpacked);
    var expected = Path.Combine(outDir, "expected");
    WiiUSample.CopyTree(baseTitle, Path.Combine(expected, "0005000010101c00_v0"));
    WiiUSample.CopyTree(updateTitle, Path.Combine(expected, "0005000e10101c00_v32"));
    WiiUSample.CopyTree(dlcTitle, Path.Combine(expected, "0005000c10101c00_v16"));
    return SameTree(expected, unpacked) ?? Note(o.Summary);
});

Test("Wii U: a newer update is merged into an existing .wua", () =>
{
    var dir = Dir("wiiu-merge");
    var wua = Path.Combine(dir, "Test Game [0005000010101C00].wua");
    File.Copy(Path.Combine(scratch, "wiiu-out", "Test Game [0005000010101C00].wua"), wua);
    var newer = WiiUSample.MakeTitle(Path.Combine(dir, "Update v48"), 0x0005000E10101C00, 48, "Test Game", 4);
    var o = RunJob(c => NoIntrovert.Core.WiiU.WuaJobs.Build(c, 0x0005000010101C00, [wua, newer]), s => s.OutputFolder = dir);
    if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
    var titles = NoIntrovert.Core.WiiU.WiiUTitles.FromWua(wua).Select(t => t.ArchiveFolder).OrderBy(n => n).ToList();
    var want = new[] { "0005000010101c00_v0", "0005000c10101c00_v16", "0005000e10101c00_v48" };
    if (!titles.SequenceEqual(want)) return $"titles {string.Join(", ", titles)}";
    var unpacked = Path.Combine(dir, "unpacked");
    WiiUSample.Unpack(wua, unpacked);
    return SameTree(newer, Path.Combine(unpacked, "0005000e10101c00_v48"))
        ?? SameTree(baseTitle, Path.Combine(unpacked, "0005000010101c00_v0"));
});

Test("Wii U: an existing .wua is skipped unless Overwrite is on", () =>
{
    var o = RunJob(c => NoIntrovert.Core.WiiU.WuaJobs.Build(c, 0x0005000010101C00, [baseTitle]), s => s.OutputFolder = Path.Combine(scratch, "wiiu-out"));
    return o.Status == JobStatus.Skipped ? null : $"{o.Status}: {o.Summary}";
});

Test("Wii U: folder scan finds titles and .wua files, not folders inside titles", () =>
{
    var found = NoIntrovert.Core.WiiU.WiiUTitles.FindSources(wiiuDir, recursive: true).Select(Path.GetFileName).OrderBy(n => n).ToList();
    return found.SequenceEqual(["DLC", "Game", "Update v32"]) ? null : string.Join(", ", found);
});

Test("Wii U: inputs it can't use are named, not guessed at", () =>
{
    var dir = Dir("wiiu-unsupported");
    var nus = Directory.CreateDirectory(Path.Combine(dir, "nus-without-ticket")).FullName;
    File.WriteAllBytes(Path.Combine(nus, "title.tmd"), [0]);
    File.WriteAllBytes(Path.Combine(dir, "app.wuhb"), [0]);
    var a = NoIntrovert.Core.WiiU.WiiUTitles.UnsupportedReason(nus);
    var b = NoIntrovert.Core.WiiU.WiiUTitles.UnsupportedReason(Path.Combine(dir, "app.wuhb"));
    return a is not null && b is not null && a.Contains("title.tik") && b.Contains("WUHB") ? null : $"{a} / {b}";
});

Test("Wii U: remove the originals once the .wua verifies", () =>
{
    var dir = Dir("wiiu-delete");
    var game = WiiUSample.MakeTitle(Path.Combine(dir, "Game"), 0x0005000010202D00, 0, "Other Game", 5);
    var o = RunJob(c => NoIntrovert.Core.WiiU.WuaJobs.Build(c, 0x0005000010202D00, [game]), s => { s.WiiUDeleteOriginals = true; s.UseRecycleBin = false; });
    return o.Status == JobStatus.Succeeded && !Directory.Exists(game) && File.Exists(Path.Combine(dir, "Other Game [0005000010202D00].wua"))
        ? null : $"{o.Status} {o.Summary}; folder still there: {Directory.Exists(game)}";
});

if (ArchiveEngine.IsAvailable(out _))
{
    Test("Archives: the registry sees what's inside without unpacking", () =>
    {
        var dir = Dir("peek");
        string Zip(string name, params string[] entries)
        {
            var path = Path.Combine(dir, name);
            using var z = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var e in entries)
                using (var w = new StreamWriter(z.CreateEntry(e).Open())) w.Write("x");
            return path;
        }
        var iso = Zip("Game (USA).zip", "Game (USA).iso", "readme.txt");
        var title = Zip("Wii U Game.zip", "Game/code/app.xml", "Game/meta/meta.xml", "Game/content/data.bin");
        var nes = Zip("NES.zip", "Mario.nes");
        var broken = Path.Combine(dir, "broken.zip");
        File.WriteAllBytes(broken, [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 7))]);
        var wuxDir = Path.Combine(scratch, "peek-src");
        Directory.CreateDirectory(wuxDir);
        File.WriteAllBytes(Path.Combine(wuxDir, "Game.wux"), new byte[4096]);
        var wux7z = Path.Combine(dir, "Game.7z");
        using (var engine = new ArchiveEngine())
            engine.Compress(ArchiveFormat.SevenZip, ArchiveLevel.Fast, 0, true, [Path.Combine(wuxDir, "Game.wux")], wux7z, false, null, CancellationToken.None);

        var cases = new (string Path, string Inside, bool Disc, bool WiiU)[]
        {
            (iso, "iso", true, false),
            (title, "bin, wiiu-folder", false, true), // its .bin doesn't make it a disc image
            (nes, "", false, false),
            (wux7z, "wiiu-disc", false, true),
            (broken, "(unreadable)", true, false),   // Convert keeps it; the job reports the problem
        };
        foreach (var (path, inside, disc, wiiu) in cases)
        {
            var set = FormatRegistry.Default.Identify(path);
            if (set is null) return $"{Path.GetFileName(path)} wasn't recognised";
            var got = set.Inside is null ? "(unreadable)" : string.Join(", ", set.Inside.Select(f => f.Id));
            if (got != inside) return $"{Path.GetFileName(path)} holds {got}, expected {inside}";
            if (KnownFormats.TakesDisc(set) != disc) return $"Convert {(disc ? "refused" : "took")} {Path.GetFileName(path)}";
            if (KnownFormats.TakesWiiU(set) != wiiu) return $"the Wii U tab {(wiiu ? "refused" : "took")} {Path.GetFileName(path)}";
        }
        var convertScan = FormatRegistry.Default.Scan(dir, false).Where(KnownFormats.TakesDisc).Select(s => Path.GetFileName(s.Primary));
        var listed = string.Join(", ", convertScan);
        return listed == "broken.zip, Game (USA).zip" ? null : $"a Convert scan lists {listed}";
    });

    Test("Wii U: a zipped title folder becomes a .wua next to the zip", () =>
    {
        var dir = Dir("wiiu-zip");
        var zip = Path.Combine(dir, "Test Game.zip");
        ZipFile.CreateFromDirectory(baseTitle, zip, CompressionLevel.Fastest, includeBaseDirectory: true);
        var o = RunJob(c => NoIntrovert.Core.WiiU.WuaJobs.BuildFromArchive(c, zip), s => { s.WiiUDeleteOriginals = true; s.UseRecycleBin = false; });
        if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
        var wua = Path.Combine(dir, "Test Game [0005000010101C00].wua");
        if (!File.Exists(wua)) return $"no .wua next to the zip: {string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName))}";
        if (File.Exists(zip)) return "the zip wasn't removed after the .wua verified";
        var unpacked = Path.Combine(dir, "unpacked");
        WiiUSample.Unpack(wua, unpacked);
        var expected = Path.Combine(dir, "expected");
        WiiUSample.CopyTree(baseTitle, Path.Combine(expected, "0005000010101c00_v0"));
        return SameTree(expected, unpacked) ?? Note(o.Summary);
    });

    Test("Archive: already-compressed files are skipped alone and stored in a set", () =>
    {
        var dir = Dir("arc-precompressed");
        var set = Path.Combine(dir, "CHDs");
        Directory.CreateDirectory(set);
        // Zero-filled, so 7z *could* shrink them: storing proves the level was switched, not that the data was random.
        File.WriteAllBytes(Path.Combine(set, "Game.chd"), new byte[4 << 20]);
        File.WriteAllBytes(Path.Combine(set, "Other.rvz"), new byte[4 << 20]);
        File.WriteAllText(Path.Combine(set, "notes.txt"), "small");
        var plain = Path.Combine(dir, "Plain");
        Directory.CreateDirectory(plain);
        File.WriteAllBytes(Path.Combine(plain, "Game.bin"), new byte[4 << 20]);

        var alone = RunJob(c => ArchiveJobs.CompressOne(c, [Path.Combine(set, "Game.chd")], "Game", dir));
        if (alone.Status != JobStatus.Skipped) return $"a lone CHD was {alone.Status}: {alone.Summary}";
        if (File.Exists(Path.Combine(dir, "Game.7z"))) return "a lone CHD was packed anyway";

        var stored = RunJob(c => ArchiveJobs.CompressOne(c, [set], "CHDs", dir), s => s.ArchiveTestAfter = true);
        if (stored.Status != JobStatus.Succeeded) return $"the CHD folder: {stored.Status}: {stored.Summary}";
        var storedRatio = (double)new FileInfo(Path.Combine(dir, "CHDs.7z")).Length / ArchiveJobs.SizeOf(set);
        if (storedRatio < 0.95) return $"the CHD folder was compressed to {storedRatio:P1} instead of stored";

        var packed = RunJob(c => ArchiveJobs.CompressOne(c, [plain], "Plain", dir));
        if (packed.Status != JobStatus.Succeeded) return $"the plain folder: {packed.Status}: {packed.Summary}";
        var packedRatio = (double)new FileInfo(Path.Combine(dir, "Plain.7z")).Length / ArchiveJobs.SizeOf(plain);
        return packedRatio < 0.05 ? Note($"stored {storedRatio:P0}, compressed {packedRatio:P2}") : $"the plain folder only shrank to {packedRatio:P1}";
    });
}

// ---- Wii U encrypted: NUS titles and WUD/WUX disc images (made-up keys) -----------

var encKeys = EncryptedSample.MakeKeys(42);
var encDir = Dir("wiiu-enc");
var encKeysTxt = Path.Combine(encDir, "keys.txt");
EncryptedSample.WriteKeysTxt(encKeysTxt, encKeys.Disc, encKeys.Common);
const ulong encBase = 0x0005000010303E00, encUpdate = 0x0005000E10303E00;
var encTree = WiiUSample.MakeTitle(Path.Combine(encDir, "plain-game"), encBase, 0, "Encrypted Game", 21);
var encUpdateTree = WiiUSample.MakeTitle(Path.Combine(encDir, "plain-update"), encUpdate, 16, "Encrypted Game", 22);
var nusGame = EncryptedSample.MakeNus(encTree, Path.Combine(encDir, "nus-game"), encBase, 0, encKeys);
var nusUpdate = EncryptedSample.MakeNus(encUpdateTree, Path.Combine(encDir, "nus-update"), encUpdate, 16, encKeys);
var wudBytes = EncryptedSample.MakeWud(encTree, encBase, encKeys);
var wudPath = Path.Combine(Dir("wiiu-wud"), "game.wud");
File.WriteAllBytes(wudPath, wudBytes);

string? EncryptedBuild(string label, IReadOnlyList<string> sources, Action<AppSettings>? configure, params (string Folder, string Tree)[] expect)
{
    var outDir = Dir("wiiu-enc-out-" + label);
    var o = RunJob(c => NoIntrovert.Core.WiiU.WuaJobs.Build(c, encBase, sources),
        s => { s.OutputFolder = outDir; s.WiiUKeysPath = encKeysTxt; configure?.Invoke(s); });
    if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
    var wua = Directory.GetFiles(outDir, "*.wua").Single();
    var unpacked = Path.Combine(outDir, "unpacked");
    WiiUSample.Unpack(wua, unpacked);
    foreach (var (folder, tree) in expect)
        if (SameTree(tree, Path.Combine(unpacked, folder)) is { } p) return $"{folder}: {p}";
    var folders = Directory.GetDirectories(unpacked).Select(Path.GetFileName).OrderBy(n => n).ToList();
    if (!folders.SequenceEqual(expect.Select(e => e.Folder).OrderBy(n => n))) return $"title folders {string.Join(", ", folders)}";
    return Note(o.Summary);
}

Test("Wii U keys.txt: comments, separators and bad lines", () =>
{
    var path = Path.Combine(encDir, "keys-format.txt");
    File.WriteAllText(path, $"# comment\n{Convert.ToHexString(encKeys.Common).ToLowerInvariant()} # common\n\n" +
        $"{string.Join("-", Convert.ToHexString(encKeys.Disc).Chunk(8).Select(c => new string(c)))} ; disc\nnot-a-key\n");
    var k = NoIntrovert.Core.WiiU.WiiUKeys.Load(path);
    return k.Keys.Count == 2 && k.Problems.Count == 1 ? null : $"{k.Keys.Count} keys, {k.Problems.Count} problems";
});

Test("Wii U NUS: decrypts to the original files", () =>
    EncryptedBuild("nus", [nusGame], null, ("0005000010303e00_v0", encTree)));

Test("Wii U NUS: game + update become one .wua", () =>
    EncryptedBuild("nus-update", [nusGame, nusUpdate], null, ("0005000010303e00_v0", encTree), ("0005000e10303e00_v16", encUpdateTree)));

Test("Wii U WUD: decrypts the game partition", () =>
    EncryptedBuild("wud", [wudPath], null, ("0005000010303e00_v0", encTree)));

Test("Wii U WUX: decompresses and decrypts", () =>
{
    var wux = Path.Combine(Dir("wiiu-wux"), "game.wux");
    File.WriteAllBytes(wux, EncryptedSample.ToWux(wudBytes));
    var smaller = new FileInfo(wux).Length < wudBytes.Length;
    return smaller ? EncryptedBuild("wux", [wux], null, ("0005000010303e00_v0", encTree)) : "WUX isn't smaller than the WUD";
});

Test("Wii U WUD: a dump split into parts", () =>
{
    var dir = Dir("wiiu-split");
    var half = wudBytes.Length / 2 / EncryptedSample.Sector * EncryptedSample.Sector;
    File.WriteAllBytes(Path.Combine(dir, "game_part1.wud"), wudBytes[..half]);
    File.WriteAllBytes(Path.Combine(dir, "game_part2.wud"), wudBytes[half..]);
    var found = NoIntrovert.Core.WiiU.WiiUTitles.FindSources(dir, true).Select(Path.GetFileName).ToList();
    if (!found.SequenceEqual(["game_part1.wud"])) return $"scan found {string.Join(", ", found)}";
    return EncryptedBuild("split", [Path.Combine(dir, "game_part1.wud")], null, ("0005000010303e00_v0", encTree));
});

Test("Wii U WUD: the disc key can come from game.key", () =>
{
    var dir = Dir("wiiu-keyfile");
    File.Copy(wudPath, Path.Combine(dir, "game.wud"));
    File.WriteAllBytes(Path.Combine(dir, "game.key"), encKeys.Disc);
    var onlyCommon = Path.Combine(dir, "keys.txt");
    EncryptedSample.WriteKeysTxt(onlyCommon, encKeys.Common);
    return EncryptedBuild("keyfile", [Path.Combine(dir, "game.wud")], s => s.WiiUKeysPath = onlyCommon, ("0005000010303e00_v0", encTree));
});

Test("Wii U: missing keys are reported, not guessed", () =>
{
    var wrong = Path.Combine(encDir, "wrong-keys.txt");
    EncryptedSample.WriteKeysTxt(wrong, new byte[16], Enumerable.Repeat((byte)0x5A, 16).ToArray());
    var o = RunJob(c => NoIntrovert.Core.WiiU.WuaJobs.Build(c, encBase, [nusGame]), s => { s.OutputFolder = Dir("wiiu-wrongkeys"); s.WiiUKeysPath = wrong; });
    return o.Status == JobStatus.Failed && o.Summary.Contains("keys.txt") ? Note(o.Summary) : $"{o.Status}: {o.Summary}";
});

Test("Wii U NUS: a damaged hashed block is caught", () =>
{
    var bad = Path.Combine(encDir, "nus-damaged");
    WiiUSample.CopyTree(nusGame, bad);
    var app = Path.Combine(bad, "00000002.app");
    var bytes = File.ReadAllBytes(app);
    bytes[0x400 + 100] ^= 0x01;
    File.WriteAllBytes(app, bytes);
    var o = RunJob(c => NoIntrovert.Core.WiiU.WuaJobs.Build(c, encBase, [bad]), s => { s.OutputFolder = Dir("wiiu-damaged"); s.WiiUKeysPath = encKeysTxt; });
    return o.Status == JobStatus.Failed && o.Summary.Contains("hash") && Directory.GetFiles(Path.Combine(scratch, "wiiu-damaged")).Length == 0
        ? Note(o.Summary) : $"{o.Status}: {o.Summary}";
});

Test("Wii U NUS: a personalised ticket is refused", () =>
{
    var personal = EncryptedSample.MakeNus(encTree, Path.Combine(encDir, "nus-personal"), encBase, 0, encKeys, deviceId: 0x12345678);
    var o = RunJob(c => NoIntrovert.Core.WiiU.WuaJobs.Build(c, encBase, [personal]), s => { s.OutputFolder = Dir("wiiu-personal"); s.WiiUKeysPath = encKeysTxt; });
    return o.Status == JobStatus.Failed && o.Summary.Contains("personalised") ? null : $"{o.Status}: {o.Summary}";
});

// ---- GameCube / Wii (libdolphinrvz) ---------------------------------------------

var rvzProblem = (string?)null;
try { rvzProblem = LibDolphinRvz.DolphinRvz.AbiVersion == 1 ? null : "unexpected libdolphinrvz ABI"; }
catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { rvzProblem = ex.Message; }
Test("libdolphinrvz loads", () => rvzProblem);
if (rvzProblem is null)
{
    var gcIso = Path.Combine(Dir("gc"), "Test Game (USA).iso");
    NintendoDiscs.WriteGameCube(gcIso);

    Test("libdolphinrvz structs match the C layout", () =>
        LibDolphinRvz.DolphinRvz.StructSizes == LibDolphinRvz.DolphinRvz.ExpectedStructSizes
            ? null : $"managed {LibDolphinRvz.DolphinRvz.StructSizes}, C {LibDolphinRvz.DolphinRvz.ExpectedStructSizes}");

    Test("Probe a GameCube ISO", () =>
    {
        using var rvz = new LibDolphinRvz.DolphinRvz();
        var info = rvz.Probe(gcIso);
        return info.Platform == LibDolphinRvz.DiscPlatform.GameCube && info.GameId == "GNIE01" && info.CanConvert
            ? null : $"{info}";
    });

    Test("GameCube ISO → RVZ (verified), and back to the same ISO", () =>
    {
        var outDir = Dir("gc-rvz");
        var o = RunJob(c => ChdJobs.Convert(c, gcIso), s => s.OutputFolder = outDir);
        var rvzPath = Path.Combine(outDir, "Test Game (USA).rvz");
        if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
        if (!File.Exists(rvzPath)) return "no .rvz written";
        if (new FileInfo(rvzPath).Length >= new FileInfo(gcIso).Length / 4) return "the RVZ is barely smaller than the ISO";
        var back = Dir("gc-rvz-back");
        var e = RunJob(c => RvzJobs.Extract(c, rvzPath), s => s.OutputFolder = back);
        if (e.Status != JobStatus.Succeeded) return $"extract: {e.Status}: {e.Summary}";
        return SameFile(gcIso, Path.Combine(back, "Test Game (USA).iso")) ?? Note($"{o.Summary}");
    });

    Test("GameCube RVZ → CHD stores the same disc as a CHD made from the ISO", () =>
    {
        var dir = Dir("gc-rvz-chd");
        var rvzPath = Path.Combine(dir, "game.rvz");
        using (var rvz = new LibDolphinRvz.DolphinRvz())
            rvz.Convert(gcIso, rvzPath, new LibDolphinRvz.ConvertSettings());
        var o = RunJob(c => ChdJobs.Convert(c, rvzPath), s => { s.OutputFolder = dir; s.NintendoTarget = NintendoTarget.Chd; });
        if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
        var expected = CreateChd(new PreparedDisc("ref", DiscKind.Dvd, gcIso, "ref"), Path.Combine(dir, "ref.chd"));
        var actual = RawSha1(Path.Combine(dir, "game.chd"));
        return actual == expected ? null : $"CHD data SHA-1 {actual}, expected {expected}";
    });

    Test("A GameCube ISO inside a zip becomes an RVZ", () =>
    {
        var dir = Dir("gc-zip");
        var zip = Path.Combine(dir, "Zipped Game.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            z.CreateEntryFromFile(gcIso, "game.iso", CompressionLevel.Fastest);
        var o = RunJob(c => ChdJobs.Convert(c, zip), s => s.OutputFolder = dir);
        return o.Status == JobStatus.Succeeded && File.Exists(Path.Combine(dir, "Zipped Game.rvz")) ? null : $"{o.Status}: {o.Summary}";
    });

    Test("An RVZ on the Convert tab is skipped (already RVZ)", () =>
    {
        var dir = Dir("gc-rvz-skip");
        var rvzPath = Path.Combine(dir, "game.rvz");
        using (var rvz = new LibDolphinRvz.DolphinRvz())
            rvz.Convert(gcIso, rvzPath, new LibDolphinRvz.ConvertSettings());
        var o = RunJob(c => ChdJobs.Convert(c, rvzPath), s => s.OutputFolder = dir);
        return o.Status == JobStatus.Skipped ? null : $"{o.Status}: {o.Summary}";
    });

    Test("Remove the original ISO after its RVZ verifies", () =>
    {
        var dir = Dir("gc-rvz-delete");
        var iso = Path.Combine(dir, "game.iso");
        File.Copy(gcIso, iso);
        var o = RunJob(c => ChdJobs.Convert(c, iso), s => { s.DeleteOriginals = true; s.UseRecycleBin = false; s.VerifyAfterCreate = false; });
        if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
        return !File.Exists(iso) && File.Exists(Path.Combine(dir, "game.rvz")) ? null : "the ISO is still there, or no RVZ";
    });

    Test("Cancelling an RVZ conversion leaves no RVZ", () =>
    {
        var dir = Dir("gc-rvz-cancel");
        using var cts = new CancellationTokenSource();
        var report = new CancelAtProgress(cts, 0.05);
        var o = RunJob(c => ChdJobs.Convert(c, gcIso), s => { s.OutputFolder = dir; s.ThreadsPerJob = 1; s.RvzBlockSize = 0x8000; }, cts.Token, report);
        if (o.Status != JobStatus.Cancelled) return $"expected Cancelled, got {o.Status}: {o.Summary}";
        return Directory.EnumerateFiles(dir).Any() ? $"left {string.Join(", ", Directory.EnumerateFiles(dir).Select(Path.GetFileName))}" : null;
    });

    Test("WBFS is refused with a reason", () =>
    {
        var dir = Dir("gc-wbfs");
        var wbfs = Path.Combine(dir, "game.wbfs");
        File.WriteAllBytes(wbfs, NintendoDiscs.FakeWbfsHeader());
        var o = RunJob(c => ChdJobs.Convert(c, wbfs), s => s.OutputFolder = dir);
        return o.Status == JobStatus.Failed ? Note(o.Summary) : $"expected Failed, got {o.Status}: {o.Summary}";
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
    var group = new JobQueue(() => 1).Submit("test", [job], settings, log);
    using (token.Register(group.Cancel))
        group.Completion.GetAwaiter().GetResult();
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
static byte[] RandomBytes(Random rng, int length) => WiiUSample.Random(rng, length);
static byte[] TextBytes(int length) => WiiUSample.Text(length);

/// <summary>
/// --wua-sample DIR: writes DIR/sample.wua from three synthetic titles (base,
/// update, DLC) through the real Wii U job, and DIR/expected with the layout
/// it should unpack to, for checking with Exzap's own zarchive tool in CI.
/// </summary>
int WriteWuaSample(string dir)
{
    Directory.CreateDirectory(dir);
    var titles = Path.Combine(dir, "titles");
    var game = WiiUSample.MakeTitle(Path.Combine(titles, "Game"), 0x0005000010101C00, 0, "Sample Game", 11);
    var update = WiiUSample.MakeTitle(Path.Combine(titles, "Update"), 0x0005000E10101C00, 64, "Sample Game", 12);
    var dlc = WiiUSample.MakeTitle(Path.Combine(titles, "DLC"), 0x0005000C10101C00, 32, "Sample Game", 13);
    var o = RunJob(c => NoIntrovert.Core.WiiU.WuaJobs.Build(c, 0x0005000010101C00, [game, update, dlc]), s => s.OutputFolder = dir);
    if (o.Status != JobStatus.Succeeded) { Console.WriteLine($"FAIL  sample .wua: {o.Status} {o.Summary}"); return 1; }
    File.Move(Path.Combine(dir, "Sample Game [0005000010101C00].wua"), Path.Combine(dir, "sample.wua"), true);
    var expected = Path.Combine(dir, "expected");
    WiiUSample.CopyTree(game, Path.Combine(expected, "0005000010101c00_v0"));
    WiiUSample.CopyTree(update, Path.Combine(expected, "0005000e10101c00_v64"));
    WiiUSample.CopyTree(dlc, Path.Combine(expected, "0005000c10101c00_v32"));
    Console.WriteLine($"wrote {Path.Combine(dir, "sample.wua")} and {expected}");
    return 0;
}

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

sealed class RecordingReport(List<double> values) : IJobReport
{
    public void SetStage(string stage) { }
    public void SetProgress(double fraction) { lock (values) values.Add(fraction); }
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

/// <summary>Settings tests never open a picker.</summary>
sealed class NoDialogs : NoIntrovert.Services.IDialogs
{
    public Task<IReadOnlyList<string>> PickFilesAsync(string title, string filterName, IReadOnlyList<string> patterns) => throw new NotSupportedException();
    public Task<string?> PickFileAsync(string title, string filterName, IReadOnlyList<string> patterns) => throw new NotSupportedException();
    public Task<string?> PickFolderAsync(string title) => throw new NotSupportedException();
    public Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string pattern) => throw new NotSupportedException();
    public Task SetClipboardAsync(string text) => throw new NotSupportedException();
}
