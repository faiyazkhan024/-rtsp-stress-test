#include "video_widget.h"
#include <QPainter>
#include <QFont>
#include <QFontMetrics>
#include <iostream>

#if defined(__APPLE__)
#include <CoreVideo/CoreVideo.h>
#endif

#ifndef GL_RED
#define GL_RED 0x1903
#endif
#ifndef GL_RG
#define GL_RG 0x8227
#endif
#ifndef GL_R8
#define GL_R8 0x8229
#endif
#ifndef GL_RG8
#define GL_RG8 0x822B
#endif
#ifndef GL_UNPACK_ROW_LENGTH
#define GL_UNPACK_ROW_LENGTH 0x0CF2
#endif

VideoWidget::VideoWidget(int streamId, StreamWorker* worker, QWidget* parent)
    : QOpenGLWidget(parent)
    , m_streamId(streamId)
    , m_worker(worker)
    , m_vbo(QOpenGLBuffer::VertexBuffer)
{
    setSizePolicy(QSizePolicy::Expanding, QSizePolicy::Expanding);
    setMinimumSize(160, 90);
}

VideoWidget::~VideoWidget() {
    makeCurrent();
    if (m_texY) glDeleteTextures(1, &m_texY);
    if (m_texU) glDeleteTextures(1, &m_texU);
    if (m_texV) glDeleteTextures(1, &m_texV);
    if (m_texUV) glDeleteTextures(1, &m_texUV);
    if (m_texRGBA) glDeleteTextures(1, &m_texRGBA);
    m_vbo.destroy();
    m_vao.destroy();
    doneCurrent();
}

void VideoWidget::initializeGL() {
    initializeOpenGLFunctions();

    // 1. Setup Fullscreen Quad Geometry
    GLfloat vertices[] = {
        // Position     // TexCoord
        -1.0f, -1.0f,   0.0f, 1.0f,
         1.0f, -1.0f,   1.0f, 1.0f,
         1.0f,  1.0f,   1.0f, 0.0f,
        -1.0f, -1.0f,   0.0f, 1.0f,
         1.0f,  1.0f,   1.0f, 0.0f,
        -1.0f,  1.0f,   0.0f, 0.0f
    };

    m_vao.create();
    m_vao.bind();

    m_vbo.create();
    m_vbo.bind();
    m_vbo.allocate(vertices, sizeof(vertices));

    glEnableVertexAttribArray(0);
    glVertexAttribPointer(0, 2, GL_FLOAT, GL_FALSE, 4 * sizeof(GLfloat), reinterpret_cast<void*>(0));

    glEnableVertexAttribArray(1);
    glVertexAttribPointer(1, 2, GL_FLOAT, GL_FALSE, 4 * sizeof(GLfloat), reinterpret_cast<void*>(2 * sizeof(GLfloat)));

    m_vbo.release();
    m_vao.release();

    // 2. Setup Shaders
    m_shaderNv12.init(this, VideoShaderType::NV12);
    m_shaderYuv420p.init(this, VideoShaderType::YUV420P);
    m_shaderRgba.init(this, VideoShaderType::RGBA);

    // 3. Setup Textures
    setupTextures();

    m_glInitialized = true;
}

void VideoWidget::setupTextures() {
    auto initTex = [this](GLuint& tex) {
        glGenTextures(1, &tex);
        glBindTexture(GL_TEXTURE_2D, tex);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        glBindTexture(GL_TEXTURE_2D, 0);
    };

    initTex(m_texY);
    initTex(m_texU);
    initTex(m_texV);
    initTex(m_texUV);
    initTex(m_texRGBA);
}

void VideoWidget::resizeGL(int w, int h) {
    glViewport(0, 0, w, h);
}

