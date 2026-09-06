#!/usr/bin/env python3
"""run_all.py - Master Orchestrator for Physical Windows RTSP Benchmark Suite.

Executes the complete test sequence with automated idle settling between every run:
  1. 01baseline.py
  2. 02CPPCPU.py
  3. 03pausetillidealagain.py
  4. 04cppgpu.py
  5. 03pausetillidealagain.py
  6. 05csharpgpu.py
  7. 03pausetillidealagain.py
  8. 06csharpcpu.py
  9. 03pausetillidealagain.py
 10. 07electrongpu.py
 11. 03pausetillidealagain.py
 12. 08electroncpu.py
 13. 03pausetillidealagain.py

Usage:
    python benchmark_windows/run_all.py [--cool-mins 5] [--quick-test]
"""
import argparse
import subprocess
import sys
import time
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
ROOT_DIR = SCRIPT_DIR.parent
LOG_DIR = ROOT_DIR / "logs"


def run_step(script_name: str, extra_args: list[str] = None) -> bool:
    cmd = [sys.executable, str(SCRIPT_DIR / script_name)]
    if extra_args:
        cmd.extend(extra_args)
    print(f"\n>>> EXECUTING: {' '.join(cmd)}")
    res = subprocess.run(cmd, cwd=str(ROOT_DIR))
    return res.returncode == 0


def main() -> None:
    parser = argparse.ArgumentParser(description="Master RTSP Benchmark Runner with Idle Stabilization (Headed UI)")
    parser.add_argument("--cool-mins", type=float, default=5.0, help="Cool-down minutes between runs (default: 5.0)")
    parser.add_argument("--quick-test", action="store_true", help="Dry run: runs each test for 1 minute to verify setup")
    parser.add_argument("--url", type=str, default="rtsp://127.0.0.1:8554/cam%d", help="RTSP target stream URL")
    parser.add_argument("--streams", type=int, default=30, help="Number of concurrent video tiles (default: 30)")
    parser.add_argument("--duration", type=float, default=None, help="Custom total test duration in minutes (e.g. 30)")
    parser.add_argument("--phase1", type=float, default=None, help="Custom Phase 1 steady-state minutes (e.g. 15)")
    parser.add_argument("--only", "--targets", type=str, default=None, dest="only", help="Comma-separated script keywords to run (e.g. csharp,electron)")
    parser.add_argument("--start-from", type=str, default=None, help="Start from a specific script (e.g. 05csharpgpu.py or csharp)")
    args = parser.parse_args()

    common_ui_args = ["--url", args.url, "--streams", str(args.streams)]

    # Configure duration overrides
    dur_args = []
    if args.quick_test:
        dur_args = ["--duration", "1.25", "--phase1", "0.5"]
    elif args.duration is not None:
        p1 = args.phase1 if args.phase1 is not None else (args.duration / 2.0)
        dur_args = ["--duration", str(args.duration), "--phase1", str(p1)]

    c_args = dur_args + common_ui_args
    net_args = dur_args + common_ui_args
    pause_args = ["--min-cool-mins", "0.2" if args.quick_test else str(args.cool_mins)]

    print("\n" + "#" * 60)
    print(" STARTING WINDOWS BENCHMARK WORKLOAD SUITE")
    if args.quick_test:
        mode_str = "DRY RUN QUICK TEST (1 min/run)"
    elif args.duration:
        mode_str = f"CUSTOM DURATION ({args.duration}m total: {args.phase1 or args.duration/2}m steady + {args.duration - (args.phase1 or args.duration/2)}m churn)"
    else:
        mode_str = "FULL PRODUCTION RUN (HEADED UI)"
    print(f" Mode: {mode_str}")
    print(f" RTSP Target: {args.url} ({args.streams} streams)")
    print(f" Inter-run cooldown: {args.cool_mins} minutes")
    if args.only:
        print(f" Filtered targets: {args.only}")
    print("#" * 60 + "\n")

    all_benchmarks = [
        ("02CPPCPU.py", c_args),
        ("04cppgpu.py", c_args),
        ("05csharpgpu.py", net_args),
        ("06csharpcpu.py", net_args),
        ("07electrongpu.py", net_args),
        ("08electroncpu.py", net_args),
    ]

    if args.only:
        filters = [f.strip().lower() for f in args.only.split(",") if f.strip()]
        selected_benchmarks = [
            (s, a) for (s, a) in all_benchmarks
            if any(f in s.lower() or f.replace("_", "") in s.lower() for f in filters)
        ]
    else:
        selected_benchmarks = all_benchmarks

    steps = [
        ("00start_rtsp_server.py", [], False),
        ("01baseline.py", [], False),
    ]
    for s, a in selected_benchmarks:
        steps.append((s, a, False))
        steps.append(("03pausetillidealagain.py", pause_args, True))

    if args.start_from:
        target = args.start_from.lower()
        start_idx = None
        for i, (script, _, _) in enumerate(steps):
            if target in script.lower():
                start_idx = i
                break
        if start_idx is not None:
            steps = steps[start_idx:]
            print(f"[*] Resuming sequence from step: {steps[0][0]}")
        else:
            print(f"[!] Warning: Step matching '{args.start_from}' not found. Running all steps.")

    total_steps = len(steps)
    start_all = time.time()

    for idx, (script, s_args, is_pause) in enumerate(steps, 1):
        if is_pause and args.cool_mins <= 0:
            print(f"\n[STEP {idx}/{total_steps}] skip cooldown (--cool-mins {args.cool_mins})")
            continue
        print(f"\n[STEP {idx}/{total_steps}] ------------------------------------------")
        success = run_step(script, s_args)
        if not success:
            print(f"[!] Warning: Step {script} returned non-zero code.")

    total_time = round((time.time() - start_all) / 60.0, 1)
    print("\n" + "=" * 60)
    print(f" ALL BENCHMARK RUNS COMPLETED IN {total_time} MINUTES!")
    print(f" Archived logs stored at: {LOG_DIR / 'archive'}")
    print("=" * 60 + "\n")


if __name__ == "__main__":
    main()
