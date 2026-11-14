using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NoIntrovert.Core.Pipelines;

namespace NoIntrovert.Cli.Output;

/// <summary>How much noi prints (docs/requirements/cli.md §4.1).</summary>
public enum Verbosity { Normal, Verbose, Quiet, Silent, Json, Ndjson }

/// <summary>
/// Everything noi prints goes through here, so each mode behaves the same in every command: results on stdout,
/// warnings and errors on stderr (or above the live bars, which share the terminal), nothing at all with
/// --silent, and only JSON on stdout with --json / --ndjson.
/// </summary>
public sealed class Ui
{
    private readonly object _gate = new();
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private LiveArea? _live;

    public Ui(Verbosity verbosity, Term term, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        Verbosity = verbosity;
        Term = term;
        _out = stdout ?? Console.Out;
        _err = stderr ?? Console.Error;
        Paint = new Paint(term.Color && verbosity is not (Verbosity.Json or Verbosity.Ndjson));
        Sym = new Symbols(term.Unicode);
    }

    public Verbosity Verbosity { get; }
    public Term Term { get; }
    public Paint Paint { get; }
    public Symbols Sym { get; }

    public bool IsHuman => Verbosity is Verbosity.Normal or Verbosity.Verbose or Verbosity.Quiet;
    public bool IsJson => Verbosity is Verbosity.Json or Verbosity.Ndjson;
    public bool IsSilent => Verbosity == Verbosity.Silent;
    public bool ShowsResults => Verbosity is Verbosity.Normal or Verbosity.Verbose;
    public bool IsVerbose => Verbosity == Verbosity.Verbose;
    public TextWriter Stdout => _out;

    /// <summary>Whether jobs are running (their warnings are their results; the log keeps them).</summary>
    public bool InRun { get; set; }

    // ---- Lines ----

    /// <summary>A line of the command's output (results, lists, the plan): normal and verbose only.</summary>
    public void Line(string text = "")
    {
        if (!ShowsResults) return;
        Write(_out, text);
    }

    /// <summary>A line that's the command's answer (a list, a value): shown in every human mode, quiet too.</summary>
    public void Answer(string text = "")
    {
        if (!IsHuman) return;
        Write(_out, text);
    }

    /// <summary>"   Converted  Crash Bandicoot (USA).cue → .chd", Cargo-style: the word right-aligned and coloured.</summary>
    public void Status(string word, string text, Func<string, string>? color = null)
    {
        if (!ShowsResults) return;
        Write(_out, StatusText(word, text, color));
    }

    public string StatusText(string word, string text, Func<string, string>? color = null) =>
        $"{(color ?? Paint.BoldGreen)(word.PadLeft(12))}  {text}";

    public void Info(string text)
    {
        if (!ShowsResults) return;
        Write(_out, Paint.Dim(text));
    }

    public void Debug(string text)
    {
        if (Verbosity != Verbosity.Verbose) return;
        Write(_out, Paint.Dim(text));
    }

    /// <summary>A warning: every mode but silent, on stderr (JSON modes too: stdout stays data).</summary>
    public void Warn(string text)
    {
        if (IsSilent) return;
        WriteError($"{Paint.BoldYellow((Term.Unicode ? "⚠ " : "warning: "))}{text}");
    }

    /// <summary>An error: every mode but silent. <paramref name="hints"/> follow it, indented, the last thing said (§6.3).</summary>
    public void Error(string text, params string[] hints)
    {
        if (IsSilent) return;
        var sb = new StringBuilder($"{Paint.BoldRed(Sym.Fail)} {text}");
        foreach (var h in hints.Where(h => h.Length > 0)) sb.Append('\n').Append("  ").Append(h);
        WriteError(sb.ToString());
    }

    /// <summary>A failed file in quiet mode (normal mode shows it among the results).</summary>
    public void QuietFailure(string text)
    {
        if (Verbosity != Verbosity.Quiet) return;
        WriteError($"{Paint.BoldRed(Sym.Fail)} {text}");
    }

    /// <summary>The one-line summary quiet mode ends with.</summary>
    public void QuietSummary(string text)
    {
        if (Verbosity != Verbosity.Quiet) return;
        Write(_out, text);
    }

    private void WriteError(string text)
    {
        lock (_gate)
        {
            if (_live is not null) _live.Print(text);
            else _err.WriteLine(text);
        }
    }

    private void Write(TextWriter writer, string text)
    {
        lock (_gate)
        {
            if (_live is not null && ReferenceEquals(writer, _out)) _live.Print(text);
            else writer.WriteLine(text);
        }
    }

    // ---- Tables ----

