#!/usr/bin/env python3
"""Refresh the Base16 colour schemes NoIntrovert offers as built-in themes.

Downloads tinted-theming's schemes repository (github.com/tinted-theming/schemes,
MIT) at the spec-0.11 branch and copies every base16/*.yaml (and .yml) into
NoIntrovert/Themes/Base16, unchanged, replacing what's there. The app embeds
them and lists each one under Settings > General > Dark theme or Light theme
(Core/Themes/ThemeLibrary.cs), named from the scheme's own name tidied up
(Core/Themes/Base16.cs).

THIRD_PARTY_NOTICES.txt next to them is rewritten with the repository's MIT
licence and the commit the files came from; release.yml copies it into every
download.

Usage: python -I scripts/refresh-base16-themes.py
Standard library only. Run from anywhere; paths are relative to the repo.
"""

from __future__ import annotations

import io
import json
import pathlib
import re
import sys
import tarfile
import urllib.request

REPO = "tinted-theming/schemes"
BRANCH = "spec-0.11"
ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "NoIntrovert" / "Themes" / "Base16"
NAME = re.compile(r"^[a-z0-9][a-z0-9-]*\.ya?ml$")


def fetch(url: str) -> bytes:
    request = urllib.request.Request(url, headers={"User-Agent": "NoIntrovert-refresh-base16-themes"})
    with urllib.request.urlopen(request, timeout=60) as response:
        return response.read()


def main() -> int:
    commit = json.loads(fetch(f"https://api.github.com/repos/{REPO}/commits/{BRANCH}"))["sha"]
    archive = tarfile.open(fileobj=io.BytesIO(fetch(f"https://codeload.github.com/{REPO}/tar.gz/{commit}")), mode="r:gz")

    schemes: dict[str, bytes] = {}
    licence = ""
    for member in archive.getmembers():
        if not member.isfile():
            continue
        parts = member.name.split("/")
        # <top>/base16/<name>.yaml: only plain names, nothing nested, nothing big.
        if len(parts) == 3 and parts[1] == "base16" and NAME.match(parts[2]) and member.size < 64 * 1024:
            data = archive.extractfile(member).read()
            if b"base00" not in data:
                continue
            schemes[parts[2]] = data
        elif len(parts) == 2 and parts[1] == "LICENSE":
            licence = archive.extractfile(member).read().decode("utf-8")
    if len(schemes) < 100 or not licence:
        print(f"Only {len(schemes)} schemes (licence found: {bool(licence)}); leaving the folder as it is.", file=sys.stderr)
        return 1

    OUT.mkdir(parents=True, exist_ok=True)
    for old in [*OUT.glob("*.yaml"), *OUT.glob("*.yml")]:
        if old.name not in schemes:
            old.unlink()
    for name, data in sorted(schemes.items()):
        (OUT / name).write_bytes(data.replace(b"\r\n", b"\n"))

    (OUT / "THIRD_PARTY_NOTICES.txt").write_text(
        "NoIntrovert's Base16 themes are the colour schemes of tinted-theming's schemes\n"
        f"repository, https://github.com/{REPO}, branch {BRANCH}, commit {commit}.\n"
        "Each scheme names its author in its own file. They are used under this licence:\n\n"
        + licence.strip() + "\n",
        encoding="utf-8", newline="\n")
    print(f"{len(schemes)} schemes from {REPO}@{commit[:12]} in {OUT.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
