#include "config.h"

#include <iostream>
#include <cstdlib>
#include <filesystem>
#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#else
#include <unistd.h>
#endif

namespace fs = std::filesystem;

static bool canWriteToDir(const fs::path& dir) {
    std::error_code ec;
    fs::create_directories(dir, ec);
    if (ec) return false;

    fs::path testFile = dir / ".test_write_perm";
    FILE* f = fopen(testFile.string().c_str(), "w");
    if (!f) return false;
    fclose(f);
    fs::remove(testFile, ec);
    return true;
}

std::string AppConfig::resolveLogPath(const std::string& preferredPath) {
    fs::path pref(preferredPath);
    fs::path dir = pref.parent_path();
    if (dir.empty()) dir = ".";

    if (canWriteToDir(dir)) {
        return preferredPath;
    }

    // Graceful fallback to ./logs/fps_metrics.log
    fs::path fallbackDir = "./logs";
    std::error_code ec;
    fs::create_directories(fallbackDir, ec);
    fs::path fallbackFile = fallbackDir / pref.filename();
    std::cout << "[Config] Warning: Cannot write to '" << dir.string()
              << "'. Falling back to '" << fallbackFile.string() << "'" << std::endl;
    return fallbackFile.string();
}

AppConfig AppConfig::loadFromArgsAndEnv(int argc, char* argv[]) {
    AppConfig config;

    // 1. Check Environment Variables
    if (const char* envUrl = std::getenv("RTSP_URL")) {
        config.rtspUrl = envUrl;
    }
    if (const char* envPattern = std::getenv("RTSP_URL_PATTERN")) {
        config.rtspUrlPattern = envPattern;
    }
    if (const char* envCount = std::getenv("STREAM_COUNT")) {
        int c = std::atoi(envCount);
        if (c > 0) config.streamCount = c;
    }
    if (const char* envW = std::getenv("RENDER_WIDTH")) {
        int w = std::atoi(envW);
        if (w > 0) config.renderWidth = w;
    }
    if (const char* envH = std::getenv("RENDER_HEIGHT")) {
        int h = std::atoi(envH);
        if (h > 0) config.renderHeight = h;
    }
    if (const char* envLog = std::getenv("FPS_METRICS_LOG_PATH")) {
        config.logPath = envLog;
    } else if (const char* envLogDir = std::getenv("BENCHMARK_LOG_DIR")) {
        config.logPath = (fs::path(envLogDir) / "fps_metrics.log").string();
    }
    if (const char* envMachine = std::getenv("MACHINE_ID")) {
        config.machineId = envMachine;
    } else {
        char hostname[256] = {0};
#if defined(_WIN32)
        DWORD size = sizeof(hostname);
        if (GetComputerNameA(hostname, &size) && hostname[0] != '\0') {
            config.machineId = hostname;
        }
#else
        if (gethostname(hostname, sizeof(hostname) - 1) == 0 && hostname[0] != '\0') {
            config.machineId = hostname;
        }
#endif
    }

    // 2. Parse Command Line Arguments
    for (int i = 1; i < argc; ++i) {
        std::string arg = argv[i];
        if ((arg == "--url" || arg == "-u") && i + 1 < argc) {
            config.rtspUrl = argv[++i];
        } else if ((arg == "--streams" || arg == "-s") && i + 1 < argc) {
            int c = std::atoi(argv[++i]);
            if (c > 0) config.streamCount = c;
        } else if ((arg == "--log-path" || arg == "-l") && i + 1 < argc) {
            config.logPath = argv[++i];
        } else if (arg == "--log-dir" && i + 1 < argc) {
            config.logPath = (fs::path(argv[++i]) / "fps_metrics.log").string();
        } else if ((arg == "--machine-id" || arg == "-m") && i + 1 < argc) {
            config.machineId = argv[++i];
        } else if (arg == "--help" || arg == "-h") {
            std::cout << "Usage: " << argv[0] << " [options]\n"
                      << "Options:\n"
                      << "  --url, -u <url>         RTSP stream URL (default: rtsp://127.0.0.1:8554/live)\n"
                      << "  --streams, -s <count>   Number of streams in grid (default: 30)\n"
                      << "  --log-path, -l <path>   Telemetry log file path (default: /var/log/benchmark/fps_metrics.log)\n"
                      << "  --log-dir <dir>         Telemetry log directory\n"
                      << "  --machine-id, -m <id>   Machine/node identifier\n"
                      << "  --help, -h              Show this help\n";
            std::exit(0);
        }
    }

    // Resolve log path with fallback
    config.logPath = resolveLogPath(config.logPath);
    if (config.rtspUrlPattern.empty() && config.rtspUrl.find("%d") != std::string::npos) {
        config.rtspUrlPattern = config.rtspUrl;
    }

    return config;
}

std::string AppConfig::urlForStream(int index) const {
    if (rtspUrlPattern.empty()) {
        return rtspUrl;
    }
    std::string out = rtspUrlPattern;
    auto pos = out.find("%d");
    if (pos != std::string::npos) {
        out.replace(pos, 2, std::to_string(index));
    }
    return out;
}
