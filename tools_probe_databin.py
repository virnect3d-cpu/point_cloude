"""Probe XGrids LCC data.bin — verify 32B record hypothesis.

File size / totalSplats = 32.0 exactly → every splat is a 32-byte record.
Candidate layout to verify:
    [12B float32×3 position]  [4B color RGBA8 or flags]
    [6B float16×3 scale log]  [8B float16×4 rotation]  [2B float16 opacity]
                                                       = 32B total

Usage:
    python tools_probe_databin.py <lcc-dir>
"""
from __future__ import annotations

import json
import os
import struct
import sys
from pathlib import Path

RECORD_SIZE = 32
DEFAULT_ROOT = Path(r"C:\Users\jeongsomin\Desktop\LCC\LCC\lcc-result")


def half_to_float(h: int) -> float:
    """IEEE 754 half → float. Handles subnormals."""
    s = (h >> 15) & 0x1
    e = (h >> 10) & 0x1F
    f = h & 0x3FF
    if e == 0:
        if f == 0:
            return -0.0 if s else 0.0
        # subnormal
        return ((-1) ** s) * (f / 1024.0) * (2 ** -14)
    if e == 31:
        return float("nan") if f else (float("-inf") if s else float("inf"))
    return ((-1) ** s) * (1 + f / 1024.0) * (2 ** (e - 15))


