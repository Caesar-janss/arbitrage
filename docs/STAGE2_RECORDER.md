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
- Cek isi rekaman kapan saja: `python3 tools/md_recorder.py --verify ~/md --last-hours 2` (hanya file 2 jam terakhir,
  beberapa detik). Tanpa `--last-hours` semua file dibaca: ~300.000 baris/detik, kira-kira 20 detik per jam rekaman,
  jadi setelah berhari-hari bisa makan waktu lama; jangan di-Ctrl+C, biarkan selesai.
- Berhenti: `sudo systemctl stop md-recorder`. Data tetap di `~/md`.

**Pemulihan otomatis (audit B-08, B-09):** bila satu stream order book diam 10 menit sementara koneksinya hidup
(subscription putus sepihak), koneksi itu disambung ulang dan semua stream di-subscribe lagi; batasnya menggandakan diri
(sampai 160 menit) bila stream memang sepi, dan kembali ke 10 menit begitu semua stream segar. Stream trade dikecualikan.
Bila thread sebuah koneksi berakhir sendiri, proses keluar dengan kode 1 sehingga systemd menyalakan ulang semuanya.

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
Sebelum mulai, preflight REST membuang simbol hanya bila venue menjawab **otoritatif** bahwa simbol itu tidak aktif
(Binance: baris simbol itu dengan status ≠ `TRADING`, atau error `-1121`; Bitget: `code "00000"` dan tidak ada baris untuk
simbol itu, atau status ≠ `online`). Jawaban lain (error sementara, non-JSON, kode error lain) = "not checked", simbol
tetap direkam (review C-13).

Semua channel order book adalah **snapshot**, jadi setelah reconnect book langsung utuh lagi di pesan berikutnya. Tidak ada
logika resync, dan gap hanya berarti data hilang, tidak pernah book yang rusak.

**Format file:** `<out>/<koneksi>/<YYYYmmdd-HH>_<k>.tsv.gz` (satu file per koneksi per jam UTC; restart di jam yang sama
membuat `_01`, `_02`, bukan menimpa). Setiap baris: `<recv_unix_ns>␉<pesan venue persis seperti diterima>` atau
`<recv_unix_ns>␉#<meta json>` (`start`, `file` + status chrony, `connect` + IP/port peer, `disconnect` + alasan, `stop`,
`stats` tiap 60 s). `md_recorder.read_file()` membaca file terpotong sampai baris lengkap terakhir; replay memakai fungsi ini.

**Durabilitas (review C-14):** baris yang ditulis di-flush ke OS paling lambat tiap 5 detik, juga saat feed diam (timer di
loop utama, bukan hanya saat ada pesan baru), lalu di-`fsync` di luar lock writer. Proses crash: hilang ≤ ~5 detik
terakhir. VM/host crash: ≤ ~5 detik ditambah fsync yang sedang berjalan. Rotasi jam mem-fsync file yang ditutup.

### Arti `recv_unix_ns` untuk analisis (review C-12, wajib dibaca sebelum menghitung durasi)

- Stamp diambil **tepat setelah read socket** yang membawa byte terakhir pesan itu, sebelum parse/klasifikasi/gzip. Byte
  yang ikut handshake dan pesan yang tiba di sela jeda subscribe Bitget juga langsung dibaca dan di-stamp.
- **Pesan dengan stamp sama berasal dari satu read:** urutannya pasti, jarak waktu di antaranya tidak diketahui. Analisis
  harus memperlakukan tie sebagai interval ambigu, bukan durasi 0 ms, dan tidak memutuskan siapa lebih dulu dari tie-break.
- Antrean di kernel tidak bisa di-timestamp dari user space Python. Batasnya diukur: meta `stats` tiap 60 s per koneksi
  berisi jumlah read, pesan per read (p99/maks), dan **reader lag** (µs dari stamp read pertama sebuah poll sampai batch
  itu selesai ditulis = berapa lama read berikutnya tertahan oleh pemrosesan kita). `--verify` menampilkannya. Menit
  dengan lag tinggi = durasi milidetik di menit itu tidak bisa dipercaya.
