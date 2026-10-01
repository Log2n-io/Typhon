cmake_minimum_required(VERSION 3.25)

# The Typhon client SDK for native clients (design/Subscriptions/13 § 7, 05 § 3a): C++20 inside, a C ABI outside, no third-party
# dependency. Every language with an FFI binds the C header; the C++ is an implementation detail.
project(TyphonClientNative VERSION 0.1.0 LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

option(TYPHON_NATIVE_TESTS "Build the native SDK's tests" ON)
option(TYPHON_NATIVE_SANITIZE "Build with AddressSanitizer (and UndefinedBehaviorSanitizer where the compiler has it)" OFF)

# W1's arithmetic is binary64, one IEEE operation per step: no contraction into FMA, no fast-math reassociation. A decoder that
# disagrees with the C# reference in the last bit fails the golden vectors, so these are correctness flags, not tuning.
if(MSVC)
  set(TYPHON_WARNINGS /W4 /WX /permissive- /utf-8 /fp:precise /fp:contract-)
else()
  set(TYPHON_WARNINGS -Wall -Wextra -Wpedantic -Werror -ffp-contract=off -fno-fast-math)
endif()

if(TYPHON_NATIVE_SANITIZE)
  if(MSVC)
    add_compile_options(/fsanitize=address /Zi)
    add_link_options(/INCREMENTAL:NO)
  else()
    add_compile_options(-fsanitize=address,undefined -fno-omit-frame-pointer -fno-sanitize-recover=all)
    add_link_options(-fsanitize=address,undefined)
  endif()
endif()

set(TYPHON_SOURCES
  src/wire/math.cpp
  src/wire/reader.cpp
  src/wire/writer.cpp
  src/wire/utf8.cpp
  src/wire/codec_kinds.cpp
  src/wire/realm_frame.cpp
  src/wire/field_codec.cpp
  src/wire/messages.cpp
  src/wire/commands.cpp
  src/wire/tick_reader.cpp
  src/json/json.cpp
  src/catalog/catalog.cpp
  src/catalog/catalog_validator.cpp
  src/catalog/catalog_plan.cpp
  src/store/memory.cpp
  src/store/schema.cpp
  src/store/archetype_store.cpp
  src/store/world_store.cpp
  src/motion/motion.cpp
  src/clock/clock.cpp
  src/aggregates/aggregate_grid.cpp
  src/apply/frame_state.cpp
  src/apply/frame_applier.cpp
  src/net/tcp_transport.cpp
  src/net/connection.cpp
  src/net/reconnect.cpp
  src/commands/command_queue.cpp
  src/client/client.cpp
  src/capi/client_capi.cpp
)

add_library(typhon_client STATIC ${TYPHON_SOURCES})
target_include_directories(typhon_client PUBLIC include PRIVATE src)
target_compile_options(typhon_client PRIVATE ${TYPHON_WARNINGS})
if(WIN32)
  target_link_libraries(typhon_client PRIVATE ws2_32)
endif()

if(TYPHON_NATIVE_TESTS)
  enable_testing()
  add_executable(typhon_client_tests
    tests/test_main.cpp
    tests/golden_support.cpp
    tests/golden_codecs_test.cpp
    tests/golden_catalogs_test.cpp
    tests/golden_messages_test.cpp
    tests/golden_ticks_test.cpp
    tests/wire_refusals_test.cpp
    tests/golden_stream_test.cpp
    tests/store_test.cpp
    tests/clock_test.cpp
    tests/net_test.cpp
    tests/capi_test.cpp
    tests/snapshot.cpp
    tests/alloc_test.cpp
    tests/alloc_counter.cpp
    tests/tcp_test.cpp
    tests/lifecycle_test.cpp
    tests/corruption_test.cpp
    tests/json_test.cpp
  )
  target_include_directories(typhon_client_tests PRIVATE src tests)
  target_compile_options(typhon_client_tests PRIVATE ${TYPHON_WARNINGS})
  find_package(Threads REQUIRED)
  target_link_libraries(typhon_client_tests PRIVATE typhon_client Threads::Threads)
  if(WIN32)
    # tcp_test.cpp plays the server with Winsock directly.
    target_link_libraries(typhon_client_tests PRIVATE ws2_32)
  endif()
  # The golden vectors are the C# reference encoder's output, shared by every SDK (05 § 4).
  target_compile_definitions(typhon_client_tests PRIVATE
    TYPHON_GOLDEN_DIR="${CMAKE_CURRENT_SOURCE_DIR}/../../test/Typhon.Protocol.Tests/Golden")
  add_test(NAME typhon_client_tests COMMAND typhon_client_tests)

  # The live differential test's native half (scripts/native-live-test.py drives it against Typhon.Subscriptions.E2EHost).
  add_executable(typhon_client_live tests/live_main.cpp tests/golden_support.cpp tests/snapshot.cpp tests/alloc_counter.cpp)
  target_include_directories(typhon_client_live PRIVATE src tests)
  target_compile_options(typhon_client_live PRIVATE ${TYPHON_WARNINGS})
  target_link_libraries(typhon_client_live PRIVATE typhon_client)
  target_compile_definitions(typhon_client_live PRIVATE
    TYPHON_GOLDEN_DIR="${CMAKE_CURRENT_SOURCE_DIR}/../../test/Typhon.Protocol.Tests/Golden")

  # The C ABI compiled as C: the header must parse under a C compiler (13 § 7). Linked by the C++ linker, for the library's runtime.
  add_executable(typhon_client_c_test tests/capi_header_test.c)
  if(MSVC)
    target_compile_options(typhon_client_c_test PRIVATE /W4 /WX /utf-8)
  else()
    target_compile_options(typhon_client_c_test PRIVATE -Wall -Wextra -Wpedantic -Werror)
  endif()
  target_link_libraries(typhon_client_c_test PRIVATE typhon_client)
  set_target_properties(typhon_client_c_test PROPERTIES LINKER_LANGUAGE CXX)
  add_test(NAME typhon_client_c_test COMMAND typhon_client_c_test)
endif()
