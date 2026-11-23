#!/usr/bin/env python3
"""
ArbEngine Tahap 2 — two questions the gap review (docs/GAP_REVIEW_2026-11-18.md) marked as deciding, answered from the
Binance USDⓈ-M <-> Bitget USDT-M recording (md_recorder.py --market perp). Stdlib only, never touches the network.

  nice -n 19 python3 tools/perp_study.py --data ~/md_perp > ~/perp_study.txt

G-3  COMPETITION. For each crossing episode (gross - --level-bps >= 0, both quotes >= --min-usd) the quote that did not
     move is the stale one: if the sell venue's bid jumped, the stale quote is the buy venue's ask (a competitor shows up
     as a taker BUY there at >= that ask); if the buy venue's ask dropped, the stale quote is the sell venue's bid (a
     taker SELL there at <= that bid). Per episode: was it hit, how fast, was there any taker trade on that side at all,
     and - as a baseline - was there one in the equally long window just BEFORE the episode. Hits much more often than
     the baseline, and faster than our latency, mean somebody else takes these.
     Taker side: Binance aggTrade "m" = buyer is maker, so m=true is a taker sell. Bitget trade "side" is read as the taker
     side (Bitget docs; UNVERIFIED on our data). Bitget "snapshot" trade pushes are history and are skipped.

G-1  POSITION SIMULATION, paying for the way out. Signal: gross >= --entry-bps (default 22 = taker 5 + 6 in and out).
     The order pair executes on the quotes in force --latency-ms later (no fill if the edge is gone: "missed"). Long the
     venue bought, short the venue sold, size = min(both top-of-book sizes, --max-usd). Close when the way back
     (sell the long at its bid, buy the short at its ask) costs no more than --close-bps, or after --max-hold-s, again
     executed --latency-ms later; a convergence that has reverted by then is cancelled. One position per symbol.
     Net = entry gross + exit gross - taker fees in - exit fees (--exit-fee-* for maker exits). Not modelled: queue
     position, partial fills, rate limits, funding while holding (positions last seconds to an hour), margin.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
from collections import Counter, deque
from typing import Optional

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import md_recorder as md  # noqa: E402
import pair_analysis as pa  # noqa: E402  (same reader, quote parsers and feed-lag measure)

EPS = 1e-9
HIST_NS = 60_000_000_000                 # trade history kept for the baseline window

_BN_TRADE = re.compile(r'"s":"([^"]+)","p":"([^"]+)","q":"([^"]+)".*?"m":(true|false)')


def parse_trade(venue: str, payload: str) -> Optional[tuple]:
    """-> (symbol, taker side 'buy'/'sell', price, qty) or None."""
    try:
        if venue == "binance":
            if payload.find('@aggTrade"', 11, 60) < 0 and payload.find('@trade"', 11, 60) < 0:
                return None
            m = _BN_TRADE.search(payload)
            if m:
                return m[1], "sell" if m[4] == "true" else "buy", float(m[2]), float(m[3])
            d = json.loads(payload)["data"]
            return d["s"], "sell" if d["m"] else "buy", float(d["p"]), float(d["q"])
        if '"channel":"trade"' not in payload[:160] or not payload.startswith('{"action":"update"'):
            return None
        j = json.loads(payload)
        d = j["data"][0]
        side = d["side"].lower()
        if side not in ("buy", "sell"):
            return None
        return j["arg"]["instId"].upper(), side, float(d["price"]), float(d["size"])
    except (ValueError, KeyError, TypeError, IndexError):
        return None


median = pa.median
pct = pa.pct
DAY_NS = 86_400 * 1_000_000_000


class _Sim:
    """The position simulation at ONE order latency. Several run side by side over the same stream (review of the 96 h
    run, 2026-11-23: L = 10 ms is optimistic from Tokyo, so the result must be shown at larger L too). They share the quote
    store but nothing else: each has its own positions, pending orders and trades."""

    def __init__(self, latency_ms: float, quotes: dict, feed_normal, entry_bps: float, close_bps: float,
                 max_hold_s: float, max_usd: float, min_usd: float, fee_in: dict, fee_out: dict, normal_only: bool):
        self.latency_ms, self.lat_ns = latency_ms, int(latency_ms * 1_000_000)
        self.quotes, self.feed_normal = quotes, feed_normal
        self.entry_bps, self.close_bps, self.max_hold_s = entry_bps, close_bps, max_hold_s
        self.max_usd, self.min_usd, self.normal_only = max_usd, min_usd, normal_only
        self.fee_in, self.fee_out = fee_in, fee_out
        self.trades: list = []
        self.missed: Counter = Counter()
        self.pos: dict = {}                # sym -> open simulated position
        self.pending: dict = {}            # sym -> (exec_ns, kind, arg)

    def record(self, sym: str, p: dict, ns: int, exit_bps: float, reason: str) -> None:
        fi, fo = self.fee_in, self.fee_out
        net = p["entry"] + exit_bps - fi[p["long"]] - fi[p["short"]] - fo[p["long"]] - fo[p["short"]]
        self.trades.append({"sym": sym, "long": p["long"], "short": p["short"], "open_ns": p["open"],
                            "close_ns": ns, "entry_bps": p["entry"], "exit_bps": exit_bps, "net_bps": net,
                            "notional": p["notional"], "usd": net / 1e4 * p["notional"], "reason": reason})

    def exit_bps_now(self, sym: str, p: dict) -> Optional[float]:
        q = self.quotes.get(sym, {})
        ql, qs = q.get(p["long"]), q.get(p["short"])
        return None if not (ql and qs) else (ql[0] / qs[2] - 1) * 1e4

    def execute(self, sym: str, exec_ns: int) -> None:
        _, kind, arg = self.pending.pop(sym)
        q = self.quotes.get(sym, {})
        if kind == "open":
            buy, sell = arg
            qb, qs = q.get(buy), q.get(sell)
            gross = None if not (qb and qs) else (qs[0] / qb[2] - 1) * 1e4
            if gross is None or gross < self.entry_bps:
                self.missed[sym] += 1
                return
            coins = min(qb[3], qs[1], self.max_usd / qb[2])
            self.pos[sym] = {"long": buy, "short": sell, "entry": gross, "open": exec_ns, "notional": coins * qb[2]}
        else:
            p = self.pos[sym]
            x = self.exit_bps_now(sym, p)
            if x is None or (arg == "converged" and x < -self.close_bps):
                return                             # quotes unknown or the convergence reverted: keep the position
            del self.pos[sym]
            self.record(sym, p, exec_ns, x, arg)

    def run_pending(self, ns: int) -> None:
        for sym in [s for s, (t, _, _) in self.pending.items() if t <= ns]:
            self.execute(sym, self.pending[sym][0])

    def signal(self, sym: str, ns: int) -> None:
        if sym in self.pending:
            return
        q = self.quotes.get(sym, {})
        if sym in self.pos:
            p = self.pos[sym]
            x = self.exit_bps_now(sym, p)
            if x is not None and x >= -self.close_bps:
                self.pending[sym] = (ns + self.lat_ns, "close", "converged")
            elif ns - p["open"] >= self.max_hold_s * 1e9:
                self.pending[sym] = (ns + self.lat_ns, "close", "timeout")
            return
        for buy, sell in pa.DIRECTIONS:
            qb, qs = q.get(buy), q.get(sell)
            if not (qb and qs) or min(qb[3], qs[1]) * qb[2] < self.min_usd:
                continue
            if (qs[0] / qb[2] - 1) * 1e4 >= self.entry_bps and (not self.normal_only or self.feed_normal(ns)):
                self.pending[sym] = (ns + self.lat_ns, "open", (buy, sell))
                return

    def finish(self, end: int) -> None:
        self.run_pending(end)
        for sym, (_, kind, _) in list(self.pending.items()):
            if kind == "close" and sym in self.pos:
                self.pending.pop(sym)
        for sym, p in list(self.pos.items()):
            x = self.exit_bps_now(sym, p)
            if x is not None:
                self.record(sym, p, end, x, "end")
            del self.pos[sym]


def concentration(trades: list) -> dict:
    """How much of the total the few biggest positions and the top coin carry. The go criterion (gap review) is a top-10
    share below 30 %. Losing positions stay in the total, so the share can exceed 100 % (shown as is, not clipped)."""
    usd = [t["usd"] for t in trades]
    total = sum(usd)
    out = {"n": len(trades), "total": total, "top10_share": pa.top_share(usd, 10), "n_for_half": None,
           "top_sym": None, "top_sym_share": None, "top3_share": None, "ex_top_usd": None}
    if not trades or total <= 0:
        return out
    run = 0.0
    for i, v in enumerate(sorted(usd, reverse=True), 1):
        run += v
        if run >= total / 2:
            out["n_for_half"] = i
            break
    per: Counter = Counter()
    for t in trades:
        per[t["sym"]] += t["usd"]
    ranked = sorted(per.items(), key=lambda kv: -kv[1])
    out.update(top_sym=ranked[0][0], top_sym_share=ranked[0][1] / total,
               top3_share=sum(v for _, v in ranked[:3]) / total, ex_top_usd=total - ranked[0][1])
    return out


def daily(trades: list, first_ns: Optional[int], last_ns: Optional[int]) -> list:
    """Per UTC day (of the opening): positions, $, $ normalised to 24 h of data, top coin. Days with no positions are
    listed too: a quiet day is information, and a result that lives on one day is not a pattern."""
    if first_ns is None or last_ns is None:
        return []
    rows = []
    for d in range(first_ns // DAY_NS, last_ns // DAY_NS + 1):
        hours = (min(last_ns, (d + 1) * DAY_NS) - max(first_ns, d * DAY_NS)) / 3.6e12
        ts = [t for t in trades if t["open_ns"] // DAY_NS == d]
        usd = sum(t["usd"] for t in ts)
        per: Counter = Counter()
        for t in ts:
            per[t["sym"]] += t["usd"]
        rows.append({"day": time.strftime("%Y-%m-%d", time.gmtime(d * 86400)), "hours": hours, "n": len(ts), "usd": usd,
                     "usd_per_day": usd * 24 / hours if hours > 0 else 0.0,
                     "top_sym": max(per, key=per.get) if per else None})
    return rows


def analyse(root: str, since_ns: Optional[int] = None, symbols: Optional[set] = None, level_bps: float = 11.0,
            min_usd: float = 50.0, latency_ms: float = 10.0, entry_bps: float = 22.0, close_bps: float = 2.0,
            max_hold_s: float = 3600.0, max_usd: float = 1000.0, fee_bn: float = 5.0, fee_bg: float = 6.0,
            exit_fee_bn: Optional[float] = None, exit_fee_bg: Optional[float] = None, normal_only: bool = True,
            feed_lag_ms: float = pa.DEFAULT_FEED_LAG_MS, progress=None, extra_latency_ms: tuple = ()) -> dict:
    lat_ns = int(latency_ms * 1_000_000)
    fee_in = {"binance": fee_bn, "bitget": fee_bg}
    fee_out = {"binance": fee_bn if exit_fee_bn is None else exit_fee_bn,
               "bitget": fee_bg if exit_fee_bg is None else exit_fee_bg}
    res: dict = {"first_ns": None, "last_ns": None, "trade_msgs": Counter(), "comp": {}, "trades": [],
                 "missed": Counter(), "sens": [], "params": dict(level_bps=level_bps, min_usd=min_usd,
                 latency_ms=latency_ms, entry_bps=entry_bps, close_bps=close_bps, max_hold_s=max_hold_s,
                 max_usd=max_usd, fee_in=fee_in, fee_out=fee_out, normal_only=normal_only, feed_lag_ms=feed_lag_ms)}
    feed = pa.FeedLag()
    quotes: dict = {}
    conn_syms: dict = {}
    eps: dict = {}                         # (sym, buy) -> open competition episode
    hist: dict = {}                        # (venue, sym, side) -> deque of (ns, price)

    def feed_normal(ns: int) -> bool:
        ex = [feed.excess_ms(v, ns) for v in pa.VENUES]
        return all(x is not None and x <= feed_lag_ms for x in ex)

    def comp(sym: str) -> dict:
        return res["comp"].setdefault(sym, {"episodes": 0, "zero": 0, "hit": 0, "hit_fast": 0, "t_hit": [],
                                            "same_side": 0, "pre_same_side": 0})

    # ── competition episodes ──────────────────────────────────────────────────────────────────────────────────────
    def close_episode(key: tuple, ns: int) -> None:
        e = eps.pop(key)
        c = comp(key[0])
        dur = ns - e["open"]
        if dur == 0:
            c["zero"] += 1
            return
        c["episodes"] += 1
        if e["hit"] is not None:
            c["hit"] += 1
            c["t_hit"].append((e["hit"] - e["open"]) / 1e6)
            c["hit_fast"] += e["hit"] - e["open"] <= lat_ns
        c["same_side"] += e["same"]
        h = hist.get((e["venue"], key[0], e["side"]), ())
        c["pre_same_side"] += any(e["open"] - dur <= t < e["open"] for t, _ in h)

    def episodes_update(sym: str, ns: int, mover: str) -> None:
        q = quotes.get(sym, {})
        for buy, sell in pa.DIRECTIONS:
            qb, qs = q.get(buy), q.get(sell)
            ok = False
            if qb and qs:
                gross = (qs[0] / qb[2] - 1) * 1e4
                ok = gross - level_bps >= -EPS and min(qb[3], qs[1]) * qb[2] >= min_usd
            key = (sym, buy)
            if ok and key not in eps and mover in pa.VENUES:
                if mover == sell:                  # the sell venue's bid moved: buy venue's ask is stale
                    eps[key] = {"open": ns, "venue": buy, "side": "buy", "price": qb[2], "hit": None, "same": False}
                else:                              # the buy venue's ask moved: sell venue's bid is stale
                    eps[key] = {"open": ns, "venue": sell, "side": "sell", "price": qs[0], "hit": None, "same": False}
            elif not ok and key in eps:
                close_episode(key, ns)

    def on_trade(venue: str, ns: int, sym: str, side: str, price: float) -> None:
        h = hist.setdefault((venue, sym, side), deque())
        h.append((ns, price))
        while h and h[0][0] < ns - HIST_NS:
            h.popleft()
        for key, e in eps.items():
            if key[0] != sym or e["venue"] != venue or e["side"] != side:
                continue
            e["same"] = True
            tol = abs(e["price"]) * 1e-9
            if e["hit"] is None and (price >= e["price"] - tol if side == "buy" else price <= e["price"] + tol):
                e["hit"] = ns

    # ── simulation: one _Sim per latency, the first is the main one ───────────────────────────────────────────────────
    lats = [latency_ms] + [x for x in extra_latency_ms if x != latency_ms]
    sims = [_Sim(x, quotes, feed_normal, entry_bps, close_bps, max_hold_s, max_usd, min_usd, fee_in, fee_out, normal_only)
            for x in lats]

    # ── the stream ────────────────────────────────────────────────────────────────────────────────────────────────
    n = 0
    for ns, conn, payload in pa.events(root, since_ns):
        n += 1
        if progress and n % 5_000_000 == 0:
            progress(n, ns)
        for sm in sims:
            sm.run_pending(ns)
        venue = conn.split("-")[0]
        if payload.startswith("#"):
            if '"ev":"disconnect"' in payload or '"ev":"stop"' in payload or '"ev":"start"' in payload:
                for sym in conn_syms.get(conn, ()):
                    qv = quotes.get(sym, {})
                    if venue in qv and qv[venue][4] == conn:
                        del qv[venue]
                        episodes_update(sym, ns, "gap")
            continue
        p = None
        if venue == "binance":
            if not payload.startswith('{"stream":"'):
                continue
            if payload.find('@bookTicker"', 11, 60) >= 0:
                m = pa._BN_E.search(payload, 0, 120)
                if m:
                    feed.add("binance", ns, int(m[1]))
                p = pa.parse_binance(payload)
            else:
                t = parse_trade("binance", payload)
                m = pa._BN_E.search(payload, 0, 200)
                if m:
                    feed.add("binance", ns, int(m[1]))
                if t and (not symbols or t[0] in symbols):
                    res["trade_msgs"]["binance"] += 1
                    on_trade("binance", ns, t[0], t[1], t[2])
                continue
        else:
            if payload.find('"channel":"books1"', 0, 120) >= 0:
                m = pa._BG_TS.search(payload)
                if m:
                    feed.add("bitget", ns, int(m[1]))
                p = pa.parse_bitget(payload)
            else:
                t = parse_trade("bitget", payload)
                if t and (not symbols or t[0] in symbols):
                    res["trade_msgs"]["bitget"] += 1
                    on_trade("bitget", ns, t[0], t[1], t[2])
                continue
        if p is None or (symbols and p[0] not in symbols):
            continue
        sym = p[0]
        res["first_ns"] = ns if res["first_ns"] is None else res["first_ns"]
        res["last_ns"] = ns
        quotes.setdefault(sym, {})[venue] = (p[1], p[2], p[3], p[4], conn)
        conn_syms.setdefault(conn, set()).add(sym)
        episodes_update(sym, ns, venue)
        for sm in sims:
            sm.signal(sym, ns)
    end = res["last_ns"] or 0
    for sm in sims:
        sm.finish(end)
    for key in list(eps):
        close_episode(key, end)
    res["trades"], res["missed"] = sims[0].trades, sims[0].missed
    res["sens"] = [{"latency_ms": sm.latency_ms, "trades": sm.trades, "missed": sm.missed} for sm in sims]
    return res


# ── report ─────────────────────────────────────────────────────────────────────────────────────────────────────────
def _f(x: Optional[float], d: int = 1) -> str:
    return "–" if x is None else f"{x:,.{d}f}"


def _pc(a: int, b: int) -> str:
    return "–" if not b else f"{100 * a / b:.0f} %"


def report(res: dict) -> str:
    out = ["# Perp Binance ↔ Bitget — bukti pesaing (G-3) dan simulasi posisi (G-1)", ""]
    if res["first_ns"] is None:
        out.append("Tidak ada data kutipan di rentang ini (cek --data: folder rekaman `--market perp`).")
        return "\n".join(out) + "\n"
    P = res["params"]
    days = max((res["last_ns"] - res["first_ns"]) / 86_400e9, 1e-9)
    out += [f"{md.utc(res['first_ns'])} → {md.utc(res['last_ns'])} ({days * 24:.1f} jam).", "",
            "## 1. Data dan parameter", "",
            f"- Trade dibaca: Binance {res['trade_msgs']['binance']:,}, Bitget {res['trade_msgs']['bitget']:,}. Sisi taker "
            "Bitget dibaca dari field `side` (belum diverifikasi pada data kita).",
            f"- Episode pesaing: silang ≥ {P['level_bps']:g} bps setelah fee satu kali silang, top-of-book ≥ ${P['min_usd']:,.0f}.",
            f"- Simulasi: masuk bila gross ≥ {P['entry_bps']:g} bps; order tiba {P['latency_ms']:g} ms kemudian; tutup bila "
            f"jalan balik ≤ {P['close_bps']:g} bps atau setelah {P['max_hold_s']:g} s; ukuran ≤ ${P['max_usd']:,.0f}; fee masuk "
            f"Binance {P['fee_in']['binance']:g} / Bitget {P['fee_in']['bitget']:g} bps, keluar {P['fee_out']['binance']:g} / "
            f"{P['fee_out']['bitget']:g} bps; " + ("hanya saat feed normal." if P["normal_only"] else "semua feed."), ""]

    out += ["## 2. Bukti pesaing (G-3)", "",
            "| Simbol | Episode | 0 ms (dibuang) | Kutipan basi diambil | Diambil ≤ L ms | Waktu sampai diambil p50 (ms) | "
            "Trade sisi itu selama episode | Trade sisi itu, jendela sebelum (baseline) |",
            "|---|---|---|---|---|---|---|---|"]
    tot = Counter()
    for sym, c in sorted(res["comp"].items(), key=lambda kv: -kv[1]["episodes"]):
        for k in ("episodes", "zero", "hit", "hit_fast", "same_side", "pre_same_side"):
            tot[k] += c[k]
        out.append(f"| {sym} | {c['episodes']:,} | {c['zero']:,} | {_pc(c['hit'], c['episodes'])} | "
                   f"{_pc(c['hit_fast'], c['episodes'])} | {_f(median(c['t_hit']))} | {_pc(c['same_side'], c['episodes'])} | "
                   f"{_pc(c['pre_same_side'], c['episodes'])} |")
    out += [f"| **semua** | {tot['episodes']:,} | {tot['zero']:,} | {_pc(tot['hit'], tot['episodes'])} | "
            f"{_pc(tot['hit_fast'], tot['episodes'])} | | {_pc(tot['same_side'], tot['episodes'])} | "
            f"{_pc(tot['pre_same_side'], tot['episodes'])} |", "",
            "Cara membaca: **diambil** = trade taker di venue yang tertinggal, di sisi dan harga kutipan basi, selama episode. "
            "Bila jauh lebih sering daripada baseline (trade di sisi itu pada jendela sama panjang sebelum episode) dan banyak "
            f"yang ≤ {P['latency_ms']:g} ms, ada pemain lain yang lebih cepat dan peluang ini bukan untuk kita.", ""]

    out += ["## 3. Simulasi posisi termasuk penutupan (G-1)", "",
            "| Simbol | Posisi | Terlewat | Tahan p50 (s) | Masuk rata2 (bps) | Keluar rata2 (bps) | Net rata2 (bps) | $ total | "
            "$/hari | Konvergen / timeout / akhir |", "|---|---|---|---|---|---|---|---|---|---|"]
    by: dict = {}
    for t in res["trades"]:
        by.setdefault(t["sym"], []).append(t)
    syms = sorted(set(by) | set(res["missed"]), key=lambda s: -sum(t["usd"] for t in by.get(s, [])))
    all_t = res["trades"]
    for sym in syms + ["**semua**"]:
        ts = all_t if sym == "**semua**" else by.get(sym, [])
        miss = sum(res["missed"].values()) if sym == "**semua**" else res["missed"][sym]
        n = len(ts)
        r = Counter(t["reason"] for t in ts)
        usd = sum(t["usd"] for t in ts)
        out.append(f"| {sym} | {n:,} | {miss:,} | {_f(median([(t['close_ns'] - t['open_ns']) / 1e9 for t in ts]), 2)} | "
                   f"{_f(sum(t['entry_bps'] for t in ts) / n if n else None)} | "
                   f"{_f(sum(t['exit_bps'] for t in ts) / n if n else None)} | "
                   f"{_f(sum(t['net_bps'] for t in ts) / n if n else None)} | {usd:,.2f} | {usd / days:,.2f} | "
                   f"{r['converged']} / {r['timeout']} / {r['end']} |")
    out += ["",
            "Net sudah memotong fee masuk DAN keluar. Optimistis: tidak ada pesaing, order terisi penuh di kutipan, tanpa "
            "rate limit. Pesimistis: hanya level 1, satu posisi per koin. Belum termasuk funding selama posisi terbuka dan "
            "kebutuhan margin. Bandingkan dengan §2: bila kutipan basi biasanya sudah diambil sebelum L ms, simulasi ini "
            "terlalu optimistis.", ""]

    # ── 3b-3e: is the number a pattern or a few events? (review 2026-11-23, go criteria in the gap review) ─────────────
    out += ["## 3b. Rincian per hari (UTC, hari posisi dibuka)", "",
            "| Hari | Jam data | Posisi | $ | $/hari (dinormalkan 24 jam) | Koin teratas |", "|---|---|---|---|---|---|"]
    for r in daily(all_t, res["first_ns"], res["last_ns"]):
        part = " (sebagian)" if r["hours"] < 20 else ""
        out.append(f"| {r['day']}{part} | {r['hours']:.1f} | {r['n']:,} | {r['usd']:,.2f} | {r['usd_per_day']:,.2f} | "
                   f"{r['top_sym'] or '–'} |")
    out += ["", "Hasil yang hanya muncul pada satu atau dua hari bukan pola yang berulang. Hari sebagian dinormalkan ke 24 jam "
            "(sampel kecil, kasar).", ""]

    c = concentration(all_t)
    out += ["## 3c. Konsentrasi (kriteria go: 10 posisi terbesar < 30 % dari total)", ""]
    if not c["n"] or c["top10_share"] is None:
        out += ["Tidak ada total positif untuk dibagi.", ""]
    else:
        out += [f"- Porsi 10 posisi terbesar: **{100 * c['top10_share']:.0f} %** dari total ${c['total']:,.2f} ({c['n']:,} posisi; "
                "di atas 100 % bila posisi rugi menahan total).",
                f"- Posisi yang dibutuhkan untuk separuh total: {c['n_for_half']:,} dari {c['n']:,}.",
                f"- Koin teratas {c['top_sym']}: {100 * c['top_sym_share']:.0f} % dari total; tiga koin teratas "
                f"{100 * c['top3_share']:.0f} %.",
                f"- Tanpa {c['top_sym']}: ${c['ex_top_usd']:,.2f} total = ${c['ex_top_usd'] / days:,.2f}/hari.", ""]

    sens = res.get("sens") or [{"latency_ms": P["latency_ms"], "trades": all_t, "missed": res["missed"]}]
    out += ["## 3d. Sensitivitas latensi (satu putaran data; baris pertama = L utama)", "",
            "Latensi order dari Tokyo belum terukur (G-4); jaringan saja ±7 ms per venue, jadi L = 10 ms optimistis.", "",
            "| L (ms) | Posisi | Terlewat | $ total | $/hari | Porsi 10 terbesar |", "|---|---|---|---|---|---|"]
    for s in sens:
        ts = s["trades"]
        usd = sum(t["usd"] for t in ts)
        sh = concentration(ts)["top10_share"]
        out.append(f"| {s['latency_ms']:g} | {len(ts):,} | {sum(s['missed'].values()):,} | {usd:,.2f} | {usd / days:,.2f} | "
                   f"{'–' if sh is None else f'{100 * sh:.0f} %'} |")
    out += [""]

    out += ["## 3e. Posisi terbesar (L utama)", "",
            "| Simbol | Dibuka (UTC) | Ukuran $ | Masuk (bps) | Keluar (bps) | Net (bps) | $ | Tahan (s) |",
            "|---|---|---|---|---|---|---|---|"]
    for t in sorted(all_t, key=lambda x: -x["usd"])[:10]:
        out.append(f"| {t['sym']} | {md.utc(t['open_ns'])} | {t['notional']:,.0f} | {t['entry_bps']:.1f} | "
                   f"{t['exit_bps']:.1f} | {t['net_bps']:.1f} | {t['usd']:,.2f} | {(t['close_ns'] - t['open_ns']) / 1e9:,.2f} |")
    if not all_t:
        out.append("| – | – | – | – | – | – | – | – |")
    out += [""]
    return "\n".join(out) + "\n"


def _ms_list(s: str) -> list:
    try:
        v = [float(x) for x in s.split(",") if x.strip()]
    except ValueError:
        raise argparse.ArgumentTypeError(f"not a comma separated list of milliseconds: {s!r}")
    if any(x <= 0 for x in v):
        raise argparse.ArgumentTypeError("latencies must be > 0 ms")
    return v


def main(argv: Optional[list] = None) -> int:
    ap = argparse.ArgumentParser(description="Perp competition evidence and position simulation (Binance <-> Bitget).")
    ap.add_argument("--data", required=True, help="perp recording directory")
    ap.add_argument("--last-hours", type=float)
    ap.add_argument("--symbols")
    ap.add_argument("--level-bps", type=float, default=11.0, help="crossing threshold for competition episodes (default 11)")
    ap.add_argument("--min-usd", type=float, default=50.0)
    ap.add_argument("--latency-ms", type=float, default=10.0, help="our order arrival delay (default 10)")
    ap.add_argument("--entry-bps", type=float, default=22.0, help="gross needed to open (default 22 = taker in and out)")
    ap.add_argument("--close-bps", type=float, default=2.0, help="close when the way back costs <= this (default 2)")
    ap.add_argument("--max-hold-s", type=float, default=3600.0)
    ap.add_argument("--max-usd", type=float, default=1000.0, help="position size cap per leg (default 1000)")
    ap.add_argument("--fee-bn", type=float, default=5.0, help="Binance taker bps (default 5, base tier)")
    ap.add_argument("--fee-bg", type=float, default=6.0, help="Bitget taker bps (default 6, base tier)")
    ap.add_argument("--exit-fee-bn", type=float, help="Binance exit fee bps (default = taker; 2 = maker exit)")
    ap.add_argument("--exit-fee-bg", type=float, help="Bitget exit fee bps (default = taker; 2 = maker exit)")
    ap.add_argument("--all-feeds", action="store_true", help="also open while a feed is late or unknown")
    ap.add_argument("--feed-lag-ms", type=float, default=pa.DEFAULT_FEED_LAG_MS)
    ap.add_argument("--also-latency-ms", type=_ms_list, default=[25.0, 50.0],
                    help="more order latencies simulated in the same pass, comma separated (default 25,50)")
    a = ap.parse_args(argv)
    since = time.time_ns() - int(a.last_hours * 3600e9) if a.last_hours else None
    syms = {s.strip().upper() for s in a.symbols.split(",") if s.strip()} if a.symbols else None
    def progress(n: int, ns: int) -> None:
        print(f"… {n:,} pesan dibaca, sampai {md.utc(ns)}", file=sys.stderr, flush=True)

    res = analyse(a.data, since, syms, a.level_bps, a.min_usd, a.latency_ms, a.entry_bps, a.close_bps, a.max_hold_s,
                  a.max_usd, a.fee_bn, a.fee_bg, a.exit_fee_bn, a.exit_fee_bg, not a.all_feeds, a.feed_lag_ms,
                  progress=progress, extra_latency_ms=tuple(a.also_latency_ms))
    sys.stdout.write(report(res))
    return 0


if __name__ == "__main__":
    sys.exit(main())
