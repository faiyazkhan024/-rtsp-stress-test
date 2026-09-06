import React, { useEffect, useRef, useImperativeHandle, forwardRef } from 'react';

export interface StreamFpsReport {
  streamId: number;
  fps: number;
  isConnected: boolean;
  lastDeltaMs: number;
  uiFrames: number;
  decodedFrames: number;
}

export interface VideoPlayerRef {
  getFpsAndReset: () => number;
  getReportAndReset: () => StreamFpsReport;
  updateFpsDisplay: (fps: number) => void;
}

interface VideoPlayerProps {
  streamId: number;
  wsPort: number;
}

type HwAccelPref = 'no-preference' | 'prefer-hardware' | 'prefer-software';

function isMacPlatform(): boolean {
  const preloadPlatform = (window as unknown as { electronBenchmark?: { platform?: string } }).electronBenchmark?.platform;
  if (preloadPlatform) return preloadPlatform === 'darwin';
  return /Mac/i.test(navigator.userAgent);
}

export const VideoPlayer = forwardRef<VideoPlayerRef, VideoPlayerProps>(({ streamId, wsPort }, ref) => {
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const fpsBadgeRef = useRef<HTMLSpanElement | null>(null);
  const statusDotRef = useRef<HTMLSpanElement | null>(null);
  const placeholderRef = useRef<HTMLDivElement | null>(null);

  // High performance mutable refs to decouple video rendering from React render cycle
  const frameCountRef = useRef<number>(0);
  const decodedCountRef = useRef<number>(0);
  const lastTickTimeRef = useRef<number>(performance.now());
  const lastPtsRef = useRef<number | null>(null);
  const lastPresentedTimeRef = useRef<number>(0);
  const lastDeltaMsRef = useRef<number>(0);
  const isConnectedRef = useRef<boolean>(false);
  const connectedSinceRef = useRef<number>(0);
  const pendingFramesRef = useRef<number>(0);
  const hasConfiguredRef = useRef<boolean>(false);
  const currentCodecRef = useRef<string>('');
  const decoderRef = useRef<VideoDecoder | null>(null);
  const wsRef = useRef<WebSocket | null>(null);
  const hwAccelRef = useRef<HwAccelPref>('prefer-hardware');
  const presentSizeRef = useRef({ width: 0, height: 0 });
  const isMac = isMacPlatform();

  // Canvas 2D rendering context ref for zero-copy low-latency presentation
  const ctxRef = useRef<CanvasRenderingContext2D | null>(null);

  useImperativeHandle(ref, () => ({
    getFpsAndReset: () => {
      const now = performance.now();
      const elapsedSec = (now - lastTickTimeRef.current) / 1000;
      lastTickTimeRef.current = now;
      const fps = elapsedSec > 0 ? Math.round(frameCountRef.current / elapsedSec) : 0;
      frameCountRef.current = 0;
      decodedCountRef.current = 0;
      return fps;
    },
    getReportAndReset: () => {
      const now = performance.now();
      const elapsedSec = (now - lastTickTimeRef.current) / 1000;
      lastTickTimeRef.current = now;
      const uiFrames = frameCountRef.current;
      const decodedFrames = decodedCountRef.current;
      const fps = elapsedSec > 0 ? Math.round(uiFrames / elapsedSec) : 0;
      frameCountRef.current = 0;
      decodedCountRef.current = 0;
      // Only mark connected if we've been receiving frames for at least 1s
      const connected = isConnectedRef.current
        && lastPresentedTimeRef.current > 0
        && now - lastPresentedTimeRef.current < 3000
        && connectedSinceRef.current > 0
        && now - connectedSinceRef.current >= 1000;
      return {
        streamId,
        fps,
        isConnected: connected,
        lastDeltaMs: lastDeltaMsRef.current,
        uiFrames,
        decodedFrames,
      };
    },
    updateFpsDisplay: (fps: number) => {
      // Direct DOM update: zero React re-renders for maximum V8 throughput
      if (fpsBadgeRef.current) {
        fpsBadgeRef.current.textContent = `${fps} FPS`;
        fpsBadgeRef.current.className = 'fps-badge ' + (
          fps >= 25 ? 'acceptable' : fps >= 20 ? 'warning' : 'unacceptable'
        );
      }
      if (statusDotRef.current) {
        statusDotRef.current.className = 'status-dot ' + (fps > 0 ? 'active' : 'waiting');
      }
      if (placeholderRef.current && fps > 0) {
        placeholderRef.current.style.display = 'none';
      }
    },
  }));

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;

    let isDestroyed = false;

    const ctx = canvas.getContext('2d', { alpha: false, desynchronized: true });
    if (!ctx) {
      console.error(`[Stream ${streamId}] Failed to acquire 2D context`);
      return;
    }
    ctxRef.current = ctx;
    ctx.imageSmoothingEnabled = false;

    const updatePresentSize = () => {
      const dpr = window.devicePixelRatio || 1;
      const cssW = Math.max(1, Math.round(canvas.clientWidth * dpr));
      const cssH = Math.max(1, Math.round(canvas.clientHeight * dpr));
      presentSizeRef.current = { width: cssW, height: cssH };
    };
    updatePresentSize();
    const resizeObserver = typeof ResizeObserver !== 'undefined'
      ? new ResizeObserver(updatePresentSize)
      : null;
    if (resizeObserver) {
      resizeObserver.observe(canvas.parentElement || canvas);
    }

    // Helper to find SPS in Annex B buffer and extract codec string (H.264 Level 5.0+ for 1440p)
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

    const onDecodedFrame = (videoFrame: VideoFrame) => {
      if (isDestroyed) {
        videoFrame.close();
        return;
      }

      decodedCountRef.current++;

      const curPts = videoFrame.timestamp;
      if (lastPtsRef.current !== null && curPts === lastPtsRef.current) {
        videoFrame.close();
        return;
      }
      lastPtsRef.current = curPts;

      const targetW = presentSizeRef.current.width > 0 ? presentSizeRef.current.width : videoFrame.displayWidth;
      const targetH = presentSizeRef.current.height > 0 ? presentSizeRef.current.height : videoFrame.displayHeight;

      if (canvas.width !== targetW || canvas.height !== targetH) {
        canvas.width = targetW;
        canvas.height = targetH;
        if (ctxRef.current) {
          ctxRef.current.imageSmoothingEnabled = false;
        }
      }

      try {
        if (ctxRef.current) {
          ctxRef.current.drawImage(videoFrame, 0, 0, targetW, targetH);
          const now = performance.now();
          if (lastPresentedTimeRef.current > 0) {
            lastDeltaMsRef.current = now - lastPresentedTimeRef.current;
          }
          lastPresentedTimeRef.current = now;
          if (!isConnectedRef.current) {
            connectedSinceRef.current = now;
          }
          isConnectedRef.current = true;
          frameCountRef.current++;
        }
      } catch (err) {
        console.warn(`[Stream ${streamId}] drawImage error:`, err);
      } finally {
        videoFrame.close();
      }

      if (placeholderRef.current && placeholderRef.current.style.display !== 'none') {
        placeholderRef.current.style.display = 'none';
      }
    };

    const createDecoder = (): VideoDecoder | null => {
      try {
        return new VideoDecoder({
          output: onDecodedFrame,
          error: (err) => {
            console.warn(`[Stream ${streamId}] VideoDecoder error:`, (err as any)?.name, (err as any)?.message || err);
            hasConfiguredRef.current = false;
            if (hwAccelRef.current === 'prefer-hardware') {
              hwAccelRef.current = 'no-preference';
              console.warn(`[Stream ${streamId}] Hardware decoder error, falling back to no-preference`);
            }
            if (decoderRef.current && decoderRef.current.state !== 'closed') {
              try {
                decoderRef.current.close();
              } catch (_) {}
            }
          },
        });
      } catch (err) {
        console.error(`[Stream ${streamId}] Failed to initialize VideoDecoder:`, err);
        return null;
      }
    };

    decoderRef.current = createDecoder();
    if (!decoderRef.current) {
      return;
    }

    // Connect to WebSocket stream
    const wsUrl = `ws://127.0.0.1:${wsPort}/stream/${streamId}`;
    const ws = new WebSocket(wsUrl);
    ws.binaryType = 'arraybuffer';
    wsRef.current = ws;

    ws.onopen = () => {
      if (statusDotRef.current) {
        statusDotRef.current.className = 'status-dot waiting';
      }
    };

    ws.onmessage = (event: MessageEvent) => {
      if (isDestroyed || !decoderRef.current) return;
      if (typeof event.data === 'string') return;

      const buffer = event.data as ArrayBuffer;
      if (buffer.byteLength < 10) return;

      const view = new DataView(buffer);
      const isKey = view.getUint8(0) === 1;
      const timestampUs = Number(view.getBigInt64(1));
      const nalData = new Uint8Array(buffer, 9);

      if (isKey) {
        const detectedCodec = extractSpsCodec(nalData) || 'avc1.42c032';
        if (!decoderRef.current || decoderRef.current.state === 'closed') {
          decoderRef.current = createDecoder();
          hasConfiguredRef.current = false;
        }

        const needsConfig = !hasConfiguredRef.current
          || currentCodecRef.current !== detectedCodec
          || (decoderRef.current && decoderRef.current.state !== 'configured');

        if (needsConfig && decoderRef.current) {
          try {
            decoderRef.current.configure({
              codec: detectedCodec,
              avc: { format: 'annexb' },
              hardwareAcceleration: hwAccelRef.current,
              optimizeForLatency: true,
            });
            hasConfiguredRef.current = true;
            currentCodecRef.current = detectedCodec;
          } catch (configErr) {
            console.warn(`[Stream ${streamId}] configure failed with ${hwAccelRef.current}:`, configErr);
            if (hwAccelRef.current !== 'no-preference') {
              hwAccelRef.current = 'no-preference';
              try {
                decoderRef.current.configure({
                  codec: detectedCodec,
                  avc: { format: 'annexb' },
                  hardwareAcceleration: 'no-preference',
                  optimizeForLatency: true,
                });
                hasConfiguredRef.current = true;
                currentCodecRef.current = detectedCodec;
              } catch (fallbackErr) {
                console.error(`[Stream ${streamId}] Fallback configure failed:`, fallbackErr);
              }
            }
          }
        }
      }

      // Can only decode if decoder has been configured with a keyframe
      if (!hasConfiguredRef.current || !decoderRef.current || decoderRef.current.state !== 'configured') {
        return;
      }

      if (!isKey && decoderRef.current.decodeQueueSize > 10) {
        return;
      }

      try {
        const chunk = new EncodedVideoChunk({
          type: isKey ? 'key' : 'delta',
          timestamp: timestampUs,
          data: nalData,
        });
        decoderRef.current.decode(chunk);
      } catch (decodeErr) {
        console.warn(`[Stream ${streamId}] decode error:`, decodeErr);
      }
    };

    ws.onclose = () => {
      isConnectedRef.current = false;
      connectedSinceRef.current = 0;
      if (!isDestroyed && statusDotRef.current) {
        statusDotRef.current.className = 'status-dot offline';
      }
    };

    return () => {
      isDestroyed = true;
      if (resizeObserver) {
        resizeObserver.disconnect();
      }
      if (wsRef.current) {
        wsRef.current.close();
        wsRef.current = null;
      }
      if (decoderRef.current) {
        if (decoderRef.current.state !== 'closed') {
          decoderRef.current.close();
        }
        decoderRef.current = null;
      }
    };
  }, [streamId, wsPort]);

  return (
    <div className="video-player-card">
      <div className="player-overlay">
        <span ref={statusDotRef} className="status-dot waiting" />
        <span className="stream-id-tag">CH-{String(streamId + 1).padStart(2, '0')}</span>
        <span ref={fpsBadgeRef} className="fps-badge warning">0 FPS</span>
      </div>
      <canvas ref={canvasRef} className="video-canvas" />
      <div ref={placeholderRef} className="waiting-placeholder">
        <span>Connecting CH-{streamId + 1}...</span>
      </div>
    </div>
  );
});

VideoPlayer.displayName = 'VideoPlayer';
