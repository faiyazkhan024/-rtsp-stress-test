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
  pendingFrame: VideoFrame | null;
}

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

  // DOM ref maps for UI badges (always safe across renders)
  const fpsBadgesRef = useRef<Map<number, HTMLSpanElement>>(new Map());
  const statusDotsRef = useRef<Map<number, HTMLSpanElement>>(new Map());
  const placeholdersRef = useRef<Map<number, HTMLDivElement>>(new Map());

  const { cols, rows } = getGridDimensions(streamCount);
  const streamIndices = Array.from({ length: streamCount }, (_, i) => i);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;

    let isDestroyed = false;
    let rafId: number | null = null;

    // Single 2D hardware-accelerated context (Skia D3D11 OOP rasterization)
    const ctx = canvas.getContext('2d', { alpha: false });
    if (!ctx) {
      console.error('[UnifiedVideoGrid] Failed to acquire 2D canvas context');
      return;
    }
    ctx.imageSmoothingEnabled = false;

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
      pendingFrame: null,
    }));

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
          const badge = fpsBadgesRef.current.get(stream.streamId);
          if (badge) {
            badge.textContent = `${fps} FPS`;
            badge.className = 'fps-badge ' + (
              fps >= 25 ? 'acceptable' : fps >= 20 ? 'warning' : 'unacceptable'
            );
          }
          const dot = statusDotsRef.current.get(stream.streamId);
          if (dot) {
            dot.className = 'status-dot ' + (fps > 0 ? 'active' : 'waiting');
          }
          const ph = placeholdersRef.current.get(stream.streamId);
          if (ph && fps > 0) {
            ph.style.display = 'none';
          }
        },
      });
    });

    // Helper to extract codec string from SPS
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
        const dot = statusDotsRef.current.get(stream.streamId);
        if (dot) dot.className = 'status-dot offline';
      };
    });

    const updateCanvasSize = () => {
      const dpr = window.devicePixelRatio || 1;
      const w = Math.max(1, Math.round(canvas.clientWidth * dpr));
      const h = Math.max(1, Math.round(canvas.clientHeight * dpr));
      if (canvas.width !== w || canvas.height !== h) {
        canvas.width = w;
        canvas.height = h;
        ctx.imageSmoothingEnabled = false;
      }
    };
    updateCanvasSize();

    const resizeObserver = typeof ResizeObserver !== 'undefined'
      ? new ResizeObserver(updateCanvasSize)
      : null;
    if (resizeObserver && containerRef.current) {
      resizeObserver.observe(containerRef.current);
    }

    console.log('[UnifiedVideoGrid] Single-Canvas 2D Hardware-Accelerated Pipeline running');

    // Single master render loop tied to monitor VSync
    const renderLoop = () => {
      if (isDestroyed) return;
      const now = performance.now();

      const dpr = window.devicePixelRatio || 1;
      const pad = 8 * dpr;
      const gap = 6 * dpr;
      const totalW = Math.max(1, canvas.width);
      const totalH = Math.max(1, canvas.height);
      const tileW = Math.max(1, (totalW - 2 * pad - (cols - 1) * gap) / cols);
      const tileH = Math.max(1, (totalH - 2 * pad - (rows - 1) * gap) / rows);

      for (let i = 0; i < streamCount; i++) {
        const stream = streams[i];
        const frame = stream.pendingFrame;
        if (frame) {
          stream.pendingFrame = null;
          const col = i % cols;
          const row = Math.floor(i / cols);
          const x = pad + col * (tileW + gap);
          const y = pad + row * (tileH + gap);

          try {
            ctx.drawImage(frame, x, y, tileW, tileH);
            if (stream.lastPresentedTime > 0) {
              stream.lastDeltaMs = now - stream.lastPresentedTime;
            }
            stream.lastPresentedTime = now;
            if (!stream.isConnected) {
              stream.connectedSince = now;
            }
            stream.isConnected = true;
            stream.frameCount++;

            const ph = placeholdersRef.current.get(stream.streamId);
            if (ph && ph.style.display !== 'none') {
              ph.style.display = 'none';
            }
          } catch (err) {
            console.warn(`[Unified Stream ${stream.streamId}] drawImage error:`, err);
          } finally {
            frame.close();
          }
        }
      }

      rafId = requestAnimationFrame(renderLoop);
    };

    rafId = requestAnimationFrame(renderLoop);

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
                  if (el) statusDotsRef.current.set(id, el);
                  else statusDotsRef.current.delete(id);
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
                  if (el) fpsBadgesRef.current.set(id, el);
                  else fpsBadgesRef.current.delete(id);
                }}
                className="fps-badge warning"
              >
                0 FPS
              </span>
            </div>
            <div
              ref={(el) => {
                if (el) placeholdersRef.current.set(id, el);
                else placeholdersRef.current.delete(id);
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
