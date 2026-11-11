#!/usr/bin/env python3
"""
ArbEngine Tahap 2 — market-data recorder for the spatial pair chosen in S0 (Binance spot <-> Bitget spot, from Tokyo).
Stdlib only, public streams only, no API key, never sends an order.

  python3 tools/md_recorder.py --out data/md                         # record until stopped (Ctrl+C / SIGTERM)
  python3 tools/md_recorder.py --out data/md --hours 72              # or for a fixed time
  python3 tools/md_recorder.py --verify data/md                      # integrity + coverage report of what was recorded

What is recorded (per symbol, defaults in DEFAULT_SYMBOLS):
  Binance  <sym>@bookTicker      best bid/ask, real time            -> top-of-book crossings, opportunity duration
           <sym>@depth20@100ms   20-level snapshot every 100 ms     -> size an opportunity against real depth
           <sym>@trade           every trade                        -> did the book move because of trading
  Bitget   books1                level-1 snapshot (default 10 ms)   -> top-of-book on the Bitget side
           books15               15-level snapshot (default 200 ms) -> depth on the Bitget side
           trade                 trades
All book channels are SNAPSHOTS: after any reconnect the book is complete again at the next message, so the recorder
needs no resync logic and a gap is only a gap, never a corrupt book.

File format (one directory per connection, one file per UTC hour, gzip):
  <out>/<conn>/<YYYYmmdd-HH>_<k>.tsv.gz      k = 00, 01, ... (a restart inside the same hour opens a new file)
  each line:  <recv_unix_ns> TAB <venue message exactly as received>
          or  <recv_unix_ns> TAB #<json meta>   (start, file, connect, disconnect, stop, clock)
Files are flushed (Z_SYNC_FLUSH) every few seconds, so a crash or power loss costs at most those seconds; --verify reads
a truncated file up to the last complete line.

Venue rules respected (sources and VERIFIED / UNVERIFIED status: docs/STAGE2_RECORDER.md):
  Binance: combined stream URL, server pings every 20 s (answered with pong), connection lives at most 24 h (we
           reconnect), <= 1024 streams per connection (we use <= 200).
  Bitget:  client sends "ping" every 30 s or is dropped after 2 min, < 50 channels per connection recommended (we use
           <= 40), subscribe request <= 4096 bytes and <= 10 subscribe messages/s (we send chunks of 20 args, 0.15 s
           apart).
"""
from __future__ import annotations

import argparse
import gzip
import http.client
import json
import os
import re
import shutil
import signal
import socket
import ssl
import subprocess
import sys
import threading
import time
import urllib.parse
import zlib
from collections import Counter
from dataclasses import dataclass, field
from typing import Callable, Iterator, Optional

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import latency_probe as lp  # noqa: E402  (shares the RFC 6455 handshake)

DEFAULT_SYMBOLS = [
    # majors: deep books, few crossings, the control group
    "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "DOGEUSDT", "ADAUSDT", "LINKUSDT", "LTCUSDT",
    # mid caps: thinner books on one side, where cross-venue gaps are more likely
    "AVAXUSDT", "SUIUSDT", "NEARUSDT", "APTUSDT", "ARBUSDT", "OPUSDT", "PEPEUSDT", "WIFUSDT",
]
BINANCE_STREAMS = ("bookTicker", "depth20@100ms", "trade")
BITGET_CHANNELS = ("books1", "books15", "trade")
EXIT_DISK_FULL = 3
MAX_FRAME = 64 << 20


def utc(ns: int) -> str:
    return time.strftime("%Y-%m-%d %H:%M:%SZ", time.gmtime(ns / 1e9))


