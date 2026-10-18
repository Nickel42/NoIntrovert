/*
 * noi_archive: a small C interface over bit7z, so NoIntrovert's C# code can
 * create, extract and test 7z/zip archives with 7-Zip's own engine.
 *
 * All strings are UTF-8. Every call that can fail returns a status and, on
 * failure, leaves a message readable with noi_last_error() until the next
 * call on the same handle. A handle runs one operation at a time; use one
 * handle per worker thread.
 */
#ifndef NOI_ARCHIVE_H
#define NOI_ARCHIVE_H

#include <stdint.h>

#ifdef _WIN32
#  define NOI_API __declspec(dllexport)
#  define NOI_CALL __cdecl
#else
#  define NOI_API __attribute__((visibility("default")))
#  define NOI_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define NOI_ABI_VERSION 1

typedef enum noi_status {
    NOI_OK = 0,
    NOI_ERROR = 1,     /* see noi_last_error() */
    NOI_CANCELLED = 2, /* the progress callback returned 0 */
    NOI_BAD_ARGS = 3
} noi_status;

typedef enum noi_format {
    NOI_FORMAT_7Z = 0,
    NOI_FORMAT_ZIP = 1
} noi_format;

/* Called as work proceeds; `total` is 0 until the size is known.
 * Return non-zero to continue, 0 to cancel. */
typedef int (NOI_CALL *noi_progress_fn)(void *user, uint64_t done, uint64_t total);

typedef struct noi_compress_options {
    uint32_t struct_size;   /* sizeof(noi_compress_options) */
    int32_t format;         /* noi_format */
    int32_t level;          /* 0 (store), 1, 3, 5, 7, 9 (ultra) */
    uint32_t threads;       /* 0 = 7-Zip's default (all cores) */
    int32_t solid;          /* 7z only: non-zero for a solid archive */
    int32_t contents_only;  /* with one folder input: store its contents, not the folder itself */
} noi_compress_options;

typedef struct noi_archive noi_archive;

NOI_API int NOI_CALL noi_abi_version(void);

/* Loads 7-Zip's library (7z.dll / 7z.so) from `lib7z_path`. */
NOI_API noi_status NOI_CALL noi_open(const char *lib7z_path, noi_archive **out, const char **error);
NOI_API void NOI_CALL noi_close(noi_archive *archive);
NOI_API const char *NOI_CALL noi_last_error(const noi_archive *archive);

/* Compresses files and folders into `out_path`, which must not exist.
 * Files are stored by name; folders are stored with their contents
 * (or only their contents, see contents_only). */
NOI_API noi_status NOI_CALL noi_compress(noi_archive *archive, const noi_compress_options *options,
                                         const char *const *inputs, int32_t input_count, const char *out_path,
                                         noi_progress_fn progress, void *user);

/* Extracts every item of any archive 7-Zip can read into `out_dir`, overwriting existing files. */
NOI_API noi_status NOI_CALL noi_extract(noi_archive *archive, const char *archive_path, const char *out_dir,
                                        noi_progress_fn progress, void *user);

/* Decompresses every item and checks its CRC without writing anything. */
NOI_API noi_status NOI_CALL noi_test(noi_archive *archive, const char *archive_path,
                                     noi_progress_fn progress, void *user);

#ifdef __cplusplus
}
#endif

#endif /* NOI_ARCHIVE_H */
