"""Verify tail layout uniformity across all 5 LODs by sampling records from each."""
from __future__ import annotations
import json, struct, sys
from pathlib import Path

ROOT = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(r"C:\Users\jeongsomin\Desktop\LCC\LCC\lcc-result")
RECORD = 32
N_PER_LOD = 2000


def main():
    lcc_path = next(ROOT.glob("*.lcc"))
    manifest = json.loads(lcc_path.read_text(encoding="utf-8-sig"))
    splats = manifest["splats"]
    attrs = {a["name"]: a for a in manifest["attributes"]}
    smin, smax = attrs["scale"]["min"], attrs["scale"]["max"]
    omin, omax = attrs["opacity"]["min"][0], attrs["opacity"]["max"][0]

    data_path = ROOT / "data.bin"
    cum = 0
    print(f"{'LOD':>4} {'splats':>10} {'scale_med':>10} {'op_med':>8} {'u24_med':>8} {'tail_zero%':>10}")
    for lod, n in enumerate(splats):
        byte_start = cum * RECORD
        cum += n
        sample_n = min(N_PER_LOD, n)
        with data_path.open("rb") as f:
            f.seek(byte_start)
            buf = f.read(sample_n * RECORD)

        scales_any, ops, u24s, tail_all_zero = [], [], [], 0
        for i in range(sample_n):
            off = i * RECORD
            sx, sy, sz, op, u24, u26, u28, u30 = struct.unpack_from("<HHHHHHHH", buf, off + 16)
            scales_any.append(sx / 65535.0 * (smax[0] - smin[0]) + smin[0])
            ops.append(op / 65535.0 * (omax - omin) + omin)
            u24s.append(u24 / 65535.0)
            if u26 == 0 and u28 == 0 and u30 == 0:
                tail_all_zero += 1

        scales_any.sort(); ops.sort(); u24s.sort()
        smed = scales_any[len(scales_any)//2]
        omed = ops[len(ops)//2]
        umed = u24s[len(u24s)//2]
        pct = 100.0 * tail_all_zero / sample_n
        print(f"{lod:>4} {n:>10,} {smed:>10.4f} {omed:>8.4f} {umed:>8.4f} {pct:>9.1f}%")


if __name__ == "__main__":
    main()
