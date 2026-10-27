# Findings Log

Satu baris per temuan. Status: CONFIRMED · REJECTED · NEEDS-DATA · DUPLICATE.
CONFIRMED di sini = bukti dijalankan ulang secara independen oleh Claude dan reproducible. Saat fix, setiap skenario
bukti dijadikan test xUnit yang gagal sebelum fix (syarat `TEAM_WORKFLOW.md` §5).

## Tahap 1 — `Arb.Core`, ronde 1 (versi v0.2 / `93df9953f26c`)

**Status Tahap 1: REVIEW TERBUKA.** Ditutup hanya jika (1) setiap temuan S1/S2 CONFIRMED punya fix + regression test
yang tercatat gagal sebelum fix dan lulus sesudahnya (`docs/review/stage1/REGRESSION_LOG.md`), (2) C-01/C-02 sudah
ditriase, (3) review Gemini masuk dan ditriase, (4) Jansen menyetujui secara eksplisit. Patch v0.3 tidak mengubah status ini.

Detail tiap temuan Claude-R: `docs/review/stage1/R_claude-r.md`. ChatGPT: lihat bagian di bawah. Gemini: menunggu.

| ID | Sev | Area | Klaim (ringkas) | Status | Bukti triase | Batch fix |
|---|---|---|---|---|---|---|
| R-01 | S1 | MarginGuard / Liquidation | per posisi vs equity cross bersama | CONFIRMED | P11 dijalankan ulang; hitung manual 1545 bps = **zona Warn** (label "Reduce" di laporan awal salah, dikoreksi) | A |
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

### ChatGPT — Tahap 1 ronde 1

Pemetaan duplikat berasal dari reviewer ChatGPT. Duplikat **tidak dihitung sebagai temuan baru**; dicatat sebagai
konfirmasi independen atas temuan yang sudah ada. Isi lengkap C-01…C-06 belum diterima Claude: klaim C-03…C-06 akan
dibandingkan dengan R-xx saat file diterima (jika ternyata berbeda substansi, status diubah dan dicatat di sini).

| ID | Sev | Area | Klaim (ringkas) | Status | Bukti triase | Batch fix |
|---|---|---|---|---|---|---|
| C-01 | ? | ? | isi belum diterima | **MENUNGGU TRIASE** (temuan tambahan) | perlu file temuan ChatGPT | — |
| C-02 | ? | ? | isi belum diterima | **MENUNGGU TRIASE** (temuan tambahan) | perlu file temuan ChatGPT | — |
| C-03 | — | InventoryLedger | → R-06 | DUPLICATE of R-06 | pemetaan reviewer | (C, via R-06) |
| C-04 | — | SpatialArbPricer | → R-03 | DUPLICATE of R-03 | pemetaan reviewer | (B, via R-03) |
| C-05 | — | RepairPlanner | → R-13 | DUPLICATE of R-13 | pemetaan reviewer | (C, via R-13) |
| C-06 | — | MarginGuard | → R-01 | DUPLICATE of R-01 | pemetaan reviewer; ChatGPT juga menandai label zona 1545 bps yang salah (dikoreksi) | (A, via R-01) |

### Koreksi atas klaim Claude sendiri (dari ronde ini)
- `STAGE1_CORE` §3.9 "available tidak pernah overstated": **salah** (R-06).
- §3.6 menyarankan cross margin di akun strategi tanpa menyebut prasyarat "satu posisi per akun". Model per posisi tidak aman untuk multi-pair (R-01).
- §4 "seqlock benar": hanya terbukti untuk **single writer di x64** (R-04, R-05).
- Laporan R-01 menulis 1545 bps sebagai "zona Reduce": **salah**, yang benar zona Warn (1500 < 1545 ≤ 2500). Ditandai
  reviewer ChatGPT. Lolos dari cek ulang Claude karena yang dicek hanya angkanya, bukan labelnya.

### Statistik reviewer
| Reviewer | Dilaporkan | Unik (baru) | DUPLICATE | CONFIRMED | REJECTED | NEEDS-DATA | Menunggu triase | Precision (unik yang terputus) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Claude-R | 20 | 20 | 0 | 19 | 0 | 1 | 0 | 100% (19/19) |
| ChatGPT | 6 | ≤ 2 | 4 | 0 | 0 | 0 | 2 | — (belum ada yang terputus) |
| Gemini | — | | | | | | | |

Aturan hitung: "Unik" = tidak tumpang-tindih dengan temuan yang tercatat lebih dulu. DUPLICATE tidak menambah skor
temuan, tetapi koreksi label (R-01) dicatat di atas sebagai kontribusi review.
