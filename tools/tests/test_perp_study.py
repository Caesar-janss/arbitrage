"""
Tests for tools/perp_study.py (stdlib only): competition evidence (gap review G-3) and a position simulation that
pays for opening AND closing (G-1).

Prices as in test_pair_analysis: Binance flat 99.99/100.01, Bitget flat 99.98/100.00; a Binance bid of 100.50 against
the Bitget ask 100.00 is a 50 bps gross crossing (buy Bitget, sell Binance) on 10 coins = $1,000.
Fees: Binance taker 5, Bitget taker 6 bps (base tiers, verified 2026-11-18) unless a test says otherwise.

  python3 -m unittest discover -s tools/tests -v
"""
from __future__ import annotations

import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(__file__))
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import perp_study as ps  # noqa: E402
import test_pair_analysis as tpa  # noqa: E402  (same recording helpers)

TOOL = os.path.join(os.path.dirname(__file__), "..", "perp_study.py")
BN_FLAT, BG_FLAT = tpa.BN_FLAT, tpa.BG_FLAT
bn, bg = tpa.bn, tpa.bg


def bgtrade(sym: str, side: str, price: float, size: float, ts: int = 1, action: str = "update") -> str:
    """Bitget USDT-FUTURES trade push; "side" = taker side (UNVERIFIED on live data, see perp_study docstring)."""
    return ('{"action":"%s","arg":{"instType":"USDT-FUTURES","channel":"trade","instId":"%s"},"data":[{"ts":"%d",'
            '"price":"%s","size":"%s","side":"%s","tradeId":"1"}],"ts":%d}' % (action, sym, ts, price, size, side, ts))


def bnagg(sym: str, price: float, qty: float, buyer_is_maker: bool, event_ms: int = 1) -> str:
    """Binance aggTrade: m = buyer is the maker, so m=true means the TAKER SOLD."""
    return ('{"stream":"%s@aggTrade","data":{"e":"aggTrade","E":%d,"a":1,"s":"%s","p":"%s","q":"%s","f":1,"l":1,"T":%d,'
            '"m":%s}}' % (sym.lower(), event_ms, sym, price, qty, event_ms, "true" if buyer_is_maker else "false"))


class Base(unittest.TestCase):
    def setUp(self):
        self.rec = tpa.Rec()
        self.rec.add("binance-0", 0, bn("BTCUSDT", *BN_FLAT))
        self.rec.add("bitget-0", 1, bg("BTCUSDT", *BG_FLAT))

    def tearDown(self):
        self.rec.close()

    def run_(self, **kw) -> dict:
        kw.setdefault("level_bps", 11.0)
        kw.setdefault("latency_ms", 10.0)
        kw.setdefault("normal_only", False)
        kw.setdefault("entry_bps", 22.0)
        kw.setdefault("close_bps", 5.0)
        kw.setdefault("max_usd", 1000.0)
        return ps.analyse(self.rec.write(), **kw)


class ParseTest(unittest.TestCase):
    def test_trade_parsers(self):
        self.assertEqual(ps.parse_trade("binance", bnagg("BTCUSDT", 100.0, 2.0, True)), ("BTCUSDT", "sell", 100.0, 2.0))
        self.assertEqual(ps.parse_trade("binance", bnagg("BTCUSDT", 100.0, 2.0, False)), ("BTCUSDT", "buy", 100.0, 2.0))
        self.assertEqual(ps.parse_trade("bitget", bgtrade("BTCUSDT", "buy", 100.0, 1.5)), ("BTCUSDT", "buy", 100.0, 1.5))
        self.assertIsNone(ps.parse_trade("bitget", bgtrade("BTCUSDT", "buy", 100.0, 1.5, action="snapshot")))  # history
        self.assertIsNone(ps.parse_trade("bitget", bg("BTCUSDT", *BG_FLAT)))
        self.assertIsNone(ps.parse_trade("binance", bn("BTCUSDT", *BN_FLAT)))


