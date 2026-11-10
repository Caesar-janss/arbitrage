# Tahap 2, langkah 1 — Market-data recorder (Binance ↔ Bitget, Tokyo)

> Tujuan: menjawab pertanyaan yang **belum** dijawab S0, yaitu apakah pasangan Binance ↔ Bitget punya peluang arbitrase
> yang nyata setelah fee dan slippage, seberapa sering, dan berapa lama peluang itu bertahan dibanding latency Bitget
> (p99 sampai ~250 ms di sesi EU/US, `docs/S0_RESULTS.md`). Order entry baru dikerjakan bila rekaman menunjukkan peluang.

`tools/md_recorder.py` hanya memakai Python stdlib dan stream publik. Tidak butuh API key dan tidak pernah mengirim order.

## 1. Menjalankan (Jansen)

**VM:** GCP `asia-northeast1` (Tokyo), `c4-standard-2` atau `e2-standard-2` (CPU kecil: lihat §4), Ubuntu 24.04,
**disk 200 GB** (perkiraan 10–15 GB/hari, angka pasti terlihat di status line pada jam pertama). Jam host harus sinkron
(Ubuntu GCP memakai chrony ke metadata server; sudah benar di probe S0).

```bash
git clone https://github.com/Caesar-janss/arbitrage.git && cd arbitrage
python3 tools/md_recorder.py --out ~/md --hours 0.05      # uji 3 menit di foreground: lihat status line, lalu selesai sendiri
bash tools/install_md_recorder_service.sh                 # pasang sebagai service: jalan terus, restart sendiri, tahan reboot
```

- Service ditulis ke `/etc/systemd/system/md-recorder.service` dan dijalankan sebagai user Anda. Exit code 2 (tidak ada
  simbol yang aktif di kedua venue) dan 3 (disk hampir penuh) **tidak** di-restart.
- Log langsung: `journalctl -u md-recorder -f` (Ctrl+C hanya keluar dari log, rekaman jalan terus).
- Cek isi rekaman kapan saja: `python3 tools/md_recorder.py --verify ~/md`.
- Berhenti: `sudo systemctl stop md-recorder`. Data tetap di `~/md`.

### Status line (setiap 60 detik)

```
[2026-11-11 02:00:00Z] binance-0 UP 850 msg/s 412 MB s1 | bitget-0 UP 610 msg/s 380 MB s1 | bitget-1 UP 590 msg/s 371 MB s1 | total 1.16 GB, ~11.9 GB/day, free 171 GB
```

`UP/DOWN` = koneksi, `sN` = jumlah sesi (naik setiap reconnect), `~GB/day` = laju disk sejak start. Baris `WARN ... no book
data` berarti stream order book diam satu interval penuh padahal koneksi UP: simbol salah, delisting, atau venue berubah.
Satu-dua kali saat pasar sepi wajar; terus-menerus berarti harus dicek.

## 2. Apa yang direkam

| Venue | Stream / channel | Isi | Untuk |
|---|---|---|---|
| Binance | `<sym>@bookTicker` | best bid/ask, real time | kapan harga dua venue bersilang, berapa lama |
| Binance | `<sym>@depth20@100ms` | 20 level, snapshot tiap 100 ms | ukuran yang bisa dieksekusi di harga itu |
| Binance | `<sym>@trade` | setiap trade | apakah harga bergerak karena trading |
| Bitget | `books1` | level 1, snapshot (default 10 ms) | top of book sisi Bitget |
| Bitget | `books15` | 15 level, snapshot (default 200 ms) | kedalaman sisi Bitget |
| Bitget | `trade` | setiap trade | idem |

Simbol default (`DEFAULT_SYMBOLS`): 8 major (BTC, ETH, SOL, XRP, DOGE, ADA, LINK, LTC) sebagai kelompok kontrol, dan
8 mid cap (AVAX, SUI, NEAR, APT, ARB, OP, PEPE, WIF) yang book-nya lebih tipis, tempat selisih antar venue lebih mungkin.
Sebelum mulai, preflight REST membuang simbol yang tidak TRADING di Binance atau tidak `online` di Bitget.

Semua channel order book adalah **snapshot**, jadi setelah reconnect book langsung utuh lagi di pesan berikutnya. Tidak ada
logika resync, dan gap hanya berarti data hilang, tidak pernah book yang rusak.

**Format file:** `<out>/<koneksi>/<YYYYmmdd-HH>_<k>.tsv.gz` (satu file per koneksi per jam UTC; restart di jam yang sama
membuat `_01`, `_02`, bukan menimpa). Setiap baris: `<recv_unix_ns>␉<pesan venue persis seperti diterima>` atau
`<recv_unix_ns>␉#<meta json>` (`start`, `file` + status chrony, `connect` + IP/port peer, `disconnect` + alasan, `stop`).
File di-flush (Z_SYNC_FLUSH) tiap 5 detik, jadi crash atau mati listrik paling banyak kehilangan 5 detik terakhir.
`md_recorder.read_file()` membaca file terpotong sampai baris lengkap terakhir; replay berikutnya memakai fungsi ini.

## 3. Aturan venue dan sumbernya