# ── WebSocket reader that never loses bytes on a timeout ─────────────────────────────────────────────────────────────
class PollingWs(lp.WsClient):
    """
    poll(timeout) returns the complete text messages that arrived, or [] after the timeout. poll_stamped(timeout) returns
    them as (recv_unix_ns, text), stamped right after the socket read that delivered the message's last byte, BEFORE any
    processing. Messages completed by the same read share a stamp: their order is known, their spacing is not.

    Rules (review C-11 / C-12):
    - A frame is consumed only once all of it is buffered, so a timeout in the middle of a frame loses nothing (the
      recorder wakes up regularly to send Bitget's keepalive and to notice a silent connection).
    - Every complete message is delivered before a close frame, a failed pong, EOF or a protocol error is reported: the
      error is raised by the NEXT call, once. Bytes after a close frame are not messages and are discarded.
    - Complete frames already buffered (e.g. sent together with the handshake response) are returned without waiting.
    Server pings are answered with a pong carrying the same payload; fragments are reassembled, also across interleaved
    control frames; a message (all its fragments) larger than MAX_FRAME is refused.
    """

    def connect(self) -> None:
        super().connect()
        self._rbuf = bytearray(self._buf)        # bytes that arrived together with the handshake response
        self._rbuf_ns = time.time_ns()
        self._buf = b""
        self._frag: list[bytes] = []

    def poll(self, timeout: float) -> list[str]:
        return [m for _, m in self.poll_stamped(timeout)]

    def poll_stamped(self, timeout: float) -> list[tuple[int, str]]:
        self._raise_pending()
        out = self._drain(getattr(self, "_rbuf_ns", None) or time.time_ns()) if self._rbuf else []
        self._rbuf_ns = None
        if out or self._pending():
            return self._finish(out)
        self.sock.settimeout(timeout)
        try:
            chunk = self.sock.recv(1 << 18)
        except (socket.timeout, ssl.SSLWantReadError):
            return out
        except (OSError, ssl.SSLError) as e:
            self._error = e
            return self._finish(out)
        if not chunk:
            self._error = lp.WsError("connection closed by peer")
            return self._finish(out)
        while chunk:
            ns = time.time_ns()                   # the stamp: right after the read, before any parsing or writing
            self._rbuf += chunk
            out += self._drain(ns)
            pending = getattr(self.sock, "pending", None)
            chunk = self.sock.recv(pending()) if pending and pending() and not getattr(self, "_closed", False) else b""
        return self._finish(out)

    def _pending(self) -> bool:
        return getattr(self, "_error", None) is not None

    def _raise_pending(self) -> None:
        if self._pending():
            e, self._error = self._error, None
            raise e

    def _finish(self, out: list) -> list:
        if not out:
            self._raise_pending()
        return out

    def _drain(self, ns: int) -> list[tuple[int, str]]:
        b, i, out = self._rbuf, 0, []
        try:
            while len(b) - i >= 2 and not getattr(self, "_closed", False):
                b0, b1 = b[i], b[i + 1]
                n, h = b1 & 0x7F, 2
                if n == 126:
                    if len(b) - i < 4:
                        break
                    n, h = int.from_bytes(b[i + 2:i + 4], "big"), 4
                elif n == 127:
                    if len(b) - i < 10:
                        break
                    n, h = int.from_bytes(b[i + 2:i + 10], "big"), 10
                if n > MAX_FRAME:
                    self._error, self._closed = lp.WsError(f"frame of {n} bytes"), True
                    i = len(b)
                    break
                mask = None
                if b1 & 0x80:
                    if len(b) - i < h + 4:
                        break
                    mask, h = bytes(b[i + h:i + h + 4]), h + 4
                if len(b) - i < h + n:
                    break                                   # incomplete: leave every byte of it in the buffer
                payload = bytes(b[i + h:i + h + n])
                i += h + n
                if mask:
                    payload = bytes(x ^ mask[k % 4] for k, x in enumerate(payload))
                op, fin = b0 & 0x0F, b0 & 0x80
                if op == 0x9:
                    try:
                        self._send_frame(0xA, payload)
                    except (OSError, ssl.SSLError) as e:    # keep delivering what already arrived; report after
                        self._error = getattr(self, "_error", None) or e
                elif op == 0xA:
                    pass
                elif op == 0x8:
                    code = int.from_bytes(payload[:2], "big") if len(payload) >= 2 else None
                    self._error, self._closed = lp.WsError(f"server closed: code {code} {payload[2:80]!r}"), True
                    i = len(b)                              # anything after a close frame is not a message
                elif op in (0x1, 0x2, 0x0):
                    if op != 0x0:
                        self._frag = []
                    self._frag.append(payload)
                    if sum(len(f) for f in self._frag) > MAX_FRAME:
                        self._error, self._closed = lp.WsError(f"fragmented message over {MAX_FRAME} bytes"), True
                        self._frag = []
                        i = len(b)
                    elif fin:
                        out.append((ns, b"".join(self._frag).decode("utf-8", "replace")))
                        self._frag = []
        finally:
            del b[:i]
        return out


# ── connection specs: what to connect to, what to send, how to name each message's stream ──────────────────────────
@dataclass
class ConnSpec:
    name: str                       # directory name, e.g. "binance-0"
    venue: str                      # "binance" | "bitget"
    host: str
    port: int
    path: str
    streams: list[str]              # expected stream keys (lower case), for coverage checks
    subscribe: list[str] = field(default_factory=list)
    keepalive: Optional[str] = None
    keepalive_s: float = 25.0


def _chunks(xs: list, n: int) -> list[list]:
    return [xs[i:i + n] for i in range(0, len(xs), n)]


def binance_specs(symbols: list[str], host: str, port: int, per_conn: int = 200) -> list[ConnSpec]:
    streams = [f"{s.lower()}@{k}" for s in symbols for k in BINANCE_STREAMS]
    return [ConnSpec(f"binance-{i}", "binance", host, port, "/stream?streams=" + "/".join(c), [x.lower() for x in c])
            for i, c in enumerate(_chunks(streams, per_conn))]


def bitget_specs(symbols: list[str], host: str, port: int, per_conn: int = 40, per_msg: int = 20) -> list[ConnSpec]:
    args = [{"instType": "SPOT", "channel": ch, "instId": s} for s in symbols for ch in BITGET_CHANNELS]
    out = []
    for i, c in enumerate(_chunks(args, per_conn)):
        subs = [json.dumps({"op": "subscribe", "args": m}, separators=(",", ":")) for m in _chunks(c, per_msg)]
        assert all(len(s) <= 4096 for s in subs)
        out.append(ConnSpec(f"bitget-{i}", "bitget", host, port, "/v2/ws/public",
                            [f"{a['channel']}:{a['instId']}".lower() for a in c], subs, keepalive="ping", keepalive_s=25.0))
    return out


