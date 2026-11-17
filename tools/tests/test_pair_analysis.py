"""
Tests for tools/pair_analysis.py (stdlib only).

Every test writes a tiny recording in the recorder's own file format (one directory per connection, hourly gzip,
"<recv_ns> TAB <venue message>") and checks the analysis exactly. Prices are chosen so the arithmetic is easy:
Bitget ask 100.00 against a Binance bid of 100.50 is a 50 bps gross gap; with 20 bps of fees for both legs, 30 bps net,
on 10 coins = $1,000 of top-of-book, i.e. $3.00 if taken once.

  python3 -m unittest discover -s tools/tests -v
"""
from __future__ import annotations

import calendar
import gzip
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
import zlib

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import pair_analysis as pa  # noqa: E402

HOUR = "20261116-10"
BASE_NS = calendar.timegm((2026, 11, 16, 10, 0, 0)) * 1_000_000_000
TOOL = os.path.join(os.path.dirname(__file__), "..", "pair_analysis.py")


def bn(sym: str, bid: float, bid_q: float, ask: float, ask_q: float) -> str:
    return ('{"stream":"%s@bookTicker","data":{"u":1,"s":"%s","b":"%s","B":"%s","a":"%s","A":"%s"}}'
            % (sym.lower(), sym, bid, bid_q, ask, ask_q))


def bg(sym: str, bid: float, bid_q: float, ask: float, ask_q: float, ts: int = 1) -> str:
    return ('{"action":"snapshot","arg":{"instType":"SPOT","channel":"books1","instId":"%s"},"data":[{"asks":[["%s","%s"]],'
            '"bids":[["%s","%s"]],"checksum":0,"seq":1,"ts":"%d"}],"ts":1}' % (sym, ask, ask_q, bid, bid_q, ts))


def bnt(sym: str, event_ms: int) -> str:
    """Binance trade message: E = venue event time (ms)."""
    return ('{"stream":"%s@trade","data":{"e":"trade","E":%d,"s":"%s","t":1,"p":"100","q":"1","T":%d,"m":true,"M":true}}'
            % (sym.lower(), event_ms, sym, event_ms))