def main() -> int:
    root = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_ROOT
    lcc = next(root.glob("*.lcc"))
    data = root / "data.bin"
    print(f"manifest: {lcc}")
    print(f"data    : {data} ({data.stat().st_size:,} B)")

    manifest = json.loads(lcc.read_text(encoding="utf-8-sig"))
    n_splats = int(manifest["totalSplats"])
    bbox_min = manifest["boundingBox"]["min"]
    bbox_max = manifest["boundingBox"]["max"]
    splats_per_lod = manifest["splats"]
    attrs_pos = next(a for a in manifest["attributes"] if a["name"] == "position")
    pos_attr_min, pos_attr_max = attrs_pos["min"], attrs_pos["max"]

    expected_bytes = n_splats * RECORD_SIZE
    actual_bytes = data.stat().st_size
    print(f"\n[1] record size check")
    print(f"    expected = {n_splats:,} × {RECORD_SIZE} = {expected_bytes:,}")
    print(f"    actual   = {actual_bytes:,}")
    print(f"    diff     = {actual_bytes - expected_bytes:+,}")
    if actual_bytes == expected_bytes:
        print("    → EXACT MATCH. 32B fixed record confirmed.")

    # ── sample first N records and verify positions
    N = 2000
    with data.open("rb") as f:
        buf = f.read(N * RECORD_SIZE)

    positions = []
    tail4 = []    # bytes 12..16
    tail16 = []   # bytes 16..32
    for i in range(N):
        off = i * RECORD_SIZE
        x, y, z = struct.unpack_from("<fff", buf, off)
        positions.append((x, y, z))
        tail4.append(buf[off + 12 : off + 16])
        tail16.append(buf[off + 16 : off + 32])

    xs = [p[0] for p in positions]
    ys = [p[1] for p in positions]
    zs = [p[2] for p in positions]

    in_bbox = sum(
        1 for p in positions
        if bbox_min[0] <= p[0] <= bbox_max[0]
        and bbox_min[1] <= p[1] <= bbox_max[1]
        and bbox_min[2] <= p[2] <= bbox_max[2]
    )
    in_attr = sum(
        1 for p in positions
        if pos_attr_min[0] <= p[0] <= pos_attr_max[0]
        and pos_attr_min[1] <= p[1] <= pos_attr_max[1]
        and pos_attr_min[2] <= p[2] <= pos_attr_max[2]
    )

    print(f"\n[2] first {N} records — position hypothesis (float32 LE @ bytes 0..12)")
    print(f"    x range: [{min(xs):.3f}, {max(xs):.3f}]    manifest bbox x: [{bbox_min[0]:.2f}, {bbox_max[0]:.2f}]")
    print(f"    y range: [{min(ys):.3f}, {max(ys):.3f}]    manifest bbox y: [{bbox_min[1]:.2f}, {bbox_max[1]:.2f}]")
    print(f"    z range: [{min(zs):.3f}, {max(zs):.3f}]    manifest bbox z: [{bbox_min[2]:.2f}, {bbox_max[2]:.2f}]")
    print(f"    inside scene bbox       : {in_bbox}/{N} ({100*in_bbox/N:.1f}%)")
    print(f"    inside attr.position bbox: {in_attr}/{N} ({100*in_attr/N:.1f}%)")

    # ── bytes 12..16 — color hypothesis (RGBA8)?
    print(f"\n[3] bytes [12..16] — color hypothesis (RGBA8)")
    # If RGBA8, distribution of each channel should span 0..255
    rgba = [(b[0], b[1], b[2], b[3]) for b in tail4]
    for i, ch in enumerate("RGBA"):
        vals = [t[i] for t in rgba]
        print(f"    {ch}: min={min(vals):3d} max={max(vals):3d} mean={sum(vals)/len(vals):6.1f}")

    # ── bytes 16..32 — scale(f16×3) + rot(f16×4) + opacity(f16) = 16B
    print(f"\n[4] bytes [16..32] — scale(f16×3) + rot(f16×4) + opacity(f16)")
    scale_xs = []
    scale_ys = []
    scale_zs = []
    rot_norms = []   # |q| should ≈ 1 if quaternion
    opacities = []
    nan_count = 0
    for b in tail16:
        sx = half_to_float(struct.unpack_from("<H", b, 0)[0])
        sy = half_to_float(struct.unpack_from("<H", b, 2)[0])
        sz = half_to_float(struct.unpack_from("<H", b, 4)[0])
        qw = half_to_float(struct.unpack_from("<H", b, 6)[0])
        qx = half_to_float(struct.unpack_from("<H", b, 8)[0])
        qy = half_to_float(struct.unpack_from("<H", b, 10)[0])
        qz = half_to_float(struct.unpack_from("<H", b, 12)[0])
        op = half_to_float(struct.unpack_from("<H", b, 14)[0])
        if any(v != v for v in (sx, sy, sz, qw, qx, qy, qz, op)):
            nan_count += 1
            continue
        scale_xs.append(sx); scale_ys.append(sy); scale_zs.append(sz)
        rot_norms.append((qw*qw + qx*qx + qy*qy + qz*qz) ** 0.5)
        opacities.append(op)

    if scale_xs:
        print(f"    scale_x : [{min(scale_xs):.4f}, {max(scale_xs):.4f}] mean={sum(scale_xs)/len(scale_xs):.4f}")
        print(f"    scale_y : [{min(scale_ys):.4f}, {max(scale_ys):.4f}] mean={sum(scale_ys)/len(scale_ys):.4f}")
        print(f"    scale_z : [{min(scale_zs):.4f}, {max(scale_zs):.4f}] mean={sum(scale_zs)/len(scale_zs):.4f}")
        q_in_unit = sum(1 for n in rot_norms if 0.9 < n < 1.1)
        print(f"    |rot|   : [{min(rot_norms):.4f}, {max(rot_norms):.4f}] mean={sum(rot_norms)/len(rot_norms):.4f} "
              f"  unit-ish({q_in_unit}/{len(rot_norms)})")
        print(f"    opacity : [{min(opacities):.4f}, {max(opacities):.4f}] mean={sum(opacities)/len(opacities):.4f}")
    print(f"    NaN records: {nan_count}/{N}")

    # ── LOD boundaries sanity
    print(f"\n[5] LOD boundaries from manifest.splats (cumulative × 32B):")
    cum = 0
    for lvl, cnt in enumerate(splats_per_lod):
        start = cum * RECORD_SIZE
        end = (cum + cnt) * RECORD_SIZE
        cum += cnt
        print(f"    LOD {lvl}: splats={cnt:>9,}  bytes=[{start:>12,} .. {end:>12,})")
    print(f"    cumulative total = {cum:,} (manifest totalSplats = {n_splats:,})"
          f"  {'MATCH' if cum == n_splats else 'MISMATCH!'}")

    # ── Dump first 10 records as CSV for external inspection
    out_csv = root / "databin_probe_first10.csv"
    with out_csv.open("w", encoding="utf-8", newline="\n") as fp:
        fp.write("idx,px,py,pz,r,g,b,a,sx,sy,sz,qw,qx,qy,qz,opacity\n")
        for i in range(10):
            off = i * RECORD_SIZE
            x, y, z = struct.unpack_from("<fff", buf, off)
            r, g, bl, a = buf[off+12], buf[off+13], buf[off+14], buf[off+15]
            sx = half_to_float(struct.unpack_from("<H", buf, off+16)[0])
            sy = half_to_float(struct.unpack_from("<H", buf, off+18)[0])
            sz = half_to_float(struct.unpack_from("<H", buf, off+20)[0])
            qw = half_to_float(struct.unpack_from("<H", buf, off+22)[0])
            qx = half_to_float(struct.unpack_from("<H", buf, off+24)[0])
            qy = half_to_float(struct.unpack_from("<H", buf, off+26)[0])
            qz = half_to_float(struct.unpack_from("<H", buf, off+28)[0])
            op = half_to_float(struct.unpack_from("<H", buf, off+30)[0])
            fp.write(f"{i},{x:.4f},{y:.4f},{z:.4f},{r},{g},{bl},{a},"
                     f"{sx:.4f},{sy:.4f},{sz:.4f},{qw:.4f},{qx:.4f},{qy:.4f},{qz:.4f},{op:.4f}\n")
    print(f"\n[6] first 10 records dumped to {out_csv}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
