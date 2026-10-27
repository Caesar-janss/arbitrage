# Regression log — Tahap 1, ronde 1 (patch v0.3)

Aturan (`TEAM_WORKFLOW.md` §5): setiap temuan CONFIRMED mendapat regression test **sebelum** patch.
Test dijalankan pada kode pra-patch → output **MERAH** dicatat. Setelah patch → output **HIJAU** + full suite dicatat.
Satu commit per temuan (test + patch + entri log ini). Status Tahap 1 tetap **REVIEW TERBUKA**.

## Cara reproduksi (siapa pun, termasuk reviewer)

Untuk commit fix `<sha>` dari temuan `R-xx` (lihat tabel di `FINDINGS_LOG.md`):

```bash
git checkout <sha>
git diff <sha>^ <sha> -- src db | git apply -R   # buang patch src/db (termasuk file baru), test baru tetap ada
dotnet test tests/Arb.Core.Tests -c Release --filter "FullyQualifiedName~Rxx_"   # harus MERAH
git checkout -- src db                         # kode SESUDAH patch
dotnet test tests/Arb.Core.Tests -c Release --filter "FullyQualifiedName~Rxx_"   # harus HIJAU
bash db/tests/run_tests.sh             # bila temuan menyentuh SQL
```

"MERAH (compile)" = test memakai API yang belum ada di kode pra-patch. Itu merah yang sah: perilaku yang diminta
memang tidak bisa diekspresikan oleh kode lama. Bila memungkinkan, test juga memuat assert perilaku pada API lama.

Output di bawah berasal dari harness offline Claude (sumber test yang sama, runner shim xUnit karena NuGet diblokir
di sandbox; lihat `STAGE1_CORE.md` §7). CI GitHub menjalankan xUnit asli pada commit yang sama.

---

## R-01 (S1) — margin cross dihitung per posisi, bukan per akun

- **Test**: `tests/Arb.Core.Tests/Regression/R01_CrossAccountMarginTests.cs` (9 test), skenario P11.
- **Angka acuan** (Python `Decimal`, prec 40): 2 short × 3000 USDT, equity 1000, m = 1 % + 5,5 bps →
  jarak akun = (1000 − 63,3) / (6000 × 1,01055) = **1544,868306 bps → zona Warn** (1500 < d ≤ 2500);
  collateral ke target 3500 bps = **1185,455 USDT**; equity 600 → 885,16 bps (Reduce) → ReduceFraction
  **0,7265547131**; equity 450 → 637,77 bps (Critical) → flatten; long+short campuran → 1561,1667 bps.
- **MERAH (compile)** pada kode pra-patch:
  ```
  BUILD FAILED
  tests/Arb.Core.Tests/Regression/R01_CrossAccountMarginTests.cs(19,20): error CS0246: The type or namespace name 'AccountPosition' could not be found
  ```
  (kode lama hanya punya `PerpExposure` tunggal: tidak ada cara mengekspresikan equity bersama.)
- **Patch**: `Risk/CrossAccountMargin.cs` (baru: `AccountPosition`, `AccountMarginEstimate`, surplus akun di bawah
  guncangan adverse seragam, bisection deterministik tanpa alokasi), `MarginGuard.EvaluateAccount` + doc: API per posisi
  hanya sah untuk isolated / akun cross dengan satu posisi. Zero-alloc test ditambah.
- **HIJAU**:
  ```
  R01_CrossAccountMarginTests             9 passed
  TOTAL: 9 passed, 0 failed
  full suite: TOTAL: 159 passed, 0 failed
  ```

## R-02 (S1) — residual SPATIAL yang di-hedge perp berakhir REPAIRED, kaki terbuka tanpa pemilik

- **Test C#**: `Regression/R02_SpatialHedgeStateTests.cs` (2, API lama), `Regression/R02_PerpHedgeLifecycleTests.cs` (6),
  skenario P18. **Test SQL**: blok `REGRESSION v0.3` di `db/tests/test_schema.sql` (trade 1008/1009, di-rollback).
