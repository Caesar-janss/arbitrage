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

## R-13 (S3) — repair BUY di spot yang fee-nya dipotong dari base tidak di-gross-up

- **Test**: `Regression/R13_RepairBaseFeeGrossUpTests.cs` (3), skenario P09.
- **MERAH (compile)**: `error CS0117: 'RepairCandidate' does not contain a definition for 'FeeAsset'`: kode lama tidak bisa
  diberi tahu di mana fee mendarat. Perilaku lama (P09 Claude-R):
  ```
  iter 0: residual -0.01 -> buy 0.010000 BTC, receive 0.009990000
  iter 1: residual -0.000010000 BTC (=-0.6000 USDT) verdict BelowVenueMinimum
  ```
- **Patch**: `RepairCandidate.FeeAsset`; BUY + fee base → `LotNormalizer.GrossUpBuyLots` (diterima ≥ residual, sama seperti
  kaki entry); SELL + fee base → ukuran dikurangi `/(1+f)`; model biaya per unit residual yang benar-benar diratakan
  (`vwap/(1−f)` untuk beli); `PlannedQtyCanon` = residual yang diratakan.
- **HIJAU**: `R13_RepairBaseFeeGrossUpTests 3 passed` (beli 0,010011 → terima 0,010000989 ≥ 0,01, lebih < 1 lot);
  full suite `215 passed, 0 failed`.

## R-18 (S3) — gate funding fail-open untuk NaN/∞; `MinFundingNetApr` tidak divalidasi

- **Test**: `Regression/R18_FundingGateNanTests.cs` (7; API lama), skenario P05.
- **MERAH**:
  ```
  FAIL …NaNGate_NeverEnters                                  NaN gate → Enter
  FAIL …NonFiniteHold_IsInvalidInput(Infinity)               Expected: RejectInvalidInput  Actual: Enter
  FAIL …NonFiniteInputs_AreInvalid                           Expected: RejectInvalidInput  Actual: RejectNegativeExpectedPnl (Leverage = +∞ lolos validasi)
  FAIL …RiskLimits_EveryDoubleProperty_IsCheckedForNaN       MaxConnectionSilenceMs = NaN is not reported: [silence limits > 0]
  FAIL …RiskLimits_RejectNonFiniteFundingAndOtherDoubles     Assert.True() Failure (MinFundingNetApr = NaN → 0 error)
  TOTAL: 2 passed, 5 failed
  ```
- **Patch**: `FundingGate.IsValid` (semua batas finite, > 0 / ≥ 1); `Evaluate` mewajibkan semua input finite dan gate valid,
  serta setiap gate ditulis `!(nilai di dalam batas)`. Jarak likuidasi NaN tetap ditolak oleh gate likuidasi sendiri
  (verdict lama `RejectLiquidationDistance` dipertahankan). `RiskLimits.Validate`: semua properti double wajib finite
  (dengan nama properti di pesan) + rentang `MinFundingNetApr`; test refleksi menjaga agar properti double baru tidak lolos.
- **HIJAU**: `R18_FundingGateNanTests 7 passed`; full suite `222 passed, 0 failed`.

## R-19 (S3) — dust gross-up ada di venue BELI tetapi dinilai pakai VWAP venue JUAL

- **Test**: `Regression/R19_ResidualMarkTests.cs` (2; API lama), skenario P21 + kasus fee base di kaki jual.
- **MERAH**:
  ```
  FAIL …LongDust_IsValuedAtTheBuyVenueBid      Expected: 4.88521  Actual: 4.92437  Diff: 0.03916  (9,2 % dari net)
  FAIL …ShortDust_IsValuedAtTheSellVenueAsk    Expected: -0.6025  Actual: -0.602   Diff: 0.0005
  TOTAL: 0 passed, 2 failed
  ```
- **Patch**: `SpatialArbPricer`: residual positif (koin lebih di venue beli) dinilai di **bid venue beli**; residual negatif
  (fee base di kaki jual → kekurangan di venue jual) dinilai di **ask venue jual** (biaya beli kembali).
- **HIJAU**: `R19_ResidualMarkTests 2 passed` (net 0,38461, bukan 0,42377); full suite `224 passed, 0 failed`.

## R-17 (S3) — restart setelah jam mundur → Snowflake menerbitkan ulang ID yang sama

