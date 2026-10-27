# ArbEngine — Architecture Blueprint (v0.1)

> Multi-Strategy Crypto Arbitrage Engine · .NET 8 Worker Service · PostgreSQL 16
> Status: skema DB tervalidasi (v0.1) · **Tahap 1 `Arb.Core`: dibangun (v0.2), status REVIEW TERBUKA** — lihat [`STAGE1_CORE.md`](STAGE1_CORE.md) & [`review/FINDINGS_LOG.md`](review/FINDINGS_LOG.md) · Tahap 2: adapter venue.

---

## 0. Prinsip desain (urut prioritas)

1. **Capital safety > profit > latency.** Setiap jalur yang bisa meninggalkan eksposur terbuka punya pemilik, deadline, dan eskalasi.
2. **Dua strategi, dua target optimasi yang berlawanan.**
   - *Spatial arb* = perlombaan **latency** (edge hilang dalam milidetik).
   - *Funding arb* = permainan **biaya** (edge datang per jam; entry/exit cost menentukan segalanya).
   Arsitektur tidak memaksa funding arb lewat jalur IOC-taker yang sama dengan spatial.
3. **Satu writer per state.** Order book, inventory ledger, dan trade state masing-masing punya tepat satu thread pemilik. Tidak ada lock di hot path.
4. **DB = audit + recovery, bukan dependency hot path.** Engine tetap aman jika PostgreSQL mati (journal lokal + halt entries).
5. **Semua yang bisa salah, terukur.** Ilusi likuiditas, slippage, latency, capture ratio — semua punya kolom/view.

---

## 1. Struktur Solution

> **As-built Tahap 1:** `Arb.Core` mengikuti pohon di bawah dengan tiga penyesuaian.
> (1) Logika murni `SanityGuard`, `StaleDataGuard`, `LiquidationCalculator` + `MarginGuard` dipindah ke `Arb.Core/Risk`.
> `Arb.Engine` nanti hanya menjadwalkan & memberi data (mis. `MarginMonitor` = timer + `MarginGuard`).
> (2) Status deposit/withdraw ada di `Risk/WalletStatus.cs`, bukan di `InstrumentSpec` (alasan: STAGE1_CORE §3.5).
> (3) `Abstractions/` (interface venue) ditunda ke Tahap 2 agar didesain bersama adapter pertama.
> Peta lengkap file ada di STAGE1_CORE §1.