- **MERAH (perilaku, API lama)**:
  ```
  FAIL R02_SpatialHedgeStateTests.Spatial_Repairing_To_PositionOpen_IsAllowed_ForAPerpHedge
      Assert.Equal() Failure  Expected: None  Actual: StrategyNotAllowed
  TOTAL: 1 passed, 1 failed
  ```
- **MERAH (compile)**, test lifecycle: `error CS1501: No overload for method 'ApplyFill' takes 7 arguments` (TradeGroup lama
  tidak tahu instrumen sebuah fill → hedge perp tak terlihat), `'TradeGroup' does not contain a definition for
  'HasOpenPerpPosition'`, `'TransitionError' does not contain a definition for 'OpenDerivativePosition'` / `'NotHedged'`.
- **MERAH (SQL, tanpa V007)**:
  ```
  --- regression v0.3 (di-rollback) ---
  ERROR:  FAIL  R-02: hedge PERP_HEDGE tidak boleh berakhir REPAIRED  expected error AR002 tapi statement sukses
  ```
- **Patch**:
  - `TradeGroup`: `ApplyFill(instrument, …)` wajib menyebut instrumen; posisi perp dilacak per instrumen (maks. 4,
    tanpa alokasi); `IsFlat` = delta ≤ dust **dan** tidak ada posisi perp; `IsDeltaNeutral` terpisah;
    `TransitionTo` menolak COMPLETED/REPAIRED/CLOSED selama perp terbuka (`OpenDerivativePosition`) dan SPATIAL →
    POSITION_OPEN tanpa hedge (`NotHedged`).
  - `TradeStateMachine.IsAllowedFor(strategy, from, to)`: SPATIAL boleh POSITION_OPEN hanya dari REPAIRING.
  - `V007`: trigger: SPATIAL → POSITION_OPEN hanya dari REPAIRING + `PERP_HEDGE`; `PERP_HEDGE` tidak boleh REPAIRED.
    `v_trade_ledger.open_perp_qty_base` (posisi perp per instrumen), `is_flat` mensyaratkan 0;
    `v_open_exposure` ikut menampilkannya → StartupRecovery melihat hedge terbuka walau label repair salah.
  - Helper SQL `expect_error_like` (SQLSTATE **dan** isi pesan): assert pertama sempat lulus karena alasan lain
    (transisi ilegal di tabel), terdeteksi dan diperbaiki sebelum commit.
- **HIJAU**:
  ```
  R02_PerpHedgeLifecycleTests             6 passed
  R02_SpatialHedgeStateTests              2 passed
  full suite: TOTAL: 167 passed, 0 failed
  PASS  R-02: hedge PERP_HEDGE tidak boleh berakhir REPAIRED → ditolak AR002 (… tidak boleh berakhir REPAIRED …)
  PASS  R-02: delta 0 tetapi TIDAK flat (perp short terbuka)
  PASS  R-02: hedge terbuka terlihat di v_open_exposure (StartupRecovery)
  PASS  R-02: hedge ditutup → realized (fee 4 kaki)  = -1.620025
  PASS  R-02: setelah CLOSED tidak lagi di v_open_exposure
  PASS  R-02: SPATIAL IN_FLIGHT → POSITION_OPEN tetap ditolak → ditolak AR002
  PASS  R-02: SPATIAL REPAIRING → POSITION_OPEN tanpa PERP_HEDGE ditolak → ditolak AR002
  PASS  R-02: REPAIRED dengan perp terbuka tetap di v_open_exposure
  ALL TESTS PASSED
  ```

## R-10 (S2) — tidak ada jalur likuidasi/ADL, venue down, dan partial reduce saat posisi ditahan

- **Test C#**: `Regression/R10_PositionLifecycleTests.cs` (7; API lama → merah perilaku), skenario P06.
  **Test SQL**: trade 1010 (reduce → likuidasi → tutup), 1011 (POSITION_OPEN → STUCK) di blok regression.
