# ArbEngine

Multi-Strategy Crypto Arbitrage Engine (Spatial + Funding Rate) — .NET 8 + PostgreSQL 16.

## Status
| Tahap | Isi | Dokumen |
|---|---|---|
| 0 | Blueprint + skema PostgreSQL (7 migrasi, 119 assertion; V007 = fix review ronde 1) | `docs/ARCHITECTURE.md` |
| **1** | **`Arb.Core` v0.3: pricing, order book seqlock, state machine, risk, inventory (250 test, 0 B/op hot path) — REVIEW TERBUKA (patch ronde 1 di draft PR #2)** | **`docs/STAGE1_CORE.md`, `docs/review/FINDINGS_LOG.md`, `docs/review/stage1/REGRESSION_LOG.md`** |
| 2 | Adapter venue (Bitget/Bybit/Binance) — menunggu keputusan venue/region (STAGE1_CORE §5) | — |

## Struktur
```
ArbEngine.sln
Directory.Build.props        net8.0, nullable, C# 12; src/ → TreatWarningsAsErrors
Directory.Packages.props     versi paket terpusat (xunit 2.9.3, BDN 0.14.0)
src/Arb.Core/                Primitives · Instruments · MarketData · Pricing · Execution · Risk · Inventory
tests/Arb.Core.Tests/        xUnit — skenario kalkulasi, 1000PEPE, partial depth, kontrak SQL, seqlock, zero-alloc
tests/Arb.Benchmarks/        BenchmarkDotNet [MemoryDiagnoser]
db/migrations, db/tests      skema PostgreSQL + test skenario
docs/                        ARCHITECTURE.md, STAGE1_CORE.md
```

## Tim & review
Alur kerja tim (Jansen, Claude, ChatGPT, Gemini): `docs/TEAM_WORKFLOW.md`. Paket review satu-file per reviewer:
```bash
python3 tools/make_review_packet.py --profile gemini  --stage "Tahap 1 (Arb.Core)"   # → review_packets/
python3 tools/make_review_packet.py --profile chatgpt --stage "Tahap 1 (Arb.Core)"
```
Temuan dicatat di `docs/review/FINDINGS_LOG.md`.

## Push pertama ke GitHub (`Caesar-janss/arbitrage`)
```bash
git clone https://github.com/Caesar-janss/arbitrage.git && cd arbitrage
# repo masih kosong? buat main dulu:  git commit --allow-empty -m "init" && git push -u origin main
git checkout -b stage1/core
unzip -o ../ArbEngine_v0.2.1.zip && cp -r ArbEngine/. . && rm -rf ArbEngine   # README bawaan GitHub tertimpa
git add -A && git commit -m "Stage 1: Arb.Core, tests, benchmarks, DB schema, team workflow"
git push -u origin stage1/core
# buka PR stage1/core → main; CI (build, test, skema DB) jalan otomatis
```
Benchmark + gate 0 B/op: tab Actions → `ci` → Run workflow → centang `run_benchmarks`.

## Menjalankan
```bash
dotnet build ArbEngine.sln -c Release
dotnet test tests/Arb.Core.Tests -c Release
dotnet run -c Release --project tests/Arb.Benchmarks -- --filter '*'
PGHOST=localhost PGPORT=5432 PGUSER=postgres ./db/tests/run_tests.sh
```
Butuh .NET SDK 8.0+ (`global.json` roll-forward ke major terbaru) dan PostgreSQL ≥ 15.
