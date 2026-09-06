#include "platform.h"

#include <algorithm>
#include <iostream>

#if defined(__unix__) || defined(__APPLE__)
#include <sys/resource.h>
#endif

#include <QtGlobal>
#include <QSurfaceFormat>

void raiseFileDescriptorLimit() {
#if defined(__unix__) || defined(__APPLE__)
    struct rlimit rl;
    if (getrlimit(RLIMIT_NOFILE, &rl) != 0) {
        return;
    }
    rlim_t old = rl.rlim_cur;
    if (rl.rlim_max < static_cast<rlim_t>(kNofileTarget)) {
        rl.rlim_max = static_cast<rlim_t>(kNofileTarget);
    }
    rl.rlim_cur = std::min<rlim_t>(static_cast<rlim_t>(kNofileTarget), rl.rlim_max);
    if (setrlimit(RLIMIT_NOFILE, &rl) == 0) {
        std::cout << "[Platform] Raised RLIMIT_NOFILE from " << old << " to " << rl.rlim_cur << std::endl;
    }
#endif
}

std::string platformName() {
#if defined(__APPLE__)
    return "macOS";
#elif defined(_WIN32)
    return "Windows";
#elif defined(__linux__)
    return "Linux";
#else
    return "Unknown";
#endif
}

void applyCpuPlatformHints() {
#if defined(Q_OS_LINUX)
    if (qEnvironmentVariableIsSet("LIBGL_ALWAYS_SOFTWARE") ||
        qEnvironmentVariableIsSet("QT_QUICK_BACKEND")) {
        qputenv("QT_QPA_PLATFORM", "xcb");
    }
#endif
}

void applyGpuPlatformHints() {
#ifdef Q_OS_MAC
    QSurfaceFormat fmt;
    fmt.setRenderableType(QSurfaceFormat::OpenGL);
    fmt.setProfile(QSurfaceFormat::CoreProfile);
    fmt.setVersion(4, 1);
    fmt.setSwapBehavior(QSurfaceFormat::DoubleBuffer);
    QSurfaceFormat::setDefaultFormat(fmt);
#elif defined(Q_OS_WIN)
    QSurfaceFormat fmt;
    fmt.setRenderableType(QSurfaceFormat::OpenGL);
    fmt.setProfile(QSurfaceFormat::CompatibilityProfile);
    fmt.setSwapInterval(0); // Non-blocking swap to allow all 30 widgets to render smoothly
    fmt.setSwapBehavior(QSurfaceFormat::DoubleBuffer);
    QSurfaceFormat::setDefaultFormat(fmt);
#endif
}

void logPlatformPath(bool gpu) {
    if (gpu) {
#if defined(__APPLE__)
        std::cout << "[Platform] macOS: VideoToolbox + IOSurface / OpenGL (no VA-API/EGL)\n";
#elif defined(_WIN32)
        std::cout << "[Platform] Windows: D3D11VA / CUDA hardware decode\n";
#elif defined(__linux__)
        std::cout << "[Platform] Linux: CUDA / VA-API hardware decode\n";
#endif
    } else {
#if defined(__APPLE__)
        std::cout << "[Platform] macOS: libavcodec software decode, Cocoa compositor\n";
#elif defined(_WIN32)
        std::cout << "[Platform] Windows: libavcodec software decode\n";
#elif defined(__linux__)
        std::cout << "[Platform] Linux: libavcodec software decode (no VA-API)\n";
#endif
    }
}
