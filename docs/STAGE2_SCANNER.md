# Tahap 2 — Spread scanner multi-venue

> Pertanyaan: dari **semua** pasangan venue, mana yang punya selisih harga yang lolos fee taker **dan** bertahan cukup
> lama untuk diambil? Melengkapi recorder (`STAGE2_RECORDER.md`): recorder melihat milidetik di satu pasangan
> (Binance ↔ Bitget), scanner melihat detik di semua pasangan dari 8 venue. Selisih yang bertahan beberapa snapshot
> justru yang tidak ditentukan oleh jarak (latency), jadi venue yang "jauh" dari Tokyo tidak otomatis kalah di sini.

`tools/spread_scanner.py`: Python stdlib saja, endpoint ticker REST publik, tanpa API key, tidak pernah trading.
Venue: Binance, Bitget, Bybit, OKX, MEXC, Gate, KuCoin, Kraken. Quote: USDT.

## 1. Menjalankan (di md-tokyo, di samping recorder)

```bash
cd ~/arbitrage && git pull
python3 tools/spread_scanner.py --probe                          # WAJIB dulu: tiap venue sekali, lihat yang ter-parse
python3 tools/spread_scanner.py --out ~/scan --hours 0.05        # uji 3 menit di foreground
bash tools/install_spread_scanner_service.sh                     # jalan terus sebagai service
python3 tools/spread_scanner.py --report ~/scan --last-hours 24  # laporan
```

`--probe` mencetak per venue: `OK binance 1500 USDT pairs in 120 ms | BTC bid … ask …` atau `FAIL <venue> <alasan>`.
Kirim hasilnya ke Claude sebelum memasang service: field beberapa venue belum terverifikasi (§3), dan probe adalah
verifikasi sebenarnya. Venue yang gagal tidak menghentikan venue lain.

Status line tiap 60 s: per venue `ok/total`, waktu fetch p50, jumlah koin, ukuran data, ruang disk, plus 3 selisih
terbesar **saat ini** (sekadar indikator; laporan yang menentukan).

## 2. Cara kerja dan filter

- Tiap `--interval` (default 5 s) semua venue di-fetch paralel: best bid/ask + ukuran top-of-book, semua pasangan USDT.
  Satu koneksi keep-alive per venue; venue yang belum selesai dari ronde sebelumnya **tidak** ditanya lagi (dicatat
  "previous fetch still running"), jadi tidak ada request dobel ke venue lambat.
- Satu baris per ronde, hanya koin yang dikutip ≥ 2 venue. File: `<out>/rounds/<YYYYmmdd-HH>_<k>.tsv.gz` (writer yang
  sama dengan recorder: gzip per jam, flush + fsync berwaktu, ekor terpotong tetap terbaca).
- **Episode** = (koin, venue beli, venue jual) selama `bid_jual / ask_beli − 1 − fee_beli − fee_jual ≥ --min-net-bps`
  dan ukuran top-of-book (sisi terkecil) ≥ `--min-usd` (default 0 bps, $50).
- Episode dihitung bila bertahan ≥ `--min-rounds` snapshot berturut-turut (default 2): snapshot venue dalam satu ronde
  diambil paralel, tidak pada detik yang persis sama, jadi selisih satu snapshot bisa artefak waktu. Venue yang gagal di
  satu ronde **mengakhiri** episode (tidak dijembatani).
- Dibuang dan dilaporkan terpisah, karena kemungkinan **tidak bisa diambil**:
  - mid satu venue > 10 % dari median venue lain: biasanya token berbeda di balik ticker yang sama, atau pasar mati.
    Hanya kutipan venue yang menyimpang yang dibuang bila venue yang sepakat mayoritas (≥ 2); kalau tidak bisa
    dipastikan siapa yang benar (mis. hanya 2 venue), koin itu dibuang untuk snapshot tersebut. Filter yang sama dipakai
    di baris `now:` status line (run pertama md-tokyo: RON di Bitget ~1.640× harga MEXC/OKX tampil sebagai
    "+16.410.477 bps" sebelum fix ini);
  - ≥ 3 snapshot tanpa perubahan harga/ukuran di kedua sisi: book beku;
  - bertahan > `--suspect-minutes` (default 10): biasanya deposit/withdraw ditutup, sehingga saldo tidak bisa
    diseimbangkan (inilah jebakan "selisih besar");
  - venue tanpa ukuran top-of-book.
- **$/hari** = jumlah per episode dari profit bersih bila diambil **sekali** di snapshot terbaiknya, sebesar top-of-book.
  Ini sinyal peringkat, bukan ramalan: pemain lain bisa lebih dulu (terlalu optimis), level book berikutnya tidak
  dihitung (terlalu pesimis), dan fee memakai asumsi tier dasar.

**Fee taker default (bps, ASUMSI tier dasar dari ingatan, belum dicek ulang):** Binance 10, Bitget 10, Bybit 10, OKX 10,
MEXC 5, Gate 10, KuCoin 10, Kraken 40. Ubah per laporan dengan `--fee kraken=26 --fee mexc=2` (laporan dihitung ulang dari
data mentah; data tidak perlu direkam ulang). Wajib dicek dari halaman fee resmi sebelum keputusan.

