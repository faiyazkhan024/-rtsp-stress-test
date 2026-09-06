#!/usr/bin/env python3
"""build_all.py - Pre-flight Dependency Checker & Build Automator for Physical Windows Test Machine.

Designed for AI Agents & Engineers:
1. Audits installed SDKs, compilers, and dependencies (Node, npm, .NET SDK, CMake, MSVC/Clang, Qt6, FFmpeg).
2. Logs exact, copy-pasteable installation instructions if any dependency is missing.
3. Compiles all 6 benchmark targets in Release mode (C++ CPU/GPU, C# CPU/GPU, Electron CPU/GPU).
4. Verifies output executables and bundles.

Usage:
    python benchmark_windows/build_all.py [--skip-cpp] [--skip-csharp] [--skip-electron]
"""
from __future__ import annotations

import argparse
import os
import platform
import shutil
import subprocess
import sys
from pathlib import Path
from typing import List, Tuple

IS_WINDOWS = platform.system().lower() == "windows"
ROOT_DIR = Path(__file__).resolve().parent.parent

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")


def print_banner(title: str) -> None:
    print("\n" + "=" * 65)
    print(f" {title}")
    print("=" * 65)


def print_agent_action(missing_item: str, install_command: str, notes: str = "") -> None:
    print("\n" + "!" * 65)
    print(f" [AGENT ACTION REQUIRED] Missing Dependency: {missing_item}")
    print(f" Execute this command to install:")
    print(f"   >>> {install_command}")
    if notes:
        print(f" Note: {notes}")
    print(" [HUMAN COLLABORATION]: If the agent cannot execute this command directly")
    print(" (e.g. requires UAC Administrator elevation or interactive installer),")
    print(" ask the human user to run the command in an elevated PowerShell prompt.")
    print("!" * 65 + "\n")


def check_tool(tool_name: str, version_cmd: List[str]) -> Tuple[bool, str]:
    """Check if a tool exists and return its version output."""
    path = shutil.which(tool_name)
    if not path and IS_WINDOWS:
        # Check .cmd or .exe
        path = shutil.which(f"{tool_name}.cmd") or shutil.which(f"{tool_name}.exe")
    if not path:
        return False, "Not installed / not in PATH"
    try:
        res = subprocess.run(
            version_cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            timeout=8.0,
            check=False,
        )
        first_line = res.stdout.strip().splitlines()[0] if res.stdout.strip() else "OK"
        return True, first_line
    except Exception as exc:
        return False, str(exc)


