# Findings Log

Satu baris per temuan. Status: CONFIRMED · REJECTED · NEEDS-DATA · DUPLICATE.
CONFIRMED di sini = bukti dijalankan ulang secara independen oleh Claude dan reproducible. Saat fix, setiap skenario
bukti dijadikan test xUnit yang gagal sebelum fix (syarat `TEAM_WORKFLOW.md` §5).

## Tahap 1 — `Arb.Core`, ronde 1 (versi v0.2 / `93df9953f26c`)

**Status Tahap 1: REVIEW TERBUKA.** Ditutup hanya jika (1) setiap temuan S1/S2 CONFIRMED punya fix + regression test
yang tercatat gagal sebelum fix dan lulus sesudahnya (`docs/review/stage1/REGRESSION_LOG.md`), (2) C-01/C-02 sudah
ditriase, (3) fix S1 (R-01, R-02) di-review ulang oleh ChatGPT pada paket v0.3, (4) Jansen menyetujui secara eksplisit.
Patch v0.3 tidak mengubah status ini. (Syarat (3) semula "review Gemini". Gemini keluar dari tim 2026-10-27, lihat
`TEAM_WORKFLOW.md` §9.)

Progres syarat penutupan (2026-10-27): (1) **terpenuhi** untuk Claude-R (semua S1/S2 + S3/S4 juga); (2) **belum**: isi
C-01/C-02 belum diterima (ChatGPT diminta menulisnya ulang sebagai komentar di PR #2); (3) **belum**: re-review ChatGPT
v0.3 di PR #2; (4) **belum**. Versi untuk ronde review berikutnya: v0.3,
`make_review_packet.py --version` = `9be7565c2c7f`.

Detail tiap temuan Claude-R: `docs/review/stage1/R_claude-r.md`. ChatGPT: lihat bagian di bawah. Gemini: keluar dari tim
(2026-10-27), tidak ada review.

| ID | Sev | Area | Klaim (ringkas) | Status | Bukti triase | Batch | Fix v0.3 (regression test dulu) |
|---|---|---|---|---|---|---|---|
| R-01 | S1 | MarginGuard / Liquidation | per posisi vs equity cross bersama | CONFIRMED | P11 dijalankan ulang; hitung manual 1545 bps = **zona Warn** (label "Reduce" di laporan awal salah, dikoreksi) | A | `bf02647` |
| R-02 | S1 | Repair PerpHedge + state | hedge perp → REPAIRED terminal, kaki terbuka tanpa pemilik | CONFIRMED | P18 | A | `e985185` |
| R-10 | S2 | State machine C#+SQL | tidak ada jalur likuidasi/ADL & partial reduce | CONFIRMED | P06 | A | `1fca29c` |
| R-03 | S2 | SpatialArbPricer | haircut tidak dipakai di limit/VWAP/edge | CONFIRMED | P01 | B | `e926aab + 1fec277` |
| R-04 | S2 | OrderBookL2 | Mark* dari thread lain merusak parity seqlock | CONFIRMED | P12, P22 | B | `7ce6929` |
| R-05 | S2 | OrderBookL2 (ARM64) | urutan store writer di ARM64 | **REJECTED (data)** | Job CI `seqlock-arm64` di `ubuntu-24.04-arm`: JIT .NET 8 memakai `ldaddal` dengan register tujuan nyata (acquire + release), bukan `staddl`/`xzr`, baik di kode v0.2 maupun v0.3; stress 20/20. Fence defensif yang semula direncanakan **tidak dipasang** (redundan menurut data). Jansen boleh membuka ulang | B | `df8d55e, 93a5e69` |
| R-07 | S2 | StaleDataGuard | book beku 4,9 s lolos; latency negatif lolos | CONFIRMED | P08 | B | `962ce64` |
| R-08 | S2 | Registry.Replace | ganti grid dengan key sama → depth fantom | CONFIRMED | P15, P19 | B | `f0a38fe` |
| R-09 | S2 | WalletStatus | NetworkId 0 = wildcard | CONFIRMED | P04 | B | `e983074` |
| R-06 | S2 | InventoryLedger | ExpireSettled / ms sama → overstated | CONFIRMED | P07. Klaim `STAGE1_CORE` §3.9 salah, dikoreksi | C | `eb3e116 + 1e629bf` |
| R-11 | S3 | SanityGuard | cap per evaluasi menguras 99,8% | CONFIRMED | P03 | C | `3ff92c3` |
| R-12 | S3 | SanityGuard | ReferencePx 0/NaN fail-open | CONFIRMED | P02 | C | `7a014b0` |
| R-13 | S3 | RepairPlanner | repair buy tanpa gross-up fee base | CONFIRMED | P09 | C | `e8877e0` |
| R-18 | S3 | Funding gate | NaN/∞ fail-open | CONFIRMED | P05 | C | `2f42151` |
| R-19 | S3 | SpatialArbPricer | dust dinilai di venue yang salah | CONFIRMED | P21 | C | `884c988` |
| R-14 | S3 | flat/dust (C# ↔ SQL) | tiga definisi flat | CONFIRMED | P13, P21 | D | `c60c5af` |
| R-15 | S3 | opportunity row | reject tidak bisa disimpan | CONFIRMED | P20 | D | `5c1f6db` |
| R-16 | S3 | capture ratio | basis expected ≠ realized | CONFIRMED | db/tests trade 1001 | D | `1733bf4` |
| R-17 | S3 | Snowflake | ID duplikat setelah restart + jam mundur | CONFIRMED | P16 | D | `7f92fbf` |
| R-20 | S4 | tests | test lulus karena alasan salah / kasus hilang | CONFIRMED | daftar (a)–(f) di laporan | D | `e6e1386` |

Bukti merah/hijau tiap baris: `docs/review/stage1/REGRESSION_LOG.md`. Semua fix ada di draft PR #2 (`stage1/review-r1`).

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
- §4 "seqlock benar": hanya terbukti untuk **single writer** (R-04). Untuk ARM64 kini ada data (R-05): benar di .NET 8.
- Rencana "fence defensif R-05 tetap dipasang" (versi awal log ini) dibatalkan setelah data ARM64 masuk; alasan di
  `REGRESSION_LOG.md` §R-05.
- Laporan R-01 menulis 1545 bps sebagai "zona Reduce": **salah**, yang benar zona Warn (1500 < 1545 ≤ 2500). Ditandai
  reviewer ChatGPT. Lolos dari cek ulang Claude karena yang dicek hanya angkanya, bukan labelnya.

### Statistik reviewer
| Reviewer | Dilaporkan | Unik (baru) | DUPLICATE | CONFIRMED | REJECTED | NEEDS-DATA | Menunggu triase | Precision (unik yang terputus) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Claude-R | 20 | 20 | 0 | 19 | 1 (R-05, data ARM64) | 0 | 0 | 95% (19/20) |
| ChatGPT | 6 | ≤ 2 | 4 | 0 | 0 | 0 | 2 | — (belum ada yang terputus) |
| Gemini | — | | | | | | | keluar dari tim 2026-10-27, tidak menyerahkan review |

Aturan hitung: "Unik" = tidak tumpang-tindih dengan temuan yang tercatat lebih dulu. DUPLICATE tidak menambah skor
temuan, tetapi koreksi label (R-01) dicatat di atas sebagai kontribusi review.
