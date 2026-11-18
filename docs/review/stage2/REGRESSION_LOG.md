# Regression log — Tahap 2 (recorder market data)

Aturan sama dengan Tahap 1 (`TEAM_WORKFLOW.md` §5): tes regresi ditulis dulu, dijalankan merah terhadap kode sebelum fix,
lalu fix, lalu hijau, lalu mutation check. Satu commit per temuan.

## Review ronde 1 — ChatGPT, post-merge PR #6 (`4acb424`), 2026-11-11

Basis: `main` = `bbd5038` (PR #6 + PR #7). Semua tes regresi ditambahkan sekaligus lalu dijalankan terhadap basis tanpa
perubahan kode: **42 tes, 9 failures + 3 errors, semuanya tes baru**; 30 tes lama tetap lulus.

| Temuan | Tes (`tools/tests/test_md_recorder.py`) | Merah di `bbd5038` | Hijau di | Mutan tertangkap |
|---|---|---|---|---|
| C-11 S2 | `test_close_frame_raises_with_code` (kini juga mengassert pesan sebelum close), `test_c11_messages_before_a_close_in_the_same_read_are_delivered`, `test_c11_a_failed_pong_does_not_lose_complete_messages`, `test_c11_bytes_from_the_handshake_are_delivered_even_if_the_peer_then_closes`, `test_c11_unbounded_fragment_stream_is_refused` | `[] != ['last']`, `[] != ['A','B']` (×2), `[] != ['early']`, `WsError not raised` | `efee67c` | 4/4: raise langsung saat close, re-raise gagal pong, lewati drain buffer, tanpa batas fragmen |
| C-12 S2 | `test_c12_a_frame_already_buffered_is_returned_without_waiting`, `test_c12_each_message_is_stamped_at_the_read_that_completed_it`, `test_c12_slow_writing_does_not_move_the_stamps`, `test_c12_subscribe_acks_are_read_while_later_subscribes_wait`, `test_c12_reader_lag_is_measured_and_reported` | menunggu 0,5008 s penuh; `poll_stamped` tidak ada; ack pertama di-stamp 146–150 ms setelah dikirim (3/3 run); tidak ada `stats` | `5683a51` (dua tes parser sudah hijau sejak `efee67c`, kode parser dipakai bersama) | 3/3: `sleep` alih-alih pump, tanpa `stats`, stamp ulang saat menulis (mutan terakhir sempat lolos → ditambah `test_c12_slow_writing_does_not_move_the_stamps`) |
| C-13 S3 | `test_c13_preflight_drops_only_on_an_authoritative_answer` | keep `['BTCUSDT','B400USDT']`: 6 dari 7 jawaban non-otoritatif membuang simbol | `9da5e65` | 3/3: Bitget 400 = tidak terdaftar, baris Binance tanpa cek identitas, Binance 400 apa pun = tidak terdaftar |
| C-14 S3 | `test_c14_flush_is_due_by_time_not_by_the_next_write`, `test_c14_an_idle_feed_is_still_flushed_on_time`, `test_c14_a_busy_feed_is_fsynced_every_interval_too` | API flush timer tidak ada; feed diam: 0 dari 10 baris terlihat saat berjalan; feed sibuk: 0 fsync | `59ac432` | 3/3: tanpa panggilan timer, tanpa fsync, tanpa flush berwaktu |
| B-05 S3 (builder) | `test_b05_an_unexpected_exception_does_not_kill_the_connection_thread` | thread mati setelah bug simulasi, 1 sesi | `603a80a` | 1/1: hanya menangkap tipe lain |

Sesudah semua commit: 13 mutan semula PR #6 dijalankan ulang, 13/13 tertangkap. Stress 30/30 (10 suite paralel).
Benchmark: `tools/tests/bench_md_recorder.py` (lihat `docs/STAGE2_RECORDER.md` §4).

## Audit builder 2026-11-12 (Fable) — basis `5d45923`

Tes ditambahkan dulu, dijalankan terhadap basis: 77 tes, 8 error (semua tes baru; API belum ada). Dua temuan dengan
perilaku salah yang terukur di basis: B-06 (`{'ok': 50}`, $35.769/hari untuk satu selisih 6 % yang menetap 25 menit) dan
B-07 (1 episode 3.000 ronde menyimpan 3.000 + 3.000 sampel dan 50 leg).

| Temuan | Tes | Mutan tertangkap |
|---|---|---|
| B-06 S2 | `test_b06_a_persistent_gap_broken_up_by_fetch_failures_is_not_many_ok_episodes`, `test_b06_short_gaps_that_recur_a_few_times_stay_tradable` | 1/1: tanpa aturan per jam |
| B-07 S2 | `test_b07_a_very_long_episode_keeps_bounded_state` | 3/3: sampel tak terbatas, leg tak terbatas, sampel tidak dibebaskan |
| B-08 S3 | `test_b08_one_dead_book_stream_forces_a_reconnect`, `test_b08_a_quiet_trade_stream_is_not_a_dead_stream`, `test_b08_stream_watchdog_backs_off_and_recovers` | 5/5: watchdog tidak pernah menyala, trade ikut diawasi, batas tidak menggandakan, batas tidak pulih, last-seen tidak diperbarui |
| B-09 S3 | `test_b09_a_dead_connection_thread_ends_the_process_with_an_error` | 1/1: thread mati diabaikan |
| B-10 S3 | `test_b10_report_shows_direction_balance_and_quote_freshness`, `test_b10_direction_share_names_the_side_that_dominates` | 2/2 (mutan "arah mengabaikan sisi" sempat lolos → tes dua arah ditambah) |

Sesudah fix: 78 tes hijau (28 scanner, 40 recorder, 10 probe), stress 30/30 (10 suite paralel).

## Langkah 2 — `tools/pair_analysis.py` (basis `efa109e`)

Tes ditulis dulu dan dijalankan terhadap basis: 1 error (modul belum ada). Setelah implementasi: 19 → 21 tes hijau
(dua tes ditambah karena mutan lolos, lihat di bawah). Suite penuh: 99 tes (21 analisis, 28 scanner, 40 recorder, 10 probe).

| Mutan | Hasil |
|---|---|
| capture memakai keadaan saat buka, bukan saat tiba | lolos pada putaran 1 (keadaan hanya berubah sekali) → `test_capture_uses_the_state_at_arrival_not_at_open` diberi perubahan kedua → tertangkap |
| capture dihitung juga bila L = durasi | tertangkap (`test_gap_closing_exactly_on_arrival_is_missed`) |
| kutipan tidak dilupakan saat putus | tertangkap |
| fee hanya satu leg | tertangkap |
| ukuran = leg terbesar | tertangkap |
| arah tertukar | tertangkap |
| label penutup `gap` hilang | tertangkap (cabang "gross None" yang lolos ternyata kode mati, dihapus) |
| tanpa penggabungan waktu antar-koneksi | tertangkap |
| potongan `--last-hours` di dalam file mati | lolos pada putaran 1 → `test_last_hours_cut_also_inside_a_file` → tertangkap |
| basis terbalik | tertangkap |

Hasil akhir 10/10, dijalankan ulang tanpa cache bytecode (temuan B-12: putaran pertama sempat memakai `.pyc` mutan).

## Langkah 2b — keterlambatan feed di `pair_analysis.py` (basis `3c13b1c`)

Tes ditulis dulu: 9 merah terhadap basis (8 error: API belum ada, 1 gagal). Setelah implementasi 30 hijau; satu mutan lolos
→ tes ke-31. Suite penuh 109 tes. Mutasi dijalankan dengan `python3 -B` (B-12).

| Mutan | Hasil |
|---|---|
| dasar lag tidak bergulir (perubahan jam = feed telat selamanya) | tertangkap |
| sampel lama dianggap terkini | tertangkap |
| lag mentah tanpa dasar | tertangkap |
| ambang diabaikan | tertangkap |
| tanpa sampel dianggap normal | tertangkap |
| trade Binance tidak disampel / `ts` Bitget tidak disampel | 2/2 tertangkap |
| capture "feed normal" menghitung semua episode | tertangkap |
| tanpa batas kewajaran stempel | lolos → `test_an_implausible_venue_timestamp_is_not_a_lag_sample` → tertangkap |
| status feed dipakai ulang dari episode lain | tertangkap |

## Langkah 3 — `md_recorder.py --market perp` (basis `c06b31f`)

Tes ditulis dulu: 7 merah di recorder (6 error: API belum ada; 1 gagal), 2 merah di analisis. Satu tes CLI sempat lolos
karena alasan yang salah (argparse menolak `--market` sebagai argumen tak dikenal) → diperketat agar menuntut "invalid
choice". Tes `books1` USDT-FUTURES hijau sejak awal (parser sudah kompatibel); dipertahankan sebagai pengaman. Tes CLI
"pakai preflight perp dan host perp" ditambah setelah implementasi untuk menutup mutan dispatch.

Mock Binance kini hanya melayani stream di path yang benar (`/public`: bookTicker/depth; `/market`: aggTrade/markPrice),
sesuai perubahan Binance 2026-04-23, sehingga salah routing = stream bisu = tes merah.

| Mutan | Hasil |
|---|---|
| stream market di `/public` | tertangkap |
| `contractType` diabaikan / status Bitget diabaikan / baris Binance hilang tetap disimpan | 3/3 |
| daftar futures kosong dianggap otoritatif | tertangkap |
| Bitget perp dikirim sebagai SPOT / tanpa channel `ticker` (funding) | 2/2 |
| nama stream di-lowercase di URL (`markPrice` case sensitive) | tertangkap |
| CLI memakai preflight spot | tertangkap |
| `E` bookTicker perp / `aggTrade` tidak disampel di analisis | 2/2 |

11/11, dengan `python3 -B`. Suite penuh: 120 tes hijau.

## Langkah 4 — `tools/funding_analysis.py` (basis `547cd59`)

Tes ditulis dulu (modul belum ada: merah). Parser diuji dengan pesan nyata dari md-tokyo. Setelah implementasi 14/14
hijau pada percobaan pertama; tiga tes lalu diperketat sebelum mutasi karena masing-masing punya mutan yang akan lolos:
settlement (rate sebelum dan sesudah 08:00 sama), perubahan jadwal (tidak ada observasi dalam 10 menit sebelum waktu
funding), dan kesegaran prediksi saat masuk (tes baru). Hasil 15 tes.

| Mutan | Hasil |
|---|---|
| rate settle = rate setelah pergantian / tanpa syarat 10 menit / perubahan jadwal dihitung settlement | 3/3 |
| sisi hold terbalik / hold tanpa fee / switch tanpa biaya / histeresis diabaikan | 4/4 |
| prediksi melihat 1 jam ke depan / prediksi basi diterima | 2/2 |
| interval tidak dinormalisasi (akrual / prediksi) | 2/2 |
| basis terbalik | 1/1 |

12/12, `python3 -B`.

## B-13 — settlement funding (basis `367b9bf`)

Run pertama di md-tokyo (18,5 jam): settlement Binance 0, Bitget 1. Diagnosa di VM (BTC, kedua venue): label waktu funding
berganti 0,4–1,05 s SESUDAH T, dan observasi terakhir berlabel lama jatuh sesudah T. Dugaan builder sebelumnya (label
berganti sebelum T) salah. Tes sintetis PR #16 berganti tepat di T, jadi tidak menangkapnya.

Tes dulu: `test_old_funding_time_still_shown_just_after_it_passed_is_a_settlement` merah terhadap basis; tes "label macet
20 menit" ditambah (hijau di basis, menjaga batas 5 menit). Setelah perbaikan 17/17. Mutation 14/14 (dua mutan baru: label
macet diterima, observasi sesudah T dihitung sebagai sebelum).