_BG_ARG = re.compile(r'"channel":"([^"]+)","instId":"([^"]+)"')


def classify(venue: str, text: str) -> tuple[str, Optional[str]]:
    """
    ('data', stream_key) | ('ack', key) | ('error', None) | ('pong', None) | ('other', None). Stream keys are lower case:
    Binance "btcusdt@bookticker", Bitget "books1:btcusdt". Fast path without a JSON parse for market data.
    """
    if text == "pong":
        return "pong", None
    if venue == "binance" and text.startswith('{"stream":"'):
        end = text.find('"', 11)
        if end > 11:
            return "data", text[11:end].lower()
    if venue == "bitget" and text.startswith('{"action"'):
        m = _BG_ARG.search(text, 0, 200)
        if m:
            return "data", f"{m[1]}:{m[2]}".lower()
    try:
        j = json.loads(text)
    except ValueError:
        return "other", None
    if not isinstance(j, dict):
        return "other", None
    if venue == "binance":
        if "stream" in j and "data" in j:
            return "data", str(j["stream"]).lower()
        if "error" in j or "code" in j:
            return "error", None
        if "result" in j and "id" in j:
            return "ack", None
        return "other", None
    arg = j.get("arg") or {}
    key = f"{arg.get('channel')}:{arg.get('instId')}".lower() if arg else None
    if j.get("event") == "error":
        return "error", key
    if j.get("event") == "subscribe":
        return "ack", key
    if "action" in j and key:
        return "data", key
    return "other", None


# ── output: one gzip file per connection per UTC hour ──────────────────────────────────────────────────────────────
class HourlyWriter:
    """
    Durability (review C-14): written lines reach the OS at least every flush_s seconds, also when the feed is idle
    (flush_if_due, called by record() every tick), and are fsync'ed right after. A process crash loses at most the last
    ~flush_s seconds; a VM/host crash at most ~flush_s plus the fsync in flight. Rotation fsyncs the closed file.
    """

    def __init__(self, root: str, conn: str, flush_s: float = 5.0, level: int = 6, header: Callable[[], dict] = dict,
                 clock: Callable[[], float] = time.monotonic):
        self.dir = os.path.join(root, conn)
        os.makedirs(self.dir, exist_ok=True)
        self.flush_s, self.level, self.header, self.clock = flush_s, level, header, clock
        self._dirty = False
        self._unsynced = False
        self._last_sync = 0.0
        self._lock = threading.Lock()
        self._hour: Optional[int] = None
        self._raw = None
        self._gz: Optional[gzip.GzipFile] = None
        self._last_flush = 0.0
        self.closed_bytes = 0
        self.path: Optional[str] = None

    def _open(self, ns: int) -> None:
        self._close_file()
        hour = ns // 3_600_000_000_000
        stamp = time.strftime("%Y%m%d-%H", time.gmtime(hour * 3600))
        k = 0
        while os.path.exists(os.path.join(self.dir, f"{stamp}_{k:02d}.tsv.gz")):
            k += 1
        self.path = os.path.join(self.dir, f"{stamp}_{k:02d}.tsv.gz")
        self._raw = open(self.path, "wb")
        self._gz = gzip.GzipFile(fileobj=self._raw, mode="wb", compresslevel=self.level, mtime=0)
        self._hour = hour
        self._put(ns, "#" + json.dumps({"ev": "file", **self.header()}, separators=(",", ":")))

    def _put(self, ns: int, payload: str) -> None:
        if "\n" in payload or "\r" in payload:          # JSON whitespace only; keeps one message per line
            payload = payload.replace("\r", " ").replace("\n", " ")
        self._gz.write(f"{ns}\t{payload}\n".encode())
        self._dirty = True
        now = self.clock()
        if now - self._last_flush >= self.flush_s:
            self._gz.flush()                            # Z_SYNC_FLUSH: everything so far is readable after a crash
            self._last_flush, self._dirty, self._unsynced = now, False, True

    def flush_if_due(self) -> None:
        """Timer side of the flush: an idle feed must not keep its tail in the compressor (C-14)."""
        fd = None
        with self._lock:
            if not self._gz:
                return
            now = self.clock()
            if self._dirty and now - self._last_flush >= self.flush_s:
                self._gz.flush()
                self._last_flush, self._dirty, self._unsynced = now, False, True
            if self._unsynced and now - self._last_sync >= self.flush_s:
                fd, self._last_sync, self._unsynced = self._raw.fileno(), now, False
        if fd is not None:
            try:
                os.fsync(fd)                            # outside the lock: the reader thread never waits on the disk
            except OSError:
                pass                                    # file rotated/closed meanwhile: rotation fsynced it

    def write(self, ns: int, payload: str) -> None:
        with self._lock:
            if self._hour != ns // 3_600_000_000_000:
                self._open(ns)
            self._put(ns, payload)

    def meta(self, ns: int, ev: str, **kv) -> None:
        self.write(ns, "#" + json.dumps({"ev": ev, **kv}, separators=(",", ":")))

    def size(self) -> int:
        with self._lock:
            return self.closed_bytes + (self._raw.tell() if self._raw else 0)

    def _close_file(self) -> None:
        if self._gz:
            self._gz.close()
            self.closed_bytes += self._raw.tell()
            self._raw.flush()
            try:
                os.fsync(self._raw.fileno())
            except OSError:
                pass
            self._raw.close()
        self._gz = self._raw = None

    def close(self) -> None:
        with self._lock:
            self._close_file()
            self._hour = None


