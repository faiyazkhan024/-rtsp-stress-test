import React, { useEffect, useRef } from 'react';
import { StreamFpsReport, VideoPlayerRef } from './VideoPlayer';

interface UnifiedVideoGridProps {
  streamCount: number;
  wsPort: number;
  playerRefs: React.MutableRefObject<Map<number, VideoPlayerRef>>;
}

interface StreamState {
  streamId: number;
  frameCount: number;
  decodedCount: number;
  lastTickTime: number;
  lastPts: number | null;
  lastPresentedTime: number;
  lastDeltaMs: number;
  isConnected: boolean;
  connectedSince: number;
  hasConfigured: boolean;
  currentCodec: string;
  decoder: VideoDecoder | null;
  ws: WebSocket | null;
  currentFrame: VideoFrame | null;
  pendingFrame: VideoFrame | null;
  fpsBadge: HTMLSpanElement | null;
  statusDot: HTMLSpanElement | null;
  placeholder: HTMLDivElement | null;
}

const WGSL_SHADER = `
struct VertexOutput {
  @builtin(position) pos: vec4f,
  @location(0) uv: vec2f,
};

struct Uniforms {
  rect: vec4f, // x, y, width, height in NDC coordinates [-1, 1]
};

@group(0) @binding(0) var mySampler: sampler;
@group(0) @binding(1) var myTexture: texture_external;
@group(0) @binding(2) var<uniform> uniforms: Uniforms;

@vertex
fn vs_main(@builtin(vertex_index) vid: u32) -> VertexOutput {
  var pos = array<vec2f, 6>(
    vec2f(0.0, 0.0),
    vec2f(1.0, 0.0),
    vec2f(0.0, 1.0),
    vec2f(0.0, 1.0),
    vec2f(1.0, 0.0),
    vec2f(1.0, 1.0)
  );
  let p = pos[vid];
  var out: VertexOutput;
  out.pos = vec4f(
    uniforms.rect.x + p.x * uniforms.rect.z,
    uniforms.rect.y - p.y * uniforms.rect.w,
    0.0,
    1.0
  );
  out.uv = p;
  return out;
}

@fragment
fn fs_main(in: VertexOutput) -> @location(0) vec4f {
  return textureSampleBaseClampToEdge(myTexture, mySampler, in.uv);
}
`;

function getGridDimensions(count: number): { cols: number; rows: number } {
  if (count <= 1) return { cols: 1, rows: 1 };
  if (count <= 4) return { cols: 2, rows: 2 };
  if (count <= 9) return { cols: 3, rows: 3 };
  if (count <= 16) return { cols: 4, rows: 4 };
  if (count <= 25) return { cols: 5, rows: 5 };
  return { cols: 6, rows: Math.ceil(count / 6) };
}

