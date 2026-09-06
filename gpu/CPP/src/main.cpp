#include <QApplication>
#include <csignal>
#include <iostream>
#include "config.h"
#include "hw_accel.h"
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
    applyGpuPlatformHints();

    QApplication app(argc, argv);
    app.setApplicationName("rtsp-stress-test-cpp-gpu");
    app.setApplicationVersion("1.0.0");

    QFont appFont = app.font();
    appFont.setStyleHint(QFont::SansSerif);
    appFont.setPointSize(10);
    app.setFont(appFont);

    AppConfig config = AppConfig::loadFromArgsAndEnv(argc, argv);

    // Initialize GPU hardware acceleration subsystem
    auto hwAccel = HwAccelManager::create(config.hwAccel, config.streamCount);

    logPlatformPath(true);
    std::cout << "=================================================================\n"
              << " 6-Hour RTSP Video Grid Benchmark (C++ Qt6 GPU Zero-Copy Decode)\n"
              << "=================================================================\n"
              << " Platform:              " << platformName() << "\n"
              << " Target RTSP URL:       " << config.rtspUrl << "\n"
              << " Active Streams:        " << config.streamCount << "\n"
              << " Telemetry Output:      " << config.logPath << "\n"
              << " Machine ID:            " << config.machineId << "\n"
              << " Requested HwAccel:     " << config.hwAccel << "\n"
              << " Active GPU Device:     " << (hwAccel ? hwAccel->deviceName() : "None") << "\n"
              << " Rendering Pipeline:    QOpenGLWidget + BT.709 GPU NV12 Shaders\n"
              << " UI Refresh Rate:       " << config.renderFps << " FPS\n"
              << " Zero-Copy VRAM Rule:   Active (zero CPU RAM frame download)\n"
              << "=================================================================" << std::endl;

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

    MainWindow window(config, hwAccel);
    window.showMaximized();
    window.raise();
    window.activateWindow();

    return app.exec();
}
