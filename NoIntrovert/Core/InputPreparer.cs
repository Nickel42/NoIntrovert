using NoIntrovert.Core.Cd;
using NoIntrovert.Core.Formats;

namespace NoIntrovert.Core;

public enum DiscKind { Cd, Dvd, HardDisk }

public enum IsoMode { Auto, Dvd, Cd }

/// <summary>
/// A disc ready for converting: what to call the output, which create call to
/// use, and its input file. IsNintendo marks a GameCube/Wii disc (plain or in
/// one of Dolphin's containers), which goes through libdolphinrvz.
/// </summary>
public sealed record PreparedDisc(string OutputName, DiscKind Kind, string InputPath, string Note, bool IsNintendo = false);

/// <summary>The discs prepared from one input file, and the scratch folder holding any rebuilt files.</summary>
internal sealed class PreparedInput(WorkDirectory work) : IDisposable
{
    public WorkDirectory Work { get; } = work;
    public List<PreparedDisc> Discs { get; } = [];
    public void Dispose() => Work.Dispose();
}

/// <summary>
/// Turns any supported input (cue/gdi/toc/nrg/iso directly; ccd, mds, cso,
/// zso, isz, pbp, ecm, loose bin/img, archives and split files through a
/// conversion step) into discs libchdman can create a CHD from.
/// </summary>
internal sealed class InputPreparer(string tempRoot, IsoMode isoMode, ILog log)
{
    public PreparedInput Prepare(string path, IProgress<(string Stage, double Fraction)>? progress, CancellationToken token)
    {
        var prepared = new PreparedInput(new WorkDirectory(tempRoot));
        try
        {
            PrepareInto(prepared, path, BaseName(path), progress, token, depth: 0);
            if (prepared.Discs.Count == 0)
                throw new InvalidDataException("No disc image was found in this input.");
            return prepared;
        }
        catch
        {
            prepared.Dispose();
            throw;
        }
    }

