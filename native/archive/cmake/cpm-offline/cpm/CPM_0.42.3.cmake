# Offline stand-in for CPM.cmake 0.42.3, which bit7z's build would otherwise
# download at configure time. NoIntrovert's build passes bit7z the 7-Zip
# source directly, so no CPM package should ever be needed; if one is, stop
# with a clear message instead of reaching out to the network.

function(CPMAddPackage)
  cmake_parse_arguments(CPM_ARGS "" "NAME;GITHUB_REPOSITORY;VERSION" "" ${ARGN})
  message(FATAL_ERROR
    "noi_archive builds offline, but bit7z asked to download '${CPM_ARGS_NAME}' "
    "(${CPM_ARGS_GITHUB_REPOSITORY} ${CPM_ARGS_VERSION}). Check NOI_7ZIP_DIR and the bit7z options in native/archive/CMakeLists.txt.")
endfunction()