    /// <summary>A table in box lines (or ASCII), sized to its contents; numbers right-aligned.</summary>
    public void Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, IReadOnlySet<int>? rightAligned = null, bool answer = false)
    {
        if (answer ? !IsHuman : !ShowsResults) return;
        var list = rows.ToList();
        var columns = headers.Count;
        var widths = Enumerable.Range(0, columns)
            .Select(c => Math.Max(Paint.Visible(headers[c]), list.Count == 0 ? 0 : list.Max(r => c < r.Count ? Paint.Visible(r[c]) : 0)))
            .ToArray();
        // Fit the terminal: the widest column gives way first.
        var total = widths.Sum() + 3 * columns + 1;
        while (Term.StdoutIsTerminal && total > Term.Width && widths.Max() > 12)
        {
            var widest = Array.IndexOf(widths, widths.Max());
            widths[widest]--;
            total--;
        }
        var u = Term.Unicode;
        var (tl, tm, tr, ml, mm, mr, bl, bm, br, h, v) = u
            ? ("┌", "┬", "┐", "├", "┼", "┤", "└", "┴", "┘", "─", "│")
            : ("+", "+", "+", "+", "+", "+", "+", "+", "+", "-", "|");
        string Rule(string l, string m, string r) => Paint.Dim(l + string.Join(m, widths.Select(w => new string(h[0], w + 2))) + r);
        string Cell(string text, int c)
        {
            var shown = Paint.Visible(text);
            if (shown > widths[c]) text = Fit(text, widths[c]);
            shown = Paint.Visible(text);
            var pad = new string(' ', Math.Max(0, widths[c] - shown));
            return rightAligned?.Contains(c) == true ? pad + text : text + pad;
        }
        string Row(IReadOnlyList<string> r) =>
            Paint.Dim(v) + string.Join(Paint.Dim(v), Enumerable.Range(0, columns).Select(c => " " + Cell(c < r.Count ? r[c] : "", c) + " ")) + Paint.Dim(v);
        var lines = new List<string> { Rule(tl, tm, tr), Row(headers.Select(Paint.Bold).ToList()), Rule(ml, mm, mr) };
        lines.AddRange(list.Select(Row));
        lines.Add(Rule(bl, bm, br));
        foreach (var line in lines) Write(_out, line);
    }

    /// <summary><paramref name="text"/> cut to <paramref name="width"/> visible characters, with an ellipsis (colour codes dropped).</summary>
    public string Fit(string text, int width)
    {
        var plain = System.Text.RegularExpressions.Regex.Replace(text, "\u001b\\[[0-9;]*m", "");
        if (plain.Length <= width) return plain;
        if (width <= Sym.Ellipsis.Length) return plain[..width];
        return plain[..(width - Sym.Ellipsis.Length)] + Sym.Ellipsis;
    }

    // ---- JSON ----

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions LineOptions = new() { WriteIndented = false };

    /// <summary>The --json document: written once, at the end.</summary>
    public void Json(JsonNode document)
    {
        if (Verbosity != Verbosity.Json) return;
        lock (_gate) _out.WriteLine(document.ToJsonString(JsonOptions));
    }

    /// <summary>One --ndjson event, as it happens.</summary>
    public void Event(string type, JsonObject fields)
    {
        if (Verbosity != Verbosity.Ndjson) return;
        var e = new JsonObject { ["event"] = type, ["time"] = DateTime.UtcNow.ToString("O") };
        foreach (var (k, v) in fields.ToList())
        {
            fields.Remove(k);
            e[k] = v;
        }
        lock (_gate) _out.WriteLine(e.ToJsonString(LineOptions));
    }

    // ---- Live progress ----

    /// <summary>Starts live bars for <paramref name="jobs"/> when the terminal can show them; null otherwise.</summary>
    public LiveArea? StartLive(IReadOnlyList<RunJob> jobs, string verb)
    {
        if (!Term.LiveBars || !ShowsResults) return null;
        lock (_gate)
        {
            _live = new LiveArea(_out, this, jobs, verb);
            _live.Start();
            return _live;
        }
    }

    public void StopLive()
    {
        LiveArea? live;
        lock (_gate)
        {
            live = _live;
            _live = null;
        }
        live?.Stop();
    }

    /// <summary>Asks a yes/no question on the terminal; false without an answer (no-input is the caller's to check).</summary>
    public bool Confirm(string question, bool defaultYes = false)
    {
        lock (_gate)
        {
            _err.Write($"{Paint.BoldCyan("?")} {question} {Paint.Dim(defaultYes ? "[Y/n]" : "[y/N]")} ");
            _err.Flush();
        }
        var answer = Console.ReadLine();
        if (answer is null) return false;
        answer = answer.Trim();
        if (answer.Length == 0) return defaultYes;
        return answer.StartsWith('y') || answer.StartsWith('Y');
    }
}

