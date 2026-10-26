# Tahap 1 — laporan Claude-R (reviewer Claude independen, konteks kosong)

Versi yang direview: v0.2 (pra-`93df9953f26c`; `src/` identik). Repo tidak dimodifikasi oleh reviewer.
Proof harness reviewer: 22 skenario (P01–P22) yang mereferensikan `src/Arb.Core`. **Semua dijalankan ulang secara
independen oleh Claude (lead) → output identik.** Angka kunci dicek ulang dengan Python `Decimal`.

| ID | Sev | Area | Klaim | Bukti (skenario) |
|---|---|---|---|---|
| R-01 | S1 | `Risk/MarginGuard.cs`, `LiquidationCalculator.cs` | Jarak likuidasi dihitung per posisi terhadap SELURUH equity cross. Akun dengan >1 posisi dilaporkan jauh lebih aman dari kenyataan | P11: 2 short @3000 notional, equity 1000 → per posisi 3194 bps/None; level akun (naik bersamaan) 1545 bps (zona Reduce) |
| R-02 | S1 | `TradeStateMachine.IsAllowedFor`, `RepairPlanner` PerpHedge, `V005 v_open_exposure` | Residual spatial yang di-hedge di perp berakhir REPAIRED (terminal) padahal spot + perp tetap terbuka; tidak ada pemilik unwind/funding/likuidasi | P18 |
| R-03 | S2 | `Pricing/SpatialArbPricer.cs` | Q* memakai haircut, tetapi limit IOC/VWAP/edge memakai 100% depth → IOC terisi sebagian, edge overstated | P01: haircut 50% → limit L1 saja, terisi 500/1000 lot, residual 0,005 BTC; edge +3,2% |
| R-04 | S2 | `MarketData/OrderBookL2.cs` MarkInvalid/MarkResyncing/EndWrite | Pemanggilan dari thread non-writer merusak versi seqlock; parity bisa terbalik permanen (snapshot tidak memulihkan) → torn read diterima sebagai Synced | P12, P22: 1.068.578 torn dari 1.112.839 read diterima |
| R-05 | S2 | `OrderBookL2.BeginWrite` | **NEEDS-DATA**: di ARM64, Interlocked.Increment tanpa hasil bisa di-lower ke `staddl` tanpa urutan acquire → store data bisa terlihat sebelum versi ganjil | analisis litmus aarch64; perlu disassembly di Graviton |
| R-06 | S2 | `Inventory/InventoryLedger.cs` | `ExpireSettled` dan event saldo di ms yang sama melepas reservasi yang fill-nya belum tercermin → Available OVERSTATED (klaim docs "tidak pernah overstated" salah) | P07: Available 0,05 vs benar 0,02 BTC |
| R-07 | S2 | `Risk/StaleDataGuard.cs` | Book beku 4,9 s dengan socket hidup = Fresh; tidak ada batas umur per strategi; latency negatif −500 ms diterima | P08 |
| R-08 | S2 | `InstrumentRegistryBuilder.Replace` + pricer | Ganti tick/step dengan key sama → book lama dibaca dengan grid baru → depth fantom 10× | P15, P19 |
| R-09 | S2 | `Risk/WalletStatus.cs` NetworkId | NetworkId 0 (tak terpetakan) jadi wildcard → route palsu Open → filter wallet trap mati | P04 |
| R-10 | S2 | `TradeStateMachine` + `V002` | Tidak ada POSITION_OPEN→RESIDUAL/STUCK (likuidasi/ADL satu kaki) dan tidak ada jalur partial reduce yang posisinya tetap terbuka | P06 |
| R-11 | S3 | `Risk/SanityGuard.cs` | Cap inventory non-replenishable dihitung per evaluasi dari sisa → 22 trade menghabiskan 99,8% (klaim 25%) | P03 |
| R-12 | S3 | `Risk/SanityGuard.cs` | Quote route tertutup + ReferencePx 0/NaN → cap dilewati (fail-open) | P02 |
| R-13 | S3 | `Execution/RepairPlanner.cs` | Repair BUY di spot yang fee-nya dipotong dari base tidak di-gross-up → kurang kirim, sisa dust di bawah minimum | P09 |
| R-14 | S3 | `TradeGroup.IsFlat`, `RepairPolicy.DustCanon`, `V001 dust_quote` | Tiga definisi "flat" (dust base C#, minimum venue, 1 USDT di DB) → trade dust tidak pernah realized, lolos dari loss-streak breaker | P13, P21 |
| R-15 | S3 | `SpatialArbPricer.Reject` vs `trd.opportunity` | Quote yang ditolak tidak bisa disimpan (kolom NOT NULL > 0) → satu baris merusak batch COPY | P20 |
| R-16 | S3 | `NetEdgeCalculator` vs `V005`; `TradeGroup` | Basis expected vs realized berbeda (buffer slippage + rebalance) → capture ratio terinflasi; TradeGroup tanpa funding | db/tests trade 1001: capture 1,29 untuk trade sempurna |
| R-17 | S3 | `Primitives/SnowflakeIdGenerator.cs` | Restart setelah jam mundur → ID sama diterbitkan ulang → clientOrderId bentrok | P16 |
| R-18 | S3 | `FundingYieldCalculator`, `RiskLimits.Validate` | Gate NaN fail-open, hold = +∞ tetap Enter, MinFundingNetApr tidak divalidasi | P05 |
| R-19 | S3 | `SpatialArbPricer` ResidualMarkPx | Dust gross-up ada di venue BELI tetapi dinilai pakai VWAP venue JUAL → net overstated | P21: 9,2% dari net |
| R-20 | S4 | `tests/*`, `DbContractTests` | Beberapa test lulus karena alasan yang salah / kasus penting tidak ada; enum yang di-mirror ke SQL tidak semua dikontrak-test | — |

## Verified OK (menurut reviewer, dirangkum)
Parser `Utf8Decimal` + `DecimalGrid` (fuzz 185.901 string, 0 mismatch), penanganan BaseMultiplier/1000PEPE,
arah pembulatan limit IOC & repair, optimizer Q* (hurdle = 0), NetEdge fee-base sekali hitung, resolusi aset fee,
rumus likuidasi tunggal (fuzz 19.306 kasus vs bisection: 0 estimasi non-konservatif), kontrak C# ↔ SQL yang sudah
dites, logika single-writer OrderBookL2 di x64, Snowflake dalam satu proses, atomicity TryReservePair, aturan
same-network wallet route, rumus funding.

## Top 3 risks (reviewer)
1. Likuidasi short funding karena MarginGuard per posisi pada akun cross multi-pair (R-01).
2. Eksposur terbuka tanpa pemilik: PerpHedge → REPAIRED; tidak ada jalur likuidasi/partial reduce (R-02, R-10).
3. Likuiditas fantom → legging: R-03, R-04/R-05, R-07, R-08, R-06.
