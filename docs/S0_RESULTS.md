# S0 — Hasil probe latency Tokyo vs Singapura (2026-11-02 → 03)

Status: **rekomendasi, menunggu keputusan Jansen** (merge PR ini = opsi (a) dipilih).
Data mentah: `docs/s0-data/probe_2026-11-02_tokyo_singapore.tgz` (2 × 24 file per jam, 3600 sampel/jam/target).
Host: GCP `asia-northeast1-a` (Tokyo) dan `asia-southeast1-b` (Singapura), c4-standard-2, chrony tersinkron (offset host < 0.1 ms).
Laporan lengkap: `python3 tools/latency_probe.py --compare tokyo/*.json sg/*.json`.

## 1. Angka inti (ms, p50; RTT origin = min(REST, WS))

| Venue | Tokyo | Singapura |
|---|---|---|
| Binance spot / USDⓈ-M | **7.6 / 5.1** | 74 / 75 |
| Bitget | **7.7** | 76 |
| Bybit spot / linear | 71 / 72 | **3.1 / 3.3** |

| Pasangan spatial (kaki terlambat) | Tokyo p50 / p99 terburuk | Singapura |
|---|---|---|
| Binance ↔ Bitget | **7.7 / 245** | 76 / 326 |
| Bitget ↔ Bybit | 71 / 245 | 76 / 326 |
| Binance ↔ Bybit | 71 / 107 | 74 / 175 |

Error: ≤ 4 per 86 400 permintaan per target. Offset jam venue: semua median < 3 ms.

## 2. Pembacaan

1. **Peta lokasi jelas:** Binance dan Bitget dekat Tokyo, Bybit dekat Singapura. Tidak ada region yang dekat dengan ketiganya,
   jadi opsi (b) gugur (Bitget↔Bybit ≥ 71 ms dari kedua region).
2. **Satu-satunya pasangan ko-lokasi: Binance ↔ Bitget dari Tokyo** (p50 7.7 ms). Ambang panduan "≲ 5 ms" sedikit terlewat;
   probe jalan di GCP sedangkan Binance di AWS Tokyo, jadi dari AWS `ap-northeast-1` kemungkinan lebih rendah (hipotesis, belum diukur).
3. **Opsi (c) tidak menolong:** setiap pasangan yang melibatkan Bybit tetap lintas region (~70 ms) dari mana pun proses ditaruh.
   Dua region hanya menambah kompleksitas tanpa membuat pasangan baru ko-lokasi.
4. **Bybit untuk funding/hedge dari Tokyo cukup:** strategi funding memegang posisi berjam-jam; 71 ms tidak relevan.
5. **Risiko utama = ekor latency Bitget**, bukan median. Tokyo: WS p90 23 ms, p99 per jam median ~62 ms, terburuk 245 ms.
   Jam dengan p99 > 190 ms berkumpul di **12:50–20:50 UTC** (sesi EU/US, saat volatilitas dan peluang arb paling tinggi).
   Binance sebaliknya stabil (REST p99 ≤ 15 ms setiap jam). Baru satu hari data (Senin), pola harian belum terbukti.

## 3. Rekomendasi: opsi (a)

Spatial **Binance ↔ Bitget dari Tokyo**; Bybit dipakai untuk funding-rate/hedge (latency-insensitive), dari host yang sama.

Konsekuensi untuk Tahap 2 (masuk ke daftar integrasi):
- Timeout kaki Bitget dan batas legging dihitung dari **p99 sesi sibuk (~250 ms)**, bukan p50.
- Kaki pertama yang dikirim = venue lebih lambat/berisiko (Bitget), atau keduanya paralel; diputuskan di desain executor.
- Monitor latency Bitget live; jika p99 sesi > ambang, pricer menaikkan buffer atau berhenti entry (bukan hanya alert).
- Sebelum live: ulang probe 24–72 jam dari host produksi (AWS Tokyo vs GCP Tokyo) dan ukur jalur **order entry** (REST order/WS trade), bukan hanya endpoint publik.