```
ArbEngine/
├── ArbEngine.sln
├── Directory.Build.props        # net8.0, Nullable, TreatWarningsAsErrors, ServerGC, ConcurrentGC,
│                                # TieredPGO, InvariantGlobalization, AllowUnsafeBlocks (Core only)
├── Directory.Packages.props     # Central Package Management, versi dikunci
│
├── src/
│   ├── Arb.Core/                        # ZERO dependency eksternal. Tanpa I/O. 100% unit-testable.
│   │   ├── Primitives/
│   │   │   ├── Venue.cs                 # enum : byte  Binance | Bybit | Bitget
│   │   │   ├── MarketType.cs            # Spot | PerpLinear
│   │   │   ├── Side.cs · TimeInForce.cs · OrderType.cs
│   │   │   ├── InstrumentKey.cs         # readonly struct (Venue, MarketType, ushort Index) → index array, bukan Dictionary
│   │   │   ├── FixedPoint.cs            # decimal ↔ long ticks/lots; akumulasi Int128 (anti-overflow)
│   │   │   └── IClock.cs / MonoClock.cs # Stopwatch µs monotonic + wall-clock UTC terkoreksi offset venue
│   │   ├── Instruments/
│   │   │   ├── InstrumentSpec.cs        # tick, step, minQty, minNotional, baseMultiplier (1000PEPE!)
│   │   │   ├── InstrumentRegistry.cs    # snapshot immutable; di-swap atomik saat exchangeInfo berubah
│   │   │   ├── CanonicalPair.cs         # BTC/USDT → daftar InstrumentKey lintas venue
│   │   │   └── LotNormalizer.cs         # floor qty ke step KEDUA venue; validasi minQty/minNotional
│   │   ├── MarketData/
│   │   │   ├── PriceLevel.cs            # struct { long PxTicks; long QtyLots; }
│   │   │   ├── OrderBookL2.cs           # sorted arrays, single-writer, seqlock; ApplySnapshot/ApplyDelta
│   │   │   ├── BookView.cs              # copy top-N ke buffer preallocated (reader tanpa lock)
│   │   │   ├── BookStamp.cs             # seq, exchangeTsUs, localRecvMonoUs
│   │   │   └── BookHealth.cs            # Synced | Resyncing | Invalid(reason)
│   │   ├── Pricing/
│   │   │   ├── VwapWalker.cs            # walk ReadOnlySpan<PriceLevel> utk Q → vwap, worstPx, levels; 0 alloc
│   │   │   ├── CrossedBookOptimizer.cs  # Q* = argmax NetProfit(Q) via merge-walk 2 sisi (bukan Q tetap)
│   │   │   ├── FeeModel.cs              # bps efektif per instrumen (VIP tier + diskon BNB/BGB) dari API
│   │   │   ├── NetEdgeCalculator.cs     # rumus Net Edge unit-konsisten (lihat §4)
│   │   │   ├── SlippageModel.cs         # buffer = f(σ harga dlm latency window, fraksi depth terpakai)
│   │   │   └── FundingYieldCalculator.cs# income/jam (interval dinamis), break-even hours, net APR (lihat §4)
│   │   ├── Execution/
│   │   │   ├── TradeState.cs            # enum ≡ trd.trade_state
│   │   │   ├── TradeStateMachine.cs     # tabel transisi murni ≡ trd.trade_state_transition (diuji identik)
│   │   │   ├── TradeGroup.cs            # agregat: legs, net delta canonical, reservation tokens
│   │   │   ├── OrderRequest.cs          # struct: clientOrderId, limitPx, qty, TIF
│   │   │   ├── ExecutionReport.cs       # struct: status, cumQty, lastQty, lastPx, fee, ts
│   │   │   ├── ExecutionPolicy.cs       # Parallel | SequentialRiskyLegFirst | MakerTaker (per pasangan)
│   │   │   └── RepairPlanner.cs         # pilih COMPLETE / UNWIND / PERP_HEDGE: expected cost terendah, ≤ MaxStopLossBps
│   │   ├── Risk/
│   │   │   ├── RiskLimits.cs            # MaxNotional, MaxStopLossBps, MaxDailyDdPct, MaxConsecLosses, MaxBookAgeMs…
│   │   │   ├── HaltScope.cs · HaltLevel.cs   # GLOBAL/VENUE/PAIR · NO_NEW_ENTRIES/FLATTEN_AND_STOP
│   │   │   └── IRiskGate.cs             # Evaluate(in Candidate) → Decision (alloc-free)
│   │   ├── Inventory/
│   │   │   └── InventoryLedger.cs       # free/reserved per (venue, asset); Reserve → Commit/Release; single owner
│   │   └── Abstractions/
│   │       ├── IMarketDataFeed.cs       # subscribe, push ke book writer
│   │       ├── ITradingGateway.cs       # ValueTask<OrderAck> Place(in OrderRequest) · Cancel · QueryByClientId
│   │       ├── IUserStream.cs           # order update, fill, balance, position, funding
│   │       ├── IVenueMetadata.cs        # exchangeInfo, fee rate, wallet status (deposit/withdraw), funding interval
│   │       ├── IPersistenceSink.cs      # bool TryWrite(in PersistEvent) — non-blocking
│   │       ├── IAlertSink.cs
│   │       └── IIdGenerator.cs          # Snowflake 64-bit (ms | node | seq)
│   │
│   ├── Arb.Venues.Common/
│   │   ├── Ws/WsConnection.cs           # ClientWebSocket + ArrayPool, ping/pong, reconnect backoff+jitter, watchdog
│   │   ├── Ws/WsMessagePump.cs          # 1 thread LongRunning per koneksi; parse langsung dari UTF-8 buffer
│   │   ├── Json/Utf8Fast.cs             # angka UTF-8 → long ticks/lots tanpa string/decimal.Parse
│   │   ├── Rest/SignedHttpClient.cs     # SocketsHttpHandler pooled, keep-alive; signer HMAC & Ed25519
│   │   ├── Rest/WeightRateLimiter.cs    # token bucket per bobot + baca header usage → 429 tidak pernah terjadi
│   │   ├── Time/ClockOffsetEstimator.cs # offset = serverTime − (tSend + RTT/2), filter min-RTT, EWMA
│   │   └── BookSync/SnapshotDeltaSynchronizer.cs  # buffer delta → snapshot → replay → validasi kontinuitas seq
│   ├── Arb.Venues.Binance/
│   │   ├── BinanceSpotFeed.cs           # SBE: @depth (20ms) + @bestBidAsk (real-time) — butuh API key Ed25519
│   │   ├── BinanceFuturesFeed.cs        # diff depth dgn validasi rantai pu→u; markPrice/funding
│   │   ├── BinanceTradingGateway.cs     # WebSocket API order.place (utama) · REST (fallback)
│   │   ├── BinanceUserStream.cs · BinanceMetadata.cs · BinanceSbeDecoder.cs
│   ├── Arb.Venues.Bybit/
│   │   ├── BybitFeed.cs                 # orderbook snapshot/delta, validasi u/seq, reset saat snapshot ulang
│   │   ├── BybitTradingGateway.cs       # WS Trade order.create (utama) · REST (fallback)
│   │   ├── BybitUserStream.cs · BybitMetadata.cs   # fundingInterval DINAMIS (1/2/4/8 jam)
│   ├── Arb.Venues.Bitget/
│   │   ├── BitgetFeed.cs                # books + CRC32 checksum → mismatch = resync
│   │   ├── BitgetTradingGateway.cs      # WS trade place-order (respons = ack saja → konfirmasi via order channel)
│   │   ├── BitgetUserStream.cs · BitgetMetadata.cs
│   │
│   ├── Arb.Engine/
│   │   ├── Pipeline/
│   │   │   ├── BookDirtySignal.cs       # conflating: flag per instrumen, enqueue hanya saat 0→1 (tanpa backlog)
│   │   │   ├── OpportunityScanner.cs    # single consumer; evaluasi semua pasangan venue utk instrumen dirty
│   │   │   └── Candidate.cs             # struct hasil evaluasi
│   │   ├── Strategies/
│   │   │   ├── IStrategy.cs
│   │   │   ├── SpatialArbStrategy.cs    # tick-driven
│   │   │   └── FundingArbStrategy.cs    # timer-driven (detik), maker-first entry; tidak di hot path
│   │   ├── Execution/
│   │   │   ├── ExecutionCoordinator.cs  # reserve → fire legs (policy) → deadline → transisi state
│   │   │   ├── OrderTracker.cs          # clientOrderId → leg; merge laporan out-of-order (logika ≡ trigger DB)
│   │   │   ├── RepairExecutor.cs        # emergency unwind: marketable-limit IOC ladder, deadline 500ms → STUCK
│   │   │   └── LegDeadlineWatcher.cs    # timeout → UNKNOWN → QueryByClientId
│   │   ├── Risk/
│   │   │   ├── StaleDataGuard.cs        # umur book, health seq, clock skew, skew antar-book
│   │   │   ├── SanityGuard.cs           # tolak spread > X% (mapping salah / wallet suspend / delisting)
│   │   │   ├── CircuitBreaker.cs        # latched + persisted; NO_NEW_ENTRIES ≠ FLATTEN; repair tak pernah diblok
│   │   │   ├── PreTradeRiskGate.cs      # komposit: halt → stale → sanity → inventory → limits
│   │   │   └── MarginMonitor.cs         # funding arb: jarak likuidasi per venue, auto-reduce
│   │   ├── Inventory/
│   │   │   ├── BalanceCache.cs          # diisi user stream → InventoryLedger
│   │   │   └── Reconciler.cs            # REST periodik (cold path) → acct.reconciliation; drift → HALT_VENUE
│   │   ├── Paper/
│   │   │   ├── PaperTradingGateway.cs   # implement ITradingGateway; fill vs book PADA t+latency, IOC, partial
│   │   │   ├── BookHistoryRing.cs       # ring buffer top-N per instrumen (~2 dtk)
│   │   │   └── LatencyModel.cs          # base + jitter lognormal + haircut kompetisi likuiditas
│   │   └── Recovery/
│   │       └── StartupRecovery.cs       # halt latch → rehydrate breaker → rekonsiliasi order hidup → lanjut repair
│   ├── Arb.Persistence/
│   │   ├── PersistEvent.cs              # union struct: Opportunity | Transition | OrderUpdate | Fill | Funding | Balance | Risk
│   │   ├── AsyncPersistenceWriter.cs    # 2 channel: critical (tak pernah drop) & best-effort (DropOldest + counter)
│   │   ├── LocalJournal.cs              # fallback append-only file saat DB down → replay saat pulih
│   │   ├── CopyWriter.cs                # Npgsql binary COPY → staging → INSERT … ON CONFLICT DO NOTHING
│   │   ├── SqlStateHandler.cs           # AR001 → drop idempoten · AR002–AR006 → alert (bug)
│   │   └── Migrations/                  # embedded V00x.sql + runner (tabel schema_version)
│   ├── Arb.Alerts/
│   │   ├── TelegramAlertSink.cs · DiscordAlertSink.cs
│   │   └── AlertThrottler.cs            # dedup + rate limit; jalur LANGSUNG (tidak bergantung DB)
│   └── Arb.Host/
│       ├── Program.cs                   # Host.CreateApplicationBuilder, GCSettings.LatencyMode, DI
│       ├── HostedServices/
│       │   ├── VenueConnectivityService.cs · EngineService.cs · PersistenceService.cs
│       │   ├── ReconciliationService.cs · FundingScheduler.cs · HealthService.cs
│       ├── Options/                     # *Options + IValidateOptions (fail-fast saat startup)
│       └── appsettings.json · appsettings.Paper.json · appsettings.Live.json   # secrets via env, BUKAN di sini
│
├── db/
│   ├── migrations/V001…V007             # skema (tervalidasi; V007 = fix review ronde 1)
│   └── tests/                           # skenario + integrity test (95 assertion)
│
└── tests/
    ├── Arb.Core.Tests/                  # VWAP, lot, net edge, funding, state machine ≡ SQL, repair planner
    ├── Arb.Venues.Tests/                # replay rekaman WS asli → book == snapshot REST / checksum
    ├── Arb.Engine.Tests/                # asymmetric fill, UNKNOWN timeout, out-of-order report, latch survive restart
    └── Arb.Benchmarks/                  # BenchmarkDotNet: ApplyDelta, VwapWalk, Scan — target 0 B allocated
```

