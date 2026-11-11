"""
Tests for tools/md_recorder.py against a local TLS mock of both venues (stdlib only).

  Binance  /stream?streams=a/b/c   server ping first; combined-stream messages {"stream":..,"data":..}; one message
                                   trickled byte by byte across many poll timeouts; one fragmented message with a ping
                                   between the fragments; one 70 kB message (64-bit length); optional close / silence.
  Bitget   /v2/ws/public           subscribe -> {"event":"subscribe"} or {"event":"error"} (BADUSDT); "ping" -> "pong";
                                   snapshot pushes {"action":"snapshot","arg":{..},"data":[{..,"ts":".."}]}.
  REST     /api/v3/exchangeInfo?symbol=X and /api/v2/spot/public/symbols?symbol=X for the preflight.

  python3 -m unittest discover -s tools/tests -v
"""
from __future__ import annotations

import base64
import gzip
import hashlib
import json
import os
import shutil
import socket
import ssl
import struct
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import urllib.parse

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import latency_probe as lp  # noqa: E402
import md_recorder as md  # noqa: E402

BINANCE_LISTED = {"BTCUSDT": "TRADING", "ETHUSDT": "TRADING", "OLDUSDT": "BREAK", "ONLYBGUSDT": None}
BITGET_LISTED = {"BTCUSDT": "online", "ETHUSDT": "online", "OLDUSDT": "online", "ONLYBNUSDT": None, "HALTUSDT": "halt"}
# C-13: answers that do NOT prove a symbol is unlisted (status, body); body None = non-JSON
BITGET_ODD = {
    "NONJSONUSDT": (400, None),
    "SYSERRUSDT": (400, {"code": "40015", "msg": "System is abnormal, please try again later"}),
    "ENVERRUSDT": (200, {"code": "40015", "data": []}),
    "PARAMUSDT": (400, {"code": "40034", "msg": "Parameter does not exist"}),
}
BINANCE_ODD = {
    "WRONGROWUSDT": (200, {"symbols": [{"symbol": "OTHERUSDT", "status": "BREAK"}]}),
    "B400USDT": (400, {"code": -1100, "msg": "Illegal characters found in a parameter."}),
}


def _frame(opcode: int, payload: bytes, fin: bool = True) -> bytes:
    n = len(payload)
    head = bytes([(0x80 if fin else 0) | opcode])
    if n < 126:
        head += bytes([n])
    elif n < 65536:
        head += bytes([126]) + struct.pack("!H", n)
    else:
        head += bytes([127]) + struct.pack("!Q", n)
    return head + payload


class _ClientFrames:
    """Server side: buffered parse of masked client frames from a socket with a short timeout."""

    def __init__(self, c, buf: bytes):
        self.c, self.buf = c, bytearray(buf)

    def poll(self) -> list[tuple[int, bytes]]:
        try:
            chunk = self.c.recv(65536)
            if not chunk:
                raise OSError("client closed")
            self.buf += chunk
        except (socket.timeout, ssl.SSLWantReadError):
            pass
        out = []
        while len(self.buf) >= 6:
            n, h = self.buf[1] & 0x7F, 2
            if n == 126:
                n, h = struct.unpack("!H", bytes(self.buf[2:4]))[0], 4
            elif n == 127:
                n, h = struct.unpack("!Q", bytes(self.buf[2:10]))[0], 10
            if len(self.buf) < h + 4 + n:
                break
            mask = bytes(self.buf[h:h + 4])
            data = bytes(b ^ mask[i % 4] for i, b in enumerate(self.buf[h + 4:h + 4 + n]))
            out.append((self.buf[0] & 0x0F, data))
            del self.buf[:h + 4 + n]
        return out


