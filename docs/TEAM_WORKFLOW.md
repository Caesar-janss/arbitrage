# Team Workflow — ArbEngine

> **v0.3 (2026-10-27).** Disusun dari tiga proposal (Claude, ChatGPT, Gemini). **Gemini keluar dari tim** (keputusan
> Jansen, §9): tidak punya akses repo, relay manual memakan waktu. Peran AI tetap dievaluasi dengan metrik §8.

Tim: **Jansen** (satu-satunya manusia: owner, keputusan, akun/key, ops) · **Claude** (builder) · **ChatGPT** (reviewer
independen, satu-satunya model di luar keluarga Claude) · **Claude-R** (agen Claude terpisah dengan konteks kosong,
dijalankan Claude untuk review).

**Akses repo:** Claude dan ChatGPT sama-sama tersambung langsung ke `Caesar-janss/arbitrage` (GitHub App / connector).
Hak tulis kode hanya untuk builder lewat branch + PR. Reviewer hanya membaca kode dan menulis **komentar/review PR**,
tanpa commit, push, atau merge.

## 0. Lima aturan inti (disepakati ketiga proposal awal)
1. **Satu pemilik per modul.** Dua anggota tidak menulis file yang sama di tahap yang sama.
2. **Reviewer tidak commit.** Patch reviewer masuk sebagai *temuan* dan ikut triase.
3. **Wasit = bukti.** Test yang gagal, hitungan yang bisa diulang, CI GitHub, atau dokumen resmi venue. Bukan voting. Tidak ada anggota yang selalu benar.
4. **Satu pengambil keputusan: Jansen.** Keputusan dicatat `Q: … A: …` dan dieksekusi tanpa tanya ulang.
5. **Uang dan kunci hanya di tangan Jansen.** API key tidak pernah masuk chat AI, paket review, atau repo.

## 1. Kriteria memilih builder/integrator (bukan penunjukan diri sendiri)
| Kriteria | Cara membuktikan |
|---|---|
| Bisa build & menjalankan test .NET 8 + PostgreSQL di lingkungannya | tunjukkan output `dotnet build` + `dotnet test` dari zip versi terbaru |
| Memegang versi terbaru | menyebut hash versi (`tools/make_review_packet.py --version`) |
| Track record | precision temuan & bug yang lolos di ronde sebelumnya (§8) |

CI GitHub (`.github/workflows/ci.yml`) adalah wasit akhir "test lulus" untuk semua anggota.