**Aturan dependency (dipaksa lewat project reference):**
`Core ← Venues.Common ← Venues.{Binance,Bybit,Bitget}` · `Core ← Engine` · `Core ← Persistence` · `Host` merakit semuanya.
Engine **tidak** mereferensikan adapter venue mana pun → strategi tidak bisa bergantung pada detail bursa.

---

## 2. Threading model & hot path

```
 [WS thread / venue-connection]           [Scanner thread]              [Executor thread]
  recv UTF-8/SBE ─► parse → ticks/lots      dirty signal ─► BookView      Candidate ─► RiskGate
  ─► OrderBookL2.Apply (single writer)      copy (seqlock, no lock)       ─► InventoryLedger.Reserve
  ─► BookDirtySignal.Mark (0→1 only)        ─► VWAP walk / Q* optimizer   ─► ITradingGateway.Place ×2
                                            ─► Candidate → channel        ─► OrderTracker (reports)
                                                                          ─► RepairExecutor bila RESIDUAL
 [UserStream thread] ─► OrderTracker / BalanceCache          (semua) ─► IPersistenceSink.TryWrite ─► [Writer] ─► PostgreSQL
```

- **Conflation, bukan antrian.** Scanner tidak butuh setiap delta, ia butuh state terbaru. `BookDirtySignal` menghilangkan backlog saat burst (backlog = keputusan berdasarkan data basi).
- **Seqlock:** writer `version++` (ganjil) → tulis → `version++` (genap). Reader copy top-N, cek versi tidak berubah & genap, retry bila perlu. Tanpa lock, tanpa alokasi.
- **InventoryLedger dimiliki executor thread saja** → mustahil dua peluang bersamaan memakai saldo yang sama (double-spend).
- **Budget compute:** wire→decision < 50 µs, decision→socket < 100 µs. Ini kecil dibanding RTT jaringan (ms). Optimasi GC/alloc penting untuk **tail latency (p99)** — satu jeda GC gen2 di momen yang salah = legging — bukan untuk median.
- **Lokasi server menentukan segalanya** untuk spatial. Ukur RTT dari kandidat VPS ke endpoint order tiap venue; pilih lokasi yang meminimalkan **max(RTT_A, RTT_B)** untuk pasangan yang ditradingkan (kaki yang lebih lambat menentukan jendela legging).

