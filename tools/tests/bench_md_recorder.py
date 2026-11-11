"""
Re-runnable throughput benchmark of the recorder's per-message path (review C-12 asked for the harness):
socket read -> WebSocket parse -> classify -> gzip line write, single thread, no TLS, no network.
Message mix: 80 % Binance bookTicker (~150 B), 20 % depth20 (~1.3 kB), as seen on md-tokyo.

  python3 tools/tests/bench_md_recorder.py            # prints msg/s on this machine
"""
from __future__ import annotations

import json
import os
import random
import shutil
import socket
import sys
import tempfile
import threading
import time

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
sys.path.insert(0, os.path.dirname(__file__))
import md_recorder as md  # noqa: E402
from test_md_recorder import _frame  # noqa: E402


def messages(n: int, seed: int = 1) -> list[str]:
    rnd = random.Random(seed)
    out = []
    for i in range(n):
        if i % 5 == 0:
            out.append(json.dumps({"stream": "btcusdt@depth20@100ms", "data": {
                "lastUpdateId": 123456789 + i,
                "bids": [[f"{60000 - k * 0.01:.2f}", f"{rnd.random():.5f}"] for k in range(20)],
                "asks": [[f"{60000 + k * 0.01:.2f}", f"{rnd.random():.5f}"] for k in range(20)]}}, separators=(",", ":")))
        else:
            out.append(json.dumps({"stream": "btcusdt@bookTicker", "data": {
                "u": 400900217 + i, "s": "BTCUSDT", "b": "60000.01", "B": f"{rnd.random():.8f}", "a": "60000.02",
                "A": "0.5"}}, separators=(",", ":")))
    return out


def run(rounds: int = 25, per_round: int = 4000) -> float:
    msgs = messages(per_round)
    blob = b"".join(_frame(0x1, m.encode()) for m in msgs)
    a, b = socket.socketpair()
    ws = md.PollingWs("x", 0, "/", None, 1.0)
    ws.sock, ws._rbuf, ws._frag = a, bytearray(), []
    d = tempfile.mkdtemp()
    w = md.HourlyWriter(d, "binance-0")
    rec = md.ConnRecorder(md.ConnSpec("binance-0", "binance", "x", 0, "/", []), w, None, threading.Event())
    threading.Thread(target=lambda: [b.sendall(blob) for _ in range(rounds)], daemon=True).start()
    want, got, t0 = rounds * per_round, 0, time.perf_counter()
    while got < want:
        batch = ws.poll_stamped(1.0)
        if batch:
            rec._deliver(batch)
        got += len(batch)
    el = time.perf_counter() - t0
    w.close()
    a.close()
    b.close()
    shutil.rmtree(d, ignore_errors=True)
    return got / el


if __name__ == "__main__":
    rates = sorted(run() for _ in range(3))
    print(f"recorder per-message path: median {rates[1]:,.0f} msg/s on one core (runs: "
          + ", ".join(f"{r:,.0f}" for r in rates) + ")")
