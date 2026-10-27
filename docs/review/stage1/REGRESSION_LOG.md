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

## R-08 (S2) — `Replace` mengganti tick/step dengan key sama → book lama dibaca dengan grid baru

- **Test**: `Regression/R08_SpecGridChangeBehaviourTests.cs` (3, API lama), `Regression/R08_GridGenerationTests.cs` (3, API baru);
  skenario P15/P19.
- **MERAH (perilaku)**:
  ```
  FAIL …StepChange_BookParsedWithOldStep_IsNotPriced   Expected: RejectStale  Actual: Execute   (beli 0,1 BTC vs depth riil 0,010)
  FAIL …TickChange_BookParsedWithOldTick_IsNotPriced   Expected: RejectStale  Actual: Execute   (bid terbaca 601.500)
  TOTAL: 1 passed, 2 failed        (StatusOnlyChange_KeepsBooksUsable lulus: perubahan non-grid tidak terdampak)
  ```
- **MERAH (compile)**: `'InstrumentSpec' does not contain a definition for 'GridGeneration'`, `'BookView' does not contain a
  definition for 'GridGeneration'`.
- **Patch**: `InstrumentSpec.GridGeneration`; `Replace` menaikkannya hanya bila tick/step berubah;
  `OrderBookL2.ApplySnapshot(…, gridGeneration)` mencapnya ke book (delta mewarisi), `TryCopyTo`/`BookView` membawanya;
  `SpatialArbPricer` → `RejectStale` dan `RepairPlanner` melewati kandidat bila generasi book ≠ generasi spec (fail-closed sampai
  re-snapshot).
- **HIJAU**: `R08_GridGenerationTests 3 passed`, `R08_SpecGridChangeBehaviourTests 3 passed`; full suite `195 passed, 0 failed`.

## R-05 (S2, NEEDS-DATA → **REJECTED berdasarkan data**) — urutan store writer seqlock di ARM64

