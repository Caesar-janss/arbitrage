#!/usr/bin/env python3
"""
ArbEngine — multi-venue spread scanner. Which venue pair and which coins show price gaps that survive taker fees AND
last long enough to act on? Stdlib only, public REST tickers only, no API key, never trades.

  python3 tools/spread_scanner.py --probe                       # hit every venue once, show what parses (run first)
  python3 tools/spread_scanner.py --out ~/scan                  # record a snapshot of all venues every 5 s
  python3 tools/spread_scanner.py --report ~/scan --last-hours 24   # rank venue pairs and coins

Complements tools/md_recorder.py: the recorder sees milliseconds on one pair (Binance <-> Bitget); the scanner sees
seconds on every pair of 8 venues. Gaps that last several snapshots are exactly the ones where distance (latency) does
not decide who wins, so a far venue is not ruled out here.

Every INTERVAL seconds all venues are fetched in parallel (best bid/ask + size, all USDT spot pairs) and ONE line is
written per round: {"t": round_ns, "v": {venue: {"t0","t1","vt","q": {BASE: [bid, bid_size, ask, ask_size]}} | {"err"}}}.
Only bases quoted by at least two venues in that round are kept. Files: <out>/rounds/<YYYYmmdd-HH>_<k>.tsv.gz, same
writer and reader as the recorder (hourly gzip, timed flush + fsync, truncated tail readable).

The report replays the rounds and opens an "episode" per (coin, buy venue, sell venue) while
  sell_bid / buy_ask - 1 - taker_fee_buy - taker_fee_sell >= --min-net-bps   and   top-of-book size >= --min-usd
It counts an episode only if it holds in >= --min-rounds consecutive snapshots (venue snapshots of one round are fetched
in parallel, not at the same instant: a one-round gap can be a timing artefact). It flags episodes that are probably
NOT tradable: a mid price far from the other venues (different token behind the same ticker, or a frozen market), a leg
whose quote never changed (stale), and gaps lasting longer than --suspect-minutes (usually deposits/withdrawals closed,
so inventory cannot be rebalanced). Fees default to base-tier taker fees (ASSUMPTIONS, see FEES_BPS); override with
--fee venue=bps.
"""
from __future__ import annotations

import argparse
import concurrent.futures as cf
import gzip
import http.client
import json
import os
import shutil
import signal
import ssl
import statistics
import sys
import threading
import time
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from typing import Callable, Optional

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import md_recorder as md  # noqa: E402  (HourlyWriter, read_file, verify-style helpers)

QUOTE = "USDT"
EXIT_DISK_FULL = 3

# Base-tier SPOT taker fees in basis points. ASSUMPTIONS from public fee pages as remembered, not re-verified: check
# each venue's current schedule (and your own tier / discounts) before acting on a report. --fee overrides.
FEES_BPS = {"binance": 10.0, "bitget": 10.0, "bybit": 10.0, "okx": 10.0, "mexc": 5.0, "gate": 10.0, "kucoin": 10.0,
            "kraken": 40.0}
REFERENCE_BASES = ("BTC", "ETH", "SOL", "XRP", "DOGE")      # liquid everywhere: how tightly is each pair linked?
BASE_ALIASES = {"XBT": "BTC", "XDG": "DOGE"}                 # Kraken legacy codes


def _f(x) -> Optional[float]:
    try:
        v = float(x)
    except (TypeError, ValueError):
        return None
    return v if v == v and v not in (float("inf"), float("-inf")) else None


def _quote_row(bid, bsz, ask, asz) -> Optional[list]:
    """[bid, bid_size, ask, ask_size] or None when the book is empty, crossed or unparsable (sizes may be None)."""
    bid, ask = _f(bid), _f(ask)
    if not bid or not ask or bid <= 0 or ask <= 0 or bid > ask:
        return None
    bsz, asz = _f(bsz), _f(asz)
    return [bid, bsz if bsz and bsz > 0 else None, ask, asz if asz and asz > 0 else None]


def _strip_quote(sym: str, quote: str) -> Optional[str]:
    return sym[:-len(quote)] if sym.endswith(quote) and len(sym) > len(quote) else None


