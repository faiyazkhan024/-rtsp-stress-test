#pragma once

#include <QOpenGLWidget>
#include <QOpenGLFunctions>
#include <QOpenGLBuffer>
#include <QOpenGLVertexArrayObject>
#include "stream_worker.h"
#include "gl_shader.h"

class VideoWidget : public QOpenGLWidget, protected QOpenGLFunctions {
    Q_OBJECT

public:
    explicit VideoWidget(int streamId, StreamWorker* worker, QWidget* parent = nullptr);
    ~VideoWidget() override;

    int streamId() const { return m_streamId; }
    bool hasNewFrame() const { return m_worker && m_worker->hasNewFrame(); }

protected:
    void initializeGL() override;
    void resizeGL(int w, int h) override;
    void paintGL() override;

private:
    void setupTextures();
    void renderFrame(AVFrame* frame, bool uploadTexture = true);
    void drawHudOverlay(QPainter& painter, int frameWidth, int frameHeight, float fps, bool isConnected, bool isHw);

    int m_streamId;
    StreamWorker* m_worker;

    QOpenGLBuffer m_vbo;
    QOpenGLVertexArrayObject m_vao;

    VideoShaderProgram m_shaderNv12;
    VideoShaderProgram m_shaderYuv420p;
    VideoShaderProgram m_shaderRgba;

    GLuint m_texY = 0;
    GLuint m_texU = 0;
    GLuint m_texV = 0;
    GLuint m_texUV = 0;
    GLuint m_texRGBA = 0;

    int m_texWidth = 0;
    int m_texHeight = 0;
    bool m_glInitialized = false;

    QPixmap m_hudCache;
    float m_cachedFps = -1.0f;
    bool m_cachedConnected = false;
    int m_cachedWidth = -1;
    int m_cachedHeight = -1;
};
