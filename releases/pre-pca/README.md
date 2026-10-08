# Pre-PCA rollback release

OledCalibration.exe is an unchanged copy of build/single-fast-measurements,
the last packaged version before histogram PCA. SHA256.txt records its hash.
Launch this executable and select the original moment-model calibration JSON
when rolling back. Do not select a PCA model: that version cannot interpret it correctly. New PCA models use JSON version 2
so its Apply command rejects them.
Existing report directories and models have not been migrated or overwritten.
The old executable includes its original native hook and helpers.

This preserves the actual pre-PCA executable, not a reconstructed source tree.
The original moment-based algorithm remains available in current source. The
first PCA source tree is separately archived in ../pca-first/source.zip.