    private void PrepareInto(PreparedInput prepared, string path, string name,
        IProgress<(string, double)>? progress, CancellationToken token, int depth)
    {
        if (depth > 4) throw new InvalidDataException("Archives are nested too deeply.");
        token.ThrowIfCancellationRequested();
        var work = prepared.Work;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        IProgress<double> Stage(string stage) => new InlineProgress<double>(f => progress?.Report((stage, f)));

        switch (ext)
        {
            case ".cue":
                prepared.Discs.Add(new PreparedDisc(name, DiscKind.Cd, PrepareCue(path, work, Stage("Decoding ECM"), token), "cue"));
                return;

            case ".gdi" or ".toc" or ".nrg":
                prepared.Discs.Add(new PreparedDisc(name, DiscKind.Cd, path, ext[1..]));
                return;

            case ".ccd":
                prepared.Discs.Add(new PreparedDisc(name, DiscKind.Cd, CcdConverter.WriteCue(path, work), "CloneCD → cue"));
                return;

            case ".mds":
            {
                var r = MdsConverter.Prepare(path, work, log.Warn, Stage("Removing subchannel data"), token);
                prepared.Discs.Add(new PreparedDisc(name, r.IsDvd ? DiscKind.Dvd : DiscKind.Cd, r.InputPath,
                    r.IsDvd ? "Alcohol DVD" : "Alcohol CD → cue"));
                return;
            }

            case ".iso":
                AddIso(prepared, path, name, "iso", cdIsAuto: true);
                return;

            case ".gcm":
                prepared.Discs.Add(new PreparedDisc(name, DiscKind.Dvd, path, "GameCube/Wii gcm", IsNintendo: true));
                return;

            case ".gcz" or ".wia" or ".rvz" or ".tgc" or ".wbfs" or ".ciso":
                prepared.Discs.Add(new PreparedDisc(name, DiscKind.Dvd, path, $"GameCube/Wii {ext[1..]}", IsNintendo: true));
                return;

            case ".cso" or ".zso":
            {
                var iso = Path.Combine(work.Path, name + ".iso");
                log.Info($"Decompressing {Path.GetFileName(path)} to a temporary ISO...");
                CsoDecoder.Decode(path, iso, Stage("Decompressing CSO"), token);
                AddIso(prepared, iso, name, ext[1..] + " → iso", cdIsAuto: false);
                return;
            }

            case ".isz":
            {
                var image = Path.Combine(work.Path, name + ".iso");
                log.Info($"Decompressing {Path.GetFileName(path)} to a temporary image...");
                IszDecoder.Decode(path, image, Stage("Decompressing ISZ"), token);
                if (StartsWithSync(image))
                    prepared.Discs.Add(new PreparedDisc(name, DiscKind.Cd, WriteLooseBinCue(image, work), "isz → bin/cue"));
                else
                    AddIso(prepared, image, name, "isz → iso", cdIsAuto: true);
                return;
            }

            case ".pbp":
            {
                log.Info($"Rebuilding the disc image(s) inside {Path.GetFileName(path)}...");
                var discs = PbpExtractor.Extract(path, work.Path, name, Stage("Unpacking PBP"), token);
                foreach (var d in discs)
                {
                    if (d.DiscId.Length > 0) log.Debug($"PBP disc {d.Number}: {d.DiscId}");
                    prepared.Discs.Add(new PreparedDisc(Path.GetFileNameWithoutExtension(d.CuePath), DiscKind.Cd, d.CuePath,
                        discs.Count > 1 ? $"pbp disc {d.Number} of {discs.Count}" : "pbp → bin/cue"));
                }
                return;
            }

            case ".ecm":
            {
                var decodedName = Path.GetFileNameWithoutExtension(path); // "game.bin.ecm" -> "game.bin"
                if (!Path.HasExtension(decodedName)) decodedName += ".bin";
                var decoded = Path.Combine(work.Path, decodedName);
                log.Info($"Decoding {Path.GetFileName(path)}...");
                EcmDecoder.Decode(path, decoded, Stage("Decoding ECM"), token);
                PrepareInto(prepared, decoded, Path.GetFileNameWithoutExtension(decodedName), progress, token, depth + 1);
                return;
            }

            case ".bin" or ".img":
            {
                if (ext == ".img" && File.Exists(Path.ChangeExtension(path, ".ccd")))
                {
                    PrepareInto(prepared, Path.ChangeExtension(path, ".ccd"), name, progress, token, depth);
                    return;
                }
                if (StartsWithSync(path) || new FileInfo(path).Length % CdSector.RawSize == 0 && ext == ".bin")
                    prepared.Discs.Add(new PreparedDisc(name, DiscKind.Cd, WriteLooseBinCue(path, work), "loose bin → cue"));
                else if (ext == ".img")
                    prepared.Discs.Add(new PreparedDisc(name, DiscKind.HardDisk, path, "hard disk image"));
                else
                    AddIso(prepared, path, name, "2048-byte bin", cdIsAuto: true);
                return;
            }

            case ".001":
            {
                log.Info($"Joining the parts of {Path.GetFileName(path)}...");
                var joined = ArchiveExtractor.JoinSplitParts(path, work.Path, Stage("Joining split parts"), token);
                PrepareInto(prepared, joined, Path.GetFileNameWithoutExtension(joined), progress, token, depth + 1);
                return;
            }
        }

        if (ArchiveExtractor.IsArchive(path))
        {
            var target = Path.Combine(work.Path, "unpacked-" + depth + "-" + prepared.Discs.Count);
            Directory.CreateDirectory(target);
            log.Info($"Unpacking {Path.GetFileName(path)}...");
            ArchiveExtractor.Extract(path, target, Stage("Unpacking archive"), token);

            var inner = DiscFiles.SelectPrimaries(Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories));
            if (inner.Count == 0)
                throw new InvalidDataException($"{Path.GetFileName(path)} holds no disc image this app can read.");
            var before = prepared.Discs.Count;
            foreach (var file in inner)
                PrepareInto(prepared, file, Path.GetFileNameWithoutExtension(file), progress, token, depth + 1);

            // One disc from "Game (USA).zip" should become "Game (USA).chd", whatever the files inside are called.
            if (prepared.Discs.Count - before == 1)
                prepared.Discs[^1] = prepared.Discs[^1] with { OutputName = name, Note = "archive → " + prepared.Discs[^1].Note };
            return;
        }