- **Test**: `Regression/R17_SnowflakeRestartTests.cs` (3), skenario P16.
- **MERAH (compile)**: `The best overload for 'SnowflakeIdGenerator' does not have a parameter named 'resumeAfterUnixMs'`,
  `'SnowflakeIdGenerator' does not contain a definition for 'LastIssuedUnixMs'`: kode lama tidak bisa diberi titik
  lanjut setelah restart. Perilaku lama (P16 Claude-R): `id before restart 41943040020480, first id after restart at same
  wall ms 41943040020480: duplicate=True`.
- **Patch**: konstruktor wajib `resumeAfterUnixMs` (0 = node tanpa riwayat): state awal = (ms resume, sequence habis) →
  ID pertama punya ms > resume walau jam di belakang; `LastIssuedUnixMs` untuk journal host. Doc: host mengambil
  max(journal, max id node di DB) + margin untuk ID yang terbit tetapi belum tersimpan.
- **HIJAU**: `R17_SnowflakeRestartTests 3 passed` (5.000 ID sebelum + 5.000 sesudah restart, tanpa duplikat);
  full suite `227 passed, 0 failed`.

## R-14 (S3) — tiga definisi "flat" (C# dust, minimum venue, 1 USDT di DB)

- **Test C#**: `Regression/R14_FlatDefinitionBehaviourTests.cs` (1, API lama), `Regression/R14_DustAcceptanceTests.cs` (4, API baru),
  skenario P13. **Test SQL**: trade 1012 di blok regression (residual 0,00008 BTC = 4,8 USDT < min notional 5).
