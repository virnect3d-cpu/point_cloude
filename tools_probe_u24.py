"""Deep probe of bytes [24..26] — the mystery u16 near 0.88 median.

Hypotheses to test:
  H1) SH-DC brightness (= (r+g+b)/3 normalized)
  H2) Max(r,g,b) normalized (= brightness peak)
  H3) Min(r,g,b) normalized
  H4) Opacity squared / sqrt (precision re-encoding)
  H5) Average(scale_x,scale_y,scale_z) normalized
  H6) Max(scale) / scale_attr_max (precision hint)
  H7) Purely uncorrelated random (reserved)

Run:
    PYTHONIOENCODING=utf-8 python tools_probe_u24.py [lod]
"""
from __future__ import annotations
import json, struct, sys, math
from pathlib import Path

ROOT = Path(r"C:\Users\jeongsomin\Desktop\LCC\LCC\lcc-result")
RECORD = 32
N_SAMPLE = 50_000


def corr(a, b):
    n = len(a)
    if n == 0: return 0.0
    ma = sum(a) / n
    mb = sum(b) / n
    num = sum((a[i] - ma) * (b[i] - mb) for i in range(n))
    da = math.sqrt(sum((a[i] - ma) ** 2 for i in range(n)))
    db = math.sqrt(sum((b[i] - mb) ** 2 for i in range(n)))
    if da == 0 or db == 0: return 0.0
    return num / (da * db)


def main():
    lod = int(sys.argv[1]) if len(sys.argv) > 1 else 0
    manifest = json.loads(next(ROOT.glob("*.lcc")).read_text(encoding="utf-8-sig"))
    attrs = {a["name"]: a for a in manifest["attributes"]}
    smin, smax = attrs["scale"]["min"], attrs["scale"]["max"]
    omin, omax = attrs["opacity"]["min"][0], attrs["opacity"]["max"][0]

    # byte range for target LOD
    splats = manifest["splats"]
    byte_start = sum(splats[:lod]) * RECORD
    n = min(N_SAMPLE, splats[lod])
    with (ROOT / "data.bin").open("rb") as f:
        f.seek(byte_start)
        buf = f.read(n * RECORD)

    u24_vals = []
    r_vals, g_vals, b_vals, a_vals = [], [], [], []
    opacity_vals = []
    scale_avg_vals = []
    scale_max_vals = []
    brightness_vals = []

    for i in range(n):
        off = i * RECORD
        r = buf[off+12]; g = buf[off+13]; b = buf[off+14]; a = buf[off+15]
        sx = struct.unpack_from("<H", buf, off+16)[0]
        sy = struct.unpack_from("<H", buf, off+18)[0]
        sz = struct.unpack_from("<H", buf, off+20)[0]
        op = struct.unpack_from("<H", buf, off+22)[0]
        u24 = struct.unpack_from("<H", buf, off+24)[0]
        u24_vals.append(u24 / 65535.0)
        r_vals.append(r); g_vals.append(g); b_vals.append(b); a_vals.append(a)
        opacity_vals.append(op/65535*(omax-omin)+omin)
        sxf = sx/65535*(smax[0]-smin[0])+smin[0]
        syf = sy/65535*(smax[1]-smin[1])+smin[1]
        szf = sz/65535*(smax[2]-smin[2])+smin[2]
        scale_avg_vals.append((sxf + syf + szf) / 3)
        scale_max_vals.append(max(sxf, syf, szf))
        brightness_vals.append((r + g + b) / 3.0 / 255.0)

    print(f"=== u24..u26 probe · LOD {lod} · {n:,} samples ===")
    print(f"u24 mean={sum(u24_vals)/n:.4f}  median={sorted(u24_vals)[n//2]:.4f}  "
          f"stdev≈{math.sqrt(sum((v-sum(u24_vals)/n)**2 for v in u24_vals)/n):.4f}")
    print()
    print("Pearson r (u24 vs hypothesis):")
    print(f"  H1  brightness (R+G+B)/3     : {corr(u24_vals, brightness_vals):+.4f}")
    print(f"  H1b just alpha channel        : {corr(u24_vals, [x/255 for x in a_vals]):+.4f}")
    print(f"  H2  max(R,G,B)                : {corr(u24_vals, [max(r,g,b)/255 for r,g,b in zip(r_vals,g_vals,b_vals)]):+.4f}")
    print(f"  H3  min(R,G,B)                : {corr(u24_vals, [min(r,g,b)/255 for r,g,b in zip(r_vals,g_vals,b_vals)]):+.4f}")
    print(f"  H4  opacity                   : {corr(u24_vals, opacity_vals):+.4f}")
    print(f"  H5  scale_avg                 : {corr(u24_vals, scale_avg_vals):+.4f}")
    print(f"  H6  scale_max                 : {corr(u24_vals, scale_max_vals):+.4f}")

    # Histogram of u24 (10 bins)
    print("\nu24 histogram (10 bins):")
    bins = [0]*10
    for v in u24_vals: bins[min(9, int(v*10))] += 1
    for i, c in enumerate(bins):
        bar = '█' * int(40 * c / max(bins))
        print(f"  {i*0.1:.1f}-{(i+1)*0.1:.1f}: {c:>5}  {bar}")


if __name__ == "__main__":
    main()