        throw new NotSupportedException($"'{Path.GetExtension(path)}' files are not supported.");
    }

    /// <summary>The name a CHD made from <paramref name="path"/> gets: its file name without extension (and without .tar for .tar.gz).</summary>
    internal static string BaseName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private void AddIso(PreparedInput prepared, string iso, string name, string note, bool cdIsAuto)
    {
        if (NintendoDisc.IsPlainDisc(iso))
        {
            log.Debug($"  {Path.GetFileName(iso)}: GameCube/Wii disc");
            prepared.Discs.Add(new PreparedDisc(name, DiscKind.Dvd, iso, $"{note}: GameCube/Wii", IsNintendo: true));
            return;
        }
        DiscKind kind;
        var why = isoMode == IsoMode.Auto ? "" : " (set in options)";
        switch (isoMode)
        {
            case IsoMode.Cd: kind = DiscKind.Cd; break;
            case IsoMode.Dvd: kind = DiscKind.Dvd; break;
            default:
                if (!cdIsAuto) { kind = DiscKind.Dvd; why = " (CSO/ZSO images are DVD)"; break; }
                kind = IsoProbe.LooksLikeDvd(iso, out var reason) ? DiscKind.Dvd : DiscKind.Cd;
                why = $" ({reason})";
                break;
        }
        log.Debug($"  {Path.GetFileName(iso)}: treating as {(kind == DiscKind.Cd ? "CD" : "DVD")}{why}");
        prepared.Discs.Add(new PreparedDisc(name, kind, iso, $"{note} as {(kind == DiscKind.Cd ? "CD" : "DVD")}"));
    }

    /// <summary>
    /// Returns a cue chdman can read. Most cues are used as they are; one whose
    /// bins are stored as .ecm gets those decoded and a rewritten copy.
    /// </summary>
    private string PrepareCue(string cuePath, WorkDirectory work, IProgress<double> progress, CancellationToken token)
    {
        var cue = CueSheet.Load(cuePath);
        var dir = Path.GetDirectoryName(cuePath)!;
        var missing = cue.Files.Where(f => !File.Exists(Path.Combine(dir, f.Name))).ToList();
        if (missing.Count == 0) return cuePath;

        var resolved = new Dictionary<CueFile, string>();
        foreach (var f in cue.Files)
        {
            var original = Path.Combine(dir, f.Name);
            if (File.Exists(original)) { resolved[f] = original; continue; }
            if (File.Exists(original + ".ecm"))
            {
                var decoded = Path.Combine(work.Path, Path.GetFileName(f.Name));
                log.Info($"Decoding {Path.GetFileName(original)}.ecm...");
                EcmDecoder.Decode(original + ".ecm", decoded, progress, token);
                resolved[f] = decoded;
                continue;
            }
            throw new FileNotFoundException($"The cue sheet needs '{f.Name}', which is not in {dir}.", original);
        }

        var newCue = work.CuePathFor(Path.GetFileName(cuePath), resolved.Values.ToList());
        foreach (var (f, location) in resolved)
            f.Name = WorkDirectory.CueReference(newCue, location);
        cue.Save(newCue);
        return newCue;
    }

    /// <summary>Writes a one-track cue for a bin that has none, with the track mode read from its first sector.</summary>
    internal static string WriteLooseBinCue(string binPath, WorkDirectory work)
    {
        var first = new byte[CdSector.RawSize];
        using (var fs = File.OpenRead(binPath)) fs.ReadAtLeast(first, first.Length, throwOnEndOfStream: false);
        var type = !CdSector.HasSync(first) ? "AUDIO" : first[15] == 2 ? "MODE2/2352" : "MODE1/2352";

        var cuePath = work.CuePathFor(Path.GetFileNameWithoutExtension(binPath) + ".cue", [binPath]);
        var cue = new CueSheet();
        var file = new CueFile { Name = WorkDirectory.CueReference(cuePath, binPath) };
        var track = new CueTrack { Number = 1, Type = type };
        track.Indexes.Add(new CueIndex(1, 0));
        file.Tracks.Add(track);
        cue.Files.Add(file);
        cue.Save(cuePath);
        return cuePath;
    }

    private static bool StartsWithSync(string path)
    {
        Span<byte> head = stackalloc byte[16];
        using var fs = File.OpenRead(path);
        return fs.Read(head) == 16 && CdSector.HasSync(head);
    }
}

/// <summary>An IProgress that reports on the calling thread (unlike Progress&lt;T&gt;).</summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