class CompetitionTest(Base):
    def _episode(self, close_ms: float = 1004):
        """Binance bid jumps (opener Binance): the stale quote is Bitget's ask; Bitget then moves its ask (closer)."""
        self.rec.add("binance-0", 1000, bn("BTCUSDT", 100.50, 10, 100.52, 10))
        self.rec.add("bitget-0", close_ms, bg("BTCUSDT", 100.49, 10, 100.55, 10))

    def test_a_taker_buy_at_the_stale_ask_is_a_hit(self):
        self._episode()
        self.rec.add("bitget-0", 1003, bgtrade("BTCUSDT", "buy", 100.00, 1))
        c = self.run_()["comp"]["BTCUSDT"]
        self.assertEqual((c["episodes"], c["hit"]), (1, 1))
        self.assertAlmostEqual(ps.median(c["t_hit"]), 3.0, places=3)

    def test_the_wrong_side_or_venue_is_not_a_hit(self):
        self._episode()
        self.rec.add("bitget-0", 1002, bgtrade("BTCUSDT", "sell", 100.00, 1))          # taker sold: not our leg
        self.rec.add("binance-m0", 1003, bnagg("BTCUSDT", 100.00, 1, False))            # wrong venue
        c = self.run_()["comp"]["BTCUSDT"]
        self.assertEqual((c["episodes"], c["hit"], c["same_side"]), (1, 0, 0))

    def test_a_buy_below_the_stale_ask_is_not_a_hit_but_is_same_side(self):
        self._episode()
        self.rec.add("bitget-0", 1003, bgtrade("BTCUSDT", "buy", 99.99, 1))             # cannot have taken 100.00
        c = self.run_()["comp"]["BTCUSDT"]
        self.assertEqual((c["hit"], c["same_side"]), (0, 1))

    def test_stale_bid_when_the_buy_venue_moved_first(self):
        # Bitget ask drops below the Binance bid (opener Bitget): the stale quote is Binance's bid -> taker SELL on Binance
        self.rec.add("bitget-0", 1000, bg("BTCUSDT", 99.40, 10, 99.42, 10))
        self.rec.add("binance-m0", 1002, bnagg("BTCUSDT", 99.99, 1, True))              # taker sold at the stale bid
        self.rec.add("binance-0", 1006, bn("BTCUSDT", 99.41, 10, 99.43, 10))
        c = self.run_()["comp"]["BTCUSDT"]
        self.assertEqual((c["episodes"], c["hit"]), (1, 1))

    def test_baseline_counts_same_side_trades_in_the_equal_window_before(self):
        self._episode(close_ms=1004)                                                   # 4 ms episode
        self.rec.add("bitget-0", 990, bgtrade("BTCUSDT", "buy", 100.00, 1))             # outside [996, 1000)
        self.rec.add("bitget-0", 998, bgtrade("BTCUSDT", "buy", 100.00, 1))             # inside
        c = self.run_()["comp"]["BTCUSDT"]
        self.assertEqual(c["pre_same_side"], 1)

    def test_a_trade_just_outside_the_equal_window_is_not_baseline(self):
        self._episode(close_ms=1004)                                                   # window [996, 1000)
        self.rec.add("bitget-0", 990, bgtrade("BTCUSDT", "buy", 100.00, 1))
        self.assertEqual(self.run_()["comp"]["BTCUSDT"]["pre_same_side"], 0)

    def test_same_read_episodes_are_left_out(self):
        self.rec.add("binance-0", 1000, bn("BTCUSDT", 100.50, 10, 100.52, 10))
        self.rec.add("binance-0", 1000, bn("BTCUSDT", *BN_FLAT))
        c = self.run_()["comp"].get("BTCUSDT", {"episodes": 0, "zero": 0})
        self.assertEqual((c["episodes"], c["zero"]), (0, 1))


