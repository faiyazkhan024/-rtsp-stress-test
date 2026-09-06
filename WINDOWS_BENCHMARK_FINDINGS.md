# Windows Physical Benchmark Findings (30-Stream 1440p Grid)

**Test Environment:**
- **OS:** Windows 11 / Physical Desktop (Headed UI)
- **GPU:** NVIDIA GeForce RTX 4060 Ti (8 GB VRAM)
- **RAM:** 16 GB DDR5
- **Workload:** 30 concurrent RTSP streams @ 1440p (2560x1440), 25 FPS (`testsrc2` pattern)

---

## 1. Quick Summary Table

| Framework & Mode | Visual Observation | Avg UI FPS (Logs) | Avg Decode FPS | GPU Decoder Load | Memory (RAM / VRAM) | Verdict |
| :--- | :--- | :---: | :---: | :---: | :---: | :--- |
| **C++ Qt6 (CPU)** | Smooth, no tearing, no lag, all 30 screens ran stably without hang/crash | **~9.2 FPS** | ~15.2 FPS | 0% | 1.9 GB RAM / 630 MB VRAM | **Best CPU Stability** (Paced & lightweight) |
| **C++ Qt6 (GPU)** | Frame tearing and lagging, but no crash | **~5.8 FPS** | ~18.6 FPS | 99% (Saturated) | 1.7 GB RAM / 5.9 GB VRAM | **Decoder Bottleneck** (NVDEC pinned at 100%) |
| **C# Avalonia (GPU)** | Frame tearing, no lag, no crash. System hardware decoded at 30+ FPS, but app choked | **~1.0 FPS** | ~34.2 FPS | 100% (Saturated) | 2.2 GB RAM / 3.3 GB VRAM | **UI Render Choke** (Decoder flew, UI thread stalled) |
| **C# Avalonia (CPU)** | Initial black screen until stream caught up; then tearing, no lag, no crash | **~0.8 FPS** | ~10.5 FPS | 0% | 2.0 GB RAM / 1.2 GB VRAM | **UI Blit Choke** (Software memory copy bottleneck) |
| **Electron (GPU)** | Good motion, slight frame tearing, no lag | **~18.5 FPS** | ~18.5 FPS | 88% | 2.4 GB RAM / 4.7 GB VRAM | **Fastest GPU Run** (Smooth, minor tearing) |
| **Electron (CPU)** | Smooth motion, no frame tearing, no lag | **~16.2 FPS** | ~16.2 FPS | 0% | 11.6 GB RAM / 2.7 GB VRAM | **Smooth but Heavy** (Clean visual, massive RAM usage) |

---

## 2. Detailed Findings per Implementation

### 1. C++ Qt6 — CPU Software Decode
* **User Observation:** Smooth playback with zero frame tearing or lag. All 30 tiles rendered without freezing or crashing, averaging ~10 FPS.
* **Log Insights:**
  - Sustained **9.2 FPS** UI presentation and **15.2 FPS** decode throughput across all 30 streams.
  - Very light on resources: strictly **1.85 GB RAM** and **0% GPU decoder** usage.
  - Qt's wait-free triple buffer and SIMD-aligned pixel blitting prevented tearing and kept the UI completely responsive despite high CPU load.

### 2. C++ Qt6 — GPU Hardware Decode
* **User Observation:** Visible frame tearing, judder, and lag, though the application stayed alive without crashing (~4 FPS avg).
* **Log Insights:**
  - UI presentation averaged **5.8 FPS** while decoder throughput hit **18.6 FPS**.
  - **The Bottleneck:** The RTX 4060 Ti hardware decoder (NVDEC) was pinned at **98.8% to 100% capacity**, and VRAM usage climbed to **5.9 GB**.
  - Pushing 30 high-resolution hardware textures simultaneously overwhelmed the OpenGL texture swap queue on the single UI thread, leading to dropped presentation ticks and tearing.

### 3. C# Avalonia — GPU Hardware Decode
* **User Observation:** Frame tearing, no input lag, no crash. The hardware decode was easily delivering 30 FPS, but the program itself couldn't present it and crawled at ~1 FPS per tile.
* **Log Insights:**
  - **Decoded FPS:** **34.2 FPS** average (97.8% of stream-time was running at a full 25–30 FPS). The backend hardware decoders ran at maximum speed.
  - **Presented FPS:** **0.95 FPS** (99.9% of time in `<5 FPS` bucket).
  - **The Bottleneck:** Avalonia's render loop / `OpenGlControlBase` synchronization could not keep up with 30 concurrent texture updates, dropping almost every frame at the UI layer.

### 4. C# Avalonia — CPU Software Decode
* **User Observation:** Started with a black screen during initial stream connection, then began displaying with frame tearing, no lag, and no crashes at ~1 FPS avg.
* **Log Insights:**
  - Decoded at **10.5 FPS**, but visual presentation crawled at **0.78 FPS**.
  - Memory was stable (~1.8 GB RAM), with 0% GPU decoder usage.
  - The initial black screen was caused by the pipeline waiting for keyframe/SPS/PPS alignment before first draw. Once running, CPU `WriteableBitmap.Lock()` memory copying saturated the UI thread.

### 5. Electron — GPU Hardware Decode (WebCodecs)
* **User Observation:** Responsive playback with minor frame tearing, no lag, averaging ~17 FPS.
* **Log Insights:**
  - Delivered **18.5 FPS** UI presentation and decode throughput across the full 60-minute test.
  - GPU Decoder averaged **88%**, with **4.6 GB VRAM** and **2.1 GB RAM**.
  - Chromium's multi-process GPU architecture handled 30 WebCodecs contexts effectively. Minor tearing occurred from compositor vsync desync under heavy load, but motion remained smooth.

### 6. Electron — CPU Software Decode
* **User Observation:** Clean presentation with no frame tearing and no lag, averaging ~17 FPS.
* **Log Insights:**
  - Rock-steady **16.2 FPS** presentation across all 30 streams with 100% of time spent consistently in the 10–19 FPS bucket.
  - **The Trade-Off:** While visually tear-free and smooth, Chromium's software decoder required massive memory buffering. RAM usage averaged **5.0 GB** and peaked at **11.6 GB**.

---

## 3. Key Takeaways

1. **Best Overall Visual Quality & Speed:** **Electron (GPU)** delivered the best balance of frame rate (~18.5 FPS) and responsiveness with moderate resource consumption.
2. **Most Predictable CPU Runner:** **C++ Qt6 (CPU)** was the most memory-efficient (~1.9 GB) and stable implementation, delivering a tear-free ~9.2 FPS without ballooning memory.
3. **Hardware NVDEC Limits:** 30 simultaneous 1440p streams at 25 FPS push the RTX 4060 Ti NVDEC engine right to its hardware limit (~100% utilization).
4. **Avalonia UI Bottleneck:** Avalonia's backend decoded well (10–34 FPS), but both CPU and GPU render pipelines throttled down to ~1 FPS on the presentation layer.
