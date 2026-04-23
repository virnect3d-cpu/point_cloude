"""End-to-end demo: LCC point cloud vs XGrids proxy mesh vertices.

Steps:
  1. Load LCC LOD N via lcc_loader
  2. Load ShinWon_1st_Cutter.ply (proxy mesh vertices) via simple binary PLY reader
  3. Run chamfer + Hausdorff via lcc_compare
  4. Emit colored PLY heatmap (distances rendered as RGB)

Run:
    PYTHONIOENCODING=utf-8 python tools_lcc_vs_mesh_compare.py [lod]
"""
from __future__ import annotations

import struct
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).parent / "PointCloudOptimizer_v2"))
from backend.core import lcc_loader, lcc_compare  # noqa: E402

LCC_DIR = Path(r"C:\Users\jeongsomin\Desktop\LCC\LCC\lcc-result")
MESH_PLY = Path(r"C:\Users\jeongsomin\Desktop\LCC\LCC\mesh-files\ShinWon_1st_Cutter.ply")


def read_binary_ply_vertices(path: Path) -> np.ndarray:
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
        # The proxy mesh has only x,y,z float32 before faces per hapPLY's simple format
        # read n * 12 bytes
        raw = f.read(n * 12)
    v = np.frombuffer(raw, dtype=np.float32).reshape(n, 3)
    return v.copy()


def write_heatmap_ply(path: Path, positions: np.ndarray, colors_rgb: np.ndarray) -> None:
    n = positions.shape[0]
    hdr = (
        "ply\nformat binary_little_endian 1.0\n"
        f"comment LCC to mesh distance heatmap\n"
        f"element vertex {n}\n"
        "property float x\nproperty float y\nproperty float z\n"
        "property uchar red\nproperty uchar green\nproperty uchar blue\n"
        "end_header\n"
    ).encode("ascii")
    with path.open("wb") as f:
        f.write(hdr)
        dt = np.dtype([("p", "<f4", 3), ("c", "u1", 3)])
        arr = np.empty(n, dtype=dt)
        arr["p"] = positions.astype(np.float32)
        arr["c"] = colors_rgb.astype(np.uint8)
        f.write(arr.tobytes())


def main() -> int:
    lod = int(sys.argv[1]) if len(sys.argv) > 1 else 2
    print(f"[1] Loading LCC LOD {lod} ...")
    pos, rgba, scale, opacity = lcc_loader.decode_lod(LCC_DIR, lod, with_scale=True, with_opacity=True)
    print(f"    positions: {pos.shape}  scale median: {np.median(scale):.4f}  opacity median: {np.median(opacity):.4f}")

    print(f"[2] Loading XGrids proxy mesh vertices from {MESH_PLY.name} ...")
    mesh_v = read_binary_ply_vertices(MESH_PLY)
    print(f"    mesh vertices: {mesh_v.shape}")

    print("[3] chamfer + Hausdorff (sampled 200K each side)...")
    r = lcc_compare.compare_pointclouds(pos, mesh_v,
                                        sample_a=200_000, sample_b=200_000,
                                        keep_per_point=True)
    print(f"    n_a = {r.n_a:,}   n_b = {r.n_b:,}")
    print(f"    chamfer(A→B) = {r.chamfer_ab:.4f} m")
    print(f"    chamfer(B→A) = {r.chamfer_ba:.4f} m")
    print(f"    chamfer symm = {r.chamfer:.4f} m")
    print(f"    Hausdorff    = {r.hausdorff:.4f} m (A→B {r.hausdorff_ab:.3f}  B→A {r.hausdorff_ba:.3f})")
    print(f"    RMS(A→B)     = {r.rms_ab:.4f} m")
    print(f"    p50/p90/p99  = {r.percentiles_ab['p50']:.4f} / {r.percentiles_ab['p90']:.4f} / {r.percentiles_ab['p99']:.4f}")
    print(f"    elapsed      = {r.elapsed_sec:.1f} s")

    # Heatmap for the SAMPLED A subset
    rng = np.random.default_rng(42)
    idx = rng.choice(pos.shape[0], min(200_000, pos.shape[0]), replace=False)
    sampled_pos = pos[idx]
    colors_rgb = lcc_compare.distances_to_rgb(r.distances_a)
    out_path = LCC_DIR / f"heatmap_LOD{lod}_vs_mesh.ply"
    write_heatmap_ply(out_path, sampled_pos, colors_rgb)
    print(f"[4] heatmap PLY written → {out_path}  ({out_path.stat().st_size/1024/1024:.1f} MB)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