class MockVenues:
    def __init__(self, certfile: str, keyfile: str):
        self.ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        self.ctx.load_cert_chain(certfile, keyfile)
        self.sock = socket.socket()
        self.sock.bind(("127.0.0.1", 0))
        self.sock.listen(64)
        self.port = self.sock.getsockname()[1]
        self.reset()
        self.running = True
        self._acceptor = threading.Thread(target=self._accept, daemon=True)
        self._acceptor.start()

    def reset(self, **cfg):
        self.cfg = {"close_after": None, "silent_after": None, "period_s": 0.004, "special": False, **cfg}
        self.binance_pongs: list[bytes] = []
        self.bitget_pings: list[float] = []
        self.bitget_subs: list[dict] = []
        self.bitget_sub_ns: list[int] = []
        self.ws_sessions = {"binance": 0, "bitget": 0}

    def stop(self):
        # Wake the acceptor and wait for it BEFORE closing: otherwise it can call accept() on a reused fd number and
        # serve the NEXT test's listener (seen under parallel load; see docs/STAGE2_RECORDER.md §5).
        self.running = False
        try:
            self.sock.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        self._acceptor.join(5)
        self.sock.close()

    def _accept(self):
        while self.running:
            try:
                c, _ = self.sock.accept()
            except OSError:
                return
            threading.Thread(target=self._serve, args=(c,), daemon=True).start()

    def _serve(self, raw):
        try:
            c = self.ctx.wrap_socket(raw, server_side=True)
        except (ssl.SSLError, OSError):
            raw.close()
            return
        try:
            buf = b""
            while b"\r\n\r\n" not in buf:
                chunk = c.recv(4096)
                if not chunk:
                    return
                buf += chunk
            head, _, buf = buf.partition(b"\r\n\r\n")
            lines = head.decode().split("\r\n")
            target = lines[0].split(" ")[1]
            headers = {k.strip().lower(): v.strip() for k, _, v in (l.partition(":") for l in lines[1:])}
            url = urllib.parse.urlsplit(target)
            if headers.get("upgrade", "").lower() == "websocket":
                key = headers["sec-websocket-key"]
                accept = base64.b64encode(hashlib.sha1((key + lp.WS_GUID).encode()).digest()).decode()
                c.sendall(("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                           f"Sec-WebSocket-Accept: {accept}\r\n\r\n").encode())
                c.settimeout(0.002)
                if url.path == "/stream":
                    self._binance(c, buf, urllib.parse.parse_qs(url.query)["streams"][0].split("/"))
                else:
                    self._bitget(c, buf)
                return
            self._rest(c, url)
        except (OSError, ssl.SSLError):
            pass
        finally:
            try:
                c.close()
            except OSError:
                pass

    def _rest(self, c, url):
        q = urllib.parse.parse_qs(url.query)
        sym = q.get("symbol", [""])[0]
        if url.path == "/api/v3/exchangeInfo":
            st = BINANCE_LISTED.get(sym, "TRADING" if sym in BITGET_ODD else None)
            status, body = BINANCE_ODD.get(sym) or ((200, {"symbols": [{"symbol": sym, "status": st}]}) if st else (400, {"code": -1121, "msg": "Invalid symbol."}))
        elif url.path == "/api/v2/spot/public/symbols":
            st = BITGET_LISTED.get(sym, "online" if sym in BINANCE_ODD else None)
            # unknown symbol: authoritative success with no row (C-13: the only answer that may DROP a symbol)
            status, body = BITGET_ODD.get(sym) or ((200, {"code": "00000", "data": [{"symbol": sym, "status": st}]}) if st else (200, {"code": "00000", "data": []}))
        else:
            status, body = 404, {}
        data = b"<html>bad gateway</html>" if body is None else json.dumps(body).encode()
        c.sendall(f"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {len(data)}\r\nConnection: close\r\n\r\n".encode() + data)

    def _binance(self, c, buf, streams):
        self.ws_sessions["binance"] += 1
        frames = _ClientFrames(c, buf)
        c.sendall(_frame(0x9, b"binance-ping-payload"))
        sent, special = 0, self.cfg["special"] and self.ws_sessions["binance"] == 1
        while True:
            for op, data in frames.poll():
                if op == 0xA:
                    self.binance_pongs.append(data)
                elif op == 0x8:
                    return
            if self.cfg["close_after"] is not None and sent >= self.cfg["close_after"]:
                c.sendall(_frame(0x8, struct.pack("!H", 1001) + b"going away"))
                return
            if self.cfg["silent_after"] is not None and sent >= self.cfg["silent_after"]:
                time.sleep(0.01)
                continue
            s = streams[sent % len(streams)]
            now = int(time.time() * 1000)
            body = {"e": "trade", "E": now - 3, "s": s.split("@")[0].upper(), "p": "1.0"} if s.endswith("@trade") else {"u": sent, "b": "1", "a": "2"}
            msg = json.dumps({"stream": s, "data": body}, separators=(",", ":")).encode()
            if special and sent == 0:                      # trickle the first message across many client poll timeouts
                f = _frame(0x1, msg)
                for i in range(len(f)):
                    c.sendall(f[i:i + 1])
                    time.sleep(0.003)
            elif special and sent == 1:                    # fragmented, with a server ping between the fragments
                c.sendall(_frame(0x1, msg[:10], fin=False) + _frame(0x9, b"mid") + _frame(0x0, msg[10:20], fin=False)
                          + _frame(0x0, msg[20:]))
            elif special and sent == 2:                    # 70 kB: 64-bit length
                big = json.dumps({"stream": s, "data": {"u": sent, "pad": "x" * 70_000}}, separators=(",", ":")).encode()
                c.sendall(_frame(0x1, big))
            else:
                c.sendall(_frame(0x1, msg))
            sent += 1
            time.sleep(self.cfg["period_s"])

    def _bitget(self, c, buf):
        self.ws_sessions["bitget"] += 1
        frames = _ClientFrames(c, buf)
        subscribed = []
        sent = 0
        while True:
            for op, data in frames.poll():
                if op == 0x8:
                    return
                text = data.decode()
                if text == "ping":
                    self.bitget_pings.append(time.monotonic())
                    c.sendall(_frame(0x1, b"pong"))
                    continue
                req = json.loads(text)
                self.bitget_sub_ns.append(time.time_ns())
                for arg in req["args"]:
                    self.bitget_subs.append(arg)
                    if arg["instId"] == "BADUSDT":
                        c.sendall(_frame(0x1, json.dumps({"event": "error", "arg": arg, "code": 30001, "msg": "instId:BADUSDT doesn't exist"}).encode()))
                    else:
                        c.sendall(_frame(0x1, json.dumps({"event": "subscribe", "arg": arg}).encode()))
                        subscribed.append(arg)
            if subscribed and not (self.cfg["silent_after"] is not None and sent >= self.cfg["silent_after"]):
                arg = subscribed[sent % len(subscribed)]
                now = int(time.time() * 1000)
                msg = {"action": "snapshot", "arg": arg, "data": [{"asks": [["1.01", "2"]], "bids": [["1.00", "3"]], "ts": str(now - 2)}], "ts": now}
                c.sendall(_frame(0x1, json.dumps(msg, separators=(",", ":")).encode()))
                sent += 1
            time.sleep(self.cfg["period_s"])


@unittest.skipIf(shutil.which("openssl") is None, "openssl CLI needed for the self-signed test certificate")
class RecorderTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp()
        cls.cert, cls.key = os.path.join(cls.tmp, "c.pem"), os.path.join(cls.tmp, "k.pem")
        subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1", "-subj", "/CN=127.0.0.1",
                        "-keyout", cls.key, "-out", cls.cert], check=True, capture_output=True)
        cls.mock = MockVenues(cls.cert, cls.key)
        cls.ctx = ssl.create_default_context()
        cls.ctx.check_hostname = False
        cls.ctx.verify_mode = ssl.CERT_NONE

    @classmethod
    def tearDownClass(cls):
        cls.mock.stop()
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def setUp(self):
        self.out = tempfile.mkdtemp(dir=self.tmp)
        self.logs: list[str] = []

    def _specs(self, symbols=("BTCUSDT", "ETHUSDT"), keepalive_s=0.2):
        specs = md.binance_specs(list(symbols), "127.0.0.1", self.mock.port)
        specs += md.bitget_specs(list(symbols), "127.0.0.1", self.mock.port)
        for s in specs:
            s.keepalive_s = keepalive_s
        return specs

    def _data_lines(self, conn: str) -> int:
        d = os.path.join(self.out, conn)
        if not os.path.isdir(d):
            return 0
        return sum(1 for f in os.listdir(d) for _, p in md.read_file(os.path.join(d, f)) if not p.startswith("#"))

    def _when(self, cond, then, timeout: float = 15.0):
        """Run then() once cond() holds (polled while recording), or after timeout: no fixed sleeps under CI load."""
        def watch():
            end = time.monotonic() + timeout
            try:
                while time.monotonic() < end and not cond():
                    time.sleep(0.05)
            finally:                                 # a crashing condition must stop the recording, not hang the test
                then()
        threading.Thread(target=watch, daemon=True).start()

    def _record(self, specs, until, timeout: float = 20.0, **kw):
        """Record until until() holds (checked while recording) or timeout."""
        stop = threading.Event()
        self._when(until, stop.set, timeout)
        kw.setdefault("free_bytes", lambda p: 10**12)
        kw.setdefault("status_s", 0.5)
        kw.setdefault("flush_s", 0.05)
        return md.record(specs, self.out, self.ctx, stop, poll_s=0.02, log=self.logs.append, **kw)

    def _conn(self, conn: str) -> dict:
        d = os.path.join(self.out, conn)
        return md.verify_conn(d) if os.path.isdir(d) and os.listdir(d) else {"sessions": 0, "streams": {}, "reasons": {}, "data": 0}

    def _all_streams_seen(self, *conns) -> bool:
        for c in conns:
            r = self._conn(c)
            if not r.get("expected") or any(r["streams"][k]["n"] == 0 for k in r["expected"]):
                return False
        return True

    # ── end to end ──────────────────────────────────────────────────────────────────────────────────────────────
    def test_records_every_stream_of_both_venues(self):
        self.mock.reset()
        self.assertEqual(self._record(self._specs(), lambda: self._all_streams_seen("binance-0", "bitget-0")), 0)
        text, reps = md.verify([self.out])
        by = {r["conn"]: r for r in reps}
        self.assertEqual(set(by), {"binance-0", "bitget-0"})
        for r in reps:
            self.assertEqual(len(r["expected"]), 6, r["conn"])                       # 2 symbols × 3 streams
            for k in r["expected"]:
                self.assertGreater(r["streams"][k]["n"], 0, f"{r['conn']} {k}")
            self.assertEqual(r["truncated"], 0)
            self.assertEqual(r["sessions"], 1)
        # trade streams carry venue time: data age = mock's 3 ms (+ local delivery), Bitget books 2 ms
        self.assertTrue(0 <= by["binance-0"]["streams"]["btcusdt@trade"]["lag_p50_ms"] <= 50)
        self.assertTrue(0 <= by["bitget-0"]["streams"]["books1:btcusdt"]["lag_p50_ms"] <= 50)
        self.assertIn("binance-0", text)

    def test_byte_trickle_fragments_and_70kB_frame_arrive_intact(self):
        self.mock.reset(special=True)
        stop = threading.Event()
        self._when(lambda: self._data_lines("binance-0") >= 4, stop.set)
        md.record(self._specs()[:1], self.out, self.ctx, stop, status_s=0.5, poll_s=0.02, flush_s=0.05,
                  free_bytes=lambda p: 10**12, log=self.logs.append)
        payloads = [p for f in sorted(os.listdir(os.path.join(self.out, "binance-0")))
                    for _, p in md.read_file(os.path.join(self.out, "binance-0", f)) if not p.startswith("#")]
        msgs = [json.loads(p) for p in payloads]                                     # every line is valid JSON
        self.assertGreaterEqual(len(msgs), 4, self.logs[-5:])
        self.assertEqual(msgs[0]["stream"], "btcusdt@bookTicker")                     # the trickled one, complete
        self.assertEqual(msgs[1]["stream"], "btcusdt@depth20@100ms")                  # the fragmented one, reassembled
        self.assertEqual(len(msgs[2]["data"]["pad"]), 70_000)                         # the 64-bit length one
        self.assertIn(b"binance-ping-payload", self.mock.binance_pongs)               # server pings answered
        self.assertIn(b"mid", self.mock.binance_pongs)

    def test_bitget_keepalive_acks_and_venue_errors(self):
        self.mock.reset()
        self._record(self._specs(("BTCUSDT", "BADUSDT"), keepalive_s=0.2)[1:],
                     lambda: len(self.mock.bitget_pings) >= 3 and self._conn("bitget-0")["data"] > 0)
        self.assertGreaterEqual(len(self.mock.bitget_pings), 3)                       # every 0.2 s, without traffic gaps
        self.assertTrue(any("BADUSDT" in l and "venue error" in l for l in self.logs))
        rep = md.verify_conn(os.path.join(self.out, "bitget-0"))
        self.assertEqual(rep["errors"], 3)                                            # books1, books15, trade
        self.assertEqual(rep["streams"]["books1:badusdt"]["n"], 0)                    # expected but silent → reported
        self.assertIn("TANPA data", md.verify([self.out])[0])
        self.assertGreater(rep["streams"]["books15:btcusdt"]["n"], 0)

    def test_reconnects_after_a_server_close(self):
        self.mock.reset(close_after=20)
        self._record(self._specs()[:1], lambda: self._conn("binance-0")["sessions"] >= 2 and self._data_lines("binance-0") > 25)
        rep = md.verify_conn(os.path.join(self.out, "binance-0"))
        self.assertGreaterEqual(rep["sessions"], 2)
        self.assertGreater(rep["down_s"], 0.5)                                         # 1 s backoff counted as down
        self.assertLess(rep["coverage"], 1.0)
        self.assertTrue(any("1001" in r for r in rep["reasons"]))

    def test_reconnects_when_the_feed_goes_silent(self):
        self.mock.reset(silent_after=10)
        specs = self._specs()[:1]
        self._record(specs, lambda: self._conn("binance-0")["sessions"] >= 2, stale_s=0.4)
        rep = md.verify_conn(os.path.join(self.out, "binance-0"))
        self.assertGreaterEqual(rep["sessions"], 2)
        self.assertTrue(any("no market data" in r for r in rep["reasons"]))

    def test_disk_guard_stops_cleanly_with_exit_code(self):
        self.mock.reset()
        free = {"n": 10**12}
        self._when(lambda: self._data_lines("binance-0") >= 3, lambda: free.update(n=10**9))
        code = self._record(self._specs()[:1], lambda: False, timeout=30.0, free_bytes=lambda p: free["n"], min_free_gb=5)
        self.assertEqual(code, md.EXIT_DISK_FULL)                                     # long before the 30 s timeout
        rep = md.verify_conn(os.path.join(self.out, "binance-0"))
        self.assertEqual(rep["truncated"], 0)                                          # files closed properly
        self.assertGreater(rep["data"], 0)
        self.assertTrue(any("STOP" in l for l in self.logs))

    def test_status_line_reports_rate_and_disk(self):
        self.mock.reset()
        self._record(self._specs(), lambda: any(l.startswith("[") and "binance-0 UP" in l for l in self.logs))
        status = [l for l in self.logs if l.startswith("[")]
        self.assertTrue(status)
        self.assertIn("binance-0 UP", status[-1])
        self.assertIn("GB/day", status[-1])

    def _stats_lines(self, conn: str) -> int:
        d = os.path.join(self.out, conn)
        if not os.path.isdir(d):
            return 0
        return sum(1 for f in os.listdir(d) for _, p in md.read_file(os.path.join(d, f)) if p.startswith('#{"ev":"stats"'))

    def test_preflight_keeps_only_symbols_trading_on_both(self):
        logs = []
        keep = md.preflight(["BTCUSDT", "ETHUSDT", "OLDUSDT", "ONLYBGUSDT", "ONLYBNUSDT", "HALTUSDT"], self.ctx,
                            binance=("127.0.0.1", self.mock.port), bitget=("127.0.0.1", self.mock.port), log=logs.append)
        self.assertEqual(keep, ["BTCUSDT", "ETHUSDT"])
        self.assertTrue(any("OLDUSDT" in l and "BREAK" in l for l in logs))
        self.assertTrue(any("HALTUSDT" in l and "halt" in l for l in logs))

    def test_preflight_unreachable_venue_drops_nothing(self):
        dead = socket.socket()
        dead.bind(("127.0.0.1", 0))
        port = dead.getsockname()[1]
        dead.close()
        logs = []
        keep = md.preflight(["BTCUSDT", "ZZZUSDT"], self.ctx, binance=("127.0.0.1", port), bitget=("127.0.0.1", port),
                            timeout=0.5, log=logs.append)
        self.assertEqual(keep, ["BTCUSDT", "ZZZUSDT"])
        self.assertTrue(any("not checked" in l for l in logs))


