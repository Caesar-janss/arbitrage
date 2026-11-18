# Tinjauan celah menyeluruh — 2026-11-18

Diminta Jansen: "cek secara keseluruhan di mana celah yang belum kita cek". Dokumen ini mendaftar **apa yang belum diukur,
belum diverifikasi, atau belum dibangun**, kenapa penting, cara mengeceknya, siapa yang bisa, dan prioritasnya. Sumber:
semua laporan Tahap 2, audit 2026-11-12, dan log temuan.

**Di mana kita berdiri:** spot Binance ↔ Bitget taker-taker = no-go (5,8 hari, ≈ $11/hari batas atas). Perp: 19 jam data,
belum layak dan belum gugur. Funding: 24 jam, selisih antar-venue ≤ 0,64 bps/8 jam (≈ 4–7 %/tahun bruto, kondisi pasar tenang).

Prioritas: **T** = menentukan go/no-go, **S** = perlu sebelum uang sungguhan, **R** = perbaikan.

## 0. Status butir audit 2026-11-12

| Butir | Status 2026-11-18 |
|---|---|
| O-1 disk | **Selesai**: 200 GB; pemakaian riil perp ≈ 5–6 GB/hari (bukan 11) → cukup ≈ 4 minggu |
| O-2 repo publik | **Terbuka** (Jansen): mau privat; butuh deploy key read-only di VM sebelum diubah |
| O-3 `main` tanpa proteksi | **Terbuka** (Jansen): butuh GitHub Pro / Student Pack untuk repo privat |
| O-4 tanpa alarm | **Terbuka**: lihat G-22 |
| O-5 aturan Indonesia | **Terbuka**: lihat G-18; catatan: aplikasi KuCoin dan MEXC tidak bisa dibuka 2026-11-17 |
| O-6 fee asumsi | **Sebagian**: tier dasar terverifikasi dari halaman resmi (§3 G-20); tier akun belum |
| A-1 spot tidak netral | Tidak relevan lagi (spot no-go) |
| A-2 perp belum diukur | **Sebagian**: 19 jam, recorder perp berjalan |
| A-3 klaim lead-lag | Diganti ukuran "dibuka/ditutup oleh" |
| A-4 $/hari ≠ pendapatan | **Masih berlaku**; versi perp = biaya menutup posisi (G-1) |

## 1. Strategi dan pengukuran

