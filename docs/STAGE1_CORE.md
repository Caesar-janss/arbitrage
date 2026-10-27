# Tahap 1 — `Arb.Core` (v0.2)

> **Status: REVIEW TERBUKA.** Temuan ronde 1 dan koreksi atas dokumen ini dicatat di
> [`review/FINDINGS_LOG.md`](review/FINDINGS_LOG.md). Klaim di dokumen ini yang dibantah review (mis. §3.9) tetap
> dibiarkan apa adanya sampai patch v0.3 masuk, dengan catatan di log.

Pure domain library: **zero dependency eksternal, tanpa I/O, deterministik**. Semua kalkulasi & state
yang menentukan "masuk / tidak / bagaimana memperbaiki" ada di sini dan diuji tanpa API key.

---

## 1. Peta permintaan → file

| Permintaan | File | Catatan |
|---|---|---|
| FixedPoint | `Primitives/FixedPoint.cs`, `DecimalGrid.cs`, `Utf8Decimal.cs` | grid exact (mantissa×10^e), parser UTF-8 → ticks tanpa decimal/double |
| InstrumentKey | `Primitives/InstrumentKey.cs` | 4 byte, index array (bukan Dictionary) |
| InstrumentSpec | `Instruments/InstrumentSpec.cs` | `BaseMultiplier` (1000PEPE), canonical lot, min qty/notional |
| LotNormalizer | `Instruments/LotNormalizer.cs` | `PairLotGrid` = LCM lot dua venue; gross-up fee base |
| OrderBookL2 (seqlock) | `MarketData/OrderBookL2.cs` | single-writer, best-at-end arrays, ask key = −px |
| BookView, BookHealth | `MarketData/BookView.cs`, `PriceLevel.cs` | `BookHealth` + `BookFault` |
| VwapWalker | `Pricing/VwapWalker.cs` | Int128 notional, marginal limit, haircut, quote budget |
| CrossedBookOptimizer Q* | `Pricing/CrossedBookOptimizer.cs` | merge-walk O(n+m), diverifikasi vs brute force |
| FeeModel | `Pricing/FeeModel.cs` | VIP tier, diskon BNB/BGB, **aset tempat fee dipotong** |
| NetEdgeCalculator | `Pricing/NetEdgeCalculator.cs` | fee-in-base dihitung sekali (sebagai residual) |
| (komposisi) | `Pricing/SpatialArbPricer.cs`, `SlippageModel.cs` | pipeline end-to-end, slippage per-leg latency |
| FundingYieldCalculator | `Pricing/FundingYieldCalculator.cs` | interval dinamis, break-even, NetAPR, CapitalAPR, gate likuidasi |
| TradeStateMachine | `Execution/TradeStateMachine.cs`, `TradeState.cs`, `TradeGroup.cs` | tabel ≡ SQL (diuji) |
| RepairPlanner | `Execution/RepairPlanner.cs`, `OrderIntent.cs` | complete / unwind / perp-hedge, marketable-limit ladder |
| InventoryLedger | `Inventory/InventoryLedger.cs` | reservasi konservatif, atomic pair |
| RiskLimits | `Risk/RiskLimits.cs` | satu objek tervalidasi, `HaltLevel` |
| **Tambahan #1** deposit/withdraw | `Risk/WalletStatus.cs`, `Risk/SanityGuard.cs` | lihat §3.5 |
| **Tambahan #2** distance-to-liquidation | `Risk/LiquidationCalculator.cs`, `Risk/MarginGuard.cs`, gate di `FundingYieldCalculator` | lihat §3.6 |
| (bonus, dibutuhkan pipeline) | `Risk/StaleDataGuard.cs`, `Primitives/SnowflakeIdGenerator.cs`, `Clock.cs` | |

Belum ada di Tahap 1 (sengaja, didesain bersama adapter di Tahap 2): interface venue
(`IMarketDataFeed`, `ITradingGateway`, `IUserStream`), `ExecutionReport`, `ExecutionPolicy`.

