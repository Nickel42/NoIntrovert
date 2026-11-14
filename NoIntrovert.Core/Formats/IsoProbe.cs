using System.Text;

namespace NoIntrovert.Core.Formats;

/// <summary>
/// Guesses whether a 2048-byte-sector ISO came from a CD or a DVD, for the
/// Auto ISO mode. DVD signs: a UDF volume recognition sequence (NSR02/NSR03,
/// used by PS2 DVDs and most video/data DVDs), a PSP UMD system identifier,
/// or more data than an 80-minute CD can hold.
/// </summary>
internal static class IsoProbe
{
    /// <summary>User data of an 80-minute CD: 360,000 sectors of 2048 bytes.</summary>
    public const long CdCapacity = 360_000L * 2048;

    public static bool LooksLikeDvd(string isoPath, out string reason)
    {
        var length = new FileInfo(isoPath).Length;
        if (length > CdCapacity)
        {
            reason = "larger than a CD";
            return true;
        }

        try
        {
            using var fs = File.OpenRead(isoPath);
            var sector = new byte[2048];

            // Primary volume descriptor at sector 16: system identifier at offset 8.
            if (ReadSector(fs, 16, sector) && sector[0] == 1 && Encoding.ASCII.GetString(sector, 1, 5) == "CD001")
            {
                var system = Encoding.ASCII.GetString(sector, 8, 32).Trim();
                if (system.Contains("PSP GAME", StringComparison.OrdinalIgnoreCase))
                {
                    reason = "PSP UMD image";
                    return true;
                }
            }

            // Volume recognition sequence follows the ISO 9660 descriptors (sectors 16..~32).
            for (var s = 17; s < 40; s++)
            {
                if (!ReadSector(fs, s, sector)) break;
                var id = Encoding.ASCII.GetString(sector, 1, 5);
                if (id is "NSR02" or "NSR03")
                {
                    reason = "has a UDF file system";
                    return true;
                }
                if (id == "TEA01") break;
            }
        }
        catch (IOException)
        {
        }

        reason = "fits on a CD and has no DVD file system";
        return false;
    }

    private static bool ReadSector(FileStream fs, long index, byte[] buffer)
    {
        if ((index + 1) * 2048 > fs.Length) return false;
        fs.Position = index * 2048;
        return fs.Read(buffer, 0, buffer.Length) == buffer.Length;
    }
}