Tidak bisa dibuat merah di sandbox (x64). Runner `ubuntu-24.04-arm` kini tersedia untuk repo private
([changelog GitHub 2026-01-29](https://github.blog/changelog/2026-01-29-arm64-standard-runners-are-now-available-in-private-repositories)),
jadi datanya dikumpulkan di CI: job manual `seqlock-arm64` (`df8d55e`) = listing JIT full-opt
(`DOTNET_JitDisasm`) + stress seqlock & R-04 20 kali. Dijalankan dua kali:

| Run | Kode OrderBookL2 | Instruksi atomik writer (anotasi CI) | Barrier | Stress ×20 |
|---|---|---|---|---|
| 40470728583 | **v0.2** (identik `7cdf6a4`; branch sementara `tmp/r05-v02-evidence`) | `ApplyDelta`: `ldaddal x3, x1, [x1]`; `ApplySnapshot`: `ldaddal x3, x3, [x21]` (BeginWrite di-inline) | – | 20/20 lulus |
| 40470466580 | sesudah R-04 (`df8d55e`) | `BeginWrite`: `ldaddal x2, x2, [x1]` (tidak di-inline) | – | 20/20 lulus |

- Hipotesis R-05 = writer di-lower ke `staddl` / `ldaddal …, xzr` (tanpa acquire, karena hasil increment dibuang).
  **Data: tidak terjadi.** RyuJIT .NET 8 memakai register tujuan nyata (x1/x2/x3), sehingga LDADDAL = acquire + release,
  yang menurut model memori aarch64 mengurutkan store versi-ganjil sebelum store data berikutnya. Stress di ARM64 lulus 20/20.
- Sejak R-04, hasil increment **dipakai** (`EndWrite(v)`), sehingga lowering ke XZR tidak mungkin secara struktural.
  Komentar di `BeginWrite` menandai syarat ini.
- **Keputusan**: fence defensif `MemoryBarrier()` yang semula direncanakan di FINDINGS_LOG **tidak dipasang**: data
  menunjukkan fence itu redundan. Job `seqlock-arm64` tetap ada untuk cek ulang saat upgrade runtime (.NET 9/10) atau CPU
  tanpa LSE. Bila Jansen tetap ingin fence asuransi, cukup satu baris di `BeginWrite` (`if (!X86Base.IsSupported)
  Interlocked.MemoryBarrier();`).
- Branch sementara `tmp/r05-v02-evidence` tidak bisa dihapus dari sandbox (proxy menolak delete); aman dihapus manual.

## R-09 (S2) — `NetworkId` 0 (tak terpetakan) menjadi wildcard → route palsu Open

- **Test**: `Regression/R09_UnmappedNetworkTests.cs` (4), skenario P04. Tiga test pertama memakai API lama.
- **MERAH (perilaku, 3 test API lama)**:
  ```
  FAIL …TwoUnmappedNetworks_DoNotFormARoute                       Assert.False() Failure (route Open via network 0)
  FAIL …UnmappedNetwork_DoesNotCountAsDepositOrWithdrawEnabled    Assert.False() Failure
  FAIL …UnmappedNetworks_KeepTheWalletTrapFilterOn                Expected: RejectSanity  Actual: Execute (edge 120 bps lolos)
  TOTAL: 0 passed, 3 failed
  ```
- **MERAH (compile, test ke-4)**: `Cannot implicitly convert type 'void' to 'int'` (Update tidak melaporkan network tak
  terpetakan), `'NetworkId' does not contain a definition for 'IsValid'`.
- **Patch**: `NetworkId.IsValid` (0 = tak terpetakan); `FindRoute` dan cek deposit/withdraw mengabaikan network tak
  terpetakan, dan route yang mungkin tersembunyi di baliknya dilaporkan `Unknown` (= tertutup untuk guard), bukan Open;
  `WalletStatusBook.Update` mengembalikan jumlah network tak terpetakan (adapter → alert).
- **HIJAU**: `R09_UnmappedNetworkTests 4 passed`; full suite `199 passed, 0 failed`.

## R-06 (S2) — `InventoryLedger` meng-overstate Available (timeout & event saldo di ms yang sama)

- **Test**: `Regression/R06_SameMillisecondBalanceTests.cs` (3, API lama), `Regression/R06_UnsettledReservationTests.cs`
  (2, API baru), skenario P07. Test lama `InventoryLedgerTests.SettleTimeout_ReleasesTerminalReservations` yang
  meng-*codify* overstatement (assert Available = 900) diganti `SettleTimeout_FlagsForReconcile_NeverReleases` (R-20 a).
- **MERAH (perilaku, ms sama)**:
  ```
  FAIL …BalanceEventInTheSameMs_DoesNotReleaseTheFilledReservation   Expected: 0.02  Actual: 0.05
  FAIL …TerminalReportAfterASameMsBalance_DoesNotReleaseImmediately   Assert.True() Failure
  TOTAL: 1 passed, 2 failed
  ```
- **MERAH (timeout)**: compile (`'InventoryLedger' does not contain a definition for 'FlagUnsettled'` / `'NeedsReconcile'`);
  perilaku lama terekam di P07 Claude-R: `expired=1 Available=0.05 BTC; true venue free = 0.02 BTC -> overstated by 0.03`,
  `a second opportunity reserving 0.04 BTC succeeds: True`.
- **Patch**: rilis hanya oleh event saldo **sesudah** (strict) waktu terminal, baik di `OnBalance` maupun `MarkTerminal`;
  `ExpireSettled` (melepas) diganti `FlagUnsettled` (menandai slot untuk rekonsiliasi REST, tidak melepas, dilaporkan sekali);
  `NeedsReconcile(slot)`; hasil REST (waktu server venue) masuk lewat `OnBalance` dan menyelesaikannya.
- **HIJAU**: `R06_SameMillisecondBalanceTests 3 passed`, `R06_UnsettledReservationTests 2 passed`; full suite
  `204 passed, 0 failed` (termasuk zero-alloc InventoryLedger dengan event saldo strictly later).

## R-12 (S3) — route quote tertutup + `ReferencePx` 0/NaN → cap dilewati (fail-open)

- **Test**: `Regression/R12_SanityReferencePriceTests.cs` (5; API lama), skenario P02.
- **MERAH**:
  ```
  FAIL …QuoteRouteClosed_WithoutAUsableReferencePrice_FailsClosed(0)     refPx=0: Execute cap=Infinity
  FAIL …QuoteRouteClosed_WithoutAUsableReferencePrice_FailsClosed(NaN)   refPx=NaN: Execute cap=Infinity
  FAIL …QuoteRouteClosed_WithoutAUsableReferencePrice_FailsClosed(-1)    refPx=-1: Execute cap=Infinity
  TOTAL: 2 passed, 3 failed     (+∞ kebetulan sudah fail-closed: cap = x/∞ = 0)
  ```
- **Patch**: `SanityGuard`: bila route quote tidak Open dan `ReferencePx` tidak finite atau ≤ 0 → `RejectSanity` +
  `SanityFlags.ReferencePriceInvalid`, cap 0.
- **HIJAU**: `R12_SanityReferencePriceTests 5 passed`; full suite `209 passed, 0 failed`.

## R-11 (S3) — cap inventory non-replenishable dihitung dari sisa per evaluasi → 22 trade menguras 99,8 %

- **Test**: `Regression/R11_NonReplenishableCapInvariantTests.cs` (1, API lama: invarian ≤ 25 %),
  `Regression/R11_RouteBudgetTrackerTests.cs` (2, API baru), skenario P03.
- **MERAH (perilaku)**:
  ```
  FAIL …RepeatedTradesAtTheCap_NeverConsumeMoreThanTheFraction
      consumed 100.00 % of the non-replenishable inventory (limit 25 %)
  TOTAL: 0 passed, 1 failed
  ```
- **MERAH (compile)**: `The type or namespace name 'RouteBudgetTracker' could not be found`, `'SanityInput' does not contain a
  definition for 'SellVenueBaseBudget'`, `'NonReplenishableBudget' could not be found`, `'SanityFlags' … 'BudgetUnknown'`.
- **Patch**: `Risk/RouteBudget.cs`: `NonReplenishableBudget` (snapshot saat route tutup + pemakaian kumulatif) dan
  `RouteBudgetTracker` (Open → reset; pertama kali tidak Open → snapshot; `RecordUse` setelah fill). `SanityGuard`: cap =
  min(inventory sekarang, fraksi × snapshot − terpakai); budget tidak diberikan → `RejectInventory` + `BudgetUnknown`
  (fail-closed). Test sanity lama memberi budget eksplisit (snapshot = inventory sekarang → cap sama dengan v0.2 untuk trade pertama).
- **HIJAU**: `R11_NonReplenishableCapInvariantTests 1 passed`, `R11_RouteBudgetTrackerTests 2 passed` (konsumsi tepat
  0,05 = 25 % dari 0,2 BTC lalu berhenti); full suite `212 passed, 0 failed`.
