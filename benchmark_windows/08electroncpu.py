#!/usr/bin/env python3
"""08electroncpu.py - Run Electron CPU (Software Decode) Benchmark.

Usage:
    python benchmark_windows/08electroncpu.py [--duration 60] [--phase1 30]
"""
import argparse
import platform
from pathlib import Path
from bench_utils import ROOT_DIR, execute_benchmark_session


def find_command(base_dir: Path) -> list[str]:
    npm_cmd = "npm.cmd" if platform.system().lower() == "windows" else "npm"
    return [npm_cmd, "start"]


def main() -> None:
    parser = argparse.ArgumentParser(description="Run Electron CPU Benchmark (Headed UI Mode)")
    parser.add_argument("--duration", type=float, default=60.0, help="Total test duration in minutes (default: 60.0)")
    parser.add_argument("--phase1", type=float, default=30.0, help="Phase 1 steady-state minutes (default: 30.0)")
    parser.add_argument("--url", type=str, default="rtsp://127.0.0.1:8554/cam%d", help="RTSP target stream URL")
    parser.add_argument("--streams", type=int, default=30, help="Number of concurrent video tiles (default: 30)")
    args = parser.parse_args()

    app_dir = ROOT_DIR / "cpu" / "Electron"
    cmd = find_command(app_dir)

    extra_env = {
        "RTSP_URL": args.url,
        "RTSP_URL_PATTERN": args.url if "%d" in args.url else "",
        "STREAM_COUNT": str(args.streams),
        "ELECTRON_ENABLE_LOGGING": "1",
    }

    execute_benchmark_session(
        framework="electron",
        hardware_mode="cpu",
        cmd=cmd,
        cwd=app_dir,
        total_minutes=args.duration,
        phase1_minutes=args.phase1,
        extra_env=extra_env,
    )


if __name__ == "__main__":
    main()
