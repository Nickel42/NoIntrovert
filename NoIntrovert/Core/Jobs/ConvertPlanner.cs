using NoIntrovert.Core.Archives;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Presets;
using NoIntrovert.Core.WiiU;

namespace NoIntrovert.Core.Jobs;

/// <summary>
/// One row to convert: its path, what it becomes, its size (which weights
/// progress) and its format preset's values. Without values a job uses its
/// action's preset (<see cref="ActionPresets"/>), as Organize does.
/// <paramref name="OwnChecks"/>: the values' Verify and Test settings are the
/// user's own (Custom or a saved preset); otherwise Settings › "Check each file
/// after converting" decides, as presets don't.
/// </summary>
public sealed record PlanInput(string Path, TargetKind Target, long Size = 0, PresetValues? Preset = null, bool OwnChecks = false);

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
/// for GameCube/Wii, 7z or zip for packing and converting archives), and
/// with its own format preset's values (or its action's preset when the row
/// has none). <paramref name="pin"/> runs after the preset, for values a caller must keep (Clean Up saves to its staging folder, so never removes originals).
/// </summary>
public static class ConvertPlanner
{
    public static ConvertPlan Plan(IReadOnlyList<PlanInput> rows, AppSettings settings, ILog log, Action<AppSettings>? pin = null)
    {
        // A copy of the settings as row i's job runs with them, with one change on top for the row's choice.
        AppSettings With(AppSettings s, int i, Action<AppSettings>? change = null)
        {
            var copy = SettingsFor(s, rows[i].Target, pin, rows[i].Preset, rows[i].OwnChecks);
            change?.Invoke(copy);
            return copy;
        }

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
            var first = discs[0].Row;
            var target = rows[first].Target;
            var nintendo = target == TargetKind.Rvz ? NintendoTarget.Rvz : NintendoTarget.Chd;
            var sources = discs.Select(d => rows[d.Row].Path).ToList();
            var extension = target == TargetKind.Rvz ? ".rvz" : ".chd";
            jobs.Add(new($"{game} ({discs.Count} discs)", discs.Select(d => d.Row).ToList(), discs.Sum(d => rows[d.Row].Size),
                c => RomJobs.MultiDisc(c.WithSettings(With(c.Settings, first, s => s.Nintendo.Target = nintendo)), sources, game, extension)));
        }
        var grouped = multiDisc.SelectMany(g => g.Discs.Select(d => d.Row)).ToHashSet();

