"""Render the heatmap PLY as a simple 2D scatter (top-down + side view) → PNG.

No Unity needed; uses matplotlib for a quick-look visualization so you can
check the LCC↔mesh comparison result at a glance.
"""
import sys
from pathlib import Path

import numpy as np

try:
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
except ImportError:
    print("matplotlib not installed; pip install matplotlib")
    sys.exit(1)

PLY = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(
    r"C:\Users\jeongsomin\Desktop\LCC\LCC\lcc-result\heatmap_LOD2_vs_mesh.ply")
OUT = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(
    r"C:\Users\jeongsomin\Desktop\PointCloudOptimizer_v2\__screenshot_C_heatmap.png")


def read_ply_pos_color(path: Path):
    with path.open("rb") as f:
        hdr = []
        while True:
            line = f.readline().decode(errors="replace")
            hdr.append(line)
            if line.strip() == "end_header":
                break
        n = 0
        for l in hdr:
            if l.startswith("element vertex"):
                n = int(l.split()[-1])
                break
        # layout: x y z (float32) r g b (uchar) — 15 bytes/vertex
        raw = f.read(n * 15)
    dt = np.dtype([("p", "<f4", 3), ("c", "u1", 3)])
    arr = np.frombuffer(raw, dtype=dt, count=n)
    return arr["p"].copy(), arr["c"].copy()


pts, col = read_ply_pos_color(PLY)
print(f"loaded {len(pts):,} points")

fig, axes = plt.subplots(1, 2, figsize=(14, 7), facecolor="#111")
for ax in axes:
    ax.set_facecolor("#111")
    ax.tick_params(colors="#aaa")
    for s in ax.spines.values(): s.set_color("#555")

# Top-down (XZ)
axes[0].scatter(pts[:, 0], pts[:, 2], c=col / 255.0, s=0.5, marker=",", linewidths=0)
axes[0].set_aspect("equal")
axes[0].set_title("LCC vs XGrids proxy mesh — TOP-DOWN (X-Z)  distance heatmap",
                  color="#ddd", fontsize=11)
axes[0].set_xlabel("X (m)", color="#aaa")
axes[0].set_ylabel("Z (m)", color="#aaa")

# Side (XY)
axes[1].scatter(pts[:, 0], pts[:, 1], c=col / 255.0, s=0.5, marker=",", linewidths=0)
axes[1].set_aspect("equal")
axes[1].set_title("SIDE (X-Y)  blue near → red far", color="#ddd", fontsize=11)
axes[1].set_xlabel("X (m)", color="#aaa")
axes[1].set_ylabel("Y (m)", color="#aaa")

plt.tight_layout()
plt.savefig(OUT, dpi=110, facecolor="#111", bbox_inches="tight")
print(f"saved {OUT}  ({OUT.stat().st_size/1024:.0f} KB)")
