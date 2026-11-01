# Future features

Features to develop later. Work that already has a requirement (for example Phases 2–4 of
[target formats](docs/requirements/target-formats.md#6-phases)) is tracked there, not here.

## Convert WBFS and CISO to RVZ

**Today:** `.wbfs` and `.ciso` GameCube/Wii images are *Left as is*. The Convert page offers
nothing else for them.

**Why:** WBFS and CISO keep only the parts of the disc that hold data, so they don't record the
disc's exact size. Dolphin's converters require an exact size, and lib-wii-gamecube-kit refuses these
images (`lib-wii-gamecube-kit.cpp`: "the container doesn't record the disc's exact size").

**What it takes:**
- lib-wii-gamecube-kit: an option to convert an image whose size is only an upper bound, treating it as
  the standard disc size (for example 4,699,979,776 bytes for a single-layer Wii disc) and filling
  the dropped areas with zeros.
- NoIntrovert:
  - offer RVZ for these rows, with a warning that the result won't match Redump (the dropped data
    comes back as zeros, not the original bytes);
  - change the check after converting, which compares the RVZ with its source and so can't match
    here;
  - make RVZ the default once the conversion works.

**Until then:** rebuild a plain ISO with another tool (for example `wit copy Game.wbfs Game.iso`
from Wiimms ISO Tools), then convert the ISO to RVZ in NoIntrovert.

## Export the original hashes as a DAT or CSV

**Today:** the original No-Intro/Redump hashes of converted files (R11) are kept in
`originals.json` beside the library index, and Verify & match uses them.

**To add:** an export from the Library page, as a Logiqx DAT or a CSV (Q1 in the
[target formats requirement](docs/requirements/target-formats.md#7-resolved-questions)), so other
tools can check the collection too.
