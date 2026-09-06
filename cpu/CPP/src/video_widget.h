#pragma once

#include <QWidget>
#include <QPainter>
#include <QPaintEvent>
#include "stream_worker.h"

class VideoWidget : public QWidget {
    Q_OBJECT

public:
    explicit VideoWidget(int streamId, StreamWorker* worker, QWidget* parent = nullptr);
    ~VideoWidget() override = default;

    int streamId() const { return m_streamId; }
    bool hasNewFrame() const { return m_worker && m_worker->hasNewFrame(); }

protected:
    void paintEvent(QPaintEvent* event) override;

private:
    int m_streamId;
    StreamWorker* m_worker;
};