---

## 3. Execution & risk state machine

```
CREATED ─► IN_FLIGHT ─┬─► COMPLETED (spatial)            terminal
   │                  ├─► MISSED                         terminal
   └─► MISSED         ├─► POSITION_OPEN ⇄ EXITING ─► CLOSED (funding; EXITING → POSITION_OPEN = partial reduce)
                      ├─► RESIDUAL ─► REPAIRING ─┬─► REPAIRED / POSITION_OPEN / CLOSED
                      └─► STUCK ◄────────────────┴─► RESIDUAL (repair parsial)
                          STUCK ─► REPAIRING | MANUAL_RESOLVED
   POSITION_OPEN ─► RESIDUAL (satu kaki dilikuidasi / ADL) | STUCK (venue tak terjangkau)        [v0.3, R-10]
   SPATIAL: REPAIRING ─► POSITION_OPEN hanya untuk hedge perp; hedge tidak pernah REPAIRED        [v0.3, R-02]
```

- Tabel transisi di `trd.trade_state_transition` (V002 + V007, 23 transisi) ≡ `TradeStateMachine.cs`. Unit test membaca semua migrasi dan memastikan identik.
- **State terminal mensyaratkan eksposur nol (v0.3):** tidak ada posisi perp terbuka, dan net delta = dust yang diterima eksplisit (`dust_accepted_base`). Aturan yang sama dipakai `rpt.v_trade_ledger.is_flat` (R-14).
- **Repair decision (RepairPlanner):** hitung expected cost real-time dari book saat ini untuk:
  (a) *complete* kaki yang kurang di venue B, (b) *unwind* di venue A, (c) *hedge* di perp paling likuid lalu unwind santai.
  Hedge (c) punya pemilik: trade tetap `POSITION_OPEN` sampai spot + perp di-unwind (`EXITING → CLOSED`), terlihat di `rpt.v_open_exposure.open_perp_qty_base` (v0.3, R-02).
  Pilih termurah dengan batas `MaxStopLossBps`. Order repair = **marketable-limit IOC** (limit = VWAP + toleransi), ladder 2–3 langkah, bukan market order murni (market order di book tipis = slippage tak terbatas).
