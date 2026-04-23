"""Test multiple 16B tail hypotheses against real data.

Hypothesis U16:
    bytes [16..18] u16 -> scale_x  in [scale.min[0]  .. scale.max[0]]
    bytes [18..20] u16 -> scale_y  in [scale.min[1]  .. scale.max[1]]
    bytes [20..22] u16 -> scale_z  in [scale.min[2]  .. scale.max[2]]
    bytes [22..24] u16 -> opacity  in [opacity.min[0].. opacity.max[0]]
    bytes [24..32] 8B  -> SH (1..N) or rotation (smallest-three)

We score by how well unquantized values fall inside the attribute range.
"""
from __future__ import annotations
import json, struct, sys
from pathlib import Path

ROOT = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(r"C:\Users\jeongsomin\Desktop\LCC\LCC\lcc-result")
RECORD = 32
N_SAMPLE = 5000


def unquantize_u16(v: int, lo: float, hi: float) -> float:
    return lo + (v / 65535.0) * (hi - lo)


def main():
    lcc_path = next(ROOT.glob("*.lcc"))
    manifest = json.loads(lcc_path.read_text(encoding="utf-8-sig"))
    attrs = {a["name"]: a for a in manifest["attributes"]}
    scale_min, scale_max = attrs["scale"]["min"], attrs["scale"]["max"]
    op_min, op_max = attrs["opacity"]["min"][0], attrs["opacity"]["max"][0]
    sh_min, sh_max = attrs["shcoef"]["min"][0], attrs["shcoef"]["max"][0]

    with (ROOT / "data.bin").open("rb") as f:
        buf = f.read(N_SAMPLE * RECORD)

    print(f"=== tail 16B hypothesis u16×8 on {N_SAMPLE} records ===")
    print(f"scale range: min={scale_min} max={scale_max}")
    print(f"opacity range: [{op_min:.4f}, {op_max:.4f}]")
    print(f"shcoef range: [{sh_min:.2f}, {sh_max:.2f}]")
    print()

    scales = [[] for _ in range(3)]
    opacities = []
    sh = [[] for _ in range(4)]
    for i in range(N_SAMPLE):
        off = i * RECORD
        sx = struct.unpack_from("<H", buf, off + 16)[0]
        sy = struct.unpack_from("<H", buf, off + 18)[0]
        sz = struct.unpack_from("<H", buf, off + 20)[0]
        op = struct.unpack_from("<H", buf, off + 22)[0]
        u4 = struct.unpack_from("<HHHH", buf, off + 24)
        scales[0].append(unquantize_u16(sx, scale_min[0], scale_max[0]))
        scales[1].append(unquantize_u16(sy, scale_min[1], scale_max[1]))
        scales[2].append(unquantize_u16(sz, scale_min[2], scale_max[2]))
        opacities.append(unquantize_u16(op, op_min, op_max))
        for j in range(4):
            sh[j].append(unquantize_u16(u4[j], sh_min, sh_max))

    def stats(label, v, lo, hi):
        in_range = sum(1 for x in v if lo <= x <= hi)
        s = sorted(v)
        med = s[len(s)//2]
        print(f"    {label:12s}: min={min(v):8.4f}  med={med:8.4f}  max={max(v):8.4f}  "
              f"in[{lo:.4f}..{hi:.4f}]={in_range}/{len(v)}")

    print("[unquantized stats]")
    for i, name in enumerate(["scale_x", "scale_y", "scale_z"]):
        stats(name, scales[i], scale_min[i], scale_max[i])
    stats("opacity", opacities, op_min, op_max)
    for i in range(4):
        stats(f"u24..32[{i}]", sh[i], sh_min, sh_max)

    # Also dump first 5 records fully parsed
    print("\n[first 5 records]")
    for i in range(5):
        off = i * RECORD
        x, y, z = struct.unpack_from("<fff", buf, off)
        r, g, b, a = buf[off+12:off+16]
        sx = unquantize_u16(struct.unpack_from("<H", buf, off+16)[0], scale_min[0], scale_max[0])
        sy = unquantize_u16(struct.unpack_from("<H", buf, off+18)[0], scale_min[1], scale_max[1])
        sz = unquantize_u16(struct.unpack_from("<H", buf, off+20)[0], scale_min[2], scale_max[2])
        op = unquantize_u16(struct.unpack_from("<H", buf, off+22)[0], op_min, op_max)
        u4 = struct.unpack_from("<HHHH", buf, off+24)
        print(f"  #{i} pos=({x:7.2f},{y:7.2f},{z:7.2f})  rgba=({r},{g},{b},{a})  "
              f"scale=({sx:.4f},{sy:.4f},{sz:.4f})  opacity={op:.4f}  tail4_u16={u4}")


if __name__ == "__main__":
    main()