---

## 2. Cara menjalankan (di mesin dengan akses NuGet)

```bash
dotnet build ArbEngine.sln -c Release          # src/: TreatWarningsAsErrors
dotnet test  tests/Arb.Core.Tests -c Release   # 150 test case
dotnet run   -c Release --project tests/Arb.Benchmarks -- --filter '*'
# acceptance: kolom "Allocated" = "-" untuk semua benchmark
```
Test kontrak SQL membaca `db/migrations/V002__trading.sql` (dicari ke atas dari folder output;
override dengan env `ARB_REPO_ROOT`).

---

## 3. Keputusan desain (dan kenapa)

### 3.1 Kebijakan numerik — tiga domain, tidak dicampur
| Domain | Tipe | Alasan |
|---|---|---|
| Dalam satu venue (book, walk, field order) | `long` ticks/lots, `Int128` notional | exact; limit price & qty yang dikirim tidak boleh hasil pembulatan float |
| Ekonomi lintas venue (edge, fee, slippage, Q*) | `double` canonical | error relatif ~1e-15 vs granularity keputusan ~1e-5 (0,1 bps); 10 orde margin |
| Saldo, eksposur dari fill, reservasi | `decimal` | exact terhadap angka venue; bukan jalur per-tick |

Setiap konversi kembali ke grid venue memakai arah pembulatan **eksplisit** (`Rounding.Down/Up`).
Temuan saat verifikasi: `6005000 × 0.999` dalam `double` bisa jatuh 1 tick di bawah. Limit price repair
sekarang dihitung dengan `decimal`, dan `FixedPoint.ToUnits` memakai toleransi relatif (1e-12·|x|).

### 3.2 OrderBookL2 + seqlock
- Writer: `Interlocked.Increment` (ganjil, full fence) → mutasi → `Volatile.Write` (genap, release).
- Reader: `Volatile.Read` → copy → `Interlocked.MemoryBarrier` → `Volatile.Read`; diterima jika versi sama & genap.
- Array kapasitas tetap, tidak pernah realokasi → torn read menghasilkan nilai sampah (ditolak versi), **tidak pernah** out-of-range.
- Best level di **akhir** array → insert/delete di top-of-book hanya menggeser sedikit elemen.
- Satu seksi seqlock per pesan venue → reader tidak pernah melihat setengah pesan.
- Crossed book / qty negatif / harga ≤ 0 → `Invalid` sampai snapshot baru.
- **Sifat seqlock:** writer yang tidak pernah jeda membuat reader kelaparan (`TryCopyTo` → false). Di engine itu berarti "evaluasi ulang pada dirty signal berikutnya", bukan error.

### 3.3 Q* (CrossedBookOptimizer)
Marginal net unit ke-q: `m(q) = bid_B(q)(1−f_B−s_B) − ask_A(q)(1+f_A+s_A) − c_unit`, non-increasing.
NetProfit(Q) = ∫m → concave piecewise-linear → maksimum saat m(q) ≤ hurdle. Cukup satu merge-walk.
Hurdle `MinMarginalEdgeBps` berlaku **per unit marginal**: level dalam yang menambah eksposur dengan
edge tipis tidak diambil. Hasil kontinu → floor ke common lot → walk exact ulang → net edge exact.

### 3.4 Fee di aset base
Spot buy tanpa token diskon: fee dipotong dari koin yang **diterima**. Tanpa koreksi, setiap trade
meninggalkan short `Q·f` di venue jual (inventory drift). `LotNormalizer.GrossUpBuyLots` membeli
`Q/(1−f)` dibulatkan ke atas → residual selalu ≥ 0 (dust long). `NetEdgeCalculator` menghitung fee
base **sekali**, sebagai residual × mark (tidak double count).

### 3.5 Deposit/withdraw (Tambahan #1) — kenapa TIDAK di `InstrumentSpec`
Status wallet itu per **(venue, aset, network)** dan berubah dalam hitungan menit (maintenance chain),
sedangkan `InstrumentSpec` adalah snapshot exchangeInfo yang immutable. Jadi status disimpan di
`WalletStatusBook` (swap array atomik) dengan flag `IsDepositEnabled` / `IsWithdrawEnabled` per network.

