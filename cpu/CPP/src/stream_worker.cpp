#include "stream_worker.h"

#include <iostream>
#include <chrono>
#include <cstring>

static int ffmpeg_interrupt_callback(void* opaque) {
    auto* worker = static_cast<StreamWorker*>(opaque);
    return (worker && worker->isInterrupted()) ? 1 : 0;
}

StreamWorker::StreamWorker(int streamId, const std::string& rtspUrl, int targetWidth, int targetHeight, QObject* parent)
    : QThread(parent)
    , m_streamId(streamId)
    , m_rtspUrl(rtspUrl)
    , m_targetWidth(targetWidth)
    , m_targetHeight(targetHeight)
{
    setObjectName(QString("StreamWorker-%1").arg(streamId));
}

StreamWorker::~StreamWorker() {
    stopWorker();
    freeBuffers();
    freeSwsContext();
}

void StreamWorker::stopWorker() {
    m_stopRequested.store(true, std::memory_order_release);
    requestInterruption();
    if (isRunning()) {
        wait(2000);
        if (isRunning()) {
            terminate();
            wait(500);
        }
    }
}

bool StreamWorker::isInterrupted() const {
    return m_stopRequested.load(std::memory_order_relaxed) || isInterruptionRequested();
}

void StreamWorker::ensureBuffers(int width, int height) {
    int dstW = (m_targetWidth > 0) ? m_targetWidth : width;
    int dstH = (m_targetHeight > 0) ? m_targetHeight : height;
    size_t required = (size_t)dstW * dstH * 4;
    if (required > m_bufferCapacity || !m_buffers[0]) {
        freeBuffers();
        for (int i = 0; i < 3; ++i) {
            m_buffers[i] = static_cast<uint8_t*>(av_malloc(required));
            if (m_buffers[i]) {
                std::memset(m_buffers[i], 0, required);
            }
        }
        m_bufferCapacity = required;
        m_width.store(dstW, std::memory_order_release);
        m_height.store(dstH, std::memory_order_release);
    }
}

void StreamWorker::freeBuffers() {
    for (int i = 0; i < 3; ++i) {
        if (m_buffers[i]) {
            av_free(m_buffers[i]);
            m_buffers[i] = nullptr;
        }
    }
    m_bufferCapacity = 0;
}

void StreamWorker::ensureSwsContext(int width, int height, int format) {
    int dstW = (m_targetWidth > 0) ? m_targetWidth : width;
    int dstH = (m_targetHeight > 0) ? m_targetHeight : height;
    if (!m_swsCtx || m_swsWidth != width || m_swsHeight != height || m_swsFormat != format) {
        freeSwsContext();
        m_swsCtx = sws_getContext(
            width, height, static_cast<AVPixelFormat>(format),
            dstW, dstH, AV_PIX_FMT_RGB32,
            SWS_FAST_BILINEAR, nullptr, nullptr, nullptr
        );
        m_swsWidth = width;
        m_swsHeight = height;
        m_swsFormat = format;
    }
}

void StreamWorker::freeSwsContext() {
    if (m_swsCtx) {
        sws_freeContext(m_swsCtx);
        m_swsCtx = nullptr;
    }
    m_swsWidth = 0;
    m_swsHeight = 0;
    m_swsFormat = AV_PIX_FMT_NONE;
}

void StreamWorker::recordPresentedFrame(int64_t pts) {
    auto now = std::chrono::steady_clock::now();
    auto nowNs = std::chrono::duration_cast<std::chrono::nanoseconds>(now.time_since_epoch()).count();
    int64_t prevNs = m_lastPresentedTimestampNs.exchange(nowNs, std::memory_order_acq_rel);
    if (prevNs > 0) {
        float deltaMs = static_cast<float>(nowNs - prevNs) / 1000000.0f;
        m_lastDeltaMs.store(deltaMs, std::memory_order_relaxed);
    }
    m_paintedFrames.fetch_add(1, std::memory_order_relaxed);
}

uint8_t* StreamWorker::acquireFrame(int& outWidth, int& outHeight) {
    if (m_hasNewFrame.exchange(false, std::memory_order_acq_rel)) {
        m_consumerIndex = m_sharedIndex.exchange(m_consumerIndex, std::memory_order_acq_rel);
        recordPresentedFrame(m_currentPts.load(std::memory_order_relaxed));
    }
    outWidth = m_width.load(std::memory_order_acquire);
    outHeight = m_height.load(std::memory_order_acquire);
    return m_buffers[m_consumerIndex];
}

