# Team Workflow — ArbEngine

> **Draft v0.2 — usulan, final setelah keputusan Jansen** (`Q: … A: …`).
> Disusun dari tiga proposal (Claude, ChatGPT, Gemini). Peran AI bersifat **sementara** sampai ronde review
> Tahap 1 selesai dan diukur dengan metrik §8. Proposal yang menunjuk dirinya sendiri sebagai lead bukan bukti.

Tim: **Jansen** (satu-satunya manusia: owner, keputusan, akun/key, ops) · **Claude** · **ChatGPT** · **Gemini** ·
**Claude-R** (agen Claude terpisah dengan konteks kosong, dijalankan Claude untuk review).

## 0. Lima aturan inti (disepakati ketiga proposal)
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
| **Jansen** | Keputusan (venue, limit risiko, go/no-go), akun exchange & API key, repo GitHub (`Caesar-janss/arbitrage`), VPS Tokyo, jalankan probe latency / testnet / paper trading, relay paket ke ChatGPT & Gemini | `Q: A:`, merge PR, data lapangan | Menempel key ke AI |
| **Claude** | Builder modul inti (kode + test + mutation test), triase temuan dengan bukti, docs & `FINDINGS_LOG`, membuat paket review, menjalankan Claude-R | zip/PR + laporan serah-terima (§4) | Menyentuh key, deploy live |
| **Gemini** | Audit **seluruh repo** (konteks panjang): kontrak C# ↔ SQL ↔ docs, logika quant (wallet trap, likuidasi, fee VIP/BGB, legging), **verifikasi asumsi API venue dengan link dokumen resmi** | temuan + tabel asumsi venue (VERIFIED/CONTRADICTED/UNVERIFIED) | Klaim API tanpa sumber |
| **ChatGPT** | **Numerik & low-level**: hitung ulang angka test (Python), derivasi rumus, red-team urutan kegagalan, audit fokus file kritis (seqlock, Int128 overflow, race) | temuan + tabel hitung ulang + usulan test adversarial | Mengubah angka tanpa hitungan |
| **Claude-R** | Review adversarial dengan eksekusi kode, tanpa melihat proses pembuatan | `FINDINGS.md` | Menulis ke repo |

## 3. Alur satu tahap (paling sedikit copy-paste untuk Jansen)
```
Jansen: keputusan/spek (Q:A:)
  → Claude: build + test → STAGEx doc → paket review (1 file per reviewer, ber-hash versi)
  → paralel, timebox 48 jam:
       Claude-R (otomatis) │ Gemini (Jansen upload 1 file) │ ChatGPT (Jansen upload 1 file) │ CI GitHub
  → Jansen kirim balik jawaban Gemini/ChatGPT apa adanya (file .md)
  → Claude: triase CONFIRMED/REJECTED/NEEDS-DATA/DUPLICATE → fix + test → FINDINGS_LOG
  → reviewer cek ulang fix S1 → CI hijau → Jansen merge → gate tahap berikutnya
```
Relay Jansen per tahap: **2 upload keluar, 2 file balik.** Spek/prompt tidak perlu dioper bolak-balik antar AI.

## 4. Serah-terima (wajib di setiap PR/zip — diadopsi dari proposal ChatGPT)
1. Versi dasar (hash) dan tugas yang dikerjakan.
2. File yang berubah + alasan.
3. Test yang **benar-benar dijalankan** + hasilnya (dan di mana dijalankan: sandbox / lokal / CI).
4. Masalah terbuka + pekerjaan berikutnya.

## 5. Temuan & triase
Format: `docs/review/FINDINGS_TEMPLATE.md` (prefix G/C/R/J; severity S1–S4).

| Status | Syarat |
|---|---|
| CONFIRMED | test baru gagal sebelum fix, lulus sesudahnya |
| REJECTED | alasan + bukti |
| NEEDS-DATA | pemilik + langkah verifikasi |
| DUPLICATE | rujuk ID lain |

S1 wajib selesai sebelum tahap berikutnya.

## 6. Keamanan & live trading (satu manusia → pengganti two-person rule)
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

## 9. Keputusan terbuka (pemilik: Jansen)
- Final pembagian peran AI (usulan di atas vs proposal ChatGPT/Gemini).
- Pasangan venue spatial (opsi a/b/c, `STAGE1_CORE.md` §5), setelah data probe latency.
- Mode akun Bitget: UTA atau classic (menentukan WS order entry).