Aturan yang lebih ketat dari sekadar "flag on/off":
1. **Route** A→B ada hanya jika **satu network yang sama** punya withdraw A ∧ deposit B. Withdraw Bitget hanya TRC20 dan deposit Bybit hanya ERC20 berarti tidak ada route.
2. Status basi (> `MaxWalletStatusAgeMs`) = `Unknown` = diperlakukan tertutup (fail-safe).
3. Spot-spot, route tertutup, edge > `WalletClosedMaxEdgeBps` → `REJECT_SANITY` + `SuspectedWalletTrap`. Spread melebar *karena* koin tidak bisa dipindah.
4. Route tertutup + edge normal → boleh **hanya** dengan inventory yang sudah ada, di-cap `NonReplenishableInventoryFraction` (tidak bisa diisi ulang sampai wallet buka).
5. Ada kaki perp (perp-perp, spot-perp) → status wallet koin diabaikan (sesuai permintaan).

### 3.6 Distance-to-liquidation (Tambahan #2)
Model equity-at-mark, konservatif (`m = mmr + fee tutup`), tier dipilih pada notional **saat likuidasi**
(iteratif; tier bisa naik saat harga naik):
```
short: P_liq = (Eq + Q·P0 + cum) / (Q(1+m))        long: P_liq = (Q·P0 − Eq − cum) / (Q(1−m))
reduce (cross, bayar fee f): Q' = (Eq + cum − Q·P0·f) / (Pt(1+m) − P0(1+f))
```
`MarginGuard` (target 3500 / warn 2500 / reduce 1500 / critical 800 bps, bisa dikonfigurasi):

| Zona | Aksi |
|---|---|
| d > warn | None |
| reduce < d ≤ warn | `AddMargin` dari wallet lokal; kalau kurang → `RebalanceCollateral` (transfer lintas venue, mulai lebih awal) |
| critical < d ≤ reduce | `AddMargin` jika cukup; jika tidak: **Cross** → `ReducePair` (kedua kaki, delta-neutral) + transfer; **Isolated** → `FlattenPair` |
| d ≤ critical | `FlattenPair` |

**Insight penting:** pada isolated margin, venue melepas margin pro-rata saat posisi dikurangi →
harga likuidasi hampir tidak bergerak. *Partial reduce hanya efektif di cross margin.* Funding arb
sebaiknya berjalan di cross margin (akun khusus strategi). Entry funding juga di-gate:
`FundingVerdict.RejectLiquidationDistance` bila jarak awal < `MinEntryLiqDistanceBps`.

### 3.7 Slippage per kaki = f(latency kaki itu)
`horizon = umur data + latency order one-way` → `σ·√horizon`. Konsekuensinya: kaki venue yang jauh
otomatis mendapat buffer lebih besar. Test `SlowLeg_GetsLargerSlippageBuffer_AndCanKillTheTrade`
menunjukkan edge 25 bps yang lolos di kaki 2 ms **ditolak** jika kaki jual berhorizon 70 ms (Tokyo→Singapura).

### 3.8 RepairPlanner
Pilih kandidat dengan biaya terendah (VWAP hasil walk book setelah fee vs harga perolehan). Kandidat
yang tidak bisa menyerap seluruh residual dipenalti. Order selalu **marketable-limit IOC** dengan
ladder (10/30/80 bps), tidak pernah market order murni.
`MaxStopLossBps` **tidak memblok** flatten: membiarkan residual terbuka = taruhan arah tanpa hedge.
Ia hanya mem-flag (→ breaker), dan `SevereLossBps` → HALT_GLOBAL.