/// <summary>
/// The live bars at the bottom of the terminal: one line per running job and an overall line, redrawn ten times a
/// second; finished lines and messages print above them (as Cargo and BuildKit do). Uses ANSI cursor movement.
/// </summary>
public sealed class LiveArea(TextWriter output, Ui ui, IReadOnlyList<RunJob> jobs, string verb)
{
    private readonly object _gate = new();
    private readonly DateTime _started = DateTime.UtcNow;
    private Timer? _timer;
    private int _drawn;
    private int _tick;
    private bool _stopped;

    public void Start()
    {
        output.Write("\u001b[?25l"); // hide the cursor
        _timer = new Timer(_ => Redraw(), null, 0, 100);
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            _timer?.Dispose();
            Clear();
            output.Write("\u001b[?25h");
            output.Flush();
        }
    }

    public void Print(string text)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                output.WriteLine(text);
                return;
            }
            Clear();
            output.WriteLine(text);
            Draw();
        }
    }

    private void Redraw()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _tick++;
            Clear();
            Draw();
        }
    }

    private void Clear()
    {
        if (_drawn == 0) return;
        output.Write($"\r\u001b[{_drawn}A\u001b[0J");
        _drawn = 0;
    }

    private void Draw()
    {
        var p = ui.Paint;
        var s = ui.Sym;
        var width = Math.Max(40, ui.Term.Width - 1);
        var running = jobs.Where(j => j.State == RunJobState.Running).ToList();
        var lines = new List<string>();
        var spinner = s.Spinner[_tick % s.Spinner.Count];
        foreach (var job in running.Take(8))
        {
            var elapsed = job.Elapsed.TotalSeconds;
            var speed = elapsed > 0.5 ? job.DoneBytes / elapsed : 0;
            var eta = speed > 0 && job.Fraction > 0.01 ? TimeSpan.FromSeconds((job.Cost - job.DoneBytes) / speed) : (TimeSpan?)null;
            var right = $" {job.Fraction,4:P0} {Rate(speed),9} {Clock(eta),6}";
            var stage = (job.Stage.Length > 0 ? job.Stage : "working").ToLowerInvariant();
            var barWidth = Math.Clamp(width / 4, 10, 30);
            var nameWidth = Math.Max(10, width - barWidth - Paint.Visible(right) - 16 - 4);
            var name = ui.Fit(job.Name, nameWidth).PadRight(nameWidth);
            lines.Add($"{p.Cyan(spinner)} {name} {ui.Fit(stage, 14),-14} {Bar(job.Fraction, barWidth)}{right}");
        }
        if (running.Count > 8) lines.Add(p.Dim($"  … and {running.Count - 8} more"));
        var done = jobs.Count(j => j.State == RunJobState.Finished);
        var total = jobs.Sum(j => (double)j.Cost);
        var fraction = total > 0 ? jobs.Sum(j => j.DoneBytes) / total : 0;
        var wall = DateTime.UtcNow - _started;
        var left = fraction > 0.01 ? TimeSpan.FromSeconds(wall.TotalSeconds * (1 - fraction) / fraction) : (TimeSpan?)null;
        var head = $"{p.BoldCyan(verb.PadLeft(12))}  {done}/{jobs.Count} ";
        var tail = $" {fraction,4:P0}  {Clock(wall)}{(left is null ? "" : $" {Clock(left)} left")}";
        var overallBar = Math.Max(10, width - Paint.Visible(head) - Paint.Visible(tail) - 2);
        lines.Add(head + Bar(fraction, Math.Min(overallBar, 50)) + tail);
        foreach (var line in lines) output.WriteLine(line);
        output.Flush();
        _drawn = lines.Count;
    }

    private string Bar(double fraction, int width)
    {
        var p = ui.Paint;
        var s = ui.Sym;
        fraction = Math.Clamp(fraction, 0, 1);
        var full = (int)Math.Floor(fraction * width);
        var bar = new StringBuilder();
        bar.Append(p.Green(string.Concat(Enumerable.Repeat(s.BarFull, full))));
        if (full < width)
        {
            bar.Append(p.Dim(s.BarEdge));
            bar.Append(p.Dim(string.Concat(Enumerable.Repeat(s.BarEmpty, width - full - 1))));
        }
        return bar.ToString();
    }

    private static string Rate(double bytesPerSecond) => bytesPerSecond <= 0 ? "" : $"{bytesPerSecond / 1048576:0} MB/s";

    public static string Clock(TimeSpan? t) => t is not { } v ? "" : v.TotalHours >= 1 ? $"{(int)v.TotalHours}:{v.Minutes:00}:{v.Seconds:00}" : $"{v.Minutes}:{v.Seconds:00}";
}
