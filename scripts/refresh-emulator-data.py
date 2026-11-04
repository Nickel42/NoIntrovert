#!/usr/bin/env python3
"""Refresh the emulator snapshot behind the side panel's "Works with" list.

Downloads two public, MIT-licensed sources and reduces them to one compact
JSON file, NoIntrovert/Core/Emulators/Data/emulators.json:

* libretro-core-info (github.com/libretro/libretro-core-info, master): one
  *.info file per RetroArch core. Kept per core: id, name, the NoIntrovert
  systems its `database` names map to, supported_extensions, license and
  block_extract. Experimental cores are left out.
* ES-DE (gitlab.com/es-de/emulationstation-de, master):
  resources/systems/windows/es_systems.xml (one extension list per system,
  one labelled command per emulator) and es_find_rules.xml (which emulators
  ES-DE knows how to find). Kept per system: the extension list and the
  standalone emulators, in ES-DE's order.

System ids are NoIntrovert's (the "id" keys of
NoIntrovert/Core/Formats/TargetFormats.json). libretro `database` names map
through that file's "dats" plus the table below; ES-DE names through its
"esde" keys.

The hand-kept facts (codecs, per-emulator overrides, popularity) are in
emulator-facts.json next to the output; this script doesn't touch them.

Usage: python -I scripts/refresh-emulator-data.py [--out FILE]
Standard library only. Run from anywhere; paths are relative to the repo.
"""

from __future__ import annotations

import argparse
import datetime as dt
import io
import json
import re
import sys
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
TARGET_FORMATS = REPO / "NoIntrovert" / "Core" / "Formats" / "TargetFormats.json"
DEFAULT_OUT = REPO / "NoIntrovert" / "Core" / "Emulators" / "Data" / "emulators.json"

LIBRETRO_REPO = "libretro/libretro-core-info"
ESDE_PROJECT = "es-de/emulationstation-de"
ESDE_DIR = "resources/systems/windows"
# The RetroDECK mirror carries the ES-DE author's commits; used only when GitLab refuses (it rate-limits).
ESDE_MIRROR = "https://raw.githubusercontent.com/RetroDECK/ES-DE/retrodeck-main/" + ESDE_DIR

# libretro `database` names that TargetFormats.json's "dats" don't already cover.
DATABASE_EXTRA = {
    "Nintendo - Nintendo DSi Decrypted": ["nintendo-dsi"],
    "Nintendo - Nintendo DSi (Digital)": ["nintendo-dsi"],
    "Nintendo - Wii U": ["wii-u"],
    "Nintendo - Game & Watch": ["game-watch-lcd-handhelds"],
    "Handheld Electronic Game": ["game-watch-lcd-handhelds"],
    "FBNeo - Arcade Games": ["finalburn-neo"],
    "MAME": ["mame"],
    "MAME 2016": ["mame"],
    "MAME 2015": ["mame"],
    "MAME 2010": ["mame"],
    "MAME 2009": ["mame"],
    "MAME 2003-Plus": ["mame"],
    "MAME 2003": ["mame"],
    "MAME 2000": ["mame"],
    "Atomiswave": ["naomi-naomi-2-atomiswave"],
    "DOS": ["ms-dos"],
    "ScummVM": ["scummvm-supported-adventure-games"],
    "Amstrad - CPC": ["amstrad-cpc-gx4000"],
    "Amstrad - GX4000": ["amstrad-cpc-gx4000"],
    "Sinclair - ZX Spectrum": ["zx-spectrum-zx81"],
    "Sinclair - ZX 81": ["zx-spectrum-zx81"],
    "Commodore - PET": ["commodore-64-128-vic-20-pet"],
    "NEC - PC-88": ["pc-88-pc-98"],
    "SNK - Neo Geo Pocket": ["neo-geo-pocket-color"],
    "SNK - Neo Geo Pocket Color": ["neo-geo-pocket-color"],
    "Bandai - WonderSwan": ["wonderswan-color-swancrystal"],
    "Bandai - WonderSwan Color": ["wonderswan-color-swancrystal"],
    "Microsoft - Xbox 360": ["xbox-360"],
    "Nintendo - Nintendo 3DS": ["nintendo-3ds"],
    "Playdia": ["playdia"],
    "Fujitsu - FM Towns": ["other-redump-cd-dvd-systems"],
}

USER_AGENT = "NoIntrovert-refresh-emulator-data"


