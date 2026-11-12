"""
Tests for tools/spread_scanner.py (stdlib only).

Parsers are fed fixtures shaped like each venue's documented / assumed answer; the analysis is checked exactly on
hand-built rounds; one end-to-end run records from a local HTTPS mock of all 8 venues (one of them failing) and reports.

  python3 -m unittest discover -s tools/tests -v
"""
from __future__ import annotations

import http.server
import json
import os
import shutil
import ssl
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import urllib.parse

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import md_recorder as md  # noqa: E402
import spread_scanner as sc  # noqa: E402

FEES = dict(sc.FEES_BPS)

# ── venue fixtures: BTC 60000/60001 everywhere except where a test needs a gap ─────────────────────────────────────
FIX = {
    "/api/v3/ticker/bookTicker": [  # Binance and MEXC
        {"symbol": "BTCUSDT", "bidPrice": "60000.00", "bidQty": "1.5", "askPrice": "60001.00", "askQty": "2"},
        {"symbol": "ETHUSDT", "bidPrice": "3000.0", "bidQty": "10", "askPrice": "3000.1", "askQty": "10"},
        {"symbol": "ETHBTC", "bidPrice": "0.05", "bidQty": "1", "askPrice": "0.0501", "askQty": "1"},       # not USDT
        {"symbol": "DEADUSDT", "bidPrice": "0", "bidQty": "0", "askPrice": "0", "askQty": "0"},               # empty book
        {"symbol": "XUSDT", "bidPrice": "2", "bidQty": "1", "askPrice": "1", "askQty": "1"},                  # crossed
    ],
    "/api/v2/spot/market/tickers": {"code": "00000", "data": [
        {"symbol": "BTCUSDT", "bidPr": "60100", "bidSz": "0.5", "askPr": "60101", "askSz": "0.4", "ts": "1762870000000"},
        {"symbol": "ETHUSDT", "bidPr": "3000.0", "bidSz": "3", "askPr": "3000.2", "askSz": "3", "ts": "1762870000000"}]},
    "/v5/market/tickers": {"retCode": 0, "result": {"category": "spot", "list": [
        {"symbol": "BTCUSDT", "bid1Price": "60000", "bid1Size": "1", "ask1Price": "60002", "ask1Size": "1"}]},
        "time": 1762870000123},
    "/api/v5/market/tickers": {"code": "0", "data": [
        {"instId": "BTC-USDT", "bidPx": "60000", "bidSz": "1", "askPx": "60001", "askSz": "1", "ts": "1762870000001"},
        {"instId": "BTC-USDC", "bidPx": "1", "bidSz": "1", "askPx": "2", "askSz": "1", "ts": "1"}]},
    "/api/v4/spot/tickers": [
        {"currency_pair": "BTC_USDT", "highest_bid": "59999", "lowest_ask": "60000", "highest_size": "1", "lowest_size": "1"},
        {"currency_pair": "ETH_USDT", "highest_bid": "3000", "lowest_ask": "3000.5"}],                        # no sizes
    "/api/v1/market/allTickers": {"code": "200000", "data": {"time": 1762870000002, "ticker": [
        {"symbol": "BTC-USDT", "buy": "60000", "sell": "60001", "bestBidSize": "1", "bestAskSize": "1"}]}},
    "/0/public/AssetPairs": {"error": [], "result": {
        "XBTUSDT": {"altname": "XBTUSDT", "wsname": "XBT/USDT", "status": "online"},
        "XXBTZUSD": {"altname": "XBTUSD", "wsname": "XBT/USD", "status": "online"}}},
    "/0/public/Ticker": {"error": [], "result": {
        "XBTUSDT": {"a": ["60001.0", "1", "1.000"], "b": ["60000.0", "2", "2.000"]},
        "XXBTZUSD": {"a": ["60001.0", "1", "1.000"], "b": ["60000.0", "2", "2.000"]}}},
}


