# Requirement: Command-line interface (`noi`)

Status: **Draft for review** (2026-11-14)

## 1. Goal

Everything the app does, from a terminal or a script: **Optimize, Convert, Verify, Organize** (with undo),
the game databases, keys, presets and settings, and the Library tests. The CLI follows the app's own rule:
**pick files → choose a preset → choose where to save → run**. Presets come first; the individual
parameters exist, but you only need them when you'd choose Custom in the app.

It must be:

- **Easy to read.** Commands are the app's verbs. Option names are the words the app uses (`--replace`,
  `--layout esde`, `--preset smallest`). `noi /?`, `noi -h` and `noi help convert` all explain themselves,
  with examples first.
- **Modern in a terminal.** Colour, live progress bars for each parallel job, a clear summary table, and
  "did you mean" suggestions.
- **Quiet in a script.** `--quiet` prints only problems and the one-line summary; `--silent` prints nothing
  at all and answers with the exit code. Colour and animation turn off by themselves when the output isn't a
  terminal.
- **As safe as the app.** Nothing is removed unless you asked for it, Replace still verifies before removing,
  Organize shows its plan before it touches anything, and every Organize run can be undone.

## 2. Research: what the best CLIs do

| CLI | What we take from it |
|---|---|
| [clig.dev](https://clig.dev/) (Command Line Interface Guidelines) | The rule book this design follows: `-h/--help` everywhere, examples first in help, data on stdout and messages on stderr, colour only on a TTY and never with `NO_COLOR`, `-q`, `--json`, `--dry-run`, `--no-input`, prompts only when stdin is a terminal and every prompt also a flag, confirmation scaled to the risk, Ctrl-C that stops at once. |
| **GitHub `gh`** | Verb commands with one consistent set of flags; help that leads with examples; `--json` on every command that reports something. |
| **Cargo / uv** | Right-aligned coloured status words (`Converting`, `Verified`, `Finished`), one line per finished item above a live bar, a one-line summary with elapsed time. |
| **Terraform** (`plan` / `apply`) | Show the plan, ask, then apply; save the plan to a file and apply exactly that plan later. This is Organize's dry run. |
| **rclone** | `--dry-run` on everything that writes, `--transfers` (our `--jobs`), live per-file progress with speed and ETA, include/exclude filters. |
| **ripgrep / fd** | Good defaults with no flags, `--color auto|always|never`, recursion on by default, plain output when piped. |
| **Docker BuildKit** | Several parallel tasks each with its own live line, collapsing to a log when the terminal isn't interactive. |
| **winget / dotnet** | Windows conventions: `/?` for help, `--accept`-style flags for unattended runs, tab completion. |
| **chdman, DolphinTool, nsz** (the tools this replaces) | What *not* to copy: one file per call, one-letter flags (`-c cdlz,cdzl`), codec names you have to know, and no summary. `noi` takes many files and folders at once and picks the format per system. |

Libraries for .NET 10 (the app's runtime):

- **System.CommandLine 2.0** (Microsoft, stable since Nov 2025) for parsing. It brings `-h`, `--help`,
  `-?` and `/?` as help aliases, typo suggestions, response files (`@files.txt`), tab completion, and it is
  trimming- and AOT-friendly.
- **Spectre.Console** (0.56, in stabilisation) for output: multi-task progress bars, spinners, tables,
  trees, markup colours, and terminal detection (TTY, `NO_COLOR`, colour depth, legacy Windows consoles).
  Spectre.Console.Cli is not used: one parser and Spectre only for rendering keeps the help under our
  control.

## 3. Shape

### 3.1 A separate console executable

`NoIntrovert.exe` is a `WinExe` (GUI subsystem). On Windows, such a program can't write to the console
that started it: the prompt comes back at once and output interleaves with it. So the CLI is its own small
program, **`noi`** (`noi.exe` on Windows), shipped in every download beside `NoIntrovert`:

```
NoIntrovert-win-x64.zip
  NoIntrovert.exe      the app
  noi.exe              the command line
```

It shares the settings file, the hash and match caches, the DATs, the run journals and the trash with the
app, so a Verify done in one is known to the other. `noi gui [paths]` opens the app with those files loaded.

### 3.2 Commands

```
noi <command> [paths…] [options]
```

| Command | Does | The app's |
|---|---|---|
| `noi optimize <paths>` | Verify, convert to the best format, rename and file into system folders, in one go | Optimize page |
| `noi convert <paths>` | Convert each file to its system's recommended format, or the one you name | Convert page |
| `noi verify <paths>` | Check for damage and look each game up in No-Intro / Redump | Verify page |
| `noi organize <paths>` | Plan, then convert, rename, move and remove duplicates (undoable) | Organize page |
| `noi runs [list\|show\|undo\|empty-trash]` | Past Organize/Optimize runs: undo one, or empty its trash | Past runs list |
| `noi info <file>` | What a file is: system, format, recommended target, presets, keys, emulators that play it | Convert side panel |
| `noi presets [system]` | The presets for each system, and which is the default | Preset pickers |
| `noi db [status\|update]` | Game databases, ES-DE system list and emulator data: age, and update now | Settings › Library |
| `noi keys` | Which Wii U, 3DS, Switch, PS3 and Vita keys were found, and where | Settings › Keys |
| `noi settings [list\|get\|set\|path\|reset]` | Read and change the shared settings | Settings page |
| `noi test [roundtrip\|compare]` | Library tests (power user): round trip, and comparison with chdman | Library tests |
| `noi gui [paths]` | Open the app with these files | — |
| `noi completion <shell>` | Tab completion for PowerShell, bash, zsh, fish | — |
| `noi help [command]` | Same as `--help` | — |

`noi` with no command prints the short help (§6.1), never runs anything.

### 3.3 Paths

Every command that works on files takes any mix of **files and folders**; folders are searched with their
subfolders, as when you drop them on the window.

| Option | Meaning |
|---|---|
| `<paths…>` | Files and folders. `-` reads one path per line from stdin. `@list.txt` reads them from a file. |
| `--no-recurse` | Only the folder itself, not its subfolders (Settings › Output › Scan subfolders) |
| `--include <glob>` | Only files that match (repeatable): `--include "*.cue" --include "*.iso"` |
| `--exclude <glob>` | Leave these out (repeatable): `--exclude "**/hidden/**"` |
| `--library` | Use the folders in Settings › Library instead of paths |
| `--system <id>` | Only games of these systems (repeatable or comma list): `--system psx,ps2` |

## 4. Global options

These work with every command, in any position.

### 4.1 How much it prints

| Option | Prints | Prompts |
|---|---|---|
| *(default)* | Live progress, a line per finished file, a summary table | Yes, on a terminal |
| `-v`, `--verbose` | Also each step (hashing, decoding, codecs chosen, temp files) | Yes |
| `-q`, `--quiet` | Only warnings, errors and the one-line summary; no progress bars | No (`--no-input`) |
| `-s`, `--silent` | Nothing at all. The exit code (§7) and the log file say how it went | No (`--no-input`) |
| `--json` | One JSON document on stdout at the end (results, summary); human messages off | No |
| `--ndjson` | One JSON event per line as it happens (start, progress, file done, summary), for front ends | No |

- `--silent` still writes the session log (`logs/noi-yyyyMMdd-HHmmss.log`), so an unattended run can be
  checked later; `--log <file>` writes it elsewhere, `--no-log` not at all.
- Anything that would need an answer fails instead of waiting, and names the flag that answers it
  (`Replace the originals needs --yes when nothing can be asked`).
- `--json` and `--ndjson` write data to **stdout** only; warnings and errors still go to **stderr** (empty
  with `--silent`).

### 4.2 Look

| Option | Meaning |
|---|---|
| `--color auto\|always\|never` | `auto` (default): colour on a terminal, off when piped, with `NO_COLOR` set, `TERM=dumb` or `--no-color`. `FORCE_COLOR` turns it on. |
| `--no-color` | Same as `--color never` |
| `--progress auto\|bars\|lines\|none` | `auto`: bars on an interactive terminal, else a plain line per file (CI logs stay readable) |
| `--ascii` | No Unicode symbols or box lines (old consoles, some CI logs) |

### 4.3 Running

| Option | Meaning | Default |
|---|---|---|
| `-n`, `--dry-run` | Say what would happen; write nothing | off |
| `-y`, `--yes` | Answer yes to every confirmation (Replace the originals, Organize's plan, undo) | off |
| `--no-input` | Never prompt; fail if an answer is needed | on when stdin isn't a terminal |
| `-j`, `--jobs <n>` | Files converted at the same time | Settings › Performance (2) |
| `--threads <n>` | Threads per job; `0` = share the processors | `0` |
| `--temp <folder>` | Temp folder | Settings › Performance |
| `--offline` | Don't check for newer databases; use the cached ones | off |
| `--settings <file>` | Use another settings file | the app's |
| `--defaults` | Ignore the saved settings: every value is the recommended one (reproducible scripts) | off |
| `-h`, `-?`, `/?`, `--help` | Help for the command | — |
| `--version` | Version, the libraries' versions, and the build's platform | — |

Values come from, in order: **flags → environment variables → the settings file → the recommended
defaults.** `noi` reads the app's settings (chosen presets, layout, keys folders, jobs) so it behaves like
the app, but never writes them, except `noi settings set`.

Environment variables: `NO_COLOR`, `FORCE_COLOR`, `NOI_SETTINGS`, `NOI_TEMP`, `NOI_JOBS`, `NOI_OFFLINE=1`,
`CI` (when set: `--progress lines`, `--no-input`).

## 5. Commands in detail

### 5.1 Where new files go (Convert, Optimize, Organize)

The app's **Save to**, as three choices that can't be combined:

| Option | Meaning |
|---|---|
| *(default)* | Next to the originals |
| `-o`, `--output <folder>` | Into a folder, laid out by `--layout` |
| `--replace` | Replace the originals ⚠: each goes (to the Recycle Bin/Trash) only after its new file verifies. Asks once on a terminal; needs `--yes` otherwise. |

| Option | Meaning |
|---|---|
| `--layout esde\|romm\|keep\|flat` | `esde` (default): `roms/<system>/` with ES-DE's names. `romm`: RomM's platform names. `keep`: the same subfolders as the originals. `flat`: no subfolders. |
| `--overwrite` | Overwrite a file that's in the way (off: the new file gets " (2)") |
| `--no-recycle-bin` | Removed originals are deleted, not sent to the Recycle Bin / Trash |

### 5.2 `noi convert`

Each file gets its system's **recommended preset** unless you choose another. A preset names *what* it
becomes and *how*.

| Option | Meaning |
|---|---|
| `-p`, `--preset <id>` | For every file the preset fits: `noi convert . -p rvz-smallest` |
| `-p`, `--preset <system>=<id>` | For one system only (repeatable): `-p ps2=zso-opl -p gc=iso-loaders` |
| `-t`, `--to <format>` | Just the format, with its standard preset: `chd`, `rvz`, `iso`, `cue`, `gdi`, `img`, `wua`, `cci`, `zcci`, `nsz`, `xcz`, `nsp`, `xiso`, `xbox-cci`, `zar`, `ps3-iso`, `ps3-folder`, `cso`, `zso`, `7z`, `zip`, `unpack` |
| `--keep <system>` | Leave these systems as they are (repeatable) |

Files a preset doesn't fit (a `--to rvz` given a PS1 disc) are listed as skipped, never forced. `--to`
and `--preset` without a system apply to every file they can apply to; with `<system>=` they win for that
system. Preset ids are the app's own (`chd-standard`, `bincue-redump`, `gdi-gdemu`, `rvz-balanced`,
`cso-psp`, `nsz-standard`…), and presets saved from Custom in the app are found by name:
`-p "My CHD settings"`. `noi presets` lists them.

**Custom values** (the parameters the app shows under Custom). Shown only by `noi convert --help-all`:

| Option | For | Values |
|---|---|---|
| `--chd-codecs <list>` | CHD | `cdlz,cdzl,cdfl` … (default: chdman's for the disc type) |
| `--chd-hunk <bytes>` | CHD | power user; `0` = default |
| `--iso-mode auto\|cd\|dvd` | CHD from ISO | `auto` |
| `--rvz-level <1-22>` | RVZ | `5` |
| `--rvz-block <size>` | RVZ | `128K` |
| `--rvz-codec zstd\|lzma\|lzma2\|bzip2\|purge\|none` | RVZ | `zstd` |
| `--scrub` | RVZ | off |
| `--archive-level fast\|normal\|ultra` | 7z / zip | `normal` |
| `--no-solid` | 7z | solid on |
| `--group per-item\|per-entry\|single` | 7z / zip | `per-item` |
| `--archive-name <name>` | `--group single` | `Archive` |
| `--extract-format auto\|cue\|gdi\|iso\|raw` | CHD → disc image | `auto` |
| `--single-bin` | CHD → cue/bin | one .bin per track |
| `--unpack-here` | unpack | into its own folder |
| `--no-verify` | CHD, RVZ, archives | verify on. Ignored with `--replace`, which always verifies. |

Keys for Wii U, 3DS, Switch, PS3 and Vita are found where the app finds them; `noi keys` says which.
To use others for one run: `--keys-wiiu <keys.txt>`, `--keys-3ds <folder>`, `--keys-switch <prod.keys>`,
`--keys-ps3 <dkey folder>`, `--zrif <file>`. These take **paths**, never key values.

### 5.3 `noi verify`

Checks each file for damage (CHD against its SHA-1, 7z/zip tested, GameCube/Wii decoded), then looks it up.
Never changes a file, unless `--fix-headers`.

| Option | Meaning |
|---|---|
| `--fix-headers` | The *Verify and fix CHD headers* preset: rewrite a wrong header SHA-1 when the data is intact |
| `--report <file>` | Also write the results as `.csv` or `.json` (by extension) |
| `--only <results>` | Show only these: `damaged`, `misnamed`, `unknown`, `no-db`, `unchecked`, `ok` |
| `--rehash` | Ignore the hash cache |

Results and colours: **✔ Verified** (green), **≠ Name differs** (yellow, with the expected name),
**? Not in the database** (blue), **– No database** / **– Can't be checked yet** (dim),
**✖ Damaged** (red). Exit code 3 when anything is damaged (§7), so `noi verify -s D:\roms || …` works.

### 5.4 `noi organize`

Terraform's plan → apply. Only files Verify has checked are renamed and moved; unchecked files are listed
with a hint (`--verify-first`, or use `noi optimize`).

| Option | Meaning |
|---|---|
| *(default)* | Show the plan, then ask *Run these 214 steps? [y/N]*. Without a terminal: show the plan and stop (exit 0), unless `--yes`. |
| `-n`, `--dry-run` | Show the plan only |
| `-y`, `--yes` | Run without asking |
| `--plan-out <file>` | Save the plan as JSON (to review, or edit by removing steps) |
| `--plan <file>` | Run exactly this saved plan; refused if a file in it changed since |
| `--new-library <folder>` | Build a new library there; the files in place don't change at all |
| `--no-copy-unmatched` | With `--new-library`: leave out games that aren't matched |
| `--layout esde\|romm\|keep\|flat` | As §5.1 (`keep` here: convert and rename in place) |
| `--no-convert`, `--no-rename`, `--no-move`, `--no-dedupe` | Turn off a whole kind of step |
| `--verify-first` | Verify the files that haven't been, then plan |
| Convert's `-p`, `-t`, `--keep` | Which formats the convert steps make |

The plan prints grouped by game: `NES/Mario.nes → roms/nes/Super Mario Bros. (World).7z`, with each step's
kind, clashes in red (nothing is ever overwritten), and the space saved.

### 5.5 `noi optimize`

Verify, then Organize's plan and run, game by game. Takes Organize's options (`--new-library`,
`--layout`, `--no-…` steps, presets) and its safety: it shows the plan and asks, or runs with `--yes`.
`-n` stops after Verify and the plan.

### 5.6 `noi runs`

```
noi runs                       list the last 20 runs: id, when, what it did, trash size
noi runs show <id|last>        every step of a run
noi runs undo <id|last>        undo it (asks; --yes to skip); lists any step that can't be undone
noi runs empty-trash <id|all>  send a run's trash to the Recycle Bin (asks; --yes)
```

### 5.7 The rest

- **`noi info <file>`**: system, format, size, the Verify result if known, the recommended target, every
  preset for it with ★ on the default, keys needed and found, and "Works with" (emulators). `--json` too.
- **`noi presets [system]`**: a table per system: id, format, name, what it's for, ★ recommended, and
  your saved ones. `noi presets --default ps2 zso-opl` sets a system's default (writes settings).
- **`noi db`**: status of the DATs (source, age, systems), the ES-DE list and the emulator data;
  `noi db update` checks now; `--dat-folder <folder>` for your own DATs.
- **`noi keys`**: each platform: found / missing, where, which file. Never prints a key.
- **`noi settings`**: `list` (every value, grouped as the app's cards), `get output.saveTo`,
  `set performance.parallelJobs 4`, `path`, `reset <key|--all>` (asks).
- **`noi test roundtrip <paths>`** and **`noi test compare <paths> --chdman <exe>`**: the Library tests,
  `--csv <file>` for the results. Hidden from the short help (power user), listed in `noi help --all`.

## 6. Help

Help is the CLI's manual. `-h`, `--help`, `-?` and `/?` give the same text on every command, and
`noi help <command>` too. Every page has the same parts in the same order: **one line saying what it does,
usage, examples, options in groups, and where to learn more**. It fits 80 columns, and is plain (no colour)
when piped, so `noi convert /? > help.txt` works.

On Linux and macOS `/?` is a shell pattern; it works if nothing in the folder matches it, and `-h` always
does. Other `/option` forms aren't accepted, since `/` starts a path there.

### 6.1 `noi /?`

```
NoIntrovert 0.10.0 — tidy, verify and compress your game library

USAGE
  noi <command> <files or folders…> [options]

MAIN COMMANDS
  optimize   Verify, convert, rename and file into system folders, in one go
  convert    Convert to each system's best format (CHD, RVZ, 7z, NSZ…)
  verify     Check for damage and look games up in No-Intro and Redump
  organize   Rename, move and de-duplicate, with a plan first and undo after

MORE
  runs       Undo an Organize or Optimize run, or empty its trash
  info       What a file is and what it can become
  presets    The presets for each system
  db         Game databases: status and update
  keys       Which console keys were found
  settings   Read and change settings (shared with the app)
  gui        Open the app with these files

EXAMPLES
  noi verify D:\Roms                       Check every game in a folder
  noi convert D:\Roms\PS1                  Discs → CHD, next to the originals
  noi convert . --to rvz -p smallest       GameCube/Wii → smallest RVZ
  noi optimize D:\Roms --new-library E:\   Build a tidy ES-DE library on E:
  noi convert D:\Roms --replace --yes -q   Replace originals, no questions

COMMON OPTIONS
  -o, --output <folder>   Save into a folder instead of next to the originals
      --replace           Replace the originals (each only after it verifies)
  -n, --dry-run           Show what would happen; change nothing
  -y, --yes               Don't ask for confirmation
  -q, --quiet             Only problems and a one-line summary
  -s, --silent            Print nothing; check the exit code
      --json              Results as JSON
  -j, --jobs <n>          Files at the same time (default 2)

Run "noi <command> --help" for a command's options, "noi help --all" for every
command, or see https://github.com/Nickel42/NoIntrovert/blob/main/docs/requirements/cli.md
```

### 6.2 `noi convert /?`

```
Convert files to each system's best format.

Each file becomes its system's recommended format, unless you choose another
preset: disc images → CHD, GameCube/Wii → RVZ, Wii U → .wua, Switch → .nsz,
cartridge ROMs → .7z. New files are verified. Originals are kept unless you
use --replace.

USAGE
  noi convert <files or folders…> [options]

EXAMPLES
  noi convert D:\Roms
      Everything to its recommended format, next to the originals.
  noi convert D:\Roms -o E:\Library --layout esde
      Into E:\Library\roms\<system>\, as ES-DE expects.
  noi convert D:\GC D:\Wii -p smallest
      GameCube and Wii discs as the smallest RVZ.
  noi convert D:\PS2 -p ps2=zso-opl
      PS2 discs as ZSO for OPL on a real PS2; other systems as usual.
  noi convert Game.chd --to cue
      A CHD back to cue/bin.
  noi convert D:\Roms --replace -y -q
      Replace each original once its new file verifies; no questions.

WHAT TO MAKE
  -p, --preset <id>          A preset for every file it fits, or <system>=<id>
                             (repeatable). See "noi presets".
  -t, --to <format>          A format with its standard preset: chd, rvz, iso,
                             cue, gdi, 7z, zip, nsz, zso, cso, unpack, …
      --keep <system>        Leave a system's files as they are

WHERE TO SAVE              (default: next to the originals)
  -o, --output <folder>      Into this folder
      --layout <layout>      esde (default), romm, keep, flat
      --replace              Replace the originals ⚠ — each only after its new
                             file verifies; they go to the Recycle Bin
      --overwrite            Overwrite files in the way (else "Name (2)")

WHICH FILES
      --no-recurse           Don't look in subfolders
      --include <glob>       Only matching files (repeatable)
      --exclude <glob>       Skip matching files (repeatable)
      --system <id>          Only these systems

OUTPUT
  -n, --dry-run   -y, --yes   -q, --quiet   -s, --silent   -v, --verbose
      --json   --ndjson   --color <when>   --progress <style>

Custom compression settings (codecs, RVZ level, 7z level…):
  noi convert --help-all
```

### 6.3 Errors that help

Errors say what went wrong, why, and what to type next; the important part is last.

```
✖ Unknown preset "smalest" for gamecube.
  Did you mean "rvz-smallest"?  Presets for gamecube: noi presets gamecube

✖ 3 Switch files need prod.keys, and none were found.
  Looked in: Ryujinx, yuzu, Eden, Citron, Sudachi, Suyu folders.
  Use --keys-switch <path to prod.keys>, or set it once:
    noi settings set keys.switchKeysPath "D:\Keys\prod.keys"

✖ --replace removes the originals, and nothing can be asked (--no-input).
  Add --yes to confirm, or leave out --replace to keep them.
```

## 7. Exit codes

| Code | Meaning |
|---|---|
| 0 | Everything done (skipped files that a preset doesn't fit are not failures) |
| 1 | Some files failed; the rest are done (the summary and log list which) |
| 2 | Wrong command or options; nothing was done |
| 3 | `verify`: at least one file is damaged |
| 4 | Missing keys or databases stopped some files |
| 5 | Organize's plan has clashes or changed files; nothing was run |
| 130 | Cancelled with Ctrl-C |

When several apply, the highest-priority one wins: 2, 130, 5, 1, 4, 3, 0.

## 8. Output

### 8.1 On a terminal

Cargo-style status words, live bars for the jobs in flight (one per `--jobs`), an overall bar, and finished
files scrolling above them:

```
 Verifying  412 files in D:\Roms (cached hashes for 380)
  Verified  Crash Bandicoot (USA).cue
 Converted  Crash Bandicoot (USA).cue → .chd          698 MB → 412 MB   −41%
 Converted  Metroid Prime (USA).iso → .rvz            1.36 GB → 892 MB  −35%
   Damaged  Tekken 3 (USA).chd — data SHA-1 doesn't match
⠹ Spyro the Dragon (USA).cue   compressing ━━━━━━━━━━━━╺━━━━━━━  63%  48 MB/s  0:07
⠼ Wii Sports (USA).iso         verifying   ━━━━━━━━━━━━━━━━━━╺━  91%  210 MB/s 0:02
  Overall   37/412  ━━━━╺━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━   9%  0:42 left

       Finished  412 files in 6m 12s
  ┌───────────┬───────┬──────────────┐
  │ Converted │   298 │ −61.4 GB     │
  │ Verified  │   402 │              │
  │ Skipped   │    96 │ already best │
  │ Failed    │     1 │ see above    │
  └───────────┴───────┴──────────────┘
  Log: %AppData%\NoIntrovert\logs\noi-20261114-101500.log
```

Colours carry meaning only alongside a symbol (✔ ≠ ? ✖ –), so nothing depends on colour alone, and the
palette stays readable on light and dark terminals (ANSI 16 colours, no fixed RGB).

### 8.2 Piped or in CI (`--progress lines`)

```
[  1/412] verified   Crash Bandicoot (USA).cue
[  2/412] converted  Crash Bandicoot (USA).cue -> Crash Bandicoot (USA).chd (698 MB -> 412 MB)
[  3/412] DAMAGED    Tekken 3 (USA).chd: data SHA-1 doesn't match
...
done: 412 files, 298 converted, 1 failed, 61.4 GB saved, 6m12s
```

### 8.3 `--quiet` and `--silent`

`--quiet`: the warnings and errors from §8.2's lines, then the `done:` line. `--silent`: nothing; the exit
code and the log.

### 8.4 `--json` and `--ndjson`

```json
{ "command": "convert", "version": "0.10.0", "exitCode": 1, "elapsedMs": 372000,
  "summary": { "files": 412, "converted": 298, "skipped": 96, "failed": 1, "bytesSaved": 65927122944 },
  "files": [ { "path": "D:\\Roms\\PS1\\Crash Bandicoot (USA).cue", "system": "playstation",
               "result": "converted", "output": "D:\\Roms\\PS1\\Crash Bandicoot (USA).chd",
               "preset": "chd-standard", "inBytes": 731906048, "outBytes": 432013312, "verified": true } ] }
```

`--ndjson` events: `start`, `file-start`, `progress` (at most 4 a second per file), `file-done`, `warning`,
`summary`. The field names are a documented, versioned contract (`"schema": 1`).

### 8.5 Ctrl-C

The first Ctrl-C stops new jobs, cancels the running ones (temp files removed, nothing half-written left
under a final name, no original removed) and says so at once; a second one exits without the clean-up.
An Organize run stopped half-way is in the journal and can be undone like any other.

## 9. Implementation

### 9.1 Projects

1. **`NoIntrovert.Core`** (new class library): the `Core` folder moves here as it is, apart from `Themes`
   (Avalonia). The app and the CLI both reference it.
2. **Headless pipelines in Core.** Today the page view models run the batches (`OptimizeViewModel`,
   `PlanTabViewModel`, `ConvertViewModel` call `ConvertPlanner`, `VerifyJobs`, `CleanupPlanner`,
   `CleanupRunner`). Their orchestration moves into `Core/Pipelines` (`VerifyPipeline`, `ConvertPipeline`,
   `OrganizePipeline`, `OptimizePipeline`) that take paths and options and report through
   `IProgress<PipelineEvent>`. The view models then call these, so the app and the CLI do exactly the same
   thing.
3. **`NoIntrovert.Cli`** (`noi`, console `Exe`): System.CommandLine for the commands, Spectre.Console for
   the output, one renderer per mode (rich, lines, quiet, silent, json, ndjson) behind one interface.
   Published single-file and trimmed, beside the app, by `release.yml`.
4. **Sharing with a running app.** The hash/match caches and journals are already written through
   `ThrottledJsonFile`; add a cross-process lock (a lock file beside each) so `noi` and the app can run at
   the same time.

### 9.2 Tests

In `NoIntrovert.Tests`: parsing (every example in the help parses), the help text as a snapshot (fails when
an option changes without its help), exit codes for each case in §7, `--json` against its schema, and a
convert / verify / organize / undo run on the synthetic discs through `noi` itself.

### 9.3 Milestones

| | Ships |
|---|---|
| C1 | Core split and pipelines; `noi verify`, `info`, `db`, `keys`, `presets`; output modes, help, exit codes |
| C2 | `noi convert`, every target and preset, Save to, keys options |
| C3 | `noi organize` (plan, plan files, new library) and `noi runs` |
| C4 | `noi optimize`, `settings`, `test`, `gui`, completions, `--ndjson` |

## 10. Questions to decide

1. **Name:** `noi` (short to type) or `nointrovert`? This draft uses `noi`.
2. **Organize without a terminal and without `--yes`:** show the plan and stop (as drafted, the safe
   choice) or fail with exit code 2?
3. **Settings:** should `noi` follow the app's saved settings by default (as drafted, with `--defaults` to
   ignore them), or always start from the recommended values?
4. **Windows `/` options:** only `/?` (as drafted), or also `/q`, `/s` and the like?
