# Regression log — Tahap 1, ronde 1 (patch v0.3)

Aturan (`TEAM_WORKFLOW.md` §5): setiap temuan CONFIRMED mendapat regression test **sebelum** patch.
Test dijalankan pada kode pra-patch → output **MERAH** dicatat. Setelah patch → output **HIJAU** + full suite dicatat.
Satu commit per temuan (test + patch + entri log ini). Status Tahap 1 tetap **REVIEW TERBUKA**.

## Cara reproduksi (siapa pun, termasuk reviewer)

Untuk commit fix `<sha>` dari temuan `R-xx` (lihat tabel di `FINDINGS_LOG.md`):

```bash
git checkout <sha>
git diff <sha>^ <sha> -- src db | git apply -R   # buang patch src/db (termasuk file baru), test baru tetap ada
dotnet test tests/Arb.Core.Tests -c Release --filter "FullyQualifiedName~Rxx_"   # harus MERAH
git checkout -- src db                         # kode SESUDAH patch
dotnet test tests/Arb.Core.Tests -c Release --filter "FullyQualifiedName~Rxx_"   # harus HIJAU
bash db/tests/run_tests.sh             # bila temuan menyentuh SQL
```

"MERAH (compile)" = test memakai API yang belum ada di kode pra-patch. Itu merah yang sah: perilaku yang diminta
memang tidak bisa diekspresikan oleh kode lama. Bila memungkinkan, test juga memuat assert perilaku pada API lama.

Output di bawah berasal dari harness offline Claude (sumber test yang sama, runner shim xUnit karena NuGet diblokir
di sandbox; lihat `STAGE1_CORE.md` §7). CI GitHub menjalankan xUnit asli pada commit yang sama.

---

## R-01 (S1) — margin cross dihitung per posisi, bukan per akun

- **Test**: `tests/Arb.Core.Tests/Regression/R01_CrossAccountMarginTests.cs` (9 test), skenario P11.
- **Angka acuan** (Python `Decimal`, prec 40): 2 short × 3000 USDT, equity 1000, m = 1 % + 5,5 bps →
  jarak akun = (1000 − 63,3) / (6000 × 1,01055) = **1544,868306 bps → zona Warn** (1500 < d ≤ 2500);
  collateral ke target 3500 bps = **1185,455 USDT**; equity 600 → 885,16 bps (Reduce) → ReduceFraction
  **0,7265547131**; equity 450 → 637,77 bps (Critical) → flatten; long+short campuran → 1561,1667 bps.
- **MERAH (compile)** pada kode pra-patch:
  ```
  BUILD FAILED
  tests/Arb.Core.Tests/Regression/R01_CrossAccountMarginTests.cs(19,20): error CS0246: The type or namespace name 'AccountPosition' could not be found
  ```
  (kode lama hanya punya `PerpExposure` tunggal: tidak ada cara mengekspresikan equity bersama.)
- **Patch**: `Risk/CrossAccountMargin.cs` (baru: `AccountPosition`, `AccountMarginEstimate`, surplus akun di bawah
  guncangan adverse seragam, bisection deterministik tanpa alokasi), `MarginGuard.EvaluateAccount` + doc: API per posisi
  hanya sah untuk isolated / akun cross dengan satu posisi. Zero-alloc test ditambah.
- **HIJAU**:
  ```
  R01_CrossAccountMarginTests             9 passed
  TOTAL: 9 passed, 0 failed
  full suite: TOTAL: 159 passed, 0 failed
  ```