class SimulationTest(Base):
    def test_open_then_close_on_convergence_pays_four_fees(self):
        self.rec.add("binance-0", 1000, bn("BTCUSDT", 100.50, 10, 100.52, 10))
        self.rec.add("binance-0", 1500, bn("BTCUSDT", *BN_FLAT))                        # exit gross -3.0 bps
        self.rec.add("binance-0", 2000, bn("BTCUSDT", *BN_FLAT))
        t = self.run_()["trades"]
        self.assertEqual(len(t), 1)
        x = t[0]
        self.assertEqual((x["sym"], x["long"], x["short"], x["reason"]), ("BTCUSDT", "bitget", "binance", "converged"))
        self.assertAlmostEqual(x["entry_bps"], 50.0, places=3)
        self.assertAlmostEqual(x["exit_bps"], (99.98 / 100.01 - 1) * 1e4, places=6)
        self.assertAlmostEqual(x["net_bps"], 50.0 + (99.98 / 100.01 - 1) * 1e4 - 22.0, places=3)
        self.assertAlmostEqual(x["usd"], x["net_bps"] / 1e4 * 1000.0, places=6)
        self.assertEqual(x["open_ns"] - tpa.BASE_NS, 1_010_000_000)                    # executed at +L, not at the signal
        self.assertEqual(x["close_ns"] - tpa.BASE_NS, 1_510_000_000)

    def test_maker_exit_fees(self):
        self.rec.add("binance-0", 1000, bn("BTCUSDT", 100.50, 10, 100.52, 10))
        self.rec.add("binance-0", 1500, bn("BTCUSDT", *BN_FLAT))
        self.rec.add("binance-0", 2000, bn("BTCUSDT", *BN_FLAT))
        x = self.run_(exit_fee_bn=2.0, exit_fee_bg=2.0)["trades"][0]
        self.assertAlmostEqual(x["net_bps"], 50.0 + (99.98 / 100.01 - 1) * 1e4 - 11.0 - 4.0, places=3)

    def test_edge_gone_before_the_order_arrives_is_a_miss(self):
        self.rec.add("binance-0", 1000, bn("BTCUSDT", 100.50, 10, 100.52, 10))
        self.rec.add("binance-0", 1005, bn("BTCUSDT", *BN_FLAT))                        # gone at +5 ms, order at +10
        self.rec.add("binance-0", 2000, bn("BTCUSDT", *BN_FLAT))
        r = self.run_()
        self.assertEqual((len(r["trades"]), r["missed"]["BTCUSDT"]), (0, 1))

    def test_no_convergence_closes_on_timeout(self):
        self.rec.add("binance-0", 1000, bn("BTCUSDT", 100.50, 10, 100.52, 10))
        for t in range(1500, 4001, 500):
            self.rec.add("binance-0", t, bn("BTCUSDT", 100.50, 10, 100.52, 10))
        x = self.run_(max_hold_s=2.0)["trades"][0]
        self.assertEqual(x["reason"], "timeout")
        self.assertAlmostEqual(x["exit_bps"], (99.98 / 100.52 - 1) * 1e4, places=6)
        self.assertGreaterEqual(x["close_ns"] - x["open_ns"], 2_000_000_000)

    def test_open_position_at_the_end_is_marked_at_the_last_quotes(self):
        self.rec.add("binance-0", 1000, bn("BTCUSDT", 100.50, 10, 100.52, 10))
        self.rec.add("binance-0", 1100, bn("BTCUSDT", 100.50, 10, 100.52, 10))
        x = self.run_()["trades"][0]
        self.assertEqual(x["reason"], "end")

    def test_one_position_per_symbol(self):
        self.rec.add("binance-0", 1000, bn("BTCUSDT", 100.50, 10, 100.52, 10))
        self.rec.add("binance-0", 1300, bn("BTCUSDT", 100.60, 10, 100.62, 10))          # bigger gap while open
        self.rec.add("binance-0", 1500, bn("BTCUSDT", *BN_FLAT))
        self.rec.add("binance-0", 2000, bn("BTCUSDT", *BN_FLAT))
        self.assertEqual(len(self.run_()["trades"]), 1)

    def test_size_is_capped_by_the_smaller_quote_and_max_usd(self):
        self.rec.add("bitget-0", 900, bg("BTCUSDT", 99.98, 10, 100.00, 3))              # 3 coins on Bitget's ask
        self.rec.add("binance-0", 1000, bn("BTCUSDT", 100.50, 10, 100.52, 10))
        self.rec.add("binance-0", 1500, bn("BTCUSDT", *BN_FLAT))
        self.rec.add("binance-0", 2000, bn("BTCUSDT", *BN_FLAT))
        self.assertAlmostEqual(self.run_()["trades"][0]["notional"], 300.0, places=6)
        self.assertAlmostEqual(self.run_(max_usd=150.0)["trades"][0]["notional"], 150.0, places=6)

    def test_below_the_entry_threshold_nothing_opens(self):
        self.rec.add("binance-0", 1000, bn("BTCUSDT", 100.20, 10, 100.22, 10))          # 20 bps < 22
        self.rec.add("binance-0", 2000, bn("BTCUSDT", *BN_FLAT))
        self.assertEqual(self.run_()["trades"], [])

    def test_normal_only_needs_known_normal_feeds(self):
        self.rec.add("binance-0", 1000, bn("BTCUSDT", 100.50, 10, 100.52, 10))
        self.rec.add("binance-0", 1500, bn("BTCUSDT", *BN_FLAT))
        self.rec.add("binance-0", 2000, bn("BTCUSDT", *BN_FLAT))
        self.assertEqual(self.run_(normal_only=True)["trades"], [])                    # no lag samples: unknown

    def test_report_and_cli(self):
        self.rec.add("binance-0", 1000, bn("BTCUSDT", 100.50, 10, 100.52, 10))
        self.rec.add("bitget-0", 1003, bgtrade("BTCUSDT", "buy", 100.00, 1))
        self.rec.add("binance-0", 1500, bn("BTCUSDT", *BN_FLAT))
        self.rec.add("binance-0", 2000, bn("BTCUSDT", *BN_FLAT))
        root = self.rec.write()
        out = subprocess.run([sys.executable, TOOL, "--data", root, "--all-feeds", "--close-bps", "5"], capture_output=True, text=True,
                             timeout=60)
        self.assertEqual(out.returncode, 0, out.stderr)
        for s in ("# Perp", "## 1.", "## 2.", "## 3.", "BTCUSDT", "Konvergen / timeout / akhir", "| 1 / 0 / 0 |"):
            self.assertIn(s, out.stdout)

    def test_report_on_empty_data(self):
        root = tempfile.mkdtemp(prefix="ps_empty_")
        try:
            self.assertIn("Tidak ada data", ps.report(ps.analyse(root)))
        finally:
            shutil.rmtree(root, ignore_errors=True)


