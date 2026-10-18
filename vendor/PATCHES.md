# Vendored source provenance and local patches

Source: https://github.com/mamedev/mame, tag `mame0289`
(commit `f34f02505e32c1993c6a782b6814232cbfc74e36`), fetched with a sparse
checkout. Only the files needed to build the CHD code path were copied; the
directory layout under `vendor/mame/` mirrors upstream.

Files under `vendor/mame/` are unmodified except for the following.

## `src/lib/util/chd.h`, `src/lib/util/chd.cpp`

1. Added `chd_file_compressor::compress_wait_idle()`, which blocks until the
   read and compression work queues are empty. libchdman's compressors call
   it from their destructors. If compression is abandoned part-way through
   (error or cancellation), this stops an I/O worker thread from calling
   `read_data()` on an object that is being destroyed. Upstream chdman
   never needed it because it exits the process on error.
2. Removed the two `fprintf(stderr, ...)` calls in
   `chd_file_compressor::async_read()`'s exception handlers. The error is
   still recorded in `m_read_error` and returned by `compress_continue()`.
   libchdman reports it through `chdman_last_error()`.

Neither change affects the bytes written to CHD files; `tests/parity.cmake`
checks that output is byte-identical to the unmodified upstream `chdman`
logic.

## Not vendored (replaced by `vendor/osd_shim.cpp`)

- `src/osd/modules/lib/osdlib_*.cpp`: only `osd_getenv()` was needed.
- `src/osd/modules/file/{posix,win}socket.cpp` and
  `{posix,win}ptty.cpp`: the socket, domain-socket and pty openers are stubs that return
  `std::errc::not_supported`, so special file names are treated as plain
  files.
- `src/lib/util/delegate.cpp` / `mfpresolve.cpp`: not used by the CHD path.

## Updating to a newer MAME

1. Sparse-checkout the new tag (see the paths in `vendor/CMakeLists.txt`).
2. Re-copy the files and re-apply the two `chd.*` changes above.
3. Diff `src/tools/chdman.cpp` against the previous version and port any
   logic changes into `src/chdman_core.cpp`. That file keeps upstream's
   function names to make this easier.
4. Build with `-DCHDMAN_BUILD_UPSTREAM_REFERENCE=ON` and run `ctest`. The
   `parity_upstream` test compares output against the new upstream chdman.
5. Update the pinned header SHA-1s in `tests/test_chdman.cpp` only if the
   CHD format itself changed.
