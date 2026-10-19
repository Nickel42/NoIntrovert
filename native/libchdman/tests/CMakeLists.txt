# Helper that writes a tiny AVI using MAME's own aviio (internal API), so the
# laserdisc path can be exercised without binary fixtures.
# Built once per MSVC runtime in use (see CHDMAN_STATIC_MSVC_RUNTIME).
function(chdman_add_test_avi tgt runtime)
  add_library(${tgt} OBJECT avi_helper.cpp)
  target_include_directories(${tgt} PRIVATE
    $<TARGET_PROPERTY:chdman_mame,INTERFACE_INCLUDE_DIRECTORIES>)
  target_compile_definitions(${tgt} PRIVATE
    $<TARGET_PROPERTY:chdman_mame,INTERFACE_COMPILE_DEFINITIONS>)
  set_target_properties(${tgt} PROPERTIES MSVC_RUNTIME_LIBRARY "${runtime}")
  chdman_msvc_options(${tgt})
endfunction()

chdman_add_test_avi(chdman_test_avi "${CHDMAN_PRIMARY_MSVC_RUNTIME}")
if(TARGET chdman_static)
  chdman_add_test_avi(chdman_test_avi_static "${CHDMAN_STATIC_MSVC_RUNTIME}")
endif()

function(chdman_add_test_exe name lib avi runtime workdir)
  add_executable(${name} test_chdman.cpp sha1.c $<TARGET_OBJECTS:${avi}> ${ARGN})
  set_target_properties(${name} PROPERTIES MSVC_RUNTIME_LIBRARY "${runtime}")
  target_link_libraries(${name} PRIVATE ${lib})
  chdman_msvc_options(${name})
  target_compile_definitions(${name} PRIVATE CHDMAN_TEST_WORKDIR="${CMAKE_CURRENT_BINARY_DIR}/${workdir}")
  if(WIN32)
    target_link_libraries(${name} PRIVATE shlwapi)
  elseif(NOT APPLE)
    find_package(Threads REQUIRED)
    target_link_libraries(${name} PRIVATE Threads::Threads)
  endif()
  add_test(NAME ${name} COMMAND ${name})
  set_tests_properties(${name} PROPERTIES FIXTURES_SETUP ${name}_fixtures)
endfunction()

if(TARGET chdman_shared)
  # the shared library hides MAME internals, so the AVI helper gets its own copy
  chdman_add_test_exe(chdman_tests chdman_shared chdman_test_avi "${CHDMAN_SHARED_MSVC_RUNTIME}" work_shared ${CHDMAN_VENDOR_OBJECTS})
  if(APPLE)
    set_target_properties(chdman_tests PROPERTIES BUILD_RPATH "$<TARGET_FILE_DIR:chdman_shared>")
  endif()
  if(WIN32)
    add_custom_command(TARGET chdman_tests POST_BUILD
      COMMAND ${CMAKE_COMMAND} -E copy_if_different $<TARGET_FILE:chdman_shared> $<TARGET_FILE_DIR:chdman_tests>)
  endif()
endif()

if(TARGET chdman_static)
  if(APPLE AND CHDMAN_STATIC_PRELINK)
    # the prelinked archive hides MAME internals; the AVI helper needs its own copy
    chdman_add_test_exe(chdman_tests_static chdman_static chdman_test_avi_static "${CHDMAN_STATIC_MSVC_RUNTIME}" work_static ${CHDMAN_STATIC_VENDOR_OBJECTS})
  else()
    # MAME internals (used by the AVI helper) come from the static archive
    chdman_add_test_exe(chdman_tests_static chdman_static chdman_test_avi_static "${CHDMAN_STATIC_MSVC_RUNTIME}" work_static)
  endif()
endif()

# CLI smoke test
if(TARGET chdman-cli)
  add_test(NAME cli_listtemplates COMMAND chdman-cli listtemplates)
  set_tests_properties(cli_listtemplates PROPERTIES PASS_REGULAR_EXPRESSION "Fireball CR 13.0 AT")
  add_test(NAME cli_bad_option COMMAND chdman-cli info --bogus x)
  set_tests_properties(cli_bad_option PROPERTIES WILL_FAIL TRUE)
endif()
