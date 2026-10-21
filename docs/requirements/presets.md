# Presets: from parameters to presets

Issue [#33](https://github.com/Nickel42/NoIntrovert/issues/33). Code: `NoIntrovert/Core/Presets/ActionPresets.cs`,
`ViewModels/PresetsViewModel.cs`, `Views/PresetPicker.axaml`.

## Rules

- **P1. One choice per action.** Each action the app performs has a short list of named presets, chosen from
  one drop-down. The action's individual parameters are shown **only when Custom is chosen**.
- **P2. A recommended preset.** The first preset of each action is the recommended one (★). New settings start
  on it, and its values are the app's defaults, so a new install behaves as it did before presets.
- **P3. Custom keeps your values.** A preset never overwrites the values stored in Settings. A run applies the
  preset to a copy of the settings, so switching from Custom to a preset and back brings your own values back.
- **P4. One preset per job.** Each job runs with its own action's preset (`ConvertPlanner.SettingsFor`), so
  presets of different actions never clash. For example, CHD *Replace the originals* removes the cue/bin after
  the CHD verifies, while RVZ *Balanced* keeps the GameCube ISO in the same run.
- **P5. The same choice everywhere.** Convert, Verify and Settings show the same pickers, bound to the same
  view models; a choice made on one page shows on the others and is saved at once.
- **P6. Upgrades change nothing.** A settings file from before presets gets, for each action, the preset its
  values already equal, or Custom (`ActionPresets.Infer`).
- **P7. Targets stay per row.** Presets decide *how* an action is done; the TARGET column on the Convert page,
  and the default targets in Settings (GameCube/Wii → RVZ or CHD, new archives 7z or zip), decide *what* a row
  becomes.
- **P8. Clean Up stays safe.** Clean Up converts with the presets' compression, but always verifies or tests
  the result and keeps the originals (it moves them to its own trash), whatever the preset says.

## Actions and presets

| Action | Presets (★ recommended) | Parameters under Custom |
|---|---|---|
| Disc images → CHD | ★ Standard · Replace the originals · Custom | codecs, ISO mode, hunk size (power user), verify, remove originals |
| GameCube / Wii → RVZ | ★ Balanced (Zstandard 5) · Smallest (Zstandard 19) · Fastest (Zstandard 1) · Replace the originals · Custom | compression, level, block size and scrub (power user), verify, remove originals |
| Wii U → .wua | ★ WUX, WUD or NUS → .wua · Replace the originals · Custom | remove originals |
| 7z and zip | ★ Balanced (Normal) · Smallest (Ultra) · Fastest (Fast) · Replace the originals · Custom | level, solid, test, grouping (one archive per item, per entry, or one combined archive), remove originals |
| CHD → disc image | ★ Best format for the disc · One .bin · Custom | format, one .bin per track |
| Unpacking archives | ★ Into its own folder · Unpack and remove the archive · Custom | own folder, remove the archive |
| Checking games (Verify & match) | ★ Check and match · Check, match and fix CHD headers · Custom | fix a CHD's header SHA-1 |

Clean Up's file layout already works this way (★ ES-DE format, or keep the current layout) and has nothing else
to set, so it has no Custom.

Custom values shared between actions: *verify* and *remove originals* are one setting for CHD and RVZ, and
*remove originals* is one setting for 7z/zip and unpacking. The pickers say so under Custom.

A 7z holding a single file is always LZMA2 at Ultra and not solid (D9 in [target-formats](target-formats.md)),
whatever the preset.

## Settings file

`AppSettings.Presets.Chosen` maps an action name to a preset id (`"Chd": "replace"`). An action that isn't
listed uses its recommended preset, so choosing the recommended one removes its entry. An unknown id is read as
the recommended preset.