        for (var i = 0; i < rows.Count; i++)
        {
            if (grouped.Contains(i)) continue;
            var row = rows[i];
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(row.Path));
            var path = row.Path;
            var at = i;
            switch (row.Target)
            {
                case TargetKind.Leave:
                    left.Add(i);
                    break;
                case TargetKind.Chd or TargetKind.Rvz:
                    var nintendo = row.Target == TargetKind.Rvz ? NintendoTarget.Rvz : NintendoTarget.Chd;
                    jobs.Add(new(name, [i], row.Size, c => ChdJobs.Convert(c.WithSettings(With(c.Settings, at, s => s.Nintendo.Target = nintendo)), path)));
                    break;
                case TargetKind.Iso:
                    jobs.Add(new(name, [i], row.Size, c => RvzJobs.Extract(c, path)));
                    break;
                case TargetKind.DiscImage:
                    jobs.Add(new(name, [i], row.Size, c => ChdJobs.Extract(c.WithSettings(With(c.Settings, at)), path)));
                    break;
                case TargetKind.Files:
                    jobs.Add(new(name, [i], row.Size, c => ArchiveJobs.Extract(c.WithSettings(With(c.Settings, at)), path)));
                    break;
                case TargetKind.Wua:
                    wiiu.Add(i);
                    break;
                case TargetKind.Xiso or TargetKind.XboxCci:
                    // Original Xbox discs: lib-xiso-kit makes and checks them.
                    var xbox = row.Target;
                    jobs.Add(new(name, [i], row.Size, c => Xbox.XboxJobs.Convert(c.WithSettings(With(c.Settings, at)), path, xbox)));
                    break;
                case TargetKind.Cso or TargetKind.Zso:
                    // PSP and PS2 discs for real hardware: compressed in 2048-byte blocks, then read back and checked.
                    var zso = row.Target == TargetKind.Zso;
                    jobs.Add(new(name, [i], row.Size, c => CsoJobs.Convert(c.WithSettings(With(c.Settings, at)), path, zso)));
                    break;
                case TargetKind.Zar:
                    // Xbox 360 discs: lib-xiso-kit reads the files, lib-wii-u-kit writes and checks the archive.
                    jobs.Add(new(name, [i], row.Size, c => Xbox.Xbox360Jobs.ToZar(c.WithSettings(With(c.Settings, at)), path)));
                    break;
                case TargetKind.Nsz or TargetKind.Xcz or TargetKind.Nsp or TargetKind.Xci:
                    // Switch packages: lib-nx-kit converts and checks them (prod.keys needed).
                    var kind = row.Target;
                    jobs.Add(new(name, [i], row.Size, c => Switch.SwitchJobs.Convert(c.WithSettings(With(c.Settings, at)), path, kind)));
                    break;
                case TargetKind.Cci or TargetKind.Zcci:
                    // D2: lib-3ds-kit makes a decrypted .cci (bare, or compressed as Azahar's .zcci).
                    var compress = row.Target == TargetKind.Zcci;
                    jobs.Add(new(name, [i], row.Size, c => Ctr.CtrJobs.ToCci(c.WithSettings(With(c.Settings, at)), path, compress)));
                    break;
                case TargetKind.SevenZip or TargetKind.Zip when IsThreeDsGame(path):
                    jobs.Add(new(name, [i], row.Size, _ => JobOutcome.Skipped("a 3DS game isn't packed into a 7z or zip; choose .cci or .zcci")));
                    break;
                case TargetKind.SevenZip when ArchiveJobs.IsConvertible(path) && HoldsSeveralGames(path):
                    // R6: one 7z per game, each holding one ROM (D9).
                    jobs.Add(new(name, [i], row.Size, c => RomJobs.Split(c.WithSettings(With(c.Settings, at, s => { s.Archives.Format = ArchiveFormat.SevenZip; SingleFile7z(s); })),
                        path, OutputPaths.FolderFor(c.Settings, path))));
                    break;
                case TargetKind.Zip when Directory.Exists(path) && VitaGame.IsGameFolder(path):
                    jobs.Add(new(name, [i], row.Size, c => RomJobs.PackVita(c.WithSettings(With(c.Settings, at)), path, OutputPaths.FolderFor(c.Settings, path))));
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
                        jobs.Add(new(name, [i], row.Size, c => ArchiveJobs.Recompress(c.WithSettings(With(c.Settings, at, Archive)), path)));
                    else
                        jobs.Add(new(name, [i], row.Size, c => ArchiveJobs.Convert(c.WithSettings(With(c.Settings, at, Archive)), path)));
                    break;
                case TargetKind.SevenZip or TargetKind.Zip:
                    packs.Add(i);
                    break;
            }
        }

        PlanWiiU(rows, wiiu, settings, log, jobs, (c, i) => c.WithSettings(With(c.Settings, i)));
        var error = PlanPacking(rows, packs, settings, pin, jobs, With);

        if (rows.Any(r => Path.GetExtension(r.Path).ToLowerInvariant() is ".wbfs" or ".ciso" && r.Target != TargetKind.Leave))
            warnings.Add("WBFS and CISO images can't be converted (they don't record the disc's exact size); convert them to ISO first.");
        var converted = rows.Where((r, i) => !left.Contains(i)).ToList();
        if (converted.Any(r => RemovesOriginals(SettingsFor(settings, r.Target, pin), r.Target)))
        {
            var fate = settings.Output.UseRecycleBin && FileRemover.CanUseBin ? $"moved to the {FileRemover.BinName}" : "deleted permanently";
            warnings.Add($"Originals will be {fate} once each new file verifies or tests OK.");
        }
        if (SettingsFor(settings, TargetKind.Iso, pin).Output.ReplacesOriginals && converted.Any(r => r.Target is TargetKind.Iso or TargetKind.DiscImage))
            warnings.Add("Disc images extracted from a CHD, RVZ, WIA or GCZ keep their originals: there's nothing to verify the new files against.");
        if (jobs.Count > 0 && rows.Any(r => r.Target is TargetKind.SevenZip or TargetKind.Zip or TargetKind.Files)
            && !SevenZipEngine.IsAvailable(out var problem))
            error ??= problem;

        jobs.Sort((a, b) => a.Rows[0].CompareTo(b.Rows[0]));
        return new ConvertPlan(jobs, left, warnings, error);
    }

    /// <summary>Wii U rows: an archive builds on its own; everything else is grouped by game (base title ID).</summary>
    private static void PlanWiiU(IReadOnlyList<PlanInput> rows, List<int> wiiu, AppSettings settings, ILog log, List<PlannedJob> jobs,
        Func<JobContext, int, JobContext> withPreset)
    {
        if (wiiu.Count == 0) return;
        var keys = WiiUKeys.Empty;
        if (wiiu.Any(i => WiiUEncrypted.IsNusFolder(rows[i].Path) || WiiUEncrypted.IsDiscImage(rows[i].Path)))
        {
            keys = WuaJobs.LoadKeys(settings.Keys.WiiUKeysPath, out var readProblem);
            if (readProblem is not null) log.Warn($"Wii U: {readProblem}");
            log.Info($"Wii U: {keys.Describe()}");
            foreach (var p in keys.Problems) log.Warn($"Wii U: {p}");
        }
        var byGame = new Dictionary<ulong, List<int>>();
        foreach (var i in wiiu)
        {
            var path = rows[i].Path;
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            var at = i;
            if (KnownFormats.Archives.Any(f => f.HasExtension(path)))
            {
                jobs.Add(new(name, [i], rows[i].Size, c => WuaJobs.BuildFromArchive(withPreset(c, at), path)));
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
                var message = ex is WiiUKeysMissingException ? WuaJobs.NoKeysMessage : ex.Message;
                jobs.Add(new(name, [i], rows[i].Size, _ => JobOutcome.Failed(message)));
            }
        }
        foreach (var (id, game) in byGame)
        {
            var sources = game.Select(i => rows[i].Path).ToList();
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(sources[0])) + (game.Count > 1 ? $" (+{game.Count - 1})" : "");
            jobs.Add(new(name, game, game.Sum(i => rows[i].Size), c => WuaJobs.Build(withPreset(c, game[0]), id, sources)));
        }
        log.Info($"Wii U: {byGame.Count} game(s) to build from {wiiu.Count} item(s)");
    }

    /// <summary>
    /// Files and folders packed into 7z or zip, by the archive grouping of each
    /// row's preset: rows set to one combined archive are packed together, per
    /// format and archive name (with the first row's values).
    /// </summary>
    private static string? PlanPacking(IReadOnlyList<PlanInput> rows, List<int> packs, AppSettings settings, Action<AppSettings>? pin, List<PlannedJob> jobs,
        Func<AppSettings, int, Action<AppSettings>?, AppSettings> With)
    {
        if (packs.Count == 0) return null;
        AppSettings RowSettings(int i) => SettingsFor(settings, rows[i].Target, pin, rows[i].Preset, rows[i].OwnChecks);
        foreach (var byFormat in packs.GroupBy(i => (rows[i].Target, Single: RowSettings(i).Archives.Grouping == ArchiveGrouping.Single)))
        {
            var format = byFormat.Key.Target == TargetKind.Zip ? ArchiveFormat.Zip : ArchiveFormat.SevenZip;
            void UseFormat(AppSettings s) => s.Archives.Format = format;
            if (byFormat.Key.Single)
            {
                foreach (var byName in byFormat.GroupBy(i => RowSettings(i).Archives.SingleName.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    var single = byName.Key;
                    if (single.Length == 0) return "Give the combined archive a name first.";
                    var members = byName.ToList();
                    var paths = members.Select(i => rows[i].Path).ToList();
                    jobs.Add(new(single + ArchiveJobs.Extension(format), members, members.Sum(i => rows[i].Size),
                        c => ArchiveJobs.CompressOne(c.WithSettings(With(c.Settings, members[0], UseFormat)), paths, single, OutputPaths.FolderFor(c.Settings, paths[0]))));
                }
                continue;
            }
            var group = byFormat.ToList();
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
                    if (RowSettings(i).Archives.Grouping == ArchiveGrouping.PerEntryInFolders && Directory.Exists(path))
                        jobs.Add(new(label, [i], rows[i].Size, c => ArchiveJobs.CompressEntries(c.WithSettings(With(c.Settings, i, UseFormat)), path, c.Settings.Output.TargetFolder ?? "")));
                    else if (File.Exists(path))
                        // One file: under its canonical name (.3ds → .cci, R12 fixes), and as a D9 7z.
                        jobs.Add(new(label, [i], rows[i].Size, c => RomJobs.Pack(
                            c.WithSettings(With(c.Settings, i, s => { UseFormat(s); if (format == ArchiveFormat.SevenZip) SingleFile7z(s); })),
                            path, archiveName, OutputPaths.FolderFor(c.Settings, path))));
                    else
                        jobs.Add(new(label, [i], rows[i].Size,
                            c => ArchiveJobs.CompressOne(c.WithSettings(With(c.Settings, i, UseFormat)), [path], archiveName, OutputPaths.FolderFor(c.Settings, path))));
                }
            }
        }
        return null;
    }

    /// <summary>D2: a 3DS game file (or an archive holding one), which is never packed into a 7z or zip.</summary>
    private static bool IsThreeDsGame(string path)
    {
        static bool Game(string name) => Path.GetExtension(name).ToLowerInvariant() is ".3ds" or ".cci" or ".cia" or ".zcci" or ".zcia";
        return File.Exists(path) && (Game(path) || (ArchiveJobs.IsConvertible(path) && (ArchivePeek.Members(path) ?? []).Any(m => Game(m.Name))));
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

    /// <summary>
    /// A copy of <paramref name="settings"/> as a job making <paramref name="target"/>
    /// runs with them: the row's format preset <paramref name="preset"/> applied
    /// (or, without one, its action's preset), then <paramref name="pin"/>.
    /// A preset doesn't decide whether new files are checked: that stays the
    /// settings' own (Settings › "Check each file after converting") unless
    /// <paramref name="ownChecks"/> says the values are the user's (Custom, a
    /// saved preset). Replace the originals checks regardless (in the jobs).
    /// A copy, so a row's choice never leaks into the shared snapshot.
    /// </summary>
    public static AppSettings SettingsFor(AppSettings settings, TargetKind target, Action<AppSettings>? pin = null, PresetValues? preset = null, bool ownChecks = false)
    {
        AppSettings copy;
        if (preset is not null)
        {
            copy = settings.Clone();
            preset.ApplyTo(copy);
        }
        else copy = ActionPresets.ForTarget(target) is { } action ? ActionPresets.Effective(settings, action) : settings.Clone();
        if (!ownChecks)
        {
            copy.Chd.VerifyAfterCreate = settings.Chd.VerifyAfterCreate;
            copy.Archives.TestAfter = settings.Archives.TestAfter;
        }
        pin?.Invoke(copy);
        return copy;
    }

    /// <summary>
    /// Whether a job making <paramref name="target"/> with <paramref name="s"/>
    /// removes what it was made from: with Replace the originals, every target
    /// that's verified or tested (an extracted disc image has nothing to check against).
    /// </summary>
    private static bool RemovesOriginals(AppSettings s, TargetKind target) =>
        s.Output.ReplacesOriginals && target is TargetKind.Chd or TargetKind.Rvz or TargetKind.Wua
            or TargetKind.SevenZip or TargetKind.Zip or TargetKind.Files or TargetKind.Cci or TargetKind.Zcci
            or TargetKind.Nsz or TargetKind.Xcz or TargetKind.Nsp or TargetKind.Xci or TargetKind.Xiso or TargetKind.XboxCci or TargetKind.Zar
            or TargetKind.Cso or TargetKind.Zso;
}
