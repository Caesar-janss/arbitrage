# Tahap 2 langkah 2 — analisis rekaman Binance ↔ Bitget (`tools/pair_analysis.py`)

Pertanyaan yang dijawab: **pada resolusi milidetik, berapa sering top-of-book Binance dan Bitget bersilang cukup jauh untuk
menutup fee, berapa lama silangnya bertahan, siapa yang bergerak lebih dulu, dan berapa yang tersisa bila order kita tiba
L ms kemudian.** Scanner (5 s, REST) tidak bisa menjawab ini; rekaman (setiap perubahan, satu jam mesin) bisa.

## 1. Menjalankan (di md-tokyo)

```
cd ~/arbitrage && git pull
nice -n 19 python3 tools/pair_analysis.py --data ~/md --last-hours 6                       # cepat: ~2–4 menit
nice -n 19 python3 tools/pair_analysis.py --data ~/md --last-hours 24 > ~/pair_report.txt  # ~8–15 menit
```

Progres dicetak ke layar (stderr) tiap 5 juta pesan, jadi alat yang lambat bisa dibedakan dari yang macet. Opsi:
`--symbols BTCUSDT,ETHUSDT`, `--fee-levels 20,15,10,0` (total bps kedua leg; yang pertama = level utama),
`--latency-ms 1,5,10,25,50,100,250`, `--ref-latency-ms 10`, `--min-usd 50`.

Kecepatan terukur (container builder, 1,25 juta baris sintetis dengan campuran pesan seperti rekaman asli): 7,8 s, 68 MB,
≈ 160 ribu baris/s. 24 jam rekaman ≈ 78 juta baris.

## 2. Definisi

- **Episode** (simbol, beli di, jual di, level fee): terbuka selama `bid_jual / ask_beli − 1 − fee ≥ 0` dan
  `min(ukuran bid_jual, ukuran ask_beli) × ask_beli ≥ --min-usd`. Satu `bookTicker` Binance atau `books1` Bitget yang
  mengubah keadaan bisa membuka, mengubah, atau menutupnya.
- **Durasi**: dari update yang membuka sampai update yang menutup, dengan `recv_unix_ns` (stamp saat read socket, review
  C-12). **0 ms** = dibuka dan ditutup dalam satu read: urutannya pasti, jaraknya tidak diketahui; dihitung terpisah.
- **Dibuka oleh / ditutup oleh**: venue yang update-nya membuka atau menutup episode. Dibuka oleh Binance dan ditutup oleh
  Bitget = Bitget mengikuti Binance (pengganti uji lead-lag yang klaimnya ditarik di audit A-3, kali ini dari urutan
  kejadian, bukan dari umur data).
- **Capture[L]**: yang ditemui satu pasangan order yang tiba L ms setelah episode dibuka = net bps × ukuran $ **dari
  keadaan yang berlaku pada open + L**, atau nihil bila episode sudah tutup (L ≥ durasi). Optimistis: tidak ada pemain
  lain, kedua leg terisi di kutipan. Pesimistis: hanya level 1.
- **Putus**: saat koneksi mencatat `disconnect`/`stop`/`start`, kutipan venue dari koneksi itu dilupakan sampai data
  baru, dan episode yang terbuka ditutup sebagai `gap`. Kutipan basi tidak pernah menciptakan episode.
- **Keterlambatan feed** (PR #14): lag = waktu terima − stempel venue (Binance: `E` di stream trade, karena `bookTicker`
  spot tidak punya stempel; Bitget: `ts` di `books1`). Lag mentah ikut memuat selisih jam dua mesin, jadi yang dipakai
  adalah **kelebihan** di atas dasar = lag terkecil 10 menit terakhir. Episode berstatus **telat** bila saat dibuka salah
  satu venue > `--feed-lag-ms` (default 20) di atas dasarnya; **tidak diketahui** bila sampel terakhir venue itu > 2 s;
  selain itu **normal**. Stempel di luar −10 s…+60 s diabaikan (bukan bacaan jam). Laporan memberi capture dan $/hari
  untuk episode feed normal saja di samping semuanya.
- **Basis**: median selisih mid Binance vs Bitget, sampel 1 per detik per simbol (positif = Binance lebih mahal).

## 3. Hasil pertama (6 jam, 2026-11-17 02:19–08:22Z) dan apa yang diuji karenanya

- Fee 20 bps, L = 10 ms: ≈ $129/hari batas atas top-of-book; fee 15 → $221, fee 10 → $430 (fee variabel terbesar).
- SUI, NEAR binance→bitget: 100 % dibuka oleh Binance, **75 % ditutup oleh Binance sendiri** (lonjakan Binance yang
  kembali, bukan Bitget yang tertinggal), durasi p50 14–19 ms, ≈ 4 kejadian per simbol dalam 6 jam (sampel terlalu kecil).
- OP bitget→binance: 79 % dibuka oleh Bitget, p50 151 ms, net 6 bps, $336; basis OP paling lebar (−11…+8 bps).
- Uji cadence `books1` (jam lengkap terakhir, SUI/NEAR/OP/BTC): jarak antar pesan p10 ≈ 4 ms, p1 = 0 ms → **durasi tidak
  terkuantisasi 10 ms** (dugaan builder salah). Tetapi terima − `ts` Bitget p50 7–11 ms, **p99 57–77 ms**: sebanding
  dengan durasi episode, sehingga sebagian "kutipan basi" mungkin hanya basi di layar kita. Itu alasan kolom feed telat.

## 4. Batas yang diketahui

- Hanya level 1 dan hanya 16 simbol rekaman (majors + mid cap). Koin ekor dari laporan scanner (INIT, CC, …) tidak
  direkam. Hasil cek manual 2026-11-17 untuk dua di antaranya: di Bitget, **penarikan INIT ditangguhkan** (satu-satunya
  jaringan) dan CC berlabel ditangguhkan; jadi baris laporan scanner `INIT bitget→binance` tidak bisa dieksekusi sebagai
  arbitrase antar-venue.
- Fee asumsi (tier dasar); tier Anda belum diverifikasi.
- Satu order per episode; tidak ada antrean, rate limit, atau penolakan order.
- Belum memakai depth (`depth20`, `books15`) dan trade. Itu langkah berikut bila level 1 menunjukkan sesuatu.

## 5. Tes

`tools/tests/test_pair_analysis.py` (31 tes, rekaman sintetis dalam format recorder). Mutation check: 10/10 mutan
(PR #13) + 10/10 (PR #14) tertangkap (lihat `docs/review/stage2/REGRESSION_LOG.md`).
