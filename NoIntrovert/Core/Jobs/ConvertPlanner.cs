using NoIntrovert.Core.Archives;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.WiiU;

namespace NoIntrovert.Core.Jobs;

/// <summary>One row to convert: its path, what it becomes, and its size (which weights progress).</summary>
public sealed record PlanInput(string Path, TargetKind Target, long Size = 0);

/// <summary>One queue job: the rows it covers (indexes into the inputs) and its work.</summary>
public sealed record PlannedJob(string Name, IReadOnlyList<int> Rows, long Cost, JobWork Work);

/// <summary>
/// The jobs for a list of rows, plus the rows left alone and anything worth
/// saying first. <see cref="Error"/> means the run shouldn't start.
/// </summary>
public sealed record ConvertPlan(IReadOnlyList<PlannedJob> Jobs, IReadOnlyList<int> Left, IReadOnlyList<string> Warnings, string? Error = null);

/// <summary>
/// Turns rows and their targets into queue jobs: one per row in general, one
/// per game for Wii U titles (base, update and DLC together), and one for the
/// whole set when files are packed into a single archive. Each job runs with
/// its row's own choice where the settings hold one global value (RVZ or CHD
/// for GameCube/Wii, 7z or zip for packing and converting archives).
/// </summary>
public static class ConvertPlanner
{
    public static ConvertPlan Plan(IReadOnlyList<PlanInput> rows, AppSettings settings, ILog log)
    {
        var jobs = new List<PlannedJob>();
        var left = new List<int>();
        var warnings = new List<string>();
        var wiiu = new List<int>();
        var packs = new List<int>();

        // FR-7: two or more discs of one game become one job (each disc into the hidden folder, then the .m3u).
        var paths = rows.Select(r => r.Path).ToList();
        var multiDisc = RomJobs.MultiDiscGroups(paths, Enumerable.Range(0, rows.Count).Where(i => rows[i].Target is TargetKind.Chd or TargetKind.Rvz));
        foreach (var (game, discs) in multiDisc)
        {
            var target = rows[discs[0].Row].Target;
            var nintendo = target == TargetKind.Rvz ? NintendoTarget.Rvz : NintendoTarget.Chd;
            var sources = discs.Select(d => rows[d.Row].Path).ToList();
            var extension = target == TargetKind.Rvz ? ".rvz" : ".chd";
            jobs.Add(new($"{game} ({discs.Count} discs)", discs.Select(d => d.Row).ToList(), discs.Sum(d => rows[d.Row].Size),
                c => RomJobs.MultiDisc(c.WithSettings(With(c.Settings, s => s.Nintendo.Target = nintendo)), sources, game, extension)));
        }
        var grouped = multiDisc.SelectMany(g => g.Discs.Select(d => d.Row)).ToHashSet();

        for (var i = 0; i < rows.Count; i++)
        {
            if (grouped.Contains(i)) continue;
            var row = rows[i];
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(row.Path));
            var path = row.Path;
            switch (row.Target)
            {
                case TargetKind.Leave:
                    left.Add(i);
                    break;
                case TargetKind.Chd or TargetKind.Rvz:
                    var nintendo = row.Target == TargetKind.Rvz ? NintendoTarget.Rvz : NintendoTarget.Chd;
                    jobs.Add(new(name, [i], row.Size, c => ChdJobs.Convert(c.WithSettings(With(c.Settings, s => s.Nintendo.Target = nintendo)), path)));
                    break;
                case TargetKind.Iso:
                    jobs.Add(new(name, [i], row.Size, c => RvzJobs.Extract(c, path)));
                    break;
                case TargetKind.DiscImage:
                    jobs.Add(new(name, [i], row.Size, c => ChdJobs.Extract(c, path)));
                    break;
                case TargetKind.Files:
                    jobs.Add(new(name, [i], row.Size, c => ArchiveJobs.Extract(c, path)));
                    break;
                case TargetKind.Wua:
                    wiiu.Add(i);
                    break;
                case TargetKind.SevenZip when ArchiveJobs.IsConvertible(path) && HoldsSeveralGames(path):
                    // R6: one 7z per game, each holding one ROM (D9).
                    jobs.Add(new(name, [i], row.Size, c => RomJobs.Split(c.WithSettings(With(c.Settings, s => { s.Archives.Format = ArchiveFormat.SevenZip; SingleFile7z(s); })),
                        path, OutputPaths.FolderFor(c.Settings, path))));
                    break;
                case TargetKind.Zip when Directory.Exists(path) && VitaGame.IsGameFolder(path):
                    jobs.Add(new(name, [i], row.Size, c => RomJobs.PackVita(c, path, OutputPaths.FolderFor(c.Settings, path))));
                    break;
                case TargetKind.SevenZip or TargetKind.Zip when ArchiveJobs.IsConvertible(path):
                    var to = row.Target == TargetKind.SevenZip ? ConvertTarget.ToSevenZip : ConvertTarget.ToZip;
                    var single = ArchivePeek.Members(path)?.Count == 1;
                    void Archive(AppSettings s)
                    {
                        s.Archives.ConvertTarget = to;
                        if (to == ConvertTarget.ToSevenZip && single) SingleFile7z(s);
                    }
                    // Asked for the format it already has: recompressed with a method RetroArch reads (R8).
                    if (Path.GetExtension(path).Equals(row.Target == TargetKind.SevenZip ? ".7z" : ".zip", StringComparison.OrdinalIgnoreCase))
                        jobs.Add(new(name, [i], row.Size, c => ArchiveJobs.Recompress(c.WithSettings(With(c.Settings, Archive)), path)));
                    else
                        jobs.Add(new(name, [i], row.Size, c => ArchiveJobs.Convert(c.WithSettings(With(c.Settings, Archive)), path)));
                    break;
                case TargetKind.SevenZip or TargetKind.Zip:
                    packs.Add(i);
                    break;
            }
        }

