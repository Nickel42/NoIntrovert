// P/Invoke bindings for libchdman (chdman.h). Works with chdman.dll on
// Windows (x64/ARM64) and libchdman.dylib on macOS: DllImport("chdman")
// resolves to the platform's naming convention.
// SPDX-License-Identifier: BSD-3-Clause

using System;
using System.Runtime.InteropServices;

namespace LibChdman
{
    public enum Status : int
    {
        Ok = 0, InvalidArgument = 1, FileExists = 2, FileOpen = 3, IO = 4, Chd = 5,
        Unsupported = 6, NotFound = 7, Cancelled = 8, OutOfMemory = 9, Internal = 10, AbiMismatch = 11
    }

    public enum Phase : int { WalkingParent = 0, Compressing = 1, Verifying = 2, Extracting = 3, Analyzing = 4 }
    public enum LogLevel : int { Info = 0, Warning = 1, Error = 2 }
    public enum VerifyOutcome : int { Ok = 0, RawMismatch = 1, OverallMismatch = 2, Fixed = 3, NotApplicable = 4 }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int ProgressCallback(IntPtr user, Phase phase, double fraction, double ratio);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void LogCallback(IntPtr user, LogLevel level, IntPtr message);

    // ---- option structs: layouts mirror chdman.h; always call the *_init function first

