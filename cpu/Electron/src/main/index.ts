import { app, BrowserWindow } from 'electron';
import path from 'path';
import { config } from './config';
import { applyChromiumFlags, ensureFileDescriptorLimit } from './platform';
import { StreamPool } from './rtsp-demuxer';
import { VideoWebSocketServer } from './ws-server';
import { telemetry } from './telemetry';

let mainWindow: BrowserWindow | null = null;
let streamPool: StreamPool | null = null;
let wsServer: VideoWebSocketServer | null = null;

if (!ensureFileDescriptorLimit()) {
  // Parent waits for the re-exec'd child with a raised RLIMIT_NOFILE.
} else {
  applyChromiumFlags();

  async function createWindow(): Promise<BrowserWindow> {
    const win = new BrowserWindow({
      width: 2560,
      height: 1440,
      minWidth: 1280,
      minHeight: 720,
      title: `RTSP 30-Stream Stress Test (Electron CPU Benchmark) [PID: ${process.pid}]`,
      backgroundColor: '#0f172a',
      webPreferences: {
        preload: path.join(__dirname, 'preload.js'),
        nodeIntegration: false,
        contextIsolation: true,
        sandbox: false,
        webSecurity: false,
      },
    });

    win.webContents.on('console-message', (e, level, msg, line, sourceId) => {
      console.log(`[Renderer Console] ${msg}`);
    });

    if (process.env.VITE_DEV_SERVER_URL) {
      await win.loadURL(process.env.VITE_DEV_SERVER_URL);
    } else {
      const indexPath = path.join(__dirname, '../renderer/index.html');
      await win.loadFile(indexPath);
    }

    win.maximize();
    win.show();
    win.focus();

    win.on('closed', () => {
      mainWindow = null;
    });

    return win;
  }

  async function bootstrap() {
    console.log(`[Main] Initializing RTSP 30-Stream CPU Benchmark...`);
    console.log(`[Main] Platform: ${process.platform}, Streams=${config.streamCount}, Mode=${config.hardwareMode}, RTSP=${config.rtspUrl}`);
    console.log(`[Main] Telemetry Log Path: ${telemetry.getLogPath()}`);

    streamPool = new StreamPool(config.streamCount);
    streamPool.startAll();

    wsServer = new VideoWebSocketServer(streamPool);
    await wsServer.listen();

    mainWindow = await createWindow();
  }

  app.whenReady().then(bootstrap);

  app.on('child-process-gone', (_event, details) => {
    console.warn(`[Main] Child process gone: type=${details.type} reason=${details.reason} exitCode=${details.exitCode} name=${details.name || ''}`);
  });

  app.on('window-all-closed', () => {
    if (process.platform !== 'darwin') {
      app.quit();
    }
  });

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      createWindow();
    }
  });

  app.on('before-quit', () => {
    console.log('[Main] Application shutting down. Cleaning up resources...');
    if (telemetry) {
      telemetry.flushToDisk();
    }
    if (streamPool) {
      streamPool.stopAll();
    }
    if (wsServer) {
      wsServer.close();
    }
  });
}