- Data yang direkam **sebelum** fix ini (11 Nov s/d update VM) memakai satu stamp per poll setelah parse: perlakukan
  setiap tie sebagai ambigu dengan cara yang sama; selisihnya terhadap stamp per-read hanya waktu parse (mikrodetik),
  kecuali di detik pertama setelah subscribe Bitget (sampai ~150 ms terlambat).
- Antar-venue: stamp kedua koneksi memakai jam host yang sama (chrony), tetapi thread berbeda; bandingkan hanya dengan
  memperhitungkan reader lag kedua sisi.

## 3. Aturan venue dan sumbernya

| Aturan | Dipakai | Status | Sumber |
|---|---|---|---|
| Binance combined stream `wss://stream.binance.com:9443/stream?streams=a/b/c`, nama stream `@bookTicker`, `@depth20@100ms`, `@trade`, payload `{"stream","data"}` | URL & parser | VERIFIED 2026-11-10 | [Binance WebSocket Streams](https://developers.binance.com/docs/binance-spot-api-docs/web-socket-streams) |
| Binance: server ping tiap 20 s, diputus bila tidak ada pong dalam 1 menit; koneksi maks 24 jam; ≤ 1024 stream/koneksi; ≤ 5 pesan masuk dari klien/detik (termasuk pong); 300 koneksi/5 menit/IP | auto-pong, reconnect, ≤ 200 stream, klien tidak mengirim apa pun selain pong | VERIFIED 2026-11-11 (review ronde 1, dicek ulang builder dari halaman resmi). `bookTicker` dan partial depth **tidak** punya field `E` | [Binance WebSocket Streams (legacy docs)](https://developers.binance.com/legacy-docs/binance-spot-api-docs/web-socket-streams) |
| Bitget `wss://ws.bitget.com/v2/ws/public`; kirim `ping` tiap 30 s, diputus setelah 2 menit tanpa ping | keepalive tiap 25 s | VERIFIED 2026-11-10 | [Bitget WebSocket intro](https://www.bitget.com/docs/classic/websocket/intro) |
| Bitget: ≤ 1000 channel/koneksi, disarankan < 50; request subscribe ≤ 4096 byte; subscribe ≤ 50 per 5 s | 40 channel/koneksi, 20 arg/request, jeda 0,15 s | VERIFIED 2026-11-10 | idem |
| Bitget `books1` snapshot 10 ms, `books15` snapshot 200 ms, push `{"action","arg","data":[{asks,bids,ts,seq}]}` | channel & parser | VERIFIED 2026-11-10 | [Bitget Depth Channel](https://www.bitget.com/legacy-docs/classic/spot/websocket/public/Depth-Channel) (link lama mati, diganti sesuai review) |
| Bitget channel `trade` (instType SPOT, `tradeId`) | channel | VERIFIED 2026-11-11 (run uji md-tokyo: 16/16 simbol ada data; dokumen resmi via review). Saat subscribe, Bitget mengirim `action:"snapshot"` berisi trade terakhir (bisa berumur menit): `--verify` tidak menghitungnya sebagai umur data, analisis wajib **dedup per `tradeId`**. Bitget mengumumkan perubahan snapshot awal (live direncanakan 17 Nov 2026); data mentah tetap kompatibel karena disimpan apa adanya | [Bitget Trades Channel](https://www.bitget.com/legacy-docs/classic/spot/websocket/public/Trades-Channel) |
| Preflight Binance `GET /api/v3/exchangeInfo?symbol=X`: status `TRADING`, error `-1121` = invalid symbol | DROP hanya pada jawaban otoritatif (C-13) | VERIFIED 2026-11-11 menurut review ronde 1 (belum dicek ulang builder: halaman di luar allowlist sesi builder) | [exchangeInfo](https://developers.binance.com/en/docs/catalog/core-trading-spot-trading/api/rest-api/general) |
| Preflight Bitget `GET /api/v2/spot/public/symbols?symbol=X`: `code "00000"`, `data` array, status `online`; 20 req/s/IP | DROP hanya pada jawaban sukses otoritatif; kode error apa pun = not checked (C-13). Kode error "simbol tidak ada" berbeda antara dokumen classic dan UTA, jadi sengaja **tidak** dipakai untuk DROP | VERIFIED 2026-11-11 menurut review ronde 1 (belum dicek ulang builder: halaman di luar allowlist sesi builder) | [Get Symbol Info](https://www.bitget.com/legacy-docs/classic/spot/market/Get-Symbols) |

Run uji pertama di md-tokyo (2026-11-11, 3 menit): 16/16 simbol lolos preflight Binance dan Bitget, 96/96 stream ada
data, coverage 100 %, ~4,1 GB/hari (bukan 10–15 GB: disk 100 GB cukup ~22 hari). Umur data p50: Binance trade 2–3 ms,
Bitget books1 8–14 ms, Bitget books15 7–13 ms (p99 sampai ~200 ms, sesuai snapshot tiap 200 ms).

## 4. Bukti (tes, mutation, performa)

`python3 -m unittest discover -s tools/tests -v` (job CI `tools`): 35 tes recorder + 10 tes probe, terhadap mock TLS lokal
yang meniru kedua venue (21 tes sebelum review + 14 dari review ronde 1, §7). Yang diuji:

- pesan yang tiba **byte demi byte** melewati banyak timeout poll, pesan terfragmentasi dengan ping di tengah, frame 70 kB
  (panjang 64-bit): semua utuh; ping server dijawab pong dengan payload sama;
- keepalive Bitget, ack subscribe, error venue (simbol salah) tercatat dan stream tanpa data dilaporkan;
- reconnect setelah server menutup koneksi dan setelah feed diam (stale), downtime dihitung;
- disk guard berhenti bersih dengan exit code 3, file tertutup rapi;
- rotasi per jam, restart di jam yang sama tidak menimpa, crash hanya kehilangan bagian yang belum di-flush;
- verifikasi: gap, umur data (nearest-rank p50/p99), downtime termasuk run yang crash, persis terhadap data sintetis.

**Mutation:** 13 mutan semula + 14 mutan dari review ronde 1, semua tertangkap (daftar per temuan di §7). Satu mutan
review sempat lolos (stamp ulang saat menulis), lalu ditambah tes `test_c12_slow_writing_does_not_move_the_stamps`.

**Stabilitas tes:** 30/30 run lulus dengan 10 suite paralel setelah review ronde 1 (sebelum fix B-04, §5: 14–24 dari 30).

**Performa:** harness yang bisa dijalankan ulang: `python3 tools/tests/bench_md_recorder.py` (read → parse → klasifikasi
→ gzip satu thread, 80 % bookTicker ~150 B, 20 % depth20 ~1,3 kB). Di mesin builder 2026-11-11: kode lama ~86.000 pesan/s,
kode sesudah review ~72.000–78.000 pesan/s (stamp per read + statistik reader lag, ~15 % lebih mahal); angka 105.000 di
PR #6 diukur di mesin yang sama pada hari lain. Laju nyata md-tokyo ~1.500 pesan/s total, jadi ruang ~45× untuk throughput
rata-rata. **Ini bukan bukti latency ekor:** itu yang diukur `stats`/reader lag di VM (§2). Reader dan writer belum
dipisah thread; dipisah hanya bila reader lag p99 nyata menunjukkan perlu. Rasio kompresi gzip ~6×.

## 5. Temuan builder B-04 (S4, harness tes S0)

Stress test pertama recorder gagal acak (6–16 dari 30 run). Penyebab: `MockVenue.stop()` di `test_latency_probe.py`
menutup socket listener sementara thread acceptor-nya masih bisa memanggil `accept()` pada **nomor fd yang sama**. Socket
berikutnya (mock recorder) memakai ulang nomor fd itu, sehingga mock lama melayani koneksi milik tes lain. Bukti: handshake
yang diterima klien tidak memuat header `X-Mock: md` milik mock recorder. Fix di kedua mock: `shutdown()` membangunkan
acceptor, `join()`, baru `close()`. Kode produksi tidak terdampak (hanya harness tes). Tercatat di `FINDINGS_LOG.md`.

## 6. Setelah data terkumpul (langkah 2, PR berikutnya)

Aturan dari review C-12 untuk alat analisis: tie stamp = interval ambigu; menit dengan reader lag tinggi tidak dipakai
untuk durasi milidetik; depth (100/200 ms) diambil dari snapshot terakhir yang **sudah** diterima pada saat itu, tidak
pernah dari snapshot sesudahnya; trade Bitget di-dedup per `tradeId`.


Data tetap di VM (puluhan GB, terlalu besar untuk diunduh lewat browser). Langkah berikutnya: alat analisis yang dijalankan
di VM yang sama dan menghasilkan laporan kecil, yaitu jumlah dan durasi silang top-of-book Binance ↔ Bitget per simbol,
edge bersih setelah fee taker kedua venue dan slippage dari depth, distribusi per jam UTC, dan berapa yang masih ada
setelah jeda latency Bitget p50/p99. Laporan itu yang diupload ke chat, bukan datanya.

## 7. Review ronde 1 (ChatGPT, post-merge PR #6, 2026-11-11)

[Komentar review](https://github.com/Caesar-janss/arbitrage/pull/6#issuecomment-6799546635). Semua temuan direproduksi
ulang builder dengan tes merah sebelum fix; satu commit per temuan. Detail merah → hijau: `docs/review/stage2/REGRESSION_LOG.md`.

| ID | Sev | Inti | Fix | Mutan |
|---|---|---|---|---|
| C-11 | S2 | pesan lengkap hilang bila close / gagal pong / EOF ada di read yang sama | pesan dikirim dulu, error dilaporkan di panggilan berikutnya; byte handshake di-drain tanpa menunggu; batas total fragmen | 4/4 |
| C-12 | S2 | satu stamp per poll setelah parse; jeda subscribe Bitget menunda read ~150 ms | stamp per read sebelum pemrosesan; tetap membaca saat jeda subscribe; meta `stats` + reader lag di `--verify` | 3/3 (1 sempat lolos, tertangkap setelah tes tambahan) |
| C-13 | S3 | preflight Bitget menganggap semua HTTP 400 = simbol tidak ada → bisa exit 2 tanpa restart | DROP hanya pada jawaban otoritatif | 3/3 |
| C-14 | S3 | flush 5 s hanya dipicu tulis berikutnya, tanpa fsync | timer flush + fsync di luar lock, fsync saat rotasi | 3/3 |
| B-05 | S3 | (builder, saat fix C-12) exception tak terduga mematikan thread koneksi selamanya, proses tetap "sehat" | ditangkap, dicatat dengan traceback, reconnect dengan backoff | 1/1 |

Catatan reviewer yang **tidak** dijadikan temuan: parser bukan validator RFC 6455 lengkap (input ilegal); deadline pong
Bitget terpisah (sudah tertutup deteksi stale 30 s); pemisahan reader/writer (menunggu data reader lag nyata).

**Update di VM sesudah PR ini di-merge** (rekaman putus beberapa detik saja; file lama tetap, format sama + meta `stats`):

```bash
cd ~/arbitrage && git pull && sudo systemctl restart md-recorder && sleep 70 && journalctl -u md-recorder -n 5 --no-pager
```
