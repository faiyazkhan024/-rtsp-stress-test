#include "video_widget.h"

#include <QFont>
#include <QFontMetrics>

VideoWidget::VideoWidget(int streamId, StreamWorker* worker, QWidget* parent)
    : QWidget(parent)
    , m_streamId(streamId)
    , m_worker(worker)
{
    setAttribute(Qt::WA_OpaquePaintEvent);
    setAttribute(Qt::WA_NoSystemBackground);
    setSizePolicy(QSizePolicy::Expanding, QSizePolicy::Expanding);
    setMinimumSize(160, 90);
}

void VideoWidget::paintEvent(QPaintEvent* /*event*/) {
    QPainter painter(this);

    int w = 0;
    int h = 0;
    uint8_t* pixels = m_worker ? m_worker->acquireFrame(w, h) : nullptr;

    if (pixels && w > 0 && h > 0) {
        // Zero-copy instantiation of QImage directly wrapping pre-allocated RGB32 memory buffer
        QImage img(pixels, w, h, w * 4, QImage::Format_RGB32);

        // Blit image onto QWidget surface
        painter.drawImage(rect(), img);
    } else {
        // Standby / connecting state
        painter.fillRect(rect(), QColor("#0d1117"));

        QFont font = painter.font();
        font.setPixelSize(13);
        font.setBold(true);
        painter.setFont(font);

        painter.setPen(QColor("#a0aec0"));
        QString text = QString("CAM %1\nCONNECTING...")
                           .arg(m_streamId + 1, 2, 10, QChar('0'));
        painter.drawText(rect(), Qt::AlignCenter, text);
    }

    // Paint HUD Overlay
    float fps = m_worker ? m_worker->currentFps() : 0.0f;
    bool isConnected = m_worker && m_worker->isConnected();

    QColor fpsColor("#ef4444"); // Red: < 20 FPS
    if (fps >= 25.0f) {
        fpsColor = QColor("#22c55e"); // Green: >= 25 FPS
    } else if (fps >= 20.0f) {
        fpsColor = QColor("#eab308"); // Yellow: 20-24 FPS
    }

    // Badge Background
    QFont hudFont = painter.font();
    hudFont.setPixelSize(11);
    hudFont.setBold(true);
    painter.setFont(hudFont);

    QString camStr = QString("CAM %1").arg(m_streamId + 1, 2, 10, QChar('0'));
    int sw = m_worker ? m_worker->sourceWidth() : 2560;
    int sh = m_worker ? m_worker->sourceHeight() : 1440;
    QString resStr = (sw > 0 && sh > 0) ? QString("%1x%2").arg(sw).arg(sh) : QString("2560x1440");
    QString fpsStr = QString("%1 FPS").arg(fps, 0, 'f', 1);

    QString badgeText = QString("%1  |  %2  |  %3").arg(camStr, resStr, fpsStr);
    QFontMetrics fm(hudFont);
    int textWidth = fm.horizontalAdvance(badgeText);
    int badgeWidth = textWidth + 18;
    int badgeHeight = 22;

    QRect badgeRect(8, 8, badgeWidth, badgeHeight);
    painter.setPen(Qt::NoPen);
    painter.setBrush(QColor(15, 23, 42, 200)); // Dark semi-transparent
    painter.drawRoundedRect(badgeRect, 4, 4);

    // Status Dot
    painter.setBrush(isConnected ? fpsColor : QColor("#ef4444"));
    painter.drawEllipse(badgeRect.x() + 6, badgeRect.y() + 7, 8, 8);

    // Badge Text
    painter.setPen(QColor("#f8fafc"));
    painter.drawText(badgeRect.x() + 18, badgeRect.y() + 15, badgeText);

    // 1px Border
    painter.setPen(QColor(30, 41, 59, 180));
    painter.setBrush(Qt::NoBrush);
    painter.drawRect(0, 0, width() - 1, height() - 1);
}