# ── venue adapters: endpoint + parser → {BASE: [bid, bsz, ask, asz]}, venue_ts_ms or None ────────────────────────────
# Field names: VERIFIED = read on the official doc page during development; UNVERIFIED = from memory / third-party
# references. `--probe` on the VM is the ground truth for all of them (docs/STAGE2_SCANNER.md §3).
def parse_binance_like(j, quote: str, meta=None):          # Binance (VERIFIED), MEXC (UNVERIFIED, same API shape)
    out = {}
    for r in j if isinstance(j, list) else []:
        base = _strip_quote(str(r.get("symbol", "")), quote)
        row = _quote_row(r.get("bidPrice"), r.get("bidQty"), r.get("askPrice"), r.get("askQty")) if base else None
        if row:
            out[base] = row
    return out, None


def parse_bitget(j, quote: str, meta=None):                 # UNVERIFIED: data[] symbol, bidPr, bidSz, askPr, askSz, ts
    out, ts = {}, None
    for r in (j.get("data") if isinstance(j, dict) else None) or []:
        base = _strip_quote(str(r.get("symbol", "")), quote)
        row = _quote_row(r.get("bidPr"), r.get("bidSz"), r.get("askPr"), r.get("askSz")) if base else None
        if row:
            out[base] = row
            ts = ts or _f(r.get("ts"))
    return out, ts


def parse_bybit(j, quote: str, meta=None):                  # VERIFIED for linear/inverse; spot assumed same fields
    res = j.get("result") if isinstance(j, dict) else None
    out = {}
    for r in (res.get("list") if isinstance(res, dict) else None) or []:
        base = _strip_quote(str(r.get("symbol", "")), quote)
        row = _quote_row(r.get("bid1Price"), r.get("bid1Size"), r.get("ask1Price"), r.get("ask1Size")) if base else None
        if row:
            out[base] = row
    return out, _f(j.get("time")) if isinstance(j, dict) else None


def _split_pair(sym: str, sep: str, quote: str) -> Optional[str]:
    base, _, q = sym.partition(sep)
    return base if q == quote and base else None


def parse_okx(j, quote: str, meta=None):                    # UNVERIFIED: data[] instId "BTC-USDT", bidPx, bidSz, askPx, askSz, ts
    out, ts = {}, None
    for r in (j.get("data") if isinstance(j, dict) else None) or []:
        base = _split_pair(str(r.get("instId", "")), "-", quote)
        row = _quote_row(r.get("bidPx"), r.get("bidSz"), r.get("askPx"), r.get("askSz")) if base else None
        if row:
            out[base] = row
            ts = max(ts or 0, _f(r.get("ts")) or 0) or None
    return out, ts


def parse_gate(j, quote: str, meta=None):                   # UNVERIFIED: [] currency_pair "BTC_USDT", highest_bid, lowest_ask, highest_size, lowest_size
    out = {}
    for r in j if isinstance(j, list) else []:
        base = _split_pair(str(r.get("currency_pair", "")), "_", quote)
        row = _quote_row(r.get("highest_bid"), r.get("highest_size"), r.get("lowest_ask"), r.get("lowest_size")) if base else None
        if row:
            out[base] = row
    return out, None


def parse_kucoin(j, quote: str, meta=None):                 # UNVERIFIED: data.ticker[] symbol "BTC-USDT", buy, sell, bestBidSize, bestAskSize; data.time
    data = j.get("data") if isinstance(j, dict) else None
    out = {}
    for r in (data.get("ticker") if isinstance(data, dict) else None) or []:
        base = _split_pair(str(r.get("symbol", "")), "-", quote)
        row = _quote_row(r.get("buy"), r.get("bestBidSize"), r.get("sell"), r.get("bestAskSize")) if base else None
        if row:
            out[base] = row
    return out, _f(data.get("time")) if isinstance(data, dict) else None


def parse_kraken_pairs(j) -> dict:                          # VERIFIED: result{key: {wsname "XBT/USDT", status}}
    m = {}
    for key, p in ((j.get("result") if isinstance(j, dict) else None) or {}).items():
        ws = str(p.get("wsname", "")) if isinstance(p, dict) else ""
        base, _, q = ws.partition("/")
        if base and q and p.get("status", "online") == "online":
            m[key] = (BASE_ALIASES.get(base, base), q)
    return m