def get(url: str, accept: str | None = None) -> bytes:
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT, **({"Accept": accept} if accept else {})})
    with urllib.request.urlopen(request, timeout=60) as response:
        return response.read()


def get_json(url: str):
    return json.loads(get(url, "application/json"))


def registry():
    systems = json.loads(TARGET_FORMATS.read_text(encoding="utf-8"))
    by_database: dict[str, list[str]] = {}
    by_esde: dict[str, list[str]] = {}
    for s in systems:
        for dat in s.get("dats") or []:
            name = dat.split("/", 1)[1] if "/" in dat else dat
            if s["id"] not in by_database.setdefault(name, []):
                by_database[name].append(s["id"])
        if s.get("esde"):
            by_esde.setdefault(s["esde"], []).append(s["id"])
    for name, ids in DATABASE_EXTRA.items():
        for i in ids:
            if i not in by_database.setdefault(name, []):
                by_database[name].append(i)
    known = {s["id"] for s in systems}
    for name, ids in by_database.items():
        for i in ids:
            if i not in known:
                sys.exit(f"Mapping for '{name}' names unknown system '{i}'")
    return by_database, by_esde


def parse_info(text: str) -> dict[str, str]:
    """A libretro .info file: key = "value" lines, # comments."""
    values: dict[str, str] = {}
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        value = value.strip()
        if len(value) >= 2 and value[0] == '"' and value[-1] == '"':
            value = value[1:-1]
        values[key.strip()] = value
    return values


def split_list(value: str) -> list[str]:
    return [v.strip() for v in value.split("|") if v.strip()]


def extensions(items) -> list[str]:
    seen: list[str] = []
    for e in items:
        e = e.strip().lstrip(".").lower()
        if e and e not in seen:
            seen.append(e)
    return seen


def libretro(by_database):
    commit = get_json(f"https://api.github.com/repos/{LIBRETRO_REPO}/commits/master")
    sha = commit["sha"]
    date = commit["commit"]["committer"]["date"][:10]
    archive = zipfile.ZipFile(io.BytesIO(get(f"https://codeload.github.com/{LIBRETRO_REPO}/zip/{sha}")))
    cores = []
    unmapped: set[str] = set()
    for name in sorted(archive.namelist()):
        file = name.rsplit("/", 1)[-1]
        if not file.endswith("_libretro.info"):
            continue
        info = parse_info(archive.read(name).decode("utf-8", "replace"))
        if info.get("is_experimental", "false").lower() == "true":
            continue
        systems: list[str] = []
        for db in split_list(info.get("database", "")):
            ids = by_database.get(db)
            if ids is None:
                unmapped.add(db)
                continue
            systems += [i for i in ids if i not in systems]
        if not systems:
            continue
        core_id = file[: -len("_libretro.info")]
        cores.append({
            "id": core_id,
            "name": info.get("corename") or info.get("display_name") or core_id,
            "systems": systems,
            "extensions": extensions(split_list(info.get("supported_extensions", ""))),
            "license": info.get("license", ""),
            **({"blockExtract": True} if info.get("block_extract", "false").lower() == "true" else {}),
        })
    source = {
        "id": "libretro-core-info",
        "name": "libretro core info",
        "url": f"https://github.com/{LIBRETRO_REPO}",
        "ref": "master",
        "commit": sha,
        "date": date,
        "license": "MIT",
    }
    return source, cores, sorted(unmapped)


def esde_file(file: str) -> tuple[bytes, str]:
    url = f"https://gitlab.com/{ESDE_PROJECT}/-/raw/master/{ESDE_DIR}/{file}"
    try:
        return get(url), url
    except urllib.error.HTTPError as e:
        if e.code != 429:
            raise
        print(f"GitLab refused {file} (429); using the RetroDECK mirror", file=sys.stderr)
        url = f"{ESDE_MIRROR}/{file}"
        return get(url), url


def esde_commit() -> tuple[str | None, str | None]:
    try:
        api = f"https://gitlab.com/api/v4/projects/{ESDE_PROJECT.replace('/', '%2F')}/repository/commits"
        rows = get_json(f"{api}?path={ESDE_DIR}/es_systems.xml&ref_name=master&per_page=1")
        return rows[0]["id"], rows[0]["committed_date"][:10]
    except (urllib.error.URLError, KeyError, IndexError, ValueError):
        return None, None