- **MERAH (perilaku)**:
  ```
  FAIL R10_PositionLifecycleTests.TransitionExists(PositionOpen, Residual)   PositionOpen -> Residual must be legal
  FAIL R10_PositionLifecycleTests.TransitionExists(PositionOpen, Stuck)      PositionOpen -> Stuck must be legal
  FAIL R10_PositionLifecycleTests.TransitionExists(Exiting, PositionOpen)    Exiting -> PositionOpen must be legal
  FAIL …PartialReduce_ReturnsToPositionOpen_AndStaysNonTerminal              Expected: None  Actual: IllegalTransition
  FAIL …ShortLegLiquidated_WhileHolding_BecomesResidual_…                    Expected: None  Actual: IllegalTransition
  FAIL …TradeGroup_Liquidation_And_PartialReduce_EndToEnd                    Expected: None  Actual: IllegalTransition
  FAIL …VenueUnreachable_WhileHolding_IsStuck_NotSilentlyOpen                Expected: None  Actual: IllegalTransition
  TOTAL: 0 passed, 7 failed
  ```
  SQL: `ERROR:  trade 1010: transisi ilegal EXITING -> POSITION_OPEN`
- **Patch**: transisi `POSITION_OPEN→RESIDUAL`, `POSITION_OPEN→STUCK`, `EXITING→POSITION_OPEN` di
  `TradeStateMachine` dan `V007` (23 transisi). Test kontrak sekarang membaca **semua** migrasi.
- **Temuan sampingan saat mutation check** (kelas R-20): test kontrak v0.2 juga menghitung baris yang
  **di-comment-out** (`-- ('POSITION_OPEN','STUCK')` tetap terbaca → test tetap hijau). Diperbaiki: komentar SQL dibuang
  sebelum parsing; mutasi yang sama sekarang merah (`Expected: 23  Actual: 22`).
- **HIJAU**:
  ```
  R10_PositionLifecycleTests              7 passed
  full suite: TOTAL: 174 passed, 0 failed
  PASS  R-10: urutan state 1010 (reduce → likuidasi → tutup)  = CREATED>IN_FLIGHT>POSITION_OPEN>EXITING>POSITION_OPEN>RESIDUAL>REPAIRING>CLOSED
  PASS  R-10: POSITION_OPEN → STUCK (bukan terminal, closed_at NULL)
  ALL TESTS PASSED
  ```

## R-03 (S2) — haircut dipakai untuk Q* tetapi tidak untuk limit IOC / VWAP / edge

- **Test**: `Regression/R03_HaircutConsistentPricingTests.cs` (4; API lama → merah perilaku), skenario P01.
- **MERAH**:
  ```
  FAIL …BuyLimit_CoversTheLevelsTheOptimizerCounted          Expected: 6000600  Actual: 6000000
  FAIL …NetEdge_IsComputedAtTheHaircutVwap_NotOverstated     Expected: 0.9179775  Actual: 0.948 (Diff 0.0300225)
  FAIL …SellSide_HaircutAlsoMovesTheSellLimit                Expected: 6019500  Actual: 6020000
  TOTAL: 1 passed, 3 failed        (FullLiquidity_IsUnchanged lulus: perilaku tanpa haircut tidak berubah)
  ```
- **Patch**: `SpatialArbPricer`: walk VWAP kedua kaki dengan `p.HaircutPpm` yang sama dengan optimizer. Level marginal
  walk tersebut = limit IOC, sehingga IOC tetap terisi penuh walau hanya fraksi haircut yang benar-benar tersedia;
  edge dihitung pada VWAP konservatif itu.
- **HIJAU**: `R03_HaircutConsistentPricingTests 4 passed`; full suite `TOTAL: 178 passed, 0 failed`.

