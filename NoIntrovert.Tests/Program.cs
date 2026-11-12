using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using LibChdKit;
using NoIntrovert.Core;
using NoIntrovert.Core.Archives;
using NoIntrovert.Core.Cd;
using NoIntrovert.Core.Esde;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Presets;
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
var upstream = ArgValue("--upstream") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "native", "lib-chd-kit", "build-x64", "Release", "chdman-upstream.exe"));
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
    using (var zr = ZArchiveReader.Open(args[zi + 1]))
        if (!zr.CheckIntegrity()) { Console.WriteLine($"FAIL  {args[zi + 1]}: SHA-256 doesn't match"); return 1; }
    WiiUSample.Unpack(args[zi + 1], args[zi + 2]);
    Console.WriteLine($"unpacked {args[zi + 1]} to {args[zi + 2]}");
    return 0;
}

Console.WriteLine($"lib-chd-kit {Chdman.Version} · roms {roms} · scratch {scratch}{(quick ? " · quick" : "")}{(synthetic ? " · synthetic discs" : "")}");

// --layout: only the layout tests (the main window at common desktop sizes); --screenshots DIR also saves a PNG of each.
if (args.Contains("--layout"))
{
    LayoutTests.Run(Test, Dir, ArgValue("--screenshots"));
    goto Summary;
}

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

ConvertTest("ISO (DVD)", isoSlice, isoRef, s => s.Chd.IsoMode = IsoMode.Dvd);

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
}, isoRef, s => s.Chd.IsoMode = IsoMode.Dvd);

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
}, isoRef, s => s.Chd.IsoMode = IsoMode.Dvd);

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

// ---- Scale: a library of 5,000 inputs in 100 folders, the size the app has to handle ----

var bigLibrary = Dir("scale-library");
var bigCount = ScaleLibrary.Make(bigLibrary, 5000);

Test("Scale: 5,000 inputs in 100 folders are scanned, targeted and planned quickly", () =>
{
    var sw = Stopwatch.StartNew();
    var sets = FormatRegistry.Default.Scan(bigLibrary, recursive: true);
    var scan = sw.Elapsed;
    if (sets.Count != bigCount) return $"the scan found {sets.Count} inputs, expected {bigCount}";
    sw.Restart();
    var settings = new AppSettings();
    var rows = sets.Select(s => new PlanInput(s.Primary, ConvertTargets.For(s, settings).Default.Kind, 1)).ToList();
    var targets = sw.Elapsed;
    sw.Restart();
    var plan = ConvertPlanner.Plan(rows, settings, log);
    var planning = sw.Elapsed;
    if (plan.Jobs.Count + plan.Left.Count != bigCount) return $"the plan covers {plan.Jobs.Count + plan.Left.Count} rows";
    var total = scan + targets + planning;
    var report = $"scan {scan.TotalSeconds:F1}s, targets {targets.TotalSeconds:F1}s, plan {planning.TotalSeconds:F2}s";
    return total < TimeSpan.FromSeconds(20) ? Note(report) : $"too slow: {report}";
});

Test("Scale: the queue runs 5,000 jobs without slowing down", () =>
{
    var queue = new JobQueue(() => 4);
    var jobs = Enumerable.Range(0, 5000)
        .Select(i => new BatchJob($"job {i}", new NullReport(), _ => new JobOutcome(JobStatus.Succeeded, "ok"), _ => { }, _ => { }, CostBytes: 1000))
        .ToList();
    var sw = Stopwatch.StartNew();
    var group = queue.Submit("Convert", jobs, new AppSettings(), log);
    if (!group.Completion.Wait(TimeSpan.FromSeconds(60))) return "5,000 jobs didn't finish in a minute";
    var took = sw.Elapsed;
    if (group.Jobs.Any(j => j.Outcome?.Status != JobStatus.Succeeded)) return "some jobs didn't succeed";
    return took < TimeSpan.FromSeconds(15) ? Note($"{took.TotalSeconds:F1}s") : $"too slow: {took.TotalSeconds:F1}s";
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
            General = { PowerUserMode = true, VerboseLog = true },
            Output = { Folder = @"D:\out", Overwrite = true, ScanSubfolders = false, SaveTo = SaveTo.ReplaceOriginals, ReplaceConfirmed = true },
            Performance = { ParallelJobs = 3, ThreadsPerJob = 2, TempFolder = @"D:\tmp" },
            Chd = { Compression = "cdzs,cdfl", HunkSize = 2048 * 8, IsoMode = IsoMode.Cd, ExtractFormat = ExtractFormat.Iso },
            Nintendo = { Target = NintendoTarget.Chd, Compression = LibWiiGameCubeKit.RvzCompression.Lzma2, CompressionLevel = 7, BlockSize = 0x80000, Scrub = true },
            Archives = { Action = ArchiveAction.Convert, Format = ArchiveFormat.Zip, Level = ArchiveLevel.Ultra, Grouping = ArchiveGrouping.Single, ConvertTarget = ConvertTarget.ToZip, SingleName = "All" },
            Keys = { WiiUKeysPath = @"D:\keys.txt" },
        };
        var before = settings.ToJson();
        var vm = new NoIntrovert.ViewModels.SettingsViewModel(settings, new NoDialogs());
        if (File.Exists(file)) return "constructing the view model saved the settings";
        if (settings.ToJson() != before) return $"constructing the view model changed them (compression is now \"{settings.Chd.Compression}\")";

        var changed = new List<string?>();
        var bound = 0;
        foreach (var section in vm.Sections)
        {
            section.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            foreach (var p in section.GetType().GetProperties()
                         .Where(p => p.CanRead && p.GetSetMethod() is not null && p.GetIndexParameters().Length == 0))
            {
                p.SetValue(section, p.GetValue(section));
                bound++;
            }
        }
        if (bound < 30) return $"only {bound} settable properties found";
        if (File.Exists(file)) return "writing back the current values saved the settings";
        if (changed.Count > 0) return $"writing back the current values raised PropertyChanged for {string.Join(", ", changed)}";
        if (settings.ToJson() != before) return "writing back the current values changed them";

        // A real change still saves, once, with its event.
        vm.Output.Overwrite = false;
        if (!File.Exists(file)) return "changing Overwrite didn't save";
        if (AppSettings.Load().Output.Overwrite) return "the saved file still has Overwrite on";
        if (!changed.SequenceEqual(["Overwrite"])) return $"changing Overwrite raised {string.Join(", ", changed)}";

        // And the window's Save on close leaves an unchanged file alone.
        var stamp = File.GetLastWriteTimeUtc(file).AddMinutes(-5);
        File.SetLastWriteTimeUtc(file, stamp);
        vm.Save();
        return File.GetLastWriteTimeUtc(file) == stamp ? Note($"{bound} settings checked") : "an explicit Save of unchanged settings rewrote the file";
    }
    finally { AppSettings.SettingsDirectoryOverride = previous; }
});

Test("Settings: a schema-1 file is converted once, keeping a copy", () =>
{
    var dir = Dir("settings-v1");
    var file = Path.Combine(dir, "settings.json");
    // The layout every release up to 0.6 wrote, with some values changed from their defaults.
    const string v1 = """
    {
      "OutputFolder": "D:\\CHD",
      "Overwrite": true,
      "ScanSubfolders": false,
      "Compression": "cdzs,cdzl,cdfl",
      "HunkSize": 0,
      "IsoMode": "Dvd",
      "VerifyAfterCreate": true,
      "DeleteOriginals": true,
      "UseRecycleBin": false,
      "NintendoTarget": "Chd",
      "RvzCompression": "Lzma2",
      "RvzCompressionLevel": 9,
      "RvzBlockSize": 131072,
      "RvzScrub": false,
      "ExtractFormat": "Auto",
      "SplitBin": false,
      "FixHeaderSha1": false,
      "ArchiveAction": "Extract",
      "ArchiveFormat": "Zip",
      "ArchiveLevel": "Maximum",
      "ArchiveSolid": true,
      "ArchiveGrouping": "PerItem",
      "ArchiveSingleName": "Archive",
      "ArchiveConvertTarget": "Swap",
      "ArchiveTestAfter": true,
      "ArchiveDeleteOriginals": true,
      "ArchiveExtractToOwnFolder": false,
      "WiiUDeleteOriginals": true,
      "WiiUKeysPath": "E:\\Cemu\\keys.txt",
      "ParallelJobs": 2,
      "ThreadsPerJob": 4,
      "TempFolder": "F:\\tmp",
      "ChdmanExePath": "",
      "VerboseLog": true,
      "LogProgress": false,
      "WindowWidth": 1400,
      "WindowHeight": 900
    }
    """;
    File.WriteAllText(file, v1);
    var previous = AppSettings.SettingsDirectoryOverride;
    AppSettings.SettingsDirectoryOverride = dir;
    try
    {
        var s = AppSettings.Load();
        var checks = new (string What, bool Ok)[]
        {
            ("output folder", s.Output.Folder == @"D:\CHD"), ("overwrite", s.Output.Overwrite), ("subfolders", !s.Output.ScanSubfolders),
            // Removal was on, so Replace stays, but it's asked before the first run (never confirmed by a migration).
            ("remove originals", s.Output.SaveTo == SaveTo.ReplaceOriginals && !s.Output.ReplaceConfirmed), ("recycle bin", !s.Output.UseRecycleBin),
            ("compression", s.Chd.Compression == "cdzs,cdzl,cdfl"), ("ISO mode", s.Chd.IsoMode == IsoMode.Dvd), ("split bin", !s.Chd.SplitBin),
            ("Nintendo target", s.Nintendo.Target == NintendoTarget.Chd), ("RVZ compression", s.Nintendo.Compression == LibWiiGameCubeKit.RvzCompression.Lzma2),
            ("RVZ level", s.Nintendo.CompressionLevel == 9), ("archive action", s.Archives.Action == ArchiveAction.Extract),
            ("archive format", s.Archives.Format == ArchiveFormat.Zip), ("archive level", s.Archives.Level == ArchiveLevel.Maximum),
            ("own folder", !s.Archives.ExtractToOwnFolder), ("keys", s.Keys.WiiUKeysPath == @"E:\Cemu\keys.txt"),
            ("parallel jobs", s.Performance.ParallelJobs == 2), ("threads", s.Performance.ThreadsPerJob == 4),
            ("temp folder", s.Performance.TempFolder == @"F:\tmp"), ("verbose log", s.General.VerboseLog),
            ("window", s.General.WindowWidth == 1400 && s.General.WindowHeight == 900), ("power-user mode off", !s.General.PowerUserMode),
        };
        if (checks.FirstOrDefault(c => !c.Ok) is { What: { } wrong }) return $"{wrong} wasn't carried over";

        var backup = Path.Combine(dir, "settings.v1.json");
        if (!File.Exists(backup) || File.ReadAllText(backup) != v1) return "the original file wasn't kept as settings.v1.json";
        var converted = File.ReadAllText(file);
        if (!converted.Contains("\"SchemaVersion\": 2")) return "settings.json wasn't rewritten in the new layout";

        // Loading again is a plain load: nothing converted or rewritten.
        var stamp = File.GetLastWriteTimeUtc(file).AddMinutes(-5);
        File.SetLastWriteTimeUtc(file, stamp);
        var again = AppSettings.Load();
        if (again.ToJson() != s.ToJson()) return "the second load read different values";
        return File.GetLastWriteTimeUtc(file) == stamp ? null : "the second load rewrote the file";
    }
    finally { AppSettings.SettingsDirectoryOverride = previous; }
});

Test("Settings: every schema-1 setting has a home", () =>
{
    var defaults = AppSettings.FromV1(new SettingsV1()).ToJson();
    if (defaults != new AppSettings().ToJson()) return "schema-1 defaults don't convert to the new defaults";
    var lost = new List<string>();
    foreach (var p in typeof(SettingsV1).GetProperties())
    {
        var v1 = new SettingsV1();
        object changed = p.PropertyType switch
        {
            var t when t == typeof(bool) => !(bool)p.GetValue(v1)!,
            var t when t == typeof(string) => "changed",
            var t when t == typeof(int) => (int)p.GetValue(v1)! + 3,
            var t when t == typeof(uint) => (uint)p.GetValue(v1)! + 3,
            var t when t == typeof(double) => (double)p.GetValue(v1)! + 3,
            var t when t.IsEnum => Enum.GetValues(t).Cast<object>().First(v => !v.Equals(p.GetValue(v1))),
            _ => throw new InvalidOperationException($"no test value for {p.PropertyType.Name}"),
        };
        p.SetValue(v1, changed);
        if (AppSettings.FromV1(v1).ToJson() == defaults) lost.Add(p.Name);
    }
    return lost.Count == 0 ? Note($"{typeof(SettingsV1).GetProperties().Length} settings mapped") : $"not carried over: {string.Join(", ", lost)}";
});

// ---- Presets (issue #33): one per action, parameters only under Custom ----

Test("Presets: each action starts on its recommended preset, and ends with Custom", () =>
{
    var fresh = new AppSettings();
    foreach (var action in ActionPresets.Actions)
    {
        var list = ActionPresets.For(action);
        // Wii U → .wua has nothing to choose, so it has one preset and no Custom.
        if (action == PresetAction.Wua ? list.Count != 1 || list[0].IsCustom : list.Count < 2) return $"{action} has {list.Count} presets";
        if (ActionPresets.Selected(fresh, action) != ActionPresets.Preferred(action)) return $"{action} doesn't start on its preferred preset";
        if (action != PresetAction.Wua && (!list[^1].IsCustom || list.Take(list.Count - 1).Any(p => p.IsCustom)))
            return $"{action}: Custom isn't the last and only custom preset";
        if (list.Select(p => p.Id).Distinct().Count() != list.Count) return $"{action} has two presets with one id";
        // The defaults are the recommended values, so a new install behaves as before.
        if (!ActionPresets.Preferred(action).Matches(fresh)) return $"{action}: the default settings aren't its recommended preset";
        // A preset only writes its own action's values: applying it twice is the same as once.
        foreach (var preset in list.Where(p => !p.IsCustom))
        {
            var once = fresh.Clone();
            preset.Values!(once);
            if (!preset.Matches(once)) return $"{action}/{preset.Id} doesn't match what it writes";
        }
    }
    if (!ActionPresets.Preferred(PresetAction.Wua).Name.Contains("WUX")) return "Wii U's preferred preset isn't WUX → .wua";
    if (!ActionPresets.Preferred(PresetAction.Chd).Label.EndsWith('★') || ActionPresets.For(PresetAction.Chd)[1].Label.Contains('★'))
        return "only the preferred preset should be starred";
    return fresh.Presets.Chosen.Count == 0 ? Note($"{ActionPresets.Actions.Count} actions") : "a new install writes down preset choices";
});

Test("Presets: a preset sets its action's values, and Custom keeps yours", () =>
{
    var s = new AppSettings { Chd = { Compression = "cdzs,cdfl", VerifyAfterCreate = false } };
    var mine = s.ToJson();
    var standard = ActionPresets.Effective(s, PresetAction.Chd); // nothing chosen yet: the recommended preset
    if (standard.Chd.Compression != "" || !standard.Chd.VerifyAfterCreate) return "Standard didn't set the CHD values";
    if (s.ToJson() != mine) return "working out a job's settings changed the stored ones";

    // Only Save to removes originals: no preset touches it.
    foreach (var action in ActionPresets.Actions)
        foreach (var p in ActionPresets.For(action).Where(p => !p.IsCustom))
        {
            var replacing = new AppSettings { Output = { SaveTo = SaveTo.ReplaceOriginals } };
            p.Values!(replacing);
            if (!replacing.Output.ReplacesOriginals || p.Id is "replace" or "remove") return $"{action}/{p.Id} decides whether originals are removed";
        }
    ActionPresets.Select(s, ActionPresets.For(PresetAction.Chd)[^1]);
    var custom = ActionPresets.Effective(s, PresetAction.Chd);
    if (custom.Chd.Compression != "cdzs,cdfl" || custom.Chd.VerifyAfterCreate) return "Custom lost your values";

    ActionPresets.Select(s, ActionPresets.Preferred(PresetAction.Chd));
    if (s.Presets.Chosen.ContainsKey("Chd")) return "choosing the recommended preset is written down";
    s.Presets.Chosen["Chd"] = "gone";
    return ActionPresets.Selected(s, PresetAction.Chd).IsPreferred ? null : "an unknown preset id isn't read as the recommended one";
});

Test("Presets: settings from before presets keep their behaviour", () =>
{
    var dir = Dir("settings-presets-upgrade");
    var file = Path.Combine(dir, "settings.json");
    var before = new AppSettings
    {
        Nintendo = { CompressionLevel = 19 },                      // = RVZ Smallest
        Archives = { Level = ArchiveLevel.Maximum },               // no preset: Custom
        Chd = { SplitBin = false },                                // = One .bin
    };
    var node = System.Text.Json.Nodes.JsonNode.Parse(before.ToJson())!.AsObject();
    node.Remove("Presets");
    File.WriteAllText(file, node.ToJsonString());
    var previous = AppSettings.SettingsDirectoryOverride;
    AppSettings.SettingsDirectoryOverride = dir;
    try
    {
        var s = AppSettings.Load();
        var expect = new Dictionary<PresetAction, string>
        {
            [PresetAction.Chd] = "standard", [PresetAction.Rvz] = "smallest", [PresetAction.Wua] = "wua",
            [PresetAction.Archive] = ActionPreset.CustomId, [PresetAction.Extract] = "single-bin", [PresetAction.Unpack] = "own-folder",
            [PresetAction.Verify] = "check",
        };
        foreach (var (action, id) in expect)
            if (ActionPresets.Selected(s, action).Id != id) return $"{action} became {ActionPresets.Selected(s, action).Id}, expected {id}";
        // Nothing behaves differently: every job's settings are the ones the file held.
        foreach (var action in ActionPresets.Actions)
            if (ActionPresets.Effective(s, action).ToJson() != s.ToJson()) return $"{action} runs with different values after the upgrade";
        s.Save();
        var again = AppSettings.Load();
        return again.ToJson() == s.ToJson() ? null : "the presets didn't survive saving and loading";
    }
    finally { AppSettings.SettingsDirectoryOverride = previous; }
});

Test("Presets: the view model saves a real choice only, and shows parameters only for Custom", () =>
{
    var dir = Dir("settings-presets-vm");
    var previous = AppSettings.SettingsDirectoryOverride;
    AppSettings.SettingsDirectoryOverride = dir;
    try
    {
        var file = Path.Combine(dir, "settings.json");
        var vm = new NoIntrovert.ViewModels.SettingsViewModel(new AppSettings(), new NoDialogs());
        var chd = vm.Presets.Chd;
        if (vm.Presets.All.Count != ActionPresets.Actions.Count) return "not every action has a picker";
        chd.Selected = chd.Selected;
        if (File.Exists(file)) return "writing back the current preset saved the settings";
        if (chd.IsCustom) return "the recommended preset shows the parameters";
        var changed = new List<string?>();
        chd.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        chd.Selected = chd.Presets[^1];
        if (!chd.IsCustom || !changed.Contains(nameof(chd.IsCustom))) return "choosing Custom didn't show the parameters";
        if (!File.Exists(file) || !File.ReadAllText(file).Contains("\"Chd\": \"custom\"")) return "choosing Custom wasn't saved";
        return vm.Presets.Rvz.IsCustom ? "choosing Custom for CHD changed RVZ" : null;
    }
    finally { AppSettings.SettingsDirectoryOverride = previous; }
});

SettingsPageTests.Run(Test, Dir);
SettingsDesignTests.Run(Test, Dir);
WiringTests.Run(Test, Dir);
ReviewFixTests.Run(Test, Dir, log);
FormatPresetTests.Run(Test, Dir, log);
EmulatorTests.Run(Test);
LayoutTests.Run(Test, Dir, ArgValue("--screenshots"));

Test("Convert planner: each job runs with its own action's preset", () =>
{
    var s = new AppSettings();
    ActionPresets.Select(s, ActionPresets.For(PresetAction.Rvz).Single(p => p.Id == "smallest"));
    ActionPresets.Select(s, ActionPresets.For(PresetAction.Archive).Single(p => p.Id == "smallest"));
    if (ConvertPlanner.SettingsFor(s, TargetKind.Rvz).Nintendo.CompressionLevel != 19) return "the RVZ job isn't at level 19";
    if (ConvertPlanner.SettingsFor(s, TargetKind.Chd).Nintendo.CompressionLevel == 19) return "RVZ's preset reached the CHD job";
    if (ConvertPlanner.SettingsFor(s, TargetKind.SevenZip).Archives.Level != ArchiveLevel.Ultra) return "the 7z job isn't at Ultra";
    if (ConvertPlanner.SettingsFor(s, TargetKind.Iso).ToJson() != s.ToJson()) return "a target without presets got different settings";
    s.Output.SaveTo = SaveTo.ReplaceOriginals;
    if (!ConvertPlanner.SettingsFor(s, TargetKind.Chd).Output.ReplacesOriginals) return "a preset undid Replace the originals";
    if (ConvertPlanner.SettingsFor(s, TargetKind.Chd, x => x.Output.SaveTo = SaveTo.Folder).Output.ReplacesOriginals) return "a pin didn't win over Save to";

    var dir = Dir("planner-presets");
    var cue = Path.Combine(dir, "game.cue");
    var gc = Path.Combine(dir, "game.gcm");
    var chd = Path.Combine(dir, "game.chd");
    var warns = ConvertPlanner.Plan([new(cue, TargetKind.Chd)], s, log).Warnings;
    if (!warns.Any(w => w.Contains("Originals"))) return "no warning that the CHD job removes originals";
    if (!ConvertPlanner.Plan([new(gc, TargetKind.Rvz)], s, log).Warnings.Any(w => w.Contains("Originals"))) return "no warning for an RVZ job";
    if (!ConvertPlanner.Plan([new(chd, TargetKind.DiscImage)], s, log).Warnings.Any(w => w.Contains("keep their originals")))
        return "no note that an extracted disc image keeps its CHD";
    return ConvertPlanner.Plan([new(cue, TargetKind.Chd)], s, log, x => x.Output.SaveTo = SaveTo.Folder).Warnings.Any(w => w.Contains("Originals"))
        ? "Clean Up's pin still warns about removing originals" : null;
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

Test("Existing CHD: a new one is named \" (2)\" unless Overwrite is on", () =>
{
    var dir = Dir("overwrite");
    var outDir = Dir("overwrite-out");
    File.Copy(synthCue, Path.Combine(dir, "synth.cue"));
    File.Copy(synthBin, Path.Combine(dir, "synth.bin"));
    File.WriteAllText(Path.Combine(outDir, "synth.chd"), "placeholder");
    var o = RunJob(c => ChdJobs.Convert(c, Path.Combine(dir, "synth.cue")), s => s.Output.Folder = outDir);
    if (o.Status != JobStatus.Succeeded || File.ReadAllText(Path.Combine(outDir, "synth.chd")) != "placeholder"
        || !File.Exists(Path.Combine(outDir, "synth (2).chd"))) return $"without Overwrite: {o.Status} {o.Summary}";
    o = RunJob(c => ChdJobs.Convert(c, Path.Combine(dir, "synth.cue")), s => { s.Output.Folder = outDir; s.Output.Overwrite = true; });
    return o.Status == JobStatus.Succeeded && RawSha1(Path.Combine(outDir, "synth.chd")) == synthRef ? null : $"overwrite gave {o.Status}: {o.Summary}";
});

Test("Remove originals after a verified conversion", () =>
{
    var dir = Dir("delete");
    var cue = Path.Combine(dir, "synth.cue");
    File.Copy(synthCue, cue);
    File.Copy(synthBin, Path.Combine(dir, "synth.bin"));
    var o = RunJob(c => ChdJobs.Convert(c, cue), s => { s.Output.SaveTo = SaveTo.ReplaceOriginals; s.Output.UseRecycleBin = false; s.Chd.VerifyAfterCreate = false; });
    if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
    var left = Directory.GetFiles(dir).Select(Path.GetFileName).ToList();
    return left.SequenceEqual(["synth.chd"]) ? null : $"folder holds {string.Join(", ", left)}";
});

SaveToTests.Run(Test, Dir, (work, configure) => RunJob(work, configure), synthCue, synthBin);

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
        s => { s.Output.Folder = outDir; s.Chd.ExtractFormat = ExtractFormat.CueBin; s.Chd.SplitBin = true; });
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
    var r = RunJob(c => ChdJobs.Extract(c, Path.Combine(dir, "m1.chd")), s => { s.Output.Folder = outDir; s.Chd.ExtractFormat = ExtractFormat.Iso; });
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
        s => { s.Output.Folder = Dir("extract-iso-refuse"); s.Chd.ExtractFormat = ExtractFormat.Iso; });
    return o.Status == JobStatus.Failed ? Note(o.Summary) : $"expected Failed, got {o.Status}";
});

