#!/usr/bin/env python3
"""02CPPCPU.py - Run C++ Qt6 CPU (Software Decode) RTSP Benchmark.

Usage:
    python benchmark_windows/02CPPCPU.py [--duration 20] [--phase1 10]
"""
import argparse
import sys
from pathlib import Path
from bench_utils import ROOT_DIR, execute_benchmark_session


def find_binary(base_dir: Path) -> list[str]:
    candidates = [
        base_dir / "build" / "Release" / "rtsp-stress-test-cpp-cpu.exe",
        base_dir / "build" / "rtsp-stress-test-cpp-cpu.exe",
        base_dir / "build" / "rtsp-stress-test-cpp-cpu",
    ]
    for c in candidates:
        if c.exists():
            return [str(c)]
    # Fallback to CMake build command or direct binary name
    return [str(candidates[0])]


def main() -> None:
    parser = argparse.ArgumentParser(description="Run C++ Qt6 CPU Benchmark (Headed UI Mode)")
    parser.add_argument("--duration", type=float, default=20.0, help="Total test duration in minutes (default: 20.0)")
    parser.add_argument("--phase1", type=float, default=10.0, help="Phase 1 steady-state minutes (default: 10.0)")
    parser.add_argument("--url", type=str, default="rtsp://127.0.0.1:8554/cam%d", help="RTSP target stream URL")
    parser.add_argument("--streams", type=int, default=30, help="Number of concurrent video tiles (default: 30)")
    args = parser.parse_args()

    app_dir = ROOT_DIR / "cpu" / "CPP"
    base_bin = find_binary(app_dir)
    # Explicit visible UI desktop execution arguments
    cmd = base_bin + ["--url", args.url, "--streams", str(args.streams), "--log-dir", str(ROOT_DIR / "logs")]

    execute_benchmark_session(
        framework="cpp_qt6",
        hardware_mode="cpu",
        cmd=cmd,
        cwd=app_dir,
        total_minutes=args.duration,
        phase1_minutes=args.phase1,
    )


if __name__ == "__main__":
    main()
