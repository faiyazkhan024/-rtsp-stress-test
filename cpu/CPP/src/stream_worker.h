#pragma once

#include <QThread>
#include <atomic>
#include <string>
#include <cstdint>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libswscale/swscale.h>
#include <libavutil/avutil.h>
#include <libavutil/imgutils.h>
}

class StreamWorker : public QThread {
    Q_OBJECT

public:
    StreamWorker(int streamId, const std::string& rtspUrl, int targetWidth = 640, int targetHeight = 360, QObject* parent = nullptr);
    ~StreamWorker() override;

    void stopWorker();
    bool isInterrupted() const;

    // Wait-free lock-free buffer acquisition for UI thread rendering
    uint8_t* acquireFrame(int& outWidth, int& outHeight);

    int streamId() const { return m_streamId; }
    bool isConnected() const { return m_isConnected.load(std::memory_order_relaxed); }
    uint64_t decodedFrames() const { return m_decodedFrames.load(std::memory_order_relaxed); }
    uint64_t paintedFrames() const { return m_paintedFrames.load(std::memory_order_relaxed); }
    int64_t currentPts() const { return m_currentPts.load(std::memory_order_relaxed); }
    float lastDeltaMs() const { return m_lastDeltaMs.load(std::memory_order_relaxed); }
    void incrementPaintedFrames() { recordPresentedFrame(m_currentPts.load(std::memory_order_relaxed)); }
    void recordPresentedFrame(int64_t pts);

    float currentFps() const { return m_currentFps.load(std::memory_order_relaxed); }
    void setCurrentFps(float fps) { m_currentFps.store(fps, std::memory_order_relaxed); }
    bool hasNewFrame() const { return m_hasNewFrame.load(std::memory_order_relaxed); }

protected:
    void run() override;

private:
    void ensureBuffers(int width, int height);
    void freeBuffers();
    void ensureSwsContext(int width, int height, int format);
    void freeSwsContext();

    int m_streamId;
    std::string m_rtspUrl;
    int m_targetWidth = 640;
    int m_targetHeight = 360;
    std::atomic<bool> m_stopRequested{false};
    std::atomic<bool> m_isConnected{false};

    // Pre-allocated triple buffer for RGB32 video frames
    uint8_t* m_buffers[3] = {nullptr, nullptr, nullptr};
    size_t m_bufferCapacity = 0;
    int m_producerIndex = 0;
    int m_consumerIndex = 1;
    std::atomic<int> m_sharedIndex{2};
    std::atomic<bool> m_hasNewFrame{false};

    std::atomic<int> m_width{0};
    std::atomic<int> m_height{0};

    // Metrics
    std::atomic<uint64_t> m_decodedFrames{0};
    std::atomic<uint64_t> m_paintedFrames{0};
    std::atomic<int64_t> m_currentPts{-1};
    std::atomic<int64_t> m_lastPresentedTimestampNs{0};
    std::atomic<float> m_lastDeltaMs{0.0f};
    std::atomic<float> m_currentFps{0.0f};

    // FFmpeg state
    SwsContext* m_swsCtx = nullptr;
    int m_swsWidth = 0;
    int m_swsHeight = 0;
    int m_swsFormat = AV_PIX_FMT_NONE;
};
