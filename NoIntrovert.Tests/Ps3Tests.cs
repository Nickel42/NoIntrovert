using System.Buffers.Binary;
using System.Text;
using LibPs3Kit;
using NoIntrovert.Core;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Ps3;

namespace NoIntrovert.Tests;

/// <summary>
/// PS3 discs through lib-ps3-kit: a synthetic disc (region table, the "PlayStation3" sector, ISO 9660 with
/// PS3_DISC.SFB, PARAM.SFO and an EBOOT.BIN in the encrypted region), encrypted with a made-up key into a
/// Redump-style image. Detection, the plans, the decrypted ISO and the JB folder, the .dkey lookup, and R11.
/// </summary>
internal static class Ps3Tests
{
    private const int S = 2048;
    private const string KeyHex = "00112233445566778899AABBCCDDEEFF";

    public static void Run(Action<string, Func<string?>> test, Func<string, string> dir, ILog log,
        Func<JobWork, Action<AppSettings>?, JobOutcome> runJob)
    {
        var key = DiscKey.Parse(KeyHex);
        var plain = PlainImage();
        var work = dir("ps3/PS3");
        string P(string name) => Path.Combine(work, name);
        FileSet Set(string name) => FormatRegistry.Default.Identify(P(name))!;
        void MakeEncrypted(string name, bool withKey)
        {
            var plainPath = P(name + ".plain.tmp");
            File.WriteAllBytes(plainPath, plain);
            Ps3Converter.ToEncryptedIso(plainPath, P(name + ".iso"), key, new Ps3Options { Overwrite = true });
            File.Delete(plainPath);
            if (withKey) File.WriteAllText(P(name + ".dkey"), KeyHex);
        }
        MakeEncrypted("Test Game (USA)", withKey: true);
        MakeEncrypted("No Key (USA)", withKey: false);
        File.WriteAllBytes(P("Decrypted (USA).iso"), plain);

        test("PS3: an encrypted image plans to become a decrypted ISO, JB folder offered; a decrypted one is optimal", () =>
        {
            var o = ConvertTargets.For(Set("Test Game (USA).iso"), new AppSettings());
            if (o.Plan.System != "playstation-3") return $"system {o.Plan.System}";
            if (o.Plan.Text != "Convert → decrypted ISO" || o.Default != TargetChoice.Ps3Iso) return $"{o.Plan.Text} ({o.Default})";
            if (!o.Choices.SequenceEqual([TargetChoice.Ps3Iso, TargetChoice.Ps3Folder, TargetChoice.Leave])) return string.Join(", ", o.Choices);
            var noKey = ConvertTargets.For(Set("No Key (USA).iso"), new AppSettings());
            if (noKey.Plan.Text != "Convert → decrypted ISO (needs its .dkey)") return $"no key: {noKey.Plan.Text}";
            var done = ConvertTargets.For(Set("Decrypted (USA).iso"), new AppSettings());
            if (done.Plan.Status != TargetStatus.Optimal || !done.Choices.SequenceEqual([TargetChoice.Ps3Folder, TargetChoice.Leave]))
                return $"decrypted: {done.Plan.Text}, {string.Join(", ", done.Choices)}";
            var s = new AppSettings();
            s.TargetFormats.Overrides.Add(new TargetOverride("playstation-3", null, "folder"));
            var jb = ConvertTargets.For(Set("Test Game (USA).iso"), s);
            if (jb.Plan.Text != "Convert → JB folder (decrypted)" || jb.Default != TargetChoice.Ps3Folder) return $"folder override: {jb.Plan.Text}";
            var jbDone = ConvertTargets.For(Set("Decrypted (USA).iso"), s);
            return jbDone.Plan.Text == "Convert → JB folder" ? null : $"decrypted, folder override: {jbDone.Plan.Text}";
        });

        test("PS3: a PS3 image is told by its disc header, wherever it is", () =>
        {
            var elsewhere = Path.Combine(dir("ps3-misc/Stuff"), "Game.iso");
            File.WriteAllBytes(elsewhere, plain);
            return DiscProbe.SystemOf(elsewhere) == "playstation-3" ? null : $"probed as {DiscProbe.SystemOf(elsewhere)}";
        });

        test("PS3: a Redump image becomes a decrypted ISO with its .dkey, checked, Redump hashes kept (R11)", () =>
        {
            var outDir = dir("ps3-out");
            var plan = ConvertPlanner.Plan([new PlanInput(P("Test Game (USA).iso"), TargetKind.Ps3Iso, 1)], new AppSettings(), log);
            var r = runJob(plan.Jobs[0].Work, st => st.Output.Folder = outDir);
            if (r.Status != JobStatus.Succeeded) return $"{r.Status}: {r.Summary}";
            var made = Path.Combine(outDir, "Test Game (USA).iso");
            if (!File.ReadAllBytes(made).AsSpan().SequenceEqual(plain)) return "the decrypted ISO isn't the plain image";
            if (Ps3Jobs.StateOf(made) != EncryptionState.Decrypted) return $"state {Ps3Jobs.StateOf(made)}";
            var original = NoIntrovert.Core.Dats.OriginalHashes.Default.Get(made)?.Files.FirstOrDefault();
            var dump = NoIntrovert.Core.Dats.Hasher.HashFile(P("Test Game (USA).iso"))[0];
            return original is not null && original.Sha1 == dump.Sha1 && original.Size == dump.Size ? null : $"kept {original?.Sha1}, the dump is {dump.Sha1}";
        });

        test("PS3: a Redump image (and a decrypted ISO, without a key) becomes a JB folder", () =>
        {
            var outDir = dir("ps3-jb");
            var r = runJob(c => Ps3Jobs.Convert(c, P("Test Game (USA).iso"), TargetKind.Ps3Folder), st => st.Output.Folder = outDir);
            if (r.Status != JobStatus.Succeeded) return $"{r.Status}: {r.Summary}";
            var eboot = Path.Combine(outDir, "Test Game (USA)", "PS3_GAME", "USRDIR", "EBOOT.BIN");
            if (!File.Exists(eboot) || !File.ReadAllBytes(eboot).AsSpan().StartsWith("SCE\0"u8)) return "EBOOT.BIN is missing or still encrypted";
            if (FormatRegistry.Default.Identify(Path.Combine(outDir, "Test Game (USA)"))?.Format != KnownFormats.Ps3Folder) return "the folder isn't seen as a PS3 game";
            var r2 = runJob(c => Ps3Jobs.Convert(c, P("Decrypted (USA).iso"), TargetKind.Ps3Folder), st => st.Output.Folder = outDir);
            if (r2.Status != JobStatus.Succeeded) return $"decrypted: {r2.Status}: {r2.Summary}";
            return File.Exists(Path.Combine(outDir, "Decrypted (USA)", "PS3_DISC.SFB")) ? null : "the decrypted ISO's folder is missing PS3_DISC.SFB";
        });

        test("PS3: without a .dkey the job fails and points to Settings › Keys; the keys folder is searched", () =>
        {
            var outDir = dir("ps3-nokey");
            var r = runJob(c => Ps3Jobs.Convert(c, P("No Key (USA).iso"), TargetKind.Ps3Iso), st => st.Output.Folder = outDir);
            if (r.Status != JobStatus.Failed || !r.Summary.Contains("Settings › Keys")) return $"{r.Status}: {r.Summary}";
            if (Directory.EnumerateFileSystemEntries(outDir).Any()) return "something was written";
            var keys = dir("ps3-keys");
            File.WriteAllText(Path.Combine(keys, "No Key (USA).dkey"), KeyHex);
            var ok = runJob(c => Ps3Jobs.Convert(c, P("No Key (USA).iso"), TargetKind.Ps3Iso), st => { st.Output.Folder = outDir; st.Keys.Ps3KeysFolder = keys; });
            return ok.Status == JobStatus.Succeeded ? null : $"with the keys folder: {ok.Status}: {ok.Summary}";
        });

        test("PS3: a wrong .dkey is caught before anything is written", () =>
        {
            MakeEncrypted("Wrong Key (USA)", withKey: false);
            File.WriteAllText(P("Wrong Key (USA).dkey"), "FFEEDDCCBBAA99887766554433221100");
            var outDir = dir("ps3-wrong");
            var r = runJob(c => Ps3Jobs.Convert(c, P("Wrong Key (USA).iso"), TargetKind.Ps3Iso), st => st.Output.Folder = outDir);
            if (r.Status != JobStatus.Failed || !r.Summary.Contains("doesn't fit")) return $"{r.Status}: {r.Summary}";
            return Directory.EnumerateFileSystemEntries(outDir).Any() ? "something was written" : null;
        });

        test("PS3: with Replace the originals the decrypted ISO takes the Redump image's name", () =>
        {
            MakeEncrypted("Swap (USA)", withKey: true);
            var dump = NoIntrovert.Core.Dats.Hasher.HashFile(P("Swap (USA).iso"))[0];
            var r = runJob(c => Ps3Jobs.Convert(c, P("Swap (USA).iso"), TargetKind.Ps3Iso),
                st => { st.Output.SaveTo = SaveTo.ReplaceOriginals; st.Output.UseRecycleBin = false; });
            if (r.Status != JobStatus.Succeeded) return $"{r.Status}: {r.Summary}";
            if (Ps3Jobs.StateOf(P("Swap (USA).iso")) != EncryptionState.Decrypted) return "Swap (USA).iso isn't decrypted";
            var left = Directory.GetFiles(work, "Swap*.iso*");
            if (left.Length != 1) return string.Join(", ", left.Select(Path.GetFileName));
            return NoIntrovert.Core.Dats.OriginalHashes.Default.Get(P("Swap (USA).iso"))?.Files.FirstOrDefault()?.Sha1 == dump.Sha1 ? null : "the Redump hashes weren't kept";
        });
    }