## 2. Peran (usulan awal)
| Anggota | Tanggung jawab | Diserahkan | Tidak boleh |
|---|---|---|---|
| **Jansen** | Keputusan (venue, limit risiko, go/no-go), akun exchange & API key, repo GitHub (`Caesar-janss/arbitrage`), VPS Tokyo, jalankan probe latency / testnet / paper trading, relay paket ke ChatGPT | `Q: A:`, merge PR, data lapangan | Menempel key ke AI |
| **Claude** | Builder modul inti (kode + test + mutation test), triase temuan dengan bukti, docs & `FINDINGS_LOG`, membuat paket review, menjalankan Claude-R. **Mengambil alih tugas Gemini:** verifikasi asumsi API venue dengan link dokumen resmi (tabel VERIFIED/CONTRADICTED/UNVERIFIED, wajib sebelum adapter Tahap 2) | zip/PR + laporan serah-terima (§4) | Menyentuh key, deploy live, klaim API tanpa sumber |
| **ChatGPT** | **Numerik & low-level**: hitung ulang angka test (Python), derivasi rumus, red-team urutan kegagalan, audit fokus file kritis (seqlock, Int128 overflow, race). **Ditambah:** cek silang tabel asumsi API venue dari Claude, dan **re-review wajib** untuk fix/modul S1 (margin, repair, state machine, gateway order) | temuan + tabel hitung ulang + usulan test adversarial (file `.md`) | Mengubah angka tanpa hitungan |
| **Claude-R** | Review adversarial dengan eksekusi kode, tanpa melihat proses pembuatan. **Ditambah:** audit konsistensi seluruh repo (C# ↔ SQL ↔ docs), sebelumnya tugas Gemini | `FINDINGS.md` | Menulis ke repo |
| ~~Gemini~~ | Keluar sejak 2026-10-27 (§9). Tugasnya dipindah ke Claude (verifikasi API venue), Claude-R (audit seluruh repo), dan ChatGPT (cek silang) | – | – |

**Risiko yang diterima:** reviewer di luar keluarga model Claude tinggal satu (ChatGPT). Builder (Claude) dan Claude-R bisa
punya blind spot yang sama. Mitigasinya: re-review ChatGPT **wajib** untuk S1 sebelum merge, dan CI/test kontrak tetap
menjadi wasit.

## 3. Alur satu tahap (tanpa copy-paste: kedua AI membaca repo langsung)
```
Jansen: keputusan/spek (Q:A:)
  → Claude: build + test → STAGEx doc → push branch → (draft) PR #N
  → paralel, timebox 48 jam:
       Claude-R (otomatis) │ ChatGPT: review PR #N pada commit <sha> langsung dari repo │ CI GitHub
  → ChatGPT menulis temuan LENGKAP sebagai review/komentar di PR #N (format FINDINGS_TEMPLATE, ID C-xx, sebut <sha>)
  → Claude membaca komentar PR lewat API → triase CONFIRMED/REJECTED/NEEDS-DATA/DUPLICATE → fix + test → FINDINGS_LOG
  → ChatGPT cek ulang fix S1 di PR yang sama → CI hijau → Jansen merge → gate tahap berikutnya
```
Relay Jansen per tahap: **satu pesan pendek ke ChatGPT** ("review PR #N @ <sha>, tulis temuan sebagai komentar PR").
Tidak ada file yang dioper. Kalau connector ChatGPT ternyata read-only (tidak bisa berkomentar), fallback: ChatGPT
menulis temuan lengkap ke file `.md`, Jansen upload 1 file ke Claude. Paket review (`tools/make_review_packet.py`) kini
hanya cadangan untuk reviewer tanpa akses repo.

## 4. Serah-terima (wajib di setiap PR/zip — diadopsi dari proposal ChatGPT)
1. Versi dasar (hash) dan tugas yang dikerjakan.
2. File yang berubah + alasan.
3. Test yang **benar-benar dijalankan** + hasilnya (dan di mana dijalankan: sandbox / lokal / CI).
4. Masalah terbuka + pekerjaan berikutnya.

## 5. Temuan & triase
Format: `docs/review/FINDINGS_TEMPLATE.md` (prefix C/R/J; prefix G tidak dipakai lagi; severity S1–S4).

| Status | Syarat |
|---|---|
| CONFIRMED | test baru gagal sebelum fix, lulus sesudahnya |
| REJECTED | alasan + bukti |
| NEEDS-DATA | pemilik + langkah verifikasi |
| DUPLICATE | rujuk ID lain |

S1 wajib selesai sebelum tahap berikutnya.

## 6. Keamanan & live trading (satu manusia → pengganti two-person rule)
- **Akses AI ke GitHub dibatasi ke repo ini saja** ("Only select repositories" → `Caesar-janss/arbitrage`), untuk GitHub
  App Claude maupun connector ChatGPT. Akun/org lain (termasuk org kantor) tidak boleh ikut ter-otorisasi.
- **`main` hanya berubah lewat PR yang di-merge Jansen.** AI tidak push ke `main`. Bila paket akun mengizinkan (GitHub
  Pro/Team untuk repo private), aktifkan ruleset: require pull request + status check `build-test` & `db-schema`, block
  force push. Status 2026-10-27: `main` belum terproteksi.
- API key: **trade only, withdraw OFF**, IP whitelist ke VPS; hanya di env/secret store VPS.
- `tools/make_review_packet.py` hanya memuat whitelist dan **berhenti** jika ada konten mirip credential.
- Mengaktifkan live (`IsPaperTrading=false`) butuh **ketiganya**:
  1. PR dengan checklist risiko terisi + CI hijau;
  2. **cooling-off 24 jam** antara approve dan aktif (keputusan tidak boleh diambil saat emosi/terburu-buru);
  3. batas keras di config yang divalidasi saat startup (max notional per trade, max daily loss, max posisi). Engine menolak start jika melewati batas pilot.
- Kill-switch `FLATTEN_AND_STOP` bisa dipicu Jansen kapan saja (CLI + Telegram).

## 7. Roadmap sampai "beres"
| Tahap | Isi | Builder | Data/ops (Jansen) | Gate keluar |
|---|---|---|---|---|
| 0 ✅ | Blueprint + skema DB | Claude | — | test DB lulus |
| 1 ✅ review | `Arb.Core` | Claude | push repo → CI hijau | 0 temuan S1 terbuka |
| S0 sekarang | Ronde review Tahap 1 · CI GitHub · probe latency | Claude (script probe) | jalankan probe dari Tokyo & Singapura | pilih pasangan venue (opsi a/b/c) |
| 2 | `Venues.Common` + 2 adapter: market data, sinkronisasi book, reconnect, clock offset, perekaman, replay test | Claude | rekam WS asli | replay 24 jam konsisten; gap → resync |
| 3 | Engine paper end-to-end + Persistence writer | Claude | DB + VPS | partial fill, latency, fee, kaki gagal tercakup |
| 4 | Gateway + user stream + recovery (testnet) + alert Telegram | Claude | testnet + chaos test | recovery tidak menggandakan order / melupakan eksposur |
| 5 | Paper trading ≥ 14 hari | — | operasi + pantau `rpt.*` | bukti cukup: lanjut / ubah / berhenti |
| 6 | Pilot live terbatas (mis. US$20–100) | Claude (fix) | Jansen (§6) | selisih paper vs live terukur; kontrol risiko terbukti |
| 7 | Operasional: deploy, monitoring, backup, panduan insiden | Claude | Jansen | bisa dijalankan & dipulihkan sesuai prosedur |

"Selesai dibangun" ≠ "terbukti untung". Jika gate Tahap 5 menunjukkan tidak ada edge, berhenti adalah hasil yang benar.

## 8. Evaluasi anggota AI (akhir tiap tahap)
| Metrik | Rumus |
|---|---|
| Precision | CONFIRMED / (CONFIRMED + REJECTED) |
| Severity yield | jumlah S1+S2 terkonfirmasi |
| Unique catch | terkonfirmasi yang tidak ditemukan reviewer lain |
| Escaped bugs | bug yang lolos review lalu ketahuan di CI/paper/live |

Peran dirotasi berdasarkan angka ini.

## 9. Keputusan (pemilik: Jansen)
Q: Gemini tetap di tim? A: **Tidak** (2026-10-27): tidak bisa akses repo, relay manual tidak sepadan dengan waktunya.
Project dikerjakan Claude + ChatGPT (peran di §2).
Q: ChatGPT akses repo? A: **Ya**, tersambung langsung ke repo (2026-10-27) → review dilakukan di PR, bukan lewat paket.

Terbuka:
- Pasangan venue spatial (opsi a/b/c, `STAGE1_CORE.md` §5), setelah data probe latency.
- Mode akun Bitget: UTA atau classic (menentukan WS order entry).
