import React from 'react';

export interface TelemetrySummary {
  machineId: string;
  framework: string;
  hardwareMode: string;
  activeStreams: number;
  currentWindowSec: number;
  acceptable25to30: number;
  acceptable20to24: number;
  unacceptable10to19: number;
  unacceptable5to9: number;
  unacceptableUnder5: number;
  avgFps: number;
  logPath: string;
}

interface StatsBarProps {
  stats: TelemetrySummary;
}

export const StatsBar: React.FC<StatsBarProps> = ({ stats }) => {
  const windowProgressPercent = Math.min(100, Math.round((stats.currentWindowSec / 60) * 100));
  const acceptableTotal = stats.acceptable25to30 + stats.acceptable20to24;
  const unacceptableTotal = stats.unacceptable10to19 + stats.unacceptable5to9 + stats.unacceptableUnder5;
  const totalStreamSeconds = acceptableTotal + unacceptableTotal;
  const acceptableRatio = totalStreamSeconds > 0
    ? ((acceptableTotal / totalStreamSeconds) * 100).toFixed(1)
    : '100.0';

  return (
    <div className="stats-bar">
      <div className="stats-title-group">
        <span className="benchmark-badge">Electron</span>
        <span className="hardware-badge-gpu">GPU Zero-Copy</span>
        <span className="stats-title">{stats.activeStreams} RTSP Streams Grid (2560×1440 1440p)</span>
      </div>

      <div className="metrics-row">
        <div className="metric-card">
          <span className="metric-label">Avg FPS</span>
          <span className={`metric-value ${stats.avgFps >= 24 ? 'green' : stats.avgFps >= 18 ? 'yellow' : 'red'}`}>
            {stats.avgFps.toFixed(1)}
          </span>
        </div>

        <div className="metric-card">
          <span className="metric-label">25-30 FPS</span>
          <span className="metric-value green">{stats.acceptable25to30}</span>
        </div>

        <div className="metric-card">
          <span className="metric-label">20-24 FPS</span>
          <span className="metric-value yellow">{stats.acceptable20to24}</span>
        </div>

        <div className="metric-card">
          <span className="metric-label">&lt;20 FPS</span>
          <span className={`metric-value ${unacceptableTotal > 0 ? 'red' : 'green'}`}>
            {unacceptableTotal}
          </span>
        </div>

        <div className="metric-card">
          <span className="metric-label">Acceptable</span>
          <span className={`metric-value ${parseFloat(acceptableRatio) >= 95 ? 'green' : 'red'}`}>
            {acceptableRatio}%
          </span>
        </div>

        <div className="countdown-bar-container">
          <span className="countdown-text">Flush: {60 - stats.currentWindowSec}s</span>
          <div className="countdown-bar">
            <div
              className="countdown-bar-fill"
              style={{ width: `${windowProgressPercent}%` }}
            />
          </div>
        </div>
      </div>
    </div>
  );
};