def parse_kraken(j, quote: str, meta=None):                 # VERIFIED: result{key: {a: [price, whole, lot], b: [...]}}
    out = {}
    for key, r in ((j.get("result") if isinstance(j, dict) else None) or {}).items():
        bq = (meta or {}).get(key)
        if not bq or bq[1] != quote or not isinstance(r, dict):
            continue
        a, b = r.get("a") or [None] * 3, r.get("b") or [None] * 3
        row = _quote_row(b[0], b[2] if len(b) > 2 else None, a[0], a[2] if len(a) > 2 else None)
        if row:
            out[bq[0]] = row
    return out, None


@dataclass
class Venue:
    name: str
    host: str
    path: str
    parse: Callable
    meta_path: Optional[str] = None
    meta_parse: Optional[Callable] = None
    port: int = 443
    meta: dict = field(default_factory=dict)
    meta_t: float = 0.0


def default_venues() -> list[Venue]:
    return [
        Venue("binance", "api.binance.com", "/api/v3/ticker/bookTicker", parse_binance_like),
        Venue("bitget", "api.bitget.com", "/api/v2/spot/market/tickers", parse_bitget),
        Venue("bybit", "api.bybit.com", "/v5/market/tickers?category=spot", parse_bybit),
        Venue("okx", "www.okx.com", "/api/v5/market/tickers?instType=SPOT", parse_okx),
        Venue("mexc", "api.mexc.com", "/api/v3/ticker/bookTicker", parse_binance_like),
        Venue("gate", "api.gateio.ws", "/api/v4/spot/tickers", parse_gate),
        Venue("kucoin", "api.kucoin.com", "/api/v1/market/allTickers", parse_kucoin),
        Venue("kraken", "api.kraken.com", "/0/public/Ticker", parse_kraken, "/0/public/AssetPairs", parse_kraken_pairs),
    ]


# ── fetching: one keep-alive HTTPS connection per venue, gzip, every failure isolated to its venue ──────────────────
class Fetcher:
    def __init__(self, ctx: ssl.SSLContext, timeout: float):
        self.ctx, self.timeout = ctx, timeout
        self._conns: dict = {}

    def get(self, v: Venue, path: str):
        for attempt in (1, 2):                              # a stale keep-alive connection gets one fresh retry
            conn = self._conns.get(v.name)
            if conn is None:
                conn = http.client.HTTPSConnection(v.host, v.port, context=self.ctx, timeout=self.timeout)
                self._conns[v.name] = conn
            try:
                conn.request("GET", path, headers={"User-Agent": "arbengine-spread-scanner", "Accept-Encoding": "gzip",
                                                   "Accept": "application/json"})
                r = conn.getresponse()
                body = r.read()
                if r.getheader("Content-Encoding", "").lower() == "gzip":
                    body = gzip.decompress(body)
                if r.status != 200:
                    raise RuntimeError(f"HTTP {r.status}: {body[:160]!r}")
                return json.loads(body)
            except (OSError, http.client.HTTPException) as e:
                conn.close()
                self._conns.pop(v.name, None)
                if attempt == 2:
                    raise RuntimeError(f"{type(e).__name__}: {e}") from e

    def snapshot(self, v: Venue, quote: str, meta_every_s: float = 3600.0) -> dict:
        t0 = time.time_ns()
        try:
            if v.meta_path and (not v.meta or time.monotonic() - v.meta_t > meta_every_s):
                v.meta, v.meta_t = v.meta_parse(self.get(v, v.meta_path)), time.monotonic()
            q, vts = v.parse(self.get(v, v.path), quote, v.meta)
            if not q:
                raise RuntimeError("0 USDT pairs parsed (field names changed?)")
            return {"t0": t0, "t1": time.time_ns(), "vt": vts, "q": q}
        except Exception as e:                              # isolate: one venue failing never stops a round
            return {"t0": t0, "t1": time.time_ns(), "err": f"{type(e).__name__}: {e}"[:300]}


# ── recording ──────────────────────────────────────────────────────────────────────────────────────────────────────
def keep_shared(snaps: dict) -> dict:
    """Drop bases quoted by fewer than two venues in this round (they can never form a pair)."""
    seen = Counter(b for s in snaps.values() if "q" in s for b in s["q"])
    for s in snaps.values():
        if "q" in s:
            s["q"] = {b: r for b, r in s["q"].items() if seen[b] >= 2}
    return snaps


