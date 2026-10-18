# Vendored third-party and MAME utility code for libchdman.
# Everything here is built as position-independent static libraries with hidden
# visibility, then folded into libchdman (static and shared).

set(MAME ${CMAKE_CURRENT_SOURCE_DIR}/mame)

# MSVC switches MAME's own build uses (scripts/genie.lua); MAME 0.289 needs
# Visual Studio 2022 17.5 or newer for these and for C++20.
function(chdman_msvc_options tgt)
  if(MSVC)
    target_compile_options(${tgt} PRIVATE
      /bigobj /utf-8 /Zc:preprocessor
      "$<$<COMPILE_LANGUAGE:CXX>:/EHsc;/Zc:__cplusplus;/Zc:enumTypes;/Zc:templateScope;/Zc:u8EscapeEncoding>")
  endif()
endfunction()

function(chdman_vendor_target tgt)
  set_target_properties(${tgt} PROPERTIES
    POSITION_INDEPENDENT_CODE ON
    C_VISIBILITY_PRESET hidden
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON
    FOLDER vendor)
  chdman_msvc_options(${tgt})
  if(MSVC)
    target_compile_options(${tgt} PRIVATE /W0)
  else()
    target_compile_options(${tgt} PRIVATE -w)
  endif()
endfunction()

# ---------------------------------------------------------------- zlib (Zlib)
add_library(chdman_zlib OBJECT
  ${MAME}/3rdparty/zlib/adler32.c
  ${MAME}/3rdparty/zlib/compress.c
  ${MAME}/3rdparty/zlib/crc32.c
  ${MAME}/3rdparty/zlib/deflate.c
  ${MAME}/3rdparty/zlib/inffast.c
  ${MAME}/3rdparty/zlib/inflate.c
  ${MAME}/3rdparty/zlib/inftrees.c
  ${MAME}/3rdparty/zlib/trees.c
  ${MAME}/3rdparty/zlib/uncompr.c
  ${MAME}/3rdparty/zlib/zutil.c)
target_include_directories(chdman_zlib PUBLIC ${MAME}/3rdparty/zlib)
target_compile_definitions(chdman_zlib PUBLIC ZLIB_CONST PRIVATE $<$<NOT:$<BOOL:${WIN32}>>:HAVE_UNISTD_H>)
chdman_vendor_target(chdman_zlib)

# ------------------------------------------------- LZMA SDK (public domain)
add_library(chdman_lzma OBJECT
  ${MAME}/3rdparty/lzma/C/LzmaDec.c
  ${MAME}/3rdparty/lzma/C/LzmaEnc.c
  ${MAME}/3rdparty/lzma/C/LzFind.c
  ${MAME}/3rdparty/lzma/C/LzFindOpt.c
  ${MAME}/3rdparty/lzma/C/CpuArch.c)
target_include_directories(chdman_lzma PUBLIC ${MAME}/3rdparty)
target_compile_definitions(chdman_lzma PUBLIC Z7_ST Z7_PPMD_SUPPORT)
chdman_vendor_target(chdman_lzma)

