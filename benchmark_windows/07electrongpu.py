#!/usr/bin/env python3
"""07electrongpu.py - Run Electron GPU (WebCodecs HW Decode) Benchmark.

Usage:
    python benchmark_windows/07electrongpu.py [--duration 60] [--phase1 30]
"""
import argparse
import platform
from pathlib import Path
from bench_utils import ROOT_DIR, execute_benchmark_session


def find_command(base_dir: Path) -> list[str]:
    # npm on Windows requires npm.cmd
    npm_cmd = "npm.cmd" if platform.system().lower() == "windows" else "npm"
    return [npm_cmd, "start"]


def main() -> None:
    parser = argparse.ArgumentParser(description="Run Electron GPU Benchmark (Headed UI Mode)")
    parser.add_argument("--duration", type=float, default=60.0, help="Total test duration in minutes (default: 60.0)")
    parser.add_argument("--phase1", type=float, default=30.0, help="Phase 1 steady-state minutes (default: 30.0)")
    parser.add_argument("--url", type=str, default="rtsp://127.0.0.1:8554/cam%d", help="RTSP target stream URL")
    parser.add_argument("--streams", type=int, default=30, help="Number of concurrent video tiles (default: 30)")
    parser.add_argument("--renderer", type=str, default="unified", choices=["unified", "webgpu", "decoupled"], help="Electron renderer mode (default: unified)")
    args = parser.parse_args()

    app_dir = ROOT_DIR / "gpu" / "Electron"
    cmd = find_command(app_dir)

    extra_env = {
        "RTSP_URL": args.url,
        "RTSP_URL_PATTERN": args.url if "%d" in args.url else "",
        "STREAM_COUNT": str(args.streams),
        "ELECTRON_ENABLE_LOGGING": "1",
        "ELECTRON_RENDERER": args.renderer,
    }

    execute_benchmark_session(
        framework="electron",
        hardware_mode="gpu",
        cmd=cmd,
        cwd=app_dir,
        total_minutes=args.duration,
        phase1_minutes=args.phase1,
        extra_env=extra_env,
    )


if __name__ == "__main__":
    main()