## R-04 (S2) — `MarkInvalid`/`MarkResyncing` dari thread lain merusak versi seqlock

- **Test**: `Regression/R04_ExternalBookMarksTests.cs` (4; API lama → merah perilaku), skenario P12/P22.
- **MERAH (deterministik)**:
  ```
  FAIL …ConcurrentExternalMarks_NeverCorruptTheSeqlockVersion   Expected: 2  Actual: 16000002   (thread eksternal masuk section writer)
  FAIL …InvalidOutranksResync_AndFirstFaultIsKept               Expected: Invalid  Actual: Resyncing (Mark terakhir menang)
  TOTAL: 2 passed, 2 failed
  ```
- **MERAH (probabilistik, storm 2 × 2.000.000 Mark per ronde, 3 run, 1 kena)** — race sungguhan yang ditandai review:
  ```
  XunitException: round 1: version 14199383 left ODD by concurrent marks
  XunitException: 777508 torn reads among 791482 accepted
  ```
  Dua run lain tidak memicu race (race nyata, tidak deterministik). Karena itu test di repo memakai assert deterministik
  (versi tidak boleh berubah oleh thread eksternal) ditambah stress yang wajib tetap 0 torn.
- **Patch** `OrderBookL2`: Mark* = *request flag* (`Interlocked.CompareExchange`, Invalid > Resync, fault pertama dipertahankan), tidak
  pernah menulis versi; `TryCopyTo`/`Health`/`Fault` langsung memperlihatkan request; `ApplyDelta` menolak selama request
  tertunda; `ApplySnapshot` mengonsumsinya (request yang datang setelahnya tetap berlaku). `EndWrite` memakai nilai ganjil
  milik section itu sendiri (tidak membaca ulang); `BeginWrite` memulihkan paritas bila menemukan versi ganjil
  (`ParityRepairs`, seharusnya selalu 0).
- **HIJAU**: `R04_ExternalBookMarksTests 4 passed`; storm 2 × 2.000.000: 3/3 run `4 passed`; full suite `182 passed, 0 failed`.

## R-07 (S2) — book beku 4,9 s lolos, tidak ada cek skew antar-book, latency negatif diterima

- **Test**: `Regression/R07_StaleGuardBehaviourTests.cs` (3, API lama), `Regression/R07_StaleGuardLimitsTests.cs` (4, API baru);
  skenario P08 dengan `RiskLimits` default.
- **MERAH (perilaku, API lama)**:
  ```
  FAIL …BookFrozen4900ms_WithLiveSocket_IsNotFresh                4.9 s old book on a live socket reported Fresh
  FAIL …PairWithOneBook4900msOlder_IsNotFresh                     pair skew 4.9 s reported Fresh
  FAIL …NegativeLatency_BeyondClockUncertainty_IsClockUncertain   Expected: ClockUncertain  Actual: Fresh
  TOTAL: 0 passed, 3 failed
  ```
- **MERAH (compile)**, test batas baru: `No overload for method 'From' takes 2 arguments`, `'Freshness' does not contain a
  definition for 'BookTooOld'` / `'PairSkew'`, `'RiskLimits' does not contain a definition for 'MaxSpatialBookAgeMs'`.
- **Patch**: `Freshness.BookTooOld` (umur maks per strategi: spatial 1000 ms, funding 5000 ms) dan `Freshness.PairSkew`
  (skew terima spatial 500 ms, funding 2000 ms); latency < −(uncertainty + 1 ms resolusi timestamp venue) →
  `ClockUncertain`; semua perbandingan ditulis NaN-safe (`!(x <= limit)`). `FreshnessLimits.From(limits)` = batas
  spatial (paling ketat); `From(limits, strategy)` eksplisit. `RiskLimits.Validate` mengecek konsistensi batas baru.
- **HIJAU**: `R07_StaleGuardBehaviourTests 3 passed`, `R07_StaleGuardLimitsTests 4 passed`; full suite `189 passed, 0 failed`.