## Laporan per koin dan funding per tahun (basis `a8fc071`)

Tes ditulis dulu: 8 merah (pair: bergantian arah, hanya level utama, `top_share`, capture per episode, tabel §2b;
funding: bruto/tahun, tanpa balik modal saat rugi, kolom lama hilang). Setelah implementasi 59/59 untuk kedua alat.
Mutation 8/8 (`python3 -B`): bergantian di semua level, hitung pada arah sama, episode pertama dihitung pasangan, porsi
terkecil, porsi pada data kosong, bruto dari net, balik modal saat rugi, balik modal per jam bukan per hari.

## Langkah 5 — `tools/perp_study.py` (basis `943caee`)

Tes ditulis dulu (merah: modul belum ada). Putaran pertama: 16/18 hijau; dua gagal karena tes builder sendiri (waktu
relatif vs epoch → kini nanodetik integer; tes CLI memakai `--close-bps` default 2 sedangkan jalan balik −3 bps, dan
mengharapkan kata "converged" padahal kolom berisi jumlah). Mutation 14/15 di putaran pertama: jendela baseline 10× lebih
lebar lolos (tes hanya punya satu trade di dalam dan satu di luar) → `test_a_trade_just_outside_the_equal_window_is_not_baseline`
→ 15/15 (`python3 -B`).
