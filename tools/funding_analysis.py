#!/usr/bin/env python3
"""
ArbEngine Tahap 2 — funding-rate difference between Binance USDⓈ-M and Bitget USDT-M, from the perp recording.
Stdlib only, reads files written by `md_recorder.py --market perp`, never touches the network.

  nice -n 19 python3 tools/funding_analysis.py --data ~/md_perp > ~/funding_report.txt

The trade it measures: hold the same notional SHORT on the venue that pays the higher funding and LONG on the other.
Prices cancel (apart from the basis between the two marks, §3 of the report); what remains is the funding difference.
Shorts receive funding when the rate is positive, so short Binance / long Bitget earns (rate_binance - rate_bitget).

Inputs
  Binance  <sym>@markPrice@1s   "r" = funding rate of the running period, "T" = its funding time, "p" = mark price
  Bitget   ticker (USDT-FUTURES) "fundingRate", "nextFundingTime", "markPrice"
Settlement: the funding time T of a venue/symbol rolls to a later time once T has passed (on md-tokyo 0.4-1.1 s after
T; observations at T+0.07..0.4 s still carry the old T). A roll within 5 minutes after T settles the period at the last
rate observed BEFORE T (no more than 10 minutes before). A roll before T (the venue moved its schedule) or long after T
(stuck label) is not a settlement. Period length = time since the previous settlement, or
the next period's length for the first one.

Per hour H in which both venues' funding is known: spread(H) = rate_bn/len_bn - rate_bg/len_bg (bps per hour).
  hold    direction from the predicted rates at the first hour (latest observation at or before H, at most 1 h old),
          kept to the end; cost = one round trip (open + close, both venues).
  flip    same start; switches side when the prediction at the start of an hour points the other way by more than
          --hysteresis-bps per 8 h; every switch costs one more round trip.
  best fixed side (hindsight)  |sum of spreads| minus one round trip: an upper bound, it knows the future.
All results are bps of the notional of ONE leg. Fees default to 22 bps per round trip (taker 5 + 6 bps, in and out,
base tiers, UNVERIFIED; --roundtrip-bps).
"""
from __future__ import annotations

import argparse
import bisect
import json
import os
import re
import sys
import time
from collections import Counter
from typing import Optional

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import md_recorder as md  # noqa: E402  (same file format, same reader)

H_MS = 3_600_000
SAMPLE_MS = 60_000                 # keep one observation per venue/symbol per minute
SETTLE_FRESH_MS = 600_000          # the last observation before a funding time must be this recent to count
ROLL_GRACE_MS = 300_000            # venues switch to the next funding time ~0.4-1.1 s after it (md-tokyo); allow 5 min
PRED_FRESH_MS = H_MS               # a prediction older than this is no prediction
DEFAULT_RT_BPS = 22.0
DEFAULT_HYST_BPS = 0.5

_BN_R = re.compile(r'"s":"([^"]+)","p":"([^"]+)".*?"r":"([^"]*)","T":(\d+)')
_BG_ID = re.compile(r'"instId":"([^"]+)"')
_BG_FR = re.compile(r'"fundingRate":"([^"]*)"')
_BG_NX = re.compile(r'"nextFundingTime":"(\d+)"')
_BG_MK = re.compile(r'"markPrice":"([^"]+)"')


def parse_binance_mark(payload: str) -> Optional[tuple]:
    """markPriceUpdate -> (symbol, rate_bps, next_funding_ms, mark) or None."""
    if '"e":"markPriceUpdate"' not in payload:
        return None
    m = _BN_R.search(payload)
    try:
        if m:
            sym, mark, r, nxt = m[1], float(m[2]), m[3], int(m[4])
        else:
            d = json.loads(payload)["data"]
            sym, mark, r, nxt = d["s"], float(d["p"]), d["r"], int(d["T"])
        if r == "":
            return None
        return sym, round(float(r) * 1e4, 10), nxt, mark
    except (ValueError, KeyError, TypeError):
        return None


def parse_bitget_ticker(payload: str) -> Optional[tuple]:
    """USDT-FUTURES ticker -> (symbol, rate_bps, next_funding_ms, mark) or None."""
    if '"channel":"ticker"' not in payload[:160] or '"USDT-FUTURES"' not in payload[:160]:
        return None
    i, fr, nx, mk = _BG_ID.search(payload, 0, 200), _BG_FR.search(payload), _BG_NX.search(payload), _BG_MK.search(payload)
    if not (i and fr and nx and mk) or fr[1] == "":
        return None
    try:
        return i[1], round(float(fr[1]) * 1e4, 10), int(nx[1]), float(mk[1])
    except ValueError:
        return None


