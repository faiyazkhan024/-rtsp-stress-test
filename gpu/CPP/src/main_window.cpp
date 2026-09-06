#include "main_window.h"
#include "platform.h"

#include <QVBoxLayout>
#include <QHBoxLayout>
#include <QFrame>
#include <QCloseEvent>
#include <iostream>

MainWindow::MainWindow(const AppConfig& config,
                       std::shared_ptr<HwAccelManager> hwAccel,
                       QWidget* parent)
    : QMainWindow(parent)
    , m_config(config)
    , m_hwAccel(hwAccel)
    , m_telemetry(std::make_unique<TelemetryManager>(config.logPath, config.machineId, config.streamCount))
{
    std::string hwDesc = m_hwAccel ? m_hwAccel->deviceName() : "GPU";
    setWindowTitle(QString("6-Hour RTSP Video Grid Benchmark (C++ Qt6 GPU Zero-Copy Hardware Decode [%1]) - %2 Streams")
                       .arg(QString::fromStdString(hwDesc).toUpper())
                       .arg(m_config.streamCount));
    resize(1920, 1080);
    setStyleSheet("background-color: #090d16; color: #f8fafc;");

    setupUi();
    startWorkers();

    // Decoupled master rendering timer: 60 Hz polling of workers without timer quantization
    m_renderTimer = new QTimer(this);
    m_renderTimer->setTimerType(Qt::PreciseTimer);
    connect(m_renderTimer, &QTimer::timeout, this, &MainWindow::onRenderTick);
    m_renderTimer->start(15);

    // Master telemetry timer: 1-second interval FPS tick & rolling window management
    m_telemetryTimer = new QTimer(this);
    connect(m_telemetryTimer, &QTimer::timeout, this, &MainWindow::onTelemetryTick);
    m_telemetryTimer->start(1000);
}

MainWindow::~MainWindow() {
    stopWorkers();
}

void MainWindow::setupUi() {
    m_centralWidget = new QWidget(this);
    setCentralWidget(m_centralWidget);

    auto* mainLayout = new QVBoxLayout(m_centralWidget);
    mainLayout->setContentsMargins(4, 4, 4, 4);
    mainLayout->setSpacing(4);

    // --- Top Dashboard / HUD Bar ---
    auto* topBar = new QFrame(this);
    topBar->setStyleSheet("background-color: #161e2e; border-radius: 6px; padding: 4px;");
    auto* topLayout = new QHBoxLayout(topBar);
    topLayout->setContentsMargins(12, 4, 12, 4);
    topLayout->setSpacing(16);

    // Title / Framework Badge
    auto* titleLabel = new QLabel("<b>RTSP 30-CAMERA GRID</b> <span style='color: #38bdf8;'>Qt6 GPU Zero-Copy Benchmark</span>", this);
    titleLabel->setStyleSheet("font-size: 13px;");
    topLayout->addWidget(titleLabel);

    // Divider
    auto* div1 = new QFrame(this);
    div1->setFrameShape(QFrame::VLine);
    div1->setStyleSheet("color: #334155;");
    topLayout->addWidget(div1);

    // Active Streams
    m_streamsLabel = new QLabel(QString("Streams: <b>0 / %1</b>").arg(m_config.streamCount), this);
    m_streamsLabel->setStyleSheet("font-size: 12px;");
    topLayout->addWidget(m_streamsLabel);

    // Aggregate FPS
    m_fpsLabel = new QLabel("FPS Total: <b>0.0</b>", this);
    m_fpsLabel->setStyleSheet("font-size: 12px; color: #22c55e;");
    topLayout->addWidget(m_fpsLabel);

    // Flush Countdown
    m_countdownLabel = new QLabel("Flush: <b>60s</b>", this);
    m_countdownLabel->setStyleSheet("font-size: 12px; color: #94a3b8;");
    topLayout->addWidget(m_countdownLabel);

    // Acceptable Buckets
    m_acceptableBucketsLabel = new QLabel("Acceptable [25-30: <b>0</b> | 20-24: <b>0</b>]", this);
    m_acceptableBucketsLabel->setStyleSheet("font-size: 12px; color: #4ade80;");
    topLayout->addWidget(m_acceptableBucketsLabel);

    // Unacceptable Buckets
    m_unacceptableBucketsLabel = new QLabel("Unacceptable [10-19: <b>0</b> | 5-9: <b>0</b> | <5: <b>0</b>]", this);
    m_unacceptableBucketsLabel->setStyleSheet("font-size: 12px; color: #f87171;");
    topLayout->addWidget(m_unacceptableBucketsLabel);

    topLayout->addStretch();

    // Mode & Architecture Note
    std::string hwName = m_hwAccel ? m_hwAccel->deviceName() : "GPU";
    QString modeDesc = QString("<span style='color: #94a3b8;'>libavcodec (<b>%1</b>) &bull; QOpenGLWidget &bull; BT.709 Shaders</span>")
                           .arg(QString::fromStdString(hwName).toUpper());
    m_modeLabel = new QLabel(modeDesc, this);
    m_modeLabel->setStyleSheet("font-size: 11px;");
    topLayout->addWidget(m_modeLabel);

    mainLayout->addWidget(topBar);

    // --- Grid Layout for 30 Hardware Video Widgets ---
    auto* gridContainer = new QWidget(this);
    m_gridLayout = new QGridLayout(gridContainer);
    m_gridLayout->setContentsMargins(0, 0, 0, 0);
    m_gridLayout->setSpacing(3);

    int totalStreams = m_config.streamCount;
    int cols = 6;
    if (totalStreams <= 4) {
        cols = 2;
    } else if (totalStreams <= 9) {
        cols = 3;
    } else if (totalStreams <= 16) {
        cols = 4;
    } else if (totalStreams <= 25) {
        cols = 5;
    }

    m_workers.reserve(totalStreams);
    m_videoWidgets.reserve(totalStreams);

    for (int i = 0; i < totalStreams; ++i) {
        auto* worker = new StreamWorker(i, m_config.urlForStream(i), m_hwAccel, this);
        m_workers.push_back(worker);

        auto* widget = new VideoWidget(i, worker, gridContainer);
        m_videoWidgets.push_back(widget);

        int row = i / cols;
        int col = i % cols;
        m_gridLayout->addWidget(widget, row, col);
    }

    mainLayout->addWidget(gridContainer, 1);
}

