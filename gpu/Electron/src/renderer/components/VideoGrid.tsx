import React from 'react';
import { VideoPlayer, VideoPlayerRef } from './VideoPlayer';
import { UnifiedVideoGrid } from './UnifiedVideoGrid';

interface VideoGridProps {
  streamCount: number;
  wsPort: number;
  playerRefs: React.MutableRefObject<Map<number, VideoPlayerRef>>;
}

export const VideoGrid: React.FC<VideoGridProps> = ({ streamCount, wsPort, playerRefs }) => {
  const params = new URLSearchParams(window.location.search);
  const rendererMode = params.get('renderer')
    || (window as any).electronBenchmark?.renderer
    || 'decoupled';

  if (rendererMode === 'unified' || rendererMode === 'webgpu') {
    return (
      <UnifiedVideoGrid
        streamCount={streamCount}
        wsPort={wsPort}
        playerRefs={playerRefs}
      />
    );
  }

  const streamIndices = Array.from({ length: streamCount }, (_, i) => i);

  // Determine grid class based on stream count
  let gridClass = 'grid-30';
  if (streamCount === 1) gridClass = 'grid-1';
  else if (streamCount <= 4) gridClass = 'grid-4';
  else if (streamCount <= 9) gridClass = 'grid-9';
  else if (streamCount <= 16) gridClass = 'grid-16';

  return (
    <div className="video-grid-container">
      <div className={`video-grid ${gridClass}`}>
        {streamIndices.map((id) => (
          <VideoPlayer
            key={id}
            streamId={id}
            wsPort={wsPort}
            ref={(el) => {
              if (el) {
                playerRefs.current.set(id, el);
              } else {
                playerRefs.current.delete(id);
              }
            }}
          />
        ))}
      </div>
    </div>
  );
};