void VideoWidget::paintGL() {
    glClearColor(0.05f, 0.07f, 0.09f, 1.0f);
    glClear(GL_COLOR_BUFFER_BIT);

    bool isNew = false;
    AVFrame* frame = m_worker ? m_worker->acquireFrame(&isNew) : nullptr;

    if (frame && frame->width > 0 && frame->height > 0) {
        renderFrame(frame, isNew);
    }

    float fps = m_worker ? m_worker->currentFps() : 0.0f;
    bool isConnected = m_worker && m_worker->isConnected();
    bool isHw = m_worker && m_worker->isHwAccelerated();
    int frameW = m_texWidth;
    int frameH = m_texHeight;

    bool hudDirty = m_hudCache.isNull() ||
                    m_hudCache.size() != size() ||
                    std::abs(fps - m_cachedFps) > 0.4f ||
                    isConnected != m_cachedConnected ||
                    frameW != m_cachedWidth ||
                    frameH != m_cachedHeight;

    if (hudDirty) {
        m_cachedFps = fps;
        m_cachedConnected = isConnected;
        m_cachedWidth = frameW;
        m_cachedHeight = frameH;

        if (m_hudCache.size() != size()) {
            m_hudCache = QPixmap(size());
        }
        m_hudCache.fill(Qt::transparent);

        QPainter cachePainter(&m_hudCache);
        cachePainter.setRenderHint(QPainter::Antialiasing, true);
        cachePainter.setRenderHint(QPainter::TextAntialiasing, true);
        drawHudOverlay(cachePainter, frameW, frameH, fps, isConnected, isHw);
        cachePainter.end();
    }

    QPainter painter(this);
    painter.drawPixmap(0, 0, m_hudCache);
    painter.end();
}

void VideoWidget::renderFrame(AVFrame* frame, bool uploadTexture) {
    int w = frame->width;
    int h = frame->height;

    // Check if texture dimensions need reallocating
    bool needRealloc = (m_texWidth != w || m_texHeight != h);
    if (needRealloc) {
        m_texWidth = w;
        m_texHeight = h;
    }

#if defined(__APPLE__)
    if (frame->format == AV_PIX_FMT_VIDEOTOOLBOX && frame->data[3]) {
        auto pixbuf = reinterpret_cast<CVPixelBufferRef>(frame->data[3]);
        if (CVPixelBufferLockBaseAddress(pixbuf, kCVPixelBufferLock_ReadOnly) == kCVReturnSuccess) {
            void* yPlane = CVPixelBufferGetBaseAddressOfPlane(pixbuf, 0);
            void* uvPlane = CVPixelBufferGetBaseAddressOfPlane(pixbuf, 1);
            size_t yStride = CVPixelBufferGetBytesPerRowOfPlane(pixbuf, 0);
            size_t uvStride = CVPixelBufferGetBytesPerRowOfPlane(pixbuf, 1);

            glActiveTexture(GL_TEXTURE0);
            glBindTexture(GL_TEXTURE_2D, m_texY);
            if (uploadTexture || needRealloc) {
                glPixelStorei(GL_UNPACK_ALIGNMENT, 1);
                glPixelStorei(GL_UNPACK_ROW_LENGTH, static_cast<GLint>(yStride));
                if (needRealloc) {
                    glTexImage2D(GL_TEXTURE_2D, 0, GL_R8, w, h, 0, GL_RED, GL_UNSIGNED_BYTE, yPlane);
                } else {
                    glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w, h, GL_RED, GL_UNSIGNED_BYTE, yPlane);
                }
            }

            glActiveTexture(GL_TEXTURE1);
            glBindTexture(GL_TEXTURE_2D, m_texUV);
            if (uploadTexture || needRealloc) {
                glPixelStorei(GL_UNPACK_ROW_LENGTH, static_cast<GLint>(uvStride / 2));
                if (needRealloc) {
                    glTexImage2D(GL_TEXTURE_2D, 0, GL_RG8, w / 2, h / 2, 0, GL_RG, GL_UNSIGNED_BYTE, uvPlane);
                } else {
                    glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w / 2, h / 2, GL_RG, GL_UNSIGNED_BYTE, uvPlane);
                }
                glPixelStorei(GL_UNPACK_ALIGNMENT, 4);
                glPixelStorei(GL_UNPACK_ROW_LENGTH, 0);
            }

            CVPixelBufferUnlockBaseAddress(pixbuf, kCVPixelBufferLock_ReadOnly);

            // Render NV12 Quad with BT.709 GPU shader
            m_shaderNv12.bind();
            m_shaderNv12.setTextureUnits(0, 1);

            m_vao.bind();
            glDrawArrays(GL_TRIANGLES, 0, 6);
            m_vao.release();

            m_shaderNv12.release();

            glActiveTexture(GL_TEXTURE1);
            glBindTexture(GL_TEXTURE_2D, 0);
            glActiveTexture(GL_TEXTURE0);
            glBindTexture(GL_TEXTURE_2D, 0);
            return;
        }
    }
