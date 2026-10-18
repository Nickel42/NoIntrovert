using System.Diagnostics;
using System.Text;

namespace NoIntrovert.Core.Jobs;

/// <summary>
/// Runs a chdman executable chosen by the user, for side-by-side comparisons with
/// libchdman. Only the Library tests tab uses this.
/// </summary>
internal static class ChdmanExe
{
    public sealed record RunResult(int ExitCode, TimeSpan Elapsed, string Output);

    public static string CommandFor(DiscKind kind) => kind switch
    {
        DiscKind.Cd => "createcd",
        DiscKind.Dvd => "createdvd",
        _ => "createhd",
    };

    public static RunResult Create(string exePath, PreparedDisc disc, string output, AppSettings settings, CancellationToken token)
    {
        var args = new List<string> { CommandFor(disc.Kind), "-i", disc.InputPath, "-o", output, "-f" };
        if (!string.IsNullOrWhiteSpace(settings.Compression)) { args.Add("-c"); args.Add(settings.Compression.Trim()); }
        if (settings.HunkSize > 0) { args.Add("-hs"); args.Add(settings.HunkSize.ToString()); }
        if (settings.ThreadsPerJob > 0) { args.Add("-np"); args.Add(settings.ThreadsPerJob.ToString()); }
        return Run(exePath, args, token);
    }

    public static string Version(string exePath)
    {
        try
        {
            var r = Run(exePath, ["--help"], CancellationToken.None, TimeSpan.FromSeconds(10));
            var line = r.Output.Split('\n').FirstOrDefault(l => l.Contains("chdman", StringComparison.OrdinalIgnoreCase)) ?? "";
            return line.Trim();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "";
        }
    }

    private static RunResult Run(string exePath, IEnumerable<string> args, CancellationToken token, TimeSpan? timeout = null)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException("chdman was not found. Set its path on the Library tests tab.", exePath);

        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        var sw = Stopwatch.StartNew();
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using (token.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }))
        {
            if (timeout is { } t)
            {
                if (!process.WaitForExit(t)) { process.Kill(true); throw new TimeoutException("chdman did not answer."); }
            }
            process.WaitForExit();
        }
        sw.Stop();
        token.ThrowIfCancellationRequested();
        lock (output) return new RunResult(process.ExitCode, sw.Elapsed, output.ToString());
    }
}
