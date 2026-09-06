#pragma once

#include <QString>
#include <string>

struct AppConfig {
    std::string rtspUrl = "rtsp://127.0.0.1:8554/live";
    std::string rtspUrlPattern;
    int streamCount = 30;
    std::string logPath = "/var/log/benchmark/fps_metrics.log";
    std::string machineId = "c7i-8xlarge-node-1";
    int targetFps = 25;
    int renderFps = 30; // UI display refresh rate
    int renderWidth = 640; // 640x360 default tile resolution prevents UI bus saturation
    int renderHeight = 360;

    static AppConfig loadFromArgsAndEnv(int argc, char* argv[]);
    static std::string resolveLogPath(const std::string& preferredPath);
    std::string urlForStream(int index) const;
};