| Aturan | Dipakai | Status | Sumber |
|---|---|---|---|
| Binance combined stream `wss://stream.binance.com:9443/stream?streams=a/b/c`, nama stream `@bookTicker`, `@depth20@100ms`, `@trade`, payload `{"stream","data"}` | URL & parser | VERIFIED 2026-11-10 | [Binance WebSocket Streams](https://developers.binance.com/docs/binance-spot-api-docs/web-socket-streams) |
| Binance: server ping ~20 s → pong, koneksi maks 24 jam, ≤ 1024 stream per koneksi | auto-pong, reconnect, ≤ 200 stream | UNVERIFIED (halaman yang diambil tidak memuat bagian ini; perilaku sudah ditangani defensif) | idem |
| Bitget `wss://ws.bitget.com/v2/ws/public`; kirim `ping` tiap 30 s, diputus setelah 2 menit tanpa ping | keepalive tiap 25 s | VERIFIED 2026-11-10 | [Bitget WebSocket intro](https://www.bitget.com/docs/classic/websocket/intro) |
| Bitget: ≤ 1000 channel/koneksi, disarankan < 50; request subscribe ≤ 4096 byte; subscribe ≤ 50 per 5 s | 40 channel/koneksi, 20 arg/request, jeda 0,15 s | VERIFIED 2026-11-10 | idem |
| Bitget `books1` snapshot 10 ms, `books15` snapshot 200 ms, push `{"action","arg","data":[{asks,bids,ts,seq}]}` | channel & parser | VERIFIED 2026-11-10 | [Bitget Depth Channel](https://www.bitget.com/docs/classic/websocket/spot/public/Depth-Channel) |
| Bitget channel `trade` | channel | UNVERIFIED (nama dari ingatan; kalau salah, Bitget membalas event `error` yang tercatat di log dan `--verify` melaporkan stream tanpa data) | — |
| Preflight: Binance `GET /api/v3/exchangeInfo?symbol=X` (status `TRADING`, error `-1121`), Bitget `GET /api/v2/spot/public/symbols?symbol=X` (status `online`) | buang simbol | UNVERIFIED; jawaban yang tidak dikenali **tidak** membuang simbol (hanya "not checked") | — |

Dua baris UNVERIFIED pertama terlihat langsung di menit pertama run uji (§1): pesan `trade` Bitget ada atau tidak, dan
preflight membuang simbol atau tidak.

## 4. Bukti (tes, mutation, performa)

`python3 -m unittest discover -s tools/tests -v` (job CI `tools`): 20 tes recorder + 10 tes probe, terhadap mock TLS lokal
yang meniru kedua venue. Yang diuji:

- pesan yang tiba **byte demi byte** melewati banyak timeout poll, pesan terfragmentasi dengan ping di tengah, frame 70 kB
  (panjang 64-bit): semua utuh; ping server dijawab pong dengan payload sama;
- keepalive Bitget, ack subscribe, error venue (simbol salah) tercatat dan stream tanpa data dilaporkan;
- reconnect setelah server menutup koneksi dan setelah feed diam (stale), downtime dihitung;
- disk guard berhenti bersih dengan exit code 3, file tertutup rapi;
- rotasi per jam, restart di jam yang sama tidak menimpa, crash hanya kehilangan bagian yang belum di-flush;
- verifikasi: gap, umur data (nearest-rank p50/p99), downtime termasuk run yang crash, persis terhadap data sintetis.

**Mutation:** 13 mutan, semua tertangkap: parse frame destruktif, tanpa keepalive, tanpa deteksi stale, file terpotong
melempar exception, tanpa disk guard, preflight mengabaikan status, crash tidak dihitung downtime, newline dalam pesan,
tanpa pong, restart menimpa file, fragmen lanjutan memulai pesan baru, tanpa flush, percentile tidak konservatif.

**Stabilitas tes:** 50/50 run lulus dengan 10 suite paralel (beban CPU tinggi). Sebelum fix B-04 (§5) hanya 14–24 dari 30.

**Performa:** parse + klasifikasi + gzip = ~105.000 pesan/s pada satu core (micro-benchmark dengan pesan ukuran nyata:
bookTicker ~150 B, depth20 ~1,3 kB). Perkiraan puncak nyata 16 simbol × 2 venue ≈ 3.000–4.000 pesan/s, jadi ruang ~25×.
Rasio kompresi gzip ~6×.

## 5. Temuan builder B-04 (S4, harness tes S0)

Stress test pertama recorder gagal acak (6–16 dari 30 run). Penyebab: `MockVenue.stop()` di `test_latency_probe.py`
menutup socket listener sementara thread acceptor-nya masih bisa memanggil `accept()` pada **nomor fd yang sama**. Socket
berikutnya (mock recorder) memakai ulang nomor fd itu, sehingga mock lama melayani koneksi milik tes lain. Bukti: handshake
yang diterima klien tidak memuat header `X-Mock: md` milik mock recorder. Fix di kedua mock: `shutdown()` membangunkan
acceptor, `join()`, baru `close()`. Kode produksi tidak terdampak (hanya harness tes). Tercatat di `FINDINGS_LOG.md`.

## 6. Setelah data terkumpul (langkah 2, PR berikutnya)

Data tetap di VM (puluhan GB, terlalu besar untuk diunduh lewat browser). Langkah berikutnya: alat analisis yang dijalankan
di VM yang sama dan menghasilkan laporan kecil, yaitu jumlah dan durasi silang top-of-book Binance ↔ Bitget per simbol,
edge bersih setelah fee taker kedua venue dan slippage dari depth, distribusi per jam UTC, dan berapa yang masih ada
setelah jeda latency Bitget p50/p99. Laporan itu yang diupload ke chat, bukan datanya.
