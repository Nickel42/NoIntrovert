# noi_archive: NoIntrovert's C interface over bit7z (see noi_archive.h).
#
#   cmake -S native/archive -B native/build-x64 -A x64 \
#         -DNOI_BIT7Z_DIR=<bit7z source> -DNOI_7ZIP_DIR=<7-Zip source>
#   cmake --build native/build-x64 --config Release
#
# Produces noi_archive.dll / libnoi_archive.dylib / libnoi_archive.so. 7-Zip's own library
# (7z.dll / 7z.so) is built separately from the same 7-Zip source and loaded
# at run time.

cmake_minimum_required(VERSION 3.20)
project(noi_archive LANGUAGES C CXX)

set(NOI_BIT7Z_DIR "" CACHE PATH "bit7z source tree (v4.1.0)")
set(NOI_7ZIP_DIR "" CACHE PATH "7-Zip source tree (26.04), the same one 7z.dll/7z.so is built from")
if(NOT EXISTS "${NOI_BIT7Z_DIR}/CMakeLists.txt")
  message(FATAL_ERROR "Set NOI_BIT7Z_DIR to the bit7z source tree")
endif()
if(NOT EXISTS "${NOI_7ZIP_DIR}/CPP/7zip")
  message(FATAL_ERROR "Set NOI_7ZIP_DIR to the 7-Zip source tree")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

# Static CRT on Windows, so the DLL needs no Visual C++ redistributable
# (7-Zip's own 7z.dll is built with /MT as well).
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# bit7z options: build against our 7-Zip copy, detect archive formats on
# extraction, and skip its tests, docs and install rules.
set(BIT7Z_CUSTOM_7ZIP_PATH "${NOI_7ZIP_DIR}" CACHE STRING "" FORCE)
set(BIT7Z_AUTO_FORMAT ON CACHE BOOL "" FORCE)
set(BIT7Z_GENERATE_PIC ON CACHE BOOL "" FORCE)
set(BIT7Z_STATIC_RUNTIME ON CACHE BOOL "" FORCE)
set(BIT7Z_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(BIT7Z_BUILD_DOCS OFF CACHE BOOL "" FORCE)
set(BIT7Z_INSTALL OFF CACHE BOOL "" FORCE)

# bit7z bootstraps the CPM package manager by downloading it at configure
# time. With a custom 7-Zip path and std::filesystem available it fetches
# nothing through CPM, so a local stand-in keeps the build fully offline.
set(CPM_SOURCE_CACHE "${CMAKE_CURRENT_SOURCE_DIR}/cmake/cpm-offline" CACHE PATH "" FORCE)

# bit7z checks for std::filesystem by building and *running* a test program.
# Cross-compiling for ARM64 on an x64 machine, the program can't run, so the
# check fails and bit7z would fall back to downloading ghc::filesystem. MSVC
# has had std::filesystem built in (no extra libraries) since VS 2017, so
# answer the check up front; check_cxx_source_runs skips a defined result.
if(MSVC)
  set(STANDARD_FILESYSTEM_LINKS 1 CACHE INTERNAL "MSVC provides std::filesystem in its standard library")
endif()

add_subdirectory("${NOI_BIT7Z_DIR}" bit7z EXCLUDE_FROM_ALL)

add_library(noi_archive SHARED noi_archive.cpp noi_archive.h)
target_link_libraries(noi_archive PRIVATE bit7z)
target_compile_definitions(noi_archive PRIVATE BIT7Z_AUTO_FORMAT)

if(APPLE)
  set_target_properties(noi_archive PROPERTIES INSTALL_NAME_DIR "@rpath")
endif()
if(UNIX AND NOT APPLE)
  # Hidden visibility still leaves libstdc++'s template instantiations
  # exported; the version script keeps the library to noi_* only.
  target_link_options(noi_archive PRIVATE "LINKER:--version-script=${CMAKE_CURRENT_SOURCE_DIR}/noi_archive.map")
  set_target_properties(noi_archive PROPERTIES LINK_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/noi_archive.map")
endif()