    [StructLayout(LayoutKind.Sequential)]
    public struct OutputOptions
    {
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Output;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? OutputParent;
        public int Force;
        public uint HunkSize;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Compression;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RangeOptions
    {
        public ulong StartByte, StartHunk, LengthBytes, LengthHunks; // Native.Unset when unused
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct InfoOptions
    {
        public nuint StructSize;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Input;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? InputParent;
        public int HunkStats;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VerifyOptions
    {
        public nuint StructSize;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Input;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? InputParent;
        public int Fix;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct VerifyResult
    {
        public nuint StructSize;
        public VerifyOutcome Outcome;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 41)] public string HeaderRawSha1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 41)] public string ComputedRawSha1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 41)] public string HeaderSha1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 41)] public string ComputedSha1;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CreateRawOptions
    {
        public nuint StructSize;
        public OutputOptions Out;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Input;
        public RangeOptions Range;
        public uint UnitSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CreateHdOptions
    {
        public nuint StructSize;
        public OutputOptions Out;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Input;
        public RangeOptions Range;
        public ulong Size;
        public int TemplateId;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Ident;
        public uint Cylinders, Heads, Sectors, SectorSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CreateCdOptions
    {
        public nuint StructSize;
        public OutputOptions Out;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Input;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CreateDvdOptions
    {
        public nuint StructSize;
        public OutputOptions Out;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Input;
        public RangeOptions Range;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CreateLdOptions
    {
        public nuint StructSize;
        public OutputOptions Out;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Input;
        public ulong StartFrame, LengthFrames;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CopyOptions
    {
        public nuint StructSize;
        public OutputOptions Out;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Input;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? InputParent;
        public RangeOptions Range;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ExtractRawOptions
    {
        public nuint StructSize;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Input;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? InputParent;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Output;
        public int Force;
        public RangeOptions Range;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ExtractCdOptions
    {
        public nuint StructSize;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Input;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? InputParent;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Output;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? OutputBin;
        public int SplitBin;
        public int Force;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ExtractLdOptions
    {
        public nuint StructSize;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Input;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? InputParent;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Output;
        public int Force;
        public ulong StartFrame, LengthFrames;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AddMetadataOptions
    {
        public nuint StructSize;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Input;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? InputParent;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Tag;
        public uint Index;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? ValueText;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string? ValueFile;
        public IntPtr ValueData;
        public nuint ValueLength;
        public int NoChecksum;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MetadataEntry
    {
        public uint Tag;
        public byte Tag0, Tag1, Tag2, Tag3, TagNul;
        public uint Index;
        public byte Flags;
        public IntPtr Data;
        public nuint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HdTemplate
    {
        public IntPtr Manufacturer, Model; // static UTF-8 strings
        public uint Cylinders, Heads, Sectors, SectorSize;
    }

    public static class Native
    {
        const string Lib = "chdman";
        public const ulong Unset = ulong.MaxValue;

        [DllImport(Lib)] public static extern IntPtr chdman_version();
        [DllImport(Lib)] public static extern int chdman_abi_version();
        [DllImport(Lib)] public static extern IntPtr chdman_status_string(Status status);

        [DllImport(Lib)] public static extern Status chdman_context_create(out IntPtr ctx);
        [DllImport(Lib)] public static extern void chdman_context_destroy(IntPtr ctx);
        [DllImport(Lib)] public static extern void chdman_context_set_progress(IntPtr ctx, ProgressCallback? fn, IntPtr user);
        [DllImport(Lib)] public static extern void chdman_context_set_log(IntPtr ctx, LogCallback? fn, IntPtr user);
        [DllImport(Lib)] public static extern void chdman_context_set_num_processors(IntPtr ctx, int count);
        [DllImport(Lib)] public static extern void chdman_context_cancel(IntPtr ctx);
        [DllImport(Lib)] public static extern IntPtr chdman_last_error(IntPtr ctx);
        [DllImport(Lib)] public static extern void chdman_free(IntPtr ptr);

        [DllImport(Lib)] public static extern void chdman_info_options_init(ref InfoOptions o);
        [DllImport(Lib)] public static extern Status chdman_info_open(IntPtr ctx, ref InfoOptions o, out IntPtr info);
        [DllImport(Lib)] public static extern void chdman_info_free(IntPtr info);
        [DllImport(Lib)] public static extern uint chdman_info_version(IntPtr info);
        [DllImport(Lib)] public static extern ulong chdman_info_logical_bytes(IntPtr info);
        [DllImport(Lib)] public static extern uint chdman_info_hunk_bytes(IntPtr info);
        [DllImport(Lib)] public static extern uint chdman_info_hunk_count(IntPtr info);
        [DllImport(Lib)] public static extern uint chdman_info_unit_bytes(IntPtr info);
        [DllImport(Lib)] public static extern ulong chdman_info_unit_count(IntPtr info);
        [DllImport(Lib)] public static extern ulong chdman_info_file_bytes(IntPtr info);
        [DllImport(Lib)] public static extern int chdman_info_compressed(IntPtr info);
        [DllImport(Lib)] public static extern uint chdman_info_codec(IntPtr info, int slot);
        [DllImport(Lib)] public static extern IntPtr chdman_info_compression_string(IntPtr info);
        [DllImport(Lib)] public static extern IntPtr chdman_info_sha1(IntPtr info);
        [DllImport(Lib)] public static extern IntPtr chdman_info_raw_sha1(IntPtr info);
        [DllImport(Lib)] public static extern IntPtr chdman_info_parent_sha1(IntPtr info);
        [DllImport(Lib)] public static extern IntPtr chdman_info_media_type(IntPtr info);
        [DllImport(Lib)] public static extern nuint chdman_info_metadata_count(IntPtr info);
        [DllImport(Lib)] public static extern Status chdman_info_metadata_get(IntPtr info, nuint i, out MetadataEntry e);

        [DllImport(Lib)] public static extern void chdman_verify_options_init(ref VerifyOptions o);
        [DllImport(Lib)] public static extern void chdman_verify_result_init(ref VerifyResult r);
        [DllImport(Lib)] public static extern Status chdman_verify(IntPtr ctx, ref VerifyOptions o, ref VerifyResult r);

        [DllImport(Lib)] public static extern void chdman_create_raw_options_init(ref CreateRawOptions o);
        [DllImport(Lib)] public static extern void chdman_create_hd_options_init(ref CreateHdOptions o);
        [DllImport(Lib)] public static extern void chdman_create_cd_options_init(ref CreateCdOptions o);
        [DllImport(Lib)] public static extern void chdman_create_dvd_options_init(ref CreateDvdOptions o);
        [DllImport(Lib)] public static extern void chdman_create_ld_options_init(ref CreateLdOptions o);
        [DllImport(Lib)] public static extern Status chdman_create_raw(IntPtr ctx, ref CreateRawOptions o);
        [DllImport(Lib)] public static extern Status chdman_create_hd(IntPtr ctx, ref CreateHdOptions o);
        [DllImport(Lib)] public static extern Status chdman_create_cd(IntPtr ctx, ref CreateCdOptions o);
        [DllImport(Lib)] public static extern Status chdman_create_dvd(IntPtr ctx, ref CreateDvdOptions o);
        [DllImport(Lib)] public static extern Status chdman_create_ld(IntPtr ctx, ref CreateLdOptions o);

        [DllImport(Lib)] public static extern void chdman_copy_options_init(ref CopyOptions o);
        [DllImport(Lib)] public static extern Status chdman_copy(IntPtr ctx, ref CopyOptions o);

        [DllImport(Lib)] public static extern void chdman_extract_raw_options_init(ref ExtractRawOptions o);
        [DllImport(Lib)] public static extern void chdman_extract_cd_options_init(ref ExtractCdOptions o);
        [DllImport(Lib)] public static extern void chdman_extract_ld_options_init(ref ExtractLdOptions o);
        [DllImport(Lib)] public static extern Status chdman_extract_raw(IntPtr ctx, ref ExtractRawOptions o);
        [DllImport(Lib)] public static extern Status chdman_extract_cd(IntPtr ctx, ref ExtractCdOptions o);
        [DllImport(Lib)] public static extern Status chdman_extract_ld(IntPtr ctx, ref ExtractLdOptions o);

        [DllImport(Lib)] public static extern void chdman_add_metadata_options_init(ref AddMetadataOptions o);
        [DllImport(Lib)] public static extern Status chdman_add_metadata(IntPtr ctx, ref AddMetadataOptions o);
        [DllImport(Lib)] public static extern Status chdman_del_metadata(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string input, [MarshalAs(UnmanagedType.LPUTF8Str)] string? inputParent, [MarshalAs(UnmanagedType.LPUTF8Str)] string tag, uint index);
        [DllImport(Lib)] public static extern Status chdman_dump_metadata(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string input, [MarshalAs(UnmanagedType.LPUTF8Str)] string? inputParent, [MarshalAs(UnmanagedType.LPUTF8Str)] string tag, uint index, [MarshalAs(UnmanagedType.LPUTF8Str)] string output, int force);
        [DllImport(Lib)] public static extern Status chdman_get_metadata(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string input, [MarshalAs(UnmanagedType.LPUTF8Str)] string? inputParent, [MarshalAs(UnmanagedType.LPUTF8Str)] string tag, uint index, out IntPtr data, out nuint length);

        [DllImport(Lib)] public static extern nuint chdman_template_count();
        [DllImport(Lib)] public static extern Status chdman_template_get(nuint id, out HdTemplate t);

        public static string Str(IntPtr p) => Marshal.PtrToStringUTF8(p) ?? "";
    }

    public sealed class ChdmanException : Exception
    {
        public Status Status { get; }
        public ChdmanException(Status status, string message) : base(message) { Status = status; }
    }

    /// <summary>Managed wrapper owning one chdman_context. Not thread-safe except Cancel().</summary>
    public sealed class Chdman : IDisposable
    {
        IntPtr _ctx;
        readonly ProgressCallback _progress; // kept alive for the native side
        readonly LogCallback _log;

        public Func<Phase, double, double, bool>? OnProgress { get; set; } // return true to cancel
        public Action<LogLevel, string>? OnLog { get; set; }

        public Chdman(int threads = 0)
        {
            Check(Native.chdman_context_create(out _ctx));
            _progress = (_, phase, fraction, ratio) => OnProgress?.Invoke(phase, fraction, ratio) == true ? 1 : 0;
            _log = (_, level, msg) => OnLog?.Invoke(level, Native.Str(msg));
            Native.chdman_context_set_progress(_ctx, _progress, IntPtr.Zero);
            Native.chdman_context_set_log(_ctx, _log, IntPtr.Zero);
            Native.chdman_context_set_num_processors(_ctx, threads);
        }

        public void Cancel() => Native.chdman_context_cancel(_ctx);

        void Check(Status st)
        {
            if (st != Status.Ok)
                throw new ChdmanException(st, _ctx == IntPtr.Zero ? st.ToString() : Native.Str(Native.chdman_last_error(_ctx)));
        }

        public void CreateRaw(string input, string output, uint unitSize, string? compression = null, bool force = false)
        {
            var o = new CreateRawOptions();
            Native.chdman_create_raw_options_init(ref o);
            o.Input = input; o.Out.Output = output; o.Out.Compression = compression; o.Out.Force = force ? 1 : 0;
            o.UnitSize = unitSize;
            Check(Native.chdman_create_raw(_ctx, ref o));
        }

        public void CreateCd(string cueOrToc, string output, bool force = false)
        {
            var o = new CreateCdOptions();
            Native.chdman_create_cd_options_init(ref o);
            o.Input = cueOrToc; o.Out.Output = output; o.Out.Force = force ? 1 : 0;
            Check(Native.chdman_create_cd(_ctx, ref o));
        }

        public void ExtractRaw(string input, string output, bool force = false)
        {
            var o = new ExtractRawOptions();
            Native.chdman_extract_raw_options_init(ref o);
            o.Input = input; o.Output = output; o.Force = force ? 1 : 0;
            Check(Native.chdman_extract_raw(_ctx, ref o));
        }

        public void ExtractCd(string input, string output, bool force = false)
        {
            var o = new ExtractCdOptions();
            Native.chdman_extract_cd_options_init(ref o);
            o.Input = input; o.Output = output; o.Force = force ? 1 : 0;
            Check(Native.chdman_extract_cd(_ctx, ref o));
        }

        public VerifyResult Verify(string input, bool fix = false)
        {
            var o = new VerifyOptions();
            Native.chdman_verify_options_init(ref o);
            o.Input = input; o.Fix = fix ? 1 : 0;
            var r = new VerifyResult();
            Native.chdman_verify_result_init(ref r);
            Check(Native.chdman_verify(_ctx, ref o, ref r));
            return r;
        }

        public (string mediaType, ulong logicalBytes, ulong fileBytes, string compression, string rawSha1, string sha1) Info(string input)
        {
            var o = new InfoOptions();
            Native.chdman_info_options_init(ref o);
            o.Input = input;
            Check(Native.chdman_info_open(_ctx, ref o, out var info));
            try
            {
                return (Native.Str(Native.chdman_info_media_type(info)),
                        Native.chdman_info_logical_bytes(info),
                        Native.chdman_info_file_bytes(info),
                        Native.Str(Native.chdman_info_compression_string(info)),
                        Native.Str(Native.chdman_info_raw_sha1(info)),
                        Native.Str(Native.chdman_info_sha1(info)));
            }
            finally { Native.chdman_info_free(info); }
        }

        public byte[] GetMetadata(string input, string tag, uint index = 0)
        {
            Check(Native.chdman_get_metadata(_ctx, input, null, tag, index, out var data, out var len));
            try
            {
                var bytes = new byte[(int)len];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                return bytes;
            }
            finally { Native.chdman_free(data); }
        }

        public void Dispose()
        {
            if (_ctx != IntPtr.Zero)
            {
                Native.chdman_context_destroy(_ctx);
                _ctx = IntPtr.Zero;
            }
            GC.KeepAlive(_progress);
            GC.KeepAlive(_log);
        }
    }
}
