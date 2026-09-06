import http from 'http';
import { WebSocketServer, WebSocket } from 'ws';
import { config } from './config';
import { StreamPool, StreamFrameEvent } from './rtsp-demuxer';
import { telemetry, FpsMetricsPayload } from './telemetry';

export class VideoWebSocketServer {
  private server: http.Server;
  private wss: WebSocketServer;
  private streamSockets: Map<number, Set<WebSocket>> = new Map();
  private controlSockets: Set<WebSocket> = new Set();
  private streamPool: StreamPool;

  constructor(streamPool: StreamPool) {
    this.streamPool = streamPool;
    this.server = http.createServer();
    this.wss = new WebSocketServer({ server: this.server });

    this.setupServer();
    this.setupStreamForwarding();
    this.setupTelemetryBroadcast();
  }

  private setupServer(): void {
    this.wss.on('connection', (ws: WebSocket, req: http.IncomingMessage) => {
      const url = req.url || '/';

      if (url === '/control') {
        this.handleControlConnection(ws);
        return;
      }

      const match = url.match(/^\/stream\/(\d+)$/);
      if (match) {
        const streamId = parseInt(match[1], 10);
        this.handleStreamConnection(streamId, ws);
        return;
      }

      ws.close(1008, 'Unknown endpoint');
    });
  }

  private handleControlConnection(ws: WebSocket): void {
    this.controlSockets.add(ws);

    // Send initial configuration to client
    const initMsg = JSON.stringify({
      type: 'init',
      data: {
        streamCount: config.streamCount,
        framework: config.framework,
        hardwareMode: config.hardwareMode,
        targetFps: config.targetFps,
        windowDurationSeconds: config.windowDurationSeconds,
        machineId: config.machineId,
        logPath: telemetry.getLogPath(),
      },
    });
    ws.send(initMsg);

    ws.on('message', (data: Buffer | string) => {
      try {
        const msg = JSON.parse(data.toString());
        if (msg.type === 'tick_fps') {
          // Record the 1-second tick into telemetry manager
          if (Array.isArray(msg.streamReports)) {
            telemetry.recordTick(msg.streamReports);
          } else if (Array.isArray(msg.streamFpsList)) {
            telemetry.recordTick(msg.streamFpsList);
          }
        }
      } catch (err) {
        console.error('[WSS Control] Failed to parse control message:', err);
      }
    });

    ws.on('close', () => {
      this.controlSockets.delete(ws);
    });
  }

  private handleStreamConnection(streamId: number, ws: WebSocket): void {
    if (!this.streamSockets.has(streamId)) {
      this.streamSockets.set(streamId, new Set());
    }
    this.streamSockets.get(streamId)!.add(ws);

    ws.on('close', () => {
      const set = this.streamSockets.get(streamId);
      if (set) {
        set.delete(ws);
        if (set.size === 0) {
          this.streamSockets.delete(streamId);
        }
      }
    });
  }

  private setupStreamForwarding(): void {
    // Listen for decoded frames from demuxers and forward to connected WebSockets
    for (const demuxer of this.streamPool.getAll()) {
      demuxer.on('frame', (frame: StreamFrameEvent) => {
        const clients = this.streamSockets.get(frame.streamId);
        if (!clients || clients.size === 0) return;

        // Binary frame payload format:
        // Byte 0: isKey (1 = keyframe/IDR/SPS, 0 = delta)
        // Bytes 1..8: timestampUs (BigInt64BE)
        // Bytes 9..end: raw Annex B NAL AU bytes
        const header = Buffer.alloc(9);
        header.writeUInt8(frame.isKey ? 1 : 0, 0);
        header.writeBigInt64BE(frame.timestampUs, 1);

        const packet = Buffer.concat([header, frame.data]);

        for (const client of clients) {
          if (client.readyState === WebSocket.OPEN) {
            // Drop delta frames if client socket buffer is saturated to prevent unbounded memory growth
            if (client.bufferedAmount > 2 * 1024 * 1024 && !frame.isKey) {
              continue;
            }
            client.send(packet, { binary: true });
          }
        }
      });
    }
  }

  private setupTelemetryBroadcast(): void {
    // Broadcast live telemetry updates to control sockets for the UI dashboard
    telemetry.onUpdate((payload: FpsMetricsPayload, currentWindowSec: number, streamFpsList: number[]) => {
      if (this.controlSockets.size === 0) return;

      const broadcastMsg = JSON.stringify({
        type: 'telemetry_tick',
        data: {
          payload,
          currentWindowSec,
          streamFpsList,
        },
      });

      for (const client of this.controlSockets) {
        if (client.readyState === WebSocket.OPEN) {
          client.send(broadcastMsg);
        }
      }
    });
  }

  public listen(): Promise<number> {
    return new Promise((resolve) => {
      this.server.listen(config.wsPort, '127.0.0.1', () => {
        const addr = this.server.address();
        const port = typeof addr === 'object' && addr ? addr.port : config.wsPort;
        console.log(`[WSS] WebSocket server listening on ws://127.0.0.1:${port}`);
        resolve(port);
      });
    });
  }

  public close(): void {
    for (const client of this.wss.clients) {
      client.close();
    }
    this.wss.close();
    this.server.close();
  }
}