# ------------------------------------------------ zstd (BSD-3-Clause option)
file(GLOB CHDMAN_ZSTD_SOURCES
  ${MAME}/3rdparty/zstd/lib/common/*.c
  ${MAME}/3rdparty/zstd/lib/compress/*.c
  ${MAME}/3rdparty/zstd/lib/decompress/*.c)
add_library(chdman_zstd OBJECT ${CHDMAN_ZSTD_SOURCES})
target_include_directories(chdman_zstd PUBLIC ${MAME}/3rdparty/zstd/lib)
# zstd forces default visibility on its API; keep it internal to libchdman
target_compile_definitions(chdman_zstd PUBLIC ZSTD_DISABLE_ASM XXH_NAMESPACE=ZSTD_ ZSTDLIB_VISIBLE= ZSTDERRORLIB_VISIBLE= ZDICTLIB_VISIBLE= PRIVATE ZSTD_MULTITHREAD=0)
chdman_vendor_target(chdman_zstd)

# --------------------------------------------- libFLAC (BSD-3-Clause, Xiph)
set(FLAC_SRC ${MAME}/3rdparty/flac/src/libFLAC)
add_library(chdman_flac OBJECT
  ${FLAC_SRC}/bitmath.c ${FLAC_SRC}/bitreader.c ${FLAC_SRC}/bitwriter.c
  ${FLAC_SRC}/cpu.c ${FLAC_SRC}/crc.c ${FLAC_SRC}/fixed.c
  ${FLAC_SRC}/fixed_intrin_avx2.c ${FLAC_SRC}/fixed_intrin_sse2.c
  ${FLAC_SRC}/fixed_intrin_sse42.c ${FLAC_SRC}/fixed_intrin_ssse3.c
  ${FLAC_SRC}/float.c ${FLAC_SRC}/format.c ${FLAC_SRC}/lpc.c
  ${FLAC_SRC}/lpc_intrin_avx2.c ${FLAC_SRC}/lpc_intrin_fma.c
  ${FLAC_SRC}/lpc_intrin_neon.c ${FLAC_SRC}/lpc_intrin_sse2.c
  ${FLAC_SRC}/lpc_intrin_sse41.c ${FLAC_SRC}/md5.c ${FLAC_SRC}/memory.c
  ${FLAC_SRC}/stream_decoder.c ${FLAC_SRC}/stream_encoder.c
  ${FLAC_SRC}/stream_encoder_framing.c
  ${FLAC_SRC}/stream_encoder_intrin_avx2.c
  ${FLAC_SRC}/stream_encoder_intrin_sse2.c
  ${FLAC_SRC}/stream_encoder_intrin_ssse3.c
  ${FLAC_SRC}/window.c)
if(WIN32)
  # UTF-8 fopen() wrapper libFLAC uses on Windows (as in MAME's build)
  target_sources(chdman_flac PRIVATE ${MAME}/3rdparty/flac/src/share/win_utf8_io/win_utf8_io.c)
endif()
target_include_directories(chdman_flac
  PUBLIC ${MAME}/3rdparty/flac/include
  PRIVATE ${FLAC_SRC}/include)
target_compile_definitions(chdman_flac
  PUBLIC FLAC__NO_DLL
  PRIVATE
    NDEBUG HAVE_CONFIG_H ENABLE_64_BIT_WORDS=1 OGG_FOUND=0 FLAC__HAS_OGG=0
    HAVE_LROUND=1 HAVE_INTTYPES_H HAVE_STDBOOL_H HAVE_STDINT_H HAVE_STDIO_H
    HAVE_STDLIB_H HAVE_STRING_H _FILE_OFFSET_BITS=64 _LARGEFILE_SOURCE
    CPU_IS_BIG_ENDIAN=0 CPU_IS_LITTLE_ENDIAN=1 WORDS_BIGENDIAN=0
    $<$<BOOL:${APPLE}>:FLAC__SYS_DARWIN>)
chdman_vendor_target(chdman_flac)

# ----------------------------------------------------------- utf8proc (MIT)
add_library(chdman_utf8proc OBJECT ${MAME}/3rdparty/utf8proc/utf8proc.c)
target_include_directories(chdman_utf8proc PUBLIC ${MAME}/3rdparty/utf8proc)
target_compile_definitions(chdman_utf8proc PUBLIC UTF8PROC_STATIC)
chdman_vendor_target(chdman_utf8proc)

# ------------------------------------- MAME util + osd subset (BSD-3-Clause)
set(UTIL ${MAME}/src/lib/util)
set(OSD ${MAME}/src/osd)
set(CHDMAN_MAME_SOURCES
  ${UTIL}/avhuff.cpp ${UTIL}/aviio.cpp ${UTIL}/bitmap.cpp ${UTIL}/cdrom.cpp
  ${UTIL}/chd.cpp ${UTIL}/chdcodec.cpp ${UTIL}/corealloc.cpp
  ${UTIL}/corefile.cpp ${UTIL}/corestr.cpp ${UTIL}/coreutil.cpp
  ${UTIL}/dvdrom.cpp ${UTIL}/flac.cpp ${UTIL}/hashing.cpp
  ${UTIL}/huffman.cpp ${UTIL}/ioprocs.cpp ${UTIL}/md5.cpp ${UTIL}/palette.cpp
  ${UTIL}/path.cpp ${UTIL}/strformat.cpp ${UTIL}/timeconv.cpp
  ${UTIL}/unicode.cpp ${UTIL}/vbiparse.cpp ${UTIL}/vecstream.cpp
  ${OSD}/osdcore.cpp ${OSD}/osdsync.cpp ${OSD}/strconv.cpp
  ${CMAKE_CURRENT_SOURCE_DIR}/osd_shim.cpp)
if(WIN32)
  list(APPEND CHDMAN_MAME_SOURCES
    ${OSD}/modules/file/winfile.cpp ${OSD}/modules/file/windir.cpp
    ${OSD}/windows/winutil.cpp ${OSD}/windows/winutf8.cpp)
else()
  list(APPEND CHDMAN_MAME_SOURCES
    ${OSD}/modules/file/posixfile.cpp ${OSD}/modules/file/posixdir.cpp)
endif()

add_library(chdman_mame OBJECT ${CHDMAN_MAME_SOURCES})
target_include_directories(chdman_mame PUBLIC ${UTIL} ${MAME}/src/lib ${OSD} ${OSD}/modules/file ${OSD}/modules/lib ${MAME}/3rdparty
  PRIVATE ${OSD}/windows)
target_compile_definitions(chdman_mame PUBLIC LSB_FIRST NDEBUG $<IF:$<BOOL:${WIN32}>,CRLF=3,CRLF=2>
  $<$<BOOL:${WIN32}>:OSD_WINDOWS WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE _WIN32_WINNT=0x0A00 _CRT_SECURE_NO_WARNINGS>
  $<$<NOT:$<BOOL:${WIN32}>>:SDLMAME_UNIX>
  $<$<BOOL:${APPLE}>:SDLMAME_MACOSX SDLMAME_DARWIN>)
target_link_libraries(chdman_mame PUBLIC chdman_zlib chdman_lzma chdman_zstd chdman_flac chdman_utf8proc)
target_compile_features(chdman_mame PUBLIC cxx_std_20)
chdman_vendor_target(chdman_mame)