Test("Extract a DVD CHD to ISO", () =>
{
    var outDir = Dir("extract-dvd");
    var o = RunJob(c => ChdJobs.Extract(c, Path.Combine(scratch, "ref", "slice.chd")), s => s.Output.Folder = outDir);
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
    var o = RunJob(c => ChdJobs.Convert(c, isoSlice), s => { s.Output.Folder = outDir; s.Chd.IsoMode = IsoMode.Dvd; s.Chd.VerifyAfterCreate = false; }, cts.Token, report);
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

if (!SevenZipEngine.IsAvailable(out var archiveProblem))
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
            using var engine = new SevenZipEngine();
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
        using (var engine = new SevenZipEngine())
            engine.Compress(ArchiveFormat.Zip, ArchiveLevel.Fast, 0, false, [archiveSrc], Path.Combine(dir, "set.zip"), true, null, CancellationToken.None);
        var o = RunJob(c => ArchiveJobs.Convert(c, Path.Combine(dir, "set.zip")), s => s.Archives.ConvertTarget = ConvertTarget.Swap);
        if (o.Status != JobStatus.Succeeded || !File.Exists(Path.Combine(dir, "set.7z"))) return $"zip→7z: {o.Status} {o.Summary}";
        File.Delete(Path.Combine(dir, "set.zip"));
        o = RunJob(c => ArchiveJobs.Convert(c, Path.Combine(dir, "set.7z")), s => s.Archives.ConvertTarget = ConvertTarget.Swap);
        if (o.Status != JobStatus.Succeeded || !File.Exists(Path.Combine(dir, "set.zip"))) return $"7z→zip: {o.Status} {o.Summary}";
        using (var engine = new SevenZipEngine())
            engine.Extract(Path.Combine(dir, "set.zip"), Path.Combine(dir, "out"), null, CancellationToken.None);
        return SameTree(archiveSrc, Path.Combine(dir, "out"));
    });

    Test("Archive: converting to the format it already has is skipped", () =>
    {
        var o = RunJob(c => ArchiveJobs.Convert(c, Path.Combine(scratch, "arc-convert", "set.zip")), s => s.Archives.ConvertTarget = ConvertTarget.ToZip);
        return o.Status == JobStatus.Skipped ? null : $"{o.Status}: {o.Summary}";
    });

    Test("Archive: one archive per file or subfolder in a folder", () =>
    {
        var outDir = Dir("arc-entries");
        var o = RunJob(c => ArchiveJobs.CompressEntries(c, archiveSrc, outDir), s => s.Archives.Format = ArchiveFormat.SevenZip);
        var made = Directory.GetFiles(outDir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var expected = new[] { "Sub Folder.7z", "data.7z", "disc.7z", "empty.7z", "notes.7z" }.OrderBy(n => n, StringComparer.Ordinal);
        return o.Status == JobStatus.Succeeded && made.SequenceEqual(expected) ? Note(o.Summary) : $"{o.Status} {o.Summary}: {string.Join(", ", made)}";
    });

    Test("Archive: several items into one archive", () =>
    {
        var outDir = Dir("arc-single");
        var o = RunJob(c => ArchiveJobs.CompressOne(c, [Path.Combine(archiveSrc, "notes.txt"), Path.Combine(archiveSrc, "Sub Folder")], "Combined", outDir));
        if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
        using var engine = new SevenZipEngine();
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
        var o = RunJob(c => ArchiveJobs.CompressOne(c, [folder], "Game Folder", dir), s => { s.Output.SaveTo = SaveTo.ReplaceOriginals; s.Output.UseRecycleBin = false; s.Archives.TestAfter = false; });
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
        using var engine = new SevenZipEngine();
        try { engine.Test(damaged, null, CancellationToken.None); return "damaged archive passed its test"; }
        catch (ArchiveException ex) { return Note(ex.Message.Split('\n')[0]); }
    });

    Test("Archive: cancelling leaves no partial archive", () =>
    {
        var outDir = Dir("arc-cancel");
        using var cts = new CancellationTokenSource();
        var report = new CancelAtProgress(cts, 0.1);
        var o = RunJob(c => ArchiveJobs.CompressOne(c, [isoSlice], "slice", outDir),
            s => { s.Archives.Level = ArchiveLevel.Ultra; s.Performance.ThreadsPerJob = 1; }, cts.Token, report);
        var left = Directory.GetFiles(outDir);
        return o.Status == JobStatus.Cancelled && left.Length == 0 ? null : $"{o.Status} {o.Summary}, {left.Length} file(s) left";
    });

    // Extract jobs read the src.7z / src.zip written by the round-trip tests above.
    var src7z = Path.Combine(scratch, "arc-7z", "src.7z");
    var srcZip = Path.Combine(scratch, "arc-zip", "src.zip");

    Test("Extract: a 7z into its own folder", () =>
    {
        var outDir = Dir("ext-own");
        var o = RunJob(c => ArchiveJobs.Extract(c, src7z), s => { s.Output.Folder = outDir; s.Archives.ExtractToOwnFolder = true; });
        if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
        var stray = Directory.GetDirectories(outDir).Where(d => Path.GetFileName(d).StartsWith(".noi-extract-")).ToList();
        return stray.Count > 0 ? "staging folder left behind" : SameTree(archiveSrc, Path.Combine(outDir, "src", "archive-src")) ?? Note(o.Summary);
    });

    Test("Extract: a zip straight into the output folder", () =>
    {
        var outDir = Dir("ext-flat");
        var o = RunJob(c => ArchiveJobs.Extract(c, srcZip), s => { s.Output.Folder = outDir; s.Archives.ExtractToOwnFolder = false; });
        return o.Status == JobStatus.Succeeded ? SameTree(archiveSrc, Path.Combine(outDir, "archive-src")) : $"{o.Status}: {o.Summary}";
    });

    Test("Extract: an existing folder gets a \" (2)\" folder unless Overwrite is on", () =>
    {
        var outDir = Dir("ext-own");
        var o = RunJob(c => ArchiveJobs.Extract(c, src7z), s => { s.Output.Folder = outDir; s.Archives.ExtractToOwnFolder = true; });
        if (o.Status != JobStatus.Succeeded || SameTree(archiveSrc, Path.Combine(outDir, "src (2)", "archive-src")) is not null)
            return $"without Overwrite: {o.Status} {o.Summary}";
        o = RunJob(c => ArchiveJobs.Extract(c, src7z), s => { s.Output.Folder = outDir; s.Archives.ExtractToOwnFolder = true; s.Output.Overwrite = true; });
        return o.Status == JobStatus.Succeeded ? null : $"with Overwrite: {o.Status} {o.Summary}";
    });

    Test("Extract: existing files are kept (the new ones get \" (2)\"), or replaced with Overwrite", () =>
    {
        var outDir = Dir("ext-conflict");
        var existing = Path.Combine(outDir, "archive-src", "notes.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllText(existing, "mine");
        var o = RunJob(c => ArchiveJobs.Extract(c, src7z), s => { s.Output.Folder = outDir; s.Archives.ExtractToOwnFolder = false; });
        if (o.Status != JobStatus.Succeeded || File.ReadAllText(existing) != "mine") return $"without Overwrite: {o.Status} {o.Summary}";
        if (!File.Exists(Path.Combine(outDir, "archive-src", "notes (2).txt"))) return "the clashing file wasn't extracted as notes (2).txt";
        File.Delete(Path.Combine(outDir, "archive-src", "notes (2).txt"));
        if (!File.Exists(Path.Combine(outDir, "archive-src", "disc.bin"))) return "files that didn't clash weren't extracted";
        o = RunJob(c => ArchiveJobs.Extract(c, src7z), s => { s.Output.Folder = outDir; s.Archives.ExtractToOwnFolder = false; s.Output.Overwrite = true; });
        if (o.Status != JobStatus.Succeeded) return $"with Overwrite: {o.Status} {o.Summary}";
        return SameTree(archiveSrc, Path.Combine(outDir, "archive-src"));
    });

    Test("Extract: remove the archive afterwards", () =>
    {
        var dir = Dir("ext-delete");
        var archive = Path.Combine(dir, "copy.7z");
        File.Copy(src7z, archive);
        var o = RunJob(c => ArchiveJobs.Extract(c, archive), s => { s.Output.SaveTo = SaveTo.ReplaceOriginals; s.Output.UseRecycleBin = false; });
        return o.Status == JobStatus.Succeeded && !File.Exists(archive) && Directory.Exists(Path.Combine(dir, "copy"))
            ? null : $"{o.Status} {o.Summary}; archive still there: {File.Exists(archive)}";
    });

    Test("Extract: a damaged archive fails and leaves nothing behind", () =>
    {
        var dir = Dir("ext-damaged");
        var damaged = Path.Combine(dir, "bad.7z");
        File.Copy(Path.Combine(scratch, "arc-damaged", "bad.7z"), damaged);
        var o = RunJob(c => ArchiveJobs.Extract(c, damaged), s => { s.Output.SaveTo = SaveTo.ReplaceOriginals; s.Output.UseRecycleBin = false; });
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
    using (var w = new ZArchiveWriter(archive))
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
    using var r = ZArchiveReader.Open(archive);
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
    using var r = ZArchiveReader.Open(bad);
    return r.CheckIntegrity() ? "damaged archive passed" : null;
});

var wiiuDir = Dir("wiiu");
var baseTitle = WiiUSample.MakeTitle(Path.Combine(wiiuDir, "Game"), 0x0005000010101C00, 0, "Test Game", 1);
var updateTitle = WiiUSample.MakeTitle(Path.Combine(wiiuDir, "Update v32"), 0x0005000E10101C00, 32, "Test Game", 2);
var dlcTitle = WiiUSample.MakeTitle(Path.Combine(wiiuDir, "DLC"), 0x0005000C10101C00, 16, "Test Game", 3);

Test("Wii U: title folders are recognised", () =>
{
    var t = WiiUTitles.FromFolder(updateTitle);
    return t.TitleId == 0x0005000E10101C00 && t.Version == 32 && t.Kind == WiiUTitleKind.Update
        && t.BaseId == 0x0005000010101C00 && t.ArchiveFolder == "0005000e10101c00_v32" && t.Name == "Test Game"
        ? null : t.ToString();
});

Test("Wii U: base + update + DLC folders become one .wua", () =>
{
    var outDir = Dir("wiiu-out");
    var o = RunJob(c => NoIntrovert.Core.WiiU.WuaJobs.Build(c, 0x0005000010101C00, [baseTitle, updateTitle, dlcTitle]),
        s => s.Output.Folder = outDir);
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
    var o = RunJob(c => NoIntrovert.Core.WiiU.WuaJobs.Build(c, 0x0005000010101C00, [wua, newer]), s => s.Output.Folder = dir);
    if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
    var titles = WiiUTitles.FromWua(wua).Select(t => t.ArchiveFolder).OrderBy(n => n).ToList();
    var want = new[] { "0005000010101c00_v0", "0005000c10101c00_v16", "0005000e10101c00_v48" };
    if (!titles.SequenceEqual(want)) return $"titles {string.Join(", ", titles)}";
    var unpacked = Path.Combine(dir, "unpacked");
    WiiUSample.Unpack(wua, unpacked);
    return SameTree(newer, Path.Combine(unpacked, "0005000e10101c00_v48"))
        ?? SameTree(baseTitle, Path.Combine(unpacked, "0005000010101c00_v0"));
});

Test("Wii U: an existing .wua is kept, and the new one gets \" (2)\" unless Overwrite is on", () =>
{
    var outDir = Path.Combine(scratch, "wiiu-out");
    var before = Directory.GetFiles(outDir, "*.wua").ToHashSet();
    var o = RunJob(c => NoIntrovert.Core.WiiU.WuaJobs.Build(c, 0x0005000010101C00, [baseTitle]), s => s.Output.Folder = outDir);
    var added = Directory.GetFiles(outDir, "*.wua").Where(f => !before.Contains(f)).ToList();
    foreach (var f in added) File.Delete(f); // later tests read this folder
    return o.Status == JobStatus.Succeeded && added.Count == 1 && added[0].EndsWith(" (2).wua") ? null : $"{o.Status}: {o.Summary}";
});

Test("Wii U: folder scan finds titles and .wua files, not folders inside titles", () =>
{
    var found = NoIntrovert.Core.WiiU.WuaJobs.FindSources(wiiuDir, recursive: true).Select(Path.GetFileName).OrderBy(n => n).ToList();
    return found.SequenceEqual(["DLC", "Game", "Update v32"]) ? null : string.Join(", ", found);
});

Test("Wii U: inputs it can't use are named, not guessed at", () =>
{
    var dir = Dir("wiiu-unsupported");
    var nus = Directory.CreateDirectory(Path.Combine(dir, "nus-without-ticket")).FullName;
    File.WriteAllBytes(Path.Combine(nus, "title.tmd"), [0]);
    File.WriteAllBytes(Path.Combine(dir, "app.wuhb"), [0]);
    var a = WiiUTitles.UnsupportedReason(nus);
    var b = WiiUTitles.UnsupportedReason(Path.Combine(dir, "app.wuhb"));
    return a is not null && b is not null && a.Contains("title.tik") && b.Contains("WUHB") ? null : $"{a} / {b}";
});

Test("Wii U: remove the originals once the .wua verifies", () =>
{
    var dir = Dir("wiiu-delete");
    var game = WiiUSample.MakeTitle(Path.Combine(dir, "Game"), 0x0005000010202D00, 0, "Other Game", 5);
    var o = RunJob(c => NoIntrovert.Core.WiiU.WuaJobs.Build(c, 0x0005000010202D00, [game]), s => { s.Output.SaveTo = SaveTo.ReplaceOriginals; s.Output.UseRecycleBin = false; });
    return o.Status == JobStatus.Succeeded && !Directory.Exists(game) && File.Exists(Path.Combine(dir, "Other Game [0005000010202D00].wua"))
        ? null : $"{o.Status} {o.Summary}; folder still there: {Directory.Exists(game)}";
});

if (SevenZipEngine.IsAvailable(out _))
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
        using (var engine = new SevenZipEngine())
            engine.Compress(ArchiveFormat.SevenZip, ArchiveLevel.Fast, 0, true, [Path.Combine(wuxDir, "Game.wux")], wux7z, false, null, CancellationToken.None);

        var cases = new (string Path, string Inside, bool Disc, bool WiiU)[]
        {
            (iso, "iso", true, false),
            (title, "bin, wiiu-folder", false, true), // its .bin doesn't make it a disc image
            (nes, "nes", false, false), // a cartridge ROM: listed by the Library, never converted
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
        var o = RunJob(c => NoIntrovert.Core.WiiU.WuaJobs.BuildFromArchive(c, zip), s => { s.Output.SaveTo = SaveTo.ReplaceOriginals; s.Output.UseRecycleBin = false; });
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

        var stored = RunJob(c => ArchiveJobs.CompressOne(c, [set], "CHDs", dir), s => s.Archives.TestAfter = true);
        if (stored.Status != JobStatus.Succeeded) return $"the CHD folder: {stored.Status}: {stored.Summary}";
        var storedRatio = (double)new FileInfo(Path.Combine(dir, "CHDs.7z")).Length / ArchiveJobs.SizeOf(set);
        if (storedRatio < 0.95) return $"the CHD folder was compressed to {storedRatio:P1} instead of stored";

        var packed = RunJob(c => ArchiveJobs.CompressOne(c, [plain], "Plain", dir));
        if (packed.Status != JobStatus.Succeeded) return $"the plain folder: {packed.Status}: {packed.Summary}";
        var packedRatio = (double)new FileInfo(Path.Combine(dir, "Plain.7z")).Length / ArchiveJobs.SizeOf(plain);
        return packedRatio < 0.05 ? Note($"stored {storedRatio:P0}, compressed {packedRatio:P2}") : $"the plain folder only shrank to {packedRatio:P1}";
    });
}

if (SevenZipEngine.IsAvailable(out _))
{
    // A folder of mixed inputs for the Convert page's targets and planner.
    var mixed = Dir("convert-mixed");
    void MakeFile(string name, byte[]? bytes = null) => File.WriteAllBytes(Path.Combine(mixed, name), bytes ?? new byte[2048]);
    File.WriteAllText(Path.Combine(mixed, "a.cue"), "FILE \"a.bin\" BINARY\n TRACK 01 MODE1/2352\n  INDEX 01 00:00:00\n");
    MakeFile("a.bin");
    MakeFile("x.chd");
    MakeFile("game.rvz");
    var gcHeader = new byte[0x40];
    gcHeader[0x1C] = 0xC2; gcHeader[0x1D] = 0x33; gcHeader[0x1E] = 0x9F; gcHeader[0x1F] = 0x3D; // GameCube magic
    MakeFile("gc.iso", gcHeader);
    MakeFile("ps2.iso");
    MakeFile("w.wbfs");
    File.WriteAllText(Path.Combine(mixed, "notes.txt"), string.Concat(Enumerable.Repeat("notes ", 500)));
    using (var z = ZipFile.Open(Path.Combine(mixed, "Game (USA).zip"), ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("Game (USA).iso").Open())) w.Write("x");
    using (var z = ZipFile.Open(Path.Combine(mixed, "NES.zip"), ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("Mario.nes").Open())) w.Write(string.Concat(Enumerable.Repeat("mario ", 500)));
    var packMe = Path.Combine(mixed, "Saves");
    Directory.CreateDirectory(packMe);
    File.WriteAllText(Path.Combine(packMe, "save.dat"), string.Concat(Enumerable.Repeat("save ", 500)));
    FileSet SetOf(string path) => FormatRegistry.Default.Identify(path)
        ?? new FileSet(path, [path], Directory.Exists(path) ? ConvertTargets.PackedFolder : ConvertTargets.OtherFile);

    Test("Convert targets: each input offers the right targets", () =>
    {
        var settings = new AppSettings();
        var cases = new (string Name, string Choices, string Default)[]
        {
            ("a.cue", "CHD, Leave as is", "CHD"),                    // a CD of no particular system: a CHD all the same
            ("x.chd", "Disc image, Leave as is", "Leave as is"),     // already optimal
            ("game.rvz", "ISO, CHD, Leave as is", "Leave as is"),    // unknown system (its header is blank)
            ("gc.iso", "RVZ, CHD, Leave as is", "RVZ"),
            ("ps2.iso", "CHD, Leave as is", "Leave as is"),          // unknown system: left alone unless picked
            ("w.wbfs", "Leave as is", "Leave as is"),                // can't be converted yet (Future.md)
            ("Game (USA).zip", "CHD, 7z, Unpack files, Leave as is", "Leave as is"),
            ("NES.zip", "7z, Unpack files, Leave as is", "7z"),
            ("notes.txt", "7z, zip, Leave as is", "7z"),
            ("Saves", "7z, zip, Leave as is", "7z"),
        };
        foreach (var (name, choices, initial) in cases)
        {
            var o = ConvertTargets.For(SetOf(Path.Combine(mixed, name)), settings);
            var got = string.Join(", ", o.Choices.Select(c => c.Label));
            if (got != choices || o.Default.Label != initial) return $"{name}: {got} (starts on {o.Default.Label}), expected {choices} (starts on {initial})";
        }
        var title = ConvertTargets.For(SetOf(baseTitle), settings);
        if (title.Default.Kind != TargetKind.Wua) return $"a Wii U title folder starts on {title.Default.Label}";
        settings.Nintendo.Target = NintendoTarget.Chd;
        if (ConvertTargets.For(SetOf(Path.Combine(mixed, "gc.iso")), settings).Default.Kind != TargetKind.Chd)
            return "a GameCube disc ignored the CHD setting";
        return null;
    });

    Test("Convert planner: rows become the right jobs", () =>
    {
        PlanInput Row(string path, TargetKind target) => new(path, target, 100);
        var rows = new List<PlanInput>
        {
            Row(Path.Combine(mixed, "a.cue"), TargetKind.Chd),          // 0
            Row(Path.Combine(mixed, "x.chd"), TargetKind.DiscImage),    // 1
            Row(Path.Combine(mixed, "NES.zip"), TargetKind.SevenZip),   // 2: zip → 7z conversion
            Row(Path.Combine(mixed, "notes.txt"), TargetKind.SevenZip), // 3: packed
            Row(packMe, TargetKind.Zip),                                // 4: packed as zip
            Row(baseTitle, TargetKind.Wua),                             // 5 ┐ one game
            Row(updateTitle, TargetKind.Wua),                           // 6 ┘
            Row(Path.Combine(mixed, "game.rvz"), TargetKind.Leave),     // 7
        };
        var plan = ConvertPlanner.Plan(rows, new AppSettings(), log);
        if (plan.Error is { } error) return error;
        var jobs = string.Join(" | ", plan.Jobs.Select(j => string.Join(",", j.Rows)));
        if (jobs != "0 | 1 | 2 | 3 | 4 | 5,6") return $"jobs cover rows {jobs}";
        if (!plan.Left.SequenceEqual([7])) return $"left alone: {string.Join(",", plan.Left)}";
        if (plan.Jobs.Single(j => j.Rows.Count == 2).Cost != 200) return "the Wii U job's cost isn't the sum of its rows";

        // Everything into one archive: the packed rows of one format become a single job.
        var single = new AppSettings { Archives = { Grouping = ArchiveGrouping.Single, SingleName = "Bundle" } };
        ActionPresets.Select(single, ActionPresets.For(PresetAction.Archive)[^1]); // a combined archive is a Custom choice
        var packRows = new List<PlanInput> { Row(Path.Combine(mixed, "notes.txt"), TargetKind.SevenZip), Row(packMe, TargetKind.SevenZip) };
        var bundled = ConvertPlanner.Plan(packRows, single, log);
        if (bundled.Jobs.Count != 1 || bundled.Jobs[0].Rows.Count != 2) return $"single-archive packing made {bundled.Jobs.Count} job(s)";
        single.Archives.SingleName = " ";
        return ConvertPlanner.Plan(packRows, single, log).Error is null ? "a blank combined-archive name wasn't refused" : null;
    });

    Test("Convert planner: planned jobs run end to end", () =>
    {
        var outDir = Dir("convert-mixed-out");
        var rows = new List<PlanInput>
        {
            new(Path.Combine(mixed, "NES.zip"), TargetKind.SevenZip),
            new(packMe, TargetKind.Zip),
            new(baseTitle, TargetKind.Wua),
            new(updateTitle, TargetKind.Wua),
        };
        var plan = ConvertPlanner.Plan(rows, new AppSettings(), log);
        foreach (var job in plan.Jobs)
        {
            var o = RunJob(job.Work, s => s.Output.Folder = outDir);
            if (o.Status is not (JobStatus.Succeeded or JobStatus.Warning)) return $"{job.Name}: {o.Status}: {o.Summary}";
        }
        var made = Directory.GetFiles(outDir).Select(Path.GetFileName).Order().ToList();
        var expected = new[] { "NES.7z", "Saves.zip", "Test Game [0005000010101C00].wua" };
        return made.SequenceEqual(expected) ? null : $"made {string.Join(", ", made)}";
    });
}

// ---- Target formats (docs/target-formats.md, Phase 1) --------------------------

Test("Target formats: the guide's table and the registry agree on every system", () =>
{
    var guide = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docs", "target-formats.md"));
    if (!File.Exists(guide)) return $"{guide} not found";
    var text = File.ReadAllText(guide);
    var table = text[text.IndexOf("## Per-system targets", StringComparison.Ordinal)..text.IndexOf("## Overriding the defaults", StringComparison.Ordinal)];
    var rows = table.Split('\n').Select(l => l.Trim())
        .Where(l => l.StartsWith("| ") && !l.StartsWith("| System") && !l.StartsWith("|---"))
        .Select(l => l.Split('|').Select(c => c.Trim()).ToArray())
        .Select(c => (Id: c[2].Trim('`'), Target: c[3], Container: c[4]))
        .ToList();
    if (rows.Count == 0) return "no systems found in the guide";
    var registry = TargetFormats.All;
    if (registry.Select(s => s.Id).Distinct().Count() != registry.Count) return "the registry repeats an id";
    var missing = rows.Select(r => r.Id).Except(registry.Select(s => s.Id)).Concat(registry.Select(s => s.Id).Except(rows.Select(r => r.Id))).ToList();
    if (missing.Count > 0) return $"only in one of them: {string.Join(", ", missing)}";
    foreach (var (id, target, container) in rows)
    {
        var s = TargetFormats.ById(id)!;
        if (s.Target.Label != target) return $"{id}: the guide's target is \"{target}\", the registry's \"{s.Target.Label}\"";
        var word = new string(container.TakeWhile(char.IsLetterOrDigit).ToArray());
        var expected = word switch
        {
            "bare" => TargetContainer.Bare, "7z" => TargetContainer.SevenZip, "zip" => TargetContainer.Zip, "folder" => TargetContainer.Folder,
            _ => (TargetContainer?)null,
        };
        if (expected != s.Target.Container) return $"{id}: the guide's container is \"{container}\", the registry's {s.Target.Container}";
    }
    return Note($"{rows.Count} systems");
});

