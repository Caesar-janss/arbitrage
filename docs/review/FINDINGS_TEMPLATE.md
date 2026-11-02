# Format temuan (semua reviewer)

Tulis satu blok per temuan, urut dari paling berat. Maksimal ±20 temuan; 0 temuan di suatu area adalah hasil yang sah.

```
- id: C-01
  severity: S1
  area: src/Arb.Core/Pricing/SpatialArbPricer.cs:142
  claim: <satu kalimat: apa yang salah>
  evidence: <counterexample input + output aktual vs seharusnya | hitungan | URL dokumen resmi | kutipan baris>
  proposed_fix: <singkat>
  confidence: high | medium | low
```

| Prefix | Reviewer |
|---|---|
| C | ChatGPT |
| R | Claude-R (Claude independen) |
| J | Jansen |
| B | Claude builder: temuan sendiri saat recheck / self-check (dicatat terpisah agar metrik reviewer tetap jujur) |
| ~~G~~ | ~~Gemini~~ (keluar dari tim 2026-10-27; prefix tidak dipakai lagi) |

| Severity | Arti |
|---|---|
| S1 | Bisa rugi modal, eksposur tanpa hedge, atau trade salah tanpa terdeteksi |
| S2 | Hasil/invariant salah tanpa jalur kerugian langsung |
| S3 | Robustness / operability / observability |
| S4 | Minor (penamaan, komentar) |

Akhiri dengan:
- `## Verified OK` — area penting yang dicek dan benar
- `## Top 3 risks`
- `## Tidak bisa diverifikasi` — hal yang butuh data/dokumen, beserta cara mengeceknya