DAY_NS = 86_400 * 1_000_000_000


def trade(sym: str, usd: float, open_ns: int, notional: float = 400.0) -> dict:
    return {"sym": sym, "long": "bitget", "short": "binance", "open_ns": open_ns, "close_ns": open_ns + 1_000_000_000,
            "entry_bps": 30.0, "exit_bps": 0.0, "net_bps": usd / notional * 1e4, "notional": notional, "usd": usd,
            "reason": "converged"}


class SensitivityTest(Base):
    """Review of the 96 h run (2026-11-23): L = 10 ms is optimistic from Tokyo, so several L in ONE pass over the data."""

    def _short_edge(self):
        self.rec.add("binance-0", 1000, bn("BTCUSDT", 100.50, 10, 100.52, 10))          # edge opens
        self.rec.add("binance-0", 1015, bn("BTCUSDT", *BN_FLAT))                        # and is gone 15 ms later
        self.rec.add("binance-0", 2000, bn("BTCUSDT", *BN_FLAT))

    def test_extra_latencies_run_in_one_pass_and_each_has_its_own_result(self):
        self._short_edge()
        r = self.run_(extra_latency_ms=(25.0,))
        self.assertEqual([s["latency_ms"] for s in r["sens"]], [10.0, 25.0])
        self.assertEqual((len(r["sens"][0]["trades"]), r["sens"][0]["missed"]["BTCUSDT"]), (1, 0))   # still there at +10
        self.assertEqual((len(r["sens"][1]["trades"]), r["sens"][1]["missed"]["BTCUSDT"]), (0, 1))   # gone at +25

    def test_the_main_result_is_the_first_latency(self):
        self._short_edge()
        r = self.run_(extra_latency_ms=(25.0, 50.0))
        self.assertEqual(r["trades"], r["sens"][0]["trades"])
        self.assertEqual(r["missed"], r["sens"][0]["missed"])

    def test_extra_latencies_do_not_change_the_main_result(self):
        self._short_edge()
        alone = self.run_()
        with_extra = self.run_(extra_latency_ms=(25.0, 50.0))
        self.assertEqual(alone["trades"], with_extra["trades"])
        self.assertEqual(alone["missed"], with_extra["missed"])
        self.assertEqual(alone["comp"], with_extra["comp"])          # competition stays at the main L

    def test_no_extra_latency_gives_a_single_sensitivity_row(self):
        self._short_edge()
        self.assertEqual(len(self.run_()["sens"]), 1)

    def test_a_pending_order_executes_on_the_quotes_at_its_own_time_not_at_the_end(self):
        self.rec.add("binance-0", 1000, bn("BTCUSDT", 100.50, 10, 100.52, 10))          # edge opens
        self.rec.add("binance-0", 1040, bn("BTCUSDT", *BN_FLAT))                        # gone at +40, after both orders (+10, +25)
        self.rec.add("binance-0", 2000, bn("BTCUSDT", *BN_FLAT))
        r = self.run_(extra_latency_ms=(25.0,))
        for s in r["sens"]:
            self.assertEqual((len(s["trades"]), s["missed"]["BTCUSDT"]), (1, 0), s["latency_ms"])

    def test_a_latency_equal_to_the_main_one_is_not_simulated_twice(self):
        self._short_edge()
        self.assertEqual([s["latency_ms"] for s in self.run_(extra_latency_ms=(10.0, 25.0, 25.0))["sens"]][:2], [10.0, 25.0])
        self.assertEqual(len(self.run_(extra_latency_ms=(10.0,))["sens"]), 1)

    def test_extra_simulations_also_mark_open_positions_at_the_end(self):
        self.rec.add("binance-0", 1000, bn("BTCUSDT", 100.50, 10, 100.52, 10))
        self.rec.add("binance-0", 1100, bn("BTCUSDT", 100.50, 10, 100.52, 10))          # edge never goes away
        r = self.run_(extra_latency_ms=(25.0,))
        self.assertEqual([[t["reason"] for t in s["trades"]] for s in r["sens"]], [["end"], ["end"]])
        self.assertEqual(r["sens"][1]["trades"][0]["open_ns"] - tpa.BASE_NS, 1_025_000_000)   # opened at its own +L