void StreamWorker::run() {
    AVPacket* pkt = av_packet_alloc();
    AVFrame* frame = av_frame_alloc();

    while (!isInterrupted()) {
        AVFormatContext* fmtCtx = avformat_alloc_context();
        if (!fmtCtx) {
            msleep(500);
            continue;
        }

        fmtCtx->interrupt_callback.callback = ffmpeg_interrupt_callback;
        fmtCtx->interrupt_callback.opaque = this;

        AVDictionary* opts = nullptr;
        av_dict_set(&opts, "rtsp_transport", "tcp", 0);
        av_dict_set(&opts, "stimeout", "5000000", 0);      // 5 sec timeout (in us)
        av_dict_set(&opts, "max_delay", "500000", 0);       // 500 ms max delay (in us)
        av_dict_set(&opts, "buffer_size", "4194304", 0);    // 4MB socket buffer

        int ret = avformat_open_input(&fmtCtx, m_rtspUrl.c_str(), nullptr, &opts);
        av_dict_free(&opts);

        if (ret < 0) {
            m_isConnected.store(false, std::memory_order_release);
            avformat_close_input(&fmtCtx);
            if (!isInterrupted()) {
                msleep(3000);
            }
            continue;
        }

        if (avformat_find_stream_info(fmtCtx, nullptr) < 0) {
            m_isConnected.store(false, std::memory_order_release);
            avformat_close_input(&fmtCtx);
            if (!isInterrupted()) {
                msleep(3000);
            }
            continue;
        }

        int videoStreamIdx = av_find_best_stream(fmtCtx, AVMEDIA_TYPE_VIDEO, -1, -1, nullptr, 0);
        if (videoStreamIdx < 0) {
            m_isConnected.store(false, std::memory_order_release);
            avformat_close_input(&fmtCtx);
            if (!isInterrupted()) {
                msleep(3000);
            }
            continue;
        }

        AVCodecParameters* codecPar = fmtCtx->streams[videoStreamIdx]->codecpar;
        const AVCodec* codec = avcodec_find_decoder(codecPar->codec_id);
        if (!codec) {
            std::cerr << "[Stream " << m_streamId << "] Codec not found for ID: " << codecPar->codec_id << std::endl;
            m_isConnected.store(false, std::memory_order_release);
            avformat_close_input(&fmtCtx);
            if (!isInterrupted()) {
                msleep(3000);
            }
            continue;
        }

        AVCodecContext* codecCtx = avcodec_alloc_context3(codec);
        if (!codecCtx) {
            m_isConnected.store(false, std::memory_order_release);
            avformat_close_input(&fmtCtx);
            if (!isInterrupted()) {
                msleep(3000);
            }
            continue;
        }

        if (avcodec_parameters_to_context(codecCtx, codecPar) < 0) {
            m_isConnected.store(false, std::memory_order_release);
            avcodec_free_context(&codecCtx);
            avformat_close_input(&fmtCtx);
            if (!isInterrupted()) {
                msleep(3000);
            }
            continue;
        }

        // Software decoding constraints & optimizations
        codecCtx->thread_count = 1; // Dedicated QThread per stream worker
        codecCtx->flags |= AV_CODEC_FLAG_LOW_DELAY;
        codecCtx->flags2 |= AV_CODEC_FLAG2_FAST;

        if (avcodec_open2(codecCtx, codec, nullptr) < 0) {
            m_isConnected.store(false, std::memory_order_release);
            avcodec_free_context(&codecCtx);
            avformat_close_input(&fmtCtx);
            if (!isInterrupted()) {
                msleep(3000);
            }
            continue;
        }

        m_isConnected.store(true, std::memory_order_release);

        // Demuxing and decoding loop
        while (!isInterrupted()) {
            ret = av_read_frame(fmtCtx, pkt);
            if (ret < 0) {
                // Connection lost or EOF; break to reconnect
                break;
            }

            if (pkt->stream_index == videoStreamIdx) {
                int sendRet = avcodec_send_packet(codecCtx, pkt);
                av_packet_unref(pkt);

                if (sendRet < 0) {
                    continue;
                }

                while (avcodec_receive_frame(codecCtx, frame) == 0) {
                    int w = frame->width;
                    int h = frame->height;
                    if (w > 0 && h > 0) {
                        m_sourceWidth.store(w, std::memory_order_relaxed);
                        m_sourceHeight.store(h, std::memory_order_relaxed);
                        ensureBuffers(w, h);
                        ensureSwsContext(w, h, frame->format);

                        if (m_buffers[m_producerIndex] && m_swsCtx) {
                            int dstW = (m_targetWidth > 0) ? m_targetWidth : w;
                            uint8_t* dstData[4] = { m_buffers[m_producerIndex], nullptr, nullptr, nullptr };
                            int dstLinesize[4] = { dstW * 4, 0, 0, 0 };

                            sws_scale(
                                m_swsCtx,
                                frame->data,
                                frame->linesize,
                                0,
                                h,
                                dstData,
                                dstLinesize
                            );

                            // Lock-free triple buffer publish
                            int64_t pts = (frame->pts != AV_NOPTS_VALUE) ? frame->pts : frame->best_effort_timestamp;
                            m_currentPts.store(pts, std::memory_order_release);
                            m_producerIndex = m_sharedIndex.exchange(m_producerIndex, std::memory_order_acq_rel);
                            m_hasNewFrame.store(true, std::memory_order_release);
                            m_decodedFrames.fetch_add(1, std::memory_order_relaxed);
                        }
                    }
                    av_frame_unref(frame);
                }
            } else {
                av_packet_unref(pkt);
            }
        }

        m_isConnected.store(false, std::memory_order_release);
        avcodec_free_context(&codecCtx);
        avformat_close_input(&fmtCtx);
        freeSwsContext();

        if (!isInterrupted()) {
            msleep(3000); // 3-second backoff before reconnecting per Master Spec
        }
    }

    av_frame_free(&frame);
    av_packet_free(&pkt);
}
