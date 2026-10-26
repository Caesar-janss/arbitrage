# Findings Log

Satu baris per temuan. Status: CONFIRMED · REJECTED · NEEDS-DATA · DUPLICATE.
CONFIRMED di sini = bukti dijalankan ulang secara independen oleh Claude dan reproducible. Saat fix, setiap skenario
bukti dijadikan test xUnit yang gagal sebelum fix (syarat `TEAM_WORKFLOW.md` §5).

## Tahap 1 — `Arb.Core`, ronde 1 (versi v0.2 / `93df9953f26c`)

Detail tiap temuan: `docs/review/stage1/R_claude-r.md`. Gemini & ChatGPT: menunggu.

| ID | Sev | Area | Klaim (ringkas) | Status | Bukti triase | Batch fix |
|---|---|---|---|---|---|---|
| R-01 | S1 | MarginGuard / Liquidation | per posisi vs equity cross bersama | CONFIRMED | P11 dijalankan ulang; hitung manual 1545 bps | A |
| R-02 | S1 | Repair PerpHedge + state | hedge perp → REPAIRED terminal, kaki terbuka tanpa pemilik | CONFIRMED | P18 | A |
| R-10 | S2 | State machine C#+SQL | tidak ada jalur likuidasi/ADL & partial reduce | CONFIRMED | P06 | A |
| R-03 | S2 | SpatialArbPricer | haircut tidak dipakai di limit/VWAP/edge | CONFIRMED | P01 | B |
| R-04 | S2 | OrderBookL2 | Mark* dari thread lain merusak parity seqlock | CONFIRMED | P12, P22 | B |
| R-05 | S2 | OrderBookL2 (ARM64) | urutan store writer di ARM64 | NEEDS-DATA | Pemilik: Jansen (runner ARM64/Graviton) + `DOTNET_JitDisasm`. Mitigasi defensif (`MemoryBarrier` setelah increment) tetap dipasang | B |
| R-07 | S2 | StaleDataGuard | book beku 4,9 s lolos; latency negatif lolos | CONFIRMED | P08 | B |
| R-08 | S2 | Registry.Replace | ganti grid dengan key sama → depth fantom | CONFIRMED | P15, P19 | B |
| R-09 | S2 | WalletStatus | NetworkId 0 = wildcard | CONFIRMED | P04 | B |
| R-06 | S2 | InventoryLedger | ExpireSettled / ms sama → overstated | CONFIRMED | P07. Klaim `STAGE1_CORE` §3.9 salah, dikoreksi | C |
| R-11 | S3 | SanityGuard | cap per evaluasi menguras 99,8% | CONFIRMED | P03 | C |
| R-12 | S3 | SanityGuard | ReferencePx 0/NaN fail-open | CONFIRMED | P02 | C |
| R-13 | S3 | RepairPlanner | repair buy tanpa gross-up fee base | CONFIRMED | P09 | C |
| R-18 | S3 | Funding gate | NaN/∞ fail-open | CONFIRMED | P05 | C |
| R-19 | S3 | SpatialArbPricer | dust dinilai di venue yang salah | CONFIRMED | P21 | C |
| R-14 | S3 | flat/dust (C# ↔ SQL) | tiga definisi flat | CONFIRMED | P13, P21 | D |
| R-15 | S3 | opportunity row | reject tidak bisa disimpan | CONFIRMED | P20 | D |
| R-16 | S3 | capture ratio | basis expected ≠ realized | CONFIRMED | db/tests trade 1001 | D |
| R-17 | S3 | Snowflake | ID duplikat setelah restart + jam mundur | CONFIRMED | P16 | D |
| R-20 | S4 | tests | test lulus karena alasan salah / kasus hilang | CONFIRMED | daftar (a)–(f) di laporan | D |

**Batch fix (v0.3):**
- **A**: risiko eksposur: margin level akun + lifecycle hedge + transisi baru (migrasi `V007`).
- **B**: integritas data/likuiditas fantom.
- **C**: inventory & gate.
- **D**: kontrak DB/analytics & test.

### Koreksi atas klaim Claude sendiri (dari ronde ini)
- `STAGE1_CORE` §3.9 "available tidak pernah overstated": **salah** (R-06).
- §3.6 menyarankan cross margin di akun strategi tanpa menyebut prasyarat "satu posisi per akun". Model per posisi tidak aman untuk multi-pair (R-01).
- §4 "seqlock benar": hanya terbukti untuk **single writer di x64** (R-04, R-05).

### Statistik reviewer
| Reviewer | Temuan | CONFIRMED | REJECTED | NEEDS-DATA | Precision |
|---|---:|---:|---:|---:|---:|
| Claude-R | 20 | 19 | 0 | 1 | 100% (19/19 yang terputus) |
| Gemini | — | | | | |
| ChatGPT | — | | | | |