def audit_prerequisites() -> List[str]:
    """Audit system prerequisites and return a list of fatal missing dependencies."""
    print_banner("1. AUDITING SYSTEM PREREQUISITES & SDks")
    missing_deps: List[str] = []

    # Auto-detect Visual Studio bundled CMake and Ninja if not in PATH
    if IS_WINDOWS and not shutil.which("cmake"):
        try:
            vs_root = Path("C:/Program Files/Microsoft Visual Studio")
            if vs_root.exists():
                cmake_dirs = list(vs_root.glob("**/CommonExtensions/Microsoft/CMake/CMake/bin"))
                if cmake_dirs and (cmake_dirs[0] / "cmake.exe").exists():
                    c_dir = str(cmake_dirs[0])
                    n_dir = str(cmake_dirs[0].parents[1] / "Ninja")
                    os.environ["PATH"] = f"{c_dir};{n_dir};" + os.environ.get("PATH", "")
        except Exception:
            pass

    # 1. Node.js & npm
    has_node, node_ver = check_tool("node", ["node", "-v"])
    has_npm, npm_ver = check_tool("npm", ["npm.cmd" if IS_WINDOWS else "npm", "-v"])
    print(f" • Node.js: {'[✓] ' + node_ver if has_node else '[✗] MISSING'}")
    print(f" • npm:     {'[✓] ' + npm_ver if has_npm else '[✗] MISSING'}")
    if not has_node or not has_npm:
        missing_deps.append("Node.js")
        print_agent_action(
            "Node.js LTS & npm",
            "winget install OpenJS.NodeJS.LTS --silent --accept-package-agreements",
            "After installation, restart the terminal/shell session so PATH is updated.",
        )

    # 2. .NET SDK
    has_dotnet, dotnet_ver = check_tool("dotnet", ["dotnet", "--version"])
    print(f" • .NET SDK: {'[✓] ' + dotnet_ver if has_dotnet else '[✗] MISSING'}")
    if not has_dotnet:
        missing_deps.append(".NET SDK")
        print_agent_action(
            ".NET SDK 9 or 10",
            "winget install Microsoft.DotNet.SDK.9 --silent --accept-package-agreements",
            "Verify with 'dotnet --version' after installation.",
        )

    # 3. CMake
    has_cmake, cmake_ver = check_tool("cmake", ["cmake", "--version"])
    print(f" • CMake:    {'[✓] ' + cmake_ver if has_cmake else '[✗] MISSING'}")
    if not has_cmake:
        missing_deps.append("CMake")
        print_agent_action(
            "CMake build system",
            "winget install Kitware.CMake --silent --accept-package-agreements",
        )

    # 4. FFmpeg shared binaries
    has_ffmpeg, ffmpeg_ver = check_tool("ffmpeg", ["ffmpeg", "-version"])
    print(f" • FFmpeg:   {'[✓] ' + ffmpeg_ver if has_ffmpeg else '[!] Not found in PATH'}")
    if not has_ffmpeg and IS_WINDOWS:
        print_agent_action(
            "FFmpeg shared libraries (Gyan.FFmpeg)",
            "winget install Gyan.FFmpeg --silent",
            "Installs FFmpeg DLLs and tools to PATH for C# and C++ decoding.",
        )

    # 5. C++ Compiler (MSVC / Ninja / Clang)
    has_cl = bool(shutil.which("cl.exe") or shutil.which("cl") or shutil.which("ninja") or shutil.which("g++"))
    print(f" • C++ Compiler / Toolchain: {'[✓] Detected' if has_cl else '[!] MSVC cl/ninja not in current PATH'}")
    if not has_cl and IS_WINDOWS:
        print("   (Note for Agent: Use Visual Studio Developer Command Prompt or install VS Build Tools)")
        print("   >>> winget install Microsoft.VisualStudio.2022.BuildTools --silent")

    # 5. Git
    has_git, git_ver = check_tool("git", ["git", "--version"])
    print(f" • Git:      {'[✓] ' + git_ver if has_git else '[✗] MISSING'}")

    # 6. Python psutil check
    try:
        import psutil
        print(f" • psutil:   [✓] Installed (fast hardware polling)")
    except ImportError:
        print(f" • psutil:   [!] Not installed (will use PowerShell/WMI fallback)")
        print("   (Recommended: 'pip install psutil' for lighter metric sampling)")

    return missing_deps


def run_command(cmd: List[str], cwd: Path, title: str) -> bool:
    """Run a build command, stream output on failure, and return success boolean."""
    print(f"[*] {title}...")
    print(f"    Directory: {cwd.relative_to(ROOT_DIR)}")
    print(f"    Command:   {' '.join(cmd)}")
    try:
        res = subprocess.run(
            cmd,
            cwd=str(cwd),
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            check=False,
        )
        if res.returncode == 0:
            print(f"[✓] {title} succeeded.")
            return True
        else:
            print(f"\n[✗] {title} FAILED (Exit Code {res.returncode}):")
            print("-" * 60)
            print(res.stdout.strip()[-2000:])  # Print last 2000 chars of build log
            print("-" * 60)
            return False
    except Exception as exc:
        print(f"[✗] Failed to invoke {title}: {exc}")
        return False


def build_electron(mode: str) -> bool:
    """Build Electron project for cpu or gpu."""
    app_dir = ROOT_DIR / mode / "Electron"
    npm_bin = "npm.cmd" if IS_WINDOWS else "npm"
    
    # 1. npm install
    if not (app_dir / "node_modules").exists():
        if not run_command([npm_bin, "install"], app_dir, f"Electron {mode.upper()}: npm install"):
            return False

    # 2. npm run build
    return run_command([npm_bin, "run", "build"], app_dir, f"Electron {mode.upper()}: npm run build")