class PollingWsUnitTests(unittest.TestCase):
    """PollingWs on a plain socket pair: bytes arrive in arbitrary pieces, polls time out mid-frame."""

    def _pair(self):
        a, b = socket.socketpair()
        self.addCleanup(a.close)
        self.addCleanup(b.close)
        ws = md.PollingWs("x", 0, "/", None, 1.0)
        ws.sock = a
        ws._rbuf, ws._frag = bytearray(), []
        return ws, b

    def test_a_timeout_in_the_middle_of_a_frame_loses_nothing(self):
        ws, peer = self._pair()
        stream = (_frame(0x1, b'{"a":1}') + _frame(0x1, b"y" * 300) + _frame(0x1, b"he", fin=False) + _frame(0x9, b"pp")
                  + _frame(0x0, b"llo") + _frame(0x1, b"z" * 70_000))
        got = []
        for i in range(0, len(stream), 7):                       # 7-byte pieces; a poll after each
            peer.sendall(stream[i:i + 7])
            got += ws.poll(0.001)
        for _ in range(50):
            got += ws.poll(0.001)
        self.assertEqual(got, ['{"a":1}', "y" * 300, "hello", "z" * 70_000])
        peer.settimeout(1)
        pong = peer.recv(64)
        self.assertEqual(pong[0], 0x8A)                          # FIN + pong, masked, same payload
        mask = pong[2:6]
        self.assertEqual(bytes(b ^ mask[i % 4] for i, b in enumerate(pong[6:8])), b"pp")

    def test_close_frame_raises_with_code(self):
        ws, peer = self._pair()
        peer.sendall(_frame(0x1, b"last") + _frame(0x8, struct.pack("!H", 1008) + b"policy"))
        got = []
        with self.assertRaises(lp.WsError) as e:
            for _ in range(5):
                got += ws.poll(0.1)
        self.assertIn("1008", str(e.exception))
        self.assertEqual(got, ["last"])                          # C-11: delivered before the close is reported

    def test_c11_messages_before_a_close_in_the_same_read_are_delivered(self):
        ws, peer = self._pair()
        peer.sendall(_frame(0x1, b"A") + _frame(0x1, b"B") + _frame(0x8, struct.pack("!H", 1000)) + _frame(0x1, b"after"))
        got = []
        with self.assertRaises(lp.WsError) as e:
            for _ in range(5):
                got += ws.poll(0.1)
        self.assertEqual(got, ["A", "B"])                        # nothing after the close frame is a message
        self.assertIn("1000", str(e.exception))

    def test_c11_a_failed_pong_does_not_lose_complete_messages(self):
        ws, peer = self._pair()

        def send_times_out(op, payload):
            raise socket.timeout("send timed out")
        ws._send_frame = send_times_out
        peer.sendall(_frame(0x1, b"A") + _frame(0x9, b"p") + _frame(0x1, b"B"))
        got = []
        with self.assertRaises(OSError):
            for _ in range(5):
                got += ws.poll(0.1)
        self.assertEqual(got, ["A", "B"])

    def test_c11_bytes_from_the_handshake_are_delivered_even_if_the_peer_then_closes(self):
        ws, peer = self._pair()
        ws._rbuf = bytearray(_frame(0x1, b"early"))              # arrived together with the 101 response
        peer.close()
        got = []
        with self.assertRaises(lp.WsError):
            for _ in range(3):
                got += ws.poll(0.5)
        self.assertEqual(got, ["early"])

    def test_c11_unbounded_fragment_stream_is_refused(self):
        ws, peer = self._pair()
        old = md.MAX_FRAME
        md.MAX_FRAME = 1000
        self.addCleanup(setattr, md, "MAX_FRAME", old)
        peer.sendall(_frame(0x1, b"x" * 600, fin=False) + _frame(0x0, b"x" * 600, fin=False))
        with self.assertRaises(lp.WsError):
            for _ in range(5):
                ws.poll(0.1)

    def test_peer_eof_raises(self):
        ws, peer = self._pair()
        peer.close()
        with self.assertRaises(lp.WsError):
            ws.poll(0.1)


class FileAndParseTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.dir, ignore_errors=True)

    def test_classify(self):
        self.assertEqual(md.classify("binance", '{"stream":"btcusdt@bookTicker","data":{}}'), ("data", "btcusdt@bookticker"))
        self.assertEqual(md.classify("binance", '{"data":{},"stream":"ethusdt@trade"}'), ("data", "ethusdt@trade"))
        self.assertEqual(md.classify("binance", '{"result":null,"id":1}'), ("ack", None))
        self.assertEqual(md.classify("binance", '{"error":{"code":2,"msg":"x"},"id":1}')[0], "error")
        bg = '{"action":"snapshot","arg":{"instType":"SPOT","channel":"books1","instId":"BTCUSDT"},"data":[{"ts":"1"}],"ts":1}'
        self.assertEqual(md.classify("bitget", bg), ("data", "books1:btcusdt"))
        self.assertEqual(md.classify("bitget", '{"event":"subscribe","arg":{"instType":"SPOT","channel":"trade","instId":"ETHUSDT"}}'), ("ack", "trade:ethusdt"))
        self.assertEqual(md.classify("bitget", '{"event":"error","code":30001,"msg":"x"}')[0], "error")
        self.assertEqual(md.classify("bitget", "pong"), ("pong", None))
        self.assertEqual(md.classify("bitget", "garbage"), ("other", None))

    def test_bitget_subscribe_requests_fit_the_limits(self):
        specs = md.bitget_specs(md.DEFAULT_SYMBOLS, "h", 443)
        self.assertEqual(sum(len(s.streams) for s in specs), 3 * len(md.DEFAULT_SYMBOLS))
        for s in specs:
            self.assertLessEqual(len(s.streams), 40)
            for sub in s.subscribe:
                self.assertLessEqual(len(sub), 4096)
                self.assertLessEqual(len(json.loads(sub)["args"]), 20)
        b = md.binance_specs(md.DEFAULT_SYMBOLS, "h", 9443)
        self.assertEqual(len(b), 1)
        self.assertIn("btcusdt@depth20@100ms", b[0].path)

    def test_hourly_rotation_and_a_restart_in_the_same_hour(self):
        H = 3_600_000_000_000
        t0 = 1_793_000_000 * 10**9 // H * H + 10**9                  # 1 s into some hour
        w = md.HourlyWriter(self.dir, "c", flush_s=0)
        w.write(t0, "a")
        w.write(t0 + H - 2 * 10**9, "b")
        w.write(t0 + H, "c")                                       # next hour → new file
        w.close()
        w2 = md.HourlyWriter(self.dir, "c", flush_s=0)
        w2.write(t0 + H + 5, "d")                                  # restart within that hour → _01, not appended
        w2.close()
        files = sorted(os.listdir(os.path.join(self.dir, "c")), key=md._file_key)
        self.assertEqual(len(files), 3)
        self.assertEqual([f.split("_")[1] for f in files], ["00.tsv.gz", "00.tsv.gz", "01.tsv.gz"])
        self.assertNotEqual(files[0].split("_")[0], files[1].split("_")[0])         # two different hours
        self.assertEqual(files[1].split("_")[0], files[2].split("_")[0])
        data = [[p for _, p in md.read_file(os.path.join(self.dir, "c", f)) if not p.startswith("#")] for f in files]
        self.assertEqual(data, [["a", "b"], ["c"], ["d"]])

    def test_a_crash_loses_at_most_the_unflushed_tail(self):
        path = os.path.join(self.dir, "c")
        w = md.HourlyWriter(self.dir, "c", flush_s=0)              # flush after every line
        for i in range(100):
            w.write(10**18 + i, f'{{"i":{i}}}')
        f = w.path
        size = os.path.getsize(f)
        with open(f, "rb") as src:
            blob = src.read()
        with open(f, "wb") as dst:                                 # simulate power loss: no gzip trailer, torn last block
            dst.write(blob[:size] + b"\x00\x13")
        info = {}
        got = [p for _, p in md.read_file(f, info) if not p.startswith("#")]
        self.assertEqual(got, [f'{{"i":{i}}}' for i in range(100)])
        self.assertTrue(info["truncated"])
        rep = md.verify_conn(path)
        self.assertEqual(rep["truncated"], 1)
        w._gz = None                                               # the writer's file was replaced underneath it
        w._raw.close()

    def test_newlines_inside_a_message_cannot_break_the_line_format(self):
        w = md.HourlyWriter(self.dir, "c", flush_s=0)
        w.write(10**18, '{"a":\n1}')
        w.close()
        f = os.listdir(os.path.join(self.dir, "c"))[0]
        got = [p for _, p in md.read_file(os.path.join(self.dir, "c", f)) if not p.startswith("#")]
        self.assertEqual(len(got), 1)
        self.assertEqual(json.loads(got[0]), {"a": 1})

    def test_verify_counts_gaps_lag_and_downtime_exactly(self):
        S = 10**9
        t = 1_800_000_000 * S
        w = md.HourlyWriter(self.dir, "binance-0", flush_s=0)
        w.meta(t, "start", streams=["btcusdt@trade", "ethusdt@trade"])
        w.meta(t, "connect")
        ms = t // 10**6
        w.write(t + 1 * S, json.dumps({"stream": "btcusdt@trade", "data": {"E": ms + 1000 - 5}}))   # 5 ms old
        w.write(t + 2 * S, json.dumps({"stream": "btcusdt@trade", "data": {"E": ms + 2000 - 7}}))   # 7 ms old
        w.meta(t + 3 * S, "disconnect", reason="TimeoutError: x")
        w.meta(t + 13 * S, "connect")                              # 10 s down
        w.write(t + 14 * S, json.dumps({"stream": "btcusdt@trade", "data": {"E": ms + 14000 - 5}}))
        w.write(t + 20 * S, "garbage")
        w.close()
        rep = md.verify_conn(os.path.join(self.dir, "binance-0"))
        s = rep["streams"]["btcusdt@trade"]
        self.assertEqual(s["n"], 3)
        self.assertAlmostEqual(s["max_gap_s"], 12.0)
        self.assertEqual(s["lag_p50_ms"], 5)
        self.assertEqual(s["lag_p99_ms"], 7)
        self.assertAlmostEqual(rep["down_s"], 10.0)
        self.assertEqual(rep["sessions"], 2)
        self.assertAlmostEqual(rep["span_s"], 20.0)
        self.assertAlmostEqual(rep["coverage"], 0.5)
        self.assertEqual(rep["streams"]["ethusdt@trade"]["n"], 0)

    def test_bitget_trade_snapshot_on_subscribe_is_not_counted_as_data_age(self):
        # First md-tokyo run: Bitget answers a trade subscription with a snapshot of recent (minutes old) trades, which
        # showed up as "data age p99 113 s". Only live updates measure how old data is when it arrives.
        S = 10**9
        t = 1_800_000_000 * S
        ms = t // 10**6
        arg = '"arg":{"instType":"SPOT","channel":"trade","instId":"OPUSDT"}'
        w = md.HourlyWriter(self.dir, "bitget-0", flush_s=0)
        w.meta(t, "start", streams=["trade:opusdt"])
        w.meta(t, "connect")
        w.write(t + S, '{"action":"snapshot",' + arg + f',"data":[{{"ts":"{ms - 170_000}"}}],"ts":{ms}}}')
        w.write(t + 2 * S, '{"action":"update",' + arg + f',"data":[{{"ts":"{ms + 2_000 - 12}"}}],"ts":{ms + 2_000}}}')
        w.close()
        s = md.verify_conn(os.path.join(self.dir, "bitget-0"))["streams"]["trade:opusdt"]
        self.assertEqual(s["n"], 2)                               # still counted as messages
        self.assertEqual(s["lag_p99_ms"], 12)                     # was 171012

    def test_a_crashed_run_counts_as_downtime_until_the_next_connect(self):
        S = 10**9
        t = 1_800_000_000 * S
        w = md.HourlyWriter(self.dir, "bitget-0", flush_s=0)
        w.meta(t, "start", streams=[])
        w.meta(t, "connect")
        w.write(t + 4 * S, "x")                                    # last line, then the process died (no "stop")
        w.close()
        w2 = md.HourlyWriter(self.dir, "bitget-0", flush_s=0)
        w2.meta(t + 30 * S, "start", streams=[])
        w2.meta(t + 31 * S, "connect")
        w2.write(t + 40 * S, "y")
        w2.close()
        rep = md.verify_conn(os.path.join(self.dir, "bitget-0"))
        self.assertAlmostEqual(rep["down_s"], 27.0)                # 4 s → 31 s

    def test_cli_rejects_bad_arguments(self):
        for argv in (["--venues", "kraken"], ["--hours", "0"], ["--symbols", "btc-usdt"], ["--status-s", "1"]):
            with self.assertRaises(SystemExit, msg=argv):
                md.main(argv)


if __name__ == "__main__":
    unittest.main()
