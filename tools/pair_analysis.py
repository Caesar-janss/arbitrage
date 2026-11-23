#!/usr/bin/env python3
"""
ArbEngine Tahap 2 — what the Binance <-> Bitget recording says about spatial arbitrage at millisecond resolution.
Stdlib only, reads the files written by tools/md_recorder.py, never touches the network.

  nice -n 19 python3 tools/pair_analysis.py --data ~/md --last-hours 6          # quick look (a few minutes)
  nice -n 19 python3 tools/pair_analysis.py --data ~/md --last-hours 24 > ~/pair_report.txt

Why this and not the scanner: the scanner sees every venue but only every 5 s (REST tickers, which can be cached). The
recorder sees two venues, every top-of-book change, stamped on one machine clock when the socket read returned. So here
a "gap" is measured from the moment one venue's book crossed the other's until the moment it stopped crossing.

Inputs per symbol: Binance <sym>@bookTicker (best bid/ask + size, every change) and Bitget books1 (level-1 snapshot).
Depth (depth20 / books15) and trades are not used yet.

An EPISODE for (symbol, buy venue, sell venue, fee level) is open while
    sell_bid / buy_ask - 1 - fee_level  >= 0         (fee_level = total bps for both taker legs, e.g. 20 = 10 + 10)
and  min(size at sell_bid, size at buy_ask) * buy_ask >= --min-usd.
For each episode it records:
  duration      first crossing to the first update that ended it (0 ms = opened and closed inside one socket read:
                the order of those messages is known, the time between them is not)
  opener        the venue whose update opened it = the venue that moved first
  closer        the venue whose update closed it; "gap" = a connection dropped (quote unknown), "end" = data ended
  capture[L]    what one order pair arriving L ms after the opening would have met: net bps x top-of-book $ of the
                state that held at open + L, or nothing if the gap had already closed (L >= duration).
                Optimistic: nobody else takes it first, both legs fill at the quote. Pessimistic: only level 1 counts.
Quotes of a connection are forgotten when it logs disconnect/stop/start, so a stale quote never makes a gap.
"""
from __future__ import annotations

import argparse
import heapq
import itertools
import json
import os
import re
import sys
import time
from collections import Counter, deque
from typing import Callable, Iterator, Optional

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import md_recorder as md  # noqa: E402  (same file format, same reader)

VENUES = ("binance", "bitget")
DIRECTIONS = (("bitget", "binance"), ("binance", "bitget"))     # (buy at, sell at)
DEFAULT_LEVELS = (20, 15, 10, 0)
DEFAULT_LAT_MS = (1, 5, 10, 25, 50, 100, 250)
TOP_EPISODES = 15                    # largest main-level normal-feed episodes kept per latency (res["top_eps"])
BASIS_EVERY_NS = 1_000_000_000
EPS = 1e-9

_BN = re.compile(r'"s":"([^"]+)","b":"([^"]+)","B":"([^"]+)","a":"([^"]+)","A":"([^"]+)"')
_BN_E = re.compile(r'"E":(\d+)')
_BG_TS = re.compile(r'"ts":"(\d+)"')
LAG_SANE_NS = (-10_000_000_000, 60_000_000_000)        # outside this a venue timestamp is not a clock reading
LAG_FRESH_NS = 2_000_000_000                            # a lag sample older than this says nothing about now
FLOOR_MINUTES = 10                                      # floor = smallest lag of the last 10 minutes
DEFAULT_FEED_LAG_MS = 20.0
_BG = re.compile(r'"instId":"([^"]+)"\},"data":\[\{"asks":\[\["([^"]+)","([^"]+)"\]\],"bids":\[\["([^"]+)","([^"]+)"\]\]')

Quote = tuple  # (bid, bid_qty, ask, ask_qty)


def _valid(q: tuple) -> bool:
    bid, bq, ask, aq = q
    return bid > 0 and ask > 0 and bq > 0 and aq > 0


