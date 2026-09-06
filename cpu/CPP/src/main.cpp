#include <QApplication>
#include <csignal>
#include <iostream>
#include "config.h"
#include "main_window.h"
#include "platform.h"

#if defined(Q_OS_UNIX)
#include <unistd.h>
#include <sys/socket.h>
#include <QSocketNotifier>

static int sigFd[2];

static void signalHandler(int /*sig*/) {
    char a = 1;
    ::write(sigFd[0], &a, sizeof(a));
}
#endif

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <timeapi.h>
#pragma comment(lib, "winmm.lib")
#endif

int main(int argc, char* argv[]) {
#if defined(_WIN32)
    timeBeginPeriod(1);
#endif
    raiseFileDescriptorLimit();
    applyCpuPlatformHints();

    QApplication app(argc, argv);
    app.setApplicationName("rtsp-stress-test-cpp-cpu");
    app.setApplicationVersion("1.0.0");

    AppConfig config = AppConfig::loadFromArgsAndEnv(argc, argv);

    logPlatformPath(false);
    std::cout << "=======================================================\n"
              << " 6-Hour RTSP Video Grid Benchmark (C++ Qt6 CPU Decode)\n"
              << "=======================================================\n"
              << " Platform:          " << platformName() << "\n"
              << " Target RTSP URL:   " << config.rtspUrl << "\n"
              << " Active Streams:    " << config.streamCount << "\n"
              << " Telemetry Output:  " << config.logPath << "\n"
              << " Machine ID:        " << config.machineId << "\n"
              << " Decoding Backend:  libavcodec (CPU Software)\n"
              << " Color Conversion:  libswscale (RGB32)\n"
              << " Presentation:      Zero-Copy QImage on QWidget (QPainter)\n"
              << " UI Refresh Rate:   " << config.renderFps << " FPS\n"
              << "=======================================================" << std::endl;

#if defined(Q_OS_UNIX)
    if (::socketpair(AF_UNIX, SOCK_STREAM, 0, sigFd) == 0) {
        auto* sn = new QSocketNotifier(sigFd[1], QSocketNotifier::Read, &app);
        QObject::connect(sn, &QSocketNotifier::activated, [&app, sn]() {
            sn->setEnabled(false);
            char a;
            ::read(sigFd[1], &a, sizeof(a));
            std::cout << "\n[Main] Shutdown signal received, terminating application cleanly..." << std::endl;
            app.quit();
        });

        struct sigaction sa;
        sa.sa_handler = signalHandler;
        sigemptyset(&sa.sa_mask);
        sa.sa_flags = SA_RESTART;
        sigaction(SIGINT, &sa, nullptr);
        sigaction(SIGTERM, &sa, nullptr);
    }
#endif

    MainWindow window(config);
    window.showMaximized();
    window.raise();
    window.activateWindow();

    return app.exec();
}