def scan(venues: list[Venue], out: str, ctx: ssl.SSLContext, stop: threading.Event, interval: float = 5.0,
         hours: Optional[float] = None, status_s: float = 60.0, min_free_gb: float = 5.0, quote: str = QUOTE,
         fees: Optional[dict] = None, free_bytes: Callable[[str], int] = lambda p: shutil.disk_usage(p).free,
         log: Callable[[str], None] = print, timeout: Optional[float] = None, flush_s: float = 5.0) -> int:
    os.makedirs(out, exist_ok=True)
    fees = {**FEES_BPS, **(fees or {})}
    writer = md.HourlyWriter(out, "rounds", flush_s=flush_s, header=lambda: {"venues": [v.name for v in venues],
                                                                                "quote": quote, "interval_s": interval})
    fetcher = Fetcher(ctx, timeout or max(1.0, interval * 0.8))
    pool = cf.ThreadPoolExecutor(max_workers=len(venues))
    end = time.monotonic() + hours * 3600 if hours else None
    ok, fail, ms, nb = Counter(), Counter(), defaultdict(list), Counter()
    last_err: dict = {}
    inflight: dict = {}                                     # venue -> Future still running from an earlier round
    t_status = started = time.monotonic()
    nxt = time.monotonic()
    code = 0
    writer.meta(time.time_ns(), "start", venues=[v.name for v in venues], interval_s=interval, quote=quote, fees_bps=fees)
    try:
        while not stop.is_set():
            if end and time.monotonic() >= end:
                log("scan time reached")
                break
            if free_bytes(out) < min_free_gb * 1e9:
                log(f"STOP: less than {min_free_gb} GB free on the data disk")
                code = EXIT_DISK_FULL
                break
            t_round = time.time_ns()
            snaps = {}
            for v in venues:                                # one request per venue at a time: its connection is not shared
                f = inflight.get(v.name)
                if f is not None and not f.done():
                    snaps[v.name] = {"t0": t_round, "t1": t_round, "err": "previous fetch still running"}
                else:
                    inflight[v.name] = pool.submit(fetcher.snapshot, v, quote)
            deadline = time.monotonic() + interval * 0.9
            for v in venues:
                if v.name in snaps:
                    continue
                try:
                    snaps[v.name] = inflight[v.name].result(timeout=max(0.0, deadline - time.monotonic()))
                except cf.TimeoutError:                     # left running; its late result is discarded (stale)
                    snaps[v.name] = {"t0": t_round, "t1": time.time_ns(), "err": f"slower than {interval * 0.9:.1f} s"}
            keep_shared(snaps)
            writer.write(t_round, json.dumps({"t": t_round, "v": snaps}, separators=(",", ":")))
            writer.flush_if_due()
            for name, s in snaps.items():
                if "err" in s:
                    fail[name] += 1
                    last_err[name] = s["err"]
                else:
                    ok[name] += 1
                    ms[name].append((s["t1"] - s["t0"]) / 1e6)
                    nb[name] = len(s["q"])
            now = time.monotonic()
            if now - t_status >= status_s:
                t_status = now
                parts = [f"{v.name} {ok[v.name]}/{ok[v.name] + fail[v.name]} ok {statistics.median(ms[v.name]):.0f}ms {nb[v.name]}c"
                         if ms[v.name] else f"{v.name} 0/{fail[v.name]} ok" for v in venues]
                top = live_top(snaps, fees, 3)
                log(f"[{md.utc(time.time_ns())}] " + " | ".join(parts)
                    + f" | disk {writer.size() / 1e6:.0f} MB, free {free_bytes(out) / 1e9:.0f} GB")
                log("  now: " + ("; ".join(top) if top else "no gap above taker fees with >= $50 on top of book"))
                for name, e in last_err.items():
                    log(f"  {name} error: {e}")
                last_err.clear()
                ok.clear(), fail.clear(), ms.clear()
            nxt += interval
            delay = nxt - time.monotonic()
            if delay < 0:                                   # a slow round: skip ahead, never burst to catch up
                nxt = time.monotonic()
                delay = 0
            stop.wait(delay)
    finally:
        stop.set()
        pool.shutdown(wait=False, cancel_futures=True)
        writer.meta(time.time_ns(), "stop")
        writer.close()
    return code