def parse_binance(payload: str) -> Optional[tuple]:
    """bookTicker message -> (symbol, bid, bid_qty, ask, ask_qty), or None for an empty side."""
    m = _BN.search(payload)
    try:
        if m:
            sym, q = m[1], (float(m[2]), float(m[3]), float(m[4]), float(m[5]))
        else:
            d = json.loads(payload)["data"]
            sym, q = d["s"], (float(d["b"]), float(d["B"]), float(d["a"]), float(d["A"]))
    except (ValueError, KeyError, TypeError):
        return None
    return (sym, *q) if _valid(q) else None


def parse_bitget(payload: str) -> Optional[tuple]:
    """books1 message -> (symbol, bid, bid_qty, ask, ask_qty), or None for an empty side."""
    m = _BG.search(payload)
    try:
        if m:
            sym, q = m[1], (float(m[4]), float(m[5]), float(m[2]), float(m[3]))
        else:
            j = json.loads(payload)
            d = j["data"][0]
            if not d.get("asks") or not d.get("bids"):
                return None
            sym = j["arg"]["instId"]
            q = (float(d["bids"][0][0]), float(d["bids"][0][1]), float(d["asks"][0][0]), float(d["asks"][0][1]))
    except (ValueError, KeyError, TypeError, IndexError):
        return None
    return (sym.upper(), *q) if _valid(q) else None


