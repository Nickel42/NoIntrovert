<#
.SYNOPSIS
    NoIntrovert ROM test matrix: runs noi against every game in a test folder, one game at a time, with each
    preset, and records the result of every command for every game.

.DESCRIPTION
    For each game found under -Source (a file, or a game folder such as a Wii U or PS3 one) it runs the tests
    chosen with -Tests, each as its own noi call, and writes one row per (game, test, preset) to results.csv:

      info           noi info: the system, and the presets noi offers for the file
      verify         noi verify on the original (damage check and database lookup)
      convert        noi convert -p <preset> into a scratch folder, once for each preset (-Presets)
      verify-output  noi verify on what each convert made
      optimize       noi optimize --new-library into a scratch folder (verify, convert, rename, file)
      roundtrip      noi test roundtrip (lib-chd-kit: create, verify, extract, compare); disc images only

    The originals are never changed: convert and optimize always write into the run's work folder, which is
    emptied after each game unless -KeepOutputs is given.

    Each run gets its own folder under -Out with:
      results.csv    one row per noi call: game, system, test, preset, result, exit code, time, sizes, message
      matrix.csv     one row per game, one column per test/preset: the result, to see a game's whole story
      summary.txt    the totals and every result that wasn't a pass
      changes.csv    with -Baseline: what got better, worse or is new since that run
      run-info.txt   noi --version, the repo's commit, keys and database status
      logs\<game>\   for every call: the command line (.cmd.txt), noi's log (.log), stdout (.json), stderr (.err)

    Results: pass, fail, crash (unhandled exception or native crash), timeout, usage (exit 2: noi didn't accept
    the command line), damaged (exit 3), missing (exit 4: keys or databases), blocked, cancelled, skipped (noi
    had nothing to do, e.g. a preset that doesn't apply).

    Adding a test: write a Test-<Name> function like the others below and add it to $TestOrder.

.PARAMETER Source
    The folder of test games. Default: test-src in the repo, beside it, or in the current folder.

.PARAMETER Noi
    noi.exe to test. Default: $env:NOI_EXE, then the newest noi built in NoIntrovert.Cli\bin, then noi on PATH.

.PARAMETER Out
    Where to put this run's results. Default: test-results\<date-time> in the repo (git ignores it).

.PARAMETER Tests
    Which tests to run (comma list): info, verify, convert, verify-output, optimize, roundtrip, or all.
    Default: info, verify, convert, verify-output, optimize.

.PARAMETER Presets
    Which presets convert tries: usable (every preset noi offers that can be used for the file; default),
    recommended (just the starred one), or all (unusable ones too, to see how noi refuses them).

.PARAMETER PresetIds
    Only these preset ids (comma list), where noi offers them for a file. Overrides -Presets.

.PARAMETER Formats
    Also convert every game with each of these --to formats (comma list), e.g. chd,cue,iso,7z,zip,unpack.

.PARAMETER Include
    Only games whose path (relative to -Source) matches one of these wildcards (comma list): "PS1\*", "*.chd".

.PARAMETER Exclude
    Skip games whose relative path matches one of these wildcards (comma list).

.PARAMETER TimeoutMinutes
    A noi call still running after this long is stopped and recorded as a timeout. Default 60.

.PARAMETER Baseline
    An earlier run's results.csv (or its folder): changes.csv and the summary list what got better or worse.

.PARAMETER Resume
    Carry on an interrupted run: give its folder as -Out and the calls already recorded are skipped.

.PARAMETER KeepOutputs
    Keep what convert and optimize made (work\<game>\...) instead of deleting it after each game.

.PARAMETER DryRun
    Pass --dry-run to convert and optimize: test the planning only, quickly.

.PARAMETER NoDbUpdate
    Don't run noi db update before the tests (every test runs with --offline either way).

.PARAMETER Rehash
    Pass --rehash to verify, so every original is hashed again instead of using noi's hash cache.

.PARAMETER NoiArgs
    Extra arguments for every noi call, e.g. "--threads 4" or "--use-settings".

.EXAMPLE
    .\test-roms.ps1 -Source D:\test-src
.EXAMPLE
    .\test-roms.ps1 -Tests convert,verify-output -PresetIds chd-standard,rvz-smallest -Include "GC\*,Wii\*"
.EXAMPLE
    .\test-roms.ps1 -Baseline ..\test-results\20261119-101500
.EXAMPLE
    .\test-roms.ps1 -Out ..\test-results\20261119-101500 -Resume
#>
[CmdletBinding()]
param(
    [string]   $Source,
    [string]   $Noi,
    [string]   $Out,
    [string[]] $Tests = @('info', 'verify', 'convert', 'verify-output', 'optimize'),
    [ValidateSet('usable', 'recommended', 'all')]
    [string]   $Presets = 'usable',
    [string[]] $PresetIds = @(),
    [string[]] $Formats = @(),
    [string[]] $Include = @(),
    [string[]] $Exclude = @(),
    [int]      $TimeoutMinutes = 60,
    [string]   $Baseline,
    [switch]   $Resume,
    [switch]   $KeepOutputs,
    [switch]   $DryRun,
    [switch]   $NoDbUpdate,
    [switch]   $Rehash,
    [string]   $NoiArgs = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# ================================================================================================
#  Settings to maintain as NoIntrovert grows
# ================================================================================================

# The tests, in the order each game runs them. Each name has a Test-<Name> function below.
$TestOrder = @('info', 'verify', 'convert', 'verify-output', 'optimize', 'roundtrip')

# Files that are never games on their own: skipped when looking for games.
$IgnoreExtensions = @(
    '.txt', '.nfo', '.diz', '.md', '.pdf', '.htm', '.html', '.url', '.log', '.csv', '.json', '.xml', '.dat', '.ini', '.cfg',
    '.jpg', '.jpeg', '.png', '.gif', '.bmp', '.webp', '.mp4',
    '.sav', '.srm', '.state', '.mcr', '.mcd', '.dkey', '.key', '.keys', '.sfv', '.md5', '.sha1', '.db', '.lnk'
)

# A folder holding any of these is one game (the folder is tested, not the files in it).
$GameFolderMarkers = @(
    @('code', 'content', 'meta'),   # Wii U (Loadiine / extracted)
    @('PS3_GAME'),                  # PS3 folder (JB)
    @('PS3_DISC.SFB'),
    @('sce_sys')                    # PS Vita / PS4 folder
)

# Disc images that noi test roundtrip takes.
$RoundTripExtensions = @('.cue', '.gdi', '.iso', '.ccd', '.toc')

# What each noi exit code means (docs/requirements/cli.md; Exit in NoIntrovert.Cli/Cli/Session.cs).
$ExitResults = @{ 0 = 'pass'; 1 = 'fail'; 2 = 'usage'; 3 = 'damaged'; 4 = 'missing'; 5 = 'blocked'; 130 = 'cancelled' }

# Results that count as a problem for the script's exit code and the summary's "Problems" list.
$ProblemResults = @('fail', 'crash', 'timeout', 'usage', 'damaged', 'blocked', 'cancelled')

# ================================================================================================
#  Setup
# ================================================================================================

if ($PSVersionTable.PSVersion.Major -lt 5) { throw 'test-roms.ps1 needs PowerShell 5.1 or later.' }

$OnWindows = ($PSVersionTable.PSVersion.Major -lt 6) -or $IsWindows
$RepoRoot = Split-Path -Parent $PSScriptRoot

# Lists given through test-roms.cmd arrive as one "a,b,c" string: split them.
function Split-List([string[]] $Values) {
    @($Values | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
}
$Tests = Split-List $Tests
$PresetIds = Split-List $PresetIds
$Formats = Split-List $Formats
$Include = Split-List $Include
$Exclude = Split-List $Exclude
if ($Tests -contains 'all') { $Tests = $TestOrder }
foreach ($t in $Tests) {
    if ($TestOrder -notcontains $t) { throw "Unknown test '$t'. Choose from: $($TestOrder -join ', '), all." }
}
# Tests run in $TestOrder whatever order they were given in; verify-output needs convert.
$Tests = @($TestOrder | Where-Object { $Tests -contains $_ })
if (($Tests -contains 'verify-output') -and ($Tests -notcontains 'convert')) {
    Write-Warning 'verify-output checks what convert makes, so convert runs too.'
    $Tests = @($TestOrder | Where-Object { $Tests -contains $_ -or $_ -eq 'convert' })
}

function Find-Noi {
    if ($Noi) { return (Resolve-Path -LiteralPath $Noi).Path }
    if ($env:NOI_EXE) { return (Resolve-Path -LiteralPath $env:NOI_EXE).Path }
    $exe = if ($OnWindows) { 'noi.exe' } else { 'noi' }
    $bin = Join-Path $RepoRoot 'NoIntrovert.Cli\bin'
    if (Test-Path -LiteralPath $bin) {
        $built = Get-ChildItem -LiteralPath $bin -Recurse -File -Filter $exe -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($built) { return $built.FullName }
    }
    $onPath = Get-Command $exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw "Couldn't find $exe. Build NoIntrovert.Cli, or give it with -Noi <path> (or set NOI_EXE)."
}

function Find-Source {
    if ($Source) { return (Resolve-Path -LiteralPath $Source).Path }
    foreach ($candidate in @((Join-Path $RepoRoot 'test-src'), (Join-Path (Split-Path -Parent $RepoRoot) 'test-src'), (Join-Path (Get-Location) 'test-src'))) {
        if (Test-Path -LiteralPath $candidate -PathType Container) { return (Resolve-Path -LiteralPath $candidate).Path }
    }
    throw 'No test-src folder found in or beside the repo. Give the test folder with -Source <folder>.'
}

$NoiPath = Find-Noi
$SourceRoot = (Find-Source).TrimEnd('\', '/')
if (-not $Out) { $Out = Join-Path (Join-Path $RepoRoot 'test-results') (Get-Date -Format 'yyyyMMdd-HHmmss') }
if ($Resume -and -not (Test-Path -LiteralPath (Join-Path $Out 'results.csv'))) { throw "-Resume needs the folder of an earlier run as -Out; there's no results.csv in $Out." }
$Out = [IO.Path]::GetFullPath($Out)
if ($Out.StartsWith($SourceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "-Out is inside -Source; the results would be tested as games. Choose another folder."
}
$null = New-Item -ItemType Directory -Force -Path $Out
$LogRoot = Join-Path $Out 'logs'
$WorkRoot = Join-Path $Out 'work'
$ResultsCsv = Join-Path $Out 'results.csv'
$null = New-Item -ItemType Directory -Force -Path $LogRoot, $WorkRoot

$Rows = New-Object System.Collections.Generic.List[object]
$Done = @{}
if ($Resume) {
    foreach ($r in @(Import-Csv -LiteralPath $ResultsCsv)) {
        $Rows.Add($r)
        $Done["$($r.Game)|$($r.Test)|$($r.Preset)"] = $r
    }
}

# ================================================================================================
#  Running noi
# ================================================================================================

# One argument list as one command line, quoted as Windows programs (and .NET) read it back.
function ConvertTo-ArgString([string[]] $List) {
    $quoted = foreach ($a in $List) {
        if ($a -eq '') { '""' }
        elseif ($a -notmatch '[\s"]') { $a }
        else { '"' + (($a -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"' }
    }
    $quoted -join ' '
}

function Stop-ProcessTree($Process) {
    try {
        if ($OnWindows) { & taskkill.exe /PID $Process.Id /T /F 2>&1 | Out-Null }
        else { $Process.Kill() }
    }
    catch { }
}

function Get-SafeName([string] $Text) {
    $name = $Text -replace '[\\/:*?"<>|]', '_'
    if ($name.Length -gt 60) {
        $sha = [Security.Cryptography.SHA1]::Create()
        $hash = -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)) | Select-Object -First 4 | ForEach-Object { $_.ToString('x2') })
        $name = $name.Substring(0, 50) + '~' + $hash
    }
    $name
}

<#
  Runs noi once. Returns: ExitCode, TimedOut, Seconds, Json (parsed stdout, or $null), StdErr, Log (folder-relative).
  Every call gets --json, --no-input and its own log; the files for it are logs\<game>\<label>.*
#>
function Invoke-Noi([string] $Label, [string[]] $Arguments, [string] $LogDir, [switch] $Online) {
    $null = New-Item -ItemType Directory -Force -Path $LogDir
    $base = Join-Path $LogDir (Get-SafeName $Label)
    $stdout = "$base.json"; $stderr = "$base.err"; $log = "$base.log"
    $all = @($Arguments) + @('--json', '--no-input', '--no-color', '--progress', 'none', '--log', $log)
    if (-not $Online) { $all += '--offline' }
    $argString = ConvertTo-ArgString $all
    if ($NoiArgs) { $argString += ' ' + $NoiArgs }
    Set-Content -LiteralPath "$base.cmd.txt" -Value ('"' + $NoiPath + '" ' + $argString) -Encoding UTF8

    $watch = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $NoiPath -ArgumentList $argString -NoNewWindow -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $null = $p.Handle   # without this, Windows PowerShell loses the exit code
    $timedOut = -not $p.WaitForExit($TimeoutMinutes * 60 * 1000)
    if ($timedOut) { Stop-ProcessTree $p; $null = $p.WaitForExit(30000) }
    else { $p.WaitForExit() }
    $watch.Stop()

    $json = $null
    $text = if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout -Raw -Encoding UTF8 } else { '' }
    if ($null -eq $text) { $text = '' }
    if ($text -and $text.TrimStart().StartsWith('{')) {
        try { $json = $text | ConvertFrom-Json } catch { }
    }
    $err = if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr -Raw -Encoding UTF8 } else { '' }
    if ($null -eq $err) { $err = '' }

    [pscustomobject]@{
        ExitCode = if ($timedOut) { $null } else { $p.ExitCode }
        TimedOut = $timedOut
        Seconds  = [math]::Round($watch.Elapsed.TotalSeconds, 1)
        Json     = $json
        StdOut   = $text
        StdErr   = $err.Trim()
        Log      = $base.Substring($Out.Length + 1)
    }
}

# pass / fail / crash / ... from how the call ended.
function Get-Result($Run) {
    if ($Run.TimedOut) { return 'timeout' }
    if ($Run.StdErr -match 'Something went wrong') { return 'crash' }   # noi's unhandled-exception message
    $r = $ExitResults[[int]$Run.ExitCode]
    if ($r) { return $r }
    'crash'   # a native crash: 0xC0000005 and the like
}

# The message for a result: noi's own words where it gave some.
function Get-Message($Run, [string] $FromJson) {
    if ($Run.TimedOut) { return "stopped after $TimeoutMinutes min" }
    $parts = @()
    if ($FromJson) { $parts += $FromJson }
    if ($Run.StdErr) { $parts += (($Run.StdErr -split "`r?`n" | Where-Object { $_.Trim() }) | Select-Object -First 3) -join ' / ' }
    $known = $ExitResults.ContainsKey([int]$Run.ExitCode)
    if (-not $known) { $parts += ('exit code {0} (0x{1:X8})' -f $Run.ExitCode, $Run.ExitCode) }
    $msg = ($parts | Where-Object { $_ }) -join ' | '
    if ($msg.Length -gt 500) { $msg = $msg.Substring(0, 497) + '...' }
    $msg
}

function Get-Prop($Object, [string] $Name) {
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($p) { $p.Value } else { $null }
}

# ================================================================================================
#  Results
# ================================================================================================

$Counter = @{ Index = 0; Total = 0 }

function Add-Result {
    param($Game, [string] $Test, [string] $Preset = '', [string] $Result, $Run = $null, [string] $Message = '',
          $InBytes = $null, $OutBytes = $null, [string] $Detail = '')
    $ratio = ''
    if ($InBytes -and $OutBytes) { $ratio = '{0:0.0}%' -f (100.0 * $OutBytes / $InBytes) }
    $row = [pscustomobject]@{
        Game     = $Game.Relative
        System   = $Game.System
        Test     = $Test
        Preset   = $Preset
        Result   = $Result
        ExitCode = if ($Run) { $Run.ExitCode } else { '' }
        Seconds  = if ($Run) { $Run.Seconds } else { '' }
        InMB     = if ($InBytes) { [math]::Round($InBytes / 1MB, 1) } else { '' }
        OutMB    = if ($OutBytes) { [math]::Round($OutBytes / 1MB, 1) } else { '' }
        Ratio    = $ratio
        Detail   = $Detail
        Message  = $Message
        Log      = if ($Run) { $Run.Log } else { '' }
        When     = (Get-Date -Format 's')
    }
    $Rows.Add($row)
    $row | Export-Csv -LiteralPath $ResultsCsv -Append -NoTypeInformation -Encoding UTF8

    $color = switch ($Result) { 'pass' { 'Green' } 'skipped' { 'DarkGray' } 'missing' { 'Yellow' } default { 'Red' } }
    $what = if ($Preset) { "$Test $Preset" } else { $Test }
    $time = if ($Run) { '{0,7:0.0}s' -f $Run.Seconds } else { '        ' }
    Write-Host ('    {0,-9}' -f $Result) -ForegroundColor $color -NoNewline
    Write-Host (' {0,-34}{1}' -f $what, $time) -NoNewline
    if ($Result -ne 'pass' -and $Message) { Write-Host "  $Message" -ForegroundColor DarkGray } else { Write-Host '' }
}

function Test-Done($Game, [string] $Test, [string] $Preset = '') {
    $Done.ContainsKey("$($Game.Relative)|$Test|$Preset")
}

# ================================================================================================
#  The tests. Each takes the game and its state ($State: what earlier tests found and made).
# ================================================================================================

function Test-Info($Game, $State) {
    $run = Invoke-Noi 'info' @('info', $Game.Path) $Game.LogDir
    $result = Get-Result $run
    $file = $null
    if ($run.Json) { $file = @(Get-Prop $run.Json 'files') | Select-Object -First 1 }
    if ($file) {
        $Game.System = Get-Prop $file 'system'
        $State.Presets = @(Get-Prop $file 'presets' | Where-Object { $_ })
        $State.Recommended = Get-Prop $file 'preset'
    }
    $usable = @($State.Presets | Where-Object { (Get-Prop $_ 'usable') -eq $true } | ForEach-Object { Get-Prop $_ 'id' })
    $detail = "format: $(Get-Prop $file 'format'); recommended: $($State.Recommended); presets: $($usable -join ' ')"
    if ($result -eq 'pass' -and -not $Game.System) { $result = 'fail'; $detail = 'noi doesn''t know the system; ' + $detail }
    Add-Result $Game 'info' -Result $result -Run $run -Message (Get-Message $run '') -Detail $detail
}

function Test-Verify($Game, $State) {
    if (Test-Done $Game 'verify') { return }
    $cmd = @('verify', $Game.Path)
    if ($Rehash) { $cmd += '--rehash' }
    $run = Invoke-Noi 'verify' $cmd $Game.LogDir
    $files = @(Get-Prop $run.Json 'files' | Where-Object { $_ })
    $detail = ($files | ForEach-Object { "$(Get-Prop $_ 'result'): $(Get-Prop $_ 'game')" }) -join '; '
    $text = ($files | Where-Object { (Get-Prop $_ 'result') -notin @('ok', 'verified') } | ForEach-Object { Get-Prop $_ 'text' }) -join '; '
    Add-Result $Game 'verify' -Result (Get-Result $run) -Run $run -Message (Get-Message $run $text) -Detail $detail
}

# The convert runs for this game: presets (by -Presets / -PresetIds) and -Formats.
function Get-ConvertRuns($Game, $State) {
    $runs = @()
    $offered = @($State.Presets)
    if ($PresetIds.Count -gt 0) {
        $ids = @($offered | ForEach-Object { Get-Prop $_ 'id' })
        $runs += @($PresetIds | Where-Object { $ids -contains $_ } | ForEach-Object { @{ Name = $_; Args = @('-p', $_) } })
    }
    else {
        $chosen = switch ($Presets) {
            'all'         { $offered }
            'recommended' { @($offered | Where-Object { (Get-Prop $_ 'id') -eq $State.Recommended }) }
            default       { @($offered | Where-Object { (Get-Prop $_ 'usable') -eq $true }) }
        }
        $runs += @($chosen | ForEach-Object { $id = Get-Prop $_ 'id'; @{ Name = $id; Args = @('-p', $id) } })
        # Nothing offered (info failed, or not run): convert the way noi would by itself.
        if ($runs.Count -eq 0) { $runs += @{ Name = '(default)'; Args = @() } }
    }
    $runs += @($Formats | ForEach-Object { @{ Name = "to-$_"; Args = @('--to', $_) } })
    $runs
}

function Test-Convert($Game, $State) {
    foreach ($c in Get-ConvertRuns $Game $State) {
        $outDir = Join-Path $Game.WorkDir (Get-SafeName "convert-$($c.Name)")
        $converted = $false
        if (-not (Test-Done $Game 'convert' $c.Name)) {
            $cmd = @('convert', $Game.Path) + $c.Args + @('-o', $outDir, '--yes')
            if ($DryRun) { $cmd += '--dry-run' }
            $run = Invoke-Noi "convert-$($c.Name)" $cmd $Game.LogDir
            $result = Get-Result $run
            $files = @(Get-Prop $run.Json 'files' | Where-Object { $_ })
            $summary = Get-Prop $run.Json 'summary'
            $failed = @($files | Where-Object { (Get-Prop $_ 'result') -in @('failed', 'cancelled') })
            $skipped = @($files | Where-Object { (Get-Prop $_ 'result') -eq 'skipped' })
            $text = ($failed | ForEach-Object { Get-Prop $_ 'summary' }) -join '; '
            if ($result -eq 'pass' -and $files.Count -gt 0 -and $skipped.Count -eq $files.Count) {
                $result = 'skipped'; $text = ($skipped | ForEach-Object { Get-Prop $_ 'reason' }) -join '; '
            }
            elseif ($result -eq 'pass' -and $null -ne $run.Json -and $files.Count -eq 0) {
                $result = 'skipped'; $text = 'noi found nothing to convert'
            }
            $detail = ($files | ForEach-Object { "$(Get-Prop $_ 'result') $(Get-Prop $_ 'target')" }) -join '; '
            Add-Result $Game 'convert' $c.Name $result $run (Get-Message $run $text) `
                (Get-Prop $summary 'inBytes') (Get-Prop $summary 'outBytes') $detail
            $converted = ($result -eq 'pass') -and -not $DryRun
        }
        if ($Tests -contains 'verify-output' -and -not (Test-Done $Game 'verify-output' $c.Name)) {
            if ($converted -and (Get-ChildItem -LiteralPath $outDir -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1)) {
                Test-VerifyOutput $Game $c.Name $outDir
            }
        }
    }
}

function Test-VerifyOutput($Game, [string] $Preset, [string] $Folder) {
    $run = Invoke-Noi "verify-output-$Preset" @('verify', $Folder) $Game.LogDir
    $files = @(Get-Prop $run.Json 'files' | Where-Object { $_ })
    $detail = ($files | ForEach-Object { "$(Get-Prop $_ 'result'): $(Split-Path -Leaf (Get-Prop $_ 'path'))" }) -join '; '
    $text = ($files | Where-Object { (Get-Prop $_ 'result') -notin @('ok', 'verified') } | ForEach-Object { Get-Prop $_ 'text' }) -join '; '
    Add-Result $Game 'verify-output' $Preset (Get-Result $run) $run (Get-Message $run $text) -Detail $detail
}

function Test-Optimize($Game, $State) {
    if (Test-Done $Game 'optimize') { return }
    $library = Join-Path $Game.WorkDir 'optimize'
    $cmd = @('optimize', $Game.Path, '--new-library', $library, '--yes')
    if ($DryRun) { $cmd += '--dry-run' }
    $run = Invoke-Noi 'optimize' $cmd $Game.LogDir
    $steps = @(Get-Prop $run.Json 'steps' | Where-Object { $_ })
    $summary = Get-Prop $run.Json 'summary'
    $detail = ($steps | Where-Object { (Get-Prop $_ 'runs') -eq $true } | ForEach-Object { "$(Get-Prop $_ 'kind') -> $(Split-Path -Leaf ([string](Get-Prop $_ 'target')))" }) -join '; '
    $text = ($steps | Where-Object { (Get-Prop $_ 'reason') } | ForEach-Object { "$(Get-Prop $_ 'kind'): $(Get-Prop $_ 'reason')" } | Select-Object -First 3) -join '; '
    $result = Get-Result $run
    if ($result -eq 'pass' -and $summary -and [int](Get-Prop $summary 'failed') -gt 0) { $result = 'fail' }
    Add-Result $Game 'optimize' -Result $result -Run $run -Message (Get-Message $run $text) -Detail $detail
}

function Test-RoundTrip($Game, $State) {
    if (Test-Done $Game 'roundtrip') { return }
    if ($RoundTripExtensions -notcontains [IO.Path]::GetExtension($Game.Path).ToLowerInvariant()) { return }
    $run = Invoke-Noi 'roundtrip' @('test', 'roundtrip', $Game.Path) $Game.LogDir
    $records = @(Get-Prop $run.Json 'results' | Where-Object { $_ })
    $detail = ($records | ForEach-Object { "$(Get-Prop $_ 'Test'): $(if ((Get-Prop $_ 'Passed') -eq $true) { 'passed' } else { 'FAILED' })" }) -join '; '
    $text = ($records | Where-Object { (Get-Prop $_ 'Passed') -ne $true } | ForEach-Object { Get-Prop $_ 'Notes' }) -join '; '
    $result = Get-Result $run
    if ($result -eq 'pass' -and $null -eq $run.Json) { $result = 'skipped'; $text = $run.StdOut.Trim() }
    Add-Result $Game 'roundtrip' -Result $result -Run $run -Message (Get-Message $run $text) -Detail $detail
}

# ================================================================================================
#  Finding the games
# ================================================================================================

function Test-GameFolder([string] $Dir) {
    foreach ($markers in $GameFolderMarkers) {
        $all = $true
        foreach ($m in $markers) { if (-not (Test-Path -LiteralPath (Join-Path $Dir $m))) { $all = $false; break } }
        if ($all) { return $true }
    }
    $false
}

# Files a .cue / .gdi / .ccd / .mds brings with it: tested through it, not on their own.
function Get-Companions([IO.FileInfo] $File) {
    $dir = $File.DirectoryName
    $ext = $File.Extension.ToLowerInvariant()
    $names = @()
    try {
        switch ($ext) {
            '.cue' {
                foreach ($line in [IO.File]::ReadAllLines($File.FullName)) {
                    if ($line -match '^\s*FILE\s+(?:"([^"]+)"|(\S+))') { $names += $(if ($Matches[1]) { $Matches[1] } else { $Matches[2] }) }
                }
            }
            '.gdi' {
                foreach ($line in [IO.File]::ReadAllLines($File.FullName) | Select-Object -Skip 1) {
                    if ($line -match '^\s*\d+\s+\d+\s+\d+\s+\d+\s+(?:"([^"]+)"|(\S+))') { $names += $(if ($Matches[1]) { $Matches[1] } else { $Matches[2] }) }
                }
            }
            '.ccd' { $names += @('.img', '.sub') | ForEach-Object { $File.BaseName + $_ } }
            '.mds' { $names += $File.BaseName + '.mdf' }
        }
    }
    catch { }   # a cue that isn't text: noi will say so
    $names | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $dir $_)) }
}

function Test-Wildcards([string] $Relative, [string[]] $Patterns) {
    foreach ($p in $Patterns) { if ($Relative -like $p) { return $true } }
    $false
}

function Get-Games {
    $folders = @(Get-ChildItem -LiteralPath $SourceRoot -Recurse -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-GameFolder $_.FullName } | ForEach-Object { $_.FullName })
    # A game folder inside another one (content\ of a PS3 game): just the outer one.
    $folders = @($folders | Where-Object { $f = $_; -not ($folders | Where-Object { $f.StartsWith($_ + [IO.Path]::DirectorySeparatorChar) }) })
    $inFolder = { param($path) foreach ($f in $folders) { if ($path.StartsWith($f + [IO.Path]::DirectorySeparatorChar)) { return $true } }; $false }

    $files = @(Get-ChildItem -LiteralPath $SourceRoot -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $IgnoreExtensions -notcontains $_.Extension.ToLowerInvariant() -and -not (& $inFolder $_.FullName) })
    $companions = @{}
    foreach ($f in $files) { foreach ($c in Get-Companions $f) { $companions[$c.ToLowerInvariant()] = $true } }

    $paths = @($folders) + @($files | Where-Object { -not $companions.ContainsKey($_.FullName.ToLowerInvariant()) } | ForEach-Object { $_.FullName })
    foreach ($path in ($paths | Sort-Object)) {
        $relative = $path.Substring($SourceRoot.Length + 1)
        if ($Include.Count -gt 0 -and -not (Test-Wildcards $relative $Include)) { continue }
        if ($Exclude.Count -gt 0 -and (Test-Wildcards $relative $Exclude)) { continue }
        $safe = Get-SafeName $relative
        [pscustomobject]@{
            Path     = $path
            Relative = $relative
            System   = ''
            LogDir   = Join-Path $LogRoot $safe
            WorkDir  = Join-Path $WorkRoot $safe
        }
    }
}

# ================================================================================================
#  Reports
# ================================================================================================

function Get-BaselineCsv {
    if (-not $Baseline) { return $null }
    $path = $Baseline
    if (Test-Path -LiteralPath $path -PathType Container) { $path = Join-Path $path 'results.csv' }
    if (-not (Test-Path -LiteralPath $path)) { Write-Warning "No baseline at $path."; return $null }
    (Resolve-Path -LiteralPath $path).Path
}

function Write-Reports {
    $all = @($Rows)
    if ($all.Count -eq 0) { return @() }

    # matrix.csv: one row per game, one column per test (and preset).
    $columns = New-Object System.Collections.Generic.List[string]
    foreach ($r in $all) { $col = if ($r.Preset) { "$($r.Test):$($r.Preset)" } else { $r.Test }; if (-not $columns.Contains($col)) { $columns.Add($col) } }
    $matrix = foreach ($g in ($all | Group-Object Game)) {
        $line = [ordered]@{ Game = $g.Name; System = (@($g.Group | Where-Object { $_.System }) | Select-Object -First 1 | ForEach-Object { $_.System }) }
        foreach ($c in $columns) { $line[$c] = '' }
        foreach ($r in $g.Group) { $col = if ($r.Preset) { "$($r.Test):$($r.Preset)" } else { $r.Test }; $line[$col] = $r.Result }
        [pscustomobject]$line
    }
    $matrix | Export-Csv -LiteralPath (Join-Path $Out 'matrix.csv') -NoTypeInformation -Encoding UTF8

    # changes.csv against the baseline.
    $changes = @()
    $basePath = Get-BaselineCsv
    if ($basePath) {
        $before = @{}
        foreach ($r in @(Import-Csv -LiteralPath $basePath)) { $before["$($r.Game)|$($r.Test)|$($r.Preset)"] = $r }
        $changes = foreach ($r in $all) {
            $key = "$($r.Game)|$($r.Test)|$($r.Preset)"
            $old = $before[$key]
            $was = if ($old) { $old.Result } else { '(new)' }
            if ($was -eq $r.Result) { continue }
            $kind = if ($r.Result -eq 'pass') { 'better' } elseif ($was -eq 'pass') { 'WORSE' } elseif ($was -eq '(new)') { 'new' } else { 'changed' }
            [pscustomobject]@{ Change = $kind; Game = $r.Game; Test = $r.Test; Preset = $r.Preset; Was = $was; Now = $r.Result; Message = $r.Message; Log = $r.Log }
        }
        $changes = @($changes)
        $changes | Export-Csv -LiteralPath (Join-Path $Out 'changes.csv') -NoTypeInformation -Encoding UTF8
    }

    # summary.txt
    $sb = New-Object System.Text.StringBuilder
    $null = $sb.AppendLine("NoIntrovert ROM test matrix  $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
    $null = $sb.AppendLine("noi:     $NoiPath")
    $null = $sb.AppendLine("source:  $SourceRoot")
    $null = $sb.AppendLine("results: $Out")
    $null = $sb.AppendLine('')
    $results = @($all | ForEach-Object { $_.Result } | Sort-Object -Unique)
    $null = $sb.AppendLine(('{0,-16}' -f 'Test') + (($results | ForEach-Object { '{0,9}' -f $_ }) -join ''))
    foreach ($t in ($all | Group-Object Test | Sort-Object { [array]::IndexOf($TestOrder, $_.Name) })) {
        $cells = foreach ($res in $results) { '{0,9}' -f @($t.Group | Where-Object { $_.Result -eq $res }).Count }
        $null = $sb.AppendLine(('{0,-16}' -f $t.Name) + ($cells -join ''))
    }
    $null = $sb.AppendLine('')
    $null = $sb.AppendLine("Games: $(@($all | Group-Object Game).Count)   noi calls: $($all.Count)   time: $([math]::Round((($all | Where-Object { $_.Seconds -ne '' } | Measure-Object -Property Seconds -Sum).Sum) / 60, 1)) min")
    if ($basePath) {
        $null = $sb.AppendLine('')
        $null = $sb.AppendLine("Against $basePath : $(@($changes | Where-Object Change -eq 'better').Count) better, $(@($changes | Where-Object Change -eq 'WORSE').Count) worse, $(@($changes | Where-Object Change -eq 'changed').Count) changed, $(@($changes | Where-Object Change -eq 'new').Count) new")
        foreach ($c in @($changes | Where-Object { $_.Change -eq 'WORSE' })) {
            $null = $sb.AppendLine("  WORSE  $($c.Test) $($c.Preset)  $($c.Game): $($c.Was) -> $($c.Now)  $($c.Message)")
        }
    }
    $problems = @($all | Where-Object { $ProblemResults -contains $_.Result })
    if ($problems.Count -gt 0) {
        $null = $sb.AppendLine('')
        $null = $sb.AppendLine("Problems ($($problems.Count)):")
        foreach ($g in ($problems | Group-Object Result)) {
            $null = $sb.AppendLine("  $($g.Name):")
            foreach ($p in $g.Group) {
                $what = if ($p.Preset) { "$($p.Test) $($p.Preset)" } else { $p.Test }
                $null = $sb.AppendLine("    $what  $($p.Game)")
                if ($p.Message) { $null = $sb.AppendLine("        $($p.Message)") }
                if ($p.Log) { $null = $sb.AppendLine("        log: $($p.Log).log") }
            }
        }
    }
    $summary = $sb.ToString()
    Set-Content -LiteralPath (Join-Path $Out 'summary.txt') -Value $summary -Encoding UTF8
    Write-Host ''
    Write-Host $summary
    , $changes
}

# ================================================================================================
#  Run
# ================================================================================================

Write-Host "noi:      $NoiPath"
Write-Host "source:   $SourceRoot"
Write-Host "results:  $Out"
Write-Host "tests:    $($Tests -join ', ')"
Write-Host ''

# run-info.txt: what was tested, so runs can be compared later.
$info = New-Object System.Text.StringBuilder
$null = $info.AppendLine("started:  $(Get-Date -Format 's')")
$null = $info.AppendLine("command:  $($MyInvocation.Line)")
$null = $info.AppendLine("noi:      $NoiPath")
# Native stderr is an error under 'Stop' in Windows PowerShell: allow it while asking noi and git.
$ErrorActionPreference = 'Continue'
try { $null = $info.AppendLine((& $NoiPath --version 2>&1 | Out-String).Trim()) } catch { $null = $info.AppendLine("noi --version failed: $_") }
try {
    $commit = (& git -C $RepoRoot log -1 --format='%h %s (%ci)' 2>$null | Out-String).Trim()
    $dirty = (& git -C $RepoRoot status --porcelain 2>$null | Measure-Object).Count
    if ($commit) { $null = $info.AppendLine("repo:     $commit$(if ($dirty) { "  + $dirty changed files" })") }
} catch { }
$null = $info.AppendLine('')
try { $null = $info.AppendLine((& $NoiPath keys --no-color 2>&1 | Out-String).Trim()) } catch { }
try { $null = $info.AppendLine((& $NoiPath db --no-color 2>&1 | Out-String).Trim()) } catch { }
$ErrorActionPreference = 'Stop'
Set-Content -LiteralPath (Join-Path $Out 'run-info.txt') -Value $info.ToString() -Encoding UTF8

$games = @(Get-Games)
if ($games.Count -eq 0) { throw "No games found under $SourceRoot$(if ($Include -or $Exclude) { ' with -Include / -Exclude' })." }
Write-Host "$($games.Count) games found."

# Phase 1: info for every game, for its system and presets (needed by convert and by the database update).
$States = @{}
foreach ($g in $games) { $States[$g.Relative] = @{ Presets = @(); Recommended = $null } }
if ($Tests -contains 'info' -or $Tests -contains 'convert') {
    Write-Host ''
    Write-Host 'Identifying each game (noi info)...'
    foreach ($g in $games) {
        $previous = $Done["$($g.Relative)|info|"]
        Write-Host "  $($g.Relative)"
        if ($previous) {
            # Resumed: run info again for the presets, but keep the row already recorded.
            $run = Invoke-Noi 'info' @('info', $g.Path) $g.LogDir
            $file = @(Get-Prop $run.Json 'files') | Select-Object -First 1
            if ($file) { $g.System = Get-Prop $file 'system'; $States[$g.Relative].Presets = @(Get-Prop $file 'presets' | Where-Object { $_ }); $States[$g.Relative].Recommended = Get-Prop $file 'preset' }
        }
        else { Test-Info $g $States[$g.Relative] }
    }
}

# Phase 2: the databases for the systems found, once, so every test after it can run --offline.
if (-not $NoDbUpdate -and ($Tests -contains 'verify' -or $Tests -contains 'verify-output' -or $Tests -contains 'optimize')) {
    $systems = @($games | Where-Object { $_.System } | ForEach-Object { $_.System } | Sort-Object -Unique)
    Write-Host ''
    Write-Host "Updating the databases$(if ($systems) { " for $($systems -join ', ')" })..."
    $setup = [pscustomobject]@{ Relative = '(setup)'; System = ''; LogDir = Join-Path $LogRoot '_setup' }
    $cmd = @('db', 'update')
    if ($systems) { $cmd += @('--system', ($systems -join ',')) }
    $run = Invoke-Noi 'db-update' $cmd $setup.LogDir -Online
    if ((Get-Result $run) -eq 'usage' -and $systems) {
        # A system id db update doesn't take: update what's there instead.
        $run = Invoke-Noi 'db-update-all' @('db', 'update') $setup.LogDir -Online
    }
    Add-Result $setup 'db-update' -Result (Get-Result $run) -Run $run -Message (Get-Message $run '')
}

# Phase 3: every other test, game by game.
$i = 0
foreach ($g in $games) {
    $i++
    Write-Host ''
    Write-Host ("[{0}/{1}] {2}  ({3})" -f $i, $games.Count, $g.Relative, $(if ($g.System) { $g.System } else { 'system unknown' })) -ForegroundColor Cyan
    $state = $States[$g.Relative]
    foreach ($t in $Tests) {
        switch ($t) {
            'verify'    { Test-Verify $g $state }
            'convert'   { Test-Convert $g $state }   # runs verify-output too
            'optimize'  { Test-Optimize $g $state }
            'roundtrip' { Test-RoundTrip $g $state }
        }
    }
    if (-not $KeepOutputs -and (Test-Path -LiteralPath $g.WorkDir)) {
        Remove-Item -LiteralPath $g.WorkDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$changes = Write-Reports
$worse = @($changes | Where-Object { $_ -and $_.Change -eq 'WORSE' }).Count
$problems = @($Rows | Where-Object { $ProblemResults -contains $_.Result }).Count
if ($Baseline) { if ($worse -gt 0) { exit 1 } else { exit 0 } }
if ($problems -gt 0) { exit 1 }
exit 0
