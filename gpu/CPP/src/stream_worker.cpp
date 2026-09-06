#include "stream_worker.h"

#include <iostream>
#include <chrono>

static int ffmpeg_interrupt_callback(void* opaque) {
    auto* worker = static_cast<StreamWorker*>(opaque);
    return (worker && worker->isInterrupted()) ? 1 : 0;
}

StreamWorker::StreamWorker(int streamId, const std::string& rtspUrl,
                           std::shared_ptr<HwAccelManager> hwAccel,
                           QObject* parent)
    : QThread(parent)
    , m_streamId(streamId)
    , m_rtspUrl(rtspUrl)
    , m_hwAccel(hwAccel)
{
    setObjectName(QString("StreamWorker-%1").arg(streamId));
    if (m_hwAccel && m_hwAccel->isInitialized()) {
        m_hwDeviceName = m_hwAccel->deviceName();
    }
}

StreamWorker::~StreamWorker() {
    stopWorker();
    if (m_consumedFrame) {
        av_frame_free(&m_consumedFrame);
        m_consumedFrame = nullptr;
    }
    AVFrame* shared = m_sharedFrame.exchange(nullptr);
    if (shared) {
        av_frame_free(&shared);
    }
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

void StreamWorker::recordPresentedFrame(int64_t pts) {
    if (pts != -1) {
        m_currentPts.store(pts, std::memory_order_relaxed);
    }
    auto now = std::chrono::steady_clock::now();
    auto nowNs = std::chrono::duration_cast<std::chrono::nanoseconds>(now.time_since_epoch()).count();
    int64_t prevNs = m_lastPresentedTimestampNs.exchange(nowNs, std::memory_order_acq_rel);
    if (prevNs > 0) {
        float deltaMs = static_cast<float>(nowNs - prevNs) / 1000000.0f;
        m_lastDeltaMs.store(deltaMs, std::memory_order_relaxed);
    }
    m_paintedFrames.fetch_add(1, std::memory_order_relaxed);
}

AVFrame* StreamWorker::acquireFrame(bool* outIsNew) {
    bool isNew = m_hasNewFrame.exchange(false, std::memory_order_acq_rel);
    if (isNew) {
        AVFrame* newFrame = m_sharedFrame.exchange(nullptr, std::memory_order_acq_rel);
        if (newFrame) {
            if (m_consumedFrame) {
                av_frame_free(&m_consumedFrame);
            }
            m_consumedFrame = newFrame;
            recordPresentedFrame(m_consumedFrame->pts);
        }
    }
    if (outIsNew) {
        *outIsNew = isNew;
    }
    return m_consumedFrame;
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
        av_dict_set(&opts, "stimeout", "5000000", 0);       // 5 sec timeout (in us)
        av_dict_set(&opts, "max_delay", "500000", 0);        // 500 ms max delay (in us)
        av_dict_set(&opts, "buffer_size", "4194304", 0);     // 4MB socket buffer

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

        // Attach GPU hardware acceleration
        if (m_hwAccel && m_hwAccel->isInitialized()) {
            AVBufferRef* hwRef = m_hwAccel->createDeviceRef();
            if (hwRef) {
                codecCtx->hw_device_ctx = hwRef;
                codecCtx->opaque = m_hwAccel.get();
                codecCtx->get_format = HwAccelManager::getHwFormat;
                m_hwDeviceName = m_hwAccel->deviceName();
            }
        }

        codecCtx->thread_count = 1;
        codecCtx->flags |= AV_CODEC_FLAG_LOW_DELAY;
        codecCtx->flags2 |= AV_CODEC_FLAG2_FAST;

        if (avcodec_open2(codecCtx, codec, nullptr) < 0) {
            std::cerr << "[Stream " << m_streamId << "] Failed to open codec context with hwaccel." << std::endl;
            m_isConnected.store(false, std::memory_order_release);
            avcodec_free_context(&codecCtx);
            avformat_close_input(&fmtCtx);
            if (!isInterrupted()) {
                msleep(3000);
            }
            continue;
        }

        // Bitstream filter to ensure Annex B start codes if extradata exists
        const AVBitStreamFilter* bsf = (codecPar->extradata_size > 0) ? av_bsf_get_by_name("h264_mp4toannexb") : nullptr;
        AVBSFContext* bsfCtx = nullptr;
        if (bsf) {
            if (av_bsf_alloc(bsf, &bsfCtx) == 0) {
                avcodec_parameters_copy(bsfCtx->par_in, codecPar);
                if (av_bsf_init(bsfCtx) < 0) {
                    av_bsf_free(&bsfCtx);
                    bsfCtx = nullptr;
                }
            }
        }
        AVPacket* filteredPkt = av_packet_alloc();

        m_isConnected.store(true, std::memory_order_release);
        struct SwsContext* swsCtx = nullptr;

        auto processDecodedFrame = [&](AVFrame* inFrame) {
            int w = inFrame->width;
            int h = inFrame->height;
            if (w <= 0 || h <= 0) {
                return;
            }

            m_width.store(w, std::memory_order_relaxed);
            m_height.store(h, std::memory_order_relaxed);
            m_currentPts.store(inFrame->pts, std::memory_order_relaxed);

            AVFrame* publishFrame = nullptr;

            if (m_hwAccel && m_hwAccel->isInitialized() && inFrame->format == m_hwAccel->hwPixFormat()) {
                m_isHwAccelerated.store(true, std::memory_order_relaxed);
                AVFrame* swFrame = av_frame_alloc();
                if (swFrame) {
                    if (av_hwframe_transfer_data(swFrame, inFrame, 0) == 0) {
                        swFrame->pts = inFrame->pts;

                        const int targetW = 320;
                        const int targetH = 180;
                        if (swFrame->width > targetW && swFrame->height > targetH) {
                            swsCtx = sws_getCachedContext(swsCtx,
                                                          swFrame->width, swFrame->height, (AVPixelFormat)swFrame->format,
                                                          targetW, targetH, (AVPixelFormat)swFrame->format,
                                                          SWS_FAST_BILINEAR, nullptr, nullptr, nullptr);
                            if (swsCtx) {
                                AVFrame* scaledFrame = av_frame_alloc();
                                if (scaledFrame) {
                                    scaledFrame->format = swFrame->format;
                                    scaledFrame->width = targetW;
                                    scaledFrame->height = targetH;
                                    scaledFrame->pts = swFrame->pts;
                                    if (av_frame_get_buffer(scaledFrame, 32) == 0) {
                                        sws_scale(swsCtx, swFrame->data, swFrame->linesize, 0, swFrame->height,
                                                  scaledFrame->data, scaledFrame->linesize);
                                        publishFrame = scaledFrame;
                                        av_frame_free(&swFrame);
                                    } else {
                                        av_frame_free(&scaledFrame);
                                        publishFrame = swFrame;
                                    }
                                } else {
                                    publishFrame = swFrame;
                                }
                            } else {
                                publishFrame = swFrame;
                            }
                        } else {
                            publishFrame = swFrame;
                        }
                    } else {
                        av_frame_free(&swFrame);
                    }
                }
            } else {
                const int targetW = 320;
                const int targetH = 180;
                if (inFrame->width > targetW && inFrame->height > targetH) {
                    swsCtx = sws_getCachedContext(swsCtx,
                                                  inFrame->width, inFrame->height, (AVPixelFormat)inFrame->format,
                                                  targetW, targetH, (AVPixelFormat)inFrame->format,
                                                  SWS_FAST_BILINEAR, nullptr, nullptr, nullptr);
                    if (swsCtx) {
                        AVFrame* scaledFrame = av_frame_alloc();
                        if (scaledFrame) {
                            scaledFrame->format = inFrame->format;
                            scaledFrame->width = targetW;
                            scaledFrame->height = targetH;
                            scaledFrame->pts = inFrame->pts;
                            if (av_frame_get_buffer(scaledFrame, 32) == 0) {
                                sws_scale(swsCtx, inFrame->data, inFrame->linesize, 0, inFrame->height,
                                          scaledFrame->data, scaledFrame->linesize);
                                publishFrame = scaledFrame;
                            } else {
                                av_frame_free(&scaledFrame);
                                publishFrame = av_frame_clone(inFrame);
                            }
                        } else {
                            publishFrame = av_frame_clone(inFrame);
                        }
                    } else {
                        publishFrame = av_frame_clone(inFrame);
                    }
                } else {
                    publishFrame = av_frame_clone(inFrame);
                }
            }

            if (publishFrame) {
                AVFrame* old = m_sharedFrame.exchange(publishFrame, std::memory_order_acq_rel);
                if (old) {
                    av_frame_free(&old);
                }
                m_hasNewFrame.store(true, std::memory_order_release);
                m_decodedFrames.fetch_add(1, std::memory_order_relaxed);
            }
        };

        // Demuxing and decoding loop
        while (!isInterrupted()) {
            ret = av_read_frame(fmtCtx, pkt);
            if (ret < 0) {
                break;
            }

            if (pkt->stream_index == videoStreamIdx) {
                if (bsfCtx) {
                    if (av_bsf_send_packet(bsfCtx, pkt) == 0) {
                        while (av_bsf_receive_packet(bsfCtx, filteredPkt) == 0) {
                            if (avcodec_send_packet(codecCtx, filteredPkt) >= 0) {
                                while (avcodec_receive_frame(codecCtx, frame) == 0) {
                                    processDecodedFrame(frame);
                                    av_frame_unref(frame);
                                }
                            }
                            av_packet_unref(filteredPkt);
                        }
                    }
                } else {
                    if (avcodec_send_packet(codecCtx, pkt) >= 0) {
                        while (avcodec_receive_frame(codecCtx, frame) == 0) {
                            processDecodedFrame(frame);
                            av_frame_unref(frame);
                        }
                    }
                }
                av_packet_unref(pkt);
            } else {
                av_packet_unref(pkt);
            }
        }

        if (bsfCtx) {
            av_bsf_free(&bsfCtx);
        }
        av_packet_free(&filteredPkt);

        m_isConnected.store(false, std::memory_order_release);
        if (swsCtx) {
            sws_freeContext(swsCtx);
            swsCtx = nullptr;
        }
        avcodec_free_context(&codecCtx);
        avformat_close_input(&fmtCtx);
        AVFrame* stale = m_sharedFrame.exchange(nullptr, std::memory_order_acq_rel);
        if (stale) {
            av_frame_free(&stale);
        }

        if (!isInterrupted()) {
            msleep(3000);
        }
    }

    av_frame_free(&frame);
    av_packet_free(&pkt);
}