| # | Celah | Kenapa penting | Cara cek | Siapa | Prio |
|---|---|---|---|---|---|
| G-1 | Biaya menutup posisi perp belum dimodelkan per posisi; laporan masih "sekali silang − fee" | Satu putaran penuh taker = 22 bps, bukan 11; bisa menghapus semua angka $/hari | Simulasi posisi di rekaman: buka di episode, tutup di episode arah sebaliknya atau saat batas waktu di harga pasar, hitung untung/rugi termasuk perubahan basis. Kolom "bergantian arah" (PR #18) memberi petunjuk pertama | builder | T |
| G-2 | Depth di bawah level 1 tidak dipakai (depth20 dan books15 sudah direkam) | Kapasitas per peluang dan slippage untuk ukuran > top-of-book | Hitung ukuran yang bisa diisi sampai net = 0 per episode | builder | R |
| G-3 | **Bukti pesaing** belum dicek | "Tersisa pada L ms" mengandaikan tak ada yang mengambil lebih dulu | Pasif, dari data yang sudah ada: apakah ada trade tercetak di harga basi venue tertinggal selama episode? Bila hampir selalu ada, peluang sudah diambil orang lain | builder | T |
| G-4 | Latensi **order** (kirim → ack → isi) belum pernah diukur; S0 hanya mengukur data pasar | Jendela 5–20 ms tidak berarti apa-apa bila order ke Bitget butuh 30–100 ms | Butuh API key + order kecil (atau testnet, yang latensinya tidak representatif) → jalur TEAM_WORKFLOW §6 | Jansen + builder | T |
| G-5 | Lonjakan keterlambatan feed Binance perp: kelebihan p99 266 ms (spot 4 ms) | Episode saat Binance telat bisa semu; penyebab belum jelas (sumber stempel `aggTrade`/`bookTicker` vs antrean nyata) | Pecah keterlambatan per jenis stream dan per jam; bandingkan dengan beban | builder | R |
| G-6 | Sinkronisasi jam VM belum dicek | Durasi aman (satu jam), tetapi kelebihan lag mengandaikan offset jam stabil | `chronyc tracking` di VM (1 perintah) | Jansen | R |
| G-7 | Makna `ts` Bitget `books1` (waktu snapshot vs waktu kejadian) belum diverifikasi | Ukuran "feed telat" Bitget bergantung padanya | Dokumen Bitget + bandingkan `ts` dengan trade yang mengubah book | builder | R |
| G-8 | Ukuran sampel: perp 19 jam, funding 2 settlement | Sampel 6 jam spot dulu meleset sampai 23× | Biarkan rekaman berjalan; analisis ulang ≥ 21–22 Nov, keputusan ≥ 7 hari | waktu | T |
| G-9 | Hanya 15 koin pilihan tangan; perp lain tidak pernah dipindai | Peluang (dan funding ekstrem) sering di perp yang lebih liar | Scanner REST untuk semua perp USDT di kedua venue: selisih mid dan funding | builder | R |
| G-10 | Funding hanya 2 venue; Bybit/OKX (rencana awal Bybit untuk funding) dan carry spot↔perp tidak diukur | Selisih funding biasanya lebih besar bila venue lebih banyak; carry spot↔perp tidak butuh dua venue | Tambah Bybit/OKX ke recorder perp (funding saja, murah) | builder | R |
| G-11 | Net funding belum memasukkan basis saat buka/tutup | Basis p10–p90 ±5–15 bps ≈ funding berhari-hari | Simulasi posisi funding dengan mark nyata saat buka dan tutup | builder | T |
| G-12 | Semua data dari minggu yang tenang | Funding dan selisih harga sangat bergantung kondisi pasar | Jangan memutuskan dari satu rezim; teruskan rekaman melewati hari ramai | waktu | T |

## 2. Eksekusi dan risiko (belum ada kodenya)

| # | Celah | Kenapa penting | Cara cek | Siapa | Prio |
|---|---|---|---|---|---|
| G-13 | Minimum order dan ukuran langkah per simbol per venue vs modal $100 | Dengan $100 kemungkinan tidak bisa membuka pasangan ter-hedge yang sama ukurannya di dua venue | Ambil filter dari `exchangeInfo` (Binance) dan `contracts` (Bitget); hitung modal minimum per koin | builder | S |
| G-14 | Margin dan likuidasi dua leg di dua venue | Leg yang rugi bisa terlikuidasi di satu venue sementara untungnya ada di venue lain; rebalancing USDT antar venue butuh waktu dan biaya transfer | Kebijakan leverage, simulasi gerak harga ekstrem, ukur waktu/biaya transfer USDT per jaringan | builder + Jansen | S |
| G-15 | Batas rate dan aturan API trading per venue | Bisa membatasi frekuensi order atau melarang pola tertentu | Dokumen resmi; catat batas per menit/detik | builder | S |
| G-16 | Risiko satu leg gagal (legging) | Risiko eksekusi utama arbitrase dua venue | Rancang aturan: bila leg kedua gagal, tutup atau hedge dalam batas waktu; uji di simulasi | builder | S |
| G-17 | Inti C# Tahap 1 belum diaudit ulang dan belum mengenal perp (posisi, funding, margin) | Semua pengukuran Tahap 2 di Python; eksekusi nyata akan lewat inti C# | Audit ulang + desain tambahan perp sebelum Tahap 3 | builder | S |

## 3. Legal, akun, biaya

| # | Celah | Kenapa penting | Cara cek | Siapa | Prio |
|---|---|---|---|---|---|
| G-18 | Legalitas dan akses Binance/Bitget (dan API dari server Tokyo) untuk penduduk Indonesia; pajak kripto | Bisa membatalkan venue atau menambah biaya per transaksi | Riset sumber resmi (OJK, ketentuan exchange); bukan nasihat hukum | Jansen (builder bisa riset awal) | S |
| G-19 | Keamanan API: sub-account, key trade-only, withdraw OFF, whitelist IP; IP eksternal VM kemungkinan *ephemeral* | Whitelist rusak setelah VM berhenti/menyala; key tanpa whitelist berisiko | Console GCP: cek tipe IP, reservasi IP statis (biaya kecil) sebelum membuat key | Jansen | S |
| G-20 | **Tier fee akun** (spot, futures, maker/taker, diskon BNB/BGB) | Variabel terbesar di semua tabel. Dasar terverifikasi 2026-11-18: Binance USDⓈ-M maker 0,02 % / taker 0,05 % (BNB −10 %); Bitget futures maker 0,02 % / taker 0,06 %; Bitget spot 0,1 % (BGB 0,08 %) | Screenshot halaman fee akun | Jansen | T |
| G-21 | Anggaran: credit GCP ≈ US$300 sampai 10 Feb 2027; md-tokyo ≈ US$75–80 + disk ≈ US$10–12 + reuztiz ≈ US$40 per bulan | Credit habis kira-kira akhir Januari; pengingat billing mingguan belum dibuat ulang | Keputusan: pengingat ulang; batas waktu Tahap 2 | Jansen | R |

## 4. Operasi dan keamanan

| # | Celah | Kenapa penting | Cara cek | Siapa | Prio |
|---|---|---|---|---|---|
| G-22 | Tidak ada alarm bila recorder berhenti (O-4) | Berhenti diam-diam = hari data hilang | Alert GCP Monitoring (proses/log) atau `OnFailure=` systemd + notifikasi | builder + Jansen | R |
| G-23 | Repo publik, `main` tanpa proteksi (O-2, O-3) | Strategi terbaca publik; push tanpa CI mungkin | Deploy key di VM → privat → branch protection | Jansen | R |
| G-24 | Data rekaman hanya di satu disk, tanpa snapshot | Kerusakan disk = semua data hilang | Snapshot schedule disk (murah) | Jansen | R |
| G-25 | PR #12–#18 belum direviu ChatGPT (reviewer) | Keputusan go/no-go diambil dari kode yang hanya diperiksa builder | Paket reviu untuk ChatGPT: alat analisis dan perbaikan B-13 | Jansen (kirim) | R |
| G-26 | Data spot lama (~25 GB) dan scanner: simpan atau hapus | Ruang disk | Putuskan setelah analisis perp selesai | Jansen | R |

## 5. Urutan yang disarankan

1. **Jansen, ±10 menit:** screenshot tier fee (G-20); `chronyc tracking` (G-6); cek tipe IP eksternal VM (G-19).
2. **Builder, pada data yang sudah ada:** G-3 (bukti pesaing), G-1 (simulasi posisi termasuk penutupan), G-11 (funding + basis), G-5.
3. **Waktu:** rekaman berjalan sampai ≥ 21–22 Nov; analisis ulang semua laporan.
4. **Jansen:** G-18 (aturan Indonesia), G-23, G-22, G-24.
5. Hanya bila langkah 2–3 menunjukkan net positif yang kuat: G-13, G-14, G-16, G-17, lalu G-4 lewat TEAM_WORKFLOW §6.

**Usulan kriteria go untuk perp** (keputusan Jansen): setelah biaya penutupan, hanya episode feed normal, pada L ≥ 10 ms,
dengan bukti pesaing rendah (G-3), selama ≥ 7 hari dan bukan satu rezim pasar saja: net simulasi ≥ 3× biaya infrastruktur
(≈ $8/hari) dengan modal yang Anda siap pakai, dan porsi 10 episode terbesar < 30 %. Bila tidak tercapai, perp spatial
ditutup seperti spot, dan sisa jalur adalah funding/carry (rendah, stabil, bergantung kondisi pasar).