        PlanWiiU(rows, wiiu, settings, log, jobs);
        var error = PlanPacking(rows, packs, settings, jobs);

        if (rows.Any(r => Path.GetExtension(r.Path).ToLowerInvariant() is ".wbfs" or ".ciso" && r.Target != TargetKind.Leave))
            warnings.Add("WBFS and CISO images can't be converted (they don't record the disc's exact size); convert them to ISO first.");
        var removes = rows.Where((r, i) => !left.Contains(i)).ToList();
        if (removes.Count > 0 && (settings.Output.RemoveOriginals || settings.Output.RemoveArchiveOriginals || settings.Output.RemoveWiiUOriginals))
        {
            var fate = settings.Output.UseRecycleBin && FileRemover.CanUseBin ? $"moved to the {FileRemover.BinName}" : "deleted permanently";
            warnings.Add($"Originals will be {fate} once each new file verifies or tests OK.");
        }
        if (jobs.Count > 0 && rows.Any(r => r.Target is TargetKind.SevenZip or TargetKind.Zip or TargetKind.Files)
            && !ArchiveEngine.IsAvailable(out var problem))
            error ??= problem;

        jobs.Sort((a, b) => a.Rows[0].CompareTo(b.Rows[0]));
        return new ConvertPlan(jobs, left, warnings, error);
    }

    /// <summary>Wii U rows: an archive builds on its own; everything else is grouped by game (base title ID).</summary>
    private static void PlanWiiU(IReadOnlyList<PlanInput> rows, List<int> wiiu, AppSettings settings, ILog log, List<PlannedJob> jobs)
    {
        if (wiiu.Count == 0) return;
        var keys = WiiUKeys.Empty;
        if (wiiu.Any(i => WiiUEncrypted.IsNusFolder(rows[i].Path) || WiiUEncrypted.IsDiscImage(rows[i].Path)))
        {
            keys = WiiUKeys.Load(settings.Keys.WiiUKeysPath);
            log.Info($"Wii U: {keys.Describe()}");
            foreach (var p in keys.Problems) log.Warn($"Wii U: {p}");
        }
        var byGame = new Dictionary<ulong, List<int>>();
        foreach (var i in wiiu)
        {
            var path = rows[i].Path;
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            if (KnownFormats.Archives.Any(f => f.HasExtension(path)))
            {
                jobs.Add(new(name, [i], rows[i].Size, c => WuaJobs.BuildFromArchive(c, path)));
                continue;
            }
            if (WiiUTitles.UnsupportedReason(path) is { } reason)
            {
                jobs.Add(new(name, [i], rows[i].Size, _ => JobOutcome.Skipped(reason)));
                continue;
            }
            try
            {
                var titles = WiiUTitles.Read(path, keys);
                if (titles.Select(t => t.BaseId).Distinct().Count() > 1)
                    log.Warn($"Wii U: {name} holds more than one game; only the first ({titles[0].BaseId:X16}) is built from it.");
                var id = titles[0].BaseId;
                if (!byGame.TryGetValue(id, out var list)) byGame[id] = list = [];
                list.Add(i);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                           or KeyNotFoundException or NotSupportedException)
            {
                var message = ex.Message;
                jobs.Add(new(name, [i], rows[i].Size, _ => JobOutcome.Failed(message)));
            }
        }
        foreach (var (id, game) in byGame)
        {
            var sources = game.Select(i => rows[i].Path).ToList();
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(sources[0])) + (game.Count > 1 ? $" (+{game.Count - 1})" : "");
            jobs.Add(new(name, game, game.Sum(i => rows[i].Size), c => WuaJobs.Build(c, id, sources)));
        }
        log.Info($"Wii U: {byGame.Count} game(s) to build from {wiiu.Count} item(s)");
    }

    /// <summary>Files and folders packed into 7z or zip, by the archive grouping setting.</summary>
    private static string? PlanPacking(IReadOnlyList<PlanInput> rows, List<int> packs, AppSettings settings, List<PlannedJob> jobs)
    {
        if (packs.Count == 0) return null;
        foreach (var byFormat in packs.GroupBy(i => rows[i].Target))
        {
            var format = byFormat.Key == TargetKind.Zip ? ArchiveFormat.Zip : ArchiveFormat.SevenZip;
            void UseFormat(AppSettings s) => s.Archives.Format = format;
            var group = byFormat.ToList();
            if (settings.Archives.Grouping == ArchiveGrouping.Single)
            {
                var single = settings.Archives.SingleName.Trim();
                if (single.Length == 0) return "Give the combined archive a name first.";
                var paths = group.Select(i => rows[i].Path).ToList();
                jobs.Add(new(single + ArchiveJobs.Extension(format), group, group.Sum(i => rows[i].Size),
                    c => ArchiveJobs.CompressOne(c.WithSettings(With(c.Settings, UseFormat)), paths, single, OutputPaths.FolderFor(c.Settings, paths[0]))));
                continue;
            }
            // One archive per row, named after it; rows from one folder that would clash keep their extension.
            foreach (var folder in group.GroupBy(i => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(rows[i].Path)) ?? "", StringComparer.OrdinalIgnoreCase))
            {
                var members = folder.ToList();
                var names = ArchiveJobs.UniqueNames(members.Select(i => rows[i].Path).ToList());
                for (var k = 0; k < members.Count; k++)
                {
                    var i = members[k];
                    var path = rows[i].Path;
                    var archiveName = names[k];
                    var label = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                    if (settings.Archives.Grouping == ArchiveGrouping.PerEntryInFolders && Directory.Exists(path))
                        jobs.Add(new(label, [i], rows[i].Size, c => ArchiveJobs.CompressEntries(c.WithSettings(With(c.Settings, UseFormat)), path, c.Settings.Output.Folder)));
                    else if (File.Exists(path))
                        // One file: under its canonical name (.3ds → .cci, R12 fixes), and as a D9 7z.
                        jobs.Add(new(label, [i], rows[i].Size, c => RomJobs.Pack(
                            c.WithSettings(With(c.Settings, s => { UseFormat(s); if (format == ArchiveFormat.SevenZip) SingleFile7z(s); })),
                            path, archiveName, OutputPaths.FolderFor(c.Settings, path))));
                    else
                        jobs.Add(new(label, [i], rows[i].Size,
                            c => ArchiveJobs.CompressOne(c.WithSettings(With(c.Settings, UseFormat)), [path], archiveName, OutputPaths.FolderFor(c.Settings, path))));
                }
            }
        }
        return null;
    }

    /// <summary>R6: an archive with more than one cartridge ROM in it.</summary>
    private static bool HoldsSeveralGames(string archive) =>
        (ArchivePeek.Members(archive) ?? []).Count(m => KnownFormats.Cartridges.Any(k => k.HasExtension(m.Name))
            && TargetFormats.ByExtension(Path.GetExtension(m.Name)).Any(s => s.Category == "cartridge")) > 1;

    /// <summary>D9: a 7z holding one file is LZMA2 at Ultra, and not solid (there's nothing to share a block with).</summary>
    private static void SingleFile7z(AppSettings s)
    {
        s.Archives.Level = ArchiveLevel.Ultra;
        s.Archives.Solid = false;
    }

    /// <summary>A copy of <paramref name="settings"/> with one change, so a row's choice never leaks into the shared snapshot.</summary>
    private static AppSettings With(AppSettings settings, Action<AppSettings> change)
    {
        var copy = settings.Clone();
        change(copy);
        return copy;
    }
}
