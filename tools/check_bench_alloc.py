#!/usr/bin/env python3
"""CI gate: fail if any BenchmarkDotNet benchmark allocates (BytesAllocatedPerOperation > 0).

Usage: python3 tools/check_bench_alloc.py BenchmarkDotNet.Artifacts/results
Reads every *-report-full*.json (BDN JsonExporter) / *-report*.json in the folder. Stdlib only.
"""
import json
import pathlib
import sys


def main() -> int:
    folder = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "BenchmarkDotNet.Artifacts/results")
    reports = sorted(folder.glob("*-report-full*.json")) or sorted(folder.glob("*-report*.json"))
    if not reports:
        print(f"no BenchmarkDotNet JSON reports in {folder}")
        return 2

    checked = 0
    offenders = []
    for report in reports:
        data = json.loads(report.read_text(encoding="utf-8"))
        for b in data.get("Benchmarks", []):
            name = b.get("FullName") or b.get("DisplayInfo") or b.get("Method", "?")
            memory = b.get("Memory") or {}
            allocated = memory.get("BytesAllocatedPerOperation")
            if allocated is None:
                offenders.append((name, "no memory data — is [MemoryDiagnoser] missing?"))
                continue
            checked += 1
            if allocated > 0:
                offenders.append((name, f"{allocated} B/op"))

    for name, why in offenders:
        print(f"FAIL {name}: {why}")
    print(f"checked {checked} benchmark(s) in {len(reports)} report(s); {len(offenders)} offender(s)")
    return 1 if offenders or checked == 0 else 0


if __name__ == "__main__":
    sys.exit(main())
