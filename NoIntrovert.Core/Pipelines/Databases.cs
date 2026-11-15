using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Emulators;
using NoIntrovert.Core.Esde;
using NoIntrovert.Core.Formats;

namespace NoIntrovert.Core.Pipelines;

/// <summary>
/// The downloaded data the app keeps up to date: the No-Intro and Redump DATs (Verify &amp; match), ES-DE's
/// system folders (the ES-DE layout) and the emulator data ("Works with"), as Settings › Library shows them.
/// </summary>
public static class Databases
{
    /// <summary>The DATs every known system uses.</summary>
    public static IReadOnlyList<string> AllDatKeys() =>
        TargetFormats.All.SelectMany(s => s.Dats ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The DATs for <paramref name="keys"/>, downloaded when they're newer (or missing); offline, the copies here stay.</summary>
    public static async Task<DatUpdateResult> UpdateDatsAsync(IReadOnlyList<string> keys, ILog log, CancellationToken token = default)
    {
        if (keys.Count == 0) return new DatUpdateResult([], null);
        var result = await new DatUpdater(DatStore.Default).UpdateAsync(keys, token).ConfigureAwait(false);
        if (result.Changed) log.Info($"Game databases: downloaded {string.Join(", ", result.Downloaded.Select(k => k[(k.IndexOf('/') + 1)..]))}");
        if (result.Problem is { } problem) log.Warn($"Game databases: couldn't check for updates ({problem}); using the copies already here.");
        return result;
    }

    /// <summary>ES-DE's system list: checked at most once a day unless <paramref name="force"/>.</summary>
    public static async Task<EsdeUpdateResult?> UpdateEsdeAsync(bool force, ILog log, CancellationToken token = default)
    {
        var updater = new EsdeUpdater(EsdeStore.Default);
        if (!force && !updater.IsDue(DateTime.UtcNow)) return null;
        var result = await updater.UpdateAsync(token).ConfigureAwait(false);
        if (result.Downloaded) log.Info($"ES-DE systems: updated to release {result.Ref}.");
        if (result.Problem is { } problem) log.Warn($"ES-DE systems: couldn't check for updates ({problem}); using the list already here.");
        foreach (var missing in result.Missing) log.Warn(missing);
        return result;
    }

    public static async Task<EmulatorCheckResult> UpdateEmulatorDataAsync(ILog log, CancellationToken token = default)
    {
        var result = await EmulatorCatalog.CheckNowAsync(token).ConfigureAwait(false);
        if (result.Updated) log.Info($"Emulator data: updated ({result.Source}).");
        else if (result.Problem is null) log.Info($"Emulator data: already up to date ({result.Source}).");
        if (result.Problem is { } problem) log.Warn($"Emulator data: couldn't check for updates ({problem}); using the data already here.");
        return result;
    }

    public static string DatStatus => DatStore.Default.Describe();
    public static string EsdeStatus => EsdeStore.Default.Describe();
    public static string EmulatorStatus => EmulatorCatalog.Current.SourceText;
}
