# SPDX-License-Identifier: GPL-3.0-only
"""Prepare standalone cost workloads; no change to the application's encoder.

Usage: python tools/prepare_histogram_benchmark.py MODEL.json [OUTPUT_DIRECTORY]
Then compile tests/native/histogram_benchmark.cpp with OUTPUT_DIRECTORY as its
include directory, and link d3d11.lib, d3dcompiler.lib and dxgi.lib.
"""
import json
import math
from pathlib import Path
import random
import struct
import sys

root = Path(__file__).resolve().parents[1]
model_path = Path(sys.argv[1]).resolve()
output = Path(sys.argv[2] if len(sys.argv) > 2 else root / "build/histogram-comparison")
output.mkdir(parents=True, exist_ok=True)
model = json.loads(model_path.read_text(encoding="utf-8-sig"))
if model["features"] != 14:
    raise ValueError("Benchmark requires the current 14-component model")

header = (root / "native/adaptive_filter.hpp").read_text(encoding="utf-8")
header = header.replace("magic != 0x344c5041)", "magic != 0x344c5041 && magic != 0x354c5041)")
header = header.replace(
    "config.histogramBins = magic == 0x344c5041 ? 768 : 512;",
    "config.histogramBins = magic == 0x354c5041 ? 5120 : magic == 0x344c5041 ? 768 : 512;",
)
(output / "benchmark_filter.hpp").write_text(header, encoding="utf-8")
header = header.replace("groups * 16 * sizeof(float)", "groups * config.histogramBins * sizeof(float)")
(output / "benchmark_filter.hpp").write_text(header, encoding="utf-8")
shader = (root / "tests/native/reference_histogram.hlsl").read_text(encoding="utf-8")
high = shader.replace("histogram[768]", "histogram[5120]").replace("histogramBins==768", "histogramBins==5120")
high = high.replace(")*7.", ")*15.").replace("0,7);", "0,15);").replace("uint3(x),6", "uint3(x),14")
high = high.replace("b.x*64+b.y*8+b.z", "b.x*256+b.y*16+b.z")
high = high.replace("*255.", "*1023.").replace("0,255);", "0,1023);").replace("uint(brightness),254", "uint(brightness),1022")
high = high.replace("histogram[512+low]", "histogram[4096+low]").replace("histogram[513+low]", "histogram[4097+low]").replace("b==512", "b==4096")

for name, source, offset, bins, magic in [
    ("current", shader, 512, 768, 0x344C5041),
    ("high", high, 4096, 5120, 0x354C5041),
]:
    (output / (name + ".hlsl")).write_text(source, encoding="utf-8")
    fused = source.replace("sums[256*4]", "sums[128*4]")
    start = fused.index(" for(uint k=lane;k<histogramBins/4;")
    end = fused.index("\n}\n[numthreads(128", start)
    fused = fused[:start] + """ if(lane<128) {
  float4 projected[4];for(uint j=0;j<4;j++)projected[j]=0;
  for(uint b=lane;b<histogramBins;b+=128) {
   float mass=histogram[b]/(float(gridWidth)*gridHeight*4096.);
   for(uint j=0;j<4;j++)projected[j]+=mass*Basis[b*4+j];
  }
  for(uint j=0;j<4;j++)sums[lane*4+j]=projected[j];
 }
 GroupMemoryBarrierWithGroupSync();
 for(uint step=64;step>0;step/=2){if(lane<step)for(uint j=0;j<4;j++)sums[lane*4+j]+=sums[(lane+step)*4+j];GroupMemoryBarrierWithGroupSync();}
 if(lane==0)for(uint j=0;j<4;j++)Partial[(group.y*groupsX+group.x)*4+j]=sums[j];
""" + fused[end:]
    start = fused.index(" for(uint b=lane;b<histogramBins;", fused.index("void HistogramInfer"))
    end = fused.index(" for(uint j=0;j<4;j++)sums[lane*4+j]", start)
    fused = fused[:start] + """ for(uint g=lane;g<groupsX*groupsY;g+=128)
  for(uint j=0;j<4;j++)projected[j]+=Partial[g*4+j];
""" + fused[end:]
    fused = fused.replace(
        "if(lane==0)for(uint j=0;j<4;j++)encoded[j]=sums[j];",
        f"if(lane==0)for(uint j=0;j<4;j++)encoded[j]=sums[j]-.5*(Basis[j]+Basis[{offset}*4+j]);",
    )
    (output / (name + "-fused.hlsl")).write_text(fused, encoding="utf-8")
    directory = output / name
    directory.mkdir(exist_ok=True)
    rng = random.Random(2084)
    # Histogram dimensions and center count determine loop/traffic cost. Dense
    # synthetic bases avoid requiring new physical calibration or a huge PCA fit.
    # These binaries are cost workloads, not usable calibration models.
    basis = [[rng.gauss(0, 1 / math.sqrt(bins)) for _ in range(bins)] for _ in range(14)]
    if bins == 768 and len(model["histogram_pca"]["Basis"][0]) == 768:
        basis = model["histogram_pca"]["Basis"]
    with (directory / "runtime.bin").open("wb") as binary:
        binary.write(struct.pack("<IIfff14f", magic, len(model["centers"]), 250, 0, .3, *model["scale"]))
        for center, gain in zip(model["centers"], model["coefficients"]):
            binary.write(struct.pack("<16f", *center, gain, 0))
        for bin_index in range(bins):
            binary.write(struct.pack("<16f", *[row[bin_index] for row in basis], 0, 0))

(output / "setup.json").write_text(json.dumps({
    "source_model": str(model_path), "centers": len(model["centers"]), "components": 14,
    "variants": ["8x8x8+256", "16x16x16+1024"],
    "basis_note": "Synthetic dense bases unless the supplied model already has 768 input entries; cost comparison only.",
}, indent=2), encoding="utf-8")
print(f"Prepared {output}: 14 components, {len(model['centers'])} centers")
