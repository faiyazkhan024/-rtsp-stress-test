cmake_minimum_required(VERSION 3.16)
project(rtsp_benchmark_cpp_cpu VERSION 1.0.0 LANGUAGES CXX C)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Optimization flags for software decoding benchmark
if(CMAKE_CXX_COMPILER_ID MATCHES "GNU|Clang|AppleClang")
    add_compile_options(-O3 -Wall -Wextra)
elseif(MSVC)
    add_compile_options(/O2 /W3)
endif()

# macOS Homebrew prefix path hints
if(APPLE)
    list(APPEND CMAKE_PREFIX_PATH
        "/opt/homebrew/opt/qtbase"
        "/opt/homebrew/opt/qt"
        "/opt/homebrew"
        "/usr/local/opt/qtbase"
        "/usr/local/opt/qt"
        "/usr/local"
    )
endif()

# Qt6 Configuration
find_package(Qt6 REQUIRED COMPONENTS Core Gui Widgets)
set(CMAKE_AUTOMOC ON)
set(CMAKE_AUTORCC ON)
set(CMAKE_AUTOUIC ON)

# FFmpeg libraries (libavcodec, libavformat, libswscale, libavutil)
find_package(PkgConfig QUIET)
if(PKG_CONFIG_FOUND)
    pkg_check_modules(FFMPEG IMPORTED_TARGET libavcodec libavformat libswscale libavutil)
endif()

if(NOT TARGET PkgConfig::FFMPEG)
    set(_FFMPEG_SEARCH_PATHS "$ENV{FFMPEG_DIR}" "C:/FFmpeg" "C:/Program Files/FFmpeg")
    if(DEFINED ENV{LOCALAPPDATA})
        file(TO_CMAKE_PATH "$ENV{LOCALAPPDATA}" _LOCALAPPDATA_CMAKE)
        file(GLOB _WINGET_FFMPEG "${_LOCALAPPDATA_CMAKE}/Microsoft/WinGet/Packages/*FFmpeg*/*ffmpeg*")
        list(APPEND _FFMPEG_SEARCH_PATHS ${_WINGET_FFMPEG})
    endif()

    find_path(FFMPEG_INCLUDE_DIR NAMES libavcodec/avcodec.h
        HINTS ${_FFMPEG_SEARCH_PATHS}
        PATH_SUFFIXES include
    )
    find_library(AVCODEC_LIBRARY NAMES avcodec HINTS ${_FFMPEG_SEARCH_PATHS} PATH_SUFFIXES lib)
    find_library(AVFORMAT_LIBRARY NAMES avformat HINTS ${_FFMPEG_SEARCH_PATHS} PATH_SUFFIXES lib)
    find_library(AVUTIL_LIBRARY NAMES avutil HINTS ${_FFMPEG_SEARCH_PATHS} PATH_SUFFIXES lib)
    find_library(SWSCALE_LIBRARY NAMES swscale HINTS ${_FFMPEG_SEARCH_PATHS} PATH_SUFFIXES lib)

    if(FFMPEG_INCLUDE_DIR AND AVCODEC_LIBRARY AND AVFORMAT_LIBRARY AND AVUTIL_LIBRARY AND SWSCALE_LIBRARY)
        add_library(PkgConfig::FFMPEG INTERFACE IMPORTED)
        target_include_directories(PkgConfig::FFMPEG INTERFACE "${FFMPEG_INCLUDE_DIR}")
        target_link_libraries(PkgConfig::FFMPEG INTERFACE
            "${AVCODEC_LIBRARY}"
            "${AVFORMAT_LIBRARY}"
            "${AVUTIL_LIBRARY}"
            "${SWSCALE_LIBRARY}"
        )
    else()
        message(FATAL_ERROR "Could NOT find FFmpeg development libraries (libavcodec, libavformat, libswscale, libavutil)")
    endif()
endif()

add_executable(rtsp-stress-test-cpp-cpu
    src/main.cpp
    src/config.h
    src/config.cpp
    src/platform.h
    src/platform.cpp
    src/stream_worker.h
    src/stream_worker.cpp
    src/video_widget.h
    src/video_widget.cpp
    src/telemetry.h
    src/telemetry.cpp
    src/main_window.h
    src/main_window.cpp
)

target_link_libraries(rtsp-stress-test-cpp-cpu PRIVATE
    Qt6::Core
    Qt6::Gui
    Qt6::Widgets
    PkgConfig::FFMPEG
)

target_include_directories(rtsp-stress-test-cpp-cpu PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/src
)

# Install target
install(TARGETS rtsp-stress-test-cpp-cpu DESTINATION bin)
