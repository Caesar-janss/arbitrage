# Tahap 2 langkah 5 — bukti pesaing dan simulasi posisi perp (`tools/perp_study.py`)

Menjawab dua celah penentu dari `docs/GAP_REVIEW_2026-11-18.md`, dari rekaman perp yang sudah ada:

- **G-3 bukti pesaing**: saat Binance dan Bitget bersilang, kutipan yang **tidak bergerak** adalah kutipan basi. Apakah
  orang lain mengambilnya (trade taker di venue itu, di sisi dan harga kutipan basi), berapa cepat, dan dibanding
  baseline (trade di sisi yang sama pada jendela sama panjang tepat sebelum episode).
- **G-1 simulasi posisi**: masuk saat gross ≥ ambang (default 22 bps = taker masuk + keluar), dieksekusi pada kutipan
  L ms kemudian, tutup saat jalan balik ≤ `--close-bps` atau setelah `--max-hold-s`, dengan fee masuk **dan keluar**.

## Menjalankan (di md-tokyo)

```
cd ~/arbitrage && git pull
nohup nice -n 19 python3 tools/perp_study.py --data ~/md_perp --last-hours 96 > ~/perp_study96.txt 2> ~/perp_study96.log &
tail -1 ~/perp_study96.log          # progres tiap 5 juta pesan
```

Varian yang berguna: `--exit-fee-bn 2 --exit-fee-bg 2` (keluar dengan order maker), `--entry-bps 15`, `--all-feeds`
(termasuk saat feed telat), `--latency-ms 25`, `--also-latency-ms 25,50` (default; latensi tambahan disimulasikan dalam
SATU putaran data, tiap latensi punya posisi dan order sendiri; `0` atau nilai negatif ditolak).

Kecepatan terukur di md-tokyo: 36,9 jam data (365 juta pesan) ≈ 1,5 jam sendirian; 96,2 jam (±1 miliar pesan) ≈ 10,5 jam
bila tiga analisis berjalan bersamaan di VM 2 core. Jangan memperkirakan dari jumlah pesan rata-rata per jam: pada jam
ramai (14–20 UTC) pesan per jam bisa 3–4× jam sepi.

## Asumsi

- Sisi taker Binance dari `aggTrade` `m` (m = true → taker jual), terverifikasi di dokumen Binance. Bitget: field
  `side` dibaca sebagai sisi taker (dokumen Bitget; **belum diverifikasi** pada data kita); push trade `snapshot` adalah
  riwayat dan dilewati.
- Simulasi optimistis: tanpa pesaing, terisi penuh di kutipan, tanpa rate limit. Pesimistis: hanya level 1, satu posisi
  per koin. Belum termasuk funding selama posisi terbuka dan margin.
- Fee default = tier dasar terverifikasi 2026-11-18: Binance taker 5, Bitget taker 6 bps; maker 2/2.

## Tes

`tools/tests/test_perp_study.py` (38 tes). Mutation 15/15 (langkah 5) dan 17/17 (tinjauan 96 jam) tertangkap
(`docs/review/stage2/REGRESSION_LOG.md`).

## Bagian laporan (tinjauan 2026-11-23)

Hasil 96 jam menunjukkan satu angka $/hari tidak cukup untuk menilai kriteria go, jadi laporan kini memuat:

- **§3b Rincian per hari (UTC, hari posisi dibuka)**: posisi, $, $/hari dinormalkan 24 jam, koin teratas. Hari tanpa posisi
  tetap dicantumkan. Hasil yang hanya hidup di satu atau dua hari bukan pola.
- **§3c Konsentrasi**: porsi 10 posisi terbesar (kriteria go < 30 %), jumlah posisi untuk separuh total, porsi koin
  teratas dan tiga koin teratas, total/hari tanpa koin teratas. Posisi rugi tetap di total, jadi porsi bisa > 100 %.
- **§3d Sensitivitas latensi**: L utama dan `--also-latency-ms` (default 25 dan 50) dalam satu putaran.
- **§3e Posisi terbesar**: 10 posisi terbesar, dengan waktu buka, ukuran, bps masuk/keluar.

`pair_analysis.py` §2c menambah daftar **episode terbesar** (level fee utama, hanya feed normal, L referensi): untuk
memeriksa apakah angka $/hari per koin bertumpu pada beberapa kejadian.

## Hasil

**36,9 jam (2026-11-17 15:05Z → 11-19 03:59Z), pratinjau.** 754 episode; 14 posisi, $5,36 ≈ $3,48/hari. Kutipan basi
diambil 39 % (baseline 18 %), 21 % dalam ≤ 10 ms. Sampel terlalu kecil: kesimpulan builder "pesaing nyata" dan "perp
hampir pasti ditutup" terlalu cepat.

**96,2 jam (2026-11-18 11:45Z → 11-22 11:59Z).** Trade dibaca: Binance 26,6 juta, Bitget 8,9 juta.

| Ukuran | Hasil |
|---|---|
| Episode (≥ 11 bps, ≥ $50) | 17.178 (1.196 berdurasi 0 ms dibuang) |
| Kutipan basi diambil | 20 % semua; trade sisi itu selama episode 23 % vs baseline 18 %; diambil dalam ≤ 10 ms hanya 5 % |
| Waktu sampai diambil p50 | 11,7 ms (ETH), 12,6 ms (BTC), koin lain 24–132 ms |
| Simulasi (L = 10 ms, entry ≥ 22 bps, fee 5/6 masuk dan keluar) | 192 posisi, 45 terlewat, tahan p50 3,78 s, masuk 29,2 / keluar 0,5 / net 7,7 bps, **$54,11 total = $13,49/hari**; 190 konvergen, 2 timeout |
| Konsentrasi | NEAR $23,99 (44 %); NEAR + WIF + APT 70 %; ukuran rata-rata ±$370 per posisi |

**Penilaian terhadap kriteria go** (`docs/GAP_REVIEW_2026-11-18.md`): net ≥ ±$8/hari secara nominal lolos ($13,49,
optimistis); bukti pesaing lemah (23 % vs 18 %); durasi 4 hari < 7 hari (belum); porsi 10 posisi terbesar belum dilaporkan
saat run ini (ditambahkan di PR ini); L = 10 ms belum realistis dari Tokyo (latensi order belum terukur, G-4).
Perkiraan ilustratif (bukan simulasi): L = 25 ms mengurangi hasil ±30 % dan L = 50 ms ±50 % (pola `pair_analysis` §3), dan
bila hanya separuh order terisi, hasilnya ±$3–9/hari, sekitar biaya VM.

**Selisih antar alat (belum diverifikasi).** `pair_analysis` melaporkan ETH $461,61/hari (feed normal, satu kali silang)
sedangkan simulasi hanya 3 posisi ETH dalam 4 hari. Hipotesis: angka ETH didominasi beberapa episode besar (porsi 10
terbesar 66 %). `pair_analysis` §2c dibuat untuk memeriksanya pada putaran berikutnya.