class ParserTests(unittest.TestCase):
    def test_binance_like_keeps_only_valid_usdt_books(self):
        q, ts = sc.parse_binance_like(FIX["/api/v3/ticker/bookTicker"], "USDT")
        self.assertEqual(sorted(q), ["BTC", "ETH"])                      # ETHBTC, empty and crossed books dropped
        self.assertEqual(q["BTC"], [60000.0, 1.5, 60001.0, 2.0])
        self.assertIsNone(ts)

    def test_bitget(self):
        q, ts = sc.parse_bitget(FIX["/api/v2/spot/market/tickers"], "USDT")
        self.assertEqual(q["BTC"], [60100.0, 0.5, 60101.0, 0.4])
        self.assertEqual(ts, 1762870000000)

    def test_bybit(self):
        q, ts = sc.parse_bybit(FIX["/v5/market/tickers"], "USDT")
        self.assertEqual(q, {"BTC": [60000.0, 1.0, 60002.0, 1.0]})
        self.assertEqual(ts, 1762870000123)

    def test_okx_filters_the_quote(self):
        q, ts = sc.parse_okx(FIX["/api/v5/market/tickers"], "USDT")
        self.assertEqual(list(q), ["BTC"])
        self.assertEqual(ts, 1762870000001)

    def test_gate_without_sizes_keeps_the_quote_with_unknown_size(self):
        q, _ = sc.parse_gate(FIX["/api/v4/spot/tickers"], "USDT")
        self.assertEqual(q["BTC"], [59999.0, 1.0, 60000.0, 1.0])
        self.assertEqual(q["ETH"], [3000.0, None, 3000.5, None])

    def test_kucoin(self):
        q, ts = sc.parse_kucoin(FIX["/api/v1/market/allTickers"], "USDT")
        self.assertEqual(q, {"BTC": [60000.0, 1.0, 60001.0, 1.0]})
        self.assertEqual(ts, 1762870000002)

    def test_kraken_maps_legacy_codes_through_asset_pairs(self):
        meta = sc.parse_kraken_pairs(FIX["/0/public/AssetPairs"])
        self.assertEqual(meta["XBTUSDT"], ("BTC", "USDT"))
        q, _ = sc.parse_kraken(FIX["/0/public/Ticker"], "USDT", meta)
        self.assertEqual(q, {"BTC": [60000.0, 2.0, 60001.0, 1.0]})       # the USD pair is not USDT

    def test_garbage_never_raises(self):
        for v in sc.default_venues():
            for junk in (None, [], {}, "x", {"data": None}, {"result": {"list": [{"symbol": 1}]}}, [{"symbol": None}]):
                q, _ = v.parse(junk, "USDT", {})
                self.assertEqual(q, {}, v.name)


def snap(q: dict, t0: int = 0) -> dict:
    return {"t0": t0, "t1": t0 + 50_000_000, "vt": None, "q": q}


def write_rounds(path: str, rounds: list, interval: float = 5.0, t0: int = 1_800_000_000 * 10**9) -> list[str]:
    w = md.HourlyWriter(path, "rounds", flush_s=0)
    w.meta(t0, "start", interval_s=interval)
    for i, venues in enumerate(rounds):
        t = t0 + int(i * interval * 1e9)
        w.write(t, json.dumps({"t": t, "v": venues}))
    w.close()
    return sc.round_files(path, None)


class AnalysisTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.dir, ignore_errors=True)

    def test_net_gap_after_both_taker_fees(self):
        # buy A at 100, sell B at 100.3: gross 30 bps, fees 10 + 10 → net 10 bps; size min(100*5, 100.3*2) = 200.6
        snaps = {"a": snap({"X": [99.9, 5, 100.0, 5]}), "b": snap({"X": [100.3, 2, 100.4, 2]})}
        gaps = list(sc.pair_gaps(snaps, {"a": 10, "b": 10}))
        self.assertEqual(len(gaps), 1)
        b, buy, sell, net, gross, size = gaps[0]
        self.assertEqual((b, buy, sell), ("X", "a", "b"))
        self.assertAlmostEqual(gross, 30.0, places=6)
        self.assertAlmostEqual(net, 10.0, places=6)
        self.assertAlmostEqual(size, 200.6, places=6)

    def test_episode_needs_consecutive_snapshots_and_fees_matter(self):
        gap = {"a": snap({"X": [99.9, 5, 100.0, 5]}), "b": snap({"X": [100.3, 2, 100.4, 2]})}
        flat = {"a": snap({"X": [99.9, 5, 100.0, 5]}), "b": snap({"X": [99.95, 2, 100.05, 2]})}
        files = write_rounds(self.dir, [gap, flat, gap, gap, gap, flat])
        a = sc.analyse(files, {"a": 10, "b": 10}, min_usd=50)
        (ep,) = a["episodes"]                                            # the 1-snapshot gap is only counted
        self.assertEqual(ep.rounds, 3)
        self.assertEqual(a["blips"]["X a→b"], 1)
        self.assertEqual(sc.classify_episode(ep, a["interval"], 2, 10), "stale")   # 3 identical snapshots: frozen
        self.assertEqual(sc.analyse(files, {"a": 20, "b": 20})["episodes"], [])   # 30 bps gross < 40 bps fees

    def test_moving_quotes_make_an_ok_episode_with_profit_taken_once(self):
        rounds = []
        for i, sell_bid in enumerate([100.3, 100.31, 100.32, 100.3]):
            rounds.append({"a": snap({"X": [99.9, 5, 100.0, 5]}), "b": snap({"X": [sell_bid, 2, sell_bid + 0.1, 2]})})
        rounds.append({"a": snap({"X": [99.9, 5, 100.0, 5]}), "b": snap({"X": [99.95, 2, 100.05, 2]})})
        a = sc.analyse(write_rounds(self.dir, rounds), {"a": 10, "b": 10})
        (ep,) = a["episodes"]
        self.assertEqual(sc.classify_episode(ep, 5.0, 2, 10), "ok")
        self.assertEqual(ep.rounds, 4)
        self.assertAlmostEqual(ep.duration_s(5.0), 20.0)
        self.assertAlmostEqual(ep.best_usd, (12.0 / 1e4) * min(500, 100.32 * 2), places=6)   # best snapshot, once
        text = sc.report(a)
        self.assertIn("a ↔ b", text)
        self.assertIn("| X | a → b |", text)

    def test_a_missing_venue_ends_the_episode_instead_of_bridging(self):
        gap = lambda bid: {"a": snap({"X": [99.9, 5, 100.0, 5]}), "b": snap({"X": [bid, 2, bid + 0.1, 2]})}  # noqa: E731
        down = {"a": snap({"X": [99.9, 5, 100.0, 5]}), "b": {"t0": 0, "t1": 0, "err": "HTTP 503"}}
        a = sc.analyse(write_rounds(self.dir, [gap(100.3), gap(100.31), down, gap(100.32), gap(100.33)]), {"a": 10, "b": 10})
        self.assertEqual(sorted(e.rounds for e in a["episodes"]), [2, 2])
        self.assertEqual(a["venue_fail"]["b"], 1)

    def test_identity_suspects_are_excluded(self):
        # venue c quotes "X" at 3x the others: a different token behind the same ticker. No episode may use it.
        r = {"a": snap({"X": [99.9, 5, 100.0, 5]}), "b": snap({"X": [99.95, 5, 100.05, 5]}),
             "c": snap({"X": [300.0, 5, 300.1, 5]})}
        a = sc.analyse(write_rounds(self.dir, [r, r, r]), FEES | {"a": 10, "b": 10, "c": 10})
        self.assertEqual(a["episodes"], [])
        self.assertEqual(a["suspect_bases"]["X@c"], 3)

    def test_only_the_outlier_venue_is_dropped_when_the_others_agree(self):
        # First md-tokyo run: RON on Bitget ~1640x the price on MEXC/OKX showed as "+16,410,477 bps" in the live view.
        # The outlier must go, but a real gap between the venues that agree must still be measured.
        rounds = [{"a": snap({"X": [99.9, 5, 100.0, 5]}), "b": snap({"X": [100.3 + i * .01, 5, 100.4 + i * .01, 5]}),
                   "c": snap({"X": [164000.0, 5, 164001.0, 5]})} for i in range(3)]
        a = sc.analyse(write_rounds(self.dir, rounds), {"a": 10, "b": 10, "c": 10})
        (ep,) = a["episodes"]
        self.assertEqual((ep.buy, ep.sell, ep.rounds), ("a", "b", 3))
        self.assertEqual(a["suspect_bases"]["X@c"], 3)
        live = sc.live_top(rounds[0], {"a": 10, "b": 10, "c": 10}, 3)
        self.assertEqual(len(live), 1)
        self.assertIn("X buy a→sell b", live[0])

    def test_two_venues_that_disagree_are_both_dropped(self):
        r = {"a": snap({"X": [99.9, 5, 100.0, 5]}), "b": snap({"X": [149.9, 5, 150.0, 5]})}
        self.assertEqual(sc.suspect_quotes(r, 0.10), {("X", "a"), ("X", "b")})
        self.assertEqual(sc.live_top(r, {"a": 10, "b": 10}, 3), [])

    def test_long_lived_gaps_are_flagged_not_counted(self):
        rounds = [{"a": snap({"X": [99.9, 5, 100.0, 5]}), "b": snap({"X": [100.3 + i * 1e-4, 2, 100.5, 2]})} for i in range(200)]
        a = sc.analyse(write_rounds(self.dir, rounds), {"a": 10, "b": 10})      # 200 × 5 s = 16.7 min > 10 min
        (ep,) = a["episodes"]
        self.assertEqual(sc.classify_episode(ep, 5.0, 2, 10), "long")
        self.assertIn("(tidak ada episode yang lolos)", sc.report(a))

    def test_b06_a_persistent_gap_broken_up_by_fetch_failures_is_not_many_ok_episodes(self):
        # 25 minutes of a steady ~6 % gap (deposits closed), with venue b failing every 6th round. Episodes never
        # bridge a failed round, so each lasted 25 s and all of them counted as tradable.
        rounds = []
        for i in range(300):
            if i % 6 == 5:
                rounds.append({"a": snap({"X": [99.9, 5, 100.0, 5]}), "b": {"t0": 0, "t1": 0, "err": "HTTP 503"}})
            else:
                bid = 106.0 + (i % 7) * 0.01
                rounds.append({"a": snap({"X": [99.9, 5, 100.0, 5]}), "b": snap({"X": [bid, 2, bid + .1, 2]})})
        a = sc.analyse(write_rounds(self.dir, rounds), {"a": 10, "b": 10})
        kinds = sc.episode_kinds(a)
        self.assertEqual(len(kinds["ok"]), 0)
        self.assertEqual(len(kinds["long"]), 50)
        self.assertIn("(tidak ada episode yang lolos)", sc.report(a))

    def test_b06_short_gaps_that_recur_a_few_times_stay_tradable(self):
        flat = {"a": snap({"X": [99.9, 5, 100.0, 5]}), "b": snap({"X": [99.95, 2, 100.05, 2]})}
        rounds = []
        for burst in range(3):
            rounds += [{"a": snap({"X": [99.9, 5, 100.0, 5]}), "b": snap({"X": [100.3 + k * .01, 2, 100.5, 2]})} for k in range(4)]
            rounds += [flat] * 40
        a = sc.analyse(write_rounds(self.dir, rounds), {"a": 10, "b": 10})
        self.assertEqual(len(sc.episode_kinds(a)["ok"]), 3)

    def test_b07_a_very_long_episode_keeps_bounded_state(self):
        rounds = [{"a": snap({"X": [99.9, 5, 100.0, 5]}), "b": snap({"X": [100.3 + (i % 50) * .01, 2, 101.0, 2]})}
                  for i in range(3000)]
        a = sc.analyse(write_rounds(self.dir, rounds), {"a": 10, "b": 10})
        (ep,) = a["episodes"]
        self.assertEqual(ep.rounds, 3000)
        self.assertEqual((ep.nets, ep.sizes, ep.legs), ([], [], set()))        # samples freed once the episode ended
        self.assertEqual(ep.legs_n, 2)                                         # "more than one distinct quote" is kept
        self.assertAlmostEqual(ep.net_med, 34.0, places=4)                     # median of the first EP_SAMPLES snapshots
        self.assertAlmostEqual(ep.net_max, (100.79 / 100.0 - 1) * 1e4 - 20, places=6)   # the maximum is still exact
        self.assertAlmostEqual(ep.best_usd, ep.net_max / 1e4 * min(500, 100.79 * 2), places=6)

    def test_b10_report_shows_direction_balance_and_quote_freshness(self):
        up = lambda k: {"a": snap({"X": [99.9, 5, 100.0, 5], "BTC": [60000 + k, 1, 60001 + k, 1]}),      # noqa: E731
                        "b": snap({"X": [100.3 + k * .01, 2, 100.5, 2], "BTC": [60000, 1, 60001, 1]})}
        flat = {"a": snap({"X": [99.9, 5, 100.0, 5], "BTC": [60050, 1, 60051, 1]}),
                "b": snap({"X": [99.95, 2, 100.05, 2], "BTC": [60000, 1, 60001, 1]})}
        a = sc.analyse(write_rounds(self.dir, [up(0), up(1), up(2), flat, up(3), up(4), flat]), {"a": 10, "b": 10})
        self.assertEqual(a["fresh"]["a"], [6, 6])                 # venue a's BTC quote changed in all 6 follow-up rounds
        self.assertEqual(a["fresh"]["b"], [0, 6])                 # venue b serves the same BTC quote every time
        text = sc.report(a)
        self.assertIn("| a | 7 | 0 |", text)
        self.assertRegex(text, r"\| b \| 7 \| 0 \| \d+ \| 10 \| 0 % \|")          # frozen reference quote is visible
        self.assertRegex(text, r"\| a ↔ b \|.*\| 100 % a→b \|")                    # every dollar in one direction

    def test_b10_direction_share_names_the_side_that_dominates(self):
        ab = lambda k: {"a": snap({"X": [99.9, 5, 100.0, 5]}), "b": snap({"X": [100.3 + k * .01, 2, 100.5, 2]})}   # noqa: E731
        ba = lambda k: {"b": snap({"X": [99.9, 5, 100.0, 5]}), "a": snap({"X": [100.3 + k * .01, 5, 100.5, 5]})}   # noqa: E731
        flat = {"a": snap({"X": [99.9, 5, 100.0, 5]}), "b": snap({"X": [99.95, 2, 100.05, 2]})}
        a = sc.analyse(write_rounds(self.dir, [ab(0), ab(1), flat, ba(0), ba(1), flat]), {"a": 10, "b": 10})
        usd = {f"{e.buy}→{e.sell}": e.best_usd for e in a["episodes"]}
        share = round(100 * usd["b→a"] / sum(usd.values()))
        self.assertGreater(share, 60)                              # b→a has 2.5x the top-of-book size
        self.assertIn(f"| {share} % b→a |", sc.report(a))

    def test_top_of_book_below_min_usd_is_ignored_and_unknown_size_is_separate(self):
        tiny = {"a": snap({"X": [99.9, 0.1, 100.0, 0.1]}), "b": snap({"X": [100.3, 0.1, 100.4, 0.1]})}  # $10
        self.assertEqual(sc.analyse(write_rounds(self.dir, [tiny, tiny]), {"a": 10, "b": 10})["episodes"], [])
        shutil.rmtree(self.dir)
        os.makedirs(self.dir)
        nosz = lambda bid: {"a": snap({"X": [99.9, None, 100.0, None]}), "b": snap({"X": [bid, 2, bid + .1, 2]})}  # noqa: E731
        a = sc.analyse(write_rounds(self.dir, [nosz(100.3), nosz(100.31)]), {"a": 10, "b": 10})
        (ep,) = a["episodes"]
        self.assertEqual(sc.classify_episode(ep, 5.0, 2, 10), "nosize")

    def test_keep_shared_drops_bases_only_one_venue_quotes(self):
        s = sc.keep_shared({"a": snap({"X": [1, 1, 2, 1], "ONLY": [1, 1, 2, 1]}), "b": snap({"X": [1, 1, 2, 1]}),
                            "c": {"err": "x"}})
        self.assertEqual(list(s["a"]["q"]), ["X"])


