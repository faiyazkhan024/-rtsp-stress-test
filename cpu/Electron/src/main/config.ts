import os from 'os';
import path from 'path';
import dotenv from 'dotenv';

dotenv.config();

export interface BenchmarkConfig {
  streamCount: number;
  rtspUrl: string;
  rtspUrlPattern?: string;
  wsPort: number;
  logDir: string;
  fpsLogPath: string;
  machineId: string;
  framework: string;
  hardwareMode: string;
  windowDurationSeconds: number;
  videoWidth: number;
  videoHeight: number;
  targetFps: number;
  isHeadless: boolean;
}

const defaultLogDir = process.platform === 'linux' ? '/var/log/benchmark' : path.resolve(process.cwd(), 'logs');
const configuredLogDir = process.env.BENCHMARK_LOG_DIR || defaultLogDir;

export const config: BenchmarkConfig = {
  streamCount: parseInt(process.env.STREAM_COUNT || '30', 10),
  rtspUrl: process.env.RTSP_URL || 'rtsp://127.0.0.1:8554/live',
  rtspUrlPattern: process.env.RTSP_URL_PATTERN, // e.g. "rtsp://127.0.0.1:8554/stream%d"
  wsPort: parseInt(process.env.WS_PORT || '9999', 10),
  logDir: configuredLogDir,
  fpsLogPath: process.env.FPS_METRICS_LOG_PATH || path.join(configuredLogDir, 'fps_metrics.log'),
  machineId: process.env.MACHINE_ID || os.hostname() || 'c7i-8xlarge-node-1',
  framework: 'electron',
  hardwareMode: 'cpu',
  windowDurationSeconds: 60,
  videoWidth: 2560,
  videoHeight: 1440,
  targetFps: 25,
  isHeadless: resolveIsHeadless(),
};

function resolveIsHeadless(): boolean {
  if (process.env.BENCHMARK_HEADLESS === '1') return true;
  if (process.env.BENCHMARK_HEADLESS === '0') return false;
  if (process.platform === 'darwin' || process.platform === 'win32') return false;
  return !process.env.DISPLAY;
}

export function getRtspUrlForStream(index: number): string {
  const pattern = config.rtspUrlPattern || (config.rtspUrl && config.rtspUrl.includes('%d') ? config.rtspUrl : undefined);
  if (pattern) {
    return pattern.replace('%d', index.toString());
  }
  return config.rtspUrl;
}

