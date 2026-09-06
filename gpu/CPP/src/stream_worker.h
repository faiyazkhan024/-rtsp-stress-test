#pragma once

#include <QThread>
#include <atomic>
#include <string>
#include <memory>
#include <cstdint>
#include "hw_accel.h"

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavcodec/bsf.h>
#include <libavformat/avformat.h>
#include <libavutil/avutil.h>
#include <libavutil/pixdesc.h>
#include <libswscale/swscale.h>
}

class StreamWorker : public QThread {
    Q_OBJECT

public:
    StreamWorker(int streamId, const std::string& rtspUrl,
                 std::shared_ptr<HwAccelManager> hwAccel,
                 QObject* parent = nullptr);
    ~StreamWorker() override;

    void stopWorker();
    bool isInterrupted() const;

    bool hasNewFrame() const { return m_hasNewFrame.load(std::memory_order_relaxed); }
    // Lock-free acquisition of hardware frame handle for UI rendering
    AVFrame* acquireFrame(bool* outIsNew = nullptr);

    int streamId() const { return m_streamId; }
    bool isConnected() const { return m_isConnected.load(std::memory_order_relaxed); }
    bool isHwAccelerated() const { return m_isHwAccelerated.load(std::memory_order_relaxed); }
    std::string hwDeviceName() const { return m_hwDeviceName; }

    uint64_t decodedFrames() const { return m_decodedFrames.load(std::memory_order_relaxed); }
    uint64_t paintedFrames() const { return m_paintedFrames.load(std::memory_order_relaxed); }
    int64_t currentPts() const { return m_currentPts.load(std::memory_order_relaxed); }
    float lastDeltaMs() const { return m_lastDeltaMs.load(std::memory_order_relaxed); }
    void incrementPaintedFrames() { recordPresentedFrame(m_currentPts.load(std::memory_order_relaxed)); }
    void recordPresentedFrame(int64_t pts);

    float currentFps() const { return m_currentFps.load(std::memory_order_relaxed); }
    void setCurrentFps(float fps) { m_currentFps.store(fps, std::memory_order_relaxed); }

    int frameWidth() const { return m_width.load(std::memory_order_relaxed); }
    int frameHeight() const { return m_height.load(std::memory_order_relaxed); }

protected:
    void run() override;

private:
    int m_streamId;
    std::string m_rtspUrl;
    std::shared_ptr<HwAccelManager> m_hwAccel;
    std::string m_hwDeviceName = "CPU";

    std::atomic<bool> m_stopRequested{false};
    std::atomic<bool> m_isConnected{false};
    std::atomic<bool> m_isHwAccelerated{false};

    // Lock-free frame pointer exchange for zero-copy handoff
    std::atomic<AVFrame*> m_sharedFrame{nullptr};
    std::atomic<bool> m_hasNewFrame{false};
    AVFrame* m_consumedFrame = nullptr;

    std::atomic<int> m_width{0};
    std::atomic<int> m_height{0};

    // Metrics
    std::atomic<uint64_t> m_decodedFrames{0};
    std::atomic<uint64_t> m_paintedFrames{0};
    std::atomic<int64_t> m_currentPts{-1};
    std::atomic<int64_t> m_lastPresentedTimestampNs{0};
    std::atomic<float> m_lastDeltaMs{0.0f};
    std::atomic<float> m_currentFps{0.0f};
};