def server_ms(ms: float, lag_ms: float, base_ns: int = None) -> int:
    """Venue timestamp of a message we receive at `ms` (test clock) after a feed lag of `lag_ms`."""
    return int(((BASE_NS if base_ns is None else base_ns) // 1_000_000) + ms - lag_ms)


def meta(ev: str) -> str:
    return '#{"ev":"%s","reason":"test"}' % ev


# neutral books: no crossing in either direction
BN_FLAT = (99.99, 10, 100.01, 10)
BG_FLAT = (99.98, 10, 100.00, 10)


class Rec:
    """A recording on disk: Rec.add("binance-0", ms, payload) ... then .root."""

    def __init__(self):
        self.root = tempfile.mkdtemp(prefix="pa_test_")
        self.lines: dict = {}

    def add(self, conn: str, ms: float, payload: str, hour: str = HOUR, base_ns: int = BASE_NS) -> None:
        self.lines.setdefault((conn, hour), []).append((base_ns + int(ms * 1_000_000), payload))

    def write(self, truncate: bool = False) -> str:
        for (conn, hour), rows in self.lines.items():
            os.makedirs(os.path.join(self.root, conn), exist_ok=True)
            path = os.path.join(self.root, conn, f"{hour}_00.tsv.gz")
            data = "".join(f"{ns}\t{p}\n" for ns, p in sorted(rows, key=lambda r: r[0])).encode()
            if truncate:                                     # a file still being written: sync-flushed, no trailer
                co = zlib.compressobj(6, zlib.DEFLATED, 31)
                raw = co.compress(data) + co.flush(zlib.Z_SYNC_FLUSH)
            else:
                raw = gzip.compress(data)
            with open(path, "wb") as f:
                f.write(raw)
        return self.root

    def close(self) -> None:
        shutil.rmtree(self.root, ignore_errors=True)


def run(rec: Rec, **kw) -> dict:
    kw.setdefault("fee_levels", (20,))
    kw.setdefault("latencies_ms", (10, 30, 50))
    kw.setdefault("min_usd", 50)
    return pa.analyse(rec.write(kw.pop("truncate", False)), keep_episodes=True, **kw)


class PairAnalysisTest(unittest.TestCase):
    def setUp(self):
        self.rec = Rec()
        self.rec.add("binance-0", 0, bn("BTCUSDT", *BN_FLAT))
        self.rec.add("bitget-0", 1, bg("BTCUSDT", *BG_FLAT))

    def tearDown(self):
        self.rec.close()

    def _spike(self, up_ms: float = 1000, down_ms: float = 1040, bid: float = 100.50):
        """Binance bid jumps above the Bitget ask, then falls back: buy on Bitget, sell on Binance."""
        self.rec.add("binance-0", up_ms, bn("BTCUSDT", bid, 10, bid + 0.02, 10))
        self.rec.add("binance-0", down_ms, bn("BTCUSDT", *BN_FLAT))

    # ── episodes ─────────────────────────────────────────────────────────────────────────────────────────────────
    def test_flat_books_have_no_episode(self):
        self.rec.add("binance-0", 500, bn("BTCUSDT", *BN_FLAT))
        self.assertEqual(run(self.rec)["episodes"], [])

    def test_crossing_is_one_episode_with_direction_duration_and_movers(self):
        self._spike()
        eps = run(self.rec)["episodes"]
        self.assertEqual(len(eps), 1)
        e = eps[0]
        self.assertEqual((e["sym"], e["buy"], e["sell"]), ("BTCUSDT", "bitget", "binance"))
        self.assertAlmostEqual(e["dur_ms"], 40.0, places=3)
        self.assertAlmostEqual(e["net_open"], 30.0, places=6)
        self.assertAlmostEqual(e["usd_open"], 1000.0, places=6)
        self.assertEqual((e["opener"], e["closer"]), ("binance", "binance"))

    def test_other_direction_and_bitget_closing_it(self):
        # Bitget bid jumps above the Binance ask (buy Binance, sell Bitget); Bitget's own update closes it.
        self.rec.add("bitget-0", 2000, bg("BTCUSDT", 100.60, 10, 100.62, 10))
        self.rec.add("binance-0", 2005, bn("BTCUSDT", 100.59, 10, 100.61, 10))   # Binance catches up -> closes
        e = run(self.rec)["episodes"][0]
        self.assertEqual((e["buy"], e["sell"], e["opener"], e["closer"]), ("binance", "bitget", "bitget", "binance"))
        self.assertAlmostEqual(e["dur_ms"], 5.0, places=3)

    def test_fee_is_charged_on_both_legs(self):
        self._spike(bid=100.15)                                  # 15 bps gross
        res = run(self.rec, fee_levels=(20, 15, 10, 0))
        levels = sorted(e["level"] for e in res["episodes"])
        self.assertEqual(levels, [0, 10, 15])                     # 15 - 15 = 0 is still >= 0; 15 - 20 < 0 is not

    def test_top_of_book_too_small_is_not_an_episode(self):
        self.rec.add("bitget-0", 900, bg("BTCUSDT", 99.98, 10, 100.00, 0.2))   # $20 on the ask
        self._spike()
        self.assertEqual(run(self.rec)["episodes"], [])

    def test_size_is_the_smaller_leg(self):
        self.rec.add("bitget-0", 900, bg("BTCUSDT", 99.98, 10, 100.00, 3))     # 3 coins on Bitget's ask
        self._spike()
        self.assertAlmostEqual(run(self.rec)["episodes"][0]["usd_open"], 300.0, places=6)

    # ── what a real order would have met ───────────────────────────────────────────────────────────────────────────
    def test_capture_is_zero_once_the_gap_closed_before_we_arrive(self):
        self._spike()                                             # lives 40 ms
        cap = run(self.rec)["episodes"][0]["capture"]
        self.assertAlmostEqual(cap[10], 3.0, places=6)
        self.assertAlmostEqual(cap[30], 3.0, places=6)
        self.assertIsNone(cap[50])

    def test_gap_closing_exactly_on_arrival_is_missed(self):
        self._spike(up_ms=1000, down_ms=1030)                    # lives exactly 30 ms
        cap = run(self.rec)["episodes"][0]["capture"]
        self.assertAlmostEqual(cap[10], 3.0, places=6)
        self.assertIsNone(cap[30])

    def test_capture_uses_the_state_at_arrival_not_at_open(self):
        self.rec.add("binance-0", 1000, bn("BTCUSDT", 100.50, 10, 100.52, 10))     # 30 bps net
        self.rec.add("binance-0", 1020, bn("BTCUSDT", 100.25, 10, 100.27, 10))     # 5 bps net, still open
        self.rec.add("binance-0", 1040, bn("BTCUSDT", 100.35, 10, 100.37, 10))     # 15 bps net, still open
        self.rec.add("binance-0", 1100, bn("BTCUSDT", *BN_FLAT))
        e = run(self.rec)["episodes"][0]
        self.assertAlmostEqual(e["capture"][10], 3.0, places=6)                  # state 0..20 ms
        self.assertAlmostEqual(e["capture"][30], 0.5, places=6)                  # state 20..40 ms, not the open one
        self.assertAlmostEqual(e["capture"][50], 1.5, places=6)                  # state 40..100 ms
        self.assertAlmostEqual(e["net_max"], 30.0, places=6)

    def test_same_read_open_and_close_is_zero_duration(self):
        self._spike(up_ms=1000, down_ms=1000)                    # one socket read carried both messages
        e = run(self.rec)["episodes"][0]
        self.assertEqual(e["dur_ms"], 0.0)
        self.assertIsNone(e["capture"][10])

    # ── data quality ───────────────────────────────────────────────────────────────────────────────────────────────
    def test_disconnect_drops_that_connections_quotes(self):
        self.rec.add("binance-0", 500, bn("BTCUSDT", 100.50, 10, 100.52, 10))     # crossing opens
        self.rec.add("binance-0", 520, meta("disconnect"))                       # quote no longer known
        self.rec.add("bitget-0", 600, bg("BTCUSDT", 99.98, 10, 100.00, 10))       # would "cross" with a stale quote
        eps = run(self.rec)["episodes"]
        self.assertEqual(len(eps), 1)
        self.assertEqual(eps[0]["closer"], "gap")
        self.assertAlmostEqual(eps[0]["dur_ms"], 20.0, places=3)

    def test_episode_still_open_at_the_end_is_closed_as_end(self):
        self.rec.add("binance-0", 500, bn("BTCUSDT", 100.50, 10, 100.52, 10))
        self.assertEqual(run(self.rec)["episodes"][0]["closer"], "end")

    def test_connections_of_one_venue_are_merged_in_time_order(self):
        self.rec.add("bitget-1", 2, bg("ETHUSDT", 2999, 1, 3000, 1))
        self.rec.add("binance-0", 3, bn("ETHUSDT", 2999.5, 1, 3000.5, 1))
        self.rec.add("binance-0", 1000, bn("ETHUSDT", 3009, 1, 3009.5, 1))        # 30 bps gross, 10 net, $3000
        self.rec.add("bitget-1", 1010, bg("ETHUSDT", 3009, 1, 3009.2, 1))         # Bitget catches up
        e = [x for x in run(self.rec)["episodes"] if x["sym"] == "ETHUSDT"][0]
        self.assertEqual((e["buy"], e["sell"], e["closer"]), ("bitget", "binance", "bitget"))
        self.assertAlmostEqual(e["dur_ms"], 10.0, places=3)

    def test_symbols_filter(self):
        self._spike()
        self.assertEqual(run(self.rec, symbols={"ETHUSDT"})["episodes"], [])

    def test_last_hours_cut_skips_older_files(self):
        self.rec.add("binance-0", 1000, bn("BTCUSDT", 100.50, 10, 100.52, 10), hour="20261116-08",
                     base_ns=BASE_NS - 2 * 3_600_000_000_000)
        res = run(self.rec, since_ns=BASE_NS - 1)               # 08:xx ends at 09:00 < since
        self.assertEqual(res["episodes"], [])

    def test_last_hours_cut_also_inside_a_file(self):
        self._spike(up_ms=500, down_ms=540)
        self.assertEqual(run(self.rec, since_ns=BASE_NS + 800_000_000)["episodes"], [])

    def test_truncated_file_is_read_up_to_the_last_line(self):
        self._spike()
        res = run(self.rec, truncate=True)
        self.assertEqual(len(res["episodes"]), 1)

    def test_parser_falls_back_to_json_and_ignores_empty_sides(self):
        self.assertEqual(pa.parse_binance('{"stream":"x@bookTicker","data":{"s":"X","a":"2","A":"3","b":"1","B":"4","u":1}}'),
                         ("X", 1.0, 4.0, 2.0, 3.0))
        empty = ('{"action":"snapshot","arg":{"instType":"SPOT","channel":"books1","instId":"BTCUSDT"},'
                 '"data":[{"asks":[],"bids":[["1","1"]],"ts":"1"}],"ts":1}')
        self.assertIsNone(pa.parse_bitget(empty))

    def test_basis_is_the_median_mid_difference(self):
        self.rec.add("binance-0", 1500, bn("BTCUSDT", 100.09, 10, 100.11, 10))    # mid 100.10 vs Bitget 99.99
        self.rec.add("binance-0", 2600, bn("BTCUSDT", 100.09, 10, 100.11, 10))
        self.rec.add("binance-0", 3700, bn("BTCUSDT", 100.09, 10, 100.11, 10))
        b = run(self.rec)["basis"]["BTCUSDT"]
        self.assertAlmostEqual(pa.median(b), (100.10 / 99.99 - 1) * 1e4, places=6)

    # ── feed lag: is the quote we see already old at the venue? ────────────────────────────────────────────────────
    def _feeds(self, until_ms: float, bn_lag: float = 5, bg_lag: float = 5, step: float = 100, start: float = 0):
        """Background trades on Binance and Bitget books1 refreshes with given lags (same flat Bitget quote)."""
        t = start
        while t <= until_ms:
            self.rec.add("binance-0", t, bnt("BTCUSDT", server_ms(t, bn_lag)))
            self.rec.add("bitget-0", t + 1, bg("BTCUSDT", *BG_FLAT, ts=server_ms(t + 1, bg_lag)))
            t += step

    def test_clean_feeds_mark_the_episode_normal(self):
        self._feeds(990)
        self._spike()
        e = run(self.rec)["episodes"][0]
        self.assertEqual(e["feed"], "normal")

    def test_episode_opened_while_bitget_feed_is_late_is_flagged(self):
        self._feeds(900)
        self.rec.add("bitget-0", 995, bg("BTCUSDT", *BG_FLAT, ts=server_ms(995, 65)))    # 60 ms over its floor
        self._spike()
        e = run(self.rec, feed_lag_ms=20)["episodes"][0]
        self.assertEqual(e["feed"], "late")
        self.assertAlmostEqual(e["lag_excess_ms"]["bitget"], 60.0, places=3)

    def test_episode_opened_while_binance_feed_is_late_is_flagged(self):
        self._feeds(900)
        self.rec.add("binance-0", 995, bnt("BTCUSDT", server_ms(995, 45)))              # 40 ms over its floor
        self._spike()
        e = run(self.rec, feed_lag_ms=20)["episodes"][0]
        self.assertEqual(e["feed"], "late")
        self.assertAlmostEqual(e["lag_excess_ms"]["binance"], 40.0, places=3)

    def test_lag_under_the_threshold_stays_normal(self):
        self._feeds(900)
        self.rec.add("bitget-0", 995, bg("BTCUSDT", *BG_FLAT, ts=server_ms(995, 20)))    # 15 ms over the floor
        self._spike()
        self.assertEqual(run(self.rec, feed_lag_ms=20)["episodes"][0]["feed"], "normal")

    def test_an_implausible_venue_timestamp_is_not_a_lag_sample(self):
        self._feeds(900)
        self.rec.add("bitget-0", 995, bg("BTCUSDT", *BG_FLAT, ts=1))                     # 1970: not a clock reading
        self._spike()
        self.assertEqual(run(self.rec, feed_lag_ms=20)["episodes"][0]["feed"], "normal")

    def test_without_lag_samples_the_feed_state_is_unknown(self):
        self._spike()                                             # setUp quotes carry no usable timestamps
        self.assertEqual(run(self.rec)["episodes"][0]["feed"], "unknown")

    def test_an_old_lag_sample_does_not_count(self):
        self._feeds(200)                                          # last samples at 200 ms
        self._spike(up_ms=5000, down_ms=5040)                     # 4.8 s later: no recent sample
        self.assertEqual(run(self.rec)["episodes"][0]["feed"], "unknown")

    def test_lag_floor_follows_a_clock_step(self):
        # a constant +30 ms (venue clock or our clock stepped) for 15 minutes is the new normal, not a late feed
        self._feeds(990, bn_lag=5, bg_lag=5)
        self._feeds(16 * 60_000, bn_lag=35, bg_lag=35, step=1000, start=1000)
        self._spike(up_ms=16 * 60_000 + 10, down_ms=16 * 60_000 + 50)
        e = run(self.rec, feed_lag_ms=20)["episodes"][0]
        self.assertEqual(e["feed"], "normal")

    def test_capture_is_also_reported_for_normal_feeds_only(self):
        self._feeds(900)
        self.rec.add("bitget-0", 995, bg("BTCUSDT", *BG_FLAT, ts=server_ms(995, 65)))
        self._spike()                                             # late episode: $3 at 10 ms
        self._spike(up_ms=3000, down_ms=3040)                     # stale lag sample (2 s) -> unknown
        res = run(self.rec, feed_lag_ms=20)
        a = res["agg"][(20, "BTCUSDT", "bitget")]
        self.assertAlmostEqual(a["cap_usd"][0], 6.0, places=6)
        self.assertAlmostEqual(a["cap_usd_normal"][0], 0.0, places=6)
        self.assertEqual(a["feed"], {"late": 1, "unknown": 1})

    def test_report_shows_feed_lag(self):
        self._feeds(990)
        self._spike()
        out = pa.report(run(self.rec))
        for head in ("Keterlambatan feed", "Feed telat saat buka", "$/hari (feed normal)"):
            self.assertIn(head, out)

    # ── report ─────────────────────────────────────────────────────────────────────────────────────────────────────
    def test_report_and_cli_render(self):
        self._spike()
        root = self.rec.write()
        out = subprocess.run([sys.executable, TOOL, "--data", root, "--latency-ms", "10,30,50"],
                             capture_output=True, text=True, timeout=60)
        self.assertEqual(out.returncode, 0, out.stderr)
        for head in ("# Binance", "## 1.", "## 2.", "## 3.", "BTCUSDT", "bitget→binance"):
            self.assertIn(head, out.stdout)

    def test_report_on_empty_data_does_not_crash(self):
        root = tempfile.mkdtemp(prefix="pa_empty_")
        try:
            self.assertIn("Tidak ada data", pa.report(pa.analyse(root)))
        finally:
            shutil.rmtree(root, ignore_errors=True)


if __name__ == "__main__":
    unittest.main()
