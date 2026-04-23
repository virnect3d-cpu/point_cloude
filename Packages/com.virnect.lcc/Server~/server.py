"""Self-contained LCC backend — ships inside the Unity UPM package.

Launched by LccServerManager (Unity Editor). Exposes 3 endpoints:
  POST /api/lcc/info     → manifest summary
  POST /api/lcc/decode   → LOD stats
  POST /api/lcc/compare  → chamfer/Hausdorff vs reference PLY
  GET  /api/health

Usage (Editor will normally invoke this automatically):
  python server.py --host 127.0.0.1 --port 8001
"""
from __future__ import annotations

import argparse
import sys
import time
from pathlib import Path
from typing import Optional

# Ensure same-dir imports work
HERE = Path(__file__).parent.resolve()
sys.path.insert(0, str(HERE))

from fastapi import FastAPI, HTTPException  # noqa: E402
from fastapi.middleware.cors import CORSMiddleware  # noqa: E402
from pydantic import BaseModel  # noqa: E402

import lcc_loader   # type: ignore  # noqa: E402
import lcc_compare  # type: ignore  # noqa: E402

app = FastAPI(title="LCC Edition (packaged server)", version="2.5.0")
app.add_middleware(
    CORSMiddleware, allow_origins=["*"], allow_methods=["*"], allow_headers=["*"]
)


# ── Health ─────────────────────────────────────────────────────────────────
@app.get("/api/health")
def health():
    import numpy, scipy  # noqa: F401
    return {
        "status": "ok",
        "version": "2.5.0",
        "cwd": str(HERE),
        "python": sys.version.split()[0],
    }


# ── /api/lcc/info ─────────────────────────────────────────────────────────
class InfoReq(BaseModel):
    directory: str

class InfoResp(BaseModel):
    name: str
    total_splats: int
    splats_per_lod: list[int]
    bbox_min: list[float]
    bbox_max: list[float]
    has_data_bin: bool
    data_bin_size: Optional[int] = None

@app.post("/api/lcc/info", response_model=InfoResp)
def info(req: InfoReq):
    d = Path(req.directory)
    if not d.exists():
        raise HTTPException(404, f"dir not found: {d}")
    try:
        m = lcc_loader.read_manifest(d)
    except StopIteration:
        raise HTTPException(404, "no .lcc in directory")
    except Exception as e:
        raise HTTPException(422, f"manifest parse: {e}")
    data_bin = d / "data.bin"
    return InfoResp(
        name=m.name, total_splats=m.total_splats,
        splats_per_lod=m.splats_per_lod,
        bbox_min=m.bbox_min, bbox_max=m.bbox_max,
        has_data_bin=data_bin.exists(),
        data_bin_size=data_bin.stat().st_size if data_bin.exists() else None,
    )


# ── /api/lcc/decode ───────────────────────────────────────────────────────
class DecodeReq(BaseModel):
    directory: str
    lod: int = 4

class DecodeResp(BaseModel):
    point_count: int
    bbox_min: list[float]
    bbox_max: list[float]
    color_mean_rgba: list[float]
    scale_median: float
    opacity_median: float
    elapsed_sec: float

@app.post("/api/lcc/decode", response_model=DecodeResp)
def decode(req: DecodeReq):
    d = Path(req.directory)
    if not d.exists():
        raise HTTPException(404, f"dir not found: {d}")
    try:
        import numpy as np
        t0 = time.time()
        pos, col, scale, opacity = lcc_loader.decode_lod(d, req.lod)
        return DecodeResp(
            point_count=int(pos.shape[0]),
            bbox_min=[float(pos[:, i].min()) for i in range(3)],
            bbox_max=[float(pos[:, i].max()) for i in range(3)],
            color_mean_rgba=[float(col[:, i].mean()) for i in range(4)],
            scale_median=float(np.median(scale)) if scale is not None else 0.0,
            opacity_median=float(np.median(opacity)) if opacity is not None else 0.0,
            elapsed_sec=time.time() - t0,
        )
    except Exception as e:
        raise HTTPException(422, f"decode failed: {e}")


# ── /api/lcc/compare ──────────────────────────────────────────────────────
class CompareReq(BaseModel):
    lcc_directory: str
    reference_ply: str
    lod: int = 2
    sample: int = 200_000

class CompareResp(BaseModel):
    n_lcc: int
    n_ref: int
    chamfer_symmetric: float
    hausdorff: float
    rms: float
    p50: float
    p90: float
    p99: float
    elapsed_sec: float

def _read_ply_vertices(path: Path):
    import numpy as np
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
        remaining = f.read()
    arr = np.frombuffer(remaining[: n * 12], dtype=np.float32).reshape(n, 3)
    return arr.copy()

@app.post("/api/lcc/compare", response_model=CompareResp)
def compare(req: CompareReq):
    d = Path(req.lcc_directory); ref = Path(req.reference_ply)
    if not d.exists(): raise HTTPException(404, f"LCC dir: {d}")
    if not ref.exists(): raise HTTPException(404, f"ref PLY: {ref}")
    try:
        lcc_pos, _, _, _ = lcc_loader.decode_lod(d, req.lod,
                                                 with_scale=False, with_opacity=False)
        ref_v = _read_ply_vertices(ref)
        r = lcc_compare.compare_pointclouds(lcc_pos, ref_v,
                                            sample_a=req.sample, sample_b=req.sample)
        return CompareResp(
            n_lcc=r.n_a, n_ref=r.n_b,
            chamfer_symmetric=r.chamfer, hausdorff=r.hausdorff, rms=r.rms_ab,
            p50=r.percentiles_ab["p50"], p90=r.percentiles_ab["p90"], p99=r.percentiles_ab["p99"],
            elapsed_sec=r.elapsed_sec,
        )
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(422, f"compare failed: {e}")


# ── Entry point ───────────────────────────────────────────────────────────
def main():
    p = argparse.ArgumentParser()
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, default=8001)
    args = p.parse_args()

    import uvicorn
    print(f"[LCC] serving on http://{args.host}:{args.port}")
    uvicorn.run(app, host=args.host, port=args.port, log_level="warning")


if __name__ == "__main__":
    main()