- **MERAH (perilaku, C#)**:
  ```
  FAIL …Terminal_WithAnUnaccountedResidual_IsRefused   REPAIRED accepted with 0.00008 BTC unaccounted
  TOTAL: 0 passed, 1 failed
  ```
- **MERAH (compile, C#)**: `'TransitionError' does not contain a definition for 'UnacceptedResidual'`, `'TradeGroup' does not contain
  a definition for 'AcceptResidualAsDust'` / `'DustAcceptedCanon'`, `'RepairPlan' does not contain a definition for 'DustQuote'`.
- **MERAH (SQL)**: tanpa kolom baru → `ERROR: column "dust_accepted_base" of relation "trade_group" does not exist`; probe dengan
  baris UPDATE dihapus (perilaku v0.2) → `FAIL  R-14: dust yang diterima engine = flat di DB  (got f)` (4,8 > dust_quote 1,0).
- **Patch**:
  - C#: `TradeGroup.AcceptResidualAsDust()` mencatat residual yang di-write-off (perp tidak pernah boleh jadi dust);
    `IsFlat` = tanpa perp terbuka **dan** |delta − dust diterima| ≤ toleransi; terminal mewajibkan toleransi 0
    (`UnacceptedResidual`); `RepairPlan.DustQuote` untuk alert.
  - SQL (`V007`): `trade_group.dust_accepted_base`; `is_flat` = `net_base_delta = COALESCE(dust_accepted_base, 0)` dan
    `open_perp_qty_base = 0` (aturan yang sama dengan C#); `dust_quote` kini hanya ambang alert (`dust_over_limit`);
    `fn_consecutive_losses` menghitung trade terminal yang tidak flat sebagai rugi (PnL tidak diketahui).
- **HIJAU**: `R14_DustAcceptanceTests 4 passed`, `R14_FlatDefinitionBehaviourTests 1 passed`; full suite `232 passed, 0 failed`;
  DB: `PASS R-14: dust yang diterima engine = flat di DB`, `realized … = -1.1952`, `dust_accepted_quote = 4.8`,
  `loss streak … = 3`, `fill terlambat → tidak flat`, `ALL TESTS PASSED` (assert lama, termasuk streak, tidak berubah).

## R-15 (S3) — quote yang ditolak tidak bisa disimpan di `trd.opportunity`

- **Test C#**: `Regression/R15_RejectQuoteFieldsTests.cs` (2, API lama), `Regression/R15_OpportunityRowContractTests.cs`
  (3, API baru + teks migrasi), skenario P20. **Test SQL**: opportunity 601 (near-miss), 602 (stale), 603 (EXECUTE tidak lengkap).
- **MERAH (perilaku, C#)**:
  ```
  FAIL …NearMiss_RejectThreshold_HasTobButNoFakeZeroVwap   vwap 0/0
  FAIL …StaleBook_RejectStale_HasNoFakeZeroPrices          tob 0/0
  TOTAL: 0 passed, 2 failed
  ```
- **MERAH (compile, C#)**: `'SpatialQuote' does not contain a definition for 'HasExecutionPricing'`.
- **MERAH (SQL)**: `ERROR: null value in column "qty_base" of relation "opportunity_20261022" violates not-null constraint`.
- **Patch**:
  - C#: kontrak persistensi: nilai yang tidak dihitung = NaN (→ NULL), tidak pernah 0; `SpatialQuote.HasExecutionPricing`;
    reject sebelum walk memakai `EdgeBreakdown.NotComputed` (NaN) dan tetap membawa TOB bila diketahui.
  - SQL (`V007`): 12 kolom eksekusi `DROP NOT NULL`; constraint `opportunity_execute_complete` mewajibkannya hanya untuk
    EXECUTE (constraint level tabel partisi, berlaku ke semua partisi).
- **HIJAU**: `R15_OpportunityRowContractTests 3 passed`, `R15_RejectQuoteFieldsTests 2 passed`; full suite `237 passed, 0 failed`;
  DB `PASS R-15: reject tanpa VWAP tersimpan … = 2`, `funnel tetap terhitung`, `EXECUTE tanpa VWAP ditolak → 23514
  (opportunity_execute_complete)`, `ALL TESTS PASSED`.

## R-16 (S3) — basis expected ≠ realized → capture ratio terinflasi; TradeGroup tanpa funding

- **Test C#**: `Regression/R16_ExpectedVsRealizedBasisTests.cs` (2). **Test SQL**: trade 1001 diberi komponen ekspektasi
  (buffer slippage 0,08 + rebalance 0,02 = opp 501), capture per trade & harian, `v_rebalance_cost_daily`.
- **MERAH (compile, C#)**: `'EdgeBreakdown' does not contain a definition for 'ExpectedCashQuote'`, `'TradeGroup' does not contain a
  definition for 'ApplyFunding'` / `'FundingQuote'`.
- **MERAH (SQL)**: `ERROR: column "exp_slippage_buffer_quote" of relation "trade_group" does not exist`. Bukti perilaku v0.2:
  capture 1001 = realized 0,4485 / exp_net 0,3485 = **1,29** untuk trade yang terisi persis di VWAP model; capture harian
  −3,0823.
- **Patch**:
  - C#: `EdgeBreakdown.ExpectedCashQuote` = net + buffer slippage + rebalance (basis yang direalisasikan fill; `NetQuote` tetap
    gate entry); `TradeGroup.ApplyFunding` / `FundingQuote`, `CashFlowQuote` = model yang sama dengan `v_trade_ledger`.
  - SQL (`V007`): `trade_group.exp_slippage_buffer_quote`, `exp_rebalance_quote`; ledger `exp_cash_quote`, `capture_ratio`;
    `shortfall_quote` dan `v_daily_pnl.capture_ratio_*` memakai exp_cash (+ `expected_cash_filled`, `expected_rebalance_quote`);
    view baru `rpt.v_rebalance_cost_daily` membandingkan rebalance yang diasumsikan vs fee `acct.transfer` riil.
- **HIJAU**: `R16_ExpectedVsRealizedBasisTests 2 passed` (cash TradeGroup = ExpectedCashQuote; 1006 dengan funding = −1,951285
  = DB); full suite `239 passed, 0 failed`; DB `capture ratio = 1 (v0.2: 1.29)`, `shortfall = 0`, capture harian **−2,5986**
  (sengaja berubah dari −3,0823 karena basisnya dikoreksi), `v_rebalance_cost_daily 0.02 vs 1.5`, `ALL TESTS PASSED`.

## R-20 (S4) — test yang lulus karena alasan salah / kasus penting hilang

Daftar (a)–(f) dari laporan Claude-R dan penutupnya:

| | Masalah | Ditutup oleh |
|---|---|---|
| a | `SettleTimeout_ReleasesTerminalReservations` meng-*codify* Available yang overstated | diganti `SettleTimeout_FlagsForReconcile_NeverReleases` (commit R-06) |
| b | semua test pricer `HaircutPpm = 1e6` | `R03_HaircutConsistentPricingTests` (haircut 50 %, kedua sisi) |
| c | semua test sanity `ReferencePx = 60000` | `R12_SanityReferencePriceTests` (0, NaN, −1, +∞) |
| d | seqlock hanya x86, tanpa `MarkInvalid` konkuren | `R04_ExternalBookMarksTests` + job CI `seqlock-arm64` (R-05) |
| e | `RepairMethod` tanpa MANUAL/`ToDbCode`; Side/OrderType/TimeInForce/MarketType tanpa kontrak SQL | `R20_EnumDbContractTests` (commit ini) |
| f | brute force Q* hanya hurdle 0, tanpa cap/haircut | `R20_OptimizerBruteForceTests` (commit ini) |

Ditambah temuan sejenis saat mengerjakan ronde ini: test kontrak transisi menghitung baris SQL yang di-comment-out
(diperbaiki di R-10; komentar SQL kini dibuang di **semua** test kontrak), dan satu assert SQL R-02 yang sempat lulus
karena alasan lain (→ helper `expect_error_like`).

- **MERAH (e, compile)**: `'RepairMethod' does not contain a definition for 'ToDbCode'`, `'Side' …`, `'OrderType' …`,
  `'TimeInForce' does not contain a definition for 'ToDbCode'`. Mutation check sesudah patch (SQL diubah sementara:
  `POST_ONLY`→`POSTONLY`, `MANUAL` dihapus) → `FAIL … time_in_force: SQL [FOK,GTC,IOC,POSTONLY] vs C# [FOK,GTC,IOC,POST_ONLY]`,
  `FAIL … repair_method: SQL […] vs C# […,MANUAL,…]`.
- **(f) bukan bug kode** (optimizer v0.2 benar; test baru hijau di kode v0.2). Merahnya dibuktikan dengan **mutation testing**:
  4 mutan yang tidak tertangkap test v0.2 semuanya tertangkap test baru.

  | Mutan pada `CrossedBookOptimizer` | Test v0.2 `MatchesBruteForceArgmax` | Test baru `R20_OptimizerBruteForceTests` |
  |---|---|---|
  | M1 hurdle diabaikan | 4 passed | **FAIL** `net 0.0302 vs brute force 0` |
  | M2 haircut diabaikan | 4 passed | **FAIL** `net 0.1330 vs brute force 0.0765` |
  | M3 cap notional dihitung pakai bid | 4 passed | **FAIL** `net 0.35801 vs brute force 0.35900` |
  | M4 cap level off-by-one | 4 passed | **FAIL** `net 0.9156 vs brute force 0.1764` |

  Versi pertama test baru juga lulus pada semua mutan: buku acak hampir tidak pernah crossed. Karena itu test kini
  mewajibkan cakupan (≥ 100 trial crossed, ≥ 10 berhenti karena hurdle, ≥ 20 karena cap, ≥ 20 memakai haircut).
- **Patch**: `ToDbCode` untuk `Side`, `OrderType`, `TimeInForce`, `RepairMethod` (+ `Manual`); `StripSqlComments` di semua
  test kontrak.
- **HIJAU**: `R20_EnumDbContractTests 4 passed`, `R20_OptimizerBruteForceTests 3 passed`; full suite `246 passed, 0 failed`.

---

## Ringkasan ronde 1 (v0.3)

| | Jumlah |
|---|---:|
| Temuan Claude-R | 20 (19 CONFIRMED + di-fix, 1 REJECTED berdasarkan data: R-05) |
| Merah dicatat sebelum patch | 19 temuan (perilaku pada API lama bila bisa diekspresikan, compile bila API baru; R-20 f via mutation) |
| Test C# | 150 (v0.2) → **250** (v0.3), harness offline 250/250 |
| DB | `ALL TESTS PASSED`, assertion PASS 95 → 119 (blok regression di-rollback) |
| Benchmark | semua **0 B/op**; latency setara v0.2 dalam noise container (§4.3a STAGE1_CORE) |
| Temuan sampingan saat menulis test | 3: test kontrak menghitung SQL yang di-comment-out; assert SQL R-02 lulus karena alasan lain; brute-force R-20 versi pertama tidak menangkap mutan apa pun |
| Belum | C-01/C-02 (isi belum diterima), review Gemini, persetujuan Jansen → **Tahap 1 tetap REVIEW TERBUKA** |

---

# Ronde 2: review ChatGPT (PR #2 review 5732047500 @ `4bf87f7`)

Temuan baru: C-07 (S1), C-08 (S2), serta C-01/C-02 (S2) yang sebelumnya menunggu triase. C-03…C-06 = DUPLICATE
R-06/R-03/R-13/R-01 (reviewer mengonfirmasi reproduksi aslinya kini tertutup). Urutan kerja: S1 dulu.

## C-07 (S1) — saldo stale dengan timestamp sama menimpa saldo yang lebih baru

- **Test**: `Regression/C07_StaleBalanceOverwriteTests.cs` (6, API lama: urutan reviewer persis, perubahan tanpa reservasi
  di ms yang sama, kedua urutan tiba, resolusi oleh observasi strictly later, duplikat, model acak 1.500 riwayat) dan
  `Regression/C07_BalanceSequencingTests.cs` (8, API baru: kode hasil, stream berurut, replay, waktu mundur, stream vs
  snapshot, model acak dengan stream berurut, stream tanpa snapshot tidak pernah konflik).
- **Model acak**: riwayat venue asli (fill kita yang sudah di-reserve, perubahan tanpa reservasi seperti fee/transfer/deposit,
  beberapa perubahan per ms); event stream dikirim berurutan tetapi terlambat; snapshot diberi stamp waktu venue saat
  diambil dan dikirim dalam urutan acak. Invariant tiap langkah: `Available ≤ F(P) − Σ fill kita sesudah P`. Coverage guard:
  ≥ 200 kejadian "observasi lama tiba dengan stamp terbaru dan nilai berbeda".
- **MERAH (perilaku, API lama)**:
  ```
  FAIL …ReviewerSequence_StaleSameMsSnapshot_DoesNotRevivePaidFunds         Expected: 20  Actual: 100
  FAIL …UnreservedChangeInTheSameMs_IsNotUndoneByAnOlderSnapshot             Expected: 95  Actual: 100
  FAIL …ConflictKeepsTheLowerValue_InEitherArrivalOrder                      Expected: 50  Actual: 70
  FAIL …StrictlyLaterObservation_ResolvesTheConflict                         Assert.True() Failure
  FAIL …RandomInterleavings_NeverOverstateRelativeToTheNewestDeliveredObservation
       seed 0 step 6 (DeliverSnapshot): Available 100 > bound 97 (newest delivered position 1, true value 97)
  TOTAL: 1 passed, 5 failed
  ```
  (yang lulus: `IdenticalSameMsObservation_IsADuplicate_NotAConflict`, guard agar fix tidak membuat konflik palsu).
  `C07_BalanceSequencingTests`: merah compile (`BalanceUpdate`, overload `OnBalance(…, long streamSequence)`,
  `HasBalanceConflict` belum ada).
- **Patch** (`InventoryLedger`): `OnBalance` mengembalikan `BalanceUpdate`. Tanpa nomor urut: `ms < last` → Stale;
  `ms == last` → Duplicate bila nilai sama, selain itu Conflict (simpan nilai terkecil, `HasBalanceConflict`,
  `NeedsReconcile`); `ms > last` → Applied, rilis reservasi terminal strictly earlier, konflik selesai. Dengan nomor urut
  (stream user, 1 sumber per slot): seq naik → diurutkan menurut kiriman di dalam 1 ms; seq tidak naik → diperlakukan tanpa
  urutan; waktu venue mundur → Conflict. Kontrak host di doc class dan `STAGE1_CORE.md` §0/§3.9.
- **HIJAU**: `C07_StaleBalanceOverwriteTests 6 passed`, `C07_BalanceSequencingTests 8 passed`; full suite
  `264 passed, 0 failed` (termasuk zero-alloc `InventoryLedger`).
- **Mutation** (6 mutan, semua tertangkap): ambil nilai masuk saat konflik (7 test merah, termasuk kedua model acak); tanpa
  flag konflik (4); abaikan `_orderedAtLast` (2, termasuk model acak berurut); replay dianggap berurut (1); waktu mundur
  dibuang diam-diam (1); konflik tidak mematikan urutan (2, termasuk model acak berurut).
- **Belum terbukti (NEEDS-DATA, Tahap 2)**: lag cache REST wallet tiap venue terhadap stream (menentukan batas bawah stamp
  snapshot bila venue tidak memberi waktu as-of).

## C-08 (S2) — hedge spatial tidak bisa kembali POSITION_OPEN setelah partial exit

- **Test C#**: `Regression/C08_SpatialHedgePartialExitTests.cs` (5, API lama; fixture sama dengan R-02: spot +0.01 / perp
  −0.01 → `POSITION_OPEN → EXITING`; jual 0.004 spot + buy-back 0.004 perp → `POSITION_OPEN`; sisa ditutup → `CLOSED`;
  perp habis tapi spot tersisa → `NotHedged` lalu `RESIDUAL`; entry `IN_FLIGHT → POSITION_OPEN` tetap ditolak).
- **Test DB**: blok baru "regression ronde 2" di `db/tests/test_schema.sql` (di-rollback), trade 1013–1015: partial unwind
  0.004/0.004 lalu tutup, `v_trade_ledger` (delta 0, tidak flat, `open_perp_qty_base` 0.006), `v_open_exposure`, urutan
  `trade_state_log`, dan dua penolakan.
- **MERAH (perilaku, API lama)**:
  ```
  FAIL …PartialUnwindOfBothLegs_ReturnsToPositionOpen          Expected: None  Actual: StrategyNotAllowed
  FAIL …AfterAPartialUnwind_TheRestUnwindsToClosed             Expected: None  Actual: StrategyNotAllowed
  FAIL …StrategyRule_AllowsExitingToPositionOpen_ForSpatial    Assert.True() Failure
  TOTAL: 2 passed, 3 failed
  ```
  DB (migrasi V001…V007):
  ```
  ERROR:  trade 1013: SPATIAL hanya masuk POSITION_OPEN dari REPAIRING dengan PERP_HEDGE (dari EXITING, metode PERP_HEDGE)
  ```
- **Patch**: `TradeStateMachine.IsAllowedFor(Spatial, from, PositionOpen)` menerima `from` REPAIRING **atau** EXITING.
  Migrasi baru `V008__review_round2_fixes.sql` (V007 tidak diubah): trigger menerima `OLD.state IN ('REPAIRING','EXITING')`
  dengan `repair_method = 'PERP_HEDGE'`. Cek "perp masih terbuka" tetap di `TradeGroup` (`NotHedged`); SQL hanya menjaga
  label, sesuai catatan reviewer bahwa kedua guard tidak identik.
- **HIJAU**: `C08_SpatialHedgePartialExitTests 5 passed`; full suite `269 passed, 0 failed`; DB `ALL TESTS PASSED`,
  assertion `NOTICE: PASS` 118 → 124. (Angka "119" di ringkasan ronde 1 menghitung baris `ALL TESTS PASSED`; jumlah
  assertion sebenarnya 118.)
- **Mutation**: C# "izinkan dari state mana pun kecuali CREATED" → 2 test merah. SQL "izinkan juga IN_FLIGHT" →
  **versi pertama test DB tidak menangkapnya**: penolakan IN_FLIGHT lulus karena `repair_method` NULL (klausa lain). Test
  diperketat: trade diberi label `PERP_HEDGE` dulu dan pesan harus memuat `(dari IN_FLIGHT, metode PERP_HEDGE)` → mutan
  tertangkap. SQL "hapus klausa repair_method" → tertangkap (R-02 dan C-08).
- **Batas yang dicatat (bukan klaim selesai)**: aturan spatial `POSITION_OPEN` memeriksa bahwa hedge **ada**
  (`HasOpenPerpPosition`), bukan bahwa rasio hedge seimbang. Partial unwind dengan kaki tidak seimbang (mis. jual spot 0.004,
  buy-back perp 0.002) masih bisa kembali `POSITION_OPEN` dengan delta +0.002. Toleransi rasio bergantung pada lot perp
  venue; usulan: host memakai `IsDeltaNeutral(perpLot)` sebelum `TransitionTo(PositionOpen)`, selain itu `RESIDUAL`. Diajukan
  ke reviewer untuk triase, belum diubah di Core.

## C-02 (S2) — gross-up fee-base setelah optimizer melewati MaxBuyNotional (dan cap lain)

- **Test**: `Regression/C02_GrossUpCapRecheckTests.cs` (6, API lama): kasus reviewer persis (Bitget → Bybit, lot 1e-6,
  cap 600, fee beli 0,1 % base); cap notional saja; cap qty saja; gross-up melewati `MaxLevelsPerSide`; gross-up melewati
  kedalaman haircut; properti 4.000 book acak (setiap Execute memenuhi semua cap pada kaki final **dan** ukurannya maksimal,
  oracle = scan linear turun dari floor optimizer, terpisah dari binary search implementasi; coverage guard ≥ 1.000 execute
  dan ≥ 100 kasus cap mengikat setelah gross-up; fase cap adversarial 5e-15 di bawah notional yang bisa dicapai, ≥ 1.000
  execute, ditambahkan untuk jalur cepat, lihat bawah).
- **MERAH (perilaku, API lama)**:
  ```
  FAIL …ReviewerCase_FinalBuyNotional_StaysWithinTheCap         buy notional 600.66000000 > 600
  FAIL …NotionalCapAlone_AppliesToTheGrossedUpBuyLeg            buy notional 600.66000000 > 600
  FAIL …QtyCapAlone_AppliesToTheGrossedUpBuyLeg                 Assert.True() Failure   (beli 0.010011 > 0.01)
  FAIL …GrossUp_DoesNotWalkPastTheLevelCap                      Expected: 1  Actual: 2
  FAIL …GrossUp_BeyondHaircutDepth_ShrinksInsteadOfRejecting    Expected: Execute  Actual: RejectThreshold
  FAIL …RandomBooks_EveryExecuteRespectsTheCaps_AndIsMaximal
       iter 3 maxQty Infinity maxNotional 1000 haircut 661854 levels 4: Execute breaks a cap (buy 16683 lots, notional 1001.01336600, levels 2/2)
  TOTAL: 0 passed, 6 failed
  ```
  Angka reviewer (600,66) tereproduksi persis. Test cap-notional-saja ditambahkan setelah mutation check (lihat bawah) dan
  merah juga pada kode pra-patch.
- **Patch** (`SpatialArbPricer`): kaki final di-size oleh `TrySize` (gross-up, walk hanya `MaxLevelsPerSide` level pertama
  dengan haircut yang sama, lalu cap qty kedua kaki dan notional beli dibandingkan **exact** dalam decimal; cap double
  dikonversi ke bentuk desimal terpendeknya, 600 → 600). Bila ukuran pertama melanggar, binary search ukuran common terbesar
  yang muat (semua cap monoton terhadap ukuran); 0 → `RejectThreshold`. Invariant terdokumentasi di class:
  `IsExecutable ⇒ notional beli ≤ MaxBuyNotional, kedua kaki ≤ MaxQtyCanon, level ≤ MaxLevelsPerSide, walk lengkap`.
  Kasus reviewer kini: deliver 0.00999, beli 0.01 (10.000 lot) seharga tepat 600.
- **HIJAU**: `C02_GrossUpCapRecheckTests 6 passed`; full suite `275 passed, 0 failed` (zero-alloc pricer tetap 0 B).
  (Pesan commit `397d9c7` menulis "7 green": salah hitung, jumlahnya 6.)
- **Mutation** (5, semua tertangkap): hapus cek notional (2 test: cap-notional-saja dan properti; **versi pertama hanya
  properti yang menangkap**, karena di kasus reviewer cap qty juga mengikat → test cap-notional-saja ditambahkan); walk tanpa
  batas level (2); hasil search tidak maksimal (2); cap qty hanya di kaki jual (2); reject alih-alih mengecil (6).
- **Benchmark & jalur cepat** (commit dokumentasi ronde 2): A/B pertama (2 run) tampak netral (465,7 → 449,0), tetapi A/B
  bergantian 4× terhadap `4bf87f7` dengan build terpisah menunjukkan regresi nyata: min 459,6 → 505,2 ns/op, dari konversi
  `(decimal)double` cap di setiap evaluasi. Perbaikan: estimasi double (error relatif ~1e-15) memutuskan kecuali berada dalam
  1e-12 dari cap; hanya di band itu konversi decimal + perbandingan exact dijalankan. Hasil 4× bergantian: min 457,3 → 485,0,
  median 484,4 → 498,4 ns/op (+3–6 %, ~15–28 ns = biaya cek ulang), 0 B/op. Mutan jalur cepat: "band → selalu terlanggar"
  tertangkap (3 test); "band → selalu muat" **lolos** sampai fase cap adversarial ditambahkan ke properti, kini tertangkap;
  "band atas dilebarkan" = mutan ekuivalen (hanya lebih banyak kasus ke jalur exact, hasil sama).

## C-01 (S2) — epsilon relatif `FixedPoint.ToUnits` menyeberangi integer

- **Test**: `Regression/C01_GridSnapBoundTests.cs` (16, API lama): kasus reviewer (1e12, Down dan Up); integer exact sebagai
  titik tetap dari 1 sampai 1e18 (unit 1 dan 0,25); pecahan asli di bawah/atas integer tidak ditarik (10 … 1e13); noise
  tetap diserap (0,01/1e-5, 0,1+0,2, 2,9999999999, limit repair 5998995, dan tiga rantai `n·unit·k/k` dengan noise 1–2 ulp
  di atas band absolut 1e-9, ditemukan dengan pencarian); properti 200.000 nilai acak sampai 1e15 unit (`Down ≤ x + s`,
  `Up ≥ x − s`, s = 1e-6 + 2 ulp; coverage ≥ 10.000 nilai ≥ 1e12); `PairLotGrid.UnitsFloor` dengan common lot 1e-8 BTC.
- **MERAH (perilaku, API lama)**:
  ```
  FAIL …ReviewerCase_ExactIntegerAt1e12_IsUnchanged                 Expected: 1000000000000  Actual: 1000000000001
  FAIL …ExactIntegers_AreFixedPoints_AtEveryMagnitude(1e12)          Actual: 1000000000001
  FAIL …ExactIntegers_AreFixedPoints_AtEveryMagnitude(3e13)          Actual: 30000000000030
  FAIL …ExactIntegers_AreFixedPoints_AtEveryMagnitude(1e15)          Actual: 1000000000001000
  FAIL …ExactIntegers_AreFixedPoints_AtEveryMagnitude(2^52)          Actual: 4503599627375000
  FAIL …ExactIntegers_AreFixedPoints_AtEveryMagnitude(2^53)          Actual: 9007199254750000
  FAIL …ExactIntegers_AreFixedPoints_AtEveryMagnitude(1e18)          Actual: 1000000000001000064
  FAIL …GenuineFractionsBelowAnInteger_AreNotPulledUp                Expected: 999999999999  Actual: 1000000000000
  FAIL …PairLotGrid_UnitsFloor_NeverExceedsTheQuantity               (999.999.999.999,5 lot → 1e12)
  FAIL …RandomValues_NeverCrossAnIntegerBeyondTheSnapBand
       Down 1234221350704 > x + snap (value 1234221350703.1597 unit 1 x 1234221350703.1597)
  TOTAL: 6 passed, 10 failed
  ```
  Yang lulus pra-patch: integer kecil (1, 7, 6e6, 999.999.999, 5e11) dan penyerapan noise, sebagai guard agar fix tidak
  merusak keduanya. Versi pertama test `PairLotGrid` lulus pra-patch karena nilainya tepat di lot; diganti setengah lot
  di bawah 1e12 → merah.
- **Patch** (`FixedPoint.ToUnits`): `tol = min(1e-9 + 16·ulp(x), 1e-6)` menggantikan `1e-9 + |x|·1e-12`
  (`GridRelativeEpsilon` dihapus; `GridUlpTolerance`, `MaxGridSnap` baru). Order quantity spatial dan cap keras sudah dicek
  ulang secara exact oleh C-02.
- **HIJAU**: `C01_GridSnapBoundTests 16 passed`; full suite `291 passed, 0 failed` (termasuk `FixedPointTests` 31 dan limit
  repair 5998995 yang bergantung pada penyerapan noise).
- **Mutation** (4, semua tertangkap): tanpa batas 1e-6 (5 test); batas 0,5 (properti); toleransi relatif 1e-15 (5);
  tanpa suku ulp (1, `NoiseIsStillAbsorbed`; **sebelum** tiga rantai noise 1–2 ulp ditambahkan, mutan ini lolos karena
  semua kasus noise lama sudah diserap band absolut 1e-9).

## Ringkasan ronde 2 (review ChatGPT)

| | Jumlah |
|---|---|
| Temuan baru ChatGPT | 4 CONFIRMED (C-07 S1; C-01, C-02, C-08 S2), 0 REJECTED; C-03…C-06 DUPLICATE |
| Merah dicatat sebelum patch | 4/4, semuanya **perilaku pada API lama** (C-07 juga compile-merah untuk API baru) |
| Test C# | 250 → **291** (harness offline 291/291) |
| DB | `ALL TESTS PASSED`, assertion 118 → **124** (blok ronde 2 di-rollback) |
| Mutation | 6 + 3 + 5(+2 jalur cepat) + 4 = 20 mutan non-ekuivalen, semua tertangkap (+1 ekuivalen); 4 test diperketat karena versi pertamanya meloloskan mutan (C-08 SQL, C-02 notional, C-02 band jalur cepat, C-01 suku ulp) dan 1 test diganti karena tidak merah pra-patch (C-01 `PairLotGrid`) |
| Benchmark | 0 B/op semua; pricer +3–6 % (15–28 ns, cek ulang cap) setelah jalur cepat; lainnya dalam noise |
| Belum | re-review ChatGPT untuk fix C-07 (S1), persetujuan Jansen → **Tahap 1 tetap REVIEW TERBUKA** |

