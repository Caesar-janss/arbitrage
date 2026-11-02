#!/usr/bin/env python3
"""R-05 evidence: summarize the JIT disassembly of the OrderBookL2 seqlock writer as GitHub annotations.

Usage: python3 tools/arm64_seqlock_evidence.py jit.txt
Looks, per method, for the atomic add on the version (ldadd*/stadd* or an ldaxr/stlxr loop) and for a full barrier
(dmb ish) after it. On ARM64 an `staddl` / `ldaddal ..., xzr` (no acquire) with no `dmb` before the first data
store is the pattern R-05 is about. Prints one ::notice per method plus a verdict; exit code is always 0 (evidence,
not a gate). Stdlib only.
"""
import re
import sys


def main() -> int:
    text = open(sys.argv[1], encoding="utf-8", errors="replace").read()
    blocks = re.split(r"(?m)^; Assembly listing for method ", text)
    found = 0
    for b in blocks[1:]:
        name = b.splitlines()[0].strip()
        if "OrderBookL2" not in name:
            continue
        found += 1
        lines = [l.strip() for l in b.splitlines()]
        atomics = [l for l in lines if re.search(r"\b(ldadd\w*|stadd\w*|ldaxr|stlxr|lock\s+xadd|xadd)\b", l)]
        barriers = [l for l in lines if re.search(r"\b(dmb|mfence)\b", l)]
        tier = "full-opt" if "FullOpts" in b or "Tier1" in b or "optimized" in b.lower() else "?"
        summary = f"{name} [{tier}] atomics: {' | '.join(atomics[:4]) or '-'} || barriers: {' | '.join(barriers[:4]) or '-'}"
        print(f"::notice title=R-05 JIT {name[:60]}::{summary[:900]}")
        no_acquire = any(re.search(r"\bstadd\w*\b|\bldadd\w*\s+[wx]\w+,\s*(wzr|xzr)", a) for a in atomics)
        if no_acquire and not barriers:
            print(f"::warning title=R-05 pattern::{name[:80]}: atomic add without acquire and no dmb (the R-05 pattern)")
    if found == 0:
        print("::warning title=R-05 JIT::no OrderBookL2 method found in the JIT listing (check DOTNET_JitDisasm)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
