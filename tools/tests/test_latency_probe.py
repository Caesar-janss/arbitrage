"""
Tests for tools/latency_probe.py against a local TLS mock that speaks the three venues' protocols (stdlib only):
  REST  /api/v3/time (Binance serverTime), /v5/market/time (Bybit timeNano), /api/v2/public/time (Bitget data.serverTime)
  WS    /ws (Binance: server ping frame first, LIST_SUBSCRIPTIONS → {"result":[…],"id":n}, reply > 125 bytes),
        /v5/public/spot (Bybit op:ping → ret_msg pong), /v2/ws/public (Bitget "ping" → "pong" sent in two fragments)
The mock adds a fixed delay and a known clock skew, so latency and offset estimates can be checked against truth.

  python3 -m unittest discover -s tools/tests -v
"""
from __future__ import annotations

import argparse
import base64
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

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import latency_probe as lp  # noqa: E402

DELAY_S = 0.02          # injected server-side delay per request / message
SKEW_MS = 5_000.0       # mock venue clock = local clock + 5 s


def _ws_accept(key: str) -> str:
    return base64.b64encode(hashlib.sha1((key + lp.WS_GUID).encode()).digest()).decode()


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


class MockVenue:
    def __init__(self, certfile: str, keyfile: str):
        self.ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        self.ctx.load_cert_chain(certfile, keyfile)
        self.sock = socket.socket()
        self.sock.bind(("127.0.0.1", 0))
        self.sock.listen(32)
        self.port = self.sock.getsockname()[1]
        self.pongs_received: list[bytes] = []
        self.running = True
        threading.Thread(target=self._accept, daemon=True).start()

    def stop(self):
        self.running = False
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
        buf = b""
        try:
            while True:
                while b"\r\n\r\n" not in buf:
                    chunk = c.recv(4096)
                    if not chunk:
                        return
                    buf += chunk
                head, _, buf = buf.partition(b"\r\n\r\n")
                lines = head.decode().split("\r\n")
                path = lines[0].split(" ")[1]
                headers = {k.strip().lower(): v.strip() for k, _, v in (l.partition(":") for l in lines[1:])}
                if headers.get("upgrade", "").lower() == "websocket":
                    c.sendall(("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                               f"Sec-WebSocket-Accept: {_ws_accept(headers['sec-websocket-key'])}\r\n\r\n").encode())
                    self._ws(c, path, buf)
                    return
                time.sleep(DELAY_S)
                now = time.time() * 1000 + SKEW_MS
                body = {"/api/v3/time": {"serverTime": int(now)},
                        "/v5/market/time": {"retCode": 0, "result": {"timeSecond": str(int(now / 1000)), "timeNano": str(int(now * 1e6))}, "time": int(now)},
                        "/api/v2/public/time": {"code": "00000", "data": {"serverTime": str(int(now))}}}.get(path)
                data = json.dumps(body).encode() if body else b"{}"
                status = "200 OK" if body else "404 Not Found"
                c.sendall(f"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {len(data)}\r\n\r\n".encode() + data)
        except (OSError, ssl.SSLError):
            pass
        finally:
            c.close()

    def _ws(self, c, path, buf):
        def recv_exact(n):
            nonlocal buf
            while len(buf) < n:
                chunk = c.recv(4096)
                if not chunk:
                    raise OSError("closed")
                buf += chunk
            out, buf = buf[:n], buf[n:]
            return out

        first = True
        while True:
            b0, b1 = recv_exact(2)
            n = b1 & 0x7F
            if n == 126:
                n = struct.unpack("!H", recv_exact(2))[0]
            elif n == 127:
                n = struct.unpack("!Q", recv_exact(8))[0]
            mask = recv_exact(4)
            data = bytes(b ^ mask[i % 4] for i, b in enumerate(recv_exact(n)))
            op = b0 & 0x0F
            if op == 0x8:
                return
            if op == 0xA:
                self.pongs_received.append(data)
                continue
            text = data.decode()
            time.sleep(DELAY_S)
            if path == "/ws":
                if first:                          # like Binance: the server pings; the client must echo the payload
                    c.sendall(_frame(0x9, b"probe-ping-payload"))
                    first = False
                req = json.loads(text)
                reply = json.dumps({"result": ["x" * 200], "id": req["id"]}).encode()   # > 125 bytes: 16-bit length
                c.sendall(_frame(0x1, reply))
            elif path == "/v5/public/spot":
                req = json.loads(text)
                c.sendall(_frame(0x1, json.dumps({"success": True, "ret_msg": "pong", "conn_id": "c", "req_id": req["req_id"], "op": "ping"}).encode()))
            elif path == "/v2/ws/public" and text == "ping":
                c.sendall(_frame(0x1, b"po", fin=False) + _frame(0x0, b"ng"))           # fragmented message


@unittest.skipIf(shutil.which("openssl") is None, "openssl CLI needed for the self-signed test certificate")
class LatencyProbeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp()
        cls.cert, cls.key = os.path.join(cls.tmp, "c.pem"), os.path.join(cls.tmp, "k.pem")
        subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1", "-subj", "/CN=127.0.0.1",
                        "-keyout", cls.key, "-out", cls.cert], check=True, capture_output=True)
        cls.mock = MockVenue(cls.cert, cls.key)
        cls.client_ctx = ssl.create_default_context()
        cls.client_ctx.check_hostname = False
        cls.client_ctx.verify_mode = ssl.CERT_NONE

    @classmethod
    def tearDownClass(cls):
        cls.mock.stop()
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def _targets(self):
        by_name = {t.name: t for t in lp.DEFAULT_TARGETS}
        out = []
        for name, ws_path in (("binance-spot", "/ws"), ("bybit-spot", "/v5/public/spot"), ("bitget", "/v2/ws/public")):
            t = by_name[name]
            out.append(lp.Target(t.name, t.venue, "127.0.0.1", t.rest_path, t.server_ms, ws_host="127.0.0.1",
                                 ws_port=self.mock.port, ws_path=ws_path, ws_ping=t.ws_ping, ws_is_pong=t.ws_is_pong,
                                 rest_port=self.mock.port))
        return out

    def _args(self, label, rounds=6):
        return argparse.Namespace(label=label, minutes=1, rounds=rounds, interval=0.01, fresh_every=3, timeout=2.0,
                                  keep_raw=True, progress=False)

    def test_measures_every_channel_without_errors(self):
        r = lp.run(self._args("mock"), self._targets(), self.client_ctx)
        for name in ("binance-spot", "bybit-spot", "bitget"):
            t = r["targets"][name]
            self.assertEqual(t["errors"], {}, name)
            self.assertEqual(t["rest_ms"]["n"], 6, name)
            self.assertEqual(t["ws_ms"]["n"], 6, name)
            self.assertEqual(t["tcp_ms"]["n"], 2, name)               # rounds 0 and 3
            self.assertGreaterEqual(t["rest_ms"]["p50"], DELAY_S * 1000, name)
            self.assertGreaterEqual(t["ws_ms"]["p50"], DELAY_S * 1000, name)
            # offset = venue − local midpoint ≈ +SKEW (the mock's delay sits before it stamps the time)
            self.assertAlmostEqual(t["offset_ms"]["median"], SKEW_MS, delta=DELAY_S * 1000 + 15, msg=name)

    def test_answers_server_ping_with_the_same_payload(self):
        before = len(self.mock.pongs_received)
        lp.run(self._args("mock", rounds=2), self._targets()[:1], self.client_ctx)
        self.assertIn(b"probe-ping-payload", self.mock.pongs_received[before:])

    def test_unreachable_target_is_counted_not_fatal(self):
        s = socket.socket()
        s.bind(("127.0.0.1", 0))
        dead_port = s.getsockname()[1]
        s.close()                                                        # nothing listens here now
        t = self._targets()[0]
        dead = lp.Target("dead", "X", "127.0.0.1", t.rest_path, t.server_ms, ws_host="127.0.0.1", ws_port=dead_port,
                         ws_path="/ws", ws_ping=t.ws_ping, ws_is_pong=t.ws_is_pong, rest_port=dead_port)
        r = lp.run(self._args("mock", rounds=3), [dead], self.client_ctx)
        d = r["targets"]["dead"]
        self.assertEqual(d["rest_ms"]["n"], 0)
        self.assertEqual(sum(d["errors"].values()), 3 + 3 + 1)          # rest ×3, ws ×3, fresh ×1

    def test_compare_picks_the_region_with_the_faster_slowest_leg(self):
        fast = lp.run(self._args("tokyo"), self._targets(), self.client_ctx)
        slow = json.loads(json.dumps(fast))
        slow["label"] = "singapore"
        for t in slow["targets"].values():                             # every leg 3× slower
            for k in ("rest_ms", "ws_ms"):
                for q in ("p50", "p90", "p99", "max", "mean"):
                    if q in t[k]:
                        t[k][q] *= 3
        paths = []
        for r in (fast, slow):
            p = os.path.join(self.tmp, f"{r['label']}.json")
            with open(p, "w", encoding="utf-8") as fh:
                json.dump(r, fh)
            paths.append(p)
        md = lp.compare(paths)
        self.assertIn("| binance-spot ↔ bitget |", md)
        self.assertIn("binance-spot↔bitget: **tokyo**", md)
        self.assertIn("bitget↔bybit-spot: **tokyo**", md)

    def test_slowest_leg_decides_not_the_fastest(self):
        # Tokyo: Binance/Bitget near (2 ms), Bybit far (70 ms). Singapore: everything medium (40/40/30 ms).
        # bitget↔bybit: slowest leg Tokyo 70 vs Singapore 40 → Singapore, although Tokyo has the faster single leg (2).
        def run_result(label, ms):
            return {"label": label, "started_utc": "t", "host": {}, "targets": {
                n: {"rest_ms": {"n": 1, "p50": v, "p99": v}, "ws_ms": {"n": 1, "p50": v, "p99": v}, "tcp_ms": {}, "offset_ms": {}, "errors": {}}
                for n, v in ms.items()}}
        paths = []
        for r in (run_result("tokyo", {"binance-spot": 2, "bitget": 2, "bybit-spot": 70}),
                  run_result("singapore", {"binance-spot": 40, "bitget": 40, "bybit-spot": 30})):
            p = os.path.join(self.tmp, f"syn_{r['label']}.json")
            with open(p, "w", encoding="utf-8") as fh:
                json.dump(r, fh)
            paths.append(p)
        md = lp.compare(paths)
        self.assertIn("bitget↔bybit-spot: **singapore** (40.0 ms)", md)
        self.assertIn("binance-spot↔bitget: **tokyo** (2.00 ms)", md)

    def test_aws_placement_rejects_error_pages_and_accepts_real_imds(self):
        import http.server

        class Imds(http.server.BaseHTTPRequestHandler):
            answers = {}

            def _reply(self, code, body):
                self.send_response(code)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body.encode())

            def do_PUT(self):
                self._reply(*self.answers.get("token", (200, "tok")))

            def do_GET(self):
                self._reply(*self.answers.get(self.path, (404, "")))

            def log_message(self, *a):
                pass

        srv = http.server.HTTPServer(("127.0.0.1", 0), Imds)
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        try:
            port = srv.server_address[1]
            # a proxy / firewall answering with an error page must not become the "availability zone"
            Imds.answers = {"token": (403, "Destination IP is in a private/reserved range")}
            self.assertIn("not on EC2", lp.aws_placement("127.0.0.1", port)["aws"])
            Imds.answers = {"/latest/meta-data/placement/availability-zone": (200, "<html>blocked</html>")}
            self.assertIn("not on EC2", lp.aws_placement("127.0.0.1", port)["aws"])
            Imds.answers = {"/latest/meta-data/placement/availability-zone": (200, "ap-northeast-1a"),
                            "/latest/meta-data/placement/region": (200, "ap-northeast-1"),
                            "/latest/meta-data/instance-type": (200, "c7i.large")}
            self.assertEqual(lp.aws_placement("127.0.0.1", port),
                             {"aws_availability_zone": "ap-northeast-1a", "aws_region": "ap-northeast-1", "aws_instance_type": "c7i.large"})
        finally:
            srv.shutdown()

    def test_leg_uses_the_smaller_of_rest_and_ws(self):
        # Binance in the first Tokyo run: REST 6.8 ms, WS LIST_SUBSCRIPTIONS 46.7 ms (server processing, TCP 5 ms)
        t = {"rest_ms": {"n": 30, "p50": 6.79, "p99": 8.48}, "ws_ms": {"n": 30, "p50": 46.7, "p99": 164.3}}
        self.assertEqual(lp.leg_ms(t, "p50"), 6.79)
        self.assertEqual(lp.leg_ms(t, "p99"), 8.48)
        self.assertEqual(lp.leg_ms({"rest_ms": {"n": 0}, "ws_ms": {"n": 3, "p50": 5.0}}, "p50"), 5.0)
        self.assertIsNone(lp.leg_ms({"rest_ms": {"n": 0}, "ws_ms": {"n": 0}}, "p50"))

    def test_hourly_files_merge_into_one_column_per_location(self):
        def hour(label, v, p99):
            return {"label": label, "started_utc": label, "ended_utc": label, "host": {}, "targets": {
                n: {"venue": "x", "rest_ms": {"n": 10, "p50": v, "p90": v, "p99": p99, "max": p99}, "ws_ms": {"n": 0},
                    "tcp_ms": {"n": 0}, "tls_ms": {"n": 0}, "offset_ms": {"n": 10, "median": 1.0, "p10": 0.5, "p90": 1.5},
                    "errors": {"rest:TimeoutError": 1}} for n in ("binance-spot", "bitget", "bybit-spot")}}
        paths = []
        for i, (v, p99) in enumerate([(5, 9), (7, 30), (6, 12)], start=1):
            p = os.path.join(self.tmp, f"hourly_{i}.json")
            with open(p, "w", encoding="utf-8") as fh:
                json.dump(hour(f"tokyo-h{i:02d}", v, p99), fh)
            paths.append(p)
        md = lp.compare(paths)
        self.assertIn("**tokyo**", md)
        self.assertIn("3 jam", md)
        self.assertIn("| binance-spot | 6.00 / 30.0 |", md)                  # median of hourly p50, worst hourly p99
        self.assertIn("(3 err)", md)
        self.assertIn("## Per jam", md)
        self.assertIn("| 02 | 7.00 |", md)

    def test_percentile(self):
        self.assertEqual(lp.pct([3, 1, 2], 0.5), 2)
        self.assertAlmostEqual(lp.pct(list(range(101)), 0.99), 99.0)
        self.assertIsNone(lp.pct([], 0.5))

    def test_cli_refuses_an_interval_that_would_crowd_the_venue_rate_limits(self):
        with self.assertRaises(SystemExit):
            lp.main(["--interval", "0.1", "--rounds", "1"])


if __name__ == "__main__":
    unittest.main()