class _Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"                                        # keep-alive, like the real venues
    fail = set()                                                         # paths answering 503
    slow = {}                                                            # path -> seconds to wait before answering
    hits = {}                                                            # path -> requests received
    gap = {"on": True}

    def log_message(self, *a):
        pass

    def do_GET(self):
        path = urllib.parse.urlsplit(self.path).path
        _Handler.hits[path] = _Handler.hits.get(path, 0) + 1
        time.sleep(self.slow.get(path, 0))
        if path in self.fail:
            self.send_response(503)
            self.send_header("Content-Length", "11")
            self.end_headers()
            self.wfile.write(b"maintenance")
            return
        body = FIX.get(path)
        if path == "/api/v2/spot/market/tickers":                      # Bitget: BTC 100 bps above, moving
            px = 60600 + time.time() % 1 if self.gap["on"] else 60000
            body = {"code": "00000", "data": [{"symbol": "BTCUSDT", "bidPr": str(px), "bidSz": "0.5",
                                               "askPr": str(px + 1), "askSz": "0.5", "ts": "1"}]}
        if body is None:
            self.send_response(404)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        data = json.dumps(body).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


@unittest.skipIf(shutil.which("openssl") is None, "openssl CLI needed for the self-signed test certificate")
class EndToEndTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp()
        cert, key = os.path.join(cls.tmp, "c.pem"), os.path.join(cls.tmp, "k.pem")
        subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1", "-subj", "/CN=127.0.0.1",
                        "-keyout", key, "-out", cert], check=True, capture_output=True)
        cls.srv = http.server.ThreadingHTTPServer(("127.0.0.1", 0), _Handler)
        sctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        sctx.load_cert_chain(cert, key)
        cls.srv.socket = sctx.wrap_socket(cls.srv.socket, server_side=True)
        threading.Thread(target=cls.srv.serve_forever, daemon=True).start()
        cls.ctx = ssl.create_default_context()
        cls.ctx.check_hostname = False
        cls.ctx.verify_mode = ssl.CERT_NONE

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()
        cls.srv.server_close()
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def venues(self):
        vs = sc.default_venues()
        for v in vs:
            v.host, v.port = "127.0.0.1", self.srv.server_address[1]
        return vs

    def test_records_all_venues_isolates_a_failing_one_and_reports(self):
        _Handler.fail = {"/api/v1/market/allTickers"}                   # KuCoin down for the whole run
        out = os.path.join(self.tmp, "run1")
        stop, logs = threading.Event(), []
        threading.Timer(3.6, stop.set).start()
        code = sc.scan(self.venues(), out, self.ctx, stop, interval=0.5, status_s=1.0, log=logs.append, timeout=2.0)
        self.assertEqual(code, 0)
        a = sc.analyse(sc.round_files(out, None), FEES, min_usd=50)
        self.assertGreaterEqual(a["rounds"], 3)
        self.assertEqual(a["venue_ok"]["kucoin"], 0)
        self.assertEqual(a["venue_fail"]["kucoin"], a["rounds"])
        # the mock serves all 8 venues from one acceptor, so the first round's TLS handshakes queue up: a venue may miss
        # the first rounds as "slower than" / "previous fetch still running", never with any other error
        for v in ("binance", "bitget", "bybit", "okx", "mexc", "gate", "kraken"):
            self.assertGreaterEqual(a["venue_ok"][v], 2, v)
        errs = [p for f in sc.round_files(out, None) for _, p in md.read_file(f) if not p.startswith("#")]
        for r in map(json.loads, errs):
            for v, s in r["v"].items():
                if "err" in s and v != "kucoin":
                    self.assertTrue(s["err"].startswith(("slower than", "previous fetch")), (v, s["err"]))
        ok = [e for e in a["episodes"] if sc.classify_episode(e, a["interval"], 2, 10) == "ok"]
        self.assertTrue(ok)
        self.assertTrue(all(e.base == "BTC" and e.sell == "bitget" for e in ok))
        self.assertTrue(any("kucoin error" in l and "503" in l for l in logs))
        self.assertTrue(any(l.startswith("  now:") and "BTC buy" in l for l in logs))
        self.assertIn("↔ bitget", sc.report(a))

    def test_probe_reports_each_venue(self):
        _Handler.fail = {"/0/public/Ticker"}
        logs = []
        self.assertEqual(sc.probe(self.venues(), self.ctx, log=logs.append), 1)
        self.assertTrue(any(l.startswith("FAIL kraken") for l in logs))
        self.assertEqual(sum(l.startswith("OK") for l in logs), 7)
        self.assertTrue(any(l.startswith("OK   binance") and "BTC bid 60000" in l for l in logs))

    def test_a_slow_venue_is_never_asked_twice_at_once(self):
        # Kraken answers in 1.3 s while rounds are 0.5 s apart: one request at a time on its connection, the rounds in
        # between are recorded as busy, and the other venues are not held up.
        _Handler.fail, _Handler.slow, _Handler.hits = set(), {"/0/public/Ticker": 1.3}, {}
        self.addCleanup(setattr, _Handler, "slow", {})
        out = os.path.join(self.tmp, "run3")
        stop = threading.Event()
        threading.Timer(4.2, stop.set).start()
        sc.scan(self.venues(), out, self.ctx, stop, interval=0.5, status_s=10, log=lambda m: None, timeout=3.0)
        a = sc.analyse(sc.round_files(out, None), FEES)
        self.assertLessEqual(_Handler.hits.get("/0/public/Ticker", 0), 4)          # ≤ 4.2 s / 1.3 s, never one per round
        self.assertGreaterEqual(a["venue_fail"]["kraken"], a["rounds"] // 2)
        self.assertGreaterEqual(a["venue_ok"]["binance"], a["rounds"] - 2)

    def test_disk_guard(self):
        _Handler.fail = set()
        stop = threading.Event()
        threading.Timer(10, stop.set).start()
        code = sc.scan(self.venues(), os.path.join(self.tmp, "run2"), self.ctx, stop, interval=0.5,
                       free_bytes=lambda p: 10**9, min_free_gb=5, log=lambda m: None)
        self.assertEqual(code, sc.EXIT_DISK_FULL)

    def test_cli(self):
        for argv in (["--venues", "binance"], ["--venues", "binance,ftx"], ["--interval", "0.2"], ["--fee", "kraken=-1"],
                     ["--fee", "nope=10"], ["--report", ".", "--last-hours", "0"], ["--hours", "0"]):
            with self.assertRaises(SystemExit, msg=argv):
                sc.main(argv)


if __name__ == "__main__":
    unittest.main()