- **Deadline:** RESIDUAL → flat ≤ 500 ms (SLO, diukur di `rpt.v_legging_incidents`). Lewat batas / percobaan habis → `STUCK` → halt GLOBAL `NO_NEW_ENTRIES` + alert.
- **Halt levels:** `NO_NEW_ENTRIES` menghentikan entry tetapi **tidak pernah** memblok repair/unwind. `FLATTEN_AND_STOP` aktif menutup semua eksposur. Kill-switch yang mematikan segalanya saat satu kaki terbuka = eksposur telanjang.
- **429 pada venue X** → HALT_VENUE X (entry). Jika repair butuh venue X, RepairPlanner memilih jalur alternatif (perp venue lain).
- **Latch persisten:** halt latched disimpan di `risk.halt_state` dan dimuat saat startup. Loss streak & daily PnL di-rehydrate dari DB. Restart/crash-loop tidak boleh mereset kill-switch.

**Stale-data guard (lebih ketat dari "exchangeTs vs localTs > 150 ms"):**
1. *Integrity dulu:* seq gap / checksum gagal → book `Invalid` → freeze pasangan → resync. Book korup dengan timestamp segar lebih berbahaya dari book basi.
2. *Clock:* host wajib chrony/NTP; `ClockOffsetEstimator` per venue. Latency = `localRecv − (exchangeTs + offset)`. Tanpa koreksi offset, ambang 150 ms membandingkan dua jam yang berbeda.
3. *Age:* umur sejak pesan terakhir per book, plus liveness koneksi (ping RTT). Instrumen sepi ≠ basi → ambang per instrumen dari cadence normalnya. v0.3: batas umur per strategi (`BookTooOld`; spatial 1 s, funding 5 s).
4. *Cross-book skew:* kedua book yang dibandingkan harus "segar bersamaan" (v0.3: `PairSkew`, spatial 500 ms). Latency negatif di luar ketidakpastian jam = `ClockUncertain` (R-07).
5. *Cadence venue:* stream depth JSON Binance spot di-batch (100 ms); gunakan SBE `@depth` (20 ms) + `@bestBidAsk` (real-time) agar ambang 150 ms bermakna.