{
    var sysDir = Dir("systems");
    string Put(string rel, byte[]? bytes = null)
    {
        var p = Path.Combine([sysDir, .. rel.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes ?? new byte[4096]);
        return p;
    }
    string ZipOf(string rel, params string[] entries)
    {
        var p = Put(rel, []);
        File.Delete(p);
        using var z = ZipFile.Open(p, ZipArchiveMode.Create);
        foreach (var e in entries) using (var w = new StreamWriter(z.CreateEntry(e).Open())) w.Write("rom");
        return p;
    }
    // A tiny ISO 9660 image whose SYSTEM.CNF says how the console boots it.
    byte[] PlayStationIso(string systemCnf)
    {
        var iso = new byte[20 * 2048];
        var pvd = 16 * 2048;
        iso[pvd] = 1;
        "CD001"u8.CopyTo(iso.AsSpan(pvd + 1));
        System.Text.Encoding.ASCII.GetBytes("PLAYSTATION".PadRight(32)).CopyTo(iso, pvd + 8);
        BitConverter.TryWriteBytes(iso.AsSpan(pvd + 156 + 2), 18u); // root directory at sector 18
        BitConverter.TryWriteBytes(iso.AsSpan(pvd + 156 + 10), 2048u);
        var dir = 18 * 2048;
        var name = "SYSTEM.CNF;1"u8;
        iso[dir] = (byte)(33 + name.Length + 1);
        BitConverter.TryWriteBytes(iso.AsSpan(dir + 2), 19u);
        BitConverter.TryWriteBytes(iso.AsSpan(dir + 10), (uint)systemCnf.Length);
        iso[dir + 32] = (byte)name.Length;
        name.CopyTo(iso.AsSpan(dir + 33));
        System.Text.Encoding.ASCII.GetBytes(systemCnf).CopyTo(iso, 19 * 2048);
        return iso;
    }
    byte[] Header(int size, params (int Offset, byte[] Bytes)[] parts)
    {
        var b = new byte[size];
        foreach (var (offset, bytes) in parts) bytes.CopyTo(b, offset);
        return b;
    }

    Test("Target formats: systems are told by header, then extension, then folder", () =>
    {
        File.WriteAllText(Path.Combine(Dir("systems/Misc"), "Game.cue"), "FILE \"Game.bin\" BINARY\n TRACK 01 MODE1/2352\n  INDEX 01 00:00:00\n");
        var cases = new (string Path, string? System)[]
        {
            (Put("Misc/Tetris.gb"), "game-boy"),
            (Put("Misc/Sonic.md"), "mega-drive-genesis"),
            (Put("Misc/Game.d64"), "commodore-64-128-vic-20-pet"),
            (Put("Misc/Game.nsp"), "switch"),
            (Put("Atari 2600/Pitfall.bin"), "atari-2600"),       // a shared extension, decided by the folder
            (Put("Downloads/loose.bin"), null),                   // …and without one, unknown
            (Put("PS2/Game.iso"), "playstation-2"),
            (Put("Misc/PS2 Game.iso", PlayStationIso("BOOT2 = cdrom0:\\SLUS_200.00;1\r\n")), "playstation-2"),
            (Put("Misc/PS1 Game.iso", PlayStationIso("BOOT = cdrom:\\SLUS_000.00;1\r\n")), "playstation"),
            (Put("Misc/Saturn Game.iso", Header(4096, (0, "SEGA SEGASATURN "u8.ToArray()))), "saturn"),
            (Put("Misc/Wii Game.gcz", Header(64, (0, [0x01, 0xC0, 0x0B, 0xB1]), (4, [1, 0, 0, 0]))), "wii"),
            (Put("Misc/GC Game.rvz", Header(0x100, (0, "RVZ\u0001"u8.ToArray()), (0x48, [0, 0, 0, 1]))), "gamecube"),
            (Put("Misc/Xbox Game.iso", Header(0x10000 + 64, (0x10000, "MICROSOFT*XBOX*MEDIA"u8.ToArray()))), "xbox"),
            (Path.Combine(sysDir, "Misc", "Game.cue"), SystemDetector.GenericDisc), // a CD of no particular system: still a CHD
            (ZipOf("Misc/Mario (USA).zip", "Mario (USA).nes"), "nes-famicom"),
            (ZipOf("MAME/sf2.zip", "sf2_30g.bin", "sf2e.11e"), "mame"),
        };
        Chdman? chdman = null;
        foreach (var (path, system) in cases)
        {
            var set = FormatRegistry.Default.Identify(path) ?? new FileSet(path, [path], ConvertTargets.OtherFile);
            var got = NoIntrovert.Core.Library.LibraryScanner.SystemOf(set, sysDir, ref chdman);
            if (got != system) return $"{Path.GetRelativePath(sysDir, path)}: {got ?? "unknown"}, expected {system ?? "unknown"}";
        }
        chdman?.Dispose();
        return TargetFormats.NameOf("nes-famicom") == "NES / Famicom" && TargetFormats.NameOf(null) == "Unknown system"
            ? Note($"{cases.Length} files") : "display names don't come from the registry";
    });

    if (SevenZipEngine.IsAvailable(out _))
    {
        var acc = Dir("acceptance");
        string In(string rel) => Path.Combine([acc, .. rel.Split('/')]);
        FileSet Identify(string path) => FormatRegistry.Default.Identify(path) ?? new FileSet(path, [path], ConvertTargets.OtherFile);
        string Make7z(string rel, string member, byte[] bytes)
        {
            var src = Path.Combine(Dir("acceptance-src/" + Guid.NewGuid().ToString("N")[..6]), member);
            File.WriteAllBytes(src, bytes);
            var path = In(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var engine = new SevenZipEngine();
            engine.Compress(ArchiveFormat.SevenZip, ArchiveLevel.Normal, 0, false, [src], path, false, null, CancellationToken.None);
            return path;
        }
        var rom = TextBytes(64 * 1024);
        Directory.CreateDirectory(In("NES"));
        File.WriteAllBytes(In("NES/Loose.nes"), rom);                                               // 1
        using (var z = ZipFile.Open(In("NES/Zipped.zip"), ZipArchiveMode.Create))                   // 2
            using (var s = z.CreateEntry("Zipped.nes").Open()) s.Write(rom);
        Make7z("NES/Packed.7z", "Packed.nes", rom);                                                 // 3
        HandMade7z.Write(In("NES/Zstd.7z"), "Zstd.nes", rom, HandMade7z.Zstd);                      // 4 (as RetroArch would see it)
        HandMade7z.Write(In("NES/Stored.7z"), "Stored.nes", rom, HandMade7z.Copy);                  // 4 (one 7-Zip can unpack)
        Make7z("Wii U/Game.7z", "Game.wux", new byte[4096]);                                        // 5
        Directory.CreateDirectory(In("PS1"));                                                       // 6: cue + 3 bins in a zip
        var tracks = new[] { (0L, (long)DataSectors), (DataSectors, 150L), (DataSectors + 150L, 150L) };
        using (var z = ZipFile.Open(In("PS1/Game.zip"), ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("Game.cue").Open()))
                w.Write("FILE \"Game (Track 1).bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n" +
                        "FILE \"Game (Track 2).bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 01 00:00:00\n" +
                        "FILE \"Game (Track 3).bin\" BINARY\n  TRACK 03 AUDIO\n    INDEX 01 00:00:00\n");
            for (var t = 0; t < tracks.Length; t++)
                using (var s = z.CreateEntry($"Game (Track {t + 1}).bin").Open()) CopyRange(synthBin, s, tracks[t].Item1 * 2352, tracks[t].Item2 * 2352);
        }
        var gcIso = new byte[0x8000];
        new byte[] { 0xC2, 0x33, 0x9F, 0x3D }.CopyTo(gcIso, 0x1C);
        Directory.CreateDirectory(In("Misc"));
        File.WriteAllBytes(In("Misc/Game.iso"), gcIso);                                              // 8
        Make7z("C64/Game.7z", "Game.d64", TextBytes(170 * 1024));                                   // 9
        Directory.CreateDirectory(In("mame"));                                                      // 10
        using (var z = ZipFile.Open(In("mame/sf2.zip"), ZipArchiveMode.Create))
            foreach (var e in new[] { "sf2_30g.bin", "sf2e.11e" }) using (var s = z.CreateEntry(e).Open()) s.Write(rom);
        Directory.CreateDirectory(In("PSP"));
        Encoders.WriteCso(isoSlice, In("PSP/Game.cso"), lz4: false);                                // 15
        Directory.CreateDirectory(In("Switch"));
        File.WriteAllBytes(In("Switch/Game.nsp"), new byte[4096]);                                  // 17
        File.WriteAllBytes(In("Switch/Game.xci"), new byte[4096]);                                  // 17b

        Test("Target formats: each acceptance case gets its status and default (1-6, 8-10, 15, 17)", () =>
        {
            var settings = new AppSettings();
            var cases = new (string Rel, TargetStatus Status, string Default, string Text)[]
            {
                ("NES/Loose.nes", TargetStatus.Convert, "7z", "Convert → .7z"),
                ("NES/Zipped.zip", TargetStatus.Convert, "7z", "Convert → .7z"),
                ("NES/Packed.7z", TargetStatus.Optimal, "Leave as is", "Optimal"),
                ("NES/Zstd.7z", TargetStatus.Convert, "7z", "Convert → .7z (recompress as LZMA2)"),
                ("NES/Stored.7z", TargetStatus.Convert, "7z", "Convert → .7z (recompress as LZMA2)"),
                ("Wii U/Game.7z", TargetStatus.Convert, ".wua", "Convert → .wua"),
                ("PS1/Game.zip", TargetStatus.Convert, "CHD", "Convert → .chd"),
                ("Misc/Game.iso", TargetStatus.Convert, "RVZ", "Convert → .rvz"),
                ("C64/Game.7z", TargetStatus.Convert, "zip", "Convert → .zip"),
                ("mame/sf2.zip", TargetStatus.LeftAsIs, "Leave as is", "Left as is (arcade romset)"),
                ("PSP/Game.cso", TargetStatus.Convert, "CHD", "Convert → .chd"),
                ("Switch/Game.nsp", TargetStatus.Convert, ".nsz", "Convert → .nsz"),
                ("Switch/Game.xci", TargetStatus.Convert, ".nsz", "Convert → .nsz"),
            };
            var rows = new List<PlanInput>();
            foreach (var (rel, status, initial, text) in cases)
            {
                var o = ConvertTargets.For(Identify(In(rel)), settings);
                if (o.Plan.Status != status || o.Default.Label != initial || o.Plan.Text != text)
                    return $"{rel}: {o.Plan.Status} \"{o.Plan.Text}\", starts on {o.Default.Label}; expected {status} \"{text}\", {initial}";
                if (status == TargetStatus.NotYetSupported && o.Choices.Count != 1) return $"{rel} offers {string.Join(", ", o.Choices)} for a target the app can't make";
                rows.Add(new PlanInput(In(rel), o.Default.Kind, 1));
            }
            // Optimal and arcade rows get no job.
            var plan = ConvertPlanner.Plan(rows, settings, log);
            var left = plan.Left.Select(i => cases[i].Rel).ToList();
            return left.SequenceEqual(["NES/Packed.7z", "mame/sf2.zip"])
                ? null : $"left alone: {string.Join(", ", left)}";
        });

        Test("Target formats: per-file-type containers (MSX carts in 7z, its disks in zip, Amiga in zip)", () =>
        {
            var cases = new (string Rel, string Text)[]
            {
                ("MSX/Game.rom", "Convert → .7z"),
                ("MSX/Disk.dsk", "Convert → .zip"),
                ("Amiga/Game.adf", "Convert → .zip"),
                ("Amiga/Game.lha", "Optimal"), // WHDLoad stays bare
            };
            foreach (var (rel, text) in cases)
            {
                var path = In(rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, TextBytes(4096));
                var got = ConvertTargets.For(Identify(path), new AppSettings()).Plan.Text;
                if (got != text) return $"{rel}: {got}, expected {text}";
            }
            return null;
        });

        Test("Target formats: WBFS and CISO are left as is", () =>
        {
            foreach (var path in new[] { Put("Wii/Game.wbfs"), Put("Wii/Game.ciso") })
            {
                var o = ConvertTargets.For(Identify(path), new AppSettings());
                var choices = string.Join(", ", o.Choices.Select(c => c.Label));
                if (o.Plan.Status != TargetStatus.LeftAsIs || choices != "Leave as is")
                    return $"{Path.GetFileName(path)}: {o.Plan.Status} \"{o.Plan.Text}\", offers {choices}";
            }
            return null;
        });

        // Runs the jobs the planner makes for each row's default, with results in their own folder.
        string? RunDefault(string rel, string outDir)
        {
            var o = ConvertTargets.For(Identify(In(rel)), new AppSettings());
            var plan = ConvertPlanner.Plan([new PlanInput(In(rel), o.Default.Kind, 1)], new AppSettings(), log);
            if (plan.Error is { } error) return error;
            foreach (var job in plan.Jobs)
            {
                var r = RunJob(job.Work, s => s.Output.Folder = outDir);
                if (r.Status != JobStatus.Succeeded) return $"{job.Name}: {r.Status}: {r.Summary}";
            }
            return null;
        }
        string? HoldsOnly(string archive, string member, string method)
        {
            var members = ArchivePeek.Members(archive);
            if (members is null) return $"{Path.GetFileName(archive)} can't be read";
            if (members.Count != 1 || members[0].Name != member || members[0].Method != method)
                return $"{Path.GetFileName(archive)} holds {string.Join(", ", members.Select(m => $"{m.Name} ({m.Method})"))}";
            return null;
        }

        Test("Target formats: a loose ROM and a zipped one become single-file LZMA2 7z, not solid (1, 2, D9)", () =>
        {
            var outDir = Dir("acceptance-out-7z");
            if ((RunDefault("NES/Loose.nes", outDir) ?? RunDefault("NES/Zipped.zip", outDir)) is { } problem) return problem;
            foreach (var name in new[] { "Loose", "Zipped" })
            {
                var made = Path.Combine(outDir, name + ".7z");
                if (HoldsOnly(made, name + ".nes", "LZMA") is { } wrong) return wrong;
                using var a = SharpCompress.Archives.ArchiveFactory.OpenArchive(made);
                if (a.IsSolid) return $"{name}.7z is solid";
                if (ConvertTargets.For(Identify(made), new AppSettings()).Plan.Status != TargetStatus.Optimal) return $"{name}.7z isn't optimal";
            }
            return null;
        });

        Test("Target formats: a 7z that isn't LZMA/LZMA2 is recompressed in place (4)", () =>
        {
            var path = In("NES/Stored.7z");
            if (RunDefault("NES/Stored.7z", "") is { } problem) return problem;
            if (HoldsOnly(path, "Stored.nes", "LZMA") is { } wrong) return wrong;
            var outDir = Dir("acceptance-out-recompressed");
            using (var engine = new SevenZipEngine()) engine.Extract(path, outDir, null, CancellationToken.None);
            return File.ReadAllBytes(Path.Combine(outDir, "Stored.nes")).AsSpan().SequenceEqual(rom) ? null : "the ROM changed";
        });

        Test("Target formats: a zipped cue with three bins becomes a bare CHD (6)", () =>
        {
            var outDir = Dir("acceptance-out-chd");
            if (RunDefault("PS1/Game.zip", outDir) is { } problem) return problem;
            var made = Directory.GetFiles(outDir).Select(Path.GetFileName).ToList();
            return made.SequenceEqual(["Game.chd"]) ? null : $"made {string.Join(", ", made)}";
        });

        Test("Target formats: a C64 disk in 7z becomes a zip (9, R9)", () =>
        {
            var outDir = Dir("acceptance-out-zip");
            if (RunDefault("C64/Game.7z", outDir) is { } problem) return problem;
            return HoldsOnly(Path.Combine(outDir, "Game.zip"), "Game.d64", "Deflate");
        });

        Test("Target formats: a PSP CSO becomes a DVD CHD with 2048-byte hunks (15, D3)", () =>
        {
            var outDir = Dir("acceptance-out-psp");
            if (RunDefault("PSP/Game.cso", outDir) is { } problem) return problem;
            using var chdman = new Chdman();
            var info = chdman.Info(Path.Combine(outDir, "Game.chd"), hunkStats: false);
            return info.HunkBytes == 2048 && info.Metadata.Any(m => m.TagString.Trim() == "DVD")
                ? null : $"{info.HunkBytes}-byte hunks, metadata {string.Join(", ", info.Metadata.Select(m => m.TagString))}";
        });
    }
}

// ---- Target formats Phase 2: overrides (FR-12 to FR-14) ---------------------------

if (SevenZipEngine.IsAvailable(out _))
{
    var ovr = Dir("overrides");
    string P(string rel) => Path.Combine([ovr, .. rel.Split('/')]);
    void Write(string rel, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!); File.WriteAllBytes(P(rel), bytes); }
    Write("NDS/Game.nds", TextBytes(2 * 1024 * 1024));
    Write("NDS/Small.nds", TextBytes(64 * 1024));
    Write("PSP/Game.cso", new byte[4096]);
    Write("Switch/Game.xci", new byte[4096]);
    Write("GBA/Game.gba", TextBytes(4096));
    FileSet SetAt(string rel) => FormatRegistry.Default.Identify(P(rel))!;
    TargetPlan PlanWith(string rel, params TargetOverride[] overrides)
    {
        var s = new AppSettings();
        s.TargetFormats.Overrides.AddRange(overrides);
        return TargetResolver.Resolve(SetAt(rel), s);
    }

    Test("Overrides: a file type in a system, then the system, then a file type everywhere, then the registry (FR-12)", () =>
    {
        var cases = new (TargetOverride[] Overrides, string Expected)[]
        {
            ([], "Convert → .7z"),
            ([new("*", ".gba", "zip")], "Convert → .zip"),
            ([new("*", ".gba", "zip"), new("game-boy-advance", null, "leave")], "Left as is (your setting)"),
            ([new("game-boy-advance", null, "leave"), new("game-boy-advance", ".gba", "zip")], "Convert → .zip"),
            ([new("game-boy-advance", null, "recommended"), new("*", ".gba", "zip")], "Convert → .7z"), // "recommended" is a choice too
        };
        foreach (var (overrides, expected) in cases)
        {
            var got = PlanWith("GBA/Game.gba", overrides).Text;
            if (got != expected) return $"{string.Join(" + ", overrides.Select(o => $"{o.System}/{o.Extension}→{o.Target}"))}: {got}, expected {expected}";
        }
        return null;
    });

    Test("Overrides: Nintendo DS → zip makes Game.zip (Phase 2 acceptance 13)", () =>
    {
        var s = new AppSettings { TargetFormats = { Overrides = [new("nintendo-ds", null, "zip")] } };
        var o = ConvertTargets.For(SetAt("NDS/Game.nds"), s);
        if (o.Default != TargetChoice.Zip || o.Plan.Text != "Convert → .zip") return $"{o.Plan.Text}, starts on {o.Default}";
        var outDir = Dir("overrides-out");
        var plan = ConvertPlanner.Plan([new PlanInput(P("NDS/Game.nds"), o.Default.Kind, 1)], s, log);
        foreach (var job in plan.Jobs)
            if (RunJob(job.Work, x => x.Output.Folder = outDir) is { Status: not JobStatus.Succeeded } r) return r.Summary;
        return File.Exists(Path.Combine(outDir, "Game.zip")) ? null : string.Join(", ", Directory.GetFiles(outDir).Select(Path.GetFileName));
    });

    Test("Overrides: every system's .cso left as is (acceptance 14), Switch → .xcz (17b)", () =>
    {
        var cso = PlanWith("PSP/Game.cso", new TargetOverride("*", ".cso", "leave"));
        if (cso.Status != TargetStatus.LeftAsIs) return $"cso: {cso.Text}";
        var xci = PlanWith("Switch/Game.xci", new TargetOverride("switch", null, "xcz"));
        return xci.Text == "Convert → .xcz" ? null : $"xci: {xci.Text}";
    });

    Test("Overrides: D1 keeps a DS ROM above the size bare", () =>
    {
        var s = new AppSettings { TargetFormats = { DsBareAboveMb = 1 } };
        var big = TargetResolver.Resolve(SetAt("NDS/Game.nds"), s);
        var small = TargetResolver.Resolve(SetAt("NDS/Small.nds"), s);
        return big.Status == TargetStatus.Optimal && small.Text == "Convert → .7z" ? null : $"big: {big.Text}; small: {small.Text}";
    });

    Test("Overrides: an unknown system or target is ignored and reported (FR-13)", () =>
    {
        var s = new AppSettings { TargetFormats = { Overrides = [new("not-a-system", null, "zip"), new("game-boy-advance", null, "xcz"), new("*", ".gba", "cso")] } };
        var plan = TargetResolver.Resolve(SetAt("GBA/Game.gba"), s);
        var problems = TargetOverrides.Problems(s.TargetFormats).ToList();
        return plan.Text == "Convert → .7z" && problems.Count == 3 ? null : $"{plan.Text}; {problems.Count} problem(s): {string.Join(" | ", problems)}";
    });

    Test("Overrides: exported on one PC, imported on another, the same rows are marked (acceptance 19)", () =>
    {
        var first = new NoIntrovert.ViewModels.TargetFormatSettingsViewModel(new AppSettings(), null!);
        first.Systems.First(r => r.System.Id == "nintendo-ds").Choice = first.Systems.First(r => r.System.Id == "nintendo-ds").Options.First(o => o.Id == "zip");
        first.NewRuleSystem = first.RuleSystems.First(c => c.Value == "*");
        first.NewRuleExtension = "cso";
        first.NewRuleTarget = first.RuleTargets.First(t => t.Id == "leave");
        first.AddRuleCommand.Execute(null);
        var exported = System.Text.Json.JsonSerializer.Serialize(new AppSettings { TargetFormats = { Overrides = [new("nintendo-ds", null, "zip"), new("*", ".cso", "leave")] } }.TargetFormats.Overrides);

        var second = new NoIntrovert.ViewModels.TargetFormatSettingsViewModel(new AppSettings(), null!);
        var note = second.ImportFrom(exported);
        string Marked(NoIntrovert.ViewModels.TargetFormatSettingsViewModel vm) =>
            string.Join(", ", vm.Systems.Where(r => r.IsOverridden).Select(r => $"{r.System.Id}={r.Choice.Id}")) + " | " + string.Join(", ", vm.Rules.Select(r => r.Text));
        return Marked(first) == Marked(second) ? Note(note) : $"{Marked(first)} ≠ {Marked(second)}";
    });

    Test("Overrides: the Library describes every file again when they change", () =>
    {
        var s = new AppSettings();
        var first = NoIntrovert.Core.Library.LibraryScanner.Scan([ovr], null, null, CancellationToken.None, s);
        var before = first.Items.Single(i => i.Path.EndsWith("Game.gba")).FormatStatusText;
        s.TargetFormats.Overrides.Add(new("game-boy-advance", null, "leave"));
        var second = NoIntrovert.Core.Library.LibraryScanner.Scan([ovr], first, null, CancellationToken.None, s);
        var after = second.Items.Single(i => i.Path.EndsWith("Game.gba")).FormatStatusText;
        return before == "Convert → .7z" && after == "Left as is (your setting)" ? null : $"{before} → {after}";
    });
}

// ---- Target formats Phase 3: multi-disc, 3DS, PS Vita, hashes, header fixes, splitting ----

