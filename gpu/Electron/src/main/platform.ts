import { app } from 'electron';
import { spawn, spawnSync } from 'child_process';

export const NOFILE_TARGET = 10240;
export const STREAM_STAGGER_MS = 20;
const V8_MAX_OLD_SPACE_MB = 8192;

function readNofile(): number {
  try {
    const result = spawnSync('/bin/sh', ['-c', 'ulimit -n'], { encoding: 'utf8' });
    const trimmed = (result.stdout || '').trim();
    if (trimmed === 'unlimited') {
      return NOFILE_TARGET;
    }
    const parsed = parseInt(trimmed, 10);
    return Number.isFinite(parsed) ? parsed : 0;
  } catch {
    return 0;
  }
}

/**
 * 30 RTSP sockets + FFmpeg pipes exceed macOS default RLIMIT_NOFILE (256).
 * Returns false when this process re-execs itself with a raised limit and should not boot.
 */
export function ensureFileDescriptorLimit(): boolean {
  if (process.platform === 'win32') {
    return true;
  }

  if (process.platform === 'linux') {
    const prlimit = spawnSync('prlimit', [`--pid=${process.pid}`, `--nofile=${NOFILE_TARGET}`], { encoding: 'utf8' });
    if (prlimit.status === 0) {
      console.log(`[Platform] Raised RLIMIT_NOFILE to ${NOFILE_TARGET} via prlimit`);
      return true;
    }
  }

  const current = readNofile();
  if (current >= NOFILE_TARGET || process.env.ELECTRON_NOFILE_RAISED === '1') {
    console.log(`[Platform] RLIMIT_NOFILE=${current > 0 ? current : 'unknown'}`);
    return true;
  }

  console.log(`[Platform] RLIMIT_NOFILE=${current}, re-exec with ulimit -n ${NOFILE_TARGET}`);
  const child = spawn(
    '/bin/sh',
    ['-c', `ulimit -n ${NOFILE_TARGET} 2>/dev/null || true; exec "$0" "$@"`, process.execPath, ...process.argv.slice(1)],
    {
      stdio: 'inherit',
      env: { ...process.env, ELECTRON_NOFILE_RAISED: '1' },
    },
  );
  child.on('exit', (code, signal) => {
    if (signal) {
      process.kill(process.pid, signal);
      return;
    }
    app.exit(code ?? 0);
  });
  return false;
}

export function applyChromiumFlags(): void {
  app.commandLine.appendSwitch('js-flags', `--max-old-space-size=${V8_MAX_OLD_SPACE_MB}`);

  if (process.platform === 'darwin') {
    app.commandLine.appendSwitch('ignore-gpu-blocklist');
    app.commandLine.appendSwitch('enable-gpu-rasterization');
    app.commandLine.appendSwitch('enable-zero-copy');
    app.commandLine.appendSwitch('enable-native-gpu-memory-buffers');
    app.commandLine.appendSwitch('enable-gpu-memory-buffer-video-frames');
    app.commandLine.appendSwitch('enable-accelerated-video-decode');
    app.commandLine.appendSwitch('disable-software-rasterizer');
    app.commandLine.appendSwitch('use-angle', 'metal');
    app.commandLine.appendSwitch(
      'enable-features',
      'Metal,CanvasOopRasterization,AcceleratedVideoDecodeMac,IOSurfaceCapturer',
    );
    console.log('[Platform] macOS: VideoToolbox decode + Metal ANGLE zero-copy');
    return;
  }

  if (process.platform === 'linux') {
    app.commandLine.appendSwitch('ignore-gpu-blocklist');
    app.commandLine.appendSwitch('enable-gpu-rasterization');
    app.commandLine.appendSwitch('enable-zero-copy');
    app.commandLine.appendSwitch('enable-features', 'VaapiVideoDecoder,VaapiVideoDecodeLinuxGL,VaapiOnNvidiaGPUs');
    app.commandLine.appendSwitch('use-gl', 'egl');
    app.commandLine.appendSwitch('disable-software-rasterizer');
    app.commandLine.appendSwitch('no-sandbox');
    app.commandLine.appendSwitch('disable-dev-shm-usage');
    console.log('[Platform] Linux: VA-API / NVDEC translation flags');
    return;
  }

  if (process.platform === 'win32') {
    app.commandLine.appendSwitch('ignore-gpu-blocklist');
    app.commandLine.appendSwitch('enable-gpu-rasterization');
    app.commandLine.appendSwitch('enable-zero-copy');
    app.commandLine.appendSwitch('enable-accelerated-video-decode');
    app.commandLine.appendSwitch('disable-frame-rate-limit');
    app.commandLine.appendSwitch('disable-gpu-vsync');
    app.commandLine.appendSwitch('use-angle', 'd3d11');
    app.commandLine.appendSwitch('enable-features', 'WebCodecs,D3D11VideoDecoder,CanvasOopRasterization');
    console.log('[Platform] Windows: D3D11 hardware decode');
  }
}