---

## 4. Koreksi rumus

### 4.1 Spatial net edge (unit-konsisten)
Spec memakai `Q` sekaligus sebagai notional (500 USDT) dan kuantitas base. Yang benar:

```
Q        = LotNormalize( min(Q*, InventoryCap, NotionalCap / px) )      # canonical base units
VWAPask_A(Q), VWAPbid_B(Q)                                              # walk L2 sampai Q terisi
Gross    = Q · (VWAPbid_B − VWAPask_A)
Fees     = Q·VWAPask_A·fA + Q·VWAPbid_B·fB,   f = taker_bps·(1 − disc)/1e4
Slip     = Q·VWAP · k · σpx(Δt_latency)   (+ penalti bila fraksi depth terpakai > X%)
Rebal    = (withdraw_fee + network_cost) / E[trade per siklus rebalance]  (per unit)
Net      = Gross − Fees − Slip − Rebal  >  max(MinProfitQuote, MinProfitBps · Notional)
```
- `Q*` = argmax Net(Q) (kurva edge cekung-turun terhadap Q). Q tetap meninggalkan uang atau memakan level jelek.
- Limit IOC = **worst level price** (bukan VWAP), supaya order bisa menyapu level yang dihitung.
- Fee dalam aset base (Binance spot tanpa BNB, Bitget) mengurangi koin yang diterima → beli `Q/(1−f)` atau aktifkan bayar-fee-pakai-koin-bursa. Kalau tidak, inventory drift tiap trade.

### 4.2 Funding arbitrage
Rumus spec `ΔFR·3·365 / (EntrySpread + ExitSpread + Fees)` adalah **rasio tanpa dimensi** ("berapa kali funding setahun menutup biaya sekali jalan"), bukan yield, dan mengasumsikan interval 8 jam. Padahal interval funding **dinamis** (Bybit: 8/4/2/1 jam otomatis saat rate menyentuh cap; Binance: sebagian simbol 4 jam).

