#!/usr/bin/env python3
"""06csharpcpu.py - Run C# Avalonia CPU (Software Decode) Benchmark.

Usage:
    python benchmark_windows/06csharpcpu.py [--duration 60] [--phase1 30]
"""
import argparse
from pathlib import Path
from bench_utils import ROOT_DIR, execute_benchmark_session


def find_command(base_dir: Path) -> list[str]:
    for pattern in (
        "bin/Release/**/rtsp-stress-test-csharp-cpu.exe",
        "bin/Release/**/rtsp-stress-test-csharp-cpu",
        "bin/**/rtsp-stress-test-csharp-cpu.exe",
        "bin/**/rtsp-stress-test-csharp-cpu",
    ):
        hits = [
            p for p in base_dir.glob(pattern)
            if p.is_file() and p.suffix in ("", ".exe")
        ]
        if hits:
            return [str(hits[0])]
    return ["dotnet", "run", "-c", "Release"]


def main() -> None:
    parser = argparse.ArgumentParser(description="Run C# Avalonia CPU Benchmark (Headed UI Mode)")
    parser.add_argument("--duration", type=float, default=60.0, help="Total test duration in minutes (default: 60.0)")
    parser.add_argument("--phase1", type=float, default=30.0, help="Phase 1 steady-state minutes (default: 30.0)")
    parser.add_argument("--url", type=str, default="rtsp://127.0.0.1:8554/cam%d", help="RTSP target stream URL")
    parser.add_argument("--streams", type=int, default=30, help="Number of concurrent video tiles (default: 30)")
    args = parser.parse_args()

    app_dir = ROOT_DIR / "cpu" / "C#"
    base_cmd = find_command(app_dir)
    extra_args = [
        "--url", args.url,
        "--streams", str(args.streams),
        "--log-dir", str(ROOT_DIR / "logs"),
    ]
    # If using dotnet run, separate with --
    cmd = base_cmd + (["--"] + extra_args if "run" in base_cmd else extra_args)

    execute_benchmark_session(
        framework="csharp_avalonia",
        hardware_mode="cpu",
        cmd=cmd,
        cwd=app_dir,
        total_minutes=args.duration,
        phase1_minutes=args.phase1,
    )


if __name__ == "__main__":
    main()