def median(xs: list) -> Optional[float]:
    if not xs:
        return None
    s = sorted(xs)
    n = len(s)
    return s[n // 2] if n % 2 else (s[n // 2 - 1] + s[n // 2]) / 2


def top_share(xs: list, k: int) -> Optional[float]:
    """Share of the total carried by the k largest values (None when the total is not positive)."""
    tot = sum(xs)
    return None if not xs or tot <= 0 else sum(sorted(xs, reverse=True)[:k]) / tot


def pct(xs: list, q: float) -> Optional[float]:
    """Nearest-rank percentile (never below the q-th sample)."""
    if not xs:
        return None
    s = sorted(xs)
    return s[min(len(s) - 1, max(0, -(-int(q * len(s) * 1_000_000) // 1_000_000) - 1))]


# ── reading: all connections of both venues, merged by receive time ────────────────────────────────────────────────
def _conn_events(dirpath: str, conn: str, since_ns: Optional[int]) -> Iterator[tuple[int, str, str]]:
    files = sorted((f for f in os.listdir(dirpath) if f.endswith(".tsv.gz")), key=md._file_key)
    if since_ns is not None:
        files = [f for f in files if md._file_hour_end_ns(f) > since_ns]
    for f in files:
        for ns, payload in md.read_file(os.path.join(dirpath, f)):
            if since_ns is None or ns >= since_ns:
                yield ns, conn, payload


def events(root: str, since_ns: Optional[int] = None) -> Iterator[tuple[int, str, str]]:
    conns = sorted(d for d in os.listdir(root) if os.path.isdir(os.path.join(root, d)) and d.split("-")[0] in VENUES)
    return heapq.merge(*(_conn_events(os.path.join(root, c), c, since_ns) for c in conns), key=lambda e: e[0])


# ── how late is each feed right now? ───────────────────────────────────────────────────────────────────────────────
class FeedLag:
    """
    lag = our receive time - the venue's own timestamp (Binance trade "E", Bitget books1 "ts"). It contains the network,
    the venue's internal queueing AND the unknown offset between the two clocks, so only its excess over a floor means
    something: floor = the smallest lag of the last FLOOR_MINUTES minutes (absorbs clock offset and steps within 10 min).
    excess > threshold when an episode opens = what we saw from that venue was already that much older than usual.
    """

    def __init__(self):
        self.mins: dict = {v: deque() for v in VENUES}       # (minute, smallest lag ns in that minute)
        self.latest: dict = {}                                # venue -> (recv_ns, lag_ns)
        self.raw: dict = {v: Counter() for v in VENUES}       # ms -> count
        self.excess: dict = {v: Counter() for v in VENUES}

    def _floor(self, venue: str) -> int:
        return min(m for _, m in self.mins[venue])

    def add(self, venue: str, recv_ns: int, venue_ms: int) -> None:
        lag = recv_ns - venue_ms * 1_000_000
        if not LAG_SANE_NS[0] <= lag <= LAG_SANE_NS[1]:
            return
        minute = recv_ns // 60_000_000_000
        d = self.mins[venue]
        if d and d[-1][0] == minute:
            if lag < d[-1][1]:
                d[-1] = (minute, lag)
        else:
            d.append((minute, lag))
        while d[0][0] <= minute - FLOOR_MINUTES:
            d.popleft()
        self.latest[venue] = (recv_ns, lag)
        self.raw[venue][lag // 1_000_000] += 1
        self.excess[venue][(lag - self._floor(venue)) // 1_000_000] += 1

    def excess_ms(self, venue: str, now_ns: int) -> Optional[float]:
        last = self.latest.get(venue)
        if last is None or now_ns - last[0] > LAG_FRESH_NS:
            return None
        return (last[1] - self._floor(venue)) / 1e6


# ── one episode ────────────────────────────────────────────────────────────────────────────────────────────────────
class Episode:
    __slots__ = ("open_ns", "net", "usd", "net_open", "usd_open", "net_max", "opener", "lat_ns", "cap", "k", "feed", "ex")

    def __init__(self, ns: int, net: float, usd: float, opener: str, lat_ns: tuple):
        self.open_ns, self.net, self.usd, self.opener, self.lat_ns = ns, net, usd, opener, lat_ns
        self.net_open, self.usd_open, self.net_max = net, usd, net
        self.cap: list = [None] * len(lat_ns)
        self.k = 0                                          # latencies already decided (lat_ns is ascending)
        self.feed, self.ex = "unknown", {}

    def step(self, ns: int, net: float, usd: float) -> None:
        """A new state from ns on. Latencies reached by now saw the state that held until this update."""
        dt = ns - self.open_ns
        while self.k < len(self.lat_ns) and dt >= self.lat_ns[self.k]:
            self.cap[self.k] = self.net * self.usd / 1e4
            self.k += 1
        self.net, self.usd = net, usd
        self.net_max = max(self.net_max, net)

    def close(self, ns: int) -> float:
        """Arriving at open+L with L < duration still met the last state; L >= duration met nothing (None)."""
        dt = ns - self.open_ns
        while self.k < len(self.lat_ns) and dt > self.lat_ns[self.k]:
            self.cap[self.k] = self.net * self.usd / 1e4
            self.k += 1
        return dt / 1e6


def _agg() -> dict:
    return {"n": 0, "zero": 0, "dur": [], "net_max": [], "usd": [], "opener": Counter(), "closer": Counter(),
            "feed": Counter(), "cap_usd": None, "cap_n": None, "cap_usd_normal": None, "cap_n_normal": None}


# ── the analysis ───────────────────────────────────────────────────────────────────────────────────────────────────
def analyse(root: str, since_ns: Optional[int] = None, symbols: Optional[set] = None,
            fee_levels: tuple = DEFAULT_LEVELS, latencies_ms: tuple = DEFAULT_LAT_MS, min_usd: float = 50.0,
            keep_episodes: bool = False, progress: Optional[Callable[[int, int], None]] = None,
            feed_lag_ms: float = DEFAULT_FEED_LAG_MS) -> dict:
    lat_ms = tuple(sorted(latencies_ms))
    lat_ns = tuple(int(x * 1_000_000) for x in lat_ms)
    levels = tuple(fee_levels)
    res: dict = {"first_ns": None, "last_ns": None, "msgs": Counter(), "gaps": Counter(), "levels": levels,
                 "lat_ms": lat_ms, "min_usd": min_usd, "agg": {}, "basis": {}, "symbols": set(),
                 "episodes": [] if keep_episodes else None, "feed_lag_ms": feed_lag_ms,
                 "main_caps": [],             # main fee level: (sym, buy, feed, captures per latency) per episode
                 "alt": {}}                   # main fee level, per symbol: consecutive episodes and direction changes
    top_heaps: list = [[] for _ in lat_ms]                  # per latency: min-heap of (capture $, seq, details), size <= TOP_EPISODES
    seq = itertools.count()
    feed = FeedLag()
    res["feedlag"] = feed
    quotes: dict = {}                                       # sym -> {venue: (bid, bq, ask, aq, conn)}
    conn_syms: dict = {}                                    # conn -> {sym}
    open_eps: dict = {}                                     # (sym, buy, level) -> Episode
    last_basis: dict = {}

    def finish(key: tuple, ep: Episode, ns: int, closer: str) -> None:
        sym, buy, level = key
        sell = "binance" if buy == "bitget" else "bitget"
        dur = ep.close(ns)
        a = res["agg"].setdefault((level, sym, buy), _agg())
        if a["cap_usd"] is None:
            a["cap_usd"], a["cap_n"] = [0.0] * len(lat_ms), [0] * len(lat_ms)
            a["cap_usd_normal"], a["cap_n_normal"] = [0.0] * len(lat_ms), [0] * len(lat_ms)
        a["feed"][ep.feed] += 1
        a["n"] += 1
        a["zero"] += dur == 0
        a["dur"].append(dur)
        a["net_max"].append(ep.net_max)
        a["usd"].append(ep.usd_open)
        a["opener"][ep.opener] += 1
        a["closer"][closer] += 1
        if level == levels[0]:
            res["main_caps"].append((sym, buy, ep.feed, tuple(ep.cap)))
            if ep.feed == "normal":
                for i, v in enumerate(ep.cap):
                    h = top_heaps[i]
                    if v is not None and (len(h) < TOP_EPISODES or v > h[0][0]):
                        item = (v, next(seq), {"sym": sym, "buy": buy, "sell": sell, "open_ns": ep.open_ns, "dur_ms": dur,
                                               "net_open": ep.net_open, "net_max": ep.net_max, "usd_open": ep.usd_open,
                                               "feed": ep.feed, "capture": dict(zip(lat_ms, ep.cap))})
                        if len(h) < TOP_EPISODES:
                            heapq.heappush(h, item)
                        else:
                            heapq.heapreplace(h, item)
            al = res["alt"].setdefault(sym, {"pairs": 0, "switches": 0, "last": None})
            if al["last"] is not None:
                al["pairs"] += 1
                al["switches"] += al["last"] != buy
            al["last"] = buy
        for i, v in enumerate(ep.cap):
            if v is not None:
                a["cap_usd"][i] += v
                a["cap_n"][i] += 1
                if ep.feed == "normal":
                    a["cap_usd_normal"][i] += v
                    a["cap_n_normal"][i] += 1
        if res["episodes"] is not None:
            res["episodes"].append({"sym": sym, "buy": buy, "sell": sell, "level": level, "open_ns": ep.open_ns,
                                    "dur_ms": dur, "net_open": ep.net_open, "net_max": ep.net_max,
                                    "usd_open": ep.usd_open, "opener": ep.opener, "closer": closer,
                                    "capture": dict(zip(lat_ms, ep.cap)), "feed": ep.feed, "lag_excess_ms": ep.ex})

    def feed_state(ns: int) -> tuple:
        ex = {v: feed.excess_ms(v, ns) for v in VENUES}
        if any(x is not None and x > feed_lag_ms for x in ex.values()):
            return "late", ex
        return ("unknown" if any(x is None for x in ex.values()) else "normal"), ex

    def evaluate(sym: str, ns: int, mover: str) -> None:
        q = quotes.get(sym, {})
        state = None
        for buy, sell in DIRECTIONS:
            qb, qs = q.get(buy), q.get(sell)
            if qb and qs:
                gross = (qs[0] / qb[2] - 1) * 1e4
                usd = min(qb[3], qs[1]) * qb[2]
            else:
                gross = usd = None
            for level in levels:
                key = (sym, buy, level)
                ok = gross is not None and gross - level >= -EPS and usd >= min_usd
                ep = open_eps.get(key)
                if ok:
                    if ep is None:
                        ep = open_eps[key] = Episode(ns, gross - level, usd, mover, lat_ns)
                        if state is None:
                            state = feed_state(ns)
                        ep.feed, ep.ex = state
                    else:
                        ep.step(ns, gross - level, usd)
                elif ep is not None:
                    del open_eps[key]
                    finish(key, ep, ns, mover)                 # mover is "gap" when a quote was dropped

    n = 0
    for ns, conn, payload in events(root, since_ns):
        n += 1
        if progress and n % 5_000_000 == 0:
            progress(n, ns)
        venue = conn.split("-")[0]
        if payload.startswith("#"):
            if '"ev":"disconnect"' in payload or '"ev":"stop"' in payload or '"ev":"start"' in payload:
                res["gaps"][conn] += '"ev":"disconnect"' in payload
                for sym in conn_syms.get(conn, ()):
                    qv = quotes.get(sym, {})
                    if venue in qv and qv[venue][4] == conn:
                        del qv[venue]
                        evaluate(sym, ns, "gap")
            continue
        if venue == "binance":
            if not payload.startswith('{"stream":"'):
                continue
            if payload.find('@bookTicker"', 11, 60) < 0:
                # spot quotes carry no venue clock, trades do (E); perp: aggTrade
                if payload.find('@trade"', 11, 60) > 0 or payload.find('@aggTrade"', 11, 60) > 0:
                    m = _BN_E.search(payload, 0, 200)
                    if m:
                        feed.add("binance", ns, int(m[1]))
                continue
            m = _BN_E.search(payload, 0, 120)                 # USDⓈ-M bookTicker carries E itself
            if m:
                feed.add("binance", ns, int(m[1]))
            p = parse_binance(payload)
        else:
            if not payload.startswith('{"action"') or payload.find('"channel":"books1"', 0, 120) < 0:
                continue
            m = _BG_TS.search(payload)
            if m:
                feed.add("bitget", ns, int(m[1]))
            p = parse_bitget(payload)
        if p is None or (symbols and p[0] not in symbols):
            continue
        sym = p[0]
        if res["first_ns"] is None:
            res["first_ns"] = ns
        res["last_ns"] = ns
        res["msgs"][venue] += 1
        res["symbols"].add(sym)
        quotes.setdefault(sym, {})[venue] = (p[1], p[2], p[3], p[4], conn)
        conn_syms.setdefault(conn, set()).add(sym)
        evaluate(sym, ns, venue)
        qv = quotes[sym]
        if len(qv) == 2 and ns - last_basis.get(sym, -BASIS_EVERY_NS) >= BASIS_EVERY_NS:
            last_basis[sym] = ns
            mb = (qv["binance"][0] + qv["binance"][2]) / 2
            mg = (qv["bitget"][0] + qv["bitget"][2]) / 2
            res["basis"].setdefault(sym, []).append((mb / mg - 1) * 1e4)
    end_ns = res["last_ns"] or 0
    for key, ep in sorted(open_eps.items(), key=lambda kv: kv[1].open_ns):
        finish(key, ep, end_ns, "end")
    res["top_eps"] = [[d for _, _, d in sorted(h, key=lambda x: (-x[0], x[1]))] for h in top_heaps]
    return res


# ── report ─────────────────────────────────────────────────────────────────────────────────────────────────────────
def _f(x: Optional[float], d: int = 1) -> str:
    return "–" if x is None else f"{x:,.{d}f}"


def _pc(a: int, b: int) -> str:
    return "–" if not b else f"{100 * a / b:.0f} %"


def report(res: dict, ref_latency_ms: float = 10) -> str:
    out = ["# Binance ↔ Bitget — analisis rekaman (resolusi milidetik)", ""]
    if res["first_ns"] is None:
        out.append("Tidak ada data di rentang ini (cek --data dan --last-hours).")
        return "\n".join(out) + "\n"
    days = max((res["last_ns"] - res["first_ns"]) / 86_400e9, 1e-9)
    lat = res["lat_ms"]
    ref = min(range(len(lat)), key=lambda i: abs(lat[i] - ref_latency_ms))
    main = res["levels"][0]
    out += [f"{md.utc(res['first_ns'])} → {md.utc(res['last_ns'])} ({days * 24:.1f} jam). "
            f"Fee level utama = {main} bps untuk kedua leg sekaligus. Ukuran top-of-book minimal ${res['min_usd']:,.0f}.",
            "",
            "## 1. Data", "",
            f"- Pesan dipakai: Binance bookTicker {res['msgs']['binance']:,}, Bitget books1 {res['msgs']['bitget']:,}.",
            f"- Koneksi putus (kutipan venue itu dilupakan sampai data baru): "
            + (", ".join(f"{c} {n}×" for c, n in sorted(res["gaps"].items())) or "tidak ada") + ".",
            f"- Simbol: {', '.join(sorted(res['symbols']))}."]
    fl = res["feedlag"]
    for v, src in (("binance", "trade E"), ("bitget", "books1 ts")):
        if fl.raw[v]:
            out.append(f"- Keterlambatan feed {v} (terima − stempel venue, {src}; {sum(fl.raw[v].values()):,} sampel): "
                       f"p50 {md._hist_pct(fl.raw[v], 0.5)} ms, p99 {md._hist_pct(fl.raw[v], 0.99)} ms; "
                       f"kelebihan di atas dasar {FLOOR_MINUTES} menit: p50 {md._hist_pct(fl.excess[v], 0.5)} ms, "
                       f"p99 {md._hist_pct(fl.excess[v], 0.99)} ms, maks {max(fl.excess[v])} ms.")
        else:
            out.append(f"- Keterlambatan feed {v}: tidak ada sampel ({src}).")
    out += [f"- Episode berstatus **feed telat** bila saat dibuka salah satu venue > {res['feed_lag_ms']:g} ms di atas "
            f"dasarnya: kutipan yang kita lihat mungkin sudah tidak ada di venue. Selisih jam antar mesin ikut di "
            f"keterlambatan mentah, tidak di kelebihannya.", ""]

    def rows_for(level: int):
        return sorted(((k[1], k[2], a) for k, a in res["agg"].items() if k[0] == level), key=lambda r: -r[2]["n"])

    out += [f"## 2. Episode per simbol dan arah (fee {main} bps)", "",
            "| Simbol | Beli → jual | Episode/hari | Durasi p50 (ms) | p90 (ms) | 0 ms (satu read) | Net maks p50 (bps) "
            "| Ukuran p50 ($) | Dibuka oleh Binance | Ditutup oleh Bitget | Feed telat saat buka | Putus/akhir |",
            "|---|---|---|---|---|---|---|---|---|---|---|---|"]
    rows = rows_for(main)
    for sym, buy, a in rows:
        sell = "binance" if buy == "bitget" else "bitget"
        out.append(f"| {sym} | {buy}→{sell} | {a['n'] / days:,.1f} | {_f(median(a['dur']))} | {_f(pct(a['dur'], 0.9))} | "
                   f"{_pc(a['zero'], a['n'])} | {_f(median(a['net_max']))} | {_f(median(a['usd']), 0)} | "
                   f"{_pc(a['opener']['binance'], a['n'])} | {_pc(a['closer']['bitget'], a['n'])} | "
                   f"{_pc(a['feed']['late'], a['n'])} | "
                   f"{a['closer']['gap'] + a['closer']['end']} |")
    if not rows:
        out.append("| (tidak ada episode) | | | | | | | | | | | |")
    out += ["",
            "**Dibuka oleh** = venue yang bergerak lebih dulu. Bila sebagian besar episode dibuka oleh update Binance dan "
            "ditutup oleh update Bitget, Bitget tertinggal: leg yang menghasilkan uang adalah order ke kutipan Bitget "
            "yang belum menyesuaikan, dan siapa yang lebih cepat ke Bitget yang menang.", ""]

    out += [f"## 2b. Per koin (fee {main} bps, L = {lat[ref]:g} ms)", "",
            "| Simbol | Episode/hari | Bergantian arah | $/hari (semua) | $/hari (feed normal) |", "|---|---|---|---|---|"]
    per = {}
    for sym, buy, a in rows:
        p_ = per.setdefault(sym, [0, 0.0, 0.0])
        p_[0] += a["n"]
        p_[1] += a["cap_usd"][ref]
        p_[2] += a["cap_usd_normal"][ref]
    for sym, (n, usd, usd_n) in sorted(per.items(), key=lambda kv: -kv[1][2]):
        al = res["alt"].get(sym, {"pairs": 0, "switches": 0})
        out.append(f"| {sym} | {n / days:,.1f} | {_pc(al['switches'], al['pairs'])} | {usd / days:,.2f} | {usd_n / days:,.2f} |")
    shares = [c[3][ref] for c in res["main_caps"] if c[2] == "normal" and c[3][ref] is not None]
    ts = top_share(shares, 10)
    out += ["",
            f"Porsi 10 episode terbesar dari $/hari feed normal di L = {lat[ref]:g} ms: "
            f"{'–' if ts is None else f'{100 * ts:.0f} %'} (dari {len(shares):,} episode). Porsi besar = angka per hari "
            "bertumpu pada segelintir kejadian, bukan pola yang berulang.",
            "",
            "**Bergantian arah** = dari dua episode berurutan di koin yang sama, berapa persen arahnya berganti. Di perp, "
            "posisi yang dibuka satu episode harus ditutup: bila arah sering berganti, penutupan bisa memakai episode "
            "berikutnya (biaya ≈ satu fee level per episode); bila searah, penutupan saat harga sudah sama membayar fee "
            "lagi (biaya satu putaran ≈ dua kali fee level: pakai level 22 untuk taker penuh).", ""]

    # 2c: which episodes carry the per-day number? (review 2026-11-23: ETH $461/day vs 3 simulated positions)
    tops = (res.get("top_eps") or [[] for _ in lat])[ref]
    out += [f"## 2c. Episode terbesar (fee {main} bps, hanya feed normal, L = {lat[ref]:g} ms; maksimum {TOP_EPISODES})", "",
            "| Simbol | Beli → jual | Dibuka (UTC) | Durasi (ms) | Net saat buka (bps) | Net maks (bps) | Ukuran dibuka ($) "
            "| $ di L |", "|---|---|---|---|---|---|---|---|"]
    for e in tops[:10]:
        out.append(f"| {e['sym']} | {e['buy']}→{e['sell']} | {md.utc(e['open_ns'])} | {_f(e['dur_ms'])} | "
                   f"{e['net_open']:.1f} | {e['net_max']:.1f} | {e['usd_open']:,.0f} | {e['capture'][lat[ref]]:,.2f} |")
    if not tops:
        out.append("| (tidak ada episode feed normal) | | | | | | | |")
    tot_normal = sum(shares)
    if tops and tot_normal > 0:
        out += ["", f"Episode terbesar tunggal = ${tops[0]['capture'][lat[ref]]:,.2f} "
                f"({100 * tops[0]['capture'][lat[ref]] / tot_normal:.0f} % dari total $ feed normal di L ini). "
                "Bila beberapa baris teratas menjelaskan sebagian besar total, angka $/hari per koin di §2b adalah "
                "kejadian langka (mis. lonjakan besar), bukan pola harian.", ""]
    else:
        out.append("")

    out += [f"## 3. Yang tersisa bila order tiba L ms setelah selisih muncul (fee {main} bps, semua simbol)", "",
            "| L (ms) | Episode masih terbuka | $/hari (sekali per episode, top-of-book) | Masih terbuka (feed normal) "
            "| $/hari (feed normal) |", "|---|---|---|---|---|"]
    tot = sum(a["n"] for _, _, a in rows)
    tot_n = sum(a["feed"]["normal"] for _, _, a in rows)
    for i, L in enumerate(lat):
        alive = sum(a["cap_n"][i] for _, _, a in rows)
        usd = sum(a["cap_usd"][i] for _, _, a in rows)
        alive_n = sum(a["cap_n_normal"][i] for _, _, a in rows)
        usd_n = sum(a["cap_usd_normal"][i] for _, _, a in rows)
        out.append(f"| {L:g} | {_pc(alive, tot)} | {usd / days:,.2f} | {_pc(alive_n, tot_n)} | {usd_n / days:,.2f} |")
    out += ["",
            "Waktu tiba L = waktu kita melihat update + memutuskan + order sampai di KEDUA venue. Dari Tokyo, perjalanan "
            "jaringan saja ~7 ms ke masing-masing venue (S0), jadi baris di bawah ~10 ms tidak realistis untuk kita.", ""]

    out += [f"## 4. Kepekaan terhadap fee (L = {lat[ref]:g} ms)", "",
            "| Fee kedua leg (bps) | Episode/hari | Masih terbuka di L | $/hari di L | $/hari di L (feed normal) |",
            "|---|---|---|---|---|"]
    for level in res["levels"]:
        rs = rows_for(level)
        n = sum(a["n"] for _, _, a in rs)
        out.append(f"| {level} | {n / days:,.1f} | {_pc(sum(a['cap_n'][ref] for _, _, a in rs), n)} | "
                   f"{sum(a['cap_usd'][ref] for _, _, a in rs) / days:,.2f} | "
                   f"{sum(a['cap_usd_normal'][ref] for _, _, a in rs) / days:,.2f} |")
    out += ["", "Fee 0 = selisih kotor (batas atas teoretis). Tier dasar (dokumen resmi, dicek 2026-11-18): spot taker "
            "10 + 10 = 20; perp taker Binance 5 + Bitget 6 = 11 per silang dan 22 per putaran buka + tutup, maker 2 + 2. "
            "Diskon BNB/BGB dan VIP menurunkannya. Tier akun Anda belum diverifikasi.", ""]

    out += ["## 5. Basis: selisih mid Binance vs Bitget (sampel 1 per detik)", "",
            "| Simbol | p10 (bps) | p50 (bps) | p90 (bps) | Sampel |", "|---|---|---|---|---|"]
    for sym in sorted(res["basis"]):
        b = res["basis"][sym]
        out.append(f"| {sym} | {_f(pct(b, 0.1), 2)} | {_f(median(b), 2)} | {_f(pct(b, 0.9), 2)} | {len(b):,} |")
    out += ["", "Positif = Binance lebih mahal. Basis yang konsisten di satu sisi berarti peluangnya searah: "
            "stok habis di satu venue tanpa transfer (lihat audit A-4).", "",
            "**Batas analisis ini:** hanya level 1 (lebih banyak likuiditas di level berikut tidak dihitung); "
            "pemain lain bisa mengambil lebih dulu; fee asumsi; satu order per episode. "
            "Ini ukuran peluang, bukan ramalan pendapatan."]
    return "\n".join(out) + "\n"


def main(argv: Optional[list] = None) -> int:
    ap = argparse.ArgumentParser(description="Binance <-> Bitget spatial gaps from md_recorder files (ms resolution).")
    ap.add_argument("--data", required=True, help="recorder output directory (the one with binance-0, bitget-0, ...)")
    ap.add_argument("--last-hours", type=float, help="only the last N hours (default: everything)")
    ap.add_argument("--symbols", help="comma-separated, e.g. BTCUSDT,ETHUSDT (default: all recorded)")
    ap.add_argument("--fee-levels", default=",".join(map(str, DEFAULT_LEVELS)),
                    help="total taker fee in bps for both legs; the first is the main one (default 20,15,10,0)")
    ap.add_argument("--latency-ms", default=",".join(map(str, DEFAULT_LAT_MS)), help="arrival delays to evaluate")
    ap.add_argument("--ref-latency-ms", type=float, default=10, help="latency used in the fee table (default 10)")
    ap.add_argument("--min-usd", type=float, default=50.0, help="minimum top-of-book size in USD (default 50)")
    ap.add_argument("--feed-lag-ms", type=float, default=DEFAULT_FEED_LAG_MS,
                    help="flag an episode when a feed is this much later than its 10-minute floor (default 20)")
    a = ap.parse_args(argv)
    since = time.time_ns() - int(a.last_hours * 3600e9) if a.last_hours else None
    syms = {s.strip().upper() for s in a.symbols.split(",") if s.strip()} if a.symbols else None

    def progress(n: int, ns: int) -> None:
        print(f"… {n:,} pesan dibaca, sampai {md.utc(ns)}", file=sys.stderr, flush=True)

    res = analyse(a.data, since, syms, tuple(int(x) for x in a.fee_levels.split(",")),
                  tuple(float(x) for x in a.latency_ms.split(",")), a.min_usd, progress=progress,
                  feed_lag_ms=a.feed_lag_ms)
    sys.stdout.write(report(res, a.ref_latency_ms))
    return 0


if __name__ == "__main__":
    sys.exit(main())