```
g  = r_short/I_short − r_long/I_long           # income per jam, fraksi notional (spot-perp: r_long = 0)
C  = entry_spread + E[exit_spread] + 4·taker_fee + slippage     # biaya sekali jalan, fraksi notional
BreakEvenHours = C / g
NetAPR(T)      = (g·T − C) / T · 8760           # T = expected holding (jam)
CapitalYield   = NetAPR / (1 + 1/leverage)      # spot-perp: modal = notional spot + margin perp
```
- Masuk jika `BreakEvenHours < 16` **dan** `g` dihitung dari **ekspektasi** rate (EWMA realized + predicted), bukan spike saat ini (funding mean-revert).
- Implikasi angka: dengan C ≈ 20 bps (4 taker leg), break-even 16 jam butuh selisih funding ≈ 10 bps/8 jam (≈ 110% APR). Itu jarang & ramai. **Entry maker/post-only** (funding arb tidak sensitif latency) memotong C drastis dan membuat aturan 16 jam realistis.
- Test DB (trade 1006) mendemonstrasikan ini: funding APR 6,6% tetapi net APR −89% untuk hold 16 jam karena 4× taker fee.

---

## 5. Risiko yang tidak ada di spec (wajib)

| # | Risiko | Mitigasi |
|---|--------|----------|
| 6 | **Margin lintas venue tidak di-net**: short perp di A bisa dilikuidasi saat spike walau long spot di B aman | Leverage rendah (≤2–3x), `MarginMonitor` jarak likuidasi, auto top-up/reduce, alert |
| 7 | **Mapping instrumen**: 1000PEPE vs PEPE, contract size, ticker sama aset beda, redenominasi | `base_multiplier` canonical + `SanityGuard` (spread > X% = tolak) |
| 8 | **Spread besar = sinyal bahaya**: deposit/withdraw suspend, delisting, chain halt | Cek wallet status via API; blacklist otomatis |
| 9 | **Inventory = eksposur arah**: koin pre-positioned di B ikut naik-turun harga | Hedge inventory dgn perp short, atau batasi; PnL inventory dipisah dari PnL arb |
| 10 | **Keamanan API key** | Tanpa izin withdraw, IP whitelist, secrets via env/secret store, Ed25519 utk Binance |
| 11 | **Counterparty / regulasi** | Sebar modal; cek status legal bursa di yurisdiksi operator |
| 12 | **DB down** | Writer → `LocalJournal` + HALT `NO_NEW_ENTRIES`; replay saat pulih |

---

## 6. Paper trading yang tidak menipu diri sendiri

Simulasi "+50 ms delay lalu isi di book yang sama" bias optimis: peluang yang terlihat sering sudah diambil pemain lebih cepat (adverse selection).

- `PaperTradingGateway` mengisi order terhadap **book pada t_decision + latency** (dari `BookHistoryRing`), dengan semantik IOC asli (sapu level s/d limit, partial fill).
- Haircut kompetisi: hanya fraksi α dari likuiditas yang terlihat dianggap bisa diambil (dikalibrasi dari trade live ukuran kecil).
- Semua hasil masuk skema yang sama (`engine_run.mode = 'PAPER'`, `fill.is_simulated`) → dibandingkan langsung via `rpt.v_daily_pnl`.
- **Go/no-go gate untuk uang asli:** `capture_ratio_all` (realized ÷ expected, termasuk MISSED) stabil positif dalam N hari paper + shadow-live ukuran minimal.

---

## 7. Persistence contract (engine ↔ DB)

**Konvensi unit:** `qty_base = qty_venue × base_multiplier`, `notional = px_venue × qty_venue`, canonical px = notional / qty_base. Kolom `*_vwap_px` di `trade_group` & `rpt.*` = canonical; `order_leg.limit_px` & `fill.px` = venue px.