    /// <summary>
    /// A 64-sector decrypted PS3 image: plain regions [0, 29] and [50, 63] around an encrypted [30, 49]; a primary
    /// ISO 9660 tree with PS3_DISC.SFB, PS3_GAME/PARAM.SFO and PS3_GAME/USRDIR/EBOOT.BIN (in the encrypted region).
    /// </summary>
    private static byte[] PlainImage()
    {
        var img = new byte[64 * S];
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(0), 2);
        uint[] bounds = [0, 29, 50, 63];
        for (var i = 0; i < bounds.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(8 + i * 4), bounds[i]);
        "PlayStation3"u8.CopyTo(img.AsSpan(S));
        "BLUS-12345      "u8.CopyTo(img.AsSpan(S + 16));

        var pvd = img.AsSpan(16 * S, S);
        pvd[0] = 1; "CD001"u8.CopyTo(pvd[1..]); pvd[6] = 1;
        Encoding.ASCII.GetBytes("PS3VOLUME".PadRight(32)).CopyTo(pvd[40..]);
        Rec(20, S, 2, [0]).CopyTo(pvd[156..]);
        img[17 * S] = 255; "CD001"u8.CopyTo(img.AsSpan(17 * S + 1));

        var sfb = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        var sfo = ParamSfo();
        var eboot = new byte[3000];
        "SCE\0"u8.CopyTo(eboot);
        for (var i = 4; i < eboot.Length; i++) eboot[i] = (byte)(i * 13);
        sfb.CopyTo(img, 23 * S); sfo.CopyTo(img, 24 * S); eboot.CopyTo(img, 30 * S);

