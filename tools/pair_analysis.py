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
import json
import os
import re
import sys
import time
from collections import Counter
from typing import Callable, Iterator, Optional

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import md_recorder as md  # noqa: E402  (same file format, same reader)

VENUES = ("binance", "bitget")
DIRECTIONS = (("bitget", "binance"), ("binance", "bitget"))     # (buy at, sell at)
DEFAULT_LEVELS = (20, 15, 10, 0)
DEFAULT_LAT_MS = (1, 5, 10, 25, 50, 100, 250)
BASIS_EVERY_NS = 1_000_000_000
EPS = 1e-9

_BN = re.compile(r'"s":"([^"]+)","b":"([^"]+)","B":"([^"]+)","a":"([^"]+)","A":"([^"]+)"')
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


# ── one episode ────────────────────────────────────────────────────────────────────────────────────────────────────
class Episode:
    __slots__ = ("open_ns", "net", "usd", "net_open", "usd_open", "net_max", "opener", "lat_ns", "cap", "k")

    def __init__(self, ns: int, net: float, usd: float, opener: str, lat_ns: tuple):
        self.open_ns, self.net, self.usd, self.opener, self.lat_ns = ns, net, usd, opener, lat_ns
        self.net_open, self.usd_open, self.net_max = net, usd, net
        self.cap: list = [None] * len(lat_ns)
        self.k = 0                                          # latencies already decided (lat_ns is ascending)

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
            "cap_usd": None, "cap_n": None}


