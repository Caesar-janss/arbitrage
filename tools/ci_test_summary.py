#!/usr/bin/env python3
"""CI: summarize .trx results as a GitHub annotation + job summary, and FAIL if no test ran.

Usage: python3 tools/ci_test_summary.py TestResults [min_expected]
`dotnet test` exits 0 when zero tests are discovered; this guard turns that into a failure.
Stdlib only.
"""
import os
import pathlib
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def main() -> int:
    folder = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "TestResults")
    min_expected = int(sys.argv[2]) if len(sys.argv) > 2 else 1
    totals = {"total": 0, "executed": 0, "passed": 0, "failed": 0, "notExecuted": 0}
    failed_names = []
    files = sorted(folder.rglob("*.trx"))
    for trx in files:
        root = ET.parse(trx).getroot()
        counters = root.find(".//t:ResultSummary/t:Counters", NS)
        if counters is not None:
            for k in totals:
                totals[k] += int(counters.get(k, "0"))
        for r in root.findall(".//t:UnitTestResult", NS):
            if r.get("outcome") == "Failed":
                failed_names.append(r.get("testName", "?"))

    line = (f"trx={len(files)} total={totals['total']} executed={totals['executed']} "
            f"passed={totals['passed']} failed={totals['failed']} notExecuted={totals['notExecuted']}")
    level = "notice" if totals["failed"] == 0 and totals["executed"] >= min_expected else "error"
    print(f"::{level} title=Test summary::{line}")
    for name in failed_names[:20]:
        print(f"::error title=Failed test::{name}")

    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as fh:
            fh.write(f"### Test summary\n\n`{line}`\n")
            if failed_names:
                fh.write("\nFailed:\n" + "\n".join(f"- `{n}`" for n in failed_names) + "\n")

    if not files or totals["executed"] < min_expected:
        print(f"no or too few tests executed (expected >= {min_expected})")
        return 1
    return 1 if totals["failed"] else 0


if __name__ == "__main__":
    sys.exit(main())