EMULATOR_VAR = re.compile(r"%EMULATOR_([^%]+)%")


def standalone_name(label: str) -> str:
    stem = re.sub(r"\s*\(Standalone\)\s*$", "", label)
    return re.sub(r"\s*\[[^\]]*\]", "", stem).strip()


def common_words(names: list[str]) -> str:
    words = names[0].split()
    for n in names[1:]:
        other = n.split()
        k = 0
        while k < min(len(words), len(other)) and words[k] == other[k]:
            k += 1
        words = words[:k]
    return " ".join(words) or names[0]


def esde(by_esde):
    systems_xml, systems_url = esde_file("es_systems.xml")
    rules_xml, _ = esde_file("es_find_rules.xml")
    findable = {e.get("name") for e in ET.fromstring(rules_xml).iter("emulator")}
    labels: dict[str, list[str]] = {}
    systems: dict[str, dict] = {}
    for system in ET.fromstring(systems_xml).iter("system"):
        esde_name = (system.findtext("name") or "").strip()
        ids = by_esde.get(esde_name)
        if not ids:
            continue
        standalone: list[str] = []
        for command in system.iter("command"):
            label = command.get("label") or ""
            if not label.endswith("(Standalone)"):
                continue
            match = EMULATOR_VAR.search(command.text or "")
            if not match or match.group(1) in ("RETROARCH", "OS-SHELL"):
                continue
            emulator = match.group(1)
            labels.setdefault(emulator, [])
            name = standalone_name(label)
            if name not in labels[emulator]:
                labels[emulator].append(name)
            if emulator not in standalone:
                standalone.append(emulator)
        entry = {
            "esde": esde_name,
            "extensions": extensions((system.findtext("extension") or "").split()),
            "standalone": standalone,
        }
        for i in ids:
            systems[i] = entry
    emulators = [
        {"id": e, "name": common_words(names), **({} if e in findable else {"findable": False})}
        for e, names in sorted(labels.items())
    ]
    commit, date = esde_commit()
    source = {
        "id": "es-de",
        "name": "ES-DE",
        "url": f"https://gitlab.com/{ESDE_PROJECT}/-/tree/master/{ESDE_DIR}",
        "ref": "master",
        "commit": commit,
        "date": date,
        "fetchedFrom": systems_url,
        "license": "MIT",
    }
    return source, emulators, systems


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT)
    args = parser.parse_args()

    by_database, by_esde = registry()
    lr_source, cores, unmapped = libretro(by_database)
    es_source, standalone, systems = esde(by_esde)
    snapshot = {
        "generated": dt.date.today().isoformat(),
        "sources": [lr_source, es_source],
        "cores": cores,
        "standalone": standalone,
        "systems": dict(sorted(systems.items())),
    }
    args.out.parent.mkdir(parents=True, exist_ok=True)
    # One core / emulator / system per line keeps diffs readable.
    lines = ["{"]
    lines.append(f'  "generated": {json.dumps(snapshot["generated"])},')
    lines.append('  "sources": [')
    lines.append(",\n".join("    " + json.dumps(s, ensure_ascii=False) for s in snapshot["sources"]))
    lines.append("  ],")
    for key in ("cores", "standalone"):
        lines.append(f'  "{key}": [')
        lines.append(",\n".join("    " + json.dumps(c, ensure_ascii=False) for c in snapshot[key]))
        lines.append("  ],")
    lines.append('  "systems": {')
    lines.append(",\n".join(f"    {json.dumps(k)}: {json.dumps(v, ensure_ascii=False)}" for k, v in snapshot["systems"].items()))
    lines.append("  }")
    lines.append("}")
    text = "\n".join(lines) + "\n"
    json.loads(text)  # the hand-made layout must still be valid JSON
    args.out.write_text(text, encoding="utf-8", newline="\n")
    print(f"Wrote {args.out.relative_to(REPO) if args.out.is_relative_to(REPO) else args.out}: "
          f"{len(cores)} cores, {len(standalone)} standalone emulators, {len(systems)} systems")
    print(f"libretro-core-info {lr_source['commit'][:10]} ({lr_source['date']}), ES-DE {es_source['commit'] or '?'} ({es_source['date'] or '?'})")
    if unmapped:
        print("libretro databases with no NoIntrovert system (fine if NoIntrovert doesn't cover them):")
        print("  " + "; ".join(unmapped))


if __name__ == "__main__":
    main()
