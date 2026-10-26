## Apa yang berubah
<!-- 1–3 kalimat. Tahap/modul. -->

## Kenapa
<!-- keputusan (link Q:A:), temuan review (ID di docs/review/FINDINGS_LOG.md), atau spek -->

## Bukti
- [ ] CI hijau (build warnings-as-errors, `dotnet test`, skema DB)
- [ ] Test baru untuk setiap perilaku baru / temuan CONFIRMED (sebutkan nama test)
- [ ] Hot path: tetap 0 B/op (`ZeroAllocationTests` lulus; benchmark jika menyentuh hot path)
- [ ] Docs diperbarui jika kontrak/perilaku berubah (`docs/STAGE*.md`, SQL ↔ C#)

## Risiko modal
<!-- Apakah perubahan menyentuh sizing, harga limit, repair, margin, inventory, atau keputusan live? Jelaskan mitigasi. -->

## Keamanan
- [ ] Tidak ada API key/secret di diff, log, atau test data
- [ ] Tidak mengubah `IsPaperTrading` / konfigurasi live. Jika ya: checklist `TEAM_WORKFLOW.md` §6 (CI hijau + cooling-off 24 jam + batas pilot divalidasi di config)