def live_top(snaps: dict, fees: dict, n: int, min_usd: float = 50.0) -> list[str]:
    rows = []
    for g in pair_gaps(snaps, fees):
        if g[5] is not None and g[5] >= min_usd and g[3] >= 0:
            rows.append(g)
    rows.sort(key=lambda g: -g[3])
    return [f"{b} buy {a}→sell {s} net {net:+.1f} bps (${size:,.0f})" for b, a, s, net, _, size in rows[:n]]


# ── analysis ───────────────────────────────────────────────────────────────────────────────────────────────────────
def suspect_bases(snaps: dict, max_dev: float) -> set:
    """Bases where some venue's mid is more than max_dev away from the median of all venues' mids (identity/frozen)."""
    mids = defaultdict(list)
    for s in snaps.values():
        for b, r in (s.get("q") or {}).items():
            mids[b].append((r[0] + r[2]) / 2)
    bad = set()
    for b, ms in mids.items():
        if len(ms) >= 2:
            med = statistics.median(ms)
            if any(abs(m / med - 1) > max_dev for m in ms):
                bad.add(b)
    return bad


def pair_gaps(snaps: dict, fees: dict, skip: frozenset = frozenset()):
    """Yield (base, buy_venue, sell_venue, net_bps, gross_bps, top_of_book_usd | None) for every ordered venue pair."""
    by_base = defaultdict(list)
    for name, s in snaps.items():
        for b, r in (s.get("q") or {}).items():
            if b not in skip:
                by_base[b].append((name, r))
    for b, rows in by_base.items():
        if len(rows) < 2:
            continue
        for a, ra in rows:
            for s, rs in rows:
                if a == s or rs[0] <= ra[2]:                # sell bid must beat buy ask before fees
                    continue
                gross = (rs[0] / ra[2] - 1) * 1e4
                size = None if ra[3] is None or rs[1] is None else min(ra[2] * ra[3], rs[0] * rs[1])
                yield b, a, s, gross - fees.get(a, 10.0) - fees.get(s, 10.0), gross, size


@dataclass
class Episode:
    base: str
    buy: str
    sell: str
    start: int
    last: int
    rounds: int = 1
    nets: list = field(default_factory=list)
    best_usd: float = 0.0                                   # net profit if taken ONCE at the best snapshot, top of book only
    sizes: list = field(default_factory=list)               # top-of-book USD per snapshot (min of both legs)
    size_known: bool = True
    legs: set = field(default_factory=set)                  # distinct (buy ask, buy size, sell bid, sell size) seen

    def duration_s(self, interval: float) -> float:
        return (self.last - self.start) / 1e9 + interval


