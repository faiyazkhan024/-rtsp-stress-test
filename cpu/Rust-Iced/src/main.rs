mod config;
mod decoder;
mod platform;
mod telemetry;
mod ui;

use config::BenchmarkConfig;
use decoder::StreamManager;
use iced::widget::column;
use iced::{Element, Length, Size, Subscription, Task, Theme};
use std::sync::atomic::Ordering;
use std::sync::{Arc, RwLock};
use std::time::Duration;
use telemetry::TelemetryManager;
use ui::{view_grid, view_hud, HudState, Message};

struct BenchmarkApp {
    config: BenchmarkConfig,
    stream_manager: Arc<StreamManager>,
    telemetry: Arc<RwLock<TelemetryManager>>,
    hud: HudState,
    #[allow(dead_code)]
    render_tick_count: u64,
}

impl BenchmarkApp {
    fn new(
        config: BenchmarkConfig,
        stream_manager: Arc<StreamManager>,
        telemetry: Arc<RwLock<TelemetryManager>>,
    ) -> (Self, Task<Message>) {
        let stream_count = config.stream_count;
        let log_path = telemetry.read().unwrap().get_log_path();

        let hud = HudState {
            machine_id: config.machine_id.clone(),
            active_streams: 0,
            total_streams: stream_count,
            tick_in_window: 0,
            window_duration: config.window_duration_seconds,
            total_flushes: 0,
            current_buckets: Default::default(),
            stream_fps: vec![0; stream_count],
            total_fps: 0,
            avg_fps: 0.0,
            log_path,
        };

        (
            Self {
                config,
                stream_manager,
                telemetry,
                hud,
                render_tick_count: 0,
            },
            Task::none(),
        )
    }

    fn update(&mut self, message: Message) -> Task<Message> {
        match message {
            Message::RenderTick => {
                self.render_tick_count += 1;
            }
            Message::TelemetryTick => {
                let metrics = self.stream_manager.collect_metrics_tick();
                let fps_list: Vec<u32> = metrics.iter().map(|&(ui, _, _)| ui).collect();
                let (payload, tick_in_win) = {
                    let mut tel = self.telemetry.write().unwrap();
                    tel.record_tick(&metrics)
                };

                let total_flushes = self.telemetry.read().unwrap().get_total_flushes();
                let total_fps: u32 = fps_list.iter().sum();
                let avg_fps = total_fps as f32 / fps_list.len().max(1) as f32;
                let active_count = self
                    .stream_manager
                    .slots
                    .iter()
                    .filter(|s| s.is_connected.load(Ordering::Relaxed))
                    .count();

                self.hud.active_streams = active_count;
                self.hud.tick_in_window = tick_in_win;
                self.hud.total_flushes = total_flushes;
                self.hud.current_buckets = payload.fps_stream_seconds;
                self.hud.stream_fps = fps_list;
                self.hud.total_fps = total_fps;
                self.hud.avg_fps = avg_fps;
            }
        }
        Task::none()
    }

    fn view(&self) -> Element<'_, Message> {
        column![
            view_hud(&self.hud),
            view_grid(&self.stream_manager.slots, &self.hud.stream_fps),
        ]
        .width(Length::Fill)
        .height(Length::Fill)
        .into()
    }

    fn subscription(&self) -> Subscription<Message> {
        let render_interval_ms = (1000 / self.config.ui_render_fps.max(1)).max(10);
        Subscription::batch([
            iced::time::every(Duration::from_millis(render_interval_ms as u64))
                .map(|_| Message::RenderTick),
            iced::time::every(Duration::from_millis(1000)).map(|_| Message::TelemetryTick),
        ])
    }

    fn theme(&self) -> Theme {
        Theme::Dark
    }
}

fn main() -> iced::Result {
    env_logger::init();
    platform::raise_file_descriptor_limit();
    platform::log_cpu();

    // Initialize GStreamer
    if let Err(e) = gstreamer::init() {
        eprintln!("[ERROR] Failed to initialize GStreamer: {}", e);
        std::process::exit(1);
    }

    let config = BenchmarkConfig::from_env();
    println!("=== 6-Hour RTSP Video Grid Benchmark (Rust Iced CPU) ===");
    println!("Platform:          {}", platform::name());
    println!("Machine ID:        {}", config.machine_id);
    println!("Stream Count:      {}", config.stream_count);
    println!("Target RTSP URL:   {}", config.rtsp_url);
    println!(
        "Target Resolution: {}x{} @ {} FPS (Render size: {}x{})",
        config.video_width, config.video_height, config.target_fps,
        config.tile_width, config.tile_height
    );
    println!("Rendering Backend: tiny-skia (Software Blitting)");
    println!("Telemetry Log:     {}", config.resolve_log_path().display());

    let telemetry = Arc::new(RwLock::new(TelemetryManager::new(config.clone())));

    let config_clone = config.clone();
    let stream_manager = Arc::new(StreamManager::new(
        config.stream_count,
        move |idx| config_clone.get_rtsp_url_for_stream(idx),
        config.video_width,
        config.video_height,
        config.tile_width,
        config.tile_height,
    ));

    println!(
        "[*] Launching {} GStreamer CPU software decoders...",
        config.stream_count
    );
    stream_manager.start_all();

    let app_config = config.clone();
    let app_stream_mgr = stream_manager.clone();
    let app_telemetry = telemetry.clone();

    iced::application(
        move || {
            BenchmarkApp::new(
                app_config.clone(),
                app_stream_mgr.clone(),
                app_telemetry.clone(),
            )
        },
        BenchmarkApp::update,
        BenchmarkApp::view,
    )
    .title("RTSP 30-Stream Video Grid Benchmark (Rust Iced CPU - tiny-skia)")
    .subscription(BenchmarkApp::subscription)
    .theme(BenchmarkApp::theme)
    .window_size(Size::new(2560.0, 1440.0))
    .run()
}