class ConcentrationTest(unittest.TestCase):
    def test_top10_share_and_positions_for_half(self):
        ts = [trade("A", 10.0, 0)] + [trade("B", 1.0, i) for i in range(1, 91)]              # total 100
        c = ps.concentration(ts)
        self.assertAlmostEqual(c["top10_share"], (10.0 + 9 * 1.0) / 100.0, places=9)
        self.assertEqual(c["n_for_half"], 41)                                                  # 10 + 40 x 1 = 50
        self.assertEqual(c["n"], 91)

    def test_losing_positions_stay_in_the_total(self):
        ts = ([trade("A", 8.0, 0), trade("A", 4.0, 1)] + [trade("A", 1.0, 2 + i) for i in range(10)]
              + [trade("B", -2.0, 20 + i) for i in range(3)])                                  # total 8+4+10-6 = 16
        c = ps.concentration(ts)
        self.assertAlmostEqual(c["total"], 16.0, places=9)
        self.assertAlmostEqual(c["top10_share"], (8.0 + 4.0 + 8 * 1.0) / 16.0, places=9)       # 125 %: losers offset, shown as is

    def test_top_symbol_top3_and_the_total_without_the_top_symbol(self):
        ts = ([trade("NEAR", 4.0, i) for i in range(6)] + [trade("WIF", 3.0, 10), trade("APT", 2.0, 11),
              trade("ETH", 1.0, 12)])                                                          # total 30
        c = ps.concentration(ts)
        self.assertEqual(c["top_sym"], "NEAR")
        self.assertAlmostEqual(c["top_sym_share"], 24.0 / 30.0, places=9)
        self.assertAlmostEqual(c["top3_share"], 29.0 / 30.0, places=9)
        self.assertAlmostEqual(c["ex_top_usd"], 6.0, places=9)

    def test_empty_and_nonpositive_totals_do_not_crash(self):
        self.assertIsNone(ps.concentration([])["top10_share"])
        self.assertIsNone(ps.concentration([trade("A", -1.0, 0)])["top10_share"])