def analyse(files: list[str], fees: dict, min_net_bps: float = 0.0, min_usd: float = 50.0, min_rounds: int = 2,
            max_dev: float = 0.10, suspect_minutes: float = 10.0) -> dict:
    open_eps: dict = {}
    done: list[Episode] = []
    first = last = None
    interval = 5.0
    rounds = 0
    venue_ok, venue_fail, venue_ms = Counter(), Counter(), defaultdict(list)
    ref = defaultdict(list)                                 # (venueA, venueB) -> |mid diff| bps on REFERENCE_BASES
    suspects: Counter = Counter()
    blips: Counter = Counter()                              # one-snapshot gaps: counted, not kept (memory over weeks)

    def close(key):
        ep = open_eps.pop(key)
        if ep.rounds < min_rounds:
            blips[f"{ep.base} {ep.buy}→{ep.sell}"] += 1
        else:
            done.append(ep)

    for f in files:
        for ns, payload in md.read_file(f):
            if payload.startswith("#"):
                try:
                    m = json.loads(payload[1:])
                    interval = float(m.get("interval_s", interval))
                except (ValueError, TypeError):
                    pass
                continue
            try:
                rnd = json.loads(payload)
            except ValueError:
                continue
            t, snaps = rnd.get("t", ns), rnd.get("v") or {}
            rounds += 1
            first = first if first is not None else t
            last = t
            live = {n for n, s in snaps.items() if "q" in s}
            for n, s in snaps.items():
                if "q" in s:
                    venue_ok[n] += 1
                    venue_ms[n].append((s["t1"] - s["t0"]) / 1e6)
                else:
                    venue_fail[n] += 1
            bad = suspect_bases(snaps, max_dev)
            for b in bad:
                suspects[b] += 1
            names = sorted(live)
            for i, a in enumerate(names):
                for s in names[i + 1:]:
                    qa, qs = snaps[a]["q"], snaps[s]["q"]
                    for b in REFERENCE_BASES:
                        if b in qa and b in qs and b not in bad:
                            ma, ms_ = (qa[b][0] + qa[b][2]) / 2, (qs[b][0] + qs[b][2]) / 2
                            ref[(a, s)].append(abs(ma / ms_ - 1) * 1e4)
            hit = set()
            for b, a, s, net, gross, size in pair_gaps(snaps, fees, frozenset(bad)):
                if net < min_net_bps or (size is not None and size < min_usd):
                    continue
                key = (b, a, s)
                hit.add(key)
                ra, rs = snaps[a]["q"][b], snaps[s]["q"][b]
                ep = open_eps.get(key)
                if ep is None:
                    ep = open_eps[key] = Episode(b, a, s, t, t, 0)
                ep.rounds += 1
                ep.last = t
                ep.nets.append(net)
                ep.legs.add((ra[2], ra[3], rs[0], rs[1]))
                if size is None:
                    ep.size_known = False
                else:
                    ep.sizes.append(size)
                    ep.best_usd = max(ep.best_usd, net / 1e4 * size)
            for key in [k for k in open_eps if k not in hit]:   # ended, or one leg missing this round: no bridging
                close(key)
    for key in list(open_eps):
        close(key)
    span_days = max(((last - first) / 1e9 + interval) / 86400, 1e-9) if first is not None else 0.0
    return {"episodes": done, "rounds": rounds, "first": first, "last": last, "interval": interval,
            "span_days": span_days, "venue_ok": venue_ok, "venue_fail": venue_fail, "venue_ms": venue_ms, "ref": ref,
            "suspect_bases": suspects, "blips": blips, "params": {"min_net_bps": min_net_bps, "min_usd": min_usd, "min_rounds": min_rounds,
                                                    "max_dev": max_dev, "suspect_minutes": suspect_minutes, "fees": fees}}


def classify_episode(ep: Episode, interval: float, min_rounds: int, suspect_minutes: float) -> str:
    if ep.rounds < min_rounds:
        return "blip"                                       # one snapshot only: may be a timing artefact
    if ep.duration_s(interval) > suspect_minutes * 60:
        return "long"                                       # rarely tradable: transfers closed / identity / dead market
    if ep.rounds >= 3 and len(ep.legs) == 1:
        return "stale"                                      # neither quote moved at all: frozen book on one side
    if not ep.size_known:
        return "nosize"
    return "ok"