def median(xs: list) -> Optional[float]:
    if not xs:
        return None
    s = sorted(xs)
    n = len(s)
    return s[n // 2] if n % 2 else (s[n // 2 - 1] + s[n // 2]) / 2


def pct(xs: list, q: float) -> Optional[float]:
    if not xs:
        return None
    s = sorted(xs)
    return s[min(len(s) - 1, max(0, -(-int(q * len(s) * 1_000_000) // 1_000_000) - 1))]


# ── per venue: observations and settlements ────────────────────────────────────────────────────────────────────────
class Track:
    """One venue, one symbol: sampled observations and detected settlements."""
    __slots__ = ("obs_ms", "obs_rate", "obs_mark", "pre_ms", "pre_rate", "next_ms", "settles")

    def __init__(self):
        self.obs_ms: list = []
        self.obs_rate: list = []
        self.obs_mark: list = []
        self.pre_ms: Optional[int] = None             # last observation before the funding time it is labelled with
        self.pre_rate: Optional[float] = None
        self.next_ms: Optional[int] = None
        self.settles: list = []                       # (funding_ms, rate_bps, period_h)

    def add(self, ms: int, rate: float, nxt: int, mark: float) -> None:
        if self.next_ms is not None and nxt > self.next_ms:
            t = self.next_ms
            # a switch within ROLL_GRACE_MS after t settles the period at the last rate seen before t; a switch before
            # t is a schedule change, a much later one means the venue's label was stuck: neither is trusted
            if t <= ms <= t + ROLL_GRACE_MS and self.pre_ms is not None and t - SETTLE_FRESH_MS <= self.pre_ms < t:
                prev = self.settles[-1][0] if self.settles else None
                h = (t - prev) / H_MS if prev is not None and t - prev <= 24 * H_MS else (nxt - t) / H_MS
                self.settles.append((t, self.pre_rate, h))
        if self.next_ms is None or nxt != self.next_ms:
            self.next_ms = nxt
        if ms < self.next_ms:
            self.pre_ms, self.pre_rate = ms, rate
        if not self.obs_ms or ms - self.obs_ms[-1] >= SAMPLE_MS:
            self.obs_ms.append(ms)
            self.obs_rate.append(rate)
            self.obs_mark.append(mark)

    def interval_h(self) -> Optional[float]:
        c = Counter(round(s[2], 6) for s in self.settles)
        return c.most_common(1)[0][0] if c else None

    def latest(self, ms: int) -> Optional[int]:
        """Index of the last observation at or before ms (no future data), if fresh."""
        i = bisect.bisect_right(self.obs_ms, ms) - 1
        return i if i >= 0 and ms - self.obs_ms[i] <= PRED_FRESH_MS else None

    def hourly(self) -> dict:
        """hour start ms -> funding accrued in that hour (bps), from settled periods."""
        out = {}
        for t, rate, h in self.settles:
            start = t - int(round(h * H_MS))
            for k in range(int(round(h))):
                out[start - start % H_MS + k * H_MS] = rate / h
        return out


def _read(root: str, since_ns: Optional[int]) -> tuple[dict, Counter, Optional[int], Optional[int]]:
    tracks: dict = {"binance": {}, "bitget": {}}
    msgs: Counter = Counter()
    first = last = None
    for conn in sorted(os.listdir(root)) if os.path.isdir(root) else []:
        d = os.path.join(root, conn)
        if not os.path.isdir(d):
            continue
        if conn.startswith("binance-m"):
            venue, parse, quick = "binance", parse_binance_mark, '"e":"markPriceUpdate"'
        elif conn.startswith("bitget-"):
            venue, parse, quick = "bitget", parse_bitget_ticker, '"channel":"ticker"'
        else:
            continue
        files = sorted((f for f in os.listdir(d) if f.endswith(".tsv.gz")), key=md._file_key)
        if since_ns is not None:
            files = [f for f in files if md._file_hour_end_ns(f) > since_ns]
        for f in files:
            for ns, payload in md.read_file(os.path.join(d, f)):
                if (since_ns is not None and ns < since_ns) or payload.find(quick, 0, 160) < 0:
                    continue
                p = parse(payload)
                if p is None:
                    continue
                ms = ns // 1_000_000
                first = ms if first is None else min(first, ms)
                last = ms if last is None else max(last, ms)
                msgs[venue] += 1
                tracks[venue].setdefault(p[0], Track()).add(ms, p[1], p[2], p[3])
    return tracks, msgs, first, last


# ── the analysis ───────────────────────────────────────────────────────────────────────────────────────────────────
def analyse(root: str, since_ns: Optional[int] = None, symbols: Optional[set] = None,
            roundtrip_bps: float = DEFAULT_RT_BPS, hysteresis_bps: float = DEFAULT_HYST_BPS) -> dict:
    tracks, msgs, first, last = _read(root, since_ns)
    res: dict = {"first_ms": first, "last_ms": last, "msgs": msgs, "rt": roundtrip_bps, "hyst": hysteresis_bps,
                 "settle": {v: {s: t.settles for s, t in tracks[v].items()} for v in tracks}, "sym": {}, "basis": {},
                 "intervals": {}}
    for sym in sorted(set(tracks["binance"]) & set(tracks["bitget"])):
        if symbols and sym not in symbols:
            continue
        bn, bg = tracks["binance"][sym], tracks["bitget"][sym]
        res["basis"][sym] = [(bn.obs_mark[i] / bg.obs_mark[j] - 1) * 1e4 for i, ms in enumerate(bn.obs_ms)
                             if (j := bg.latest(ms)) is not None and ms - bg.obs_ms[j] <= SAMPLE_MS]
        h_bn, h_bg = bn.interval_h(), bg.interval_h()
        res["intervals"][sym] = (h_bn, h_bg)
        if not h_bn or not h_bg:
            continue
        acc_bn, acc_bg = bn.hourly(), bg.hourly()
        hours = sorted(set(acc_bn) & set(acc_bg))
        spread = {H: acc_bn[H] - acc_bg[H] for H in hours}

        def pred(H: int) -> Optional[float]:
            i, j = bn.latest(H), bg.latest(H)
            return None if i is None or j is None else bn.obs_rate[i] / h_bn - bg.obs_rate[j] / h_bg

        entry = next((k for k, H in enumerate(hours) if pred(H) is not None), None)
        if entry is None:
            continue
        hours = hours[entry:]
        d0 = 1 if pred(hours[0]) >= 0 else -1
        hold = sum(d0 * spread[H] for H in hours)
        d, flip, switches = d0, 0.0, 0
        for H in hours:
            p = pred(H)
            if p is not None and abs(p) * 8 > hysteresis_bps and (1 if p > 0 else -1) != d:
                d, switches = -d, switches + 1
            flip += d * spread[H]
        res["sym"][sym] = {
            "hours": len(hours), "bn_8h": sum(r * 8 / h for _, r, h in bn.settles) / len(bn.settles),
            "bg_8h": sum(r * 8 / h for _, r, h in bg.settles) / len(bg.settles),
            "spread_8h": sum(spread[H] for H in hours) / len(hours) * 8,
            "hold_dir": "short binance / long bitget" if d0 > 0 else "long binance / short bitget",
            "hold_gross": hold, "hold_net": hold - roundtrip_bps,
            "hold_gross_yr_pct": hold / len(hours) * 8760 / 100,
            "breakeven_days": roundtrip_bps / (hold / len(hours) * 24) if hold > 0 else None,
            "flip_gross": flip, "flip_switches": switches, "flip_net": flip - roundtrip_bps * (1 + switches),
            "oracle_gross": abs(sum(spread[H] for H in hours)),
            "oracle_net": abs(sum(spread[H] for H in hours)) - roundtrip_bps,
        }
    return res


# ── report ─────────────────────────────────────────────────────────────────────────────────────────────────────────
def _f(x: Optional[float], d: int = 2) -> str:
    return "–" if x is None else f"{x:,.{d}f}"


def _utc(ms: int) -> str:
    return time.strftime("%Y-%m-%d %H:%MZ", time.gmtime(ms / 1000))


def report(res: dict) -> str:
    out = ["# Funding Binance USDⓈ-M ↔ Bitget USDT-M — analisis rekaman", ""]
    if res["first_ms"] is None:
        out.append("Tidak ada data funding di rentang ini (cek --data: folder rekaman `--market perp`).")
        return "\n".join(out) + "\n"
    days = (res["last_ms"] - res["first_ms"]) / 86_400_000
    out += [f"{_utc(res['first_ms'])} → {_utc(res['last_ms'])} ({days * 24:.1f} jam). Fee per round trip (buka + tutup, "
            f"kedua venue): **{res['rt']:g} bps** (asumsi, belum diverifikasi). Histeresis flip: {res['hyst']:g} bps/8 jam.",
            "", "## 1. Data", "",
            f"- Pesan funding: Binance markPrice {res['msgs']['binance']:,}, Bitget ticker {res['msgs']['bitget']:,}.",
            f"- Settlement terdeteksi: Binance {sum(len(v) for v in res['settle']['binance'].values())}, "
            f"Bitget {sum(len(v) for v in res['settle']['bitget'].values())}.",
            "- Interval funding (jam) Binance / Bitget: "
            + ", ".join(f"{s} {_f(a, 0)}/{_f(b, 0)}" for s, (a, b) in sorted(res["intervals"].items())) + ".", ""]

    rows = sorted(res["sym"].items(), key=lambda kv: -kv[1]["flip_net"])
    out += ["## 2. Per koin (bps dari notional satu leg)", "",
            "| Simbol | Jam | Funding Binance rata2 (bps/8 j) | Bitget | Selisih rata2 (bps/8 j) | Hold: sisi | Hold net | "
            "Bruto/tahun (sisi hold) | Hari balik modal | Flip net (switch) | Sisi tetap terbaik, hindsight |",
            "|---|---|---|---|---|---|---|---|---|---|---|"]
    for sym, r in rows:
        be = "tidak pernah" if r["breakeven_days"] is None else f"{r['breakeven_days']:,.1f}"
        out.append(f"| {sym} | {r['hours']} | {_f(r['bn_8h'])} | {_f(r['bg_8h'])} | {_f(r['spread_8h'])} | {r['hold_dir']} | "
                   f"{_f(r['hold_net'])} | {_f(r['hold_gross_yr_pct'], 1)} % | {be} | {_f(r['flip_net'])} ({r['flip_switches']}) | "
                   f"{_f(r['oracle_net'])} |")
    if not rows:
        out.append("| (belum ada jam dengan funding kedua venue) | | | | | | | | | | |")
    out += ["",
            "**Hold** dan **flip** hanya memakai prediksi yang tersedia saat itu (tanpa melihat masa depan). **Sisi tetap "
            "terbaik** memakai hindsight: batas atas, bukan strategi. Net = funding diterima − fee round trip. "
            "**Bruto/tahun** = funding yang diterima sisi hold per jam, disetahunkan (tanpa fee, karena fee dibayar sekali "
            "per posisi, bukan per tahun). **Hari balik modal** = fee round trip ÷ funding per hari; 'tidak pernah' = sisi "
            "hold merugi. Dengan data beberapa hari, keduanya sangat kasar. "
            "Modal yang dibutuhkan = margin di kedua venue (dua leg), jadi imbal hasil per modal bergantung leverage.", ""]

    out += ["## 3. Basis mark Binance vs Bitget (biaya/risiko saat buka dan tutup)", "",
            "| Simbol | p10 (bps) | p50 (bps) | p90 (bps) | Sampel |", "|---|---|---|---|---|"]
    for sym in sorted(res["basis"]):
        b = res["basis"][sym]
        out.append(f"| {sym} | {_f(pct(b, 0.1))} | {_f(median(b))} | {_f(pct(b, 0.9))} | {len(b):,} |")
    out += ["", "Saat posisi dibuka dan ditutup, perubahan basis antara dua venue ikut menjadi untung/rugi. Rentang "
            "p10–p90 yang lebar dibanding funding yang dikumpulkan berarti hasil funding bisa hilang di basis.", ""]
    return "\n".join(out) + "\n"


def main(argv: Optional[list] = None) -> int:
    ap = argparse.ArgumentParser(description="Funding difference Binance USDⓈ-M vs Bitget USDT-M from a perp recording.")
    ap.add_argument("--data", required=True, help="perp recording directory (md_recorder.py --market perp --out ...)")
    ap.add_argument("--last-hours", type=float, help="only the last N hours (default: everything)")
    ap.add_argument("--symbols", help="comma-separated (default: all)")
    ap.add_argument("--roundtrip-bps", type=float, default=DEFAULT_RT_BPS,
                    help="fees for open + close on both venues, bps of one leg (default 22, base taker tiers, unverified)")
    ap.add_argument("--hysteresis-bps", type=float, default=DEFAULT_HYST_BPS,
                    help="flip only when the predicted spread exceeds this, bps per 8 h (default 0.5)")
    a = ap.parse_args(argv)
    since = time.time_ns() - int(a.last_hours * 3600e9) if a.last_hours else None
    syms = {s.strip().upper() for s in a.symbols.split(",") if s.strip()} if a.symbols else None
    sys.stdout.write(report(analyse(a.data, since, syms, a.roundtrip_bps, a.hysteresis_bps)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
