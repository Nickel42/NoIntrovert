using System.Text;

namespace NoIntrovert.Cli.Output;

public enum ColorMode { Auto, Always, Never }

public enum ProgressStyle { Auto, Bars, Lines, None }

/// <summary>
/// What the terminal noi writes to can do, decided once at start (docs/requirements/cli.md §4.2): colour only on a
/// terminal and never with NO_COLOR, TERM=dumb or --no-color (FORCE_COLOR turns it on); live bars only on an
/// interactive terminal outside CI; Unicode symbols unless --ascii or the console can't show them.
/// </summary>
public sealed class Term
{
    public bool Color { get; init; }
    public bool Unicode { get; init; }
    public bool LiveBars { get; init; }
    public bool StdoutIsTerminal { get; init; }
    public bool StderrIsTerminal { get; init; }
    public bool StdinIsTerminal { get; init; }

    public int Width
    {
        get
        {
            if (Environment.GetEnvironmentVariable("COLUMNS") is { } c && int.TryParse(c, out var cols) && cols > 20) return cols;
            try { return StdoutIsTerminal && Console.WindowWidth > 20 ? Console.WindowWidth : 100; }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or InvalidOperationException) { return 100; }
        }
    }

    public static bool InCi => Environment.GetEnvironmentVariable("CI") is { Length: > 0 } ci && ci != "0" && !ci.Equals("false", StringComparison.OrdinalIgnoreCase);

    public static Term Detect(ColorMode color, ProgressStyle progress, bool ascii)
    {
        var stdoutTty = !Console.IsOutputRedirected;
        var stderrTty = !Console.IsErrorRedirected;
        var stdinTty = !Console.IsInputRedirected;
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR") is { Length: > 0 };
        var dumb = string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase);
        var force = Environment.GetEnvironmentVariable("FORCE_COLOR") is { Length: > 0 } f && f != "0";
        var useColor = color switch
        {
            ColorMode.Always => true,
            ColorMode.Never => false,
            _ => force || (stdoutTty && !noColor && !dumb),
        };
        if (useColor || stdoutTty) EnableVirtualTerminal();
        var unicode = !ascii && CanShowUnicode();
        var bars = progress switch
        {
            ProgressStyle.Bars => stdoutTty,
            ProgressStyle.Lines or ProgressStyle.None => false,
            _ => stdoutTty && !InCi && !dumb,
        };
        return new Term
        {
            Color = useColor, Unicode = unicode, LiveBars = bars,
            StdoutIsTerminal = stdoutTty, StderrIsTerminal = stderrTty, StdinIsTerminal = stdinTty,
        };
    }

    private static bool CanShowUnicode()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows Terminal, VS Code and conhost all show UTF-8 once the console is set to it.
                Console.OutputEncoding = new UTF8Encoding(false);
                return true;
            }
            var lang = Environment.GetEnvironmentVariable("LC_ALL") ?? Environment.GetEnvironmentVariable("LC_CTYPE") ?? Environment.GetEnvironmentVariable("LANG") ?? "";
            return lang.Length == 0 || lang.Contains("UTF-8", StringComparison.OrdinalIgnoreCase) || lang.Contains("utf8", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { return false; }
    }

    // ---- Windows: turn on ANSI escape sequences in the console (Windows 10 and later) ----

    private static void EnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            foreach (var std in new[] { -11, -12 }) // STD_OUTPUT_HANDLE, STD_ERROR_HANDLE
            {
                var handle = GetStdHandle(std);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1)) continue;
                if (GetConsoleMode(handle, out var mode)) SetConsoleMode(handle, mode | 0x0004); // ENABLE_VIRTUAL_TERMINAL_PROCESSING
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}

/// <summary>Colours as ANSI codes (the 16 standard ones, so light and dark terminals both read them), or nothing.</summary>
public sealed class Paint(bool color)
{
    public bool Enabled { get; } = color;

    private string Wrap(string code, string text) => Enabled && text.Length > 0 ? $"\u001b[{code}m{text}\u001b[0m" : text;

    public string Red(string t) => Wrap("31", t);
    public string Green(string t) => Wrap("32", t);
    public string Yellow(string t) => Wrap("33", t);
    public string Blue(string t) => Wrap("34", t);
    public string Magenta(string t) => Wrap("35", t);
    public string Cyan(string t) => Wrap("36", t);
    public string Bold(string t) => Wrap("1", t);
    public string Dim(string t) => Wrap("2", t);
    public string BoldGreen(string t) => Wrap("1;32", t);
    public string BoldRed(string t) => Wrap("1;31", t);
    public string BoldYellow(string t) => Wrap("1;33", t);
    public string BoldCyan(string t) => Wrap("1;36", t);

    /// <summary>The length of <paramref name="text"/> as shown: without its escape codes.</summary>
    public static int Visible(string text)
    {
        var n = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\u001b')
            {
                while (i < text.Length && text[i] != 'm') i++;
                continue;
            }
            if (!char.IsLowSurrogate(text[i])) n++;
        }
        return n;
    }
}

/// <summary>The symbols results are marked with (never colour alone), in Unicode or plain ASCII.</summary>
public sealed class Symbols(bool unicode)
{
    public string Ok => unicode ? "✔" : "OK";
    public string Differs => unicode ? "≠" : "!=";
    public string Unknown => "?";
    public string None => unicode ? "–" : "-";
    public string Fail => unicode ? "✖" : "X";
    public string Arrow => unicode ? "→" : "->";
    public string Warn => unicode ? "⚠" : "!";
    public string Star => unicode ? "★" : "*";
    public string Dot => unicode ? "·" : "-";
    public string BarFull => unicode ? "━" : "#";
    public string BarEmpty => unicode ? "━" : "-";
    public string BarEdge => unicode ? "╺" : ">";
    public string Ellipsis => unicode ? "…" : "...";
    public IReadOnlyList<string> Spinner { get; } = unicode ? ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"] : ["|", "/", "-", "\\"];
}
