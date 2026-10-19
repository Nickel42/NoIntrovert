using System.IO.Compression;

namespace NoIntrovert.Tests;

/// <summary>
/// A made-up library the size real ones reach: thousands of inputs in many
/// folders, a mix of cue/bin sets, ISOs, zipped discs, CHDs and zipped
/// cartridge ROMs. The files are tiny; only their names and layout matter.
/// </summary>
internal static class ScaleLibrary
{
    /// <summary>Writes about <paramref name="count"/> inputs under <paramref name="root"/>; returns how many a Convert scan should find.</summary>
    public static int Make(string root, int count, int folders = 100)
    {
        var inputs = 0;
        for (var i = 0; i < count; i++)
        {
            var dir = Path.Combine(root, $"System {i % 5}", $"Folder {i % folders:D3}");
            Directory.CreateDirectory(dir);
            var name = $"Game {i:D5} (USA)";
            switch (i % 5)
            {
                case 0: // cue + bin: one input
                    File.WriteAllText(Path.Combine(dir, name + ".cue"), $"FILE \"{name}.bin\" BINARY\n TRACK 01 MODE1/2352\n  INDEX 01 00:00:00\n");
                    File.WriteAllBytes(Path.Combine(dir, name + ".bin"), new byte[2352]);
                    break;
                case 1: // a plain ISO (not GameCube/Wii)
                    File.WriteAllBytes(Path.Combine(dir, name + ".iso"), new byte[4096]);
                    break;
                case 2: // a zip holding a disc image
                    using (var z = ZipFile.Open(Path.Combine(dir, name + ".zip"), ZipArchiveMode.Create))
                        z.CreateEntry(name + ".iso");
                    break;
                case 3: // a finished CHD (a scan's job is to know what's already done)
                    File.WriteAllBytes(Path.Combine(dir, name + ".chd"), new byte[512]);
                    break;
                case 4: // a zip of a cartridge ROM
                    using (var z = ZipFile.Open(Path.Combine(dir, name + ".zip"), ZipArchiveMode.Create))
                        z.CreateEntry(name + ".nes");
                    break;
            }
            inputs++;
        }
        return inputs;
    }
}