## 3. Endpoint dan status verifikasi

| Venue | Endpoint | Field | Status |
|---|---|---|---|
| Binance | `GET api.binance.com/api/v3/ticker/bookTicker` (weight 4 tanpa symbol) | `symbol, bidPrice, bidQty, askPrice, askQty` | VERIFIED (dokumen resmi, 2026-11-11) |
| Bybit | `GET api.bybit.com/v5/market/tickers?category=spot` | `result.list[]: symbol, bid1Price, bid1Size, ask1Price, ask1Size`; `time` | Field VERIFIED untuk linear/inverse; spot diasumsikan sama |
| Kraken | `GET api.kraken.com/0/public/AssetPairs` + `/0/public/Ticker` (tanpa pair = semua) | `wsname "XBT/USDT"`, `status`; `a/b: [harga, whole lot, lot volume]` | VERIFIED (dokumen resmi; probe: 44 pasangan USDT, ukuran tampak dibulatkan ke lot utuh) |
| Bitget | `GET api.bitget.com/api/v2/spot/market/tickers` | `data[]: symbol, bidPr, bidSz, askPr, askSz, ts` | VERIFIED empiris (`--probe` md-tokyo 2026-11-11: 3.598 pasangan, BTC konsisten) |
| OKX | `GET www.okx.com/api/v5/market/tickers?instType=SPOT` | `data[]: instId "BTC-USDT", bidPx, bidSz, askPx, askSz, ts` | VERIFIED empiris (475 pasangan) |
| MEXC | `GET api.mexc.com/api/v3/ticker/bookTicker` | sama dengan Binance | VERIFIED empiris (1.413 pasangan) |
| Gate | `GET api.gateio.ws/api/v4/spot/tickers` | `currency_pair "BTC_USDT", highest_bid, lowest_ask` | VERIFIED empiris (2.004 pasangan). **Tidak ada ukuran top-of-book** (`highest_size/lowest_size` tidak ada di jawaban): episode Gate masuk "tanpa ukuran", tidak ikut peringkat $/hari |
| KuCoin | `GET api.kucoin.com/api/v1/market/allTickers` | `data.ticker[]: symbol "BTC-USDT", buy, sell, bestBidSize, bestAskSize`; `data.time` | VERIFIED empiris (803 pasangan) |

Run pertama md-tokyo: 8/8 venue ok tiap ronde, ~1 MB/menit (≈ 1,4 GB/hari) → bersama recorder ≈ 5,5 GB/hari, disk 100 GB
cukup ~17 hari. Halaman resmi venue di atas tidak bisa dibuka dari sandbox builder; verifikasinya dari probe. Field yang salah membuat venue itu `FAIL ... 0 USDT
pairs parsed` di `--probe`, tidak salah diam-diam. Interval 5 s jauh di bawah batas rate publik yang tercatat (Binance
weight 4/ronde dari 6.000/menit; venue lain dicek saat probe/run).

## 4. Bukti

`python3 -m unittest discover -s tools/tests -v`: 21 tes scanner (parser 8 venue dari fixture, logika episode persis
terhadap ronde sintetis, filter identitas/beku/lama/tanpa ukuran, venue gagal tidak dijembatani, end-to-end terhadap mock
HTTPS 8 venue dengan satu venue mati, venue lambat tidak ditanya dobel, disk guard, CLI). Mutation 7/7 tertangkap: fee
tidak dikurangkan, tanpa syarat ≥ 2 snapshot, tanpa filter identitas, menjembatani venue yang gagal, tanpa deteksi book
beku, request dobel ke venue lambat, blip disimpan sebagai episode. Stress: 30/30 run seluruh suite tools (10 paralel).

Analisis: ~16 s per jam data sintetis (8 venue × ~300 koin, harga acak), jadi `--last-hours 24` ≈ 6 menit. Ukuran data
sintetis 64 MB/jam gzip (≈ 1,5 GB/hari, angka acak sulit dikompres); ukuran nyata terlihat di status line jam pertama.
Bersama recorder (~4,1 GB/hari), disk 100 GB cukup sekitar 2–3 minggu; disk guard menghentikan scanner dengan rapi
(exit 3) bila sisa < 5 GB, recorder punya guard sendiri.

## 5. Batas yang diketahui

- Resolusi 5 s: peluang di bawah ~5 s tidak terlihat (itu wilayah recorder).
- Top-of-book saja; kedalaman book tidak diambil (mahal untuk ribuan koin). Untuk kandidat teratas, kedalaman bisa
  ditambah ke recorder.
- Status deposit/withdraw kebanyakan venue butuh API key; diganti heuristik "bertahan > 10 menit".
- Ketersediaan venue untuk pengguna Indonesia dan risiko pihak lawan (terutama venue dengan KYC longgar) **tidak** dinilai
  scanner; wajib dicek terpisah sebelum memakai venue untuk trading.
