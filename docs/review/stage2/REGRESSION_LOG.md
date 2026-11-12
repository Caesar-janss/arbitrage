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
