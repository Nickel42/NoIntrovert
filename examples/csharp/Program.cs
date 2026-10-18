// Example: create, inspect, verify and extract a raw CHD from C#.
// SPDX-License-Identifier: BSD-3-Clause

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using LibChdman;

var dir = Path.Combine(Path.GetTempPath(), "chdman-csharp-example");
Directory.CreateDirectory(dir);
string image = Path.Combine(dir, "disk.img"), chd = Path.Combine(dir, "disk.chd"), back = Path.Combine(dir, "disk.out");

var bytes = new byte[1 << 20];
for (int i = 0; i < bytes.Length / 2; i++) bytes[i] = (byte)(i * 7);
File.WriteAllBytes(image, bytes);

Console.WriteLine($"libchdman {Native.Str(Native.chdman_version())}, ABI {Native.chdman_abi_version()}");
Console.WriteLine($"struct sizes: create_raw={Marshal.SizeOf<CreateRawOptions>()} create_hd={Marshal.SizeOf<CreateHdOptions>()} verify_result={Marshal.SizeOf<VerifyResult>()} metadata_entry={Marshal.SizeOf<MetadataEntry>()}");

using var chdman = new Chdman();
chdman.OnProgress = (phase, fraction, ratio) =>
{
    if (phase == Phase.Compressing) Console.WriteLine($"  compressing {fraction:P0}");
    return false; // true cancels
};

chdman.CreateRaw(image, chd, unitSize: 512, force: true);
var info = chdman.Info(chd);
Console.WriteLine($"created {info.mediaType} CHD: {info.logicalBytes} -> {info.fileBytes} bytes, {info.compression}");
Console.WriteLine($"raw SHA-1 {info.rawSha1}");
Console.WriteLine($"verify: {chdman.Verify(chd).Outcome}");
chdman.ExtractRaw(chd, back, force: true);
Console.WriteLine($"round trip identical: {File.ReadAllBytes(back).SequenceEqual(bytes)}");

for (nuint id = 0; id < Native.chdman_template_count(); id += 8)
{
    Native.chdman_template_get(id, out var t);
    Console.WriteLine($"template {id}: {Native.Str(t.Manufacturer)} {Native.Str(t.Model)} {t.Cylinders}/{t.Heads}/{t.Sectors}");
}

try { chdman.CreateRaw(image, chd, 512); }
catch (ChdmanException e) { Console.WriteLine($"expected error -> {e.Status}: {e.Message}"); }