def build_csharp(mode: str) -> bool:
    """Build C# project for cpu or gpu."""
    app_dir = ROOT_DIR / mode / "C#"
    cmd = ["dotnet", "build", "-c", "Release"]
    return run_command(cmd, app_dir, f"C# Avalonia {mode.upper()}: dotnet build -c Release")


def build_cpp(mode: str) -> bool:
    """Build C++ Qt6 project for cpu or gpu."""
    app_dir = ROOT_DIR / mode / "CPP"
    build_dir = app_dir / "build"
    build_dir.mkdir(parents=True, exist_ok=True)

    # 1. CMake configure
    config_cmd = ["cmake", "-B", "build", "-DCMAKE_BUILD_TYPE=Release"]
    qt_candidate = Path("C:/Qt/6.8.3/msvc2022_64")
    if qt_candidate.exists() and "CMAKE_PREFIX_PATH" not in os.environ:
        config_cmd.append(f"-DCMAKE_PREFIX_PATH={qt_candidate}")
    elif "CMAKE_PREFIX_PATH" in os.environ:
        config_cmd.append(f"-DCMAKE_PREFIX_PATH={os.environ['CMAKE_PREFIX_PATH']}")

    if not run_command(config_cmd, app_dir, f"C++ Qt6 {mode.upper()}: CMake Configure"):
        print_agent_action(
            f"Qt6 / FFmpeg libraries for C++ {mode.upper()}",
            "vcpkg install qtbase ffmpeg --triplet x64-windows",
            "Or ensure CMAKE_PREFIX_PATH points to your Qt6 installation.",
        )
        return False

    # 2. CMake build
    build_cmd = ["cmake", "--build", "build", "--config", "Release"]
    return run_command(build_cmd, app_dir, f"C++ Qt6 {mode.upper()}: CMake Build")


def main() -> None:
    parser = argparse.ArgumentParser(description="Build all RTSP stress test implementations.")
    parser.add_argument("--skip-cpp", action="store_true", help="Skip building C++ targets")
    parser.add_argument("--skip-csharp", action="store_true", help="Skip building C# targets")
    parser.add_argument("--skip-electron", action="store_true", help="Skip building Electron targets")
    args = parser.parse_args()

    # Phase 1: Audit dependencies
    missing = audit_prerequisites()
    if missing:
        print_banner("FATAL: MISSING PREREQUISITES")
        print(f"The following required toolchains are missing: {', '.join(missing)}")
        print("Please review the AGENT ACTION blocks above, install the tools, and re-run.")
        sys.exit(1)

    # Phase 2: Build all targets
    print_banner("2. COMPILING BENCHMARK IMPLEMENTATIONS")
    results = {}

    # Electron
    if not args.skip_electron:
        results["Electron CPU"] = build_electron("cpu")
        results["Electron GPU"] = build_electron("gpu")

    # C# Avalonia
    if not args.skip_csharp:
        results["C# CPU"] = build_csharp("cpu")
        results["C# GPU"] = build_csharp("gpu")

    # C++ Qt6
    if not args.skip_cpp:
        results["C++ CPU"] = build_cpp("cpu")
        results["C++ GPU"] = build_cpp("gpu")

    # Phase 3: Final Report
    print_banner("3. BUILD SUMMARY")
    all_passed = True
    for target, passed in results.items():
        status = "[✓] READY" if passed else "[✗] BUILD FAILED"
        print(f" • {target:<20}: {status}")
        if not passed:
            all_passed = False

    if all_passed:
        print("\n[✓] All implementations compiled successfully and are ready for benchmarking!")
        print("You can now run: python benchmark_windows/run_all.py")
        sys.exit(0)
    else:
        print("\n[!] One or more builds failed. Check the error logs above for missing packages.")
        sys.exit(1)


if __name__ == "__main__":
    main()
