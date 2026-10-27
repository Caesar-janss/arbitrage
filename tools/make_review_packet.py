#!/usr/bin/env python3
"""Build ONE self-contained review packet (prompt + manifest + line-numbered files) for an external reviewer.

Usage:
  python3 tools/make_review_packet.py --profile gemini  --stage "Tahap 1 (Arb.Core)"
  python3 tools/make_review_packet.py --profile chatgpt --stage "Tahap 1 (Arb.Core)" --out review_packets/x.md
  python3 tools/make_review_packet.py --version          # print repo version hash only

Safety: only whitelisted globs are included, secret-looking files are excluded by name, and the run ABORTS if any
included file content looks like a credential. Version = sha256 over all source files → findings cite a version.
Stdlib only (Python 3.9+).
"""
import argparse
import datetime
import fnmatch
import hashlib
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
EXCLUDED_DIRS = {"bin", "obj", ".git", "review_packets", "BenchmarkDotNet.Artifacts", "TestResults", ".vs", ".idea", "node_modules"}
SECRET_NAMES = [".env", ".env.*", "*.env", "appsettings.*.json", "*secret*", "*.pfx", "*.pem", "*.key", "*.p12"]
SECRET_CONTENT = re.compile(
    r"""(?ix)
    (api[_-]?key|api[_-]?secret|secret[_-]?key|passphrase|private[_-]?key)\s*[:=]\s*["']?[A-Za-z0-9+/=_\-]{16,}
    | -----BEGIN\ [A-Z ]*PRIVATE\ KEY-----
    """
)

SOURCE_GLOBS = [
    "src/**/*.cs", "src/**/*.csproj", "src/**/*.props",
    "tests/**/*.cs", "tests/**/*.csproj",
    "db/**/*.sql", "db/**/*.sh",
    "docs/*.md", "docs/review/FINDINGS_TEMPLATE.md",
    "*.props", "*.sln", "global.json", "README.md",
]

PROFILES = {
    "gemini": {"prompt": "docs/review/prompts/gemini.md", "include": SOURCE_GLOBS},
    "claude": {"prompt": "docs/review/prompts/claude.md", "include": SOURCE_GLOBS},
    "chatgpt": {
        "prompt": "docs/review/prompts/chatgpt.md",
        "include": [
            "docs/STAGE*.md", "docs/review/FINDINGS_TEMPLATE.md",
            "src/Arb.Core/Primitives/FixedPoint.cs", "src/Arb.Core/Primitives/DecimalGrid.cs", "src/Arb.Core/Primitives/Enums.cs",
            "src/Arb.Core/MarketData/PriceLevel.cs", "src/Arb.Core/MarketData/BookView.cs",
            "src/Arb.Core/Instruments/*.cs", "src/Arb.Core/Pricing/*.cs", "src/Arb.Core/Risk/*.cs", "src/Arb.Core/Inventory/*.cs",
            "src/Arb.Core/Execution/RepairPlanner.cs", "src/Arb.Core/Execution/TradeGroup.cs", "src/Arb.Core/Execution/OrderIntent.cs",
            "tests/Arb.Core.Tests/TestSupport/*.cs", "tests/Arb.Core.Tests/Pricing/*.cs", "tests/Arb.Core.Tests/Risk/*.cs",
            "tests/Arb.Core.Tests/Instruments/*.cs", "tests/Arb.Core.Tests/Inventory/*.cs",
            "tests/Arb.Core.Tests/Execution/RepairPlannerTests.cs",
        ],
    },
}

LANG = {".cs": "csharp", ".sql": "sql", ".sh": "bash", ".md": "markdown", ".json": "json",
        ".csproj": "xml", ".props": "xml", ".sln": "text", ".py": "python", ".yml": "yaml"}


def is_excluded(rel: pathlib.Path) -> bool:
    if any(part in EXCLUDED_DIRS for part in rel.parts):
        return True
    return any(fnmatch.fnmatch(rel.name.lower(), pat) for pat in SECRET_NAMES)


def collect(globs):
    seen = {}
    for pattern in globs:
        for p in sorted(ROOT.glob(pattern)):
            if not p.is_file():
                continue
            rel = p.relative_to(ROOT)
            if not is_excluded(rel):
                seen[rel.as_posix()] = p
    return dict(sorted(seen.items()))


def repo_version() -> str:
    h = hashlib.sha256()
    for rel, p in collect(SOURCE_GLOBS).items():
        h.update(rel.encode())
        h.update(b"\0")
        h.update(p.read_bytes())
        h.update(b"\0")
    return h.hexdigest()[:12]


def build(profile: str, stage: str, out: pathlib.Path) -> None:
    cfg = PROFILES[profile]
    files = collect(cfg["include"])
    version = repo_version()
    for rel, p in files.items():
        text = p.read_text(encoding="utf-8", errors="replace")
        if SECRET_CONTENT.search(text):
            sys.exit(f"ABORT: {rel} looks like it contains a credential. Remove it before building a packet.")

    prompt = (ROOT / cfg["prompt"]).read_text(encoding="utf-8")
    prompt = prompt.replace("{{STAGE}}", stage).replace("{{VERSION}}", version)
    now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M UTC")

    total_lines = 0
    body = []
    manifest = []
    for i, (rel, p) in enumerate(files.items(), 1):
        lines = p.read_text(encoding="utf-8", errors="replace").splitlines()
        total_lines += len(lines)
        manifest.append(f"| {i} | `{rel}` | {len(lines)} |")
        lang = LANG.get(p.suffix.lower(), "text")
        numbered = "\n".join(f"{n:5d}| {line}" for n, line in enumerate(lines, 1))
        body.append(f"### `{rel}`\n\n````{lang}\n{numbered}\n````\n")

    header = (
        f"<!-- ArbEngine review packet | profile={profile} | version={version} | generated={now} -->\n"
        f"# ArbEngine — paket review `{profile}` — {stage} — versi `{version}`\n\n"
        f"Dibuat {now}. Kutip temuan sebagai `path:baris` sesuai nomor baris di bawah.\n\n---\n\n"
    )
    content = (
        header + prompt.strip() + "\n\n---\n\n"
        + f"## Manifest ({len(files)} file, {total_lines} baris)\n\n| # | path | baris |\n|---|---|---:|\n"
        + "\n".join(manifest) + "\n\n---\n\n## Isi file\n\n" + "\n".join(body)
    )
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(content, encoding="utf-8")
    kb = out.stat().st_size / 1024
    print(f"{out.relative_to(ROOT) if out.is_relative_to(ROOT) else out}: {len(files)} files, {total_lines} lines, "
          f"{kb:.0f} KB (~{len(content) // 4:,} tokens), version {version}")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--profile", choices=sorted(PROFILES))
    ap.add_argument("--stage", default="Tahap 1 (Arb.Core)")
    ap.add_argument("--out")
    ap.add_argument("--version", action="store_true", help="print repo version hash and exit")
    args = ap.parse_args()
    if args.version:
        print(repo_version())
        return
    if not args.profile:
        ap.error("--profile is required")
    slug = re.sub(r"[^0-9a-z]+", "", args.stage.lower().split("(")[0]).replace("tahap", "stage")
    out = pathlib.Path(args.out) if args.out else ROOT / "review_packets" / f"{slug}_{args.profile}.md"
    build(args.profile, args.stage, out if out.is_absolute() else ROOT / out)


if __name__ == "__main__":
    main()