**ID:** opportunity/trade/order di-generate engine (Snowflake) → clientOrderId ada sebelum order keluar; writer fire-and-forget.

**Channel:**
| Event | Channel | Cara tulis |
|-------|---------|-----------|
| Fill, Funding | critical | COPY → temp staging → `INSERT … ON CONFLICT (instrument_id, venue_trade_id) DO NOTHING` |
| OrderUpdate | critical | `INSERT … ON CONFLICT (order_id) DO UPDATE` — trigger merge monoton |
| TradeTransition | critical | `UPDATE … SET state, state_version = v` berurutan per trade |
| RiskEvent, Halt | critical | insert / `risk.fn_latch_halt` |
| Opportunity, Balance | best-effort | COPY batch; drop saat tekanan (counter metric) |

Critical channel penuh / DB down → `LocalJournal` + halt entries (jangan pernah blok hot path).

**SQLSTATE dari trigger:**
| Code | Arti | Tindakan writer |
|------|------|-----------------|
| AR001 | stale/duplicate state write | drop (idempoten) + metric |
| AR002 | transisi ilegal | alert — bug engine |
| AR003 | version gap | alert + replay dari journal |
| AR004 | waktu transisi mundur | alert |
| AR005 | kolom identitas order diubah | alert |
| AR006 | fill tak cocok dgn order / update fill | alert |

Satu error membatalkan satu transaksi → transisi state ditulis per-statement (volume kecil); fill/opportunity via COPY batch.

**Startup recovery (urutan):**
1. `risk.v_active_halts` → pasang halt di memori.
2. `risk.fn_evaluate_circuit_breaker(...)` → rehydrate streak & drawdown.
3. `rpt.v_open_exposure` + order non-terminal → `QueryByClientId` di venue → rekonsiliasi fill → lanjutkan repair.
4. Rekonsiliasi saldo REST → seed `BalanceCache`.
5. Baru buka entry (jika tidak ada latch).

**Skala:** view `rpt.*` menghitung dari fill (sumber kebenaran). Untuk > ~1 juta fill, tambahkan tabel hasil yang ditulis saat trade terminal; fungsi breaker hanya dipakai saat startup/EOD (breaker live ada di memori).

---

## 8. Skema DB (ringkas)

| Schema | Objek | Fungsi |
|--------|-------|--------|
| `ref` | exchange, asset, instrument, fee_schedule, setting, `fn_effective_fee_bps` | master data; multiplier canonical; fee SCD + override VIP |
| `md` | funding_rate | rate per settlement + interval aktual (dinamis), predicted vs final |
| `trd` | engine_run, opportunity (partisi harian), trade_state(+transition), trade_group, trade_state_log, order_leg, fill, funding_payment | log keputusan → eksekusi → audit state |
| `acct` | balance_snapshot, reconciliation, transfer, equity_snapshot | saldo, drift, biaya rebalancing aktual |
| `risk` | halt_state, risk_event, `fn_consecutive_losses`, `fn_daily_pnl`, `fn_evaluate_circuit_breaker`, `fn_latch_halt`, `fn_clear_halt`, `v_active_halts` | latch persisten & rehydration |
| `rpt` | v_leg_exec, v_trade_ledger, v_daily_pnl, v_execution_quality, v_opportunity_funnel, v_legging_incidents, v_funding_positions, v_inventory, v_open_exposure, v_order_integrity, v_rebalance_cost_daily (v0.3) | analitik PnL & kualitas eksekusi |

Model PnL (satu rumus untuk spatial, perp, spot-perp):
```
cash_flow = Σ notional SELL − Σ notional BUY − fee(quote & koin pihak ketiga) + Σ funding
net_delta = Σ qty_base BUY − Σ qty_base SELL − fee dalam aset base
est_pnl   = cash_flow + net_delta × ref_px          realized = est_pnl bila terminal & |residual| ≤ dust
```