### 3.9 InventoryLedger
`Available = venueFree − reserved`. Reservasi dilepas saat order terminal **dan** ada balance event
dengan waktu ≥ terminal. Selama IOC berjalan bisa terjadi double count, tapi ke arah aman: available
bisa *understated*, tidak pernah *overstated*. `TryReservePair` atomik sehingga dua peluang tidak pernah
memakai saldo yang sama. `ExpireSettled` sebagai jaring pengaman (diikuti rekonsiliasi REST).

---

## 4. Laporan verifikasi

> **Keterbatasan lingkungan:** `api.nuget.org` diblokir egress policy sandbox ini, jadi paket xUnit/BDN
> tidak bisa di-restore. Yang dilakukan:
> 1. `Arb.Core` di-build penuh (Debug & Release, warnings-as-errors): **0 warning, 0 error**.
> 2. Source test & benchmark **asli** di-compile apa adanya terhadap shim yang meniru signature xunit 2.9 / BDN 0.14 yang dipakai, lalu dijalankan dengan runner reflection.
> 3. **Konfirmasi final tetap `dotnet test` / `dotnet run` asli di mesin Anda.** Risiko residual: perbedaan kecil overload/analyzer xUnit (test project tidak memakai warnings-as-errors agar warning analyzer tidak memblok).

### 4.1 Test: 150/150 lulus (Debug ×5, Release ×5, stabil)
| Kelas | Case | Kelas | Case |
|---|---:|---|---:|
| FixedPointTests | 31 | RepairPlannerTests | 9 |
| OrderBookL2Tests | 10 | WalletStatusAndSanityTests | 9 |
| CrossedBookOptimizerTests (incl. 4×300 random vs brute force) | 9 | VwapWalkerTests | 8 |
| InstrumentTests | 8 | InventoryLedgerTests | 8 |
| LiquidationAndMarginTests | 11 | TradeStateMachineTests (vs SQL) | 7 |
| SpatialArbPricerTests | 6 | FundingYieldCalculatorTests | 6 |
| SnowflakeIdGeneratorTests | 5 | FeeAndSlippageTests | 5 |
| ZeroAllocationTests | 4 | StaleDataGuardTests | 4 |
| NetEdgeCalculatorTests | 4 | DbContractTests | 2 |
| RiskLimitsTests | 2 | SeqlockConcurrencyTests | 1 |
| TradeGroupTests | 1 | | |

Angka ekspektasi dihitung independen (Python `Decimal`, presisi 40 digit), bukan disalin dari output C#.
Beberapa sengaja identik dengan skenario `db/tests` (opp 501, trade 1001/1002) supaya Core dan DB terbukti satu model.

### 4.2 Mutation testing: 8/8 bug sengaja tertangkap
| Mutasi | Test yang gagal |
|---|---|
| M1 seqlock menerima copy torn | Seqlock (≈2.200–2.600 torn read terdeteksi per run) |
| M2 transisi STUCK→MANUAL_RESOLVED dihapus | kontrak SQL + skenario STUCK |
| M3 alokasi tersembunyi di VwapWalker | ZeroAllocation ×2 |
| M4 cek crossed book dimatikan | OrderBook ×2 |
| M5 tanda rumus likuidasi short salah | Liquidation/Margin ×6 |
| M6 route wallet diabaikan | Sanity ×2 |
| M7 multiplier diabaikan di harga canonical | 1000PEPE ×5 |
| M8 hurdle diabaikan | Optimizer hurdle |

**RCA flaky test yang ditemukan dan diperbaiki:** versi awal test seqlock (loop jumlah pesan tetap) sesekali
gagal di syarat "≥100 read tervalidasi". Stress 600 ronde: **0 torn read**. Penyebabnya bukan seqlock:
writer selesai sebelum thread reader terjadwal, ditambah reader kelaparan oleh writer tanpa jeda (5% ronde
gagal, 39% di bawah tekanan GC). Test diubah ke berbasis durasi + start barrier + jeda writer realistis.
Stress ulang: 124 juta read tervalidasi, minimum 173 ribu per ronde, 0 torn.