void MainWindow::startWorkers() {
    std::cout << "[MainWindow] Starting " << m_workers.size() << " background RTSP GPU decoder threads..." << std::endl;
    for (size_t i = 0; i < m_workers.size(); ++i) {
        m_workers[i]->start();
        QThread::msleep(kStreamStaggerMs);
    }
    std::cout << "[MainWindow] All GPU decoder threads started successfully." << std::endl;
}

void MainWindow::stopWorkers() {
    if (m_renderTimer) {
        m_renderTimer->stop();
    }
    if (m_telemetryTimer) {
        m_telemetryTimer->stop();
    }

    std::cout << "[MainWindow] Stopping GPU decoder threads..." << std::endl;
    for (auto* worker : m_workers) {
        if (worker) {
            worker->stopWorker();
        }
    }
    std::cout << "[MainWindow] All GPU decoder threads stopped." << std::endl;
}

void MainWindow::onRenderTick() {
    for (auto* widget : m_videoWidgets) {
        if (widget && widget->hasNewFrame()) {
            widget->update();
        }
    }
}

void MainWindow::onTelemetryTick() {
    m_telemetry->tick(m_workers);
    updateHud();
}

void MainWindow::updateHud() {
    int connectedCount = 0;
    for (const auto* worker : m_workers) {
        if (worker && worker->isConnected()) {
            connectedCount++;
        }
    }

    m_streamsLabel->setText(QString("Streams: <b>%1 / %2</b>")
                                .arg(connectedCount)
                                .arg(m_config.streamCount));

    float aggFps = m_telemetry->aggregateFps();
    m_fpsLabel->setText(QString("FPS Total: <b>%1</b>").arg(aggFps, 0, 'f', 1));

    int secLeft = m_telemetry->secondsRemaining();
    m_countdownLabel->setText(QString("Flush: <b>%1s</b>").arg(secLeft));

    FpsBuckets buckets = m_telemetry->currentWindowBuckets();
    m_acceptableBucketsLabel->setText(
        QString("Acceptable [25-30: <b>%1</b> | 20-24: <b>%2</b>]")
            .arg(buckets.fps_25_to_30)
            .arg(buckets.fps_20_to_24)
    );

    m_unacceptableBucketsLabel->setText(
        QString("Unacceptable [10-19: <b>%1</b> | 5-9: <b>%2</b> | &lt;5: <b>%3</b>]")
            .arg(buckets.fps_10_to_19)
            .arg(buckets.fps_5_to_9)
            .arg(buckets.fps_under_5)
    );
}

void MainWindow::closeEvent(QCloseEvent* event) {
    stopWorkers();
    event->accept();
}