        Dir(img, 20, 20, Rec(23, sfb.Length, 0, Name("PS3_DISC.SFB;1")), Rec(21, S, 2, Name("PS3_GAME")));
        Dir(img, 21, 20, Rec(24, sfo.Length, 0, Name("PARAM.SFO;1")), Rec(22, S, 2, Name("USRDIR")));
        Dir(img, 22, 21, Rec(30, eboot.Length, 0, Name("EBOOT.BIN;1")));
        return img;
    }

    private static byte[] Name(string n) => Encoding.ASCII.GetBytes(n);

    private static void Dir(byte[] img, int sector, int parent, params byte[][] children)
    {
        var at = sector * S;
        foreach (var r in new[] { Rec(sector, S, 2, [0]), Rec(parent, S, 2, [1]) }.Concat(children))
        {
            r.CopyTo(img, at);
            at += r.Length;
        }
    }

    private static byte[] Rec(int lba, int size, byte flags, byte[] name)
    {
        var length = 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0);
        var r = new byte[length];
        r[0] = (byte)length;
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(2), (uint)lba);
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(6), (uint)lba);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(10), (uint)size);
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(14), (uint)size);
        r[25] = flags; r[28] = 1; r[31] = 1;
        r[32] = (byte)name.Length;
        name.CopyTo(r, 33);
        return r;
    }

    private static byte[] ParamSfo()
    {
        var data = new byte[20 + 16 + 16 + 16];
        "\0PSF"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 36);  // key table
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 52); // data table
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), 0x0204);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 10);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), 16);
        "TITLE_ID\0"u8.CopyTo(data.AsSpan(36));
        "BLUS12345\0"u8.CopyTo(data.AsSpan(52));
        return data;
    }
}