#endif

    if (frame->format == AV_PIX_FMT_CUDA || frame->format == AV_PIX_FMT_D3D11 || frame->format == AV_PIX_FMT_VAAPI) {
        return;
    }

    // Tri-planar YUV420P (hardware or fallback planar YUV)
    if (frame->format == AV_PIX_FMT_YUV420P || (frame->data[0] && frame->data[1] && frame->data[2])) {
        glActiveTexture(GL_TEXTURE0);
        glBindTexture(GL_TEXTURE_2D, m_texY);
        if (uploadTexture || needRealloc) {
            glPixelStorei(GL_UNPACK_ALIGNMENT, 1);
            glPixelStorei(GL_UNPACK_ROW_LENGTH, frame->linesize[0]);
            if (needRealloc) {
                glTexImage2D(GL_TEXTURE_2D, 0, GL_R8, w, h, 0, GL_RED, GL_UNSIGNED_BYTE, frame->data[0]);
            } else {
                glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w, h, GL_RED, GL_UNSIGNED_BYTE, frame->data[0]);
            }
        }

        glActiveTexture(GL_TEXTURE1);
        glBindTexture(GL_TEXTURE_2D, m_texU);
        if (uploadTexture || needRealloc) {
            glPixelStorei(GL_UNPACK_ROW_LENGTH, frame->linesize[1]);
            if (needRealloc) {
                glTexImage2D(GL_TEXTURE_2D, 0, GL_R8, w / 2, h / 2, 0, GL_RED, GL_UNSIGNED_BYTE, frame->data[1]);
            } else {
                glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w / 2, h / 2, GL_RED, GL_UNSIGNED_BYTE, frame->data[1]);
            }
        }

        glActiveTexture(GL_TEXTURE2);
        glBindTexture(GL_TEXTURE_2D, m_texV);
        if (uploadTexture || needRealloc) {
            glPixelStorei(GL_UNPACK_ROW_LENGTH, frame->linesize[2]);
            if (needRealloc) {
                glTexImage2D(GL_TEXTURE_2D, 0, GL_R8, w / 2, h / 2, 0, GL_RED, GL_UNSIGNED_BYTE, frame->data[2]);
            } else {
                glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w / 2, h / 2, GL_RED, GL_UNSIGNED_BYTE, frame->data[2]);
            }
            glPixelStorei(GL_UNPACK_ALIGNMENT, 4);
            glPixelStorei(GL_UNPACK_ROW_LENGTH, 0);
        }

        m_shaderYuv420p.bind();
        m_shaderYuv420p.setTextureUnits(0, 1, 2);

        m_vao.bind();
        glDrawArrays(GL_TRIANGLES, 0, 6);
        m_vao.release();

        m_shaderYuv420p.release();

        glActiveTexture(GL_TEXTURE2);
        glBindTexture(GL_TEXTURE_2D, 0);
        glActiveTexture(GL_TEXTURE1);
        glBindTexture(GL_TEXTURE_2D, 0);
        glActiveTexture(GL_TEXTURE0);
        glBindTexture(GL_TEXTURE_2D, 0);
        return;
    }

    // Standard NV12 (hardware or direct VRAM plane)
    if (frame->format == AV_PIX_FMT_NV12 || (frame->data[0] && frame->data[1])) {
        glActiveTexture(GL_TEXTURE0);
        glBindTexture(GL_TEXTURE_2D, m_texY);
        if (uploadTexture || needRealloc) {
            glPixelStorei(GL_UNPACK_ALIGNMENT, 1);
            glPixelStorei(GL_UNPACK_ROW_LENGTH, frame->linesize[0]);
            if (needRealloc) {
                glTexImage2D(GL_TEXTURE_2D, 0, GL_R8, w, h, 0, GL_RED, GL_UNSIGNED_BYTE, frame->data[0]);
            } else {
                glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w, h, GL_RED, GL_UNSIGNED_BYTE, frame->data[0]);
            }
        }

        glActiveTexture(GL_TEXTURE1);
        glBindTexture(GL_TEXTURE_2D, m_texUV);
        if (uploadTexture || needRealloc) {
            glPixelStorei(GL_UNPACK_ROW_LENGTH, frame->linesize[1] / 2);
            if (needRealloc) {
                glTexImage2D(GL_TEXTURE_2D, 0, GL_RG8, w / 2, h / 2, 0, GL_RG, GL_UNSIGNED_BYTE, frame->data[1]);
            } else {
                glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w / 2, h / 2, GL_RG, GL_UNSIGNED_BYTE, frame->data[1]);
            }
            glPixelStorei(GL_UNPACK_ALIGNMENT, 4);
            glPixelStorei(GL_UNPACK_ROW_LENGTH, 0);
        }

        m_shaderNv12.bind();
        m_shaderNv12.setTextureUnits(0, 1);

        m_vao.bind();
        glDrawArrays(GL_TRIANGLES, 0, 6);
        m_vao.release();

        m_shaderNv12.release();

        glActiveTexture(GL_TEXTURE1);
        glBindTexture(GL_TEXTURE_2D, 0);
        glActiveTexture(GL_TEXTURE0);
        glBindTexture(GL_TEXTURE_2D, 0);
        return;
    }
}

