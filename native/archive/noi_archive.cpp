// noi_archive: C interface over bit7z. See noi_archive.h.

#include "noi_archive.h"

#include <bit7z/bit7zlibrary.hpp>
#include <bit7z/bitexception.hpp>
#include <bit7z/bitfilecompressor.hpp>
#include <bit7z/bitfileextractor.hpp>
#include <bit7z/bitformat.hpp>

#include <exception>
#include <memory>
#include <string>
#include <vector>

struct noi_archive {
    std::unique_ptr<bit7z::Bit7zLibrary> lib;
    std::string error;
};

namespace {

// Routes bit7z's total/progress callbacks to one C callback, and remembers
// whether the caller asked to stop, so an abort isn't reported as a failure.
class ProgressBridge {
public:
    ProgressBridge(noi_progress_fn fn, void *user) : fn_(fn), user_(user) {}

    template <typename Handler>
    void attach(Handler &handler) {
        if (fn_ == nullptr) return;
        handler.setTotalCallback([this](uint64_t total) { total_ = total; });
        handler.setProgressCallback([this](uint64_t done) {
            if (fn_(user_, done, total_) == 0) {
                cancelled_ = true;
                return false;
            }
            return true;
        });
    }

    bool cancelled() const { return cancelled_; }

private:
    noi_progress_fn fn_;
    void *user_;
    uint64_t total_ = 0;
    bool cancelled_ = false;
};

// Runs one operation, turning exceptions into a status and a message.
template <typename Body>
noi_status guarded(noi_archive *archive, const ProgressBridge &progress, Body body) {
    archive->error.clear();
    try {
        body();
        return NOI_OK;
    } catch (const bit7z::BitException &e) {
        if (progress.cancelled()) return NOI_CANCELLED;
        archive->error = e.what();
        for (const auto &failed : e.failedFiles()) {
            archive->error += "\n  ";
            archive->error += failed.first;
            archive->error += ": ";
            archive->error += failed.second.message();
        }
    } catch (const std::exception &e) {
        if (progress.cancelled()) return NOI_CANCELLED;
        archive->error = e.what();
    } catch (...) {
        archive->error = "unknown error";
    }
    return NOI_ERROR;
}

const bit7z::BitInOutFormat &to_format(int32_t format) {
    return format == NOI_FORMAT_ZIP ? static_cast<const bit7z::BitInOutFormat &>(bit7z::BitFormat::Zip)
                                    : static_cast<const bit7z::BitInOutFormat &>(bit7z::BitFormat::SevenZip);
}

bool valid_level(int32_t level) {
    return level == 0 || level == 1 || level == 3 || level == 5 || level == 7 || level == 9;
}

} // namespace

extern "C" {

NOI_API int NOI_CALL noi_abi_version(void) { return NOI_ABI_VERSION; }

NOI_API noi_status NOI_CALL noi_open(const char *lib7z_path, noi_archive **out, const char **error) {
    static thread_local std::string open_error;
    if (out == nullptr || lib7z_path == nullptr) return NOI_BAD_ARGS;
    *out = nullptr;
    try {
        auto archive = std::make_unique<noi_archive>();
        archive->lib = std::make_unique<bit7z::Bit7zLibrary>(lib7z_path);
        *out = archive.release();
        return NOI_OK;
    } catch (const std::exception &e) {
        open_error = e.what();
    } catch (...) {
        open_error = "unknown error loading 7-Zip";
    }
    if (error != nullptr) *error = open_error.c_str();
    return NOI_ERROR;
}

NOI_API void NOI_CALL noi_close(noi_archive *archive) { delete archive; }

NOI_API const char *NOI_CALL noi_last_error(const noi_archive *archive) {
    return archive != nullptr ? archive->error.c_str() : "";
}

NOI_API noi_status NOI_CALL noi_compress(noi_archive *archive, const noi_compress_options *options,
                                         const char *const *inputs, int32_t input_count, const char *out_path,
                                         noi_progress_fn progress, void *user) {
    if (archive == nullptr || options == nullptr || options->struct_size < sizeof(noi_compress_options) ||
        inputs == nullptr || input_count <= 0 || out_path == nullptr || !valid_level(options->level)) {
        return NOI_BAD_ARGS;
    }
    ProgressBridge bridge(progress, user);
    return guarded(archive, bridge, [&] {
        bit7z::BitFileCompressor compressor(*archive->lib, to_format(options->format));
        compressor.setCompressionLevel(static_cast<bit7z::BitCompressionLevel>(options->level));
        if (options->threads > 0) compressor.setThreadsCount(options->threads);
        if (options->format == NOI_FORMAT_7Z) compressor.setSolidMode(options->solid != 0);
        bridge.attach(compressor);

        if (options->contents_only != 0 && input_count == 1) {
            compressor.compressDirectoryContents(inputs[0], out_path, true);
        } else {
            std::vector<bit7z::tstring> paths(inputs, inputs + input_count);
            compressor.compress(paths, out_path);
        }
    });
}

NOI_API noi_status NOI_CALL noi_extract(noi_archive *archive, const char *archive_path, const char *out_dir,
                                        noi_progress_fn progress, void *user) {
    if (archive == nullptr || archive_path == nullptr || out_dir == nullptr) return NOI_BAD_ARGS;
    ProgressBridge bridge(progress, user);
    return guarded(archive, bridge, [&] {
        bit7z::BitFileExtractor extractor(*archive->lib, bit7z::BitFormat::Auto);
        extractor.setOverwriteMode(bit7z::OverwriteMode::Overwrite);
        bridge.attach(extractor);
        extractor.extract(archive_path, out_dir);
    });
}

NOI_API noi_status NOI_CALL noi_test(noi_archive *archive, const char *archive_path,
                                     noi_progress_fn progress, void *user) {
    if (archive == nullptr || archive_path == nullptr) return NOI_BAD_ARGS;
    ProgressBridge bridge(progress, user);
    return guarded(archive, bridge, [&] {
        bit7z::BitFileExtractor extractor(*archive->lib, bit7z::BitFormat::Auto);
        bridge.attach(extractor);
        extractor.test(archive_path);
    });
}

} // extern "C"