# ── the analysis ───────────────────────────────────────────────────────────────────────────────────────────────────
def analyse(root: str, since_ns: Optional[int] = None, symbols: Optional[set] = None,
            fee_levels: tuple = DEFAULT_LEVELS, latencies_ms: tuple = DEFAULT_LAT_MS, min_usd: float = 50.0,
            keep_episodes: bool = False, progress: Optional[Callable[[int, int], None]] = None) -> dict:
    lat_ms = tuple(sorted(latencies_ms))
    lat_ns = tuple(int(x * 1_000_000) for x in lat_ms)
    levels = tuple(fee_levels)
    res: dict = {"first_ns": None, "last_ns": None, "msgs": Counter(), "gaps": Counter(), "levels": levels,
                 "lat_ms": lat_ms, "min_usd": min_usd, "agg": {}, "basis": {}, "symbols": set(),
                 "episodes": [] if keep_episodes else None}
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
        a["n"] += 1
        a["zero"] += dur == 0
        a["dur"].append(dur)
        a["net_max"].append(ep.net_max)
        a["usd"].append(ep.usd_open)
        a["opener"][ep.opener] += 1
        a["closer"][closer] += 1
        for i, v in enumerate(ep.cap):
            if v is not None:
                a["cap_usd"][i] += v
                a["cap_n"][i] += 1
        if res["episodes"] is not None:
            res["episodes"].append({"sym": sym, "buy": buy, "sell": sell, "level": level, "open_ns": ep.open_ns,
                                    "dur_ms": dur, "net_open": ep.net_open, "net_max": ep.net_max,
                                    "usd_open": ep.usd_open, "opener": ep.opener, "closer": closer,
                                    "capture": dict(zip(lat_ms, ep.cap))})

    def evaluate(sym: str, ns: int, mover: str) -> None:
        q = quotes.get(sym, {})
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
                        open_eps[key] = Episode(ns, gross - level, usd, mover, lat_ns)
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
            if not payload.startswith('{"stream":"') or payload.find('@bookTicker"', 11, 60) < 0:
                continue
            p = parse_binance(payload)
        else:
            if not payload.startswith('{"action"') or payload.find('"channel":"books1"', 0, 120) < 0:
                continue
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
            f"- Simbol: {', '.join(sorted(res['symbols']))}.", ""]

    def rows_for(level: int):
        return sorted(((k[1], k[2], a) for k, a in res["agg"].items() if k[0] == level), key=lambda r: -r[2]["n"])

    out += [f"## 2. Episode per simbol dan arah (fee {main} bps)", "",
            "| Simbol | Beli → jual | Episode/hari | Durasi p50 (ms) | p90 (ms) | 0 ms (satu read) | Net maks p50 (bps) "
            "| Ukuran p50 ($) | Dibuka oleh Binance | Ditutup oleh Bitget | Putus/akhir |",
            "|---|---|---|---|---|---|---|---|---|---|---|"]
    rows = rows_for(main)
    for sym, buy, a in rows:
        sell = "binance" if buy == "bitget" else "bitget"
        out.append(f"| {sym} | {buy}→{sell} | {a['n'] / days:,.1f} | {_f(median(a['dur']))} | {_f(pct(a['dur'], 0.9))} | "
                   f"{_pc(a['zero'], a['n'])} | {_f(median(a['net_max']))} | {_f(median(a['usd']), 0)} | "
                   f"{_pc(a['opener']['binance'], a['n'])} | {_pc(a['closer']['bitget'], a['n'])} | "
                   f"{a['closer']['gap'] + a['closer']['end']} |")
    if not rows:
        out.append("| (tidak ada episode) | | | | | | | | | | |")
    out += ["",
            "**Dibuka oleh** = venue yang bergerak lebih dulu. Bila sebagian besar episode dibuka oleh update Binance dan "
            "ditutup oleh update Bitget, Bitget tertinggal: leg yang menghasilkan uang adalah order ke kutipan Bitget "
            "yang belum menyesuaikan, dan siapa yang lebih cepat ke Bitget yang menang.", ""]

    out += [f"## 3. Yang tersisa bila order tiba L ms setelah selisih muncul (fee {main} bps, semua simbol)", "",
            "| L (ms) | Episode masih terbuka | $/hari (sekali per episode, top-of-book) |", "|---|---|---|"]
    tot = sum(a["n"] for _, _, a in rows)
    for i, L in enumerate(lat):
        alive = sum(a["cap_n"][i] for _, _, a in rows)
        usd = sum(a["cap_usd"][i] for _, _, a in rows)
        out.append(f"| {L:g} | {_pc(alive, tot)} | {usd / days:,.2f} |")
    out += ["",
            "Waktu tiba L = waktu kita melihat update + memutuskan + order sampai di KEDUA venue. Dari Tokyo, perjalanan "
            "jaringan saja ~7 ms ke masing-masing venue (S0), jadi baris di bawah ~10 ms tidak realistis untuk kita.", ""]

    out += [f"## 4. Kepekaan terhadap fee (L = {lat[ref]:g} ms)", "",
            "| Fee kedua leg (bps) | Episode/hari | Masih terbuka di L | $/hari di L |", "|---|---|---|---|"]
    for level in res["levels"]:
        rs = rows_for(level)
        n = sum(a["n"] for _, _, a in rs)
        out.append(f"| {level} | {n / days:,.1f} | {_pc(sum(a['cap_n'][ref] for _, _, a in rs), n)} | "
                   f"{sum(a['cap_usd'][ref] for _, _, a in rs) / days:,.2f} |")
    out += ["", "Fee 0 = selisih kotor (batas atas teoretis). Fee tier dasar taker 10 + 10 = 20; diskon (BNB/BGB, VIP, "
            "maker) menurunkannya. Tier Anda belum diverifikasi.", ""]

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
    a = ap.parse_args(argv)
    since = time.time_ns() - int(a.last_hours * 3600e9) if a.last_hours else None
    syms = {s.strip().upper() for s in a.symbols.split(",") if s.strip()} if a.symbols else None

    def progress(n: int, ns: int) -> None:
        print(f"… {n:,} pesan dibaca, sampai {md.utc(ns)}", file=sys.stderr, flush=True)

    res = analyse(a.data, since, syms, tuple(int(x) for x in a.fee_levels.split(",")),
                  tuple(float(x) for x in a.latency_ms.split(",")), a.min_usd, progress=progress)
    sys.stdout.write(report(res, a.ref_latency_ms))
    return 0


if __name__ == "__main__":
    sys.exit(main())