### 4.3 Alokasi & latency (harness offline, container 2 vCPU, .NET 8.0.132)
| Benchmark | ns/op (median, indikatif) | B/op |
|---|---:|---:|
| OrderBookL2.ApplyDelta (10 level, book 200) | 123 | 0 |
| OrderBookL2.TryCopyTo (top 20) | 45 | 0 |
| VwapWalker.WalkLots (~20 level) | 54 | 0 |
| CrossedBookOptimizer.Optimize (Q*) | 198 | 0 |
| **SpatialArbPricer.Evaluate (end-to-end)** | **385** | 0 |
| FundingYieldCalculator.Evaluate | 7 | 0 |
| LiquidationCalculator.Estimate (tiered) | 14 | 0 |
| MarginGuard.Evaluate (reduce zone) | 61 | 0 |
| TradeStateMachine (2 transisi) | 6 | 0 |
| RepairPlanner.Plan (2 venue) | 186 | 0 |
| SanityGuard.Evaluate (wallet routes) | 25 | 0 |
| StaleDataGuard.Check | 5 | 0 |
| InventoryLedger reserve→terminal→settle (pair) | 483 | 0 |
| Utf8Decimal.TryParse + DecimalGrid.TryToUnits | 82 | 0 |
| Snowflake.Next + ClientOrderId.TryFormat | 60 | 0 |

Alokasi diukur exact (`GC.GetAllocatedBytesForCurrentThread`, 1 juta call). ns dari Stopwatch, sudah
termasuk overhead delegate ~1–2 ns. Untuk angka publikasi gunakan BDN asli. Budget compute
wire→decision (< 50 µs) aman dengan margin dua orde. Hot path tetap jauh lebih kecil dari RTT jaringan.

---

## 5. Konsekuensi keputusan venue/region (perlu keputusan Anda sebelum Tahap 2)

1. **Tokyo + pasangan utama Bitget–Bybit.** Bitget & Binance terkonsentrasi di AWS Tokyo; Bybit di AWS
   Singapura. Dari Tokyo, kaki Bybit ≈ 35 ms data basi + 35 ms transit order (RTT Tokyo↔Singapura kira-kira
   65–75 ms, **ukur sendiri**). Jendela legging ditentukan kaki paling lambat, jadi Bitget–Bybit spatial dari Tokyo
   adalah kombinasi *terlambat*, sedangkan **Binance–Bitget** (keduanya Tokyo, ~1–3 ms) adalah pasangan spatial alami.
   Opsi:
   - (a) spatial utama Binance↔Bitget, Bybit untuk funding arb (tidak sensitif latency) & venue hedge;
   - (b) tetap Bitget↔Bybit dan terima buffer slippage lebih besar (otomatis via §3.7, artinya lebih sedikit trade);
   - (c) dua proses di dua region (kompleks, butuh koordinasi state lintas region).
   Kode Tahap 1 venue-agnostic; yang berubah hanya urutan adapter di Tahap 2.
2. **Bitget WS order entry.** WS `place-order` terdokumentasi di bagian **UTA**. Pihak ketiga mencatat stream
   WS Bitget (API classic) tidak menerima order. Pastikan akun dalam mode UTA sebelum mendesain gateway Bitget.
   Bybit V5 juga punya WS order entry, jadi ini bukan pembeda antar venue.

---

## 6. Masukan untuk Tahap 2
- Adapter perlu memetakan nama network venue → `NetworkId` canonical (ERC20/ETH/…).
- `BookStamp.ExchangeUnixMicros` wajib dari timestamp venue, `LocalRecvUnixMicros` dari jam host ber-chrony.
- `ClockOffsetEstimator` (Venues.Common) menghasilkan `VenueClock` untuk `StaleDataGuard`.
- Synchronizer snapshot/delta memanggil `MarkInvalid(SequenceGap)` → `ApplySnapshot` (Bitget: CRC32 via `CopyTopFromWriterThread`).
- `InventoryLedger.OnBalance` butuh event time venue (Binance `u`, Bybit `creationTime`, Bitget `ts`).
