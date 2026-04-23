"""Extract LCC point cloud (LOD N) → binary PLY.

Uses the confirmed 32B record layout:
    bytes [ 0..12] = float32 LE × 3   → position (world coords)
    bytes [12..16] = RGBA8              → color
    bytes [16..32] = (TODO)             → scale/rotation/opacity (skipped for v2)

LOD offsets are cumulative × 32 over manifest.splats[].

Usage:
    python tools_lcc_to_ply.py <lcc-dir> [lod_level] [out_ply]
"""
from __future__ import annotations

import json
import struct
import sys
import time
from pathlib import Path

RECORD = 32
DEFAULT_ROOT = Path(r"C:\Users\jeongsomin\Desktop\LCC\LCC\lcc-result")


def main() -> int:
    root = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_ROOT
    lod = int(sys.argv[2]) if len(sys.argv) > 2 else 0
    lcc = next(root.glob("*.lcc"))
    data = root / "data.bin"

    manifest = json.loads(lcc.read_text(encoding="utf-8-sig"))
    splats_per_lod = manifest["splats"]
    if lod < 0 or lod >= len(splats_per_lod):
        print(f"lod must be in 0..{len(splats_per_lod)-1}")
        return 1

    # Cumulative offset to the start of this LOD
    start_splat = sum(splats_per_lod[:lod])
    n = splats_per_lod[lod]
    byte_start = start_splat * RECORD
    byte_len = n * RECORD

    out_default = root / f"{manifest['name'].replace(' ','_')}_LOD{lod}.ply"
    out = Path(sys.argv[3]) if len(sys.argv) > 3 else out_default

    print(f"manifest : {lcc.name}")
    print(f"lod      : {lod}  ({n:,} splats)")
    print(f"byte read: [{byte_start:,} .. {byte_start+byte_len:,})")
    print(f"writing  : {out}")

    t0 = time.time()
    with data.open("rb") as f:
        f.seek(byte_start)
        raw = f.read(byte_len)
    if len(raw) != byte_len:
        print(f"short read: {len(raw)}/{byte_len}")
        return 2

    # Vectorized with numpy
    try:
        import numpy as np
        recs = np.frombuffer(raw, dtype=np.uint8).reshape(n, RECORD)
        pos = np.frombuffer(recs[:, :12].tobytes(), dtype=np.float32).reshape(n, 3)
        col = recs[:, 12:16]  # RGBA uint8
    except ImportError:
        # Fallback pure-python (slow)
        pos = [struct.unpack_from("<fff", raw, i * RECORD) for i in range(n)]
        col = [raw[i * RECORD + 12 : i * RECORD + 16] for i in range(n)]

    # Write PLY binary_little_endian  with x,y,z + red,green,blue + alpha
    with out.open("wb") as f:
        header = (
            "ply\n"
            "format binary_little_endian 1.0\n"
            f"comment exported from LCC (XGrids PortalCam) LOD {lod}\n"
            f"element vertex {n}\n"
            "property float x\n"
            "property float y\n"
            "property float z\n"
            "property uchar red\n"
            "property uchar green\n"
            "property uchar blue\n"
            "property uchar alpha\n"
            "end_header\n"
        ).encode("ascii")
        f.write(header)
        try:
            import numpy as np
            dt = np.dtype([("pos", np.float32, 3), ("col", np.uint8, 4)])
            out_arr = np.empty(n, dtype=dt)
            out_arr["pos"] = pos
            out_arr["col"] = col
            f.write(out_arr.tobytes())
        except ImportError:
            for p, c in zip(pos, col):
                f.write(struct.pack("<fff", *p))
                f.write(bytes(c))

    print(f"done in {time.time()-t0:.1f}s  →  {out.stat().st_size/1024/1024:.1f} MB")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
