# ArbEngine

Multi-Strategy Crypto Arbitrage Engine (Spatial + Funding Rate) — .NET 8 + PostgreSQL 16.

## Status
| Tahap | Isi | Dokumen |
|---|---|---|
| 0 | Blueprint + skema PostgreSQL (8 migrasi, 124 assertion; V007/V008 = fix review) | `docs/ARCHITECTURE.md` |
| 1 ✅ | `Arb.Core`: pricing, order book seqlock, state machine, risk, inventory (323 test, 0 B/op hot path). Review 4 ronde: 33 laporan (Claude-R 20, ChatGPT 10 termasuk 4 duplikat, builder 3), semua S1/S2 di-fix dengan regression merah→hijau. **DITUTUP 2026-11-02 (persetujuan Jansen setelah recheck menyeluruh)** | `docs/STAGE1_CORE.md`, `docs/review/FINDINGS_LOG.md`, `docs/review/stage1/REGRESSION_LOG.md` |
| S0 ✅ | Probe latency 24 jam Tokyo vs Singapura → **opsi (a)**: spatial Binance ↔ Bitget dari Tokyo, Bybit untuk funding/hedge (merge PR #5 = keputusan Jansen) | `docs/S0_LATENCY_PROBE.md`, `docs/S0_RESULTS.md` |
| **2** | **Langkah 1 (sekarang): rekam market data Binance + Bitget di Tokyo, `python3 tools/md_recorder.py`** → langkah 2: ukur peluang nyata dari rekaman → baru adapter order entry; wajib membuktikan kewajiban I-1…I-3 (STAGE1_CORE §6.1) | `docs/STAGE2_RECORDER.md` |
| 2 (paralel) | Spread scanner 8 venue (Binance, Bitget, Bybit, OKX, MEXC, Gate, KuCoin, Kraken), snapshot tiap 5 s: pasangan venue & koin mana yang selisihnya lolos fee dan bertahan, `python3 tools/spread_scanner.py` | `docs/STAGE2_SCANNER.md` |

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
Alur kerja tim (Jansen, Claude, ChatGPT; Gemini keluar 2026-10-27): `docs/TEAM_WORKFLOW.md`. Claude dan ChatGPT membaca repo
langsung; review dilakukan sebagai komentar di PR. Paket review satu-file hanya cadangan:
```bash
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
