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
nohup nice -n 19 python3 tools/perp_study.py --data ~/md_perp --last-hours 48 > ~/perp_study.txt 2> ~/perp_study.log &
tail -1 ~/perp_study.log            # progres tiap 5 juta pesan
```

Varian yang berguna: `--exit-fee-bn 2 --exit-fee-bg 2` (keluar dengan order maker), `--entry-bps 15`, `--all-feeds`
(termasuk saat feed telat), `--latency-ms 25`. Kecepatan: ≈ 2× `pair_analysis` pada data uji yang sama.

## Asumsi

- Sisi taker Binance dari `aggTrade` `m` (m = true → taker jual), terverifikasi di dokumen Binance. Bitget: field
  `side` dibaca sebagai sisi taker (dokumen Bitget; **belum diverifikasi** pada data kita); push trade `snapshot` adalah
  riwayat dan dilewati.
- Simulasi optimistis: tanpa pesaing, terisi penuh di kutipan, tanpa rate limit. Pesimistis: hanya level 1, satu posisi
  per koin. Belum termasuk funding selama posisi terbuka dan margin.
- Fee default = tier dasar terverifikasi 2026-11-18: Binance taker 5, Bitget taker 6 bps; maker 2/2.

## Tes

`tools/tests/test_perp_study.py` (19 tes). Mutation 15/15 tertangkap (`docs/review/stage2/REGRESSION_LOG.md`).