if (SevenZipEngine.IsAvailable(out _))
{
    var p3 = Dir("phase3");
    string Q(string rel) => Path.Combine([p3, .. rel.Split('/')]);
    void Put3(string rel, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(Q(rel))!); File.WriteAllBytes(Q(rel), bytes); }
    FileSet Set3(string rel) => FormatRegistry.Default.Identify(Q(rel))!;
    // Plans the row's default and runs its jobs; returns a problem or null.
    string? RunDefault3(string rel, Action<AppSettings>? configure = null)
    {
        var s = new AppSettings();
        configure?.Invoke(s);
        var o = ConvertTargets.For(Set3(rel), s);
        var plan = ConvertPlanner.Plan([new PlanInput(Q(rel), o.Default.Kind, 1)], s, log);
        foreach (var job in plan.Jobs)
            if (RunJob(job.Work, x => configure?.Invoke(x)) is { Status: not (JobStatus.Succeeded or JobStatus.Warning) } r) return $"{job.Name}: {r.Status}: {r.Summary}";
        return plan.Jobs.Count == 0 ? "no job" : null;
    }
    string Members3(string archive) => string.Join(", ", (ArchivePeek.Members(archive) ?? []).Select(m => m.Name));

    Test("Multi-disc: three discs become CHDs in hidden/ and one .m3u listing them (acceptance 7, FR-7)", () =>
    {
        for (var d = 1; d <= 3; d++)
        {
            var stem = $"Game (USA) (Disc {d})";
            Directory.CreateDirectory(Q("PlayStation"));
            File.Copy(synthBin, Q($"PlayStation/{stem}.bin"));
            File.WriteAllText(Q($"PlayStation/{stem}.cue"), File.ReadAllText(synthCue).Replace("synth.bin", stem + ".bin"));
        }
        var rows = Enumerable.Range(1, 3).Select(d => new PlanInput(Q($"PlayStation/Game (USA) (Disc {d}).cue"), TargetKind.Chd, 1)).ToList();
        for (var run = 0; run < 2; run++) // the second run rewrites the playlist instead of adding another
        {
            var plan = ConvertPlanner.Plan(rows, new AppSettings(), log);
            if (plan.Jobs.Count != 1 || plan.Jobs[0].Rows.Count != 3) return $"{plan.Jobs.Count} job(s)";
            var o = RunJob(plan.Jobs[0].Work, s => s.Output.Overwrite = true);
            if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
        }
        var chds = Directory.GetFiles(Q("PlayStation/hidden"), "*.chd").Select(Path.GetFileName).Order().ToList();
        if (chds.Count != 3) return $"hidden/ has {string.Join(", ", chds)}";
        var playlists = Directory.GetFiles(Q("PlayStation"), "*.m3u");
        if (playlists.Length != 1) return $"{playlists.Length} playlists";
        var lines = File.ReadAllLines(playlists[0]);
        var expected = Enumerable.Range(1, 3).Select(d => $"hidden/Game (USA) (Disc {d}).chd");
        return Path.GetFileName(playlists[0]) == "Game (USA).m3u" && lines.SequenceEqual(expected) ? null : $"{Path.GetFileName(playlists[0])}: {string.Join(" | ", lines)}";
    });

    // A 3DS cartridge image: an NCSD header whose first partition's NCCH has (or hasn't) the NoCrypto flag.
    byte[] Cci(bool encrypted)
    {
        var image = TextBytes(64 * 1024);
        image.AsSpan(0, 0x400).Clear();
        "NCSD"u8.CopyTo(image.AsSpan(0x100));
        BitConverter.TryWriteBytes(image.AsSpan(0x120), 1u); // partition 0 at media unit 1 (0x200)
        "NCCH"u8.CopyTo(image.AsSpan(0x200 + 0x100));
        image[0x200 + 0x188 + 7] = (byte)(encrypted ? 0 : 0x04);
        return image;
    }
    Put3("3DS/Game.3ds", Cci(encrypted: false));
    Put3("3DS/Locked.3ds", Cci(encrypted: true));

    Test("3DS: a decrypted .3ds becomes a bare .cci, the raw file (acceptance 11, D2)", () =>
    {
        var o = ConvertTargets.For(Set3("3DS/Game.3ds"), new AppSettings());
        if (o.Plan.Text != "Convert → .cci (renamed from .3ds)" || o.Default != TargetChoice.Cci) return $"{o.Plan.Text} ({o.Default})";
        if (!o.Choices.Contains(TargetChoice.SevenZip) || !o.Choices.Contains(TargetChoice.Zcci)) return string.Join(", ", o.Choices);
        if (RunDefault3("3DS/Game.3ds") is { } problem) return problem;
        if (File.Exists(Q("3DS/Game.7z"))) return "a 7z was made";
        return File.Exists(Q("3DS/Game.3ds")) && File.ReadAllBytes(Q("3DS/Game.cci")).AsSpan().SequenceEqual(File.ReadAllBytes(Q("3DS/Game.3ds")))
            ? null : "Game.cci isn't the .3ds's bytes";
    });

    Test("3DS: with the 7z override a decrypted .3ds still goes into a 7z as .cci", () =>
    {
        void SevenZip(AppSettings s) => s.TargetFormats.Overrides.Add(new TargetOverride("nintendo-3ds", null, "7z"));
        var s7 = new AppSettings();
        SevenZip(s7);
        var o = ConvertTargets.For(Set3("3DS/Game.3ds"), s7);
        if (o.Plan.Text != "Convert → .7z (as .cci)") return o.Plan.Text;
        if (RunDefault3("3DS/Game.3ds", SevenZip) is { } problem) return problem;
        var held = Members3(Q("3DS/Game.7z"));
        return held == "Game.cci" ? null : $"holds {held}";
    });

    Test("3DS: an encrypted image is planned to be decrypted into a bare .cci, with .zcci and 7z offered (acceptance 12, FR-9)", () =>
    {
        var o = ConvertTargets.For(Set3("3DS/Locked.3ds"), new AppSettings());
        if (o.Plan.Text != "Convert → .cci (decrypted)" || o.Default != TargetChoice.Cci) return o.Plan.Text;
        return o.Choices.Contains(TargetChoice.Zcci) && o.Choices.Contains(TargetChoice.SevenZip) ? null : string.Join(", ", o.Choices);
    });

    // ---- 3DS through lib-3ds-kit (D2): its synthetic images, made with its fake keys, and the bytes its reference expects ----
    var ctrFixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "native", "lib-3ds-kit", "tests", "fixtures"));
    if (Directory.Exists(ctrFixtures))
    {
        Lib3dsKit.Keys.CtrKeyStore FakeKeys()
        {
            var ks = new Lib3dsKit.Keys.CtrKeyStore();
            ks.LoadBoot9(File.ReadAllBytes(Path.Combine(ctrFixtures, "keys", "boot9_fake.bin")), requireKnownHash: false);
            ks.LoadAesKeysFile(Path.Combine(ctrFixtures, "keys", "aes_keys_fake.txt"));
            ks.LoadSeedDbFile(Path.Combine(ctrFixtures, "keys", "seeddb_fake.bin"));
            return ks;
        }
        string Expected3(string name) => System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(ctrFixtures, "expected.json")))
            .RootElement.GetProperty(name).GetProperty("sha256").GetString()!;
        string Sha256Of(string path) { using var f = File.OpenRead(path); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f)).ToLowerInvariant(); }
        string Sha256In7z(string archive)
        {
            var into = Dir("ctr-check-" + Guid.NewGuid().ToString("N")[..6]);
            NoIntrovert.Core.Formats.ArchiveExtractor.Extract(archive, into, null, CancellationToken.None);
            var files = Directory.GetFiles(into);
            return files.Length == 1 ? Path.GetFileName(files[0]) + ":" + Sha256Of(files[0]) : $"{files.Length} files";
        }
        Directory.CreateDirectory(Q("3DS-real"));
        File.Copy(Path.Combine(ctrFixtures, "game_a.3ds"), Q("3DS-real/Game A.3ds"));       // encrypted cartridge image
        File.Copy(Path.Combine(ctrFixtures, "game_b.cia"), Q("3DS-real/Game B.cia"));       // encrypted .cia of a game
        new Lib3dsKit.Operations.CtrConverter(FakeKeys()).Compress(Path.Combine(ctrFixtures, "expected", "game_a.decrypted.3ds"), Q("3DS-real/Game C.zcci"), new());
        NoIntrovert.Core.Ctr.CtrJobs.KeysForTests = FakeKeys;

        Test("3DS: an encrypted image, a .cia and a .zcci each plan their way to a decrypted .cci (D2)", () =>
        {
            string? Expect(string rel, string text, TargetChoice choice, Action<AppSettings>? configure = null)
            {
                var s = new AppSettings();
                configure?.Invoke(s);
                var o = ConvertTargets.For(Set3(rel), s);
                return o.Plan.Text == text && o.Default == choice ? null : $"{rel}: {o.Plan.Text} ({o.Default})";
            }
            void Zcci(AppSettings s) => s.TargetFormats.Overrides.Add(new TargetOverride("nintendo-3ds", null, "zcci"));
            void SevenZip(AppSettings s) => s.TargetFormats.Overrides.Add(new TargetOverride("nintendo-3ds", null, "7z"));
            return Expect("3DS-real/Game A.3ds", "Convert → .cci (decrypted)", TargetChoice.Cci)
                ?? Expect("3DS-real/Game B.cia", "Convert → .cci (from the .cia)", TargetChoice.Cci)
                ?? Expect("3DS-real/Game C.zcci", "Convert → .cci (unpacked from Z3DS)", TargetChoice.Cci)
                ?? Expect("3DS-real/Game A.3ds", "Convert → .zcci (decrypted)", TargetChoice.Zcci, Zcci)
                ?? Expect("3DS-real/Game C.zcci", "Optimal", TargetChoice.Leave, Zcci)
                ?? Expect("3DS-real/Game B.cia", "Convert → .7z (.cci, from the .cia)", TargetChoice.SevenZip, SevenZip)
                ?? Expect("3DS/Game.3ds", "Convert → .zcci", TargetChoice.Zcci, Zcci);
        });

        Test("3DS: without keys an encrypted image's job fails, says where keys go, and writes nothing", () =>
        {
            NoIntrovert.Core.Ctr.CtrJobs.KeysForTests = () => new Lib3dsKit.Keys.CtrKeyStore();
            try
            {
                var plan = ConvertPlanner.Plan([new PlanInput(Q("3DS-real/Game A.3ds"), TargetKind.Cci, 1)], new AppSettings(), log);
                var r = RunJob(plan.Jobs[0].Work);
                return r.Status == JobStatus.Failed && r.Summary.Contains("3DS key missing") && r.Summary.Contains("Settings › Keys")
                       && !File.Exists(Q("3DS-real/Game A.cci")) && File.Exists(Q("3DS-real/Game A.3ds")) ? null : $"{r.Status}: {r.Summary}";
            }
            finally { NoIntrovert.Core.Ctr.CtrJobs.KeysForTests = FakeKeys; }
        });

        Test("3DS: an encrypted image becomes a bare decrypted .cci, byte for byte as lib-3ds-kit's reference, with its dump's hashes kept (R11)", () =>
        {
            if (RunDefault3("3DS-real/Game A.3ds") is { } problem) return problem;
            if (File.Exists(Q("3DS-real/Game A.7z"))) return "a 7z was made";
            if (Sha256Of(Q("3DS-real/Game A.cci")) != Expected3("game_a.decrypted.3ds")) return "Game A.cci isn't the reference";
            var original = NoIntrovert.Core.Dats.OriginalHashes.Default.Get(Q("3DS-real/Game A.cci"));
            var dump = NoIntrovert.Core.Dats.Hasher.HashFile(Q("3DS-real/Game A.3ds"))[0];
            return original?.Files.FirstOrDefault()?.Sha1 == dump.Sha1 ? null : "the dump's hashes weren't recorded";
        });

        Test("3DS: with the 7z override a .cia becomes a .cci in its 7z, as lib-3ds-kit's reference CCI", () =>
        {
            if (RunDefault3("3DS-real/Game B.cia", s => s.TargetFormats.Overrides.Add(new TargetOverride("nintendo-3ds", null, "7z"))) is { } problem) return problem;
            var held = Sha256In7z(Q("3DS-real/Game B.7z"));
            return held == "Game B.cci:" + Expected3("game_b.3ds") ? null : held;
        });

        Test("3DS: a 7z holding an encrypted image becomes a bare decrypted .cci, with the image's own hashes kept", () =>
        {
            Directory.CreateDirectory(Q("3DS-packed"));
            var inner = Path.Combine(Dir("3ds-packed-src"), "Game D.3ds");
            File.Copy(Path.Combine(ctrFixtures, "game_a.3ds"), inner);
            using (var engine = new SevenZipEngine())
                engine.Compress(ArchiveFormat.SevenZip, ArchiveLevel.Normal, 0, false, [inner], Q("3DS-packed/Game D.7z"), false, null, CancellationToken.None);
            var o = ConvertTargets.For(Set3("3DS-packed/Game D.7z"), new AppSettings());
            if (o.Plan.Text != "Convert → .cci (unpacked)" || o.Default != TargetChoice.Cci) return $"{o.Plan.Text} ({o.Default})";
            if (RunDefault3("3DS-packed/Game D.7z") is { } problem) return problem;
            if (Sha256Of(Q("3DS-packed/Game D.cci")) != Expected3("game_a.decrypted.3ds")) return "Game D.cci isn't the decrypted image";
            var original = NoIntrovert.Core.Dats.OriginalHashes.Default.Get(Q("3DS-packed/Game D.cci"));
            return original?.Files.FirstOrDefault()?.Sha1 == NoIntrovert.Core.Dats.Hasher.HashFile(inner)[0].Sha1
                ? null : "the hashes kept aren't the image's (the 7z's?)";
        });

        Test("3DS: .zcci for Azahar from a .cia, bare .cci from a .zcci, both checked after", () =>
        {
            var plan = ConvertPlanner.Plan([new PlanInput(Q("3DS-real/Game B.cia"), TargetKind.Zcci, 1), new PlanInput(Q("3DS-real/Game C.zcci"), TargetKind.Cci, 1)],
                new AppSettings(), log);
            foreach (var job in plan.Jobs)
                if (RunJob(job.Work) is { Status: not (JobStatus.Succeeded or JobStatus.Warning) } r) return $"{job.Name}: {r.Summary}";
            if (!Lib3dsKit.CtrFile.IsCompressed(Q("3DS-real/Game B.zcci"))) return "Game B.zcci isn't Z3DS";
            var back = Path.Combine(Dir("ctr-back"), "b.cci");
            new Lib3dsKit.Operations.CtrConverter(FakeKeys()).Decompress(Q("3DS-real/Game B.zcci"), back, new());
            if (Sha256Of(back) != Expected3("game_b.3ds")) return "Game B.zcci doesn't hold the reference CCI";
            return Sha256Of(Q("3DS-real/Game C.cci")) == Expected3("game_a.decrypted.3ds") ? null : "Game C.cci isn't the decrypted image";
        });

        Test("3DS: a .zcci is hashed as the image it holds, so it matches its DAT entry (Verify & match)", () =>
        {
            var z = NoIntrovert.Core.Dats.Hasher.HashFile(Q("3DS-real/Game C.zcci"))[0];
            var plain = NoIntrovert.Core.Dats.Hasher.HashFile(Path.Combine(ctrFixtures, "expected", "game_a.decrypted.3ds"))[0];
            return z.Sha1 == plain.Sha1 && z.Crc == plain.Crc && z.Size == plain.Size ? null : $"{z.Size} {z.Sha1} vs {plain.Size} {plain.Sha1}";
        });

        Test("3DS: Replace the originals removes a .cia only once its .cci is made and checked", () =>
        {
            File.Copy(Path.Combine(ctrFixtures, "game_b.cia"), Q("3DS-real/Replace.cia"));
            var r = RunJob(c => NoIntrovert.Core.Ctr.CtrJobs.ToCci(c, Q("3DS-real/Replace.cia"), compress: false),
                s => { s.Output.SaveTo = SaveTo.ReplaceOriginals; s.Output.UseRecycleBin = false; });
            return r.Status == JobStatus.Succeeded && File.Exists(Q("3DS-real/Replace.cci")) && !File.Exists(Q("3DS-real/Replace.cia")) ? null : $"{r.Status}: {r.Summary}";
        });
        NoIntrovert.Core.Ctr.CtrJobs.KeysForTests = null;
    }
    else Console.WriteLine($"SKIP  3DS through lib-3ds-kit: no fixtures at {ctrFixtures}");

    // ---- Switch through lib-nx-kit: its synthetic packages and the fake prod.keys that opens them ----
    var nxFixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "native", "lib-nx-kit", "tests", "fixtures"));
    if (Directory.Exists(nxFixtures))
    {
        LibNxKit.Keys.KeySet NxKeys() => LibNxKit.Keys.KeySet.Load(Path.Combine(nxFixtures, "prod.keys.fake"));
        Directory.CreateDirectory(Q("Switch-real"));
        File.Copy(Path.Combine(nxFixtures, "test_game.nsp"), Q("Switch-real/Test Game.nsp"));
        File.Copy(Path.Combine(nxFixtures, "test_game_update.nsp"), Q("Switch-real/Test Game Update.nsp"));
        NoIntrovert.Core.Switch.SwitchJobs.KeysForTests = NxKeys;
        string? RunKind(string rel, TargetKind kind, Action<AppSettings>? configure = null)
        {
            var plan = ConvertPlanner.Plan([new PlanInput(Q(rel), kind, 1)], new AppSettings(), log);
            if (plan.Jobs.Count != 1) return $"{plan.Jobs.Count} job(s)";
            var r = RunJob(plan.Jobs[0].Work, configure);
            return r.Status is JobStatus.Succeeded or JobStatus.Warning ? null : $"{r.Status}: {r.Summary}";
        }

        // Every file in a package, by name, with its SHA-256: what an .nsz must give back (the package's file order may differ).
        string Contents(string path)
        {
            using var package = LibNxKit.Packages.SwitchPackage.Open(path, NxKeys());
            return string.Join(" ", package.Files.OrderBy(f => f.Name, StringComparer.Ordinal).Select(f =>
            {
                using var data = package.OpenFile(f);
                return f.Name + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data))[..12];
            }));
        }

        Test("Switch: an .nsp becomes an .nsz by default, and back to the same .nsp byte for byte (lib-nx-kit)", () =>
        {
            var o = ConvertTargets.For(Set3("Switch-real/Test Game.nsp"), new AppSettings());
            if (o.Plan.Text != "Convert → .nsz" || o.Default != TargetChoice.Nsz) return $"{o.Plan.Text} ({o.Default})";
            if (!o.Choices.Contains(TargetChoice.Xcz)) return string.Join(", ", o.Choices);
            if (RunDefault3("Switch-real/Test Game.nsp") is { } problem) return problem;
            if (!File.Exists(Q("Switch-real/Test Game.nsz"))) return string.Join(", ", Directory.GetFiles(Q("Switch-real")).Select(Path.GetFileName));
            if (new FileInfo(Q("Switch-real/Test Game.nsz")).Length >= new FileInfo(Q("Switch-real/Test Game.nsp")).Length) return "the .nsz isn't smaller";
            var nsz = ConvertTargets.For(Set3("Switch-real/Test Game.nsz"), new AppSettings());
            if (nsz.Plan.Status != TargetStatus.Optimal || !nsz.Choices.Contains(TargetChoice.Nsp)) return $".nsz: {nsz.Plan.Text}, {string.Join(", ", nsz.Choices)}";
            var back = Dir("switch-back");
            if (RunKind("Switch-real/Test Game.nsz", TargetKind.Nsp, s => s.Output.Folder = back) is { } p2) return p2;
            var original = Contents(Q("Switch-real/Test Game.nsp"));
            var restored = Contents(Path.Combine(back, "Test Game.nsp"));
            if (restored != original) return $"the files differ:\n  {original}\n  {restored}";
            // lib-nx-kit keeps the file order and header padding, so the whole file is the original (R11).
            return File.ReadAllBytes(Path.Combine(back, "Test Game.nsp")).AsSpan().SequenceEqual(File.ReadAllBytes(Q("Switch-real/Test Game.nsp")))
                ? null : "same files, but not the same .nsp";
        });

        Test("Switch: an update with title-key crypto, as a cartridge image (.xcz) and back to .xci", () =>
        {
            if (RunKind("Switch-real/Test Game Update.nsp", TargetKind.Xcz) is { } problem) return problem;
            var o = ConvertTargets.For(Set3("Switch-real/Test Game Update.xcz"), new AppSettings());
            if (!o.Choices.Contains(TargetChoice.Xci) || o.Choices.Contains(TargetChoice.Nsp)) return string.Join(", ", o.Choices);
            if (RunKind("Switch-real/Test Game Update.xcz", TargetKind.Nsp) is { } p2) return p2; // "uncompressed" of a cartridge image is an .xci
            using var package = LibNxKit.Packages.SwitchPackage.Open(Q("Switch-real/Test Game Update.xci"), NxKeys());
            return package.IsGameCard && package.Titles.Count == 1 ? null : $"{package.Format}, {package.Titles.Count} title(s)";
        });

        Test("Switch: without prod.keys the job fails, says where it goes, and writes nothing", () =>
        {
            NoIntrovert.Core.Switch.SwitchJobs.KeysForTests = null;
            try
            {
                var empty = Dir("switch-no-keys");
                var r = RunJob(c => NoIntrovert.Core.Switch.SwitchJobs.Convert(c, Q("Switch-real/Test Game.nsp"), TargetKind.Xcz, empty),
                    s => s.Keys.SwitchKeysPath = Path.Combine(empty, "nowhere.keys"));
                // The machine running the tests may have real keys in an emulator's folder; then the fake package can't be opened with them.
                return r.Status == JobStatus.Failed && Directory.GetFiles(empty).Length == 0 ? Note(r.Summary) : $"{r.Status}: {r.Summary}";
            }
            finally { NoIntrovert.Core.Switch.SwitchJobs.KeysForTests = NxKeys; }
        });

        Test("Switch: Replace the originals removes the .nsp once its .nsz is written and checked", () =>
        {
            File.Copy(Path.Combine(nxFixtures, "test_game.nsp"), Q("Switch-real/Replace.nsp"));
            var problem = RunKind("Switch-real/Replace.nsp", TargetKind.Nsz, s => { s.Output.SaveTo = SaveTo.ReplaceOriginals; s.Output.UseRecycleBin = false; });
            return problem ?? (File.Exists(Q("Switch-real/Replace.nsz")) && !File.Exists(Q("Switch-real/Replace.nsp")) ? null : "the original is still there or the .nsz is missing");
        });

        Test("Switch: Settings › Keys describes the prod.keys lib-nx-kit finds", () =>
        {
            var text = NoIntrovert.Core.Switch.SwitchJobs.Describe(Path.Combine(nxFixtures, "prod.keys.fake"));
            return text.StartsWith("Using prod.keys in ") && text.Contains(nxFixtures) ? null : text;
        });
        NoIntrovert.Core.Switch.SwitchJobs.KeysForTests = null;
    }
    else Console.WriteLine($"SKIP  Switch through lib-nx-kit: no fixtures at {nxFixtures}");

    // A NoNpDrm folder: sce_sys/param.sfo (TITLE, TITLE_ID) and the game's files.
    byte[] Sfo(params (string Key, string Value)[] entries)
    {
        var keys = new MemoryStream();
        var data = new MemoryStream();
        var index = new List<(int Key, int Len, int Max, int Data)>();
        foreach (var (key, value) in entries)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
            var max = (bytes.Length + 3) & ~3;
            index.Add(((int)keys.Length, bytes.Length, max, (int)data.Length));
            keys.Write(System.Text.Encoding.UTF8.GetBytes(key + "\0"));
            data.Write(bytes); data.Write(new byte[max - bytes.Length]);
        }
        while (keys.Length % 4 != 0) keys.WriteByte(0);
        var keyStart = 20 + 16 * entries.Length;
        var o = new MemoryStream();
        o.Write("\0PSF"u8); o.Write(BitConverter.GetBytes(0x101)); o.Write(BitConverter.GetBytes(keyStart));
        o.Write(BitConverter.GetBytes(keyStart + (int)keys.Length)); o.Write(BitConverter.GetBytes(entries.Length));
        foreach (var e in index)
        {
            o.Write(BitConverter.GetBytes((ushort)e.Key)); o.Write(BitConverter.GetBytes((ushort)0x0204));
            o.Write(BitConverter.GetBytes(e.Len)); o.Write(BitConverter.GetBytes(e.Max)); o.Write(BitConverter.GetBytes(e.Data));
        }
        o.Write(keys.ToArray()); o.Write(data.ToArray());
        return o.ToArray();
    }
    Put3("Vita/PCSE00000/sce_sys/param.sfo", Sfo(("CATEGORY", "gd"), ("TITLE", "Test: Game"), ("TITLE_ID", "PCSE00000")));
    Put3("Vita/PCSE00000/eboot.bin", TextBytes(4096));

    Test("PS Vita: a NoNpDrm folder becomes Title (ID).zip and a launcher file (acceptance 16, FR-10)", () =>
    {
        var set = Set3("Vita/PCSE00000");
        if (set.Format != KnownFormats.VitaFolder) return $"seen as {set.Format.Id}";
        var o = ConvertTargets.For(set, new AppSettings());
        if (o.Plan.Text != "Convert → .zip") return o.Plan.Text;
        if (RunDefault3("Vita/PCSE00000", s => s.TargetFormats.WriteVitaLauncher = true) is { } problem) return problem;
        var made = Directory.GetFiles(Q("Vita")).Select(Path.GetFileName).Order().ToList();
        if (!made.SequenceEqual(["Test Game (PCSE00000).psvita", "Test Game (PCSE00000).zip"])) return $"made {string.Join(", ", made)}";
        if (File.ReadAllText(Q("Vita/Test Game (PCSE00000).psvita")) != "PCSE00000") return "the launcher doesn't hold the title ID";
        return Members3(Q("Vita/Test Game (PCSE00000).zip")).Contains("PCSE00000/sce_sys/param.sfo") ? null : Members3(Q("Vita/Test Game (PCSE00000).zip"));
    });

    // A byte-swapped N64 ROM: the first word is 0x37804012.
    var z64 = TextBytes(8192);
    new byte[] { 0x80, 0x37, 0x12, 0x40 }.CopyTo(z64, 0);
    var v64 = (byte[])z64.Clone();
    for (var i = 0; i < v64.Length; i += 2) (v64[i], v64[i + 1]) = (v64[i + 1], v64[i]);
    Put3("N64 off/Game.v64", v64);
    Put3("N64 on/Game.v64", v64);

    Test("Header fixes: .v64 stays .v64 with them off, becomes .z64 with them on (acceptance 18, R12)", () =>
    {
        if (RunDefault3("N64 off/Game.v64") is { } off) return $"off: {off}";
        if (Members3(Q("N64 off/Game.7z")) != "Game.v64") return $"off: holds {Members3(Q("N64 off/Game.7z"))}";
        var on = ConvertTargets.For(Set3("N64 on/Game.v64"), new AppSettings { TargetFormats = { NormalizeHeaders = true } });
        if (on.Plan.Text != "Convert → .7z (as .z64)") return $"on: {on.Plan.Text}";
        if (RunDefault3("N64 on/Game.v64", s => s.TargetFormats.NormalizeHeaders = true) is { } problem) return $"on: {problem}";
        if (Members3(Q("N64 on/Game.7z")) != "Game.z64") return $"on: holds {Members3(Q("N64 on/Game.7z"))}";
        var outDir = Dir("phase3-z64");
        using (var engine = new SevenZipEngine()) engine.Extract(Q("N64 on/Game.7z"), outDir, null, CancellationToken.None);
        if (!File.ReadAllBytes(Path.Combine(outDir, "Game.z64")).SequenceEqual(z64)) return "the .z64 isn't the big-endian ROM";
        // R11: the fixed ROM's archive keeps the hashes of the .v64 it came from.
        return NoIntrovert.Core.Dats.OriginalHashes.Default.Get(Q("N64 on/Game.7z"))?.Source == "Game.v64" ? null : "no original hashes recorded";
    });

    Test("Splitting: a zip holding two games becomes one 7z per game (R6)", () =>
    {
        Directory.CreateDirectory(Q("NES"));
        using (var z = ZipFile.Open(Q("NES/Two Games.zip"), ZipArchiveMode.Create))
            foreach (var name in new[] { "Alpha (USA).nes", "Beta (USA).nes" })
                using (var s = z.CreateEntry(name).Open()) s.Write(TextBytes(4096));
        var o = ConvertTargets.For(Set3("NES/Two Games.zip"), new AppSettings());
        if (o.Plan.Text != "Convert → one .7z per game (R6)") return o.Plan.Text;
        if (RunDefault3("NES/Two Games.zip") is { } problem) return problem;
        return Members3(Q("NES/Alpha (USA).7z")) == "Alpha (USA).nes" && Members3(Q("NES/Beta (USA).7z")) == "Beta (USA).nes"
            ? null : string.Join(", ", Directory.GetFiles(Q("NES")).Select(Path.GetFileName));
    });

    Test("Original hashes: a CHD remembers its cue/bin's name, size, CRC32, MD5 and SHA-1 (R11, FR-11)", () =>
    {
        var dir = Q("R11");
        Directory.CreateDirectory(dir);
        File.Copy(synthBin, Path.Combine(dir, "Disc.bin"));
        File.WriteAllText(Path.Combine(dir, "Disc.cue"), File.ReadAllText(synthCue).Replace("synth.bin", "Disc.bin"));
        var o = RunJob(c => ChdJobs.Convert(c, Path.Combine(dir, "Disc.cue")));
        if (o.Status != JobStatus.Succeeded) return o.Summary;
        var entry = NoIntrovert.Core.Dats.OriginalHashes.Default.Get(Path.Combine(dir, "Disc.chd"));
        var bin = entry?.Files.SingleOrDefault(f => f.Name == "Disc.bin");
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(synthBin)));
        return bin is { Md5: not null } && bin.Sha1 == sha && bin.Size == new FileInfo(synthBin).Length ? null : $"{entry?.Source}: {string.Join(" | ", entry?.Files ?? [])}";
    });
}

