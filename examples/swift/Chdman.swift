// Thin Swift wrapper over the libchdman C API (import the CChdman module from
// include/module.modulemap, or the headers inside an XCFramework).
// SPDX-License-Identifier: BSD-3-Clause

import CChdman
import Foundation

public struct ChdmanError: Error, CustomStringConvertible {
    public let status: chdman_status
    public let message: String
    public var description: String { "\(String(cString: chdman_status_string(status))): \(message)" }
}

public struct ChdInfo {
    public let version: UInt32
    public let logicalBytes: UInt64
    public let hunkBytes: UInt32
    public let hunkCount: UInt32
    public let unitBytes: UInt32
    public let fileBytes: UInt64
    public let compression: String
    public let mediaType: String
    public let sha1: String
    public let rawSHA1: String
    public let metadata: [(tag: String, index: UInt32, data: Data)]
}

public final class Chdman {
    private let ctx: OpaquePointer

    /// Called with (phase, fraction 0...1, compression ratio). Return true to cancel.
    public var onProgress: ((chdman_phase, Double, Double) -> Bool)?
    public var onLog: ((chdman_log_level, String) -> Void)?

    public init(threads: Int = 0) throws {
        var c: OpaquePointer?
        let st = chdman_context_create(&c)
        guard st == CHDMAN_OK, let c else { throw ChdmanError(status: st, message: "context") }
        ctx = c
        let me = Unmanaged.passUnretained(self).toOpaque()
        chdman_context_set_progress(ctx, { user, phase, fraction, ratio in
            let this = Unmanaged<Chdman>.fromOpaque(user!).takeUnretainedValue()
            return (this.onProgress?(phase, fraction, ratio) ?? false) ? 1 : 0
        }, me)
        chdman_context_set_log(ctx, { user, level, message in
            let this = Unmanaged<Chdman>.fromOpaque(user!).takeUnretainedValue()
            this.onLog?(level, String(cString: message!))
        }, me)
        chdman_context_set_num_processors(ctx, Int32(threads))
    }

    deinit { chdman_context_destroy(ctx) }

    /// Thread-safe: may be called from any thread while an operation runs.
    public func cancel() { chdman_context_cancel(ctx) }

    private func check(_ st: chdman_status) throws {
        if st != CHDMAN_OK {
            throw ChdmanError(status: st, message: String(cString: chdman_last_error(ctx)))
        }
    }

    public func info(_ path: String) throws -> ChdInfo {
        var opts = chdman_info_options()
        chdman_info_options_init(&opts)
        var handle: OpaquePointer?
        try path.withCString { p in
            opts.input = p
            try check(chdman_info_open(ctx, &opts, &handle))
        }
        defer { chdman_info_free(handle) }
        var meta: [(String, UInt32, Data)] = []
        for i in 0..<chdman_info_metadata_count(handle) {
            var e = chdman_metadata_entry()
            if chdman_info_metadata_get(handle, i, &e) == CHDMAN_OK {
                let tag = withUnsafeBytes(of: e.tag_string) { String(decoding: $0.prefix(4), as: UTF8.self) }
                meta.append((tag, e.index, e.data.map { Data(bytes: $0, count: e.length) } ?? Data()))
            }
        }
        return ChdInfo(
            version: chdman_info_version(handle),
            logicalBytes: chdman_info_logical_bytes(handle),
            hunkBytes: chdman_info_hunk_bytes(handle),
            hunkCount: chdman_info_hunk_count(handle),
            unitBytes: chdman_info_unit_bytes(handle),
            fileBytes: chdman_info_file_bytes(handle),
            compression: String(cString: chdman_info_compression_string(handle)),
            mediaType: String(cString: chdman_info_media_type(handle)),
            sha1: String(cString: chdman_info_sha1(handle)),
            rawSHA1: String(cString: chdman_info_raw_sha1(handle)),
            metadata: meta)
    }

    public func verify(_ path: String, fix: Bool = false) throws -> chdman_verify_outcome {
        var opts = chdman_verify_options()
        chdman_verify_options_init(&opts)
        var result = chdman_verify_result()
        chdman_verify_result_init(&result)
        opts.fix = fix ? 1 : 0
        try path.withCString { p in
            opts.input = p
            try check(chdman_verify(ctx, &opts, &result))
        }
        return result.outcome
    }

    /// compression: nil = media default, "none", or e.g. "lzma,zlib,huff,flac".
    public func createRaw(input: String, output: String, unitSize: UInt32,
                          compression: String? = nil, force: Bool = false) throws {
        var o = chdman_create_raw_options()
        chdman_create_raw_options_init(&o)
        o.unit_size = unitSize
        o.out.force = force ? 1 : 0
        try input.withCString { i in
            try output.withCString { out in
                try withOptionalCString(compression) { c in
                    o.input = i
                    o.out.output = out
                    o.out.compression = c
                    try check(chdman_create_raw(ctx, &o))
                }
            }
        }
    }

    public func createCD(input: String, output: String, force: Bool = false) throws {
        var o = chdman_create_cd_options()
        chdman_create_cd_options_init(&o)
        o.out.force = force ? 1 : 0
        try input.withCString { i in
            try output.withCString { out in
                o.input = i
                o.out.output = out
                try check(chdman_create_cd(ctx, &o))
            }
        }
    }

    public func extractRaw(input: String, output: String, force: Bool = false) throws {
        var o = chdman_extract_raw_options()
        chdman_extract_raw_options_init(&o)
        o.force = force ? 1 : 0
        try input.withCString { i in
            try output.withCString { out in
                o.input = i
                o.output = out
                try check(chdman_extract_raw(ctx, &o))
            }
        }
    }

    /// output extension picks the format: .cue (CUE/BIN), .gdi, or cdrdao .toc
    public func extractCD(input: String, output: String, force: Bool = false) throws {
        var o = chdman_extract_cd_options()
        chdman_extract_cd_options_init(&o)
        o.force = force ? 1 : 0
        try input.withCString { i in
            try output.withCString { out in
                o.input = i
                o.output = out
                try check(chdman_extract_cd(ctx, &o))
            }
        }
    }
}

private func withOptionalCString<R>(_ s: String?, _ body: (UnsafePointer<CChar>?) throws -> R) rethrows -> R {
    guard let s else { return try body(nil) }
    return try s.withCString { try body($0) }
}