void VideoWidget::drawHudOverlay(QPainter& painter, int frameWidth, int frameHeight, float fps, bool isConnected, bool isHw) {
    if (frameWidth <= 0 || frameHeight <= 0) {
        QFont font = painter.font();
        font.setStyleHint(QFont::SansSerif);
        font.setPixelSize(13);
        font.setBold(true);
        painter.setFont(font);

        painter.setPen(QColor("#94a3b8"));
        QString text = QString("CAM %1\nCONNECTING...")
                           .arg(m_streamId + 1, 2, 10, QChar('0'));
        painter.drawText(rect(), Qt::AlignCenter, text);
    }

    // Paint HUD Overlay Badge
    QColor fpsColor("#ef4444"); // Red: < 20 FPS
    if (fps >= 25.0f) {
        fpsColor = QColor("#22c55e"); // Green: >= 25 FPS
    } else if (fps >= 20.0f) {
        fpsColor = QColor("#eab308"); // Yellow: 20-24 FPS
    }

    QFont hudFont = painter.font();
    hudFont.setStyleHint(QFont::SansSerif);
    hudFont.setPixelSize(11);
    hudFont.setBold(true);
    painter.setFont(hudFont);

    QString camStr = QString("CAM %1").arg(m_streamId + 1, 2, 10, QChar('0'));
    QString resStr = (frameWidth > 0 && frameHeight > 0) ? QString("%1x%2").arg(frameWidth).arg(frameHeight) : QString("2560x1440");
    QString fpsStr = QString("%1 FPS").arg(fps, 0, 'f', 1);
    QString hwStr = isHw ? QString::fromStdString(m_worker->hwDeviceName()).toUpper() : "GPU";

    QString badgeText = QString("%1  |  %2  |  %3  |  %4").arg(camStr, resStr, fpsStr, hwStr);
    QFontMetrics fm(hudFont);
    int textWidth = fm.horizontalAdvance(badgeText);
    int badgeWidth = textWidth + 24;
    int badgeHeight = 22;

    QRect badgeRect(8, 8, badgeWidth, badgeHeight);
    painter.setPen(Qt::NoPen);
    painter.setBrush(QColor(15, 23, 42, 220));
    painter.drawRoundedRect(badgeRect, 4, 4);

    // Status Dot
    painter.setBrush(isConnected ? fpsColor : QColor("#ef4444"));
    painter.drawEllipse(badgeRect.x() + 7, badgeRect.y() + 7, 8, 8);

    // Badge Text with bounded vertical centering to avoid baseline clipping
    painter.setPen(QColor("#f8fafc"));
    QRect textRect(badgeRect.x() + 19, badgeRect.y(), badgeWidth - 21, badgeHeight);
    painter.drawText(textRect, Qt::AlignVCenter | Qt::AlignLeft, badgeText);

    // 1px Border
    painter.setPen(QColor(51, 65, 85, 200));
    painter.setBrush(Qt::NoBrush);
    painter.drawRect(0, 0, width() - 1, height() - 1);
}
