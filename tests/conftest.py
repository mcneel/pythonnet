# -*- coding: utf-8 -*-

"""Helpers for testing."""

import ctypes
import os
import sys
import sysconfig
from pathlib import Path
from subprocess import check_call
from tempfile import mkdtemp
import shutil

import pytest

# Add path for `Python.Test`
cwd = Path(__file__).parent
fixtures_path = cwd / "fixtures"
sys.path.append(str(fixtures_path))


def pytest_addoption(parser):
    parser.addoption(
        "--runtime",
        action="store",
        default="default",
        help="Must be one of default, coreclr, netfx and mono",
    )
    parser.addoption(
        "--perf",
        action="store_true",
        help="Run only the timing tests, against a Release build",
    )


collect_ignore = []


def pytest_configure(config):
    global bin_path
    config.addinivalue_line("markers", "perf: timing test, runs only with --perf")
    if "clr" in sys.modules:
        # Already loaded (e.g. by the C# test runner), skip build
        import clr

        clr.AddReference("Python.Test")
        return

    runtime_opt = config.getoption("runtime")
    if runtime_opt not in ["coreclr", "netfx", "mono", "default"]:
        raise RuntimeError(f"Invalid runtime: {runtime_opt}")

    test_proj_path = cwd.parent / "src" / "testing"
    bin_path = Path(mkdtemp())

    fw = "netstandard2.0"
    runtime_params = {}

    if runtime_opt == "coreclr":
        # This is optional now:
        #
        # fw = "net7.0"
        # runtime_params["runtime_config"] = str(
        #     bin_path / "Python.Test.runtimeconfig.json"
        # )
        collect_ignore.append("domain_tests/test_domain_reload.py")
    else:
        domain_tests_dir = cwd / "domain_tests"
        domain_bin_path = domain_tests_dir / "bin"
        build_cmd = [
            "dotnet",
            "build",
            str(domain_tests_dir),
            "-o",
            str(domain_bin_path),
        ]
        is_64bits = sys.maxsize > 2**32
        if not is_64bits:
            build_cmd += ["/p:Prefer32Bit=True"]
        check_call(build_cmd)

    publish_cmd = ["dotnet", "publish", "-f", fw, "-o", str(bin_path), str(test_proj_path)]
    if config.getoption("perf"):
        # Debug builds are several times slower and would skew the timings
        publish_cmd += ["-c", "Release"]
    check_call(publish_cmd)

    import os
    os.environ["PYTHONNET_RUNTIME"] = runtime_opt
    for k, v in runtime_params.items():
        os.environ[f"PYTHONNET_{runtime_opt.upper()}_{k.upper()}"] = v

    import clr

    sys.path.append(str(bin_path))
    clr.AddReference("Python.Test")


def pytest_collection_modifyitems(config, items):
    if config.getoption("perf"):
        # leave out everything else so the output is just the timings
        others = [item for item in items if item.get_closest_marker("perf") is None]
        if others:
            config.hook.pytest_deselected(items=others)
            items[:] = [item for item in items if item.get_closest_marker("perf") is not None]
        return
    skip = pytest.mark.skip(reason="timing test, run with --perf")
    for item in items:
        if item.get_closest_marker("perf") is not None:
            item.add_marker(skip)


_perf_results = {}


@pytest.fixture(scope="session")
def perf_results():
    """Collects nanoseconds per call by name, printed at the end of the run."""
    return _perf_results


def pytest_terminal_summary(terminalreporter):
    if not _perf_results:
        return
    terminalreporter.section("perf (ns per call, best of runs)")
    width = max(len(name) for name in _perf_results)
    for name, ns in _perf_results.items():
        terminalreporter.write_line(f"{name:<{width}}  {ns:8.0f}")


def pytest_unconfigure(config):
    global bin_path
    try:
        shutil.rmtree(bin_path)
    except Exception:
        pass


def pytest_report_header(config):
    """Generate extra report headers"""
    # FIXME: https://github.com/pytest-dev/pytest/issues/2257
    is_64bits = sys.maxsize > 2**32
    arch = "x64" if is_64bits else "x86"
    ucs = ctypes.sizeof(ctypes.c_wchar)
    libdir = sysconfig.get_config_var("LIBDIR")

    return f"Arch: {arch}, UCS: {ucs}, LIBDIR: {libdir}"


@pytest.fixture()
def filepath():
    """Returns full filepath for file in `fixtures` directory."""

    def make_filepath(filename):
        # http://stackoverflow.com/questions/18011902/parameter-to-a-fixture
        return os.path.join(fixtures_path, filename)

    return make_filepath