# ── one thread per connection ──────────────────────────────────────────────────────────────────────────────────────
class ConnRecorder(threading.Thread):
    def __init__(self, spec: ConnSpec, writer: HourlyWriter, ctx: ssl.SSLContext, stop: threading.Event,
                 stale_s: float = 30.0, timeout: float = 10.0, backoff: tuple[float, float] = (1.0, 60.0),
                 poll_s: float = 0.5, log: Callable[[str], None] = print, stats_s: float = 60.0):
        super().__init__(name=spec.name, daemon=True)
        self.spec, self.writer, self.ctx, self.stop = spec, writer, ctx, stop
        self.stale_s, self.timeout, self.backoff, self.poll_s, self.log = stale_s, timeout, backoff, poll_s, log
        self.stats_s = stats_s
        self._st_reads: list[int] = []       # messages per socket read
        self._st_lag: list[int] = []         # µs from a poll's first read stamp to the end of writing that poll's batch
        self._st_t0 = time.monotonic()
        self.connected = False
        self.sessions = 0
        self.msgs = 0
        self.counts: Counter = Counter()
        self.errors: list[str] = []
        self.pongs = 0

    def run(self) -> None:
        delay = self.backoff[0]
        self.writer.meta(time.time_ns(), "start", conn=self.spec.name, venue=self.spec.venue, host=self.spec.host,
                         path=self.spec.path[:200], streams=self.spec.streams)
        try:
            while not self.stop.is_set():
                started = time.monotonic()
                reason = "stopped"
                try:
                    self._session()
                except (OSError, lp.WsError, ssl.SSLError, ValueError) as e:
                    reason = f"{type(e).__name__}: {e}"[:300]
                except _Stale as e:
                    reason = str(e)
                finally:
                    if self.connected:
                        self.connected = False
                        self.writer.meta(time.time_ns(), "disconnect", reason=reason)
                if self.stop.is_set():
                    break
                if time.monotonic() - started > 60:
                    delay = self.backoff[0]
                self.log(f"{self.spec.name}: {reason}; reconnect in {delay:.0f} s")
                self.stop.wait(delay)
                delay = min(delay * 2, self.backoff[1])
        finally:
            self.writer.meta(time.time_ns(), "stop")
            self.writer.close()

    def _session(self) -> None:
        ws = PollingWs(self.spec.host, self.spec.port, self.spec.path, self.ctx, self.timeout)
        try:
            ws.connect()
            peer, peer_port = ws.sock.getpeername()[:2]
            self.sessions += 1
            self.connected = True
            self.writer.meta(time.time_ns(), "connect", peer=peer, peer_port=peer_port, local_port=ws.sock.getsockname()[1],
                             session=self.sessions)
            for i, sub in enumerate(self.spec.subscribe):
                if i:
                    self._pump(ws, 0.15)                    # keep reading (and stamping) while pacing requests
                ws.send_text(sub)
            last_data = last_ka = time.monotonic()
            while not self.stop.is_set():
                batch = ws.poll_stamped(self.poll_s)
                now = time.monotonic()
                if batch and self._deliver(batch):
                    last_data = now
                if self.spec.keepalive and now - last_ka >= self.spec.keepalive_s:
                    ws.send_text(self.spec.keepalive)
                    last_ka = now
                if now - last_data > self.stale_s:
                    raise _Stale(f"no market data for {self.stale_s:.0f} s")
        finally:
            ws.close()

    def _pump(self, ws: PollingWs, seconds: float) -> None:
        end = time.monotonic() + seconds
        while not self.stop.is_set():
            left = end - time.monotonic()
            if left <= 0:
                return
            batch = ws.poll_stamped(left)
            if batch:
                self._deliver(batch)

    def _deliver(self, batch: list[tuple[int, str]]) -> bool:
        """Write one poll's messages with their read stamps; measure how long that held up the next read (C-12)."""
        data = False
        for ns, text in batch:
            if self._on_text(ns, text):
                data = True
        self._st_lag.append((time.time_ns() - batch[0][0]) // 1000)
        run, prev = 0, batch[0][0]
        for ns, _ in batch:
            if ns != prev:
                self._st_reads.append(run)
                run, prev = 0, ns
            run += 1
        self._st_reads.append(run)
        if time.monotonic() - self._st_t0 >= self.stats_s:
            self._write_stats()
        return data

    def _write_stats(self) -> None:
        reads, lag = sorted(self._st_reads), sorted(self._st_lag)
        if reads:
            pick = lambda xs, q: xs[min(len(xs) - 1, max(0, -(-len(xs) * q // 100) - 1))]  # noqa: E731 nearest rank
            self.writer.meta(time.time_ns(), "stats", s=round(time.monotonic() - self._st_t0, 1), reads=len(reads),
                             msgs=sum(reads), batch_p99=pick(reads, 99), batch_max=reads[-1], lag_p50_us=pick(lag, 50),
                             lag_p99_us=pick(lag, 99), lag_max_us=lag[-1])
        self._st_reads, self._st_lag, self._st_t0 = [], [], time.monotonic()

    def _on_text(self, ns: int, text: str) -> bool:
        kind, key = classify(self.spec.venue, text)
        if kind == "pong":
            self.pongs += 1
            return False
        self.writer.write(ns, text)
        self.msgs += 1
        if kind == "data":
            self.counts[key] += 1
            return True
        if kind == "error":
            self.errors.append(text[:300])
            self.log(f"{self.spec.name}: venue error {text[:300]}")
        return False


class _Stale(Exception):
    pass


# ── preflight: are the symbols listed and trading on both venues? ──────────────────────────────────────────────────
def _get_json(host: str, port: int, path: str, ctx: ssl.SSLContext, timeout: float) -> tuple[int, object]:
    conn = http.client.HTTPSConnection(host, port, context=ctx, timeout=timeout)
    try:
        conn.request("GET", path, headers={"User-Agent": "arbengine-md-recorder"})
        r = conn.getresponse()
        body = r.read()
        try:
            return r.status, json.loads(body)
        except ValueError:
            return r.status, None
    finally:
        conn.close()


def preflight(symbols: list[str], ctx: ssl.SSLContext, binance=("api.binance.com", 443), bitget=("api.bitget.com", 443),
              timeout: float = 5.0, log: Callable[[str], None] = print) -> list[str]:
    """
    Keep a symbol unless a venue gives an AUTHORITATIVE answer that it does not trade (review C-13):
      Binance  200 with this symbol's row and status != TRADING, or 400 with code -1121 ("Invalid symbol.")
      Bitget   200 with code "00000" and a data list that has no row for this symbol, or whose row status != "online"
    Everything else (network error, 5xx/429, non-JSON, other error codes, an answer about another symbol, a missing
    status) is "not checked" and keeps the symbol: a transient error must never shrink the recorded universe. The
    recorder's silent-stream warning and --verify are the second line of defence.
    """
    keep = []
    for s in symbols:
        verdicts = []
        try:
            st, j = _get_json(*binance, f"/api/v3/exchangeInfo?symbol={urllib.parse.quote(s)}", ctx, timeout)
            rows = j.get("symbols") if isinstance(j, dict) else None
            row = next((r for r in rows if isinstance(r, dict) and r.get("symbol") == s), None) if isinstance(rows, list) else None
            if st == 200 and row is not None and isinstance(row.get("status"), str):
                if row["status"] != "TRADING":
                    verdicts.append(f"Binance: status {row['status']}")
            elif st == 400 and isinstance(j, dict) and j.get("code") == -1121:
                verdicts.append("Binance: not listed")
            else:
                code = j.get("code") if isinstance(j, dict) else None
                log(f"preflight {s}: Binance answered HTTP {st} (code {code}, no row for {s}), not checked")
        except (OSError, http.client.HTTPException) as e:
            log(f"preflight {s}: Binance unreachable ({type(e).__name__}), not checked")
        try:
            st, j = _get_json(*bitget, f"/api/v2/spot/public/symbols?symbol={urllib.parse.quote(s)}", ctx, timeout)
            data = j.get("data") if isinstance(j, dict) else None
            if st == 200 and j.get("code") == "00000" and isinstance(data, list):
                row = next((d for d in data if isinstance(d, dict) and d.get("symbol") == s), None)
                if row is None:
                    verdicts.append("Bitget: not listed")
                elif not isinstance(row.get("status"), str):
                    log(f"preflight {s}: Bitget row without status, not checked")
                elif row["status"] != "online":
                    verdicts.append(f"Bitget: status {row['status']}")
            else:
                code = j.get("code") if isinstance(j, dict) else None
                log(f"preflight {s}: Bitget answered HTTP {st} (code {code}), not checked")
        except (OSError, http.client.HTTPException) as e:
            log(f"preflight {s}: Bitget unreachable ({type(e).__name__}), not checked")
        if verdicts:
            log(f"preflight: DROP {s} ({'; '.join(verdicts)})")
        else:
            keep.append(s)
    return keep


# ── host clock (for the replay: is recv time trustworthy?) ─────────────────────────────────────────────────────────
def clock_status() -> dict:
    try:
        r = subprocess.run(["chronyc", "-n", "-c", "tracking"], capture_output=True, text=True, timeout=2)
        if r.returncode == 0:
            f = r.stdout.strip().split(",")
            # CSV fields: ref id, ref name, stratum, ref time, system time offset (s), last offset (s), rms offset (s), ...
            return {"chrony_ref": f[1] if len(f) > 1 else None, "chrony_stratum": f[2] if len(f) > 2 else None,
                    "chrony_offset_s": f[4] if len(f) > 4 else None, "chrony_rms_s": f[6] if len(f) > 6 else None}
    except (OSError, subprocess.SubprocessError):
        pass
    return {"chrony": "unavailable"}


# ── run ────────────────────────────────────────────────────────────────────────────────────────────────────────────
def record(specs: list[ConnSpec], out: str, ctx: ssl.SSLContext, stop: threading.Event, hours: Optional[float] = None,
           status_s: float = 60.0, min_free_gb: float = 5.0, stale_s: float = 30.0, poll_s: float = 0.5,
           free_bytes: Callable[[str], int] = lambda p: shutil.disk_usage(p).free,
           log: Callable[[str], None] = print, flush_s: float = 5.0, stats_s: float = 60.0) -> int:
    os.makedirs(out, exist_ok=True)
    clock = {"v": clock_status(), "t": time.monotonic()}
    header = lambda: {"host": socket.gethostname(), **clock["v"]}  # noqa: E731
    recs = [ConnRecorder(s, HourlyWriter(out, s.name, flush_s=flush_s, header=header), ctx, stop, stale_s=stale_s,
                         poll_s=poll_s, log=log, stats_s=stats_s) for s in specs]
    for r in recs:
        r.start()
    end = time.monotonic() + hours * 3600 if hours else None
    code = 0
    last = {r.spec.name: (0, Counter()) for r in recs}
    t_status = time.monotonic()
    started = time.monotonic()
    try:
        while not stop.is_set():
            stop.wait(min(1.0, status_s))
            now = time.monotonic()
            if end and now >= end:
                log("recording time reached")
                break
            if free_bytes(out) < min_free_gb * 1e9:
                log(f"STOP: less than {min_free_gb} GB free on the data disk")
                code = EXIT_DISK_FULL
                break
            for r in recs:
                r.writer.flush_if_due()
            if now - clock["t"] >= 600:
                clock.update(v=clock_status(), t=now)
            if now - t_status >= status_s:
                dt = now - t_status
                t_status = now
                parts, warns = [], []
                for r in recs:
                    m0, c0 = last[r.spec.name]
                    snap = Counter(dict.copy(r.counts))      # C-level copy: safe while the thread adds keys
                    silent = [k for k in r.spec.streams if snap[k] == c0[k]]
                    last[r.spec.name] = (r.msgs, snap)
                    parts.append(f"{r.spec.name} {'UP' if r.connected else 'DOWN'} {(r.msgs - m0) / dt:.0f} msg/s "
                                 f"{r.writer.size() / 1e6:.0f} MB s{r.sessions}")
                    books = [k for k in silent if not k.endswith("trade") and not k.startswith("trade:")]
                    if r.connected and books and now - started > 2 * status_s:
                        warns.append(f"{r.spec.name}: no book data in {dt:.0f} s for {', '.join(books[:6])}"
                                     + (f" (+{len(books) - 6})" if len(books) > 6 else ""))
                total = sum(r.writer.size() for r in recs)
                rate = total / max(now - started, 1) * 86400
                log(f"[{utc(time.time_ns())}] " + " | ".join(parts)
                    + f" | total {total / 1e9:.2f} GB, ~{rate / 1e9:.1f} GB/day, free {free_bytes(out) / 1e9:.0f} GB")
                for w in warns:
                    log("WARN " + w)
    finally:
        stop.set()
        for r in recs:
            r.join(timeout=15)
    return code


# ── verify: what did we actually get? ──────────────────────────────────────────────────────────────────────────────
_E = re.compile(r'"E":\s*(\d+)')
_TS = re.compile(r'"ts":\s*"?(\d+)')


def read_file(path: str, info: Optional[dict] = None) -> Iterator[tuple[int, str]]:
    """
    (recv_ns, payload) of one recording file, in arrival order. A truncated tail (crash, power loss) ends the iteration
    after the last complete line instead of raising; info["truncated"] tells whether that happened.
    """
    if info is not None:
        info["truncated"] = False
    with gzip.open(path, "rb") as f:
        try:
            for raw in f:
                if not raw.endswith(b"\n"):
                    break
                ns, sep, payload = raw[:-1].decode("utf-8", "replace").partition("\t")
                if sep and ns.isdigit():
                    yield int(ns), payload
            else:
                return
        except (EOFError, gzip.BadGzipFile, zlib.error, OSError):
            pass
    if info is not None:
        info["truncated"] = True


def _file_key(name: str) -> tuple:
    stem = name.split(".", 1)[0]
    hour, _, k = stem.partition("_")
    return hour, int(k) if k.isdigit() else 0


def _hist_pct(h: Counter, q: float) -> Optional[int]:
    """Nearest-rank percentile of a histogram {value: count}: never below the q-th sample (conservative for tails)."""
    n = sum(h.values())
    if not n:
        return None
    rank, acc = max(1, -(-int(q * n * 1_000_000) // 1_000_000)), 0
    for v in sorted(h):
        acc += h[v]
        if acc >= rank:
            return v
    return max(h)


def verify_conn(dirpath: str) -> dict:
    files = sorted((f for f in os.listdir(dirpath) if f.endswith(".tsv.gz")), key=_file_key)
    rep = {"conn": os.path.basename(dirpath), "files": len(files), "truncated": 0, "lines": 0, "data": 0,
           "bytes": sum(os.path.getsize(os.path.join(dirpath, f)) for f in files), "first_ns": None, "last_ns": None,
           "sessions": 0, "down_s": 0.0, "errors": 0, "streams": {}, "expected": [], "reasons": Counter(),
           "reader": {"minutes": 0, "reads": 0, "msgs": 0, "batch_max": 0, "lag_p99_us": None, "lag_max_us": None}}
    venue = "binance" if rep["conn"].startswith("binance") else "bitget"
    last_by: dict = {}
    gap_by: dict = {}
    cnt: Counter = Counter()
    lag: dict = {}
    down_since: Optional[int] = None
    last_ns: Optional[int] = None
    for f in files:
        info: dict = {}
        for ns, payload in read_file(os.path.join(dirpath, f), info):
            rep["lines"] += 1
            if rep["first_ns"] is None:
                rep["first_ns"] = ns
            if payload.startswith("#"):
                try:
                    m = json.loads(payload[1:])
                except ValueError:
                    continue
                ev = m.get("ev")
                if ev == "stats":
                    rd = rep["reader"]
                    rd["minutes"] += 1
                    rd["reads"] += int(m.get("reads", 0))
                    rd["msgs"] += int(m.get("msgs", 0))
                    rd["batch_max"] = max(rd["batch_max"], int(m.get("batch_max", 0)))
                    for k in ("lag_p99_us", "lag_max_us"):
                        if m.get(k) is not None:
                            rd[k] = max(rd[k] or 0, int(m[k]))
                    continue
                if ev == "start":
                    rep["expected"] = sorted(set(rep["expected"]) | set(m.get("streams") or []))
                    if down_since is None and last_ns is not None:
                        down_since = last_ns                       # previous run ended without "stop" (crash)
                elif ev == "connect":
                    rep["sessions"] += 1
                    if down_since is not None:
                        rep["down_s"] += (ns - down_since) / 1e9
                        down_since = None
                elif ev in ("disconnect", "stop"):
                    if down_since is None:
                        down_since = ns
                    if ev == "disconnect":
                        rep["reasons"][str(m.get("reason", ""))[:60]] += 1
                if ev != "file":                                   # a new file's header is not activity
                    last_ns = ns
                continue
            last_ns = ns
            kind, key = classify(venue, payload)
            if kind == "error":
                rep["errors"] += 1
            if kind != "data":
                continue
            rep["data"] += 1
            cnt[key] += 1
            prev = last_by.get(key)
            if prev is not None and ns - prev > gap_by.get(key, 0):
                gap_by[key] = ns - prev
            last_by[key] = ns
            if venue == "bitget" and key.startswith("trade:") and payload.startswith('{"action":"snapshot"'):
                continue                                   # trade history replayed on subscribe: old by design, not lag
            m = (_E if venue == "binance" else _TS).search(payload)
            if m:
                lag.setdefault(key, Counter())[round(ns / 1e6 - int(m[1]))] += 1
        rep["truncated"] += info["truncated"]
    rep["last_ns"] = last_ns
    span = (rep["last_ns"] - rep["first_ns"]) / 1e9 if rep["first_ns"] and rep["last_ns"] else 0.0
    rep["span_s"] = span
    rep["coverage"] = max(0.0, 1 - rep["down_s"] / span) if span else 0.0
    for key in sorted(set(cnt) | set(rep["expected"])):
        h = lag.get(key, Counter())
        rep["streams"][key] = {"n": cnt[key], "per_s": cnt[key] / span if span else 0.0,
                               "max_gap_s": gap_by.get(key, 0) / 1e9, "lag_p50_ms": _hist_pct(h, 0.5),
                               "lag_p99_ms": _hist_pct(h, 0.99)}
    return rep


def verify(roots: list[str]) -> tuple[str, list[dict]]:
    dirs = []
    for root in roots:
        for d in sorted(os.listdir(root)):
            p = os.path.join(root, d)
            if os.path.isdir(p) and any(f.endswith(".tsv.gz") for f in os.listdir(p)):
                dirs.append(p)
    reps = [verify_conn(d) for d in dirs]
    lines = ["# Rekaman market data — verifikasi", ""]
    for r in reps:
        if not r["first_ns"]:
            lines.append(f"- **{r['conn']}**: kosong")
            continue
        silent = [k for k, s in r["streams"].items() if s["n"] == 0]
        lines.append(f"- **{r['conn']}**: {utc(r['first_ns'])} → {utc(r['last_ns'])} ({r['span_s'] / 3600:.2f} jam), "
                     f"coverage {r['coverage'] * 100:.2f} %, {r['sessions']} sesi, putus total {r['down_s']:.0f} s, "
                     f"{r['files']} file ({r['bytes'] / 1e6:.0f} MB, {r['truncated']} terpotong), "
                     f"{r['data']:,} pesan data, {r['errors']} error venue"
                     + (f", **{len(silent)} stream TANPA data**: {', '.join(silent[:8])}" if silent else ""))
        for reason, n in r["reasons"].most_common(3):
            lines.append(f"  - putus {n}×: {reason}")
    lines += ["", "## Reader lag (C-12)", "",
              "Waktu dari stamp read pertama sebuah poll sampai batch itu selesai ditulis = berapa lama read berikutnya",
              "tertahan oleh pemrosesan kita. Pesan dengan stamp yang sama berasal dari satu read: urutannya pasti, jaraknya tidak.",
              "", "| Koneksi | Menit | Read | Pesan/read rata2 | Batch maks (pesan/read) | Lag p99 menit terburuk (µs) | Lag maks (µs) |",
              "|---|---|---|---|---|---|---|"]
    for r in reps:
        rd = r["reader"]
        if rd["minutes"]:
            lines.append(f"| {r['conn']} | {rd['minutes']} | {rd['reads']:,} | {rd['msgs'] / max(rd['reads'], 1):.2f} | "
                         f"{rd['batch_max']} | {rd['lag_p99_us']:,} | {rd['lag_max_us']:,} |")
        else:
            lines.append(f"| {r['conn']} | 0 | – | – | – | – | – |")
    lines += ["", "| Koneksi | Stream | Pesan | Pesan/s | Gap terpanjang (s) | Umur data p50 / p99 (ms) |", "|---|---|---|---|---|---|"]
    for r in reps:
        for k, s in r["streams"].items():
            age = f"{s['lag_p50_ms']} / {s['lag_p99_ms']}" if s["lag_p50_ms"] is not None else "–"
            lines.append(f"| {r['conn']} | {k} | {s['n']:,} | {s['per_s']:.1f} | {s['max_gap_s']:.1f} | {age} |")
    lines += ["", "Umur data = jam terima host − timestamp venue (Binance `E`, Bitget `ts`); termasuk selisih jam host–venue.",
              "Gap terpanjang termasuk waktu koneksi putus; stream trade di simbol sepi wajar punya gap panjang."]
    return "\n".join(lines), reps


# ── CLI ────────────────────────────────────────────────────────────────────────────────────────────────────────────
def main(argv: Optional[list] = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0], formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default="data/md", help="output directory (default data/md)")
    ap.add_argument("--symbols", default=",".join(DEFAULT_SYMBOLS), help="comma-separated, e.g. BTCUSDT,ETHUSDT")
    ap.add_argument("--venues", default="binance,bitget")
    ap.add_argument("--hours", type=float, default=None, help="stop after this many hours (default: until stopped)")
    ap.add_argument("--min-free-gb", type=float, default=5.0, help="stop cleanly when free disk drops below this")
    ap.add_argument("--status-s", type=float, default=60.0)
    ap.add_argument("--no-preflight", action="store_true", help="skip the REST check that every symbol trades on both venues")
    ap.add_argument("--verify", nargs="+", metavar="DIR", help="report on recorded data instead of recording")
    a = ap.parse_args(argv)

    if a.verify:
        text, _ = verify(a.verify)
        print(text)
        return 0
    venues = [v.strip() for v in a.venues.split(",") if v.strip()]
    if not venues or any(v not in ("binance", "bitget") for v in venues):
        ap.error("--venues: binance and/or bitget")
    if a.hours is not None and a.hours <= 0:
        ap.error("--hours must be > 0")
    if a.status_s < 5:
        ap.error("--status-s must be >= 5")
    symbols = [s.strip().upper() for s in a.symbols.split(",") if s.strip()]
    if not symbols or any(not re.fullmatch(r"[A-Z0-9]{2,20}", s) for s in symbols):
        ap.error("--symbols: comma-separated symbols like BTCUSDT")
    ctx = ssl.create_default_context()
    if not a.no_preflight:
        symbols = preflight(symbols, ctx)
        if not symbols:
            print("no symbol trades on both venues; nothing to record", file=sys.stderr)
            return 2
    specs = []
    if "binance" in venues:
        specs += binance_specs(symbols, "stream.binance.com", 9443)
    if "bitget" in venues:
        specs += bitget_specs(symbols, "ws.bitget.com", 443)
    stop = threading.Event()
    for sig in (signal.SIGINT, signal.SIGTERM):
        signal.signal(sig, lambda *_: stop.set())
    print(f"recording {len(symbols)} symbols on {', '.join(venues)} into {os.path.abspath(a.out)}: "
          + ", ".join(f"{s.name} ({len(s.streams)} streams)" for s in specs), flush=True)
    return record(specs, a.out, ctx, stop, hours=a.hours, status_s=a.status_s, min_free_gb=a.min_free_gb,
                  log=lambda m: print(m, flush=True))


if __name__ == "__main__":
    sys.exit(main())