// ---- Verify & match (M6): DATs, hashing, matching --------------------------------

Test("DATs: ClrMamePro and Logiqx XML read the same games", () =>
{
    var cmp = NoIntrovert.Core.Dats.DatFile.ParseClrMamePro("""
        clrmamepro (
        	name "Nintendo - Game Boy"
        	version "2026.10.07"
        )

        game (
        	name "Tetris (World) (Rev 1)"
        	region "World"
        	rom ( name "Tetris (World) (Rev 1).gb" size 32768 crc 46DF91AD md5 982ED5D2B12A0377EB14BCDC4123744E sha1 74591CC9501AF93873F9A5D3EB12DA12C0723BBC )
        )
        game (
        	name "Disc Game (Japan)"
        	serial "SLPS-00001"
        	rom ( name "Disc Game (Japan) (Track 01).bin" size 1000 crc 0000abcd sha1 aa serial "SLPS-00001" )
        )
        """);
    var xml = NoIntrovert.Core.Dats.DatFile.ParseXml("""
        <?xml version="1.0"?>
        <!DOCTYPE datafile PUBLIC "-//Logiqx//DTD ROM Management Datafile//EN" "http://www.logiqx.com/dtds/datafile.dtd">
        <datafile>
          <header><name>Nintendo - Game Boy</name><version>20261001</version></header>
          <game name="Tetris (World) (Rev 1)"><description>x</description>
            <rom name="Tetris (World) (Rev 1).gb" size="32768" crc="46df91ad" sha1="74591cc9501af93873f9a5d3eb12da12c0723bbc"/>
          </game>
        </datafile>
        """);
    if (cmp.Name != "Nintendo - Game Boy" || cmp.Version != "2026.10.07" || cmp.Games.Count != 2) return $"ClrMamePro: {cmp.Name} {cmp.Version}, {cmp.Games.Count} games";
    if (cmp.Games[1].Roms[0].Crc != "0000ABCD") return $"CRC read as {cmp.Games[1].Roms[0].Crc}";
    if (xml.Name != cmp.Name || xml.Games.Count != 1) return $"XML: {xml.Name}, {xml.Games.Count} games";
    return xml.Games[0].Roms[0] == cmp.Games[0].Roms[0] ? null : $"{xml.Games[0].Roms[0]} ≠ {cmp.Games[0].Roms[0]}";
});

Test("Hashing: CRC32 and SHA-1 of the standard check string, and NES without its header", () =>
{
    var dir = Dir("hashing");
    var check = Path.Combine(dir, "check.bin");
    File.WriteAllText(check, "123456789");
    var h = NoIntrovert.Core.Dats.Hasher.HashFile(check);
    if (h.Count != 1 || h[0].Crc != "CBF43926" || h[0].Sha1 != "F7C3BC1D808E04732ADF679965CCC34CA7AE3441") return $"got {h[0]}";
    var nes = Path.Combine(dir, "game.nes");
    var body = TextBytes(8192);
    File.WriteAllBytes(nes, [.. "NES\u001a"u8.ToArray(), .. new byte[12], .. body]);
    var both = NoIntrovert.Core.Dats.Hasher.HashFile(nes);
    var expected = System.IO.Hashing.Crc32.HashToUInt32(body).ToString("X8");
    return both.Count == 2 && both[1].Headerless && both[1].Size == 8192 && both[1].Crc == expected ? null : string.Join(" | ", both);
});

{
    // A made-up GitHub with three DATs; the Library wants two of them (acceptance 1-4).
    string DatText(string name, params (string Game, string Rom, byte[] Bytes)[] games)
    {
        var sb = new System.Text.StringBuilder($"clrmamepro (\n\tname \"{name}\"\n\tversion \"test\"\n)\n\n");
        foreach (var (game, rom, bytes) in games)
            sb.Append($"game (\n\tname \"{game}\"\n\trom ( name \"{rom}\" size {bytes.Length} crc {System.IO.Hashing.Crc32.HashToUInt32(bytes):X8} " +
                      $"sha1 {Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes))} )\n)\n");
        return sb.ToString();
    }
    const string nesKey = "no-intro/Nintendo - Nintendo Entertainment System", psxKey = "redump/Sony - PlayStation";
    var github = new FakeGitHub();
    github.Files[nesKey] = DatText("Nintendo - Nintendo Entertainment System", ("Old (USA)", "Old (USA).nes", [1, 2, 3]));
    github.Files[psxKey] = DatText("Sony - PlayStation", ("Old Disc (USA)", "Old Disc (USA).bin", [4, 5, 6]));
    github.Files["no-intro/Nintendo - Game Boy"] = DatText("Nintendo - Game Boy", ("Tetris (World)", "Tetris (World).gb", [7]));
    var updateStore = new NoIntrovert.Core.Dats.DatStore(Dir("dats-update"));
    var wanted = new[] { "nes-famicom", "playstation" }.SelectMany(id => TargetFormats.ById(id)!.Dats!).ToList();

    Test("DATs: the first update downloads only the Library's systems (M6 1)", () =>
    {
        var r = new NoIntrovert.Core.Dats.DatUpdater(updateStore, github).UpdateAsync(wanted).GetAwaiter().GetResult();
        if (r.Problem is not null) return r.Problem;
        var got = string.Join(", ", github.Downloads.Order());
        return got == $"{nesKey}, {psxKey}" && File.Exists(updateStore.PathOf(nesKey)) ? null : $"downloaded {got}";
    });

    Test("DATs: nothing changed upstream means one 304 per folder and no downloads (M6 2)", () =>
    {
        github.Downloads.Clear();
        var r = new NoIntrovert.Core.Dats.DatUpdater(updateStore, github).UpdateAsync(wanted).GetAwaiter().GetResult();
        return r.Problem is null && github.Downloads.Count == 0 && github.NotModified == 2 ? null : $"{github.NotModified} 304s, downloaded {string.Join(", ", github.Downloads)}";
    });

    Test("DATs: a changed DAT is the only one downloaded again (M6 3)", () =>
    {
        github.Downloads.Clear();
        github.Files[nesKey] = DatText("Nintendo - Nintendo Entertainment System", ("New (USA)", "New (USA).nes", [9, 9, 9]));
        var r = new NoIntrovert.Core.Dats.DatUpdater(updateStore, github).UpdateAsync(wanted).GetAwaiter().GetResult();
        if (r.Problem is not null || !github.Downloads.SequenceEqual([nesKey])) return $"downloaded {string.Join(", ", github.Downloads)}";
        return updateStore.Load([nesKey], null)[0].Games[0].Name == "New (USA)" ? null : "the cached copy wasn't replaced";
    });

    Test("DATs: offline, the cached copies are used and their age shown (M6 4)", () =>
    {
        github.Offline = true;
        var r = new NoIntrovert.Core.Dats.DatUpdater(updateStore, github).UpdateAsync(wanted).GetAwaiter().GetResult();
        github.Offline = false;
        var status = updateStore.Describe();
        return r.Problem is not null && updateStore.Load(wanted, null).Count == 2 && status.Contains("updated") && status.Contains("2 databases")
            ? Note(status) : $"problem {r.Problem ?? "none"}; {status}";
    });

    // ES-DE's system folders (#29): the bundled snapshot, and a made-up GitLab for the update check.
    Test("ES-DE systems: the bundled snapshot is parsed and has every folder the registry maps to", () =>
    {
        var snap = EsdeSystems.Snapshot;
        if (!snap.Ref.StartsWith('v') || snap.Commit.Length != 40 || snap.Systems.Count < 150) return $"{snap.Ref} {snap.Commit}: {snap.Systems.Count} systems";
        if (snap.Find("psx") is not { FullName: "Sony PlayStation" } psx || !psx.Extensions.Contains(".chd")) return "psx isn't as ES-DE has it";
        var problems = EsdeSystems.Problems(snap);
        return problems.Count == 0 ? Note($"{snap.Ref}, {snap.Systems.Count} systems") : string.Join("\n", problems);
    });

    Test("ES-DE systems: es_systems.xml is read as ES-DE writes it (names, full names, extensions in lower case)", () =>
    {
        var list = EsdeSystemList.Parse(FakeGitLab.XmlOf(EsdeSystems.Snapshot.Systems.Take(3)), "v1", "c1");
        var first = EsdeSystems.Snapshot.Systems[0];
        return list.Systems.Count == 3 && list.Systems[0].Name == first.Name && list.Systems[0].FullName == first.FullName
               && list.Systems[0].Extensions.SequenceEqual(first.Extensions)
            ? null : $"{list.Systems.Count} systems; {string.Join(" ", list.Systems[0].Extensions)}";
    });

    {
        var gitlab = new FakeGitLab { Tag = EsdeSystems.Snapshot.Ref, Commit = EsdeSystems.Snapshot.Commit, Xml = FakeGitLab.XmlOf(EsdeSystems.Snapshot.Systems) };
        var esdeStore = new EsdeStore(Dir("esde-update"));
        EsdeUpdateResult CheckEsde() => new EsdeUpdater(esdeStore, gitlab).UpdateAsync().GetAwaiter().GetResult();

        Test("ES-DE systems: offline on first run, the snapshot is used and the check stays due", () =>
        {
            gitlab.Offline = true;
            var r = CheckEsde();
            gitlab.Offline = false;
            var list = EsdeSystems.Load(esdeStore);
            return r.Problem is not null && ReferenceEquals(list, EsdeSystems.Snapshot) && esdeStore.LoadList() is null && new EsdeUpdater(esdeStore).IsDue(DateTime.UtcNow)
                   && EsdeSystems.Resolve(TargetFormats.ById("gamecube")!, list).Name == "gc"
                ? Note(esdeStore.Describe()) : $"problem {r.Problem ?? "none"}";
        });

        Test("ES-DE systems: the release the snapshot came from downloads nothing, then costs one 304 (unchanged)", () =>
        {
            var r = CheckEsde();
            if (r.Problem is not null || r.Downloaded || gitlab.Downloads + gitlab.Commits > 0) return $"{r.Problem}; {gitlab.Commits} commit checks, {gitlab.Downloads} downloads";
            if (new EsdeUpdater(esdeStore).IsDue(DateTime.UtcNow)) return "a check just made is due again";
            if (!new EsdeUpdater(esdeStore).IsDue(DateTime.UtcNow.AddDays(1.1))) return "a check over a day old isn't due";
            r = CheckEsde();
            return r.Problem is null && gitlab.NotModified == 1 && gitlab.Downloads == 0 && r.Ref == EsdeSystems.Snapshot.Ref ? null : $"{gitlab.NotModified} 304s, {gitlab.Downloads} downloads";
        });

        Test("ES-DE systems: a new release with the same es_systems.xml moves the ref without downloading", () =>
        {
            gitlab.Tag = "v98.0.0";
            var r = CheckEsde();
            var list = esdeStore.LoadList();
            return r.Problem is null && !r.Downloaded && gitlab.Downloads == 0 && list?.Ref == "v98.0.0" && list.Commit == EsdeSystems.Snapshot.Commit
                ? null : $"{r.Problem}; downloads {gitlab.Downloads}; cached {list?.Ref} {list?.Commit}";
        });

        Test("ES-DE systems: a changed es_systems.xml is downloaded and cached with its release and commit (changed)", () =>
        {
            gitlab.Tag = "v99.0.0";
            gitlab.Commit = new string('a', 40);
            gitlab.Xml = FakeGitLab.XmlOf([.. EsdeSystems.Snapshot.Systems, new EsdeSystem("newsystem", "A New System", [".new"])]);
            var r = CheckEsde();
            var list = esdeStore.LoadList();
            if (r.Problem is not null || !r.Downloaded || gitlab.Downloads != 1) return $"{r.Problem}; downloads {gitlab.Downloads}";
            if (list?.Ref != "v99.0.0" || list.Commit != gitlab.Commit || list.Find("newsystem") is null) return $"cached {list?.Ref} {list?.Commit}";
            r = CheckEsde();
            return r.Problem is null && !r.Downloaded && gitlab.Downloads == 1 ? Note(esdeStore.Describe()) : $"downloaded again ({gitlab.Downloads})";
        });

        Test("ES-DE systems: offline later, the cache is kept and the last check time isn't moved (offline)", () =>
        {
            var before = esdeStore.LoadState().LastCheckedUtc;
            gitlab.Offline = true;
            var r = CheckEsde();
            gitlab.Offline = false;
            return r.Problem is not null && esdeStore.LoadList()?.Ref == "v99.0.0" && esdeStore.LoadState().LastCheckedUtc == before ? null : $"problem {r.Problem ?? "none"}";
        });

        Test("ES-DE systems: a renamed or removed system is reported, falls back to the snapshot name, and is never invented", () =>
        {
            gitlab.Tag = "v100.0.0";
            gitlab.Commit = new string('b', 40);
            gitlab.Xml = FakeGitLab.XmlOf(EsdeSystems.Snapshot.Systems
                .Where(s => s.Name != "wiiu")
                .Select(s => s.Name == "gc" ? s with { Name = "gamecube" } : s));
            var r = CheckEsde();
            var list = esdeStore.LoadList()!;
            var gc = EsdeSystems.Resolve(TargetFormats.ById("gamecube")!, list);
            var wiiu = EsdeSystems.Resolve(TargetFormats.ById("wii-u")!, list);
            if (r.Problem is not null || gc.Name != "gc" || wiiu.Name != "wiiu") return $"{r.Problem}; gc → {gc.Name}, wiiu → {wiiu.Name}";
            if (r.Missing.Count != 2 || !r.Missing.Any(m => m.Contains("\"gc\"") && m.Contains("renamed to \"gamecube\""))
                || !r.Missing.Any(m => m.Contains("\"wiiu\"") && !m.Contains("renamed"))) return string.Join("\n", r.Missing);
            // Neither list has it: the games stay where they are.
            var empty = new EsdeSystemList { Ref = "v0", Systems = [] };
            var lost = EsdeSystems.Resolve(TargetFormats.ById("gamecube")!, list, empty);
            return lost.Name is null && lost.Problem!.Contains("stay where they are") ? Note(string.Join(" | ", r.Missing)) : $"{lost.Name}: {lost.Problem}";
        });
    }

    // Matching (acceptance 5-12): a DAT store with made-up NES and PS1 entries.
    var lib = Dir("m6-library");
    var rom = TextBytes(32 * 1024);
    byte[] nesFile = [.. "NES\u001a"u8.ToArray(), 2, 1, .. new byte[10], .. rom];
    var otherRom = TextBytes(16 * 1024).Reverse().ToArray();
    Directory.CreateDirectory(Path.Combine(lib, "NES"));
    Directory.CreateDirectory(Path.Combine(lib, "PS1"));
    File.WriteAllBytes(Path.Combine(lib, "NES", "Game (USA).nes"), nesFile);
    File.WriteAllBytes(Path.Combine(lib, "NES", "Wrong.nes"), nesFile);
    File.WriteAllBytes(Path.Combine(lib, "NES", "Other (Japan).nes"), [.. "NES\u001a"u8.ToArray(), 1, 1, .. new byte[10], .. otherRom]);
    File.WriteAllBytes(Path.Combine(lib, "NES", "Unknown.nes"), [.. "NES\u001a"u8.ToArray(), .. new byte[12], .. TextBytes(4096).Select(b => (byte)(b ^ 0x5A))]);
    using (var z = ZipFile.Open(Path.Combine(lib, "NES", "Game (USA).zip"), ZipArchiveMode.Create))
        using (var s = z.CreateEntry("Game (USA).nes").Open()) s.Write(nesFile);
    // A three-track disc whose first track is what libretro's Redump DAT lists.
    var trackSpans = new[] { (0L, (long)DataSectors), (DataSectors, 150L), (DataSectors + 150L, 150L) };
    var track1 = new byte[DataSectors * 2352];
    for (var t = 0; t < 3; t++)
        using (var o = File.Create(Path.Combine(lib, "PS1", $"Game (USA) (Track {t + 1}).bin")))
            CopyRange(synthBin, o, trackSpans[t].Item1 * 2352, trackSpans[t].Item2 * 2352);
    using (var s = File.OpenRead(Path.Combine(lib, "PS1", "Game (USA) (Track 1).bin"))) s.ReadExactly(track1);
    File.WriteAllText(Path.Combine(lib, "PS1", "Game (USA).cue"),
        "FILE \"Game (USA) (Track 1).bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n" +
        "FILE \"Game (USA) (Track 2).bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 01 00:00:00\n" +
        "FILE \"Game (USA) (Track 3).bin\" BINARY\n  TRACK 03 AUDIO\n    INDEX 01 00:00:00\n");

    var matchStore = new NoIntrovert.Core.Dats.DatStore(Dir("dats-match"));
    Directory.CreateDirectory(Path.Combine(matchStore.Folder, "no-intro"));
    Directory.CreateDirectory(Path.Combine(matchStore.Folder, "redump"));
    File.WriteAllText(matchStore.PathOf(nesKey), DatText("Nintendo - Nintendo Entertainment System",
        ("Game (USA)", "Game (USA).nes", nesFile), ("Other (Japan)", "Other (Japan).unh", otherRom)));
    File.WriteAllText(matchStore.PathOf(psxKey), DatText("Sony - PlayStation", ("Game (USA)", "Game (USA) (Track 01).bin", track1)));

    // One Verify & match job, with the result it stored.
    (JobOutcome Outcome, NoIntrovert.Core.Dats.MatchResult? Result) Check(string rel, Action<AppSettings>? configure = null)
    {
        var path = Path.Combine(lib, rel);
        var o = RunJob(c => NoIntrovert.Core.Dats.VerifyJobs.Check(c, path, matchStore,
            NoIntrovert.Core.Dats.HashCache.Default, NoIntrovert.Core.Dats.MatchStore.Default), configure);
        return (o, NoIntrovert.Core.Dats.MatchStore.Default.Get(path));
    }

    Test("Verify & match: ROMs are verified, misnamed, matched without their header, or not found (M6 5, 6, 8)", () =>
    {
        var cases = new (string Rel, NoIntrovert.Core.Dats.MatchStatus Status, string? Expected)[]
        {
            ("NES/Game (USA).nes", NoIntrovert.Core.Dats.MatchStatus.Verified, "Game (USA).nes"),
            ("NES/Wrong.nes", NoIntrovert.Core.Dats.MatchStatus.NameDiffers, "Game (USA).nes"),
            ("NES/Other (Japan).nes", NoIntrovert.Core.Dats.MatchStatus.Verified, "Other (Japan).nes"), // matched on the headerless entry
            ("NES/Unknown.nes", NoIntrovert.Core.Dats.MatchStatus.NotInDatabase, null),
        };
        foreach (var (rel, status, expected) in cases)
        {
            var (o, r) = Check(rel);
            if (r is null || r.Status != status || r.ExpectedName != expected) return $"{rel}: {r?.Status} {r?.ExpectedName} ({o.Status}: {o.Summary})";
        }
        return null;
    });

    Test("Verify & match: a zipped ROM is verified from the zip's own CRC32 (M6 7)", () =>
    {
        var (o, r) = Check("NES/Game (USA).zip");
        return r?.Status == NoIntrovert.Core.Dats.MatchStatus.Verified && o.Summary.Contains("CRC32") ? Note(o.Summary) : $"{o.Status}: {o.Summary}";
    });

    Test("Verify & match: a cue/bin and the CHD made from it match Redump's first track (M6 9)", () =>
    {
        var (o, r) = Check("PS1/Game (USA).cue");
        if (r?.Status != NoIntrovert.Core.Dats.MatchStatus.Verified) return $"cue/bin: {o.Status}: {o.Summary}";
        var made = RunJob(c => ChdJobs.Convert(c, Path.Combine(lib, "PS1", "Game (USA).cue")));
        if (made.Status != JobStatus.Succeeded) return $"making the CHD: {made.Summary}";
        (o, r) = Check("PS1/Game (USA).chd");
        return r?.Status == NoIntrovert.Core.Dats.MatchStatus.Verified ? null : $"CHD: {o.Status}: {o.Summary}";
    });

    Test("Verify & match: a damaged CHD and a damaged 7z are reported as damaged (M6 10)", () =>
    {
        var chd = Path.Combine(lib, "PS1", "Broken.chd");
        var bytes = File.ReadAllBytes(Path.Combine(lib, "PS1", "Game (USA).chd"));
        for (var i = bytes.Length / 2; i < bytes.Length / 2 + 64; i++) bytes[i] ^= 0xFF;
        File.WriteAllBytes(chd, bytes);
        var seven = Path.Combine(lib, "NES", "Broken.7z");
        using (var engine = new SevenZipEngine())
            engine.Compress(ArchiveFormat.SevenZip, ArchiveLevel.Normal, 0, false, [Path.Combine(lib, "NES", "Game (USA).nes")], seven, false, null, CancellationToken.None);
        var packed = File.ReadAllBytes(seven);
        for (var i = 64; i < 96; i++) packed[i] ^= 0xFF;
        File.WriteAllBytes(seven, packed);
        foreach (var rel in new[] { "PS1/Broken.chd", "NES/Broken.7z" })
        {
            var (o, r) = Check(rel);
            if (o.Status != JobStatus.Failed || r?.Status != NoIntrovert.Core.Dats.MatchStatus.Damaged) return $"{rel}: {o.Status}: {o.Summary}";
        }
        return null;
    });

    Test("Verify & match: an unchanged file isn't hashed again (M6 11)", () =>
    {
        var cache = NoIntrovert.Core.Dats.HashCache.Default;
        var before = cache.Hits;
        Check("NES/Game (USA).nes");
        return cache.Hits == before + 1 ? null : $"{cache.Hits - before} cache hits";
    });

    Test("Verify & match: a DAT in your own folder wins over the downloaded one (M6 12)", () =>
    {
        var own = Dir("own-dats");
        File.WriteAllText(Path.Combine(own, "my nes.dat"), DatText("Nintendo - Nintendo Entertainment System", ("Own Name (USA)", "Own Name (USA).nes", nesFile)));
        var (o, r) = Check("NES/Game (USA).nes", s => s.Dats.OwnFolder = own);
        return r?.Game == "Own Name (USA)" && r.Status == NoIntrovert.Core.Dats.MatchStatus.NameDiffers ? null : $"{o.Status}: {o.Summary}";
    });
}