def report(a: dict) -> str:
    p, iv, days = a["params"], a["interval"], a["span_days"]
    if not a["rounds"]:
        return "# Spread scanner — tidak ada data\n"
    kinds = defaultdict(list)
    for ep in a["episodes"]:
        kinds[classify_episode(ep, iv, p["min_rounds"], p["suspect_minutes"])].append(ep)
    ok = kinds["ok"]
    lines = ["# Spread scanner — laporan", "",
             f"{md.utc(a['first'])} → {md.utc(a['last'])} ({days * 24:.1f} jam, {a['rounds']:,} snapshot, interval {iv:g} s). "
             f"Syarat: net ≥ {p['min_net_bps']:g} bps setelah fee taker kedua sisi, ukuran top-of-book ≥ ${p['min_usd']:g}, "
             f"bertahan ≥ {p['min_rounds']} snapshot berturut-turut.", "",
             "**Cara membaca:** $/hari = jumlah, per episode, profit bersih bila diambil SEKALI di snapshot terbaiknya, "
             "hanya sebesar ukuran top-of-book. Ini sinyal peringkat, bukan ramalan: pemain lain bisa lebih dulu "
             "(terlalu optimis), level book berikutnya tidak dihitung (terlalu pesimis). Fee = asumsi tier dasar.", "",
             "## 1. Venue", "", "| Venue | Snapshot OK | Gagal | Waktu fetch p50 (ms) | Fee taker asumsi (bps) |",
             "|---|---|---|---|---|"]
    for v in sorted(set(a["venue_ok"]) | set(a["venue_fail"])):
        ms = a["venue_ms"].get(v) or []
        lines.append(f"| {v} | {a['venue_ok'][v]:,} | {a['venue_fail'][v]:,} | {statistics.median(ms):.0f} |"
                     f" {p['fees'].get(v, 10.0):g} |" if ms else f"| {v} | 0 | {a['venue_fail'][v]:,} | – | {p['fees'].get(v, 10.0):g} |")
    pairs = defaultdict(list)
    for ep in ok:
        pairs[tuple(sorted((ep.buy, ep.sell)))].append(ep)
    lines += ["", "## 2. Peringkat pasangan venue (episode yang lolos semua filter)", "",
              "| Pasangan | $/hari | Episode/hari | Durasi median (s) | Net median (bps) | Ukuran median ($) | Koin berbeda | "
              "Selisih mid BTC/ETH/SOL/XRP/DOGE p50 (bps) |", "|---|---|---|---|---|---|---|---|"]
    rank = []
    for pr, eps in pairs.items():
        usd = sum(e.best_usd for e in eps) / days
        ref = a["ref"].get(pr) or []
        rank.append((usd, pr, eps, statistics.median(ref) if ref else None))
    for usd, pr, eps, ref in sorted(rank, key=lambda x: -x[0]):
        sizes = [statistics.median(e.sizes) for e in eps if e.sizes]
        lines.append(f"| {pr[0]} ↔ {pr[1]} | {usd:,.2f} | {len(eps) / days:,.1f} | "
                     f"{statistics.median(e.duration_s(iv) for e in eps):.0f} | {statistics.median(statistics.median(e.nets) for e in eps):.1f} | "
                     f"{statistics.median(sizes):,.0f} | {len({e.base for e in eps})} | {'–' if ref is None else f'{ref:.1f}'} |"
                     if sizes else f"| {pr[0]} ↔ {pr[1]} | {usd:,.2f} | {len(eps) / days:,.1f} | – | – | – | {len({e.base for e in eps})} | – |")
    if not rank:
        lines.append("| (tidak ada episode yang lolos) | | | | | | | |")
    lines += ["", "Pasangan tanpa episode tidak tercantum. Selisih mid koin besar = seberapa ketat dua venue terhubung "
              "(kecil = efisien, peluang jarang).", "",
              "## 3. Koin teratas (semua pasangan)", "", "| Koin | Beli di → jual di | $/hari | Episode | Durasi median (s) | Net maks (bps) |",
              "|---|---|---|---|---|---|"]
    per = defaultdict(list)
    for ep in ok:
        per[(ep.base, ep.buy, ep.sell)].append(ep)
    top = sorted(per.items(), key=lambda kv: -sum(e.best_usd for e in kv[1]))[:25]
    for (b, x, y), eps in top:
        lines.append(f"| {b} | {x} → {y} | {sum(e.best_usd for e in eps) / days:,.2f} | {len(eps)} | "
                     f"{statistics.median(e.duration_s(iv) for e in eps):.0f} | {max(max(e.nets) for e in eps):.1f} |")
    lines += ["", "## 4. Dibuang (kemungkinan TIDAK bisa diambil)", "",
              "| Jenis | Episode | Contoh |", "|---|---|---|"]
    expl = {"blip": f"hanya 1 snapshot (bisa artefak waktu fetch)", "long": f"> {p['suspect_minutes']:g} menit (biasanya transfer ditutup / token beda / pasar mati)",
            "stale": "harga kedua sisi tidak berubah sama sekali (book beku)", "nosize": "venue tidak memberi ukuran top-of-book"}
    for k in ("blip", "long", "stale", "nosize"):
        ex = a["blips"] if k == "blip" else Counter(f"{e.base} {e.buy}→{e.sell}" for e in kinds[k])
        n = sum(ex.values())
        lines.append(f"| {expl[k]} | {n:,} | {', '.join(f'{x} ({c})' for x, c in ex.most_common(4)) or '–'} |")
    sb = a["suspect_bases"].most_common(10)
    lines += ["", f"Koin dengan harga mid > {p['max_dev'] * 100:g} % dari median venue lain (dikeluarkan per snapshot; "
              "biasanya token berbeda dengan ticker sama): " + (", ".join(f"{b} ({c})" for b, c in sb) or "–")]
    return "\n".join(lines)


