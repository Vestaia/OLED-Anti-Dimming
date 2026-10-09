# SPDX-License-Identifier: GPL-3.0-only
"""Remove only PE/export linker timestamps from the built safety bridge."""
from pathlib import Path
import struct

path = Path(__file__).resolve().parents[1] / "build/oled-hook-bridge.dll"
image = bytearray(path.read_bytes())
pe = struct.unpack_from("<I", image, 60)[0]
assert image[pe:pe + 4] == b"PE\0\0"
assert struct.unpack_from("<H", image, pe + 24)[0] == 0x20b
image[pe + 8:pe + 12] = bytes(4)
def map_rva(rva):
    sections = struct.unpack_from("<H", image, pe + 6)[0]
    section = pe + 24 + struct.unpack_from("<H", image, pe + 20)[0]
    for i in range(sections):
        start = section + 40 * i
        va, size, raw = struct.unpack_from("<III", image, start + 12)
        if va <= rva < va + size:
            return raw + rva - va
    raise ValueError("Missing bridge metadata directory")

export = map_rva(struct.unpack_from("<I", image, pe + 136)[0])
image[export + 4:export + 8] = bytes(4)
debug_rva, debug_size = struct.unpack_from("<II", image, pe + 136 + 6 * 8)
if debug_rva:
    debug = map_rva(debug_rva)
    for i in range(debug_size // 28):
        offset = debug + 28 * i + 4
        image[offset:offset + 4] = bytes(4)
path.write_bytes(image)
