using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Library;

namespace NoIntrovert.Core.Cleanup;

/// <summary>
/// FR-8's extra files: what a new library takes along besides the games. Files named like a game with
/// another extension (its saves and states: <c>Game.srm</c>, <c>Game.state.auto</c>) are copied beside the
/// game under its new name, and only when the game is. Every other file in the folders can go along too,
/// into the system folder its folder's games go to (kept below it as it is), or as it is when its folder has none.
/// </summary>
public static partial class CleanupPlanner
{
    private static void PlanExtras(IReadOnlyList<Game> games, AppSettings settings, string dest, bool companions, bool others, List<CleanupStep> steps)
    {
        var items = games.SelectMany(g => g.Items).ToList();
        var gameFiles = items.SelectMany(i => i.Members.Append(i.Path)).Select(Path.GetFullPath).ToHashSet(PathNames.Comparer);
        var folderGames = items.Where(i => Directory.Exists(i.Path)).Select(i => i.Path).ToList();
        var taken = new HashSet<string>(PathNames.Comparer);

        // Not a game (in the list or not), not inside a folder game, not the app's own working files.
        bool IsExtra(string file) =>
            !gameFiles.Contains(file) && !folderGames.Any(f => PathNames.IsInside(file, f)) && !IsOwnFile(file)
            && FormatRegistry.Default.Detect(file) is null;

        if (companions) PlanCompanions(steps, dest, IsExtra, taken);
        if (others) PlanOtherFiles(games, items, settings, dest, IsExtra, taken, steps);
    }

    /// <summary>Saves and states: <c>Game.*</c> beside each game copied or converted into the new library, renamed with it.</summary>
    private static void PlanCompanions(List<CleanupStep> steps, string dest, Func<string, bool> isExtra, HashSet<string> taken)
    {
        var listings = new Dictionary<string, string[]>(PathNames.Comparer);
        foreach (var owner in steps.ToList())
        {
            if (owner.Kind is not (CleanupKind.Copy or CleanupKind.Convert) || owner.Target is null || owner.Sources.Count == 0) continue;
            // A multi-disc game's playlist step and a folder game have no one name to follow.
            if (owner.Target.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) || Directory.Exists(owner.Sources[0])) continue;
            var source = Path.GetFullPath(owner.Sources[0]);
            if (Path.GetDirectoryName(source) is not { } dir) continue;
            if (!listings.TryGetValue(dir, out var files))
            {
                try { files = Directory.GetFiles(dir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { files = []; }
                listings[dir] = files;
            }
            var stem = Path.GetFileNameWithoutExtension(source) + ".";
            var newStem = Path.GetFileNameWithoutExtension(owner.Target);
            var folder = Path.GetDirectoryName(owner.Target)!;
            foreach (var file in files.Select(Path.GetFullPath))
            {
                var name = Path.GetFileName(file);
                if (name.Length <= stem.Length || !name.StartsWith(stem, PathNames.Comparison) || !isExtra(file) || !taken.Add(file)) continue;
                steps.Add(new CleanupStep
                {
                    Kind = CleanupKind.Copy, GameKey = owner.GameKey, Game = owner.Game, SystemId = owner.SystemId, Sources = [file],
                    Target = Path.Combine(folder, newStem + name[(stem.Length - 1)..]), Root = owner.Root, Destination = dest, Owner = owner,
                    Reason = "Named like its game (a save or a state): copied beside it",
                });
            }
        }
    }

    /// <summary>
    /// Every other file in the folders the games were added from: into the system folder its folder's games go to
    /// (the one most of them go to), keeping where it is below that folder; as it is when no folder above it has games.
    /// </summary>
    private static void PlanOtherFiles(IReadOnlyList<Game> games, List<LibraryItem> items, AppSettings settings, string dest,
        Func<string, bool> isExtra, HashSet<string> taken, List<CleanupStep> steps)
    {
        var votes = new Dictionary<string, Dictionary<string, int>>(PathNames.Comparer);
        foreach (var game in games)
        {
            if (TargetFormats.ById(game.System) is not { } system || system.Follows("R5")) continue;
            foreach (var item in game.Items)
            {
                if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Path))) is not { } dir) continue;
                if (!votes.TryGetValue(dir, out var counts)) votes[dir] = counts = new(PathNames.Comparer);
                var to = FolderFor(item, system, settings, dest);
                counts[to] = counts.GetValueOrDefault(to) + 1;
            }
        }
        var places = votes.ToDictionary(v => v.Key, v => v.Value.MaxBy(p => p.Value).Key, PathNames.Comparer);

        var walk = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        foreach (var root in items.Select(i => Path.TrimEndingDirectorySeparator(Path.GetFullPath(i.Root))).Distinct(PathNames.Comparer))
        {
            List<string> files;
            try { files = Directory.EnumerateFiles(root, "*", walk).Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                if (taken.Contains(file) || !isExtra(file) || !taken.Add(file)) continue;
                var anchor = AnchorOf(Path.GetDirectoryName(file)!, root, places);
                var target = anchor is null
                    ? Path.Combine(dest, Path.GetRelativePath(root, file))
                    : Path.Combine(places[anchor], Path.GetRelativePath(anchor, file));
                var from = anchor ?? root;
                steps.Add(new CleanupStep
                {
                    Kind = CleanupKind.Copy, GameKey = "other files|" + from, Game = $"Other files in {Path.GetFileName(from)}", Sources = [file],
                    Target = Path.GetFullPath(target), Root = root, Destination = dest,
                    Reason = anchor is null ? "Not a game: copied as it is" : $"Not a game: copied into {RelativeFolder(dest, target)}",
                });
            }
        }
    }

    /// <summary>
    /// The folder whose games say where a file goes: its own, or the nearest one above it below the library folder.
    /// The library folder's own games only place the files directly in it, not every folder below.
    /// </summary>
    private static string? AnchorOf(string dir, string root, Dictionary<string, string> places)
    {
        if (PathNames.Same(dir, root)) return places.ContainsKey(root) ? root : null;
        for (var d = dir; d is not null && PathNames.IsInside(d, root); d = Path.GetDirectoryName(d))
            if (places.ContainsKey(d)) return d;
        return null;
    }

    /// <summary>The app's own files in a library folder: its trash, half-copied files, ES-DE's skip file and playlists it rewrites.</summary>
    private static bool IsOwnFile(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}.noi-trash{Path.DirectorySeparatorChar}", PathNames.Comparison)
        || file.EndsWith(".noi-part", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileName(file), EsdeSkipFile, StringComparison.OrdinalIgnoreCase)
        || file.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase);
}