// ---- Cleanup wizard (M7) ------------------------------------------------------

if (SevenZipEngine.IsAvailable(out _))
{
    var games7 = Dir("m7/Games");
    string G7(string rel) => Path.Combine([games7, .. rel.Split('/')]);
    string Dat7(string name, params (string Game, string Rom, byte[] Bytes)[] games)
    {
        var sb = new System.Text.StringBuilder($"clrmamepro (\n\tname \"{name}\"\n)\n");
        foreach (var (game, rom, bytes) in games)
            sb.Append($"game (\n\tname \"{game}\"\n\trom ( name \"{rom}\" size {bytes.Length} crc {System.IO.Hashing.Crc32.HashToUInt32(bytes):X8} " +
                      $"sha1 {Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes))} )\n)\n");
        return sb.ToString();
    }
    // A disc: a copy of the synthetic PS1 bin, made different by its last byte.
    byte[] Disc(int n) { var b = File.ReadAllBytes(synthBin); b[^1] = (byte)n; return b; }
    void PutDisc(string rel, byte[] bin)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(G7(rel))!);
        File.WriteAllBytes(G7(rel) + ".bin", bin);
        File.WriteAllText(G7(rel) + ".cue", File.ReadAllText(synthCue).Replace("synth.bin", Path.GetFileName(rel) + ".bin"));
    }
    byte[] Nes(int seed) => [.. "NES\u001a"u8.ToArray(), 2, 1, .. new byte[10], .. TextBytes(16384 + seed)];

    PutDisc("PS1/Crash Bandicoot", Disc(1));
    for (var d = 1; d <= 3; d++) PutDisc($"PS1/Epic (USA) (Disc {d})", Disc(10 + d));
    var mario = Nes(1); var zelda = Nes(2); var metroid = Nes(3);
    Directory.CreateDirectory(G7("NES"));
    File.WriteAllBytes(G7("NES/Mario.nes"), mario);
    File.WriteAllBytes(G7("NES/Zelda.nes"), zelda);
    File.WriteAllBytes(G7("NES/Unknown.nes"), Nes(99));
    Directory.CreateDirectory(G7("roms/nes"));
    File.WriteAllBytes(G7("roms/nes/Zelda (USA).7z"), [1, 2, 3]); // in the way of Zelda's target: a clash
    Directory.CreateDirectory(G7("Misc"));
    File.WriteAllBytes(G7("Misc/loose.bin"), new byte[4096]);
    using (var engine = new SevenZipEngine())
    {
        var src = Path.Combine(Dir("m7-src"), "Metroid.nes");
        File.WriteAllBytes(src, metroid);
        engine.Compress(ArchiveFormat.SevenZip, ArchiveLevel.Normal, 0, false, [src], G7("roms/nes/Wrong Name.7z"), false, null, CancellationToken.None);
    }

    var store7 = new NoIntrovert.Core.Dats.DatStore(Dir("m7-dats"));
    Directory.CreateDirectory(Path.Combine(store7.Folder, "no-intro"));
    Directory.CreateDirectory(Path.Combine(store7.Folder, "redump"));
    File.WriteAllText(store7.PathOf("no-intro/Nintendo - Nintendo Entertainment System"), Dat7("Nintendo - Nintendo Entertainment System",
        ("Super Mario Bros. (World)", "Super Mario Bros. (World).nes", mario), ("Zelda (USA)", "Zelda (USA).nes", zelda),
        ("Metroid (USA)", "Metroid (USA).nes", metroid)));
    File.WriteAllText(store7.PathOf("redump/Sony - PlayStation"), Dat7("Sony - PlayStation",
        [("Crash Bandicoot (USA)", "Crash Bandicoot (USA).bin", Disc(1)), .. Enumerable.Range(1, 3).Select(d => ($"Epic (USA) (Disc {d})", $"Epic (USA) (Disc {d}).bin", Disc(10 + d)))]));

    // Scans the Library and checks every file in it against the made-up DATs, as Verify & match would.
    List<NoIntrovert.Core.Library.Game> ScanAndVerify(AppSettings s, string? root = null)
    {
        var index = NoIntrovert.Core.Library.LibraryScanner.Scan([root ?? games7], null, null, CancellationToken.None, s);
        foreach (var item in index.Items)
            RunJob(c => NoIntrovert.Core.Dats.VerifyJobs.Check(c, item.Path, store7, NoIntrovert.Core.Dats.HashCache.Default, NoIntrovert.Core.Dats.MatchStore.Default));
        return index.Games();
    }
    NoIntrovert.Core.Cleanup.CleanupPlan PlanFor(LibraryLayout layout)
    {
        var s = new AppSettings { Library = { Layout = layout } };
        return NoIntrovert.Core.Cleanup.CleanupPlanner.Plan(ScanAndVerify(s), s, NoIntrovert.Core.Dats.MatchStore.Default);
    }
    string Rel(string? path) => path is null ? "" : Path.GetRelativePath(games7, path).Replace('\\', '/');
    string Describe(NoIntrovert.Core.Cleanup.CleanupPlan plan) =>
        string.Join("\n", plan.Steps.Select(s => $"{s.Kind} {s.Game}: {string.Join(",", s.Sources.Take(1).Select(Rel))} → {Rel(s.Target)} ({s.Reason})"));
    NoIntrovert.Core.Cleanup.CleanupStep? Find(NoIntrovert.Core.Cleanup.CleanupPlan plan, string game) =>
        plan.Steps.FirstOrDefault(s => s.Game == game);
    // Paths compared as the OS does: on Windows and macOS, NES/ and ES-DE's nes/ are one folder; on Linux, two.
    HashSet<string> Tree() => Directory.EnumerateFileSystemEntries(games7, "*", SearchOption.AllDirectories).Select(Rel).ToHashSet(PathNames.Comparer);
    NoIntrovert.Core.Cleanup.CleanupRun RunPlan(NoIntrovert.Core.Cleanup.CleanupPlan plan)
    {
        var runner = new NoIntrovert.Core.Cleanup.CleanupRunner();
        foreach (var (name, _, work) in runner.Jobs(plan))
            if (RunJob(work) is { Status: not JobStatus.Succeeded } o) throw new InvalidOperationException($"{name}: {o.Summary}");
        return runner.Run;
    }
    NoIntrovert.Core.Cleanup.CleanupPlan? esdePlan = null;

    Test("Cleanup: the layout setting defaults to ES-DE format", () =>
        new AppSettings().Library.Layout == LibraryLayout.EsDe
        && System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{"SchemaVersion":2,"Library":{"Folders":[]}}""")!.Library.Layout == LibraryLayout.EsDe ? null : "the default isn't ES-DE");

    Test("Cleanup: ES-DE system folders go in roms/, or straight in a Library folder called roms (FR-6)", () =>
    {
        var lib = Path.Combine(Path.GetTempPath(), "Lib");
        var a = NoIntrovert.Core.Cleanup.CleanupPlanner.RomsFolder(lib);
        var b = NoIntrovert.Core.Cleanup.CleanupPlanner.RomsFolder(Path.Combine(lib, "ROMs") + Path.DirectorySeparatorChar);
        if (a != Path.Combine(lib, "roms") || b != Path.Combine(lib, "ROMs")) return $"{a} / {b}";
        // ES-DE's own names (USERGUIDE.md, Supported game systems), lower-case as ES-DE expects them.
        var names = new Dictionary<string, string>
        {
            ["nes-famicom"] = "nes", ["snes-super-famicom"] = "snes", ["playstation"] = "psx", ["saturn"] = "saturn",
            ["dreamcast"] = "dreamcast", ["gamecube"] = "gc", ["wii"] = "wii", ["wii-u"] = "wiiu", ["playstation-portable"] = "psp",
            ["playstation-2"] = "ps2", ["playstation-3"] = "ps3", ["mega-drive-genesis"] = "megadrive", ["sg-1000-sc-3000"] = "sg-1000",
        };
        var wrong = names.Where(n => NoIntrovert.Core.Formats.TargetFormats.ById(n.Key)?.Esde != n.Value).Select(n => n.Key).ToList();
        var upper = NoIntrovert.Core.Formats.TargetFormats.All.Where(t => t.Esde is { } e && e != e.ToLowerInvariant()).Select(t => t.Id).ToList();
        return wrong.Count + upper.Count == 0 ? null : $"wrong: {string.Join(", ", wrong)}; not lower-case: {string.Join(", ", upper)}";
    });

    Test("Cleanup: the plan converts, renames and moves into ES-DE folders, and leaves the rest (M7 1, 3, 4, 6, 7)", () =>
    {
        var plan = esdePlan = PlanFor(LibraryLayout.EsDe);
        string? Check(string game, NoIntrovert.Core.Cleanup.CleanupKind kind, string? target)
        {
            var s = Find(plan, game);
            return s is not null && s.Kind == kind && Rel(s.Target) == (target ?? "") ? null : $"{game}: expected {kind} → {target}\n{Describe(plan)}";
        }
        return Check("Crash Bandicoot", NoIntrovert.Core.Cleanup.CleanupKind.Convert, "roms/psx/Crash Bandicoot (USA).chd")
            ?? Check("Mario", NoIntrovert.Core.Cleanup.CleanupKind.Convert, "roms/nes/Super Mario Bros. (World).7z")
            ?? Check("Epic (USA)", NoIntrovert.Core.Cleanup.CleanupKind.Convert, "roms/psx/Epic (USA).m3u")
            ?? Check("Zelda", NoIntrovert.Core.Cleanup.CleanupKind.Clash, "roms/nes/Zelda (USA).7z")
            ?? Check("Wrong Name", NoIntrovert.Core.Cleanup.CleanupKind.Rename, "roms/nes/Metroid (USA).7z")
            ?? Check("Unknown", NoIntrovert.Core.Cleanup.CleanupKind.Untouched, null)
            ?? Check("loose", NoIntrovert.Core.Cleanup.CleanupKind.Untouched, null);
    });

    Test("Cleanup: a system ES-DE renamed upstream is reported and still filed under the snapshot's folder (#29)", () =>
    {
        // The cache says ES-DE now calls psx "playstation"; the plan must not invent that folder or lose psx.
        var store = EsdeStore.Default;
        var renamed = EsdeSystems.Snapshot.Systems.Select(s => s.Name == "psx" ? s with { Name = "playstation" } : s).ToList();
        store.SaveList(new EsdeSystemList { Ref = "v99.0.0", Commit = "renamed", Systems = renamed });
        EsdeSystems.Reload();
        try
        {
            var plan = PlanFor(LibraryLayout.EsDe);
            var status = store.Describe();
            if (Rel(Find(plan, "Crash Bandicoot")?.Target) != "roms/psx/Crash Bandicoot (USA).chd") return Describe(plan);
            return status.Contains("v99.0.0") && status.Contains("no \"psx\" folder") && status.Contains("renamed to \"playstation\"")
                ? Note(status) : status;
        }
        finally
        {
            File.Delete(Path.Combine(store.Folder, "es_systems.json"));
            EsdeSystems.Reload();
        }
    });

    Test("Cleanup: keeping the current layout only converts and renames, in place (M7 2)", () =>
    {
        var plan = PlanFor(LibraryLayout.InPlace);
        var crash = Find(plan, "Crash Bandicoot");
        if (Rel(crash?.Target) != "PS1/Crash Bandicoot (USA).chd" || Rel(Find(plan, "Mario")?.Target) != "NES/Super Mario Bros. (World).7z")
            return Describe(plan);
        if (Rel(Find(plan, "Epic (USA)")?.Target) != "PS1/Epic (USA).m3u") return Describe(plan);
        // Nothing changes folder: every target is beside its source.
        var moved = plan.Steps.Where(st => st.Target is not null && Path.GetDirectoryName(st.Target) != Path.GetDirectoryName(st.Sources[0])).ToList();
        return moved.Count == 0 && plan.Count(NoIntrovert.Core.Cleanup.CleanupKind.Move) == 0 ? null : Describe(plan);
    });

    Test("Cleanup: a dry run writes nothing (M7 8)", () =>
    {
        var before = Tree();
        PlanFor(LibraryLayout.EsDe);
        var after = Tree();
        return before.SetEquals(after) ? null : $"changed: {string.Join(", ", after.Except(before).Concat(before.Except(after)))}";
    });

    // FR-8: the same plan, built as a new library in another folder; the Library itself must not change at all.
    var newLib = Dir("m7-new/Library");
    string RelN(string? path) => path is null ? "" : Path.GetRelativePath(newLib, path).Replace('\\', '/');
    HashSet<string> TreeN() => Directory.EnumerateFileSystemEntries(newLib, "*", SearchOption.AllDirectories).Select(RelN).ToHashSet(PathNames.Comparer);
    // The Library's every file with its size and date, so even a rename-and-back shows.
    Dictionary<string, (long, DateTime)> Stamped() => Directory.EnumerateFiles(games7, "*", SearchOption.AllDirectories)
        .ToDictionary(Rel, f => (new FileInfo(f).Length, File.GetLastWriteTimeUtc(f)), PathNames.Comparer);
    string Same(Dictionary<string, (long, DateTime)> a, Dictionary<string, (long, DateTime)> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value) ? ""
            : $"the Library changed: {string.Join(", ", a.Keys.Except(b.Keys).Concat(b.Keys.Except(a.Keys)).Concat(a.Keys.Where(k => b.TryGetValue(k, out var v) && v != a[k])))}";
    NoIntrovert.Core.Cleanup.CleanupPlan PlanNew(bool copyUnmatched = true)
    {
        var s = new AppSettings { Library = { Layout = LibraryLayout.EsDe } };
        return NoIntrovert.Core.Cleanup.CleanupPlanner.Plan(ScanAndVerify(s), s, NoIntrovert.Core.Dats.MatchStore.Default, newLib, copyUnmatched);
    }
    string DescribeN(NoIntrovert.Core.Cleanup.CleanupPlan plan) =>
        string.Join("\n", plan.Steps.Select(st => $"{st.Kind} {st.Game}: {string.Join(",", st.Sources.Take(1).Select(Rel))} → {RelN(st.Target)} ({st.Reason})"));
    NoIntrovert.Core.Cleanup.CleanupPlan? newPlan = null;

    Test("Cleanup: a new library plans every game into the other folder, and copies or leaves out the rest (FR-8)", () =>
    {
        var plan = newPlan = PlanNew();
        string? Check(string game, NoIntrovert.Core.Cleanup.CleanupKind kind, string? target)
        {
            var st = Find(plan, game);
            return st is not null && st.Kind == kind && RelN(st.Target) == (target ?? "") && st.Destination is { } d && PathNames.Same(d, newLib) ? null : $"{game}: expected {kind} → {target}\n{DescribeN(plan)}";
        }
        if (plan.Steps.Any(st => st.Kind is NoIntrovert.Core.Cleanup.CleanupKind.Move or NoIntrovert.Core.Cleanup.CleanupKind.Rename or NoIntrovert.Core.Cleanup.CleanupKind.RemoveDuplicate))
            return $"a new library's plan moves, renames or removes in the Library\n{DescribeN(plan)}";
        if (plan.Steps.Any(st => st.Target is { } t && !RelN(t).StartsWith("roms/") && !RelN(t).StartsWith("Misc/")))
            return $"a target outside the new library\n{DescribeN(plan)}";
        var leftOut = PlanNew(copyUnmatched: false);
        if (Find(leftOut, "Unknown") is not { Kind: NoIntrovert.Core.Cleanup.CleanupKind.Untouched } u || !u.Reason.EndsWith("not copied"))
            return $"with Copy unmatched off, Unknown should be left out\n{DescribeN(leftOut)}";
        return Check("Crash Bandicoot", NoIntrovert.Core.Cleanup.CleanupKind.Convert, "roms/psx/Crash Bandicoot (USA).chd")
            ?? Check("Mario", NoIntrovert.Core.Cleanup.CleanupKind.Convert, "roms/nes/Super Mario Bros. (World).7z")
            ?? Check("Epic (USA)", NoIntrovert.Core.Cleanup.CleanupKind.Convert, "roms/psx/Epic (USA).m3u")
            ?? Check("Wrong Name", NoIntrovert.Core.Cleanup.CleanupKind.Copy, "roms/nes/Metroid (USA).7z")
            ?? Check("Unknown", NoIntrovert.Core.Cleanup.CleanupKind.Copy, "roms/nes/Unknown.nes")
            ?? Check("loose", NoIntrovert.Core.Cleanup.CleanupKind.Copy, "Misc/loose.bin");
    });

    NoIntrovert.Core.Cleanup.CleanupRun? newRun = null;
    Test("Cleanup: running a new library's plan fills the other folder and leaves the Library as it was (FR-8)", () =>
    {
        var before = Stamped();
        // Run only what can run: the junk "Zelda (USA).7z" and Zelda's conversion want the same name, so one is a clash.
        newRun = RunPlan(newPlan!);
        if (Same(before, Stamped()) is { Length: > 0 } changed) return changed;
        var tree = TreeN();
        var expected = new[]
        {
            "roms/psx/Crash Bandicoot (USA).chd", "roms/nes/Super Mario Bros. (World).7z", "roms/nes/Metroid (USA).7z",
            "roms/psx/Epic (USA).m3u", "roms/psx/hidden/Epic (USA) (Disc 1).chd", "roms/psx/hidden/Epic (USA) (Disc 3).chd",
            "roms/psx/hidden/noload.txt", "roms/nes/Unknown.nes", "Misc/loose.bin",
        };
        if (expected.FirstOrDefault(e => !tree.Contains(e)) is { } missing) return $"missing {missing}:\n{string.Join("\n", tree.Order())}";
        if (tree.FirstOrDefault(t => t.Contains(".noi-")) is { } leftover) return $"left behind: {leftover}";
        if (!File.ReadAllBytes(Path.Combine(newLib, "roms", "nes", "Unknown.nes")).SequenceEqual(File.ReadAllBytes(G7("NES/Unknown.nes")))) return "a copy differs from its original";
        var m3u = File.ReadAllLines(Path.Combine(newLib, "roms", "psx", "Epic (USA).m3u"));
        if (!m3u.SequenceEqual(Enumerable.Range(1, 3).Select(d => $"hidden/Epic (USA) (Disc {d}).chd"))) return string.Join(" | ", m3u);
        return newRun.NewLibrary is { } nl && PathNames.Same(nl, newLib) && newRun.Entries.All(e => e.Action == NoIntrovert.Core.Cleanup.JournalAction.Created) ? null : "the journal isn't a new library's";
    });

    Test("Cleanup: making the plan again for the same new library shows clashes, not copies over what's there (FR-5, FR-8)", () =>
    {
        var plan = PlanNew();
        var crash = Find(plan, "Crash Bandicoot");
        return crash?.Kind == NoIntrovert.Core.Cleanup.CleanupKind.Clash && Find(plan, "Unknown")?.Kind == NoIntrovert.Core.Cleanup.CleanupKind.Clash ? null : DescribeN(plan);
    });

    Test("Cleanup: undoing a new library's run removes what it made and nothing in the Library (FR-8)", () =>
    {
        var before = Stamped();
        var problems = NoIntrovert.Core.Cleanup.CleanupRunner.Undo(newRun!, useBin: false, log);
        if (problems.Count > 0) return string.Join(" | ", problems);
        if (Same(before, Stamped()) is { Length: > 0 } changed) return changed;
        var files = Directory.EnumerateFiles(newLib, "*", SearchOption.AllDirectories).Select(RelN).ToList();
        return files.Count == 0 ? null : $"still there: {string.Join(", ", files)}";
    });

    Test("Cleanup: a new library can't be inside a Library folder or hold one (FR-8)", () =>
    {
        var lib = Path.Combine(Path.GetTempPath(), "Lib");
        string? P(string folder) => NoIntrovert.ViewModels.CleanupViewModel.NewLibraryProblem(folder, [lib]);
        return P("") is not null && P(Path.Combine(lib, "new")) is not null && P(Path.GetTempPath()) is not null
               && P(lib) is not null && P(Path.Combine(Path.GetTempPath(), "Lib2")) is null && P(Path.Combine(Path.GetTempPath(), "New")) is null
            ? null : "wrong answer for one of the folders";
    });

    NoIntrovert.Core.Cleanup.CleanupRun? firstRun = null;
    Test("Cleanup: running the plan gives the ES-DE layout, multi-disc playlist and trash (M7 1, 3, 4)", () =>
    {
        firstRun = RunPlan(esdePlan!);
        var tree = Tree();
        var expected = new[]
        {
            "roms/psx/Crash Bandicoot (USA).chd", "roms/nes/Super Mario Bros. (World).7z", "roms/nes/Metroid (USA).7z",
            "roms/psx/Epic (USA).m3u", "roms/psx/hidden/Epic (USA) (Disc 1).chd", "roms/psx/hidden/Epic (USA) (Disc 3).chd",
            "roms/psx/hidden/noload.txt", "NES/Zelda.nes", "NES/Unknown.nes", "Misc/loose.bin",
        };
        if (expected.FirstOrDefault(e => !tree.Contains(e)) is { } missing) return $"missing {missing}:\n{string.Join("\n", tree.Order())}";
        if (tree.Any(t => t.StartsWith("PS1/") && !t.StartsWith("PS1/.")) || tree.Contains("NES/Mario.nes")) return $"originals left behind:\n{string.Join("\n", tree.Order())}";
        if (!tree.Any(t => t.StartsWith(".noi-trash/") && t.EndsWith("Crash Bandicoot.bin"))) return "the originals aren't in the trash";
        if (!File.Exists(G7("roms/psx/Epic (USA).m3u"))) return "the playlist isn't a file in roms/psx/";
        if (new FileInfo(G7("roms/psx/hidden/noload.txt")).Length != 0) return "noload.txt isn't empty";
        var m3u = File.ReadAllLines(G7("roms/psx/Epic (USA).m3u"));
        if (!m3u.SequenceEqual(Enumerable.Range(1, 3).Select(d => $"hidden/Epic (USA) (Disc {d}).chd"))) return string.Join(" | ", m3u);
        // The only folder at the game level is hidden/; everything else in a system folder is a file.
        var gameFolders = tree.Where(t => t.StartsWith("roms/") && t.Count(ch => ch == '/') == 2 && Directory.Exists(G7(t))).ToList();
        return gameFolders.SequenceEqual(["roms/psx/hidden"]) ? null : string.Join(", ", gameFolders);
    });

    // A second Library for the folder exceptions: Wii U titles and PS3 games stay folders; an old hidden/ playlist moves.
    var games7b = Dir("m7b/Library");
    string G7b(string rel) => Path.Combine([games7b, .. rel.Split('/')]);
    string RelB(string? path) => path is null ? "" : Path.GetRelativePath(games7b, path).Replace('\\', '/');
    HashSet<string> TreeB() => Directory.EnumerateFileSystemEntries(games7b, "*", SearchOption.AllDirectories).Select(RelB).ToHashSet(PathNames.Comparer);
    NoIntrovert.Core.Cleanup.CleanupPlan PlanB(LibraryLayout layout)
    {
        var s = new AppSettings { Library = { Layout = layout } };
        return NoIntrovert.Core.Cleanup.CleanupPlanner.Plan(ScanAndVerify(s, games7b), s, NoIntrovert.Core.Dats.MatchStore.Default);
    }
    string DescribeB(NoIntrovert.Core.Cleanup.CleanupPlan plan) =>
        string.Join("\n", plan.Steps.Select(st => $"{st.Kind} {st.Game}: {string.Join(",", st.Sources.Take(1).Select(RelB))} → {RelB(st.Target)} ({st.Reason})"));
    NoIntrovert.Core.Cleanup.CleanupRun? layoutRun = null;

    Test("Cleanup: folder games stay folders and move whole into roms/<system>/ (FR-6)", () =>
    {
        Directory.CreateDirectory(G7b("Wii U/Some NUS Title"));
        File.WriteAllBytes(G7b("Wii U/Some NUS Title/title.tmd"), [0]);
        File.WriteAllBytes(G7b("Wii U/Some NUS Title/title.tik"), [0]);
        File.WriteAllBytes(G7b("Wii U/Some NUS Title/00000000.app"), new byte[64]);
        WiiUSample.MakeTitle(G7b("Wii U/Extracted/Some Game"), 0x0005000010199900, 0, "Some Game", 3);
        Directory.CreateDirectory(G7b("PS3/Gran Turismo 5/PS3_GAME/USRDIR"));
        File.WriteAllBytes(G7b("PS3/Gran Turismo 5/PS3_GAME/PARAM.SFO"), new byte[64]);
        File.WriteAllBytes(G7b("PS3/Gran Turismo 5/PS3_GAME/USRDIR/EBOOT.BIN"), new byte[64]);
        Directory.CreateDirectory(G7b("Games/Demo.ps3"));
        File.WriteAllBytes(G7b("Games/Demo.ps3/PS3_DISC.SFB"), new byte[16]);
        Directory.CreateDirectory(G7b("roms/ps3/Already.ps3/PS3_GAME"));
        File.WriteAllBytes(G7b("roms/ps3/Already.ps3/PS3_GAME/PARAM.SFO"), new byte[64]);
        // Converted discs in the old layout: hidden/ beside the playlist.
        Directory.CreateDirectory(G7b("PS1/hidden"));
        for (var d = 1; d <= 3; d++)
        {
            var name = $"Epic (USA) (Disc {d}).chd";
            File.Copy(G7($"roms/psx/hidden/{name}"), G7b($"PS1/hidden/{name}"));
            if (NoIntrovert.Core.Dats.OriginalHashes.Default.Get(G7($"roms/psx/hidden/{name}")) is { } o)
                NoIntrovert.Core.Dats.OriginalHashes.Default.Set(G7b($"PS1/hidden/{name}"), o.Source, o.Files);
        }
        File.WriteAllText(G7b("PS1/Epic (USA).m3u"), string.Join("\n", Enumerable.Range(1, 3).Select(d => $"hidden/Epic (USA) (Disc {d}).chd")) + "\n");

        var plan = PlanB(LibraryLayout.EsDe);
        var expected = new Dictionary<string, string>
        {
            ["Wii U/Some NUS Title"] = "roms/wiiu/Some NUS Title",
            ["Wii U/Extracted/Some Game"] = "roms/wiiu/Some Game",
            ["PS3/Gran Turismo 5"] = "roms/ps3/Gran Turismo 5.ps3",
            ["Games/Demo.ps3"] = "roms/ps3/Demo.ps3",
        };
        foreach (var (from, to) in expected)
        {
            var step = plan.Steps.FirstOrDefault(st => st.Sources.Count == 1 && RelB(st.Sources[0]) == from);
            if (step is not { Kind: NoIntrovert.Core.Cleanup.CleanupKind.Move } || RelB(step.Target) != to) return $"{from}: expected a move to {to}\n{DescribeB(plan)}";
        }
        if (plan.Steps.Any(st => st.Sources.Any(src => RelB(src).StartsWith("roms/ps3/Already.ps3")) && st.Kind != NoIntrovert.Core.Cleanup.CleanupKind.Untouched))
            return $"a PS3 game already in place would change\n{DescribeB(plan)}";
        if (plan.Steps.Any(st => st.Sources.Any(src => RelB(src).Contains("/PS3_GAME") || RelB(src).Contains("/code/") || RelB(src).EndsWith(".app"))))
            return $"the plan looks inside a folder game\n{DescribeB(plan)}";
        var epic = plan.Steps.FirstOrDefault(st => st.Game == "Epic (USA)");
        if (epic is not { Kind: NoIntrovert.Core.Cleanup.CleanupKind.Move } || RelB(epic.Target) != "roms/psx/Epic (USA).m3u" || epic.Sources.Count != 3)
            return $"the discs in PS1/hidden/ should move into roms/psx/hidden/\n{DescribeB(plan)}";
        return null;
    });

    Test("Cleanup: keeping the current layout leaves folder games and hidden/ discs where they are", () =>
    {
        var plan = PlanB(LibraryLayout.InPlace);
        var moves = plan.Steps.Where(st => st.Kind is NoIntrovert.Core.Cleanup.CleanupKind.Move or NoIntrovert.Core.Cleanup.CleanupKind.Convert).ToList();
        return moves.Count == 0 ? null : DescribeB(plan);
    });

    Test("Cleanup: running the ES-DE plan moves folder games whole and the old playlist's discs into roms/psx/hidden/, through the journal", () =>
    {
        var before = TreeB();
        layoutRun = RunPlan(PlanB(LibraryLayout.EsDe));
        var tree = TreeB();
        var expected = new[]
        {
            "roms/wiiu/Some NUS Title/title.tmd", "roms/wiiu/Some NUS Title/00000000.app", "roms/wiiu/Some Game/code/app.xml",
            "roms/ps3/Gran Turismo 5.ps3/PS3_GAME/USRDIR/EBOOT.BIN", "roms/ps3/Demo.ps3/PS3_DISC.SFB", "roms/ps3/Already.ps3/PS3_GAME/PARAM.SFO",
            "roms/psx/Epic (USA).m3u", "roms/psx/hidden/Epic (USA) (Disc 2).chd", "roms/psx/hidden/noload.txt",
        };
        if (expected.FirstOrDefault(e => !tree.Contains(e)) is { } missing) return $"missing {missing}:\n{string.Join("\n", tree.Order())}";
        if (tree.Contains("PS1/Epic (USA).m3u") || tree.Contains("PS1/hidden/Epic (USA) (Disc 1).chd")) return "the old layout was left behind";
        if (!tree.Any(t => t.StartsWith(".noi-trash/") && t.EndsWith("PS1/Epic (USA).m3u"))) return "the old playlist isn't in the trash";
        var m3u = File.ReadAllLines(G7b("roms/psx/Epic (USA).m3u"));
        if (!m3u.SequenceEqual(Enumerable.Range(1, 3).Select(d => $"hidden/Epic (USA) (Disc {d}).chd"))) return string.Join(" | ", m3u);
        var moves = layoutRun.Entries.Count(e => e.Action == NoIntrovert.Core.Cleanup.JournalAction.Moved);
        if (moves != 7) return $"{moves} moves in the journal, expected 7 (4 folder games, 3 discs)";
        // Planned again, the result is already in place.
        var again = PlanB(LibraryLayout.EsDe);
        return again.Steps.All(st => st.Kind == NoIntrovert.Core.Cleanup.CleanupKind.Untouched) ? null : DescribeB(again);
    });

    Test("Cleanup: undoing the ES-DE run puts folder games, discs and the old playlist back", () =>
    {
        var problems = NoIntrovert.Core.Cleanup.CleanupRunner.Undo(layoutRun!, useBin: false, log);
        if (problems.Count > 0) return string.Join(" | ", problems);
        var tree = TreeB();
        var back = new[]
        {
            "Wii U/Some NUS Title/title.tik", "Wii U/Extracted/Some Game/meta/meta.xml", "PS3/Gran Turismo 5/PS3_GAME/PARAM.SFO",
            "Games/Demo.ps3/PS3_DISC.SFB", "PS1/hidden/Epic (USA) (Disc 3).chd", "PS1/Epic (USA).m3u", "roms/ps3/Already.ps3/PS3_GAME/PARAM.SFO",
        };
        if (back.FirstOrDefault(b => !tree.Contains(b)) is { } missing) return $"not restored: {missing}\n{string.Join("\n", tree.Order())}";
        return tree.Any(t => t.StartsWith("roms/psx/")) || tree.Any(t => t.StartsWith("roms/wiiu/")) ? $"left behind:\n{string.Join("\n", tree.Order())}" : null;
    });

    // A third Library: a Game.m3u/ folder (playlist and discs together) from an earlier version of the wizard.
    var games7c = Dir("m7c/Library");
    string G7c(string rel) => Path.Combine([games7c, .. rel.Split('/')]);
    string RelC(string? path) => path is null ? "" : Path.GetRelativePath(games7c, path).Replace('\\', '/');
    HashSet<string> TreeC() => Directory.EnumerateFileSystemEntries(games7c, "*", SearchOption.AllDirectories).Select(RelC).ToHashSet(PathNames.Comparer);
    NoIntrovert.Core.Cleanup.CleanupPlan PlanC()
    {
        var s = new AppSettings();
        return NoIntrovert.Core.Cleanup.CleanupPlanner.Plan(ScanAndVerify(s, games7c), s, NoIntrovert.Core.Dats.MatchStore.Default);
    }
    string DescribeC(NoIntrovert.Core.Cleanup.CleanupPlan plan) =>
        string.Join("\n", plan.Steps.Select(st => $"{st.Kind} {st.Game}: {string.Join(",", st.Sources.Take(1).Select(RelC))} → {RelC(st.Target)} ({st.Reason})"));

    Test("Cleanup: a Game.m3u/ folder from an earlier run becomes Game.m3u plus hidden/, and undo puts it back", () =>
    {
        Directory.CreateDirectory(G7c("roms/psx/Epic (USA).m3u"));
        for (var d = 1; d <= 3; d++)
        {
            var name = $"Epic (USA) (Disc {d}).chd";
            File.Copy(G7($"roms/psx/hidden/{name}"), G7c($"roms/psx/Epic (USA).m3u/{name}"));
            if (NoIntrovert.Core.Dats.OriginalHashes.Default.Get(G7($"roms/psx/hidden/{name}")) is { } o)
                NoIntrovert.Core.Dats.OriginalHashes.Default.Set(G7c($"roms/psx/Epic (USA).m3u/{name}"), o.Source, o.Files);
        }
        File.WriteAllText(G7c("roms/psx/Epic (USA).m3u/Epic (USA).m3u"), string.Join("\n", Enumerable.Range(1, 3).Select(d => $"Epic (USA) (Disc {d}).chd")) + "\n");
        var before = TreeC();

        var plan = PlanC();
        var epic = plan.Steps.FirstOrDefault(st => st.Game == "Epic (USA)");
        if (epic is not { Kind: NoIntrovert.Core.Cleanup.CleanupKind.Move } || RelC(epic.Target) != "roms/psx/Epic (USA).m3u" || epic.Sources.Count != 3)
            return $"the discs in Epic (USA).m3u/ should move into hidden/\n{DescribeC(plan)}";
        var run = RunPlan(plan);
        var tree = TreeC();
        var expected = new[] { "roms/psx/Epic (USA).m3u", "roms/psx/hidden/Epic (USA) (Disc 1).chd", "roms/psx/hidden/Epic (USA) (Disc 3).chd", "roms/psx/hidden/noload.txt" };
        if (expected.FirstOrDefault(e => !tree.Contains(e)) is { } missing) return $"missing {missing}:\n{string.Join("\n", tree.Order())}";
        if (!File.Exists(G7c("roms/psx/Epic (USA).m3u"))) return "Epic (USA).m3u is still a folder";
        if (!tree.Any(t => t.StartsWith(".noi-trash/") && t.EndsWith("roms/psx/Epic (USA).m3u/Epic (USA).m3u"))) return "the old playlist isn't in the trash";
        var m3u = File.ReadAllLines(G7c("roms/psx/Epic (USA).m3u"));
        if (!m3u.SequenceEqual(Enumerable.Range(1, 3).Select(d => $"hidden/Epic (USA) (Disc {d}).chd"))) return string.Join(" | ", m3u);
        var again = PlanC();
        if (again.Steps.Any(st => st.Kind != NoIntrovert.Core.Cleanup.CleanupKind.Untouched)) return $"planned again, it would still change:\n{DescribeC(again)}";

        var problems = NoIntrovert.Core.Cleanup.CleanupRunner.Undo(run, useBin: false, log);
        if (problems.Count > 0) return string.Join(" | ", problems);
        var undone = TreeC().Where(t => !t.StartsWith(".noi-trash")).ToHashSet(PathNames.Comparer);
        return undone.SetEquals(before) ? null : $"after undo:\n{string.Join("\n", undone.Order())}";
    });

    Test("Cleanup: of two copies, the one in the layout folder stays (M7 5)", () =>
    {
        Directory.CreateDirectory(G7("Old/psx"));
        File.Copy(G7("roms/psx/Crash Bandicoot (USA).chd"), G7("Old/psx/Crash Bandicoot (USA).chd"));
        var plan = PlanFor(LibraryLayout.EsDe);
        var dup = plan.Steps.Where(s => s.Kind == NoIntrovert.Core.Cleanup.CleanupKind.RemoveDuplicate).Select(s => Rel(s.Sources[0])).ToList();
        var stays = plan.Steps.Where(s => s.Game.StartsWith("Epic") && s.Kind != NoIntrovert.Core.Cleanup.CleanupKind.Untouched).ToList();
        if (stays.Count > 0) return $"the discs in hidden/ would move again:\n{Describe(plan)}";
        return dup.SequenceEqual(["Old/psx/Crash Bandicoot (USA).chd"]) ? null : Describe(plan);
    });

    Test("Cleanup: undo puts everything back, conversions included (M7 9)", () =>
    {
        var problems = NoIntrovert.Core.Cleanup.CleanupRunner.Undo(firstRun!, useBin: false, log);
        if (problems.Count > 0) return string.Join(" | ", problems);
        var tree = Tree();
        var back = new[] { "PS1/Crash Bandicoot.cue", "PS1/Crash Bandicoot.bin", "PS1/Epic (USA) (Disc 2).cue", "NES/Mario.nes", "roms/nes/Wrong Name.7z" };
        if (back.FirstOrDefault(b => !tree.Contains(b)) is { } missing) return $"not restored: {missing}";
        var gone = new[] { "roms/psx/Crash Bandicoot (USA).chd", "roms/nes/Super Mario Bros. (World).7z", "roms/psx/Epic (USA).m3u", "roms/psx/hidden", "roms/nes/Metroid (USA).7z" };
        return gone.FirstOrDefault(tree.Contains) is { } left ? $"still there: {left}" : null;
    });

    Test("Cleanup: after the trash is emptied, undo reverses moves but keeps conversions (M7 10)", () =>
    {
        File.Delete(G7("Old/psx/Crash Bandicoot (USA).chd"));
        var run = RunPlan(PlanFor(LibraryLayout.EsDe));
        NoIntrovert.Core.Cleanup.CleanupRunner.EmptyTrash(run, useBin: false, log);
        var problems = NoIntrovert.Core.Cleanup.CleanupRunner.Undo(run, useBin: false, log);
        var tree = Tree();
        if (!tree.Contains("roms/nes/Wrong Name.7z")) return "the rename wasn't undone";
        if (!tree.Contains("roms/psx/Crash Bandicoot (USA).chd")) return "a conversion whose originals are gone was undone anyway";
        return problems.Any(p => p.Contains("no longer in the trash")) ? Note($"{problems.Count} kept") : string.Join(" | ", problems);
    });
}

// ---- Library (M5): scanning folders into games ----

Test("Library: titles drop disc markers, generic names use their folder", () =>
{
    string P(params string[] parts) => Path.Combine([Path.GetTempPath(), "Lib", .. parts]);
    var cases = new (string Path, string Title, string Disc)[]
    {
        (P("PS1", "Final Fantasy VII (USA) (Disc 2).cue"), "Final Fantasy VII (USA)", "Disc 2"),
        (P("PS1", "Metal Gear Solid (USA) (Disc 1 of 2).chd"), "Metal Gear Solid (USA)", "Disc 1"),
        (P("Saturn", "Panzer Dragoon Saga - Disc 3.cue"), "Panzer Dragoon Saga", "Disc 3"),
        (P("DC", "Jet Grind Radio (USA)", "disc.gdi"), "Jet Grind Radio (USA)", ""),
        (P("PSP", "Corpse Party.iso"), "Corpse Party", ""),
        (P("Mixed", "Game (USA).iso.gz"), "Game (USA)", ""),
    };
    foreach (var (path, title, disc) in cases)
    {
        var got = NoIntrovert.Core.Library.LibraryScanner.TitleOf(path);
        if (got.Title != title || got.Disc != disc) return $"{Path.GetFileName(path)} → \"{got.Title}\" / \"{got.Disc}\"";
    }
    return null;
});

{
    var lib = Dir("library");
    void Put(string rel, byte[]? bytes = null, string? text = null)
    {
        var p = Path.Combine([lib, .. rel.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        if (text is not null) File.WriteAllText(p, text); else File.WriteAllBytes(p, bytes ?? new byte[2352]);
    }
    string Cue(string bin) => $"FILE \"{bin}\" BINARY\n TRACK 01 MODE1/2352\n  INDEX 01 00:00:00\n";
    Put("PS1/Final Fantasy VII (USA)/Final Fantasy VII (USA) (Disc 1).cue", text: Cue("Final Fantasy VII (USA) (Disc 1).bin"));
    Put("PS1/Final Fantasy VII (USA)/Final Fantasy VII (USA) (Disc 1).bin");
    Put("PS1/Final Fantasy VII (USA)/Final Fantasy VII (USA) (Disc 2).cue", text: Cue("Final Fantasy VII (USA) (Disc 2).bin"));
    Put("PS1/Final Fantasy VII (USA)/Final Fantasy VII (USA) (Disc 2).bin");
    Put("PS1/Converted/Final Fantasy VII (USA) (Disc 1).chd", new byte[512]);
    Put("Saturn/Guardian Heroes (USA).chd", new byte[512]);
    Put("Saturn/Backup/Guardian Heroes (USA).chd", new byte[512]);
    var gc = new byte[0x40];
    gc[0x1C] = 0xC2; gc[0x1D] = 0x33; gc[0x1E] = 0x9F; gc[0x1F] = 0x3D;
    Put("Misc/Metroid Prime (USA).iso", gc);
    Put("Carts/Tetris (World).gb", new byte[1024]);
    using (var z = ZipFile.Open(Path.Combine(lib, "Carts", "Mario (USA).zip"), ZipArchiveMode.Create))
        z.CreateEntry("Mario (USA).nes");
    WiiUSample.CopyTree(baseTitle, Path.Combine(lib, "Wii U", "Game"));
    WiiUSample.CopyTree(updateTitle, Path.Combine(lib, "Wii U", "Update v32"));
    var previousDir = AppSettings.SettingsDirectoryOverride;

    Test("Library: a scan groups files into games with their system and status", () =>
    {
        var index = NoIntrovert.Core.Library.LibraryScanner.Scan([lib], null, null, CancellationToken.None);
        var games = index.Games().ToDictionary(g => g.Title);
        string Describe(string title) => games.TryGetValue(title, out var g) ? $"{g.SystemName} · {g.Items.Count} item(s) · {g.Status}" : "missing";
        var expected = new Dictionary<string, string>
        {
            ["Final Fantasy VII (USA)"] = "PlayStation · 3 item(s) · OriginalsRemain",
            ["Guardian Heroes (USA)"] = "Saturn · 2 item(s) · Duplicate",
            ["Metroid Prime (USA)"] = "GameCube · 1 item(s) · NeedsConverting",
            ["Tetris (World)"] = "Game Boy · 1 item(s) · NeedsConverting", // a loose ROM goes into a 7z (R1)
            ["Mario (USA)"] = "NES / Famicom · 1 item(s) · NeedsConverting", // told from the zip's contents (FR-3)
            ["Test Game"] = "Wii U · 2 item(s) · NeedsConverting",
        };
        foreach (var (title, want) in expected)
            if (Describe(title) != want) return $"{title}: {Describe(title)}, expected {want}";
        if (games.Count != expected.Count) return $"{games.Count} games: {string.Join(", ", games.Keys)}";
        var ff7 = games["Final Fantasy VII (USA)"];
        return ff7.Discs == 2 ? null : $"Final Fantasy VII has {ff7.Discs} disc(s)";
    });

    Test("Library: a rescan reuses unchanged items and rereads changed ones", () =>
    {
        var first = NoIntrovert.Core.Library.LibraryScanner.Scan([lib], null, null, CancellationToken.None);
        var tetris = Path.Combine(lib, "Carts", "Tetris (World).gb");
        File.WriteAllBytes(tetris, new byte[2048]);
        File.SetLastWriteTimeUtc(tetris, DateTime.UtcNow.AddMinutes(1));
        var second = NoIntrovert.Core.Library.LibraryScanner.Scan([lib], first, null, CancellationToken.None);
        var same = second.Items.Count(i => first.Items.Any(o => ReferenceEquals(o, i)));
        var changed = second.Items.Single(i => i.Path.EndsWith("Tetris (World).gb"));
        if (changed.Size != 2048) return "the changed file wasn't reread";
        if (same != second.Items.Count - 1) return $"{same} of {second.Items.Count} items were reused, expected all but one";
        var missing = NoIntrovert.Core.Library.LibraryScanner.Scan([lib, Path.Combine(lib, "gone")], second, null, CancellationToken.None);
        return missing.MissingFolders.Count == 1 ? null : "a folder that isn't there wasn't reported";
    });

    Test("Library: the index is saved and loaded", () =>
    {
        var dir = Dir("library-index");
        AppSettings.SettingsDirectoryOverride = dir;
        try
        {
            var index = NoIntrovert.Core.Library.LibraryScanner.Scan([lib], null, null, CancellationToken.None);
            index.Save();
            var loaded = NoIntrovert.Core.Library.LibraryIndex.Load();
            if (loaded.Items.Count != index.Items.Count) return $"loaded {loaded.Items.Count} of {index.Items.Count} items";
            var a = string.Join("|", index.Games().Select(g => $"{g.Title}:{g.Status}"));
            var b = string.Join("|", loaded.Games().Select(g => $"{g.Title}:{g.Status}"));
            return a == b ? Note($"{new FileInfo(Path.Combine(dir, "library.json")).Length:N0} bytes for {index.Items.Count} items") : "the loaded index groups differently";
        }
        finally { AppSettings.SettingsDirectoryOverride = previousDir; }
    });

    Test("Scale: a 5,000-input library scans, and rescans faster", () =>
    {
        var sw = Stopwatch.StartNew();
        var first = NoIntrovert.Core.Library.LibraryScanner.Scan([bigLibrary], null, null, CancellationToken.None);
        var cold = sw.Elapsed;
        sw.Restart();
        var again = NoIntrovert.Core.Library.LibraryScanner.Scan([bigLibrary], first, null, CancellationToken.None);
        var warm = sw.Elapsed;
        sw.Restart();
        var games = again.Games();
        var grouping = sw.Elapsed;
        if (again.Items.Count != bigCount) return $"{again.Items.Count} items, expected {bigCount}";
        var report = $"{games.Count:N0} games; first scan {cold.TotalSeconds:F1}s, rescan {warm.TotalSeconds:F1}s, grouping {grouping.TotalSeconds:F2}s";
        return cold < TimeSpan.FromSeconds(30) && warm < cold ? Note(report) : $"too slow: {report}";
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
        s => { s.Output.Folder = outDir; s.Keys.WiiUKeysPath = encKeysTxt; configure?.Invoke(s); });
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
    var k = WiiUKeys.Load(path);
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
    var found = NoIntrovert.Core.WiiU.WuaJobs.FindSources(dir, true).Select(Path.GetFileName).ToList();
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
    return EncryptedBuild("keyfile", [Path.Combine(dir, "game.wud")], s => s.Keys.WiiUKeysPath = onlyCommon, ("0005000010303e00_v0", encTree));
});

Test("Wii U: missing keys are reported, not guessed", () =>
{
    var wrong = Path.Combine(encDir, "wrong-keys.txt");
    EncryptedSample.WriteKeysTxt(wrong, new byte[16], Enumerable.Repeat((byte)0x5A, 16).ToArray());
    var o = RunJob(c => NoIntrovert.Core.WiiU.WuaJobs.Build(c, encBase, [nusGame]), s => { s.Output.Folder = Dir("wiiu-wrongkeys"); s.Keys.WiiUKeysPath = wrong; });
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
    var o = RunJob(c => NoIntrovert.Core.WiiU.WuaJobs.Build(c, encBase, [bad]), s => { s.Output.Folder = Dir("wiiu-damaged"); s.Keys.WiiUKeysPath = encKeysTxt; });
    return o.Status == JobStatus.Failed && o.Summary.Contains("hash") && Directory.GetFiles(Path.Combine(scratch, "wiiu-damaged")).Length == 0
        ? Note(o.Summary) : $"{o.Status}: {o.Summary}";
});

Test("Wii U NUS: a personalised ticket is refused", () =>
{
    var personal = EncryptedSample.MakeNus(encTree, Path.Combine(encDir, "nus-personal"), encBase, 0, encKeys, deviceId: 0x12345678);
    var o = RunJob(c => NoIntrovert.Core.WiiU.WuaJobs.Build(c, encBase, [personal]), s => { s.Output.Folder = Dir("wiiu-personal"); s.Keys.WiiUKeysPath = encKeysTxt; });
    return o.Status == JobStatus.Failed && o.Summary.Contains("personalised") ? null : $"{o.Status}: {o.Summary}";
});

// Issue #21: the .wua goes next to a title's folder, as a CHD goes next to its disc
// image, not inside it. Rows come from a real scan and the Convert planner.
JobWork PlannedWua(IEnumerable<string> rowPaths, Action<AppSettings>? configure = null)
{
    var settings = new AppSettings { Keys = { WiiUKeysPath = encKeysTxt } };
    configure?.Invoke(settings);
    var plan = ConvertPlanner.Plan(rowPaths.Select(p => new PlanInput(p, TargetKind.Wua, 1)).ToList(), settings, log);
    if (plan.Error is { } error) throw new InvalidOperationException(error);
    return plan.Jobs.Count == 1 ? plan.Jobs[0].Work : throw new InvalidOperationException($"{plan.Jobs.Count} jobs planned");
}

string? WuaLandsIn(string label, JobWork work, string root, string expectFolder, Action<AppSettings>? configure = null)
{
    var before = Directory.GetFiles(root, "*.wua", SearchOption.AllDirectories).ToDictionary(f => f, File.GetLastWriteTimeUtc);
    var o = RunJob(work, s => { s.Keys.WiiUKeysPath = encKeysTxt; s.Output.Overwrite = true; configure?.Invoke(s); });
    if (o.Status != JobStatus.Succeeded) return $"{label}: {o.Status}: {o.Summary}";
    var written = Directory.GetFiles(root, "*.wua", SearchOption.AllDirectories)
        .Where(f => !before.TryGetValue(f, out var t) || File.GetLastWriteTimeUtc(f) != t).ToList();
    if (written.Count != 1) return $"{label}: wrote {written.Count} .wua files ({string.Join(", ", written)})";
    return PathNames.Comparer.Equals(Path.GetDirectoryName(written[0]), Path.GetFullPath(expectFolder))
        ? null : $"{label}: the .wua went to {Path.GetDirectoryName(written[0])}, not {expectFolder}";
}

string? NothingWritten(string label, JobWork work, string root)
{
    var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length;
    var o = RunJob(work, s => s.Keys.WiiUKeysPath = encKeysTxt);
    var after = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length;
    return o.Status == JobStatus.Failed && after == before ? null : $"{label}: {o.Status} ({after - before} new files)";
}

Test("Wii U: the .wua is written next to the title's folder, not inside it", () =>
{
    // A NUS folder, found by scanning its parent (the Convert page's folder drop).
    var root = Dir("wiiu-next-to-nus");
    var games = Path.Combine(root, "Games");
    var nus = Path.Combine(games, "Mario Kart 8 [NUS]");
    WiiUSample.CopyTree(nusGame, nus);
    var scanned = FormatRegistry.Default.Scan(games, recursive: true);
    if (scanned.Count != 1 || scanned[0].Format != KnownFormats.Nus) return $"the scan found {string.Join(", ", scanned.Select(s => $"{s.Primary} ({s.Format.Id})"))}";
    if (ConvertTargets.For(scanned[0], new AppSettings()).Default.Kind != TargetKind.Wua) return "a NUS folder doesn't start on .wua";
    JobWork Direct(string source) => c => NoIntrovert.Core.WiiU.WuaJobs.Build(c, encBase, [source]);
    var problems = new List<string?>
    {
        WuaLandsIn("NUS folder from a scan", PlannedWua(scanned.Select(s => s.Primary)), root, games),
        // The NUS folder dropped on its own (a scan of it), and given with a trailing separator.
        WuaLandsIn("NUS folder dropped", PlannedWua(FormatRegistry.Default.Scan(nus, true).Select(s => s.Primary)), root, games),
        WuaLandsIn("NUS folder with a trailing separator", PlannedWua([nus + Path.DirectorySeparatorChar]), root, games),
        // A file inside the NUS folder isn't a title: it fails without writing anything.
        NothingWritten("a file inside the NUS folder", PlannedWua([Path.Combine(nus, "title.tmd")]), root),
        // The output folder setting still wins.
        WuaLandsIn("NUS folder with an output folder", PlannedWua([nus]), root, Path.Combine(root, "Out"), s => s.Output.Folder = Path.Combine(root, "Out")),
    };

    // A plain title folder (code/content/meta), and a file inside it.
    var titleRoot = Dir("wiiu-next-to-title");
    var titleGames = Path.Combine(titleRoot, "Games");
    var title = Path.Combine(titleGames, "Encrypted Game");
    WiiUSample.CopyTree(encTree, title);
    var titleScan = FormatRegistry.Default.Scan(titleGames, true);
    if (titleScan.Count != 1 || titleScan[0].Format != KnownFormats.WiiUFolder) return "the title folder wasn't found as one";
    problems.Add(WuaLandsIn("title folder from a scan", PlannedWua(titleScan.Select(s => s.Primary)), titleRoot, titleGames));
    problems.Add(WuaLandsIn("title folder with a trailing separator", PlannedWua([title + Path.DirectorySeparatorChar]), titleRoot, titleGames));
    problems.Add(NothingWritten("a file inside the title folder", Direct(Path.Combine(title, "meta", "meta.xml")), titleRoot));

    // A .wua with an update folder beside it: the merged .wua stays beside them, whichever row comes first.
    var wuaRoot = Dir("wiiu-next-to-wua");
    var wuaGames = Path.Combine(wuaRoot, "Games");
    var made = RunJob(Direct(encTree), s => s.Output.Folder = wuaGames);
    if (made.Status != JobStatus.Succeeded) return $"making the .wua: {made.Status}: {made.Summary}";
    WiiUSample.CopyTree(encUpdateTree, Path.Combine(wuaGames, "Encrypted Game Update"));
    var wuaScan = FormatRegistry.Default.Scan(wuaGames, true).Select(s => s.Primary).ToList();
    if (wuaScan.Count != 2) return $"the .wua scan found {wuaScan.Count} inputs";
    problems.Add(WuaLandsIn(".wua and an update folder", PlannedWua(wuaScan), wuaRoot, wuaGames));
    problems.Add(WuaLandsIn("update folder and .wua", PlannedWua(Enumerable.Reverse(wuaScan)), wuaRoot, wuaGames));

    var failed = problems.OfType<string>().ToList();
    return failed.Count == 0 ? null : string.Join("; ", failed);
});

// ---- Wii U archives (R3): a .zip/.7z holding a WUX, WUD or NUS title ----------------

if (SevenZipEngine.IsAvailable(out _))
{
    const string encWua = "Encrypted Game [0005000010303E00].wua";

    // Packs the given files and folders into an archive in its own folder.
    string PackWiiU(string label, string archiveName, params string[] members)
    {
        var dir = Dir("wiiu-arc-" + label);
        var archive = Path.Combine(dir, archiveName);
        using var engine = new SevenZipEngine();
        engine.Compress(archiveName.EndsWith(".zip") ? ArchiveFormat.Zip : ArchiveFormat.SevenZip, ArchiveLevel.Fastest, 0, false,
            members, archive, contentsOnly: false, null, CancellationToken.None);
        return archive;
    }

    // Runs BuildFromArchive with its own temp folder, so leftovers can be counted.
    (JobOutcome Outcome, string Temp) FromArchive(string archive, bool remove, Action<AppSettings>? configure = null,
        CancellationToken token = default, IJobReport? report = null)
    {
        var temp = Dir("wiiu-arc-temp-" + Path.GetFileName(Path.GetDirectoryName(archive)));
        var o = RunJob(c => NoIntrovert.Core.WiiU.WuaJobs.BuildFromArchive(c, archive), s =>
        {
            s.Keys.WiiUKeysPath = encKeysTxt;
            s.Performance.TempFolder = temp;
            s.Output.SaveTo = remove ? SaveTo.ReplaceOriginals : SaveTo.NextToOriginals;
            s.Output.UseRecycleBin = false;
            configure?.Invoke(s);
        }, token, report);
        return (o, temp);
    }

    string? Leftovers(string temp, string dir)
    {
        var inTemp = Directory.GetFileSystemEntries(temp);
        if (inTemp.Length > 0) return $"temp files left: {string.Join(", ", inTemp.Select(Path.GetFileName))}";
        var tmp = Directory.GetFiles(dir, "*.tmp");
        return tmp.Length > 0 ? $"partial archive left: {Path.GetFileName(tmp[0])}" : null;
    }

    string? SameAsGame(string wua)
    {
        var unpacked = Path.Combine(Path.GetDirectoryName(wua)!, "unpacked");
        WiiUSample.Unpack(wua, unpacked);
        var folders = Directory.GetDirectories(unpacked).Select(Path.GetFileName).ToList();
        if (!folders.SequenceEqual(["0005000010303e00_v0"])) return $"title folders {string.Join(", ", folders)}";
        return SameTree(encTree, Path.Combine(unpacked, "0005000010303e00_v0"));
    }

    Test("Wii U archive: Game.7z holding Game.wux → .wua next to it, archive and temp removed", () =>
    {
        var src = Dir("wiiu-arc-wux-src");
        var wux = Path.Combine(src, "Game.wux");
        File.WriteAllBytes(wux, EncryptedSample.ToWux(wudBytes));
        File.SetAttributes(wux, FileAttributes.ReadOnly); // unpacked read-only, the temp folder must still go
        var archive = PackWiiU("wux", "Game.7z", wux);
        File.SetAttributes(wux, FileAttributes.Normal);
        var (o, temp) = FromArchive(archive, remove: true);
        if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
        var dir = Path.GetDirectoryName(archive)!;
        var wua = Path.Combine(dir, encWua);
        if (!File.Exists(wua)) return $"no .wua next to the 7z: {string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName))}";
        if (File.Exists(archive)) return "the 7z wasn't removed after the .wua verified";
        return Leftovers(temp, dir) ?? SameAsGame(wua) ?? Note(o.Summary);
    });

    Test("Wii U archive: Game.zip holding a NUS folder → one .wua", () =>
    {
        var archive = PackWiiU("nus", "Game.zip", nusGame);
        var (o, temp) = FromArchive(archive, remove: false);
        if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
        var dir = Path.GetDirectoryName(archive)!;
        var wuas = Directory.GetFiles(dir, "*.wua");
        if (wuas.Length != 1 || Path.GetFileName(wuas[0]) != encWua) return $"{wuas.Length} .wua: {string.Join(", ", wuas.Select(Path.GetFileName))}";
        if (!File.Exists(archive)) return "the zip was removed with Remove originals off";
        return Leftovers(temp, dir) ?? SameAsGame(wuas[0]) ?? Note(o.Summary);
    });

    Test("Wii U archive: a split _partN.wud set inside a 7z", () =>
    {
        var src = Dir("wiiu-arc-split-src");
        var half = wudBytes.Length / 2 / EncryptedSample.Sector * EncryptedSample.Sector;
        File.WriteAllBytes(Path.Combine(src, "Game_part1.wud"), wudBytes[..half]);
        File.WriteAllBytes(Path.Combine(src, "Game_part2.wud"), wudBytes[half..]);
        var archive = PackWiiU("split", "Game.7z", Path.Combine(src, "Game_part1.wud"), Path.Combine(src, "Game_part2.wud"));
        var inside = ArchivePeek.Inside(archive) ?? [];
        if (!KnownFormats.TakesWiiU(new FileSet(archive, [archive], KnownFormats.ByName(archive)!, inside)))
            return $"the 7z isn't offered a .wua: it holds {string.Join(", ", inside.Select(f => f.Id))}";
        var (o, temp) = FromArchive(archive, remove: true);
        if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
        var dir = Path.GetDirectoryName(archive)!;
        var wuas = Directory.GetFiles(dir, "*.wua");
        if (wuas.Length != 1) return $"{wuas.Length} .wua files";
        if (File.Exists(archive)) return "the 7z wasn't removed after the .wua verified";
        return Leftovers(temp, dir) ?? SameAsGame(wuas[0]) ?? Note(o.Summary);
    });

    Test("Wii U archive: a failed build keeps the archive and leaves no temp files", () =>
    {
        var archive = PackWiiU("fail", "Game.zip", nusGame);
        var (o, temp) = FromArchive(archive, remove: true, s => s.Keys.WiiUKeysPath = Path.Combine(encDir, "wrong-keys.txt"));
        if (o.Status != JobStatus.Failed) return $"{o.Status}: {o.Summary}";
        var dir = Path.GetDirectoryName(archive)!;
        if (!File.Exists(archive)) return "the zip was removed although nothing was built";
        if (Directory.GetFiles(dir, "*.wua").Length > 0) return "a .wua was written";
        return Leftovers(temp, dir) ?? Note(o.Summary);
    });

    Test("Wii U archive: cancelling while packing keeps the archive and leaves no temp files", () =>
    {
        var archive = PackWiiU("cancel", "Game.7z", nusGame);
        using var cts = new CancellationTokenSource();
        var (o, temp) = FromArchive(archive, remove: true, null, cts.Token, new CancelAtStage(cts, "Packing"));
        if (o.Status != JobStatus.Cancelled) return $"{o.Status}: {o.Summary}";
        var dir = Path.GetDirectoryName(archive)!;
        if (!File.Exists(archive)) return "the 7z was removed after a cancel";
        if (Directory.GetFiles(dir, "*.wua").Length > 0) return "a .wua was written";
        return Leftovers(temp, dir);
    });

    Test("Wii U archive: an existing .wua is kept, the new one gets \" (2)\", then Replace removes the archive", () =>
    {
        var archive = PackWiiU("exists", "Game.zip", nusGame);
        var dir = Path.GetDirectoryName(archive)!;
        File.WriteAllText(Path.Combine(dir, encWua), "not mine");
        var (o, temp) = FromArchive(archive, remove: true);
        if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
        if (!File.Exists(Path.Combine(dir, Path.GetFileNameWithoutExtension(encWua) + " (2).wua"))) return "the new .wua isn't there as (2)";
        if (File.Exists(archive)) return "the zip was kept although its .wua was built and verified";
        return File.ReadAllText(Path.Combine(dir, encWua)) == "not mine" ? Leftovers(temp, dir) : "the existing .wua was replaced";
    });

    Test("Wii U archive: no Wii U content → \"No Wii U game found\", archive kept", () =>
    {
        var src = Dir("wiiu-arc-none-src");
        File.WriteAllText(Path.Combine(src, "readme.txt"), "nothing to see");
        File.WriteAllBytes(Path.Combine(src, "game_part2.wud"), new byte[4096]); // a stray part alone isn't a disc
        var archive = PackWiiU("none", "Stuff.zip", Path.Combine(src, "readme.txt"), Path.Combine(src, "game_part2.wud"));
        var (o, temp) = FromArchive(archive, remove: true);
        if (o.Status != JobStatus.Skipped || !o.Summary.Contains("No Wii U game found")) return $"{o.Status}: {o.Summary}";
        if (!File.Exists(archive)) return "the zip was removed";
        return Leftovers(temp, Path.GetDirectoryName(archive)!) ?? Note(o.Summary);
    });
}

// ---- GameCube / Wii (lib-wii-gamecube-kit) ---------------------------------------------

var rvzProblem = (string?)null;
try { rvzProblem = LibWiiGameCubeKit.WiiGameCubeKit.AbiVersion == 1 ? null : "unexpected lib-wii-gamecube-kit ABI"; }
catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { rvzProblem = ex.Message; }
Test("lib-wii-gamecube-kit loads", () => rvzProblem);
if (rvzProblem is null)
{
    var gcIso = Path.Combine(Dir("gc"), "Test Game (USA).iso");
    NintendoDiscs.WriteGameCube(gcIso);

    Test("lib-wii-gamecube-kit structs match the C layout", () =>
        LibWiiGameCubeKit.WiiGameCubeKit.StructSizes == LibWiiGameCubeKit.WiiGameCubeKit.ExpectedStructSizes
            ? null : $"managed {LibWiiGameCubeKit.WiiGameCubeKit.StructSizes}, C {LibWiiGameCubeKit.WiiGameCubeKit.ExpectedStructSizes}");

    Test("Probe a GameCube ISO", () =>
    {
        using var rvz = new LibWiiGameCubeKit.WiiGameCubeKit();
        var info = rvz.Probe(gcIso);
        return info.Platform == LibWiiGameCubeKit.DiscPlatform.GameCube && info.GameId == "GNIE01" && info.CanConvert
            ? null : $"{info}";
    });

    Test("GameCube ISO → RVZ (verified), and back to the same ISO", () =>
    {
        var outDir = Dir("gc-rvz");
        var o = RunJob(c => ChdJobs.Convert(c, gcIso), s => s.Output.Folder = outDir);
        var rvzPath = Path.Combine(outDir, "Test Game (USA).rvz");
        if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
        if (!File.Exists(rvzPath)) return "no .rvz written";
        if (new FileInfo(rvzPath).Length >= new FileInfo(gcIso).Length / 4) return "the RVZ is barely smaller than the ISO";
        var back = Dir("gc-rvz-back");
        var e = RunJob(c => RvzJobs.Extract(c, rvzPath), s => s.Output.Folder = back);
        if (e.Status != JobStatus.Succeeded) return $"extract: {e.Status}: {e.Summary}";
        return SameFile(gcIso, Path.Combine(back, "Test Game (USA).iso")) ?? Note($"{o.Summary}");
    });

    Test("GameCube RVZ → CHD stores the same disc as a CHD made from the ISO", () =>
    {
        var dir = Dir("gc-rvz-chd");
        var rvzPath = Path.Combine(dir, "game.rvz");
        using (var rvz = new LibWiiGameCubeKit.WiiGameCubeKit())
            rvz.Convert(gcIso, rvzPath, new LibWiiGameCubeKit.ConvertSettings());
        var o = RunJob(c => ChdJobs.Convert(c, rvzPath), s => { s.Output.Folder = dir; s.Nintendo.Target = NintendoTarget.Chd; });
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
        var o = RunJob(c => ChdJobs.Convert(c, zip), s => s.Output.Folder = dir);
        return o.Status == JobStatus.Succeeded && File.Exists(Path.Combine(dir, "Zipped Game.rvz")) ? null : $"{o.Status}: {o.Summary}";
    });

    Test("An RVZ on the Convert tab is skipped (already RVZ)", () =>
    {
        var dir = Dir("gc-rvz-skip");
        var rvzPath = Path.Combine(dir, "game.rvz");
        using (var rvz = new LibWiiGameCubeKit.WiiGameCubeKit())
            rvz.Convert(gcIso, rvzPath, new LibWiiGameCubeKit.ConvertSettings());
        var o = RunJob(c => ChdJobs.Convert(c, rvzPath), s => s.Output.Folder = dir);
        return o.Status == JobStatus.Skipped ? null : $"{o.Status}: {o.Summary}";
    });

    Test("Remove the original ISO after its RVZ verifies", () =>
    {
        var dir = Dir("gc-rvz-delete");
        var iso = Path.Combine(dir, "game.iso");
        File.Copy(gcIso, iso);
        var o = RunJob(c => ChdJobs.Convert(c, iso), s => { s.Output.SaveTo = SaveTo.ReplaceOriginals; s.Output.UseRecycleBin = false; s.Chd.VerifyAfterCreate = false; });
        if (o.Status != JobStatus.Succeeded) return $"{o.Status}: {o.Summary}";
        return !File.Exists(iso) && File.Exists(Path.Combine(dir, "game.rvz")) ? null : "the ISO is still there, or no RVZ";
    });

    Test("Cancelling an RVZ conversion leaves no RVZ", () =>
    {
        var dir = Dir("gc-rvz-cancel");
        using var cts = new CancellationTokenSource();
        var report = new CancelAtProgress(cts, 0.05);
        var o = RunJob(c => ChdJobs.Convert(c, gcIso), s => { s.Output.Folder = dir; s.Performance.ThreadsPerJob = 1; s.Nintendo.BlockSize = 0x8000; }, cts.Token, report);
        if (o.Status != JobStatus.Cancelled) return $"expected Cancelled, got {o.Status}: {o.Summary}";
        return Directory.EnumerateFiles(dir).Any() ? $"left {string.Join(", ", Directory.EnumerateFiles(dir).Select(Path.GetFileName))}" : null;
    });

    Test("WBFS is refused with a reason", () =>
    {
        var dir = Dir("gc-wbfs");
        var wbfs = Path.Combine(dir, "game.wbfs");
        File.WriteAllBytes(wbfs, NintendoDiscs.FakeWbfsHeader());
        var o = RunJob(c => ChdJobs.Convert(c, wbfs), s => s.Output.Folder = dir);
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
            var o = RunJob(c => TestJobs.Run(c, path, TestKind.CompareWithChdman, records.Add), s => { s.Diagnostics.ChdmanExePath = upstream; s.Chd.IsoMode = mode; });
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

ShellTests.Run(Test, scratch);
FileFactsTests.Run(Test, scratch);
ConvertDesignTests.Run(Test);

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
            var o = RunJob(c => TestJobs.Run(c, path, TestKind.RoundTrip, records.Add), s => s.Performance.ParallelJobs = 1);
            return o.Status == JobStatus.Succeeded ? Note(records[0].Notes + $"; {records[0].LibMbps:F0} MB/s") : string.Join("; ", records.Select(r => r.Notes));
        });
    }

    if (File.Exists(upstream))
        Test("Compare with upstream chdman, full disc (Saturn 25 tracks)", () =>
        {
            var records = new List<TestRecord>();
            var o = RunJob(c => TestJobs.Run(c, discs[2].Path, TestKind.CompareWithChdman, records.Add), s => s.Diagnostics.ChdmanExePath = upstream);
            return o.Status == JobStatus.Succeeded ? Note(records[0].Notes) : string.Join("; ", records.Select(r => r.Notes));
        });
}

// ---- Summary -------------------------------------------------------------------

Summary:
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
        var outcome = RunJob(c => ChdJobs.Convert(c, source), s => { s.Output.Folder = outDir; configure?.Invoke(s); });
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
    var settings = new AppSettings { Performance = { TempFolder = Path.Combine(scratch, "_temp"), ParallelJobs = 1 }, Chd = { VerifyAfterCreate = true } };
    configure?.Invoke(settings);
    // Tests name an output folder by setting it; the app also chooses Save to: Folder.
    if (settings.Output.SaveTo == SaveTo.NextToOriginals && settings.Output.Folder.Length > 0) settings.Output.SaveTo = SaveTo.Folder;
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
    var o = RunJob(c => NoIntrovert.Core.WiiU.WuaJobs.Build(c, 0x0005000010101C00, [game, update, dlc]), s => s.Output.Folder = dir);
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

/// <summary>Cancels the batch when a job reaches a given stage.</summary>
sealed class CancelAtStage(CancellationTokenSource cts, string stage) : IJobReport
{
    public void SetStage(string s) { if (s.StartsWith(stage, StringComparison.Ordinal)) cts.Cancel(); }
    public void SetProgress(double fraction) { }
}

/// <summary>Settings tests never open a picker.</summary>
sealed class NoDialogs : NoIntrovert.Services.IDialogs
{
    public Task OpenFolderAsync(string folder) => Task.CompletedTask;
    public Task<IReadOnlyList<string>> PickFilesAsync(string title, string filterName, IReadOnlyList<string> patterns) => throw new NotSupportedException();
    public Task<string?> PickFileAsync(string title, string filterName, IReadOnlyList<string> patterns) => throw new NotSupportedException();
    public Task<string?> PickFolderAsync(string title) => throw new NotSupportedException();
    public Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string pattern) => throw new NotSupportedException();
    public Task SetClipboardAsync(string text) => throw new NotSupportedException();
}
