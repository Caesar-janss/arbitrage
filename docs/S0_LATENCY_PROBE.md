# S0 — Probe latency Tokyo vs Singapura

> Tujuan: mengganti asumsi `STAGE1_CORE.md` §5 ("Bitget & Binance di AWS Tokyo, Bybit di Singapura, RTT 65–75 ms") dengan
> angka yang diukur dari VPS kita sendiri, lalu memilih pasangan venue (opsi a/b/c). Keputusan tetap milik Jansen.

## 1. Menjalankan (Jansen)

Butuh dua VPS sementara: **AWS Tokyo `ap-northeast-1`** dan **AWS Singapura `ap-southeast-1`**. Instance kecil cukup
(probe hanya beberapa koneksi; `t3.small` atau sejenisnya). Ubuntu sudah punya `python3`; tidak perlu install apa pun,
tidak butuh API key, dan tidak mengirim order.

```bash
# di masing-masing VPS (jam sinkron dulu: Ubuntu memakai chrony / systemd-timesyncd)
git clone https://github.com/Caesar-janss/arbitrage.git && cd arbitrage
python3 tools/latency_probe.py --label tokyo --minutes 30 --progress          # di VPS Tokyo
python3 tools/latency_probe.py --label singapore --minutes 30 --progress      # di VPS Singapura, JAM YANG SAMA
```

- Jalankan kedua lokasi **bersamaan**, idealnya dua kali: jam ramai Asia (± 08:00–10:00 UTC) dan jam sepi. Jaringan berubah
  menurut jam.
- Hasil: `probe_tokyo.json` dan `probe_singapore.json`. Kirim kedua file ke Claude (tidak berisi rahasia: hanya angka
  latency, hostname VPS, alamat IP publik venue).
- Perbandingan (siapa pun, di mana pun): `python3 tools/latency_probe.py --compare probe_tokyo.json probe_singapore.json`.
- VPS boleh dimatikan sesudahnya.

## 2. Yang diukur

| Kolom | Arti | Kenapa |
|---|---|---|
| `rest_ms` | HTTPS GET endpoint server-time pada koneksi keep-alive | round trip sampai **origin** API (melewati edge CDN) |
| `ws_ms` | ping level aplikasi di WebSocket publik yang persisten | jalur market data yang akan dipakai engine |
| `tcp_ms` / `tls_ms` | koneksi baru tiap 10 putaran | RTT jaringan ke hop pertama (sering edge CDN, bukan origin) |
| `offset_ms` | jam venue − titik tengah kirim/terima lokal | asumsi `VenueClock` / `StaleDataGuard`; valid hanya bila host tersinkron NTP |
| `dns` | alamat + reverse DNS | PTR AWS / CloudFront menyebut region / edge yang melayani |
| `host` | AZ AWS (IMDSv2), status chrony / timedatectl | membuktikan di mana dan dengan jam apa pengukuran dibuat |

Laporan `--compare` juga menghitung **kaki terlambat** tiap pasangan spatial (maks RTT origin dua venue, p50 dan p99), karena
jendela legging ditentukan kaki paling lambat (§5).

## 3. Cara membaca → opsi §5 (panduan, bukan aturan otomatis)

| Pola hasil | Arti | Opsi yang cocok |
|---|---|---|
| Tokyo: Binance↔Bitget kaki terlambat ≲ 5 ms; Bitget↔Bybit ≳ 30 ms dari kedua region | Bitget–Bybit lintas region | **(a)** spatial Binance↔Bitget dari Tokyo; Bybit untuk funding/hedge |
| Satu region memberi Bitget↔Bybit ≲ 10 ms | ketiga venue dekat region itu | **(b)** Bitget↔Bybit dari region itu |
| Pasangan terbaik masing-masing butuh region berbeda dan keduanya bernilai | tidak ada satu region yang cocok | **(c)** dua proses, dua region (paling kompleks) |

p99 lebih penting daripada p50 untuk risiko legging; selisih p99 − p50 yang besar = jitter. Offset jam > ±20 ms
(`RiskLimits.MaxClockUncertaintyMs`) atau sebaran lebar berarti `StaleDataGuard` akan sering menolak; periksa NTP host dulu.

## 4. Endpoint dan sumbernya

Kewajiban `TEAM_WORKFLOW.md` §2: klaim API venue punya sumber. Dicek 2026-11-02.

