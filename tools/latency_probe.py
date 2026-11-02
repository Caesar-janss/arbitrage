#!/usr/bin/env python3
"""
ArbEngine S0 latency probe — measure, from THIS host, how far each venue is (stdlib only, public endpoints, no API key).

  python3 tools/latency_probe.py --label tokyo --minutes 30 --out probe_tokyo.json
  python3 tools/latency_probe.py --label singapore --minutes 30 --out probe_singapore.json
  python3 tools/latency_probe.py --compare probe_tokyo.json probe_singapore.json      # → markdown table for the decision

Per target, every round (default 1 s):
  rest_ms   HTTPS GET of the venue's server-time endpoint on a keep-alive connection: round trip to the API ORIGIN
            (passes any CDN edge), plus a clock-offset sample: server_time − midpoint of local send/receive.
  ws_ms     application-level ping on a persistent public WebSocket (Binance LIST_SUBSCRIPTIONS, Bybit op:ping,
            Bitget "ping"): round trip to the stream servers that will carry market data.
Every --fresh-every rounds, on a NEW connection:
  tcp_ms    TCP connect = network RTT to the first hop that terminates TCP (often a CDN/edge, not the origin).
  tls_ms    TLS handshake on top of it.
Once: DNS answers and reverse DNS of each address (AWS PTRs name the region / CloudFront edge), and the host's own
AWS placement (IMDSv2) and NTP status, so the result file says where it was measured.

Rate limits respected (official docs): Binance WS ≤ 5 msg/s per connection, Bitget WS ≤ 10 msg/s, REST weight 1 per
call; the probe sends at most 1 message per second per connection. It never subscribes to data and never trades.

Endpoint sources: docs/S0_LATENCY_PROBE.md (VERIFIED / UNVERIFIED per endpoint).
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import http.client
import json
import os
import platform
import random
import socket
import ssl
import statistics
import struct
import subprocess
import sys
import time
from dataclasses import dataclass, field
from typing import Callable, Optional

WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"


# ── targets ───────────────────────────────────────────────────────────────────────────────────────────────────────
@dataclass
class Target:
    name: str
    venue: str
    rest_host: str
    rest_path: str
    server_ms: Callable[[dict], Optional[float]]     # parse server time (ms, float) from the JSON body
    ws_host: Optional[str] = None
    ws_port: int = 443
    ws_path: str = "/"
    ws_ping: Callable[[int], str] = lambda n: "ping"
    ws_is_pong: Callable[[str, int], bool] = lambda text, n: text == "pong"
    rest_port: int = 443


def _f(v) -> Optional[float]:
    try:
        return float(v)
    except (TypeError, ValueError):
        return None


def _bybit_ms(j: dict) -> Optional[float]:
    nano = _f((j.get("result") or {}).get("timeNano"))
    return nano / 1e6 if nano is not None else _f(j.get("time"))


def _binance_pong(text: str, n: int) -> bool:
    try:
        j = json.loads(text)
    except ValueError:
        return False
    return j.get("id") == n and "result" in j


def _bybit_pong(text: str, n: int) -> bool:
    try:
        j = json.loads(text)
    except ValueError:
        return False
    return j.get("op") == "ping" and (j.get("ret_msg") == "pong" or j.get("success") is True)


DEFAULT_TARGETS = [
    Target("binance-spot", "Binance", "api.binance.com", "/api/v3/time", lambda j: _f(j.get("serverTime")),
           ws_host="stream.binance.com", ws_port=9443, ws_path="/ws",
           ws_ping=lambda n: json.dumps({"method": "LIST_SUBSCRIPTIONS", "id": n}), ws_is_pong=_binance_pong),
    Target("binance-usdm", "Binance", "fapi.binance.com", "/fapi/v1/time", lambda j: _f(j.get("serverTime"))),
    Target("bybit-spot", "Bybit", "api.bybit.com", "/v5/market/time", _bybit_ms,
           ws_host="stream.bybit.com", ws_path="/v5/public/spot",
           ws_ping=lambda n: json.dumps({"req_id": str(n), "op": "ping"}), ws_is_pong=_bybit_pong),
    Target("bybit-linear", "Bybit", "api.bybit.com", "/v5/market/time", _bybit_ms,
           ws_host="stream.bybit.com", ws_path="/v5/public/linear",
           ws_ping=lambda n: json.dumps({"req_id": str(n), "op": "ping"}), ws_is_pong=_bybit_pong),
    Target("bitget", "Bitget", "api.bitget.com", "/api/v2/public/time", lambda j: _f((j.get("data") or {}).get("serverTime")),
           ws_host="ws.bitget.com", ws_path="/v2/ws/public"),
]

# spatial pairs of STAGE1_CORE §5 (spot legs)
PAIRS = [("binance-spot", "bitget"), ("bitget", "bybit-spot"), ("binance-spot", "bybit-spot")]


# ── minimal RFC 6455 client (text frames, ping/pong, close) ─────────────────────────────────────────────────────────
class WsError(Exception):
    pass


class WsClient:
    def __init__(self, host: str, port: int, path: str, ctx: ssl.SSLContext, timeout: float):
        self.host, self.port, self.path, self.ctx, self.timeout = host, port, path, ctx, timeout
        self.sock: Optional[ssl.SSLSocket] = None
        self._buf = b""

    def connect(self) -> None:
        raw = socket.create_connection((self.host, self.port), timeout=self.timeout)
        raw.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.sock = self.ctx.wrap_socket(raw, server_hostname=self.host)
        key = base64.b64encode(os.urandom(16)).decode()
        req = (f"GET {self.path} HTTP/1.1\r\nHost: {self.host}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
               f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\nUser-Agent: arbengine-latency-probe\r\n\r\n")
        self.sock.sendall(req.encode())
        head = self._read_until(b"\r\n\r\n").decode("latin-1")
        status = head.split("\r\n", 1)[0]
        if " 101 " not in status + " ":
            raise WsError(f"handshake refused: {status}")
        expect = base64.b64encode(hashlib.sha1((key + WS_GUID).encode()).digest()).decode()
        headers = {k.strip().lower(): v.strip() for k, _, v in (line.partition(":") for line in head.split("\r\n")[1:]) if k}
        if headers.get("sec-websocket-accept") != expect:
            raise WsError("bad Sec-WebSocket-Accept")

    def _read_until(self, marker: bytes) -> bytes:
        while marker not in self._buf:
            chunk = self.sock.recv(4096)
            if not chunk:
                raise WsError("connection closed during handshake")
            self._buf += chunk
            if len(self._buf) > 65536:
                raise WsError("handshake too large")
        head, _, self._buf = self._buf.partition(marker)
        return head

    def _recv_exact(self, n: int) -> bytes:
        while len(self._buf) < n:
            chunk = self.sock.recv(max(4096, n - len(self._buf)))
            if not chunk:
                raise WsError("connection closed")
            self._buf += chunk
        out, self._buf = self._buf[:n], self._buf[n:]
        return out

    def _send_frame(self, opcode: int, payload: bytes) -> None:
        header = bytes([0x80 | opcode])
        n = len(payload)
        if n < 126:
            header += bytes([0x80 | n])
        elif n < 65536:
            header += bytes([0x80 | 126]) + struct.pack("!H", n)
        else:
            header += bytes([0x80 | 127]) + struct.pack("!Q", n)
        mask = os.urandom(4)
        masked = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
        self.sock.sendall(header + mask + masked)

    def send_text(self, text: str) -> None:
        self._send_frame(0x1, text.encode())

    def recv_text(self) -> str:
        """Next complete text message; answers server pings (Binance pings every 20 s) on the way."""
        parts: list[bytes] = []
        while True:
            b0, b1 = self._recv_exact(2)
            fin, opcode = b0 & 0x80, b0 & 0x0F
            n = b1 & 0x7F
            if n == 126:
                n = struct.unpack("!H", self._recv_exact(2))[0]
            elif n == 127:
                n = struct.unpack("!Q", self._recv_exact(8))[0]
            mask = self._recv_exact(4) if b1 & 0x80 else None
            data = self._recv_exact(n)
            if mask:
                data = bytes(b ^ mask[i % 4] for i, b in enumerate(data))
            if opcode == 0x9:                       # ping → pong with the same payload
                self._send_frame(0xA, data)
                continue
            if opcode == 0xA:                       # unsolicited pong
                continue
            if opcode == 0x8:
                raise WsError(f"server closed: {data[:2].hex()} {data[2:60]!r}")
            if opcode in (0x1, 0x0, 0x2):
                parts.append(data)
                if fin:
                    return b"".join(parts).decode("utf-8", "replace")

    def close(self) -> None:
        try:
            if self.sock:
                self._send_frame(0x8, struct.pack("!H", 1000))
                self.sock.close()
        except OSError:
            pass
        self.sock = None
        self._buf = b""


# ── measurement ───────────────────────────────────────────────────────────────────────────────────────────────────
@dataclass
class Series:
    rest_ms: list = field(default_factory=list)
    ws_ms: list = field(default_factory=list)
    tcp_ms: list = field(default_factory=list)
    tls_ms: list = field(default_factory=list)
    offset_ms: list = field(default_factory=list)      # server − local midpoint (+ = venue clock ahead of ours)
    errors: dict = field(default_factory=dict)

    def error(self, kind: str, exc: Exception) -> None:
        key = f"{kind}:{type(exc).__name__}"
        self.errors[key] = self.errors.get(key, 0) + 1


class Prober:
    def __init__(self, t: Target, ctx: ssl.SSLContext, timeout: float):
        self.t, self.ctx, self.timeout = t, ctx, timeout
        self.s = Series()
        self.http: Optional[http.client.HTTPSConnection] = None
        self.ws: Optional[WsClient] = None
        self.ws_n = 0

    def rest(self) -> None:
        t = self.t
        try:
            if self.http is None:
                self.http = http.client.HTTPSConnection(t.rest_host, t.rest_port, timeout=self.timeout, context=self.ctx)
                self.http.connect()
                self.http.sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            w0 = time.time_ns()
            p0 = time.perf_counter_ns()
            self.http.request("GET", t.rest_path, headers={"Host": t.rest_host, "User-Agent": "arbengine-latency-probe", "Connection": "keep-alive"})
            resp = self.http.getresponse()
            body = resp.read()
            p1 = time.perf_counter_ns()
            w1 = time.time_ns()
            if resp.status != 200:
                raise RuntimeError(f"HTTP {resp.status}")
            self.s.rest_ms.append((p1 - p0) / 1e6)
            server = t.server_ms(json.loads(body))
            if server is not None:
                self.s.offset_ms.append(server - (w0 + w1) / 2e6)
            if resp.getheader("connection", "").lower() == "close":
                self._drop_http()
        except Exception as e:  # noqa: BLE001 — every failure is counted, the probe keeps going
            self.s.error("rest", e)
            self._drop_http()

    def _drop_http(self) -> None:
        try:
            if self.http:
                self.http.close()
        except OSError:
            pass
        self.http = None

    def wsping(self) -> None:
        t = self.t
        if not t.ws_host:
            return
        try:
            if self.ws is None:
                self.ws = WsClient(t.ws_host, t.ws_port, t.ws_path, self.ctx, self.timeout)
                self.ws.connect()
            self.ws_n += 1
            n = self.ws_n
            p0 = time.perf_counter_ns()
            self.ws.send_text(t.ws_ping(n))
            deadline = time.monotonic() + self.timeout
            while True:
                text = self.ws.recv_text()
                if t.ws_is_pong(text, n):
                    break
                if time.monotonic() > deadline:
                    raise TimeoutError("no pong")
            self.s.ws_ms.append((time.perf_counter_ns() - p0) / 1e6)
        except Exception as e:  # noqa: BLE001
            self.s.error("ws", e)
            if self.ws:
                self.ws.close()
            self.ws = None

    def fresh(self) -> None:
        t = self.t
        try:
            p0 = time.perf_counter_ns()
            raw = socket.create_connection((t.rest_host, t.rest_port), timeout=self.timeout)
            p1 = time.perf_counter_ns()
            tls = self.ctx.wrap_socket(raw, server_hostname=t.rest_host)
            p2 = time.perf_counter_ns()
            tls.close()
            self.s.tcp_ms.append((p1 - p0) / 1e6)
            self.s.tls_ms.append((p2 - p1) / 1e6)
        except Exception as e:  # noqa: BLE001
            self.s.error("fresh", e)

    def close(self) -> None:
        self._drop_http()
        if self.ws:
            self.ws.close()


def dns_info(host: str) -> dict:
    out: dict = {"host": host}
    try:
        p0 = time.perf_counter_ns()
        infos = socket.getaddrinfo(host, 443, proto=socket.IPPROTO_TCP)
        out["resolve_ms"] = round((time.perf_counter_ns() - p0) / 1e6, 2)
        ips = sorted({i[4][0] for i in infos})
        out["addresses"] = ips
        ptr = {}
        for ip in ips[:6]:
            try:
                ptr[ip] = socket.gethostbyaddr(ip)[0]
            except OSError:
                ptr[ip] = None
        out["ptr"] = ptr
    except OSError as e:
        out["error"] = f"{type(e).__name__}: {e}"
    return out


_AWS_FORMATS = {"availability_zone": r"[a-z]{2}(-[a-z]+)+-\d[a-z]", "region": r"[a-z]{2}(-[a-z]+)+-\d", "instance_type": r"[a-z0-9]+\.[a-z0-9]+"}


def aws_placement(host: str = "169.254.169.254", port: int = 80, timeout: float = 0.5) -> dict:
    """AWS IMDSv2 placement of this host; anything that is not a 200 with a well-formed value means "not on EC2"."""
    import re
    try:
        c = http.client.HTTPConnection(host, port, timeout=timeout)
        c.request("PUT", "/latest/api/token", headers={"X-aws-ec2-metadata-token-ttl-seconds": "60"})
        r = c.getresponse()
        token = r.read().decode("utf-8", "replace")
        if r.status != 200 or not token:
            return {"aws": f"not on EC2 (IMDS token HTTP {r.status})"}
        out = {}
        for key, field_name in (("placement/availability-zone", "availability_zone"), ("placement/region", "region"), ("instance-type", "instance_type")):
            c.request("GET", "/latest/meta-data/" + key, headers={"X-aws-ec2-metadata-token": token})
            r = c.getresponse()
            value = r.read().decode("utf-8", "replace").strip()
            if r.status != 200 or not re.fullmatch(_AWS_FORMATS[field_name], value):
                return {"aws": f"not on EC2 (IMDS {key} HTTP {r.status})"}
            out["aws_" + field_name] = value
        c.close()
        return out
    except (OSError, http.client.HTTPException):
        return {"aws": "not on EC2 or IMDS unreachable"}


def host_info() -> dict:
    info: dict = {"hostname": socket.gethostname(), "python": platform.python_version(), "platform": platform.platform()}
    info.update(aws_placement())
    for cmd in (["chronyc", "-n", "tracking"], ["timedatectl", "show", "-p", "NTPSynchronized", "-p", "TimeUSec"]):
        try:
            r = subprocess.run(cmd, capture_output=True, text=True, timeout=2)
            if r.returncode == 0:
                info["clock_" + cmd[0]] = r.stdout.strip()[:800]
        except (OSError, subprocess.SubprocessError):
            pass
    return info


# ── statistics & report ───────────────────────────────────────────────────────────────────────────────────────────
def pct(xs: list, q: float) -> Optional[float]:
    if not xs:
        return None
    s = sorted(xs)
    k = (len(s) - 1) * q
    lo, hi = int(k), min(int(k) + 1, len(s) - 1)
    return s[lo] + (s[hi] - s[lo]) * (k - lo)


def summarize(xs: list) -> dict:
    if not xs:
        return {"n": 0}
    return {"n": len(xs), "p50": round(pct(xs, 0.5), 3), "p90": round(pct(xs, 0.9), 3), "p99": round(pct(xs, 0.99), 3),
            "max": round(max(xs), 3), "mean": round(statistics.fmean(xs), 3)}


def run(args, targets: list[Target], ctx: ssl.SSLContext) -> dict:
    started = time.time()
    result = {"label": args.label, "started_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(started)),
              "interval_s": args.interval, "host": host_info(), "targets": {}}
    hosts = sorted({h for t in targets for h in (t.rest_host, t.ws_host) if h})
    result["dns"] = {h: dns_info(h) for h in hosts}
    probers = [Prober(t, ctx, args.timeout) for t in targets]
    rounds = max(1, int(args.minutes * 60 / args.interval)) if args.rounds is None else args.rounds
    try:
        for r in range(rounds):
            t0 = time.monotonic()
            order = probers[:]
            random.shuffle(order)               # no target always measured first
            for p in order:
                p.rest()
                p.wsping()
                if r % args.fresh_every == 0:
                    p.fresh()
            if args.progress and (r + 1) % max(1, rounds // 20) == 0:
                print(f"  {r + 1}/{rounds} rounds", file=sys.stderr)
            time.sleep(max(0.0, args.interval - (time.monotonic() - t0)))
    except KeyboardInterrupt:
        result["interrupted"] = True
    finally:
        for p in probers:
            p.close()
    result["ended_utc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    for p in probers:
        s = p.s
        off = s.offset_ms
        result["targets"][p.t.name] = {
            "venue": p.t.venue, "rest_host": p.t.rest_host, "ws": f"{p.t.ws_host}:{p.t.ws_port}{p.t.ws_path}" if p.t.ws_host else None,
            "rest_ms": summarize(s.rest_ms), "ws_ms": summarize(s.ws_ms), "tcp_ms": summarize(s.tcp_ms), "tls_ms": summarize(s.tls_ms),
            "offset_ms": {"n": len(off), "median": round(pct(off, 0.5), 3) if off else None,
                          "p10": round(pct(off, 0.1), 3) if off else None, "p90": round(pct(off, 0.9), 3) if off else None},
            "errors": s.errors,
            "raw": {"rest_ms": [round(x, 3) for x in s.rest_ms], "ws_ms": [round(x, 3) for x in s.ws_ms]} if args.keep_raw else None,
        }
    return result


def _fmt(x) -> str:
    return "–" if x is None else (f"{x:.1f}" if abs(x) >= 10 else f"{x:.2f}")


def leg_ms(target: dict, q: str) -> Optional[float]:
    """Origin round trip of one leg: WS ping if available (market-data path), else REST."""
    ws, rest = target.get("ws_ms", {}), target.get("rest_ms", {})
    if ws.get("n"):
        return ws.get(q)
    return rest.get(q) if rest.get("n") else None


def pair_worst_leg(run_result: dict, a: str, b: str) -> Optional[dict]:
    """Slowest of the two legs (p50 and p99 separately); None when a target is missing or never answered."""
    ta, tb = run_result["targets"].get(a), run_result["targets"].get(b)
    if not ta or not tb:
        return None
    out = {}
    for q in ("p50", "p99"):
        la, lb = leg_ms(ta, q), leg_ms(tb, q)
        out[q] = None if la is None or lb is None else max(la, lb)
    return out


def compare(files: list[str]) -> str:
    runs = []
    for f in files:
        with open(f, encoding="utf-8") as fh:
            runs.append(json.load(fh))
    lines = ["# Hasil probe latency S0", ""]
    for r in runs:
        h = r.get("host", {})
        where = h.get("aws_availability_zone") or h.get("aws", "?")
        lines.append(f"- **{r['label']}**: {r['started_utc']} → {r.get('ended_utc')}, host `{h.get('hostname')}`, AWS `{where}`"
                     + (" (dihentikan manual)" if r.get("interrupted") else ""))
    lines += ["", "## Per target (ms; p50 / p99)", "",
              "| Target | " + " | ".join(f"{r['label']} REST | {r['label']} WS | {r['label']} TCP | {r['label']} offset" for r in runs) + " |",
              "|---|" + "---|" * (4 * len(runs))]
    names = sorted({n for r in runs for n in r["targets"]})
    for n in names:
        cells = []
        for r in runs:
            t = r["targets"].get(n, {})
            rest, ws, tcp, off = t.get("rest_ms", {}), t.get("ws_ms", {}), t.get("tcp_ms", {}), t.get("offset_ms", {})
            err = sum((t.get("errors") or {}).values())
            cells += [f"{_fmt(rest.get('p50'))} / {_fmt(rest.get('p99'))}", f"{_fmt(ws.get('p50'))} / {_fmt(ws.get('p99'))}" if ws.get("n") else "–",
                      f"{_fmt(tcp.get('p50'))}", f"{_fmt(off.get('median'))} ±{_fmt((off.get('p90') or 0) - (off.get('p10') or 0))}" + (f" ({err} err)" if err else "")]
        lines.append(f"| {n} | " + " | ".join(cells) + " |")
    lines += ["", "## Pasangan spatial: kaki terlambat (max RTT origin dua venue; ms, p50 / p99)", "",
              "| Pasangan | " + " | ".join(r["label"] for r in runs) + " |", "|---|" + "---|" * len(runs)]
    best: dict = {}
    for a, b in PAIRS:
        cells = []
        for r in runs:
            worst = pair_worst_leg(r, a, b)
            if worst is None:
                cells.append("–")
                continue
            cells.append(f"{_fmt(worst['p50'])} / {_fmt(worst['p99'])}")
            if worst["p50"] is not None:
                if (a, b) not in best or worst["p50"] < best[(a, b)][1]:
                    best[(a, b)] = (r["label"], worst["p50"])
        lines.append(f"| {a} ↔ {b} | " + " | ".join(cells) + " |")
    lines += ["", "Region terbaik per pasangan (p50 kaki terlambat): "
              + "; ".join(f"{a}↔{b}: **{lab}** ({_fmt(v)} ms)" for (a, b), (lab, v) in best.items()), "",
              "Catatan: RTT dari endpoint publik (REST time / WS ping), bukan jalur order entry; offset = jam venue − jam host",
              "(+ = venue lebih cepat), valid hanya bila host tersinkron NTP (lihat field `host.clock_*`). TCP sering berhenti di",
              "edge CDN, jadi yang menentukan adalah REST/WS (origin)."]
    return "\n".join(lines)


def main(argv: Optional[list] = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--label", default=socket.gethostname(), help="name of this location, e.g. tokyo / singapore")
    ap.add_argument("--minutes", type=float, default=30.0)
    ap.add_argument("--rounds", type=int, default=None, help="override --minutes with an exact number of rounds")
    ap.add_argument("--interval", type=float, default=1.0, help="seconds between rounds (≥ 0.5: venue rate limits)")
    ap.add_argument("--fresh-every", type=int, default=10, help="new TCP+TLS connection every N rounds")
    ap.add_argument("--timeout", type=float, default=5.0)
    ap.add_argument("--only", default="", help="comma-separated target names (default: all)")
    ap.add_argument("--out", default=None, help="result JSON (default: probe_<label>.json)")
    ap.add_argument("--keep-raw", action="store_true", help="store every sample, not only percentiles")
    ap.add_argument("--progress", action="store_true")
    ap.add_argument("--compare", nargs="+", metavar="RESULT_JSON", help="print the markdown comparison of result files and exit")
    args = ap.parse_args(argv)
    if args.compare:
        print(compare(args.compare))
        return 0
    if args.interval < 0.5:
        ap.error("--interval must be ≥ 0.5 s (Binance WS allows 5 incoming msg/s, the probe stays far below)")
    targets = [t for t in DEFAULT_TARGETS if not args.only or t.name in args.only.split(",")]
    if not targets:
        ap.error("no target selected")
    ctx = ssl.create_default_context()
    print(f"probe '{args.label}': {len(targets)} targets, {args.minutes} min, interval {args.interval}s", file=sys.stderr)
    result = run(args, targets, ctx)
    out = args.out or f"probe_{args.label}.json"
    with open(out, "w", encoding="utf-8") as fh:
        json.dump(result, fh, indent=1)
    print(compare([out]))
    print(f"\nsaved {out}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
