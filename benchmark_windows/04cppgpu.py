#!/usr/bin/env python3
"""04cppgpu.py - Run C++ Qt6 GPU (Hardware Zero-Copy Decode) RTSP Benchmark.

Usage:
    python benchmark_windows/04cppgpu.py [--duration 20] [--phase1 10]
"""
import argparse
import sys
from pathlib import Path
from bench_utils import ROOT_DIR, execute_benchmark_session


def find_binary(base_dir: Path) -> list[str]:
    candidates = [
        base_dir / "build" / "Release" / "rtsp-stress-test-cpp-gpu.exe",
        base_dir / "build" / "rtsp-stress-test-cpp-gpu",
        base_dir / "build" / "rtsp-stress-test-cpp-gpu",
    ]
    for c in candidates:
        if c.exists():
            return [str(c)]
    return [str(candidates[0])]


def main() -> None:
    parser = argparse.ArgumentParser(description="Run C++ Qt6 GPU Benchmark (Headed UI Mode)")
    parser.add_argument("--duration", type=float, default=20.0, help="Total test duration in minutes (default: 20.0)")
    parser.add_argument("--phase1", type=float, default=10.0, help="Phase 1 steady-state minutes (default: 10.0)")
    parser.add_argument("--url", type=str, default="rtsp://127.0.0.1:8554/cam%d", help="RTSP target stream URL")
    parser.add_argument("--streams", type=int, default=30, help="Number of concurrent video tiles (default: 30)")
    parser.add_argument("--hw-accel", type=str, default="auto", help="Hardware acceleration backend (default: auto)")
    args = parser.parse_args()

    app_dir = ROOT_DIR / "gpu" / "CPP"
    base_bin = find_binary(app_dir)
    cmd = base_bin + [
        "--url", args.url,
        "--streams", str(args.streams),
        "--hw-accel", args.hw_accel,
        "--log-dir", str(ROOT_DIR / "logs"),
    ]

    execute_benchmark_session(
        framework="cpp_qt6",
        hardware_mode="gpu",
        cmd=cmd,
        cwd=app_dir,
        total_minutes=args.duration,
        phase1_minutes=args.phase1,
    )


if __name__ == "__main__":
    main()