def round_files(root: str, since_ns: Optional[int]) -> list[str]:
    d = os.path.join(root, "rounds")
    files = sorted((f for f in os.listdir(d) if f.endswith(".tsv.gz")), key=md._file_key) if os.path.isdir(d) else []
    if since_ns is not None:
        files = [f for f in files if md._file_hour_end_ns(f) > since_ns]
    return [os.path.join(d, f) for f in files]


# ── probe: one call per venue, show what parsed ────────────────────────────────────────────────────────────────────
def probe(venues: list[Venue], ctx: ssl.SSLContext, quote: str = QUOTE, log: Callable[[str], None] = print) -> int:
    f = Fetcher(ctx, 10.0)
    bad = 0
    for v in venues:
        s = f.snapshot(v, quote)
        if "err" in s:
            bad += 1
            log(f"FAIL {v.name:8s} {s['err']}")
            continue
        q = s["q"]
        sample = "; ".join(f"{b} bid {q[b][0]:g} ({q[b][1]}) ask {q[b][2]:g} ({q[b][3]})" for b in ("BTC", "ETH") if b in q)
        log(f"OK   {v.name:8s} {len(q):5d} {quote} pairs in {(s['t1'] - s['t0']) / 1e6:5.0f} ms | {sample}")
    return 1 if bad else 0


def main(argv: Optional[list] = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0], formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default="scan", help="output directory for recording (default ./scan)")
    ap.add_argument("--probe", action="store_true", help="fetch every venue once and show what parsed")
    ap.add_argument("--report", metavar="DIR", help="analyse a recording directory instead of recording")
    ap.add_argument("--last-hours", type=float, default=None, help="with --report: only the last N hours")
    ap.add_argument("--venues", default=",".join(v.name for v in default_venues()))
    ap.add_argument("--interval", type=float, default=5.0, help="seconds between snapshots (default 5)")
    ap.add_argument("--hours", type=float, default=None, help="stop recording after N hours")
    ap.add_argument("--min-free-gb", type=float, default=5.0)
    ap.add_argument("--fee", action="append", default=[], metavar="VENUE=BPS", help="override a taker fee, e.g. kraken=26")
    ap.add_argument("--min-net-bps", type=float, default=0.0)
    ap.add_argument("--min-usd", type=float, default=50.0)
    ap.add_argument("--min-rounds", type=int, default=2)
    ap.add_argument("--suspect-minutes", type=float, default=10.0)
    a = ap.parse_args(argv)

    fees = dict(FEES_BPS)
    for item in a.fee:
        name, _, bps = item.partition("=")
        if name not in FEES_BPS or _f(bps) is None or _f(bps) < 0:
            ap.error(f"--fee {item}: expected one of {','.join(FEES_BPS)}=<bps >= 0>")
        fees[name] = _f(bps)
    if a.report:
        if a.last_hours is not None and a.last_hours <= 0:
            ap.error("--last-hours must be > 0")
        since = time.time_ns() - int(a.last_hours * 3600e9) if a.last_hours else None
        print(report(analyse(round_files(a.report, since), fees, a.min_net_bps, a.min_usd, a.min_rounds,
                             suspect_minutes=a.suspect_minutes)))
        return 0
    known = {v.name: v for v in default_venues()}
    names = [n.strip() for n in a.venues.split(",") if n.strip()]
    if len(names) < 2 or any(n not in known for n in names):
        ap.error(f"--venues: at least two of {','.join(known)}")
    if a.interval < 1:
        ap.error("--interval must be >= 1 s (venue rate limits)")
    if a.hours is not None and a.hours <= 0:
        ap.error("--hours must be > 0")
    venues = [known[n] for n in names]
    ctx = ssl.create_default_context()
    if a.probe:
        return probe(venues, ctx)
    stop = threading.Event()
    for sig in (signal.SIGINT, signal.SIGTERM):
        signal.signal(sig, lambda *_: stop.set())
    print(f"scanning {', '.join(names)} every {a.interval:g} s into {os.path.abspath(a.out)}", flush=True)
    return scan(venues, a.out, ctx, stop, interval=a.interval, hours=a.hours, min_free_gb=a.min_free_gb, fees=fees,
                log=lambda m: print(m, flush=True))


if __name__ == "__main__":
    sys.exit(main())