export const UnifiedVideoGrid: React.FC<UnifiedVideoGridProps> = ({ streamCount, wsPort, playerRefs }) => {
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const containerRef = useRef<HTMLDivElement | null>(null);
  const streamsRef = useRef<StreamState[]>([]);

  const { cols, rows } = getGridDimensions(streamCount);
  const streamIndices = Array.from({ length: streamCount }, (_, i) => i);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;

    let isDestroyed = false;
    let rafId: number | null = null;

    // Initialize stream states
    const streams: StreamState[] = streamIndices.map((id) => ({
      streamId: id,
      frameCount: 0,
      decodedCount: 0,
      lastTickTime: performance.now(),
      lastPts: null,
      lastPresentedTime: 0,
      lastDeltaMs: 0,
      isConnected: false,
      connectedSince: 0,
      hasConfigured: false,
      currentCodec: '',
      decoder: null,
      ws: null,
      currentFrame: null,
      pendingFrame: null,
      fpsBadge: null,
      statusDot: null,
      placeholder: null,
    }));
    streamsRef.current = streams;

    // Register playerRefs for telemetry engine
    streams.forEach((stream) => {
      playerRefs.current.set(stream.streamId, {
        getFpsAndReset: () => {
          const now = performance.now();
          const elapsedSec = (now - stream.lastTickTime) / 1000;
          stream.lastTickTime = now;
          const fps = elapsedSec > 0 ? Math.round(stream.frameCount / elapsedSec) : 0;
          stream.frameCount = 0;
          stream.decodedCount = 0;
          return fps;
        },
        getReportAndReset: () => {
          const now = performance.now();
          const elapsedSec = (now - stream.lastTickTime) / 1000;
          stream.lastTickTime = now;
          const uiFrames = stream.frameCount;
          const decodedFrames = stream.decodedCount;
          const fps = elapsedSec > 0 ? Math.round(uiFrames / elapsedSec) : 0;
          stream.frameCount = 0;
          stream.decodedCount = 0;
          const connected = stream.isConnected
            && stream.lastPresentedTime > 0
            && now - stream.lastPresentedTime < 3000
            && stream.connectedSince > 0
            && now - stream.connectedSince >= 1000;
          return {
            streamId: stream.streamId,
            fps,
            isConnected: connected,
            lastDeltaMs: stream.lastDeltaMs,
            uiFrames,
            decodedFrames,
          };
        },
        updateFpsDisplay: (fps: number) => {
          if (stream.fpsBadge) {
            stream.fpsBadge.textContent = `${fps} FPS`;
            stream.fpsBadge.className = 'fps-badge ' + (
              fps >= 25 ? 'acceptable' : fps >= 20 ? 'warning' : 'unacceptable'
            );
          }
          if (stream.statusDot) {
            stream.statusDot.className = 'status-dot ' + (fps > 0 ? 'active' : 'waiting');
          }
          if (stream.placeholder && fps > 0) {
            stream.placeholder.style.display = 'none';
          }
        },
      });
    });

    // Helper to find SPS in Annex B buffer and extract codec string
    const extractSpsCodec = (data: Uint8Array): string | null => {
      for (let i = 0; i < data.length - 5; i++) {
        let scLen = 0;
        if (data[i] === 0 && data[i + 1] === 0) {
          if (data[i + 2] === 1) scLen = 3;
          else if (data[i + 2] === 0 && data[i + 3] === 1) scLen = 4;
        }
        if (scLen > 0) {
          const nalType = data[i + scLen] & 0x1F;
          if (nalType === 7 && i + scLen + 3 < data.length) {
            const profile = data[i + scLen + 1].toString(16).padStart(2, '0');
            const constraints = data[i + scLen + 2].toString(16).padStart(2, '0');
            const level = data[i + scLen + 3].toString(16).padStart(2, '0');
            return `avc1.${profile}${constraints}${level}`;
          }
          i += scLen;
        }
      }
      return null;
    };

    // Initialize decoders and WebSockets for all streams
    streams.forEach((stream) => {
      const createDecoder = (): VideoDecoder | null => {
        try {
          return new VideoDecoder({
            output: (videoFrame: VideoFrame) => {
              if (isDestroyed) {
                videoFrame.close();
                return;
              }
              stream.decodedCount++;
              const curPts = videoFrame.timestamp;
              if (stream.lastPts !== null && curPts === stream.lastPts) {
                videoFrame.close();
                return;
              }
              stream.lastPts = curPts;

              // Decouple: Hold newest frame, closing previous pending frame if unpresented
              if (stream.pendingFrame) {
                stream.pendingFrame.close();
              }
              stream.pendingFrame = videoFrame;
            },
            error: (err) => {
              console.warn(`[Unified Stream ${stream.streamId}] VideoDecoder error:`, err);
              stream.hasConfigured = false;
              if (stream.decoder && stream.decoder.state !== 'closed') {
                try { stream.decoder.close(); } catch (_) {}
              }
            },
          });
        } catch (err) {
          console.error(`[Unified Stream ${stream.streamId}] Failed to initialize VideoDecoder:`, err);
          return null;
        }
      };

      stream.decoder = createDecoder();

      // Connect WebSocket
      const wsUrl = `ws://127.0.0.1:${wsPort}/stream/${stream.streamId}`;
      const ws = new WebSocket(wsUrl);
      ws.binaryType = 'arraybuffer';
      stream.ws = ws;

      ws.onmessage = (event: MessageEvent) => {
        if (isDestroyed || typeof event.data === 'string') return;
        const buffer = event.data as ArrayBuffer;
        if (buffer.byteLength < 10) return;

        const view = new DataView(buffer);
        const isKey = view.getUint8(0) === 1;
        const timestampUs = Number(view.getBigInt64(1));
        const nalData = new Uint8Array(buffer, 9);

        if (isKey) {
          const detectedCodec = extractSpsCodec(nalData) || 'avc1.42c032';
          if (!stream.decoder || stream.decoder.state === 'closed') {
            stream.decoder = createDecoder();
            stream.hasConfigured = false;
          }

          const needsConfig = !stream.hasConfigured
            || stream.currentCodec !== detectedCodec
            || (stream.decoder && stream.decoder.state !== 'configured');

          if (needsConfig && stream.decoder) {
            try {
              stream.decoder.configure({
                codec: detectedCodec,
                avc: { format: 'annexb' },
                hardwareAcceleration: 'prefer-hardware',
                optimizeForLatency: true,
              });
              stream.hasConfigured = true;
              stream.currentCodec = detectedCodec;
            } catch (cfgErr) {
              console.warn(`[Unified Stream ${stream.streamId}] Config error, falling back to no-preference:`, cfgErr);
              try {
                stream.decoder.configure({
                  codec: detectedCodec,
                  avc: { format: 'annexb' },
                  hardwareAcceleration: 'no-preference',
                  optimizeForLatency: true,
                });
                stream.hasConfigured = true;
                stream.currentCodec = detectedCodec;
              } catch (fallbackErr) {
                console.error(`[Unified Stream ${stream.streamId}] Fallback config failed:`, fallbackErr);
              }
            }
          }
        }

        if (!stream.hasConfigured || !stream.decoder || stream.decoder.state !== 'configured') {
          return;
        }

        if (!isKey && stream.decoder.decodeQueueSize > 10) {
          return;
        }

        try {
          const chunk = new EncodedVideoChunk({
            type: isKey ? 'key' : 'delta',
            timestamp: timestampUs,
            data: nalData,
          });
          stream.decoder.decode(chunk);
        } catch (decErr) {
          console.warn(`[Unified Stream ${stream.streamId}] decode error:`, decErr);
        }
      };

      ws.onclose = () => {
        stream.isConnected = false;
        stream.connectedSince = 0;
        if (!isDestroyed && stream.statusDot) {
          stream.statusDot.className = 'status-dot offline';
        }
      };
    });

    const updateCanvasSize = () => {
      const dpr = window.devicePixelRatio || 1;
      const w = Math.max(1, Math.round(canvas.clientWidth * dpr));
      const h = Math.max(1, Math.round(canvas.clientHeight * dpr));
      if (canvas.width !== w || canvas.height !== h) {
        canvas.width = w;
        canvas.height = h;
      }
    };
    updateCanvasSize();

    const resizeObserver = typeof ResizeObserver !== 'undefined'
      ? new ResizeObserver(updateCanvasSize)
      : null;
    if (resizeObserver && containerRef.current) {
      resizeObserver.observe(containerRef.current);
    }

    // Try WebGPU first, with automatic 2D context fallback
    async function startRenderPipeline() {
      if (navigator.gpu) {
        try {
          const adapter = await navigator.gpu.requestAdapter({ powerPreference: 'high-performance' });
          if (adapter) {
            const device = await adapter.requestDevice();
            const webgpuCtx = canvas?.getContext('webgpu');
            if (webgpuCtx) {
              const format = navigator.gpu.getPreferredCanvasFormat();
              webgpuCtx.configure({
                device,
                format,
                alphaMode: 'opaque',
              });

              const shaderModule = device.createShaderModule({ code: WGSL_SHADER });
              const pipeline = device.createRenderPipeline({
                layout: 'auto',
                vertex: { module: shaderModule, entryPoint: 'vs_main' },
                fragment: {
                  module: shaderModule,
                  entryPoint: 'fs_main',
                  targets: [{ format }],
                },
                primitive: { topology: 'triangle-list' },
              });

              const sampler = device.createSampler({ magFilter: 'linear', minFilter: 'linear' });

              // Allocate uniform buffers for each tile quad
              const uniformBuffers: GPUBuffer[] = [];
              const ndcW = 2.0 / cols;
              const ndcH = 2.0 / rows;

              for (let i = 0; i < streamCount; i++) {
                const col = i % cols;
                const row = Math.floor(i / cols);
                const ndcX = -1.0 + col * ndcW;
                const ndcY = 1.0 - row * ndcH;

                const ubuf = device.createBuffer({
                  size: 16,
                  usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
                });
                device.queue.writeBuffer(ubuf, 0, new Float32Array([ndcX, ndcY, ndcW, ndcH]));
                uniformBuffers.push(ubuf);
              }

              console.log('[UnifiedVideoGrid] WebGPU Zero-Copy Single-Canvas Pipeline initialized successfully');

              // WebGPU Master Render Loop
              const renderWebGPU = () => {
                if (isDestroyed) return;

                // Update frames
                const now = performance.now();
                for (const stream of streams) {
                  if (stream.pendingFrame) {
                    if (stream.currentFrame) {
                      stream.currentFrame.close();
                    }
                    stream.currentFrame = stream.pendingFrame;
                    stream.pendingFrame = null;

                    if (stream.lastPresentedTime > 0) {
                      stream.lastDeltaMs = now - stream.lastPresentedTime;
                    }
                    stream.lastPresentedTime = now;
                    if (!stream.isConnected) {
                      stream.connectedSince = now;
                    }
                    stream.isConnected = true;
                    stream.frameCount++;
                    if (stream.placeholder && stream.placeholder.style.display !== 'none') {
                      stream.placeholder.style.display = 'none';
                    }
                  }
                }

                try {
                  const commandEncoder = device.createCommandEncoder();
                  const textureView = webgpuCtx.getCurrentTexture().createView();
                  const renderPass = commandEncoder.beginRenderPass({
                    colorAttachments: [
                      {
                        view: textureView,
                        clearValue: { r: 0.035, g: 0.05, b: 0.086, a: 1.0 },
                        loadOp: 'clear',
                        storeOp: 'store',
                      },
                    ],
                  });

                  renderPass.setPipeline(pipeline);

                  for (let i = 0; i < streamCount; i++) {
                    const stream = streams[i];
                    if (stream && stream.currentFrame && stream.currentFrame.displayWidth > 0) {
                      try {
                        const externalTexture = device.importExternalTexture({ source: stream.currentFrame });
                        const bindGroup = device.createBindGroup({
                          layout: pipeline.getBindGroupLayout(0),
                          entries: [
                            { binding: 0, resource: sampler },
                            { binding: 1, resource: externalTexture },
                            { binding: 2, resource: { buffer: uniformBuffers[i] } },
                          ],
                        });
                        renderPass.setBindGroup(0, bindGroup);
                        renderPass.draw(6);
                      } catch (_) {}
                    }
                  }

                  renderPass.end();
                  device.queue.submit([commandEncoder.finish()]);
                } catch (renderErr) {
                  console.warn('[UnifiedVideoGrid] WebGPU render pass error:', renderErr);
                }

                rafId = requestAnimationFrame(renderWebGPU);
              };

              rafId = requestAnimationFrame(renderWebGPU);
              return;
            }
          }
        } catch (webgpuErr) {
          console.warn('[UnifiedVideoGrid] WebGPU initialization failed, falling back to 2D single canvas:', webgpuErr);
        }
      }

      // 2D Canvas Hardware Fallback (Single Canvas Architecture)
      console.log('[UnifiedVideoGrid] Initializing Single-Canvas 2D hardware-accelerated pipeline');
      const ctx2d = canvas?.getContext('2d', { alpha: false });
      if (!ctx2d) return;
      ctx2d.imageSmoothingEnabled = false;

      const render2D = () => {
        if (isDestroyed) return;
        const now = performance.now();
        const tileW = canvas.width / cols;
        const tileH = canvas.height / rows;

        for (let i = 0; i < streamCount; i++) {
          const stream = streams[i];
          const frame = stream.pendingFrame;
          if (frame) {
            stream.pendingFrame = null;
            const col = i % cols;
            const row = Math.floor(i / cols);
            const x = col * tileW;
            const y = row * tileH;

            try {
              ctx2d.drawImage(frame, x, y, tileW, tileH);
              if (stream.lastPresentedTime > 0) {
                stream.lastDeltaMs = now - stream.lastPresentedTime;
              }
              stream.lastPresentedTime = now;
              if (!stream.isConnected) {
                stream.connectedSince = now;
              }
              stream.isConnected = true;
              stream.frameCount++;
              if (stream.placeholder && stream.placeholder.style.display !== 'none') {
                stream.placeholder.style.display = 'none';
              }
            } catch (err) {
              console.warn(`[Unified Stream ${stream.streamId}] 2D drawImage error:`, err);
            } finally {
              frame.close();
            }
          }
        }

        rafId = requestAnimationFrame(render2D);
      };

      rafId = requestAnimationFrame(render2D);
    }

    startRenderPipeline();

    return () => {
      isDestroyed = true;
      if (rafId !== null) {
        cancelAnimationFrame(rafId);
      }
      if (resizeObserver) {
        resizeObserver.disconnect();
      }
      streams.forEach((stream) => {
        if (stream.ws) {
          stream.ws.close();
          stream.ws = null;
        }
        if (stream.decoder && stream.decoder.state !== 'closed') {
          try { stream.decoder.close(); } catch (_) {}
          stream.decoder = null;
        }
        if (stream.pendingFrame) {
          stream.pendingFrame.close();
          stream.pendingFrame = null;
        }
        if (stream.currentFrame) {
          stream.currentFrame.close();
          stream.currentFrame = null;
        }
        playerRefs.current.delete(stream.streamId);
      });
    };
  }, [streamCount, wsPort, cols, rows]);

  return (
    <div ref={containerRef} className="unified-grid-container">
      <canvas ref={canvasRef} className="unified-grid-canvas" />
      <div
        className="unified-overlay-grid"
        style={{
          gridTemplateColumns: `repeat(${cols}, 1fr)`,
          gridTemplateRows: `repeat(${rows}, 1fr)`,
          gap: '6px',
          padding: '8px',
        }}
      >
        {streamIndices.map((id) => (
          <div key={id} className="unified-tile-overlay">
            <div className="player-overlay">
              <span
                ref={(el) => {
                  if (streamsRef.current[id]) streamsRef.current[id].statusDot = el;
                }}
                className="status-dot waiting"
              />
              <span className="stream-id-tag">CH-{String(id + 1).padStart(2, '0')}</span>
              <span
                className="res-tag"
                style={{ fontSize: '10px', color: '#94a3b8', background: 'rgba(0,0,0,0.5)', padding: '1px 4px', borderRadius: '3px' }}
              >
                2560x1440
              </span>
              <span
                ref={(el) => {
                  if (streamsRef.current[id]) streamsRef.current[id].fpsBadge = el;
                }}
                className="fps-badge warning"
              >
                0 FPS
              </span>
            </div>
            <div
              ref={(el) => {
                if (streamsRef.current[id]) streamsRef.current[id].placeholder = el;
              }}
              className="waiting-placeholder"
            >
              <span>Connecting CH-{id + 1}...</span>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
};
