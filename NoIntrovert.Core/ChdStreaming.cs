using System.Security.Cryptography;
using LibChdKit;
using NoIntrovert.Core.Cd;
using NoIntrovert.Core.Dats;

namespace NoIntrovert.Core;

/// <summary>
/// Hashes the files a streaming CHD extraction produces, as <see cref="Hasher.HashFile"/>
/// would hash them on disk: size, CRC32, SHA-1 and MD5 of each, in order. Files
/// named in <paramref name="skip"/> (the TOC) are left out.
/// </summary>
internal sealed class HashingSink(params string[] skip) : ChdExtractSink
{
    private System.IO.Hashing.Crc32? _crc;
    private IncrementalHash? _sha1, _md5;
    private string _name = "";
    private long _size;

    public List<HashedFile> Files { get; } = [];

    public override void BeginFile(string name)
    {
        _name = name;
        if (skip.Contains(name, StringComparer.Ordinal)) return;
        _crc = new System.IO.Hashing.Crc32();
        _sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        _size = 0;
    }

    public override void Write(ReadOnlySpan<byte> data)
    {
        if (_crc is null) return;
        _crc.Append(data);
        _sha1!.AppendData(data);
        _md5!.AppendData(data);
        _size += data.Length;
    }

    public override void EndFile()
    {
        if (_crc is null) return;
        Files.Add(new HashedFile(Path.GetFileName(_name), _size, _crc.GetCurrentHashAsUInt32().ToString("X8"),
            Convert.ToHexString(_sha1!.GetHashAndReset()), Md5: Convert.ToHexString(_md5!.GetHashAndReset())));
        _sha1.Dispose();
        _md5!.Dispose();
        _crc = null;
        _sha1 = _md5 = null;
    }
}

/// <summary>
/// Writes a single-data-track CD as an ISO straight from a streaming cue/bin
/// extraction: the cue (which comes first) is checked, then the 2048-byte user
/// data of each 2352-byte sector of the bin is written. The result is the same
/// as extracting the bin and running <see cref="ChdService.BinToIso"/> on it.
/// The ISO is removed again if the extraction fails.
/// </summary>
internal sealed class CdIsoSink(string cueName, string isoPath, bool overwrite) : ChdExtractSink, IDisposable
{
    private readonly byte[] _sector = new byte[CdSector.RawSize];
    private int _filled;
    private long _sectors;
    private MemoryStream? _cue;
    private bool _checked;
    private Stream? _iso;

    public override void BeginFile(string name)
    {
        if (name == cueName)
        {
            _cue = new MemoryStream();
            return;
        }
        if (!_checked || _iso is not null)
            throw new InvalidOperationException($"Unexpected file {Path.GetFileName(name)} in a single-bin CD extraction.");
        _iso = new BufferedStream(File.Create(isoPath), 1 << 20);
    }

    public override void Write(ReadOnlySpan<byte> data)
    {
        if (_cue is not null)
        {
            _cue.Write(data);
            return;
        }
        // Whole 2352-byte sectors, whatever the block boundaries; a partial sector at the end is dropped, as BinToIso does.
        while (data.Length > 0)
        {
            var take = Math.Min(data.Length, _sector.Length - _filled);
            data[..take].CopyTo(_sector.AsSpan(_filled));
            _filled += take;
            data = data[take..];
            if (_filled < _sector.Length) break;
            _filled = 0;
            var offset = CdSector.UserDataOffset(_sector);
            if (offset < 0)
                throw new InvalidOperationException($"Sector {_sectors} is not a 2048-byte data sector (Mode 2 Form 2 or audio), so this disc can't be stored as an ISO without losing data.");
            _iso!.Write(_sector, offset, CdSector.UserSize);
            _sectors++;
        }
    }

    public override void EndFile()
    {
        if (_cue is null) return;
        _cue.Position = 0;
        string text;
        using (var reader = new StreamReader(_cue)) text = reader.ReadToEnd(); // decoded as File.ReadAllText would
        _cue = null;
        var tracks = CueSheet.Parse(text).Tracks.ToList();
        if (tracks.Count != 1 || tracks[0].IsAudio)
            throw new InvalidOperationException($"This CD has {tracks.Count} tracks; only a single data track can be written as an ISO. Use cue/bin instead.");
        if (File.Exists(isoPath) && !overwrite)
            throw new IOException($"{Path.GetFileName(isoPath)} already exists.");
        _checked = true;
    }

    /// <summary>Flushes and closes the ISO after a successful extraction.</summary>
    public void Complete()
    {
        _iso?.Dispose();
        _iso = null;
    }

    /// <summary>After a failure: closes the ISO and removes the partial file.</summary>
    public void Dispose()
    {
        if (_iso is null) return;
        try { _iso.Dispose(); } catch (IOException) { }
        _iso = null;
        try { File.Delete(isoPath); } catch (IOException) { }
    }
}
