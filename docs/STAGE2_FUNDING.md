# Tahap 2 langkah 4 — analisis funding Binance USDⓈ-M ↔ Bitget USDT-M (`tools/funding_analysis.py`)

Pertanyaan: **kalau kita short di venue yang funding-nya lebih tinggi dan long di venue lain dengan notional yang sama,
berapa funding bersih yang terkumpul setelah fee, tanpa melihat masa depan?** Ini bukan perlombaan latensi; yang
menentukan adalah besar dan kestabilan selisih funding, fee buka/tutup, dan basis antar venue.

## 1. Menjalankan (di md-tokyo)

```
cd ~/arbitrage && git pull
nice -n 19 python3 tools/funding_analysis.py --data ~/md_perp > ~/funding_report.txt
```

Opsi: `--last-hours N`, `--symbols BTCUSDT,ETHUSDT`, `--roundtrip-bps 22` (fee buka + tutup di kedua venue, bps dari
notional satu leg; default = taker 5 + 6 bps × 2, tier dasar, **belum diverifikasi**), `--hysteresis-bps 0.5`.
Kecepatan: ≈ 650 ribu baris/s di container builder (1,05 juta baris sintetis 1,6 s); 1 hari rekaman perp ≈ 70 juta
baris ≈ 4–8 menit di VM. Hasil bermakna butuh beberapa settlement: interval 8 jam = 3 per hari.

## 2. Sumber data (format dari rekaman md-tokyo 2026-11-17)

- Binance `<sym>@markPrice@1s` (`markPriceUpdate`): `r` = funding rate periode berjalan, `T` = waktu funding periode itu,
  `p` = mark price.
- Bitget `ticker` USDT-FUTURES: `fundingRate`, `nextFundingTime`, `markPrice`.

## 3. Definisi

- **Settlement** (diperbaiki di PR #17, temuan B-13): waktu funding venue berpindah ke waktu berikutnya **sesudah** waktu
  itu lewat. Di md-tokyo: Binance ±1,05 s sesudahnya, Bitget ±0,4–0,6 s, dan observasi pada T+0,07…0,4 s masih membawa
  label lama. Perpindahan dalam 5 menit sesudah T = settlement, dengan rate = observasi terakhir **sebelum** T (paling lama
  10 menit sebelumnya). Nilai berlabel lama sesudah T bisa sudah estimasi periode baru (Binance 00:00: 0,5973 sebelum,
  0,6014 sesudah). Pindah sebelum T (jadwal diubah) atau jauh sesudah T (label macet) bukan settlement. Panjang periode =
  jarak ke settlement sebelumnya.
- **Akrual per jam**: rate settle / panjang periode, sehingga venue dengan interval berbeda (mis. 8 jam vs 4 jam)
  dibandingkan per jam.
- **Hold**: sisi ditentukan dari prediksi rate (observasi terakhir pada atau sebelum awal jam, paling tua 1 jam) di
  jam pertama, dipegang sampai akhir; biaya satu round trip.
- **Flip**: sama, tetapi pindah sisi bila prediksi di awal jam menunjuk sisi lain lebih dari `--hysteresis-bps` per
  8 jam; tiap pindah = satu round trip lagi.
- **Sisi tetap terbaik (hindsight)**: |jumlah selisih| − satu round trip. Batas atas, bukan strategi.
- **Basis mark**: Binance vs Bitget, sampel per menit. Perubahannya antara buka dan tutup ikut menjadi untung/rugi dan
  tidak termasuk di "net".

## 3b. Hasil 24 jam pertama dan perubahan laporan (PR #18)

48 settlement per venue (14 koin × 3 + WIF 4 jam × 6). Selisih rata-rata terbesar 0,64 bps/8 jam (ADA); BTC 0,52; sisanya
≤ 0,48, beberapa berganti arah. Kolom lama "hold net/tahun" menyebar fee sekali bayar ke 17 jam lalu disetahunkan
(−106 % dst.): menyesatkan, diganti **Bruto/tahun (sisi hold)** dan **Hari balik modal** = fee round trip ÷ funding per
hari. Fee dasar terverifikasi 2026-11-18 (`docs/GAP_REVIEW_2026-11-18.md` G-20).

## 4. Batas yang diketahui

- Fee asumsi; fee maker bisa jauh lebih rendah tetapi berisiko tidak terisi.
- Interval funding per simbol diambil dari settlement yang teramati (properti jadwal venue, bukan informasi pasar).
- Belum menghitung slippage saat buka/tutup, kebutuhan margin, atau risiko likuidasi pada leg yang merugi.
- Hold "per tahun" diskalakan dari jam yang terukur: dengan beberapa hari data, angka ini sangat kasar.

## 4b. Hasil 118 jam (2026-11-17 14:46Z → 11-22 12:59Z)

- Settlement terdeteksi: Binance 239, Bitget 240. Interval 8 jam di kedua venue, WIF 4 jam.
- **Semua koin net negatif** setelah fee 22 bps dalam 113 jam (hold: −7,3 sampai −27,6 bps). Terbaik OP: selisih
  −1,04 bps/8 jam (long Binance / short Bitget) = 11,4 %/tahun bruto, balik modal 7,1 hari. BTC 3,4 %, LTC 4,7 %,
  ETH 2,8 %, NEAR 3,0 %, APT 2,8 %; ADA, LINK, WIF, ARB bruto negatif (−1,6 sampai −4,3 %).
- Strategi flip (tanpa melihat masa depan) lebih buruk: −38 sampai −170 bps. Sisi tetap terbaik dengan hindsight pun
  tetap negatif untuk semua koin.
- Basis mark p10–p90 ±5–20 bps (OP −8…+20, WIF −16…+8) sebanding dengan fee 22 bps: hasil funding bisa hilang di basis.
- Pada notional $10.000 per leg, OP ≈ $1.100/tahun bruto bila selisih bertahan; biaya VM + disk ≈ $1.080/tahun.
  Empat hari satu rezim, jadi sangat kasar.

## 5. Tes

`tools/tests/test_funding_analysis.py` (17 tes; pesan nyata dari md-tokyo untuk parser, pola waktu pergantian nyata untuk
settlement). Mutation 12/12 (PR #16) dan 14/14 (PR #17) tertangkap
(`docs/review/stage2/REGRESSION_LOG.md`).