class DailyTest(unittest.TestCase):
    D0 = 20_043 * DAY_NS                          # a whole UTC day (2024-12-...: any day boundary will do)

    def test_trades_are_grouped_by_utc_day_of_the_opening_and_normalised_to_24_hours(self):
        first, last = self.D0 + 10 * 3600 * 10**9, self.D0 + DAY_NS + 12 * 3600 * 10**9          # 14 h + 12 h of data
        ts = [trade("A", 2.0, self.D0 + 11 * 3600 * 10**9), trade("B", 1.0, self.D0 + 20 * 3600 * 10**9),
              trade("A", 6.0, self.D0 + DAY_NS + 3600 * 10**9)]
        rows = ps.daily(ts, first, last)
        self.assertEqual([r["n"] for r in rows], [2, 1])
        self.assertEqual([round(r["hours"], 6) for r in rows], [14.0, 12.0])
        self.assertAlmostEqual(rows[0]["usd"], 3.0, places=9)
        self.assertAlmostEqual(rows[0]["usd_per_day"], 3.0 * 24 / 14, places=9)
        self.assertAlmostEqual(rows[1]["usd_per_day"], 6.0 * 24 / 12, places=9)
        self.assertEqual(rows[0]["top_sym"], "A")

    def test_the_day_is_the_day_of_the_opening_not_of_the_closing(self):
        first, last = self.D0, self.D0 + 2 * DAY_NS - 1
        t = trade("A", 1.0, self.D0 + DAY_NS - 500_000_000)                                      # opens 23:59:59.5, closes next day
        self.assertEqual(t["close_ns"] // DAY_NS, self.D0 // DAY_NS + 1)
        self.assertEqual([r["n"] for r in ps.daily([t], first, last)], [1, 0])

    def test_a_day_without_trades_is_still_listed_so_quiet_days_are_visible(self):
        first, last = self.D0, self.D0 + 3 * DAY_NS - 1
        rows = ps.daily([trade("A", 5.0, self.D0 + 3600 * 10**9), trade("A", 5.0, self.D0 + 2 * DAY_NS + 5)], first, last)
        self.assertEqual([r["n"] for r in rows], [1, 0, 1])
        self.assertEqual(rows[1]["usd"], 0.0)
        self.assertIsNone(rows[1]["top_sym"])

    def test_no_data_no_rows(self):
        self.assertEqual(ps.daily([], None, None), [])


class ExtendedReportTest(Base):
    def _trades(self):
        self.rec.add("binance-0", 1000, bn("BTCUSDT", 100.50, 10, 100.52, 10))
        self.rec.add("binance-0", 1500, bn("BTCUSDT", *BN_FLAT))
        self.rec.add("binance-0", 2000, bn("BTCUSDT", *BN_FLAT))

    def test_the_new_sections_are_in_the_report(self):
        self._trades()
        txt = ps.report(self.run_(extra_latency_ms=(25.0,)))
        for s in ("## 3b. Rincian per hari", "2026-11-16", "## 3c. Konsentrasi", "10 posisi terbesar",
                  "## 3d. Sensitivitas latensi", "| 25 |", "## 3e. Posisi terbesar"):
            self.assertIn(s, txt)

    def test_largest_positions_are_listed_largest_first_and_capped_at_ten(self):
        self._trades()
        res = self.run_()
        res["trades"] = [trade("S%02d" % i, float(i), tpa.BASE_NS) for i in range(1, 13)]
        res["sens"][0]["trades"] = res["trades"]
        sec = ps.report(res).split("## 3e.")[1]
        rows = [ln for ln in sec.splitlines() if re.match(r"\| S\d\d ", ln)]                    # not the "| Simbol |" header
        self.assertEqual(len(rows), 10)
        self.assertTrue(rows[0].startswith("| S12 ") and rows[-1].startswith("| S03 "), rows)

    def test_the_report_survives_no_positions(self):
        self.rec.add("binance-0", 1000, bn("BTCUSDT", 100.20, 10, 100.22, 10))
        self.rec.add("binance-0", 2000, bn("BTCUSDT", *BN_FLAT))
        txt = ps.report(self.run_(extra_latency_ms=(25.0, 50.0)))
        self.assertIn("## 3d. Sensitivitas latensi", txt)
        self.assertNotIn("Traceback", txt)

    def test_cli_also_latency_ms(self):
        self._trades()
        root = self.rec.write()
        out = subprocess.run([sys.executable, TOOL, "--data", root, "--all-feeds", "--close-bps", "5",
                              "--also-latency-ms", "25,50"], capture_output=True, text=True, timeout=60)
        self.assertEqual(out.returncode, 0, out.stderr)
        self.assertIn("| 50 |", out.stdout)
        for arg in ("x", "0", "25,-5"):
            bad = subprocess.run([sys.executable, TOOL, "--data", root, "--also-latency-ms", arg], capture_output=True,
                                 text=True, timeout=60)
            self.assertNotEqual(bad.returncode, 0, arg)


if __name__ == "__main__":
    unittest.main()