| Target | Endpoint | Status | Sumber |
|---|---|---|---|
| binance-spot REST | `GET https://api.binance.com/api/v3/time` → `serverTime` (ms), weight 1 | VERIFIED | [Binance Spot API — General endpoints](https://developers.binance.com/docs/zh-CN/binance-spot-api-docs/rest-api/general-endpoints) |
| binance-spot WS | `wss://stream.binance.com:9443/ws`; `{"method":"LIST_SUBSCRIPTIONS","id":n}` → `{"result":[…],"id":n}`; server ping tiap 20 s, pong wajib membawa payload yang sama; maks 5 pesan masuk/s; koneksi 24 jam | VERIFIED (path `/ws` tanpa stream = pola umum klien, dibuktikan pada run pertama) | [Binance — WebSocket Streams](https://developers.binance.com/en/dev-docs/products/spot/web-socket-streams), [Web Socket Streams](https://developers.binance.com/docs/binance-spot-api-docs/web-socket-streams) |
| binance-usdm REST | `GET https://fapi.binance.com/fapi/v1/time` | UNVERIFIED (halaman resmi tidak terjangkau dari sandbox; kegagalan hanya dihitung sebagai error) | — |
| bybit-spot / bybit-linear REST | `GET https://api.bybit.com/v5/market/time` → `result.timeNano` (ns), `time` (ms) | VERIFIED | [Bybit V5 — Get Bybit Server Time](https://bybit-exchange.github.io/docs/v5/market/time) |
| bybit-spot / bybit-linear WS | `wss://stream.bybit.com/v5/public/spot`, `…/linear`; `{"req_id":"n","op":"ping"}` → `{"success":true,"ret_msg":"pong",…,"op":"ping"}`; ping disarankan tiap 20 s; maks 500 koneksi / 5 menit | VERIFIED | [Bybit V5 — Connect](https://bybit-exchange.github.io/docs/v5/ws/connect) |
| bitget REST | `GET https://api.bitget.com/api/v2/public/time` → `data.serverTime` (ms) | UNVERIFIED (resmi tidak terjangkau; path dari SDK yang dipakai luas) | [tiagosiebler/bitget-api endpoint list](https://github.com/tiagosiebler/bitget-api/blob/master/docs/endpointFunctionList.md) |
| bitget WS | `wss://ws.bitget.com/v2/ws/public`; kirim string `ping` → `pong`; maks 10 pesan/s; putus bila 2 menit tanpa ping; maks 100 koneksi / IP | VERIFIED | [Bitget — WebSocket intro](https://www.bitget.com/docs/classic/websocket/intro) |

Endpoint UNVERIFIED tidak menghalangi probe: kegagalan tercatat di `errors` dan target lain tetap diukur. Sebelum adapter
Tahap 2, keduanya harus diverifikasi dari halaman resmi.

## 5. Batas pengukuran

- Endpoint publik ≠ jalur order entry (WS trade / REST order bisa dilayani gateway lain). Order entry diukur di Tahap 4
  (testnet).
- Server bisa menambah waktu proses ke `rest_ms`; `ws_ms` (ping) biasanya lebih bersih.
- `tcp_ms` ke edge CDN bisa sangat kecil walau origin jauh; jangan dipakai sendirian.
- Offset jam: ketidakpastian ± setengah RTT; bermakna hanya bila `host.clock_*` menunjukkan NTP tersinkron.

## 6. QA (tanpa akses ke venue dari sandbox)

`python3 -m unittest discover -s tools/tests -v` (juga job CI `tools`): mock TLS lokal yang meniru ketiga protokol, dengan
delay dan selisih jam yang diketahui (REST 3 format waktu; WS Binance: ping server lebih dulu + balasan > 125 byte; Bybit
op:ping; Bitget pong terfragmentasi). Test memeriksa: semua kanal terukur tanpa error, offset ≈ selisih jam yang
disuntikkan, pong membawa payload ping server, target mati dihitung bukan crash, `--compare` memilih region berdasarkan
kaki **terlambat**, IMDS yang dijawab halaman error tidak dianggap AZ, dan batas interval CLI. Mutation: 6 mutan (pong,
fragmentasi, panjang 16-bit, tanda offset, satuan `timeNano`, max→min di pasangan) semuanya tertangkap; mutan max→min baru
tertangkap setelah test pasangan asimetris ditambahkan. Smoke di sandbox: DNS venue diblokir → error terhitung, laporan
tetap terbentuk; IMDS sandbox dijawab proxy (HTTP 403) → tercatat "not on EC2" (bug ini ditemukan dan diperbaiki saat
smoke).
