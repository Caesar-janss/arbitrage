"""
Tests for tools/funding_analysis.py (stdlib only).

Synthetic perp recordings in the recorder's file format. Message shapes are copied from md-tokyo (2026-11-17):
  Binance  {"stream":"btcusdt@markPrice@1s","data":{"e":"markPriceUpdate","E":..,"s":"BTCUSDT","p":..,"ap":..,"P":..,
            "i":..,"r":"0.00004061","T":1794931200000,"st":1}}
  Bitget   {"action":"snapshot","arg":{"instType":"USDT-FUTURES","channel":"ticker","instId":"ETHUSDT"},"data":[{...,
            "fundingRate":"-0.000032","nextFundingTime":"1794931200000","markPrice":"1814.14",...,"ts":"1794926785084"}],..}
Rates in the tests are given in bps (1 bps = 0.0001).

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

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import funding_analysis as fa  # noqa: E402

T0_MS = calendar.timegm((2026, 11, 16, 0, 0, 0)) * 1000          # a UTC midnight
H_MS = 3_600_000
TOOL = os.path.join(os.path.dirname(__file__), "..", "funding_analysis.py")

REAL_BN = ('{"stream":"btcusdt@markPrice@1s","data":{"e":"markPriceUpdate","E":1794926785000,"s":"BTCUSDT",'
           '"p":"64965.40000000","ap":"64965.40000000","P":"65045.31654419","i":"64995.33782609","r":"0.00004061",'
           '"T":1794931200000,"st":1}}')
REAL_BG = ('{"action":"snapshot","arg":{"instType":"USDT-FUTURES","channel":"ticker","instId":"ETHUSDT"},"data":[{'
           '"instId":"ETHUSDT","lastPr":"1813.98","bidPr":"1813.94","askPr":"1813.95","bidSz":"49.86","askSz":"41.65",'
           '"open24h":"1845.46","high24h":"1853.94","low24h":"1784.3","change24h":"-0.01706","fundingRate":"-0.000032",'
           '"nextFundingTime":"1794931200000","markPrice":"1814.14","indexPrice":"1815.1121739130434782",'
           '"holdingAmount":"772953.409999999765","baseVolume":"632699.01","quoteVolume":"1150650409.323",'
           '"openUtc":"1819.62","symbolType":"1","symbol":"ETHUSDT","ts":"1794926785084"}],"ts":1794926785086}')


def bmark(sym: str, rate_bps: float, next_ms: int, mark: float, e_ms: int) -> str:
    return ('{"stream":"%s@markPrice@1s","data":{"e":"markPriceUpdate","E":%d,"s":"%s","p":"%s","ap":"%s","P":"%s",'
            '"i":"%s","r":"%.8f","T":%d,"st":1}}' % (sym.lower(), e_ms, sym, mark, mark, mark, mark, rate_bps / 1e4,
                                                     next_ms))


def btick(sym: str, rate_bps: float, next_ms: int, mark: float, ts_ms: int) -> str:
    return ('{"action":"snapshot","arg":{"instType":"USDT-FUTURES","channel":"ticker","instId":"%s"},"data":[{'
            '"instId":"%s","lastPr":"%s","fundingRate":"%.8f","nextFundingTime":"%d","markPrice":"%s",'
            '"indexPrice":"%s","symbol":"%s","ts":"%d"}],"ts":%d}'
            % (sym, sym, mark, rate_bps / 1e4, next_ms, mark, mark, sym, ts_ms, ts_ms))


class Rec:
    def __init__(self):
        self.root = tempfile.mkdtemp(prefix="fa_test_")
        self.rows: dict = {}

    def add(self, conn: str, ms: int, payload: str) -> None:
        hour = __import__("time").strftime("%Y%m%d-%H", __import__("time").gmtime(ms / 1000))
        self.rows.setdefault((conn, hour), []).append((ms * 1_000_000, payload))

    def series(self, venue: str, rate, hours: float, interval_h: float = 8, sym: str = "BTCUSDT", step_min: int = 5,
               mark=lambda t: 100.0, start_h: float = 0, skip=lambda t: False) -> None:
        """One message every step_min minutes from start_h to hours; rate(t_hours) in bps; next funding on the grid."""
        t = int(start_h * H_MS)
        while t <= hours * H_MS:
            th = t / H_MS
            if not skip(th):
                nxt = T0_MS + (int(t // (interval_h * H_MS)) + 1) * int(interval_h * H_MS)
                ms = T0_MS + t
                if venue == "binance":
                    self.add("binance-m0", ms, bmark(sym, rate(th), nxt, mark(th), ms - 2))
                else:
                    self.add("bitget-0", ms, btick(sym, rate(th), nxt, mark(th), ms - 5))
            t += step_min * 60_000

    def write(self) -> str:
        for (conn, hour), rows in self.rows.items():
            os.makedirs(os.path.join(self.root, conn), exist_ok=True)
            data = "".join(f"{ns}\t{p}\n" for ns, p in sorted(rows, key=lambda r: r[0])).encode()
            with open(os.path.join(self.root, conn, f"{hour}_00.tsv.gz"), "wb") as f:
                f.write(gzip.compress(data))
        return self.root

    def close(self) -> None:
        shutil.rmtree(self.root, ignore_errors=True)


class FundingAnalysisTest(unittest.TestCase):
    def setUp(self):
        self.rec = Rec()

    def tearDown(self):
        self.rec.close()

    def run_(self, **kw) -> dict:
        kw.setdefault("roundtrip_bps", 0.0)
        return fa.analyse(self.rec.write(), **kw)

    # ── parsing real messages ─────────────────────────────────────────────────────────────────────────────────────
    def test_parses_the_real_binance_and_bitget_messages(self):
        self.assertEqual(fa.parse_binance_mark(REAL_BN), ("BTCUSDT", 0.4061, 1794931200000, 64965.4))
        self.assertEqual(fa.parse_bitget_ticker(REAL_BG), ("ETHUSDT", -0.32, 1794931200000, 1814.14))
        self.assertIsNone(fa.parse_bitget_ticker(REAL_BG.replace('"fundingRate":"-0.000032",', "")))
        self.assertIsNone(fa.parse_binance_mark('{"stream":"btcusdt@aggTrade","data":{"e":"aggTrade","E":1}}'))

    # ── settlements ──────────────────────────────────────────────────────────────────────────────────────────────
    def test_settlement_is_the_last_rate_before_the_funding_time_rolls(self):
        self.rec.series("binance", lambda t: 1.0 if t < 7.9 else (3.0 if t < 8 else 5.0), 24.5)
        st = self.run_()["settle"]["binance"]["BTCUSDT"]
        self.assertEqual([s[0] for s in st], [T0_MS + 8 * H_MS, T0_MS + 16 * H_MS, T0_MS + 24 * H_MS])
        self.assertAlmostEqual(st[0][1], 3.0, places=6)          # just before 08:00: not earlier (1), not after (5)
        self.assertEqual({s[2] for s in st}, {8.0})

    def test_four_hour_interval_is_detected(self):
        self.rec.series("bitget", lambda t: 1.0, 24.5, interval_h=4)
        st = self.run_()["settle"]["bitget"]["BTCUSDT"]
        self.assertEqual(len(st), 6)
        self.assertEqual({s[2] for s in st}, {4.0})

    def test_no_recent_observation_before_the_funding_time_is_not_a_settlement(self):
        self.rec.series("binance", lambda t: 1.0, 24.5, skip=lambda t: 7.5 <= t < 8.0)   # blind for 30 min
        st = self.run_()["settle"]["binance"]["BTCUSDT"]
        self.assertEqual([s[0] for s in st], [T0_MS + 16 * H_MS, T0_MS + 24 * H_MS])

    def test_a_schedule_change_before_the_funding_time_is_not_a_settlement(self):
        self.rec.series("binance", lambda t: 1.0, 7.84)            # last regular observation 07:50
        nxt = T0_MS + 12 * H_MS                                   # at 07:55 the venue moves the next funding to 12:00
        for k in range(0, 8):
            ms = T0_MS + 7 * H_MS + 55 * 60_000 + k * 300_000
            self.rec.add("binance-m0", ms, bmark("BTCUSDT", 1.0, nxt, 100.0, ms))
        self.assertEqual(self.run_()["settle"]["binance"].get("BTCUSDT", []), [])

    # ── policies ─────────────────────────────────────────────────────────────────────────────────────────────────
    def _flat(self, bn: float, bg: float, hours: float = 24.5):
        self.rec.series("binance", lambda t: bn, hours)
        self.rec.series("bitget", lambda t: bg, hours)

    def test_hold_earns_the_funding_difference_minus_one_round_trip(self):
        self._flat(2.0, -1.0)                                     # 3 bps per 8 h in favour of short Binance / long Bitget
        r = self.run_(roundtrip_bps=2.0)["sym"]["BTCUSDT"]
        self.assertEqual(r["hours"], 24)
        self.assertAlmostEqual(r["hold_gross"], 9.0, places=6)
        self.assertAlmostEqual(r["hold_net"], 7.0, places=6)
        self.assertEqual(r["hold_dir"], "short binance / long bitget")

    def test_hold_takes_the_side_the_prediction_points_to(self):
        self._flat(-2.0, 1.0)
        r = self.run_()["sym"]["BTCUSDT"]
        self.assertAlmostEqual(r["hold_gross"], 9.0, places=6)
        self.assertEqual(r["hold_dir"], "long binance / short bitget")

    def test_rates_on_different_intervals_are_compared_per_hour(self):
        self.rec.series("binance", lambda t: 2.0, 24.5, interval_h=8)     # 0.25 bps/h
        self.rec.series("bitget", lambda t: 1.0, 24.5, interval_h=4)      # 0.25 bps/h
        r = self.run_()["sym"]["BTCUSDT"]
        self.assertAlmostEqual(r["hold_gross"], 0.0, places=6)
        self.assertAlmostEqual(r["bn_8h"], 2.0, places=6)
        self.assertAlmostEqual(r["bg_8h"], 2.0, places=6)

    def test_flip_switches_on_a_reversal_and_uses_no_future_data(self):
        self.rec.series("binance", lambda t: 2.0 if t < 12 else -2.0, 24.5)
        self.rec.series("bitget", lambda t: 0.0, 24.5)
        r = self.run_(hysteresis_bps=0.5)["sym"]["BTCUSDT"]
        self.assertAlmostEqual(r["hold_gross"], -2.0, places=6)
        self.assertEqual(r["flip_switches"], 1)
        self.assertAlmostEqual(r["flip_gross"], 4.0, places=6)    # switching at 12:00 exactly, not at 11:00
        self.assertAlmostEqual(r["oracle_gross"], 2.0, places=6)

    def test_each_switch_costs_a_round_trip(self):
        self.rec.series("binance", lambda t: 2.0 if t < 12 else -2.0, 24.5)
        self.rec.series("bitget", lambda t: 0.0, 24.5)
        r = self.run_(roundtrip_bps=1.0, hysteresis_bps=0.5)["sym"]["BTCUSDT"]
        self.assertAlmostEqual(r["flip_net"], 4.0 - 2 * 1.0, places=6)
        self.assertAlmostEqual(r["hold_net"], -2.0 - 1.0, places=6)

    def test_a_stale_prediction_does_not_open_the_position(self):
        self.rec.series("binance", lambda t: 1.0, 24.5, skip=lambda t: 0.1 < t < 7.9)     # blind 00:10 to 07:50
        self.rec.series("bitget", lambda t: 0.0, 24.5, start_h=3)
        r = self.run_()["sym"]["BTCUSDT"]
        self.assertEqual(r["hours"], 16)                         # entry at 08:00, not at 03:00 on a 3-hour-old quote

    def test_small_predicted_spreads_do_not_cause_switches(self):
        self.rec.series("binance", lambda t: 0.2 if (t // 2) % 2 == 0 else -0.2, 24.5)    # flickers around zero
        self.rec.series("bitget", lambda t: 0.0, 24.5)
        r = self.run_(hysteresis_bps=0.5)["sym"]["BTCUSDT"]
        self.assertEqual(r["flip_switches"], 0)

    # ── basis and output ─────────────────────────────────────────────────────────────────────────────────────────
    def test_mark_basis_between_venues(self):
        self.rec.series("binance", lambda t: 1.0, 3, mark=lambda t: 100.10)
        self.rec.series("bitget", lambda t: 1.0, 3, mark=lambda t: 100.00)
        b = self.run_()["basis"]["BTCUSDT"]
        self.assertAlmostEqual(fa.median(b), 10.0, places=6)

    def test_report_and_cli(self):
        self._flat(2.0, -1.0)
        root = self.rec.write()
        out = subprocess.run([sys.executable, TOOL, "--data", root, "--roundtrip-bps", "22"], capture_output=True,
                             text=True, timeout=60)
        self.assertEqual(out.returncode, 0, out.stderr)
        for s in ("# Funding", "## 1.", "## 2.", "## 3.", "BTCUSDT", "short binance / long bitget"):
            self.assertIn(s, out.stdout)

    def test_report_on_empty_data(self):
        root = tempfile.mkdtemp(prefix="fa_empty_")
        try:
            self.assertIn("Tidak ada data", fa.report(fa.analyse(root)))
        finally:
            shutil.rmtree(root, ignore_errors=True)


if __name__ == "__main__":
    unittest.main()
