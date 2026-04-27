"""LCC-aware endpoints for v2 backend.

Add-on module — v1 에서 포크된 app.py 에 include_router 로 붙입니다.
"""
from __future__ import annotations

import time
from pathlib import Path
from typing import List, Optional

from fastapi import APIRouter, HTTPException
from pydantic import BaseModel

from backend.core import lcc_loader, lcc_compare

router = APIRouter(prefix="/api/lcc", tags=["lcc"])


class LccInfoRequest(BaseModel):
    directory: str


class LccInfoResponse(BaseModel):
    name: str
    total_splats: int
    splats_per_lod: list[int]
    bbox_min: list[float]
    bbox_max: list[float]
    has_data_bin: bool
    data_bin_size: Optional[int] = None


@router.post("/info", response_model=LccInfoResponse)
def info(req: LccInfoRequest):
    d = Path(req.directory)
    if not d.exists() or not d.is_dir():
        raise HTTPException(404, f"LCC directory not found: {req.directory}")
    try:
        m = lcc_loader.read_manifest(d)
    except StopIteration:
        raise HTTPException(404, "no .lcc manifest in directory")
    except Exception as e:
        raise HTTPException(422, f"manifest parse failed: {e}")
    data_bin = d / "data.bin"
    return LccInfoResponse(
        name=m.name,
        total_splats=m.total_splats,
        splats_per_lod=m.splats_per_lod,
        bbox_min=m.bbox_min,
        bbox_max=m.bbox_max,
        has_data_bin=data_bin.exists(),
        data_bin_size=data_bin.stat().st_size if data_bin.exists() else None,
    )


class LccDecodeRequest(BaseModel):
    directory: str
    lod: int = 4


class LccDecodeResponse(BaseModel):
    point_count: int
    bbox_min: list[float]
    bbox_max: list[float]
    color_mean_rgba: list[float]
    scale_median: float
    opacity_median: float
    elapsed_sec: float


@router.post("/decode", response_model=LccDecodeResponse)
def decode(req: LccDecodeRequest):
    d = Path(req.directory)
    if not d.exists():
        raise HTTPException(404, f"LCC directory not found: {req.directory}")
    try:
        t0 = time.time()
        pos, col, scale, opacity = lcc_loader.decode_lod(d, req.lod)
        elapsed = time.time() - t0
        import numpy as np
        return LccDecodeResponse(
            point_count=int(pos.shape[0]),
            bbox_min=[float(pos[:, i].min()) for i in range(3)],
            bbox_max=[float(pos[:, i].max()) for i in range(3)],
            color_mean_rgba=[float(col[:, i].mean()) for i in range(4)],
            scale_median=float(np.median(scale)) if scale is not None else 0.0,
            opacity_median=float(np.median(opacity)) if opacity is not None else 0.0,
            elapsed_sec=elapsed,
        )
    except Exception as e:
        raise HTTPException(422, f"decode failed: {e}")


class LccCompareRequest(BaseModel):
    lcc_directory: str
    reference_ply: str
    lod: int = 2
    sample: int = 200_000


class LccCompareResponse(BaseModel):
    n_lcc: int
    n_ref: int
    chamfer_symmetric: float
    hausdorff: float
    rms: float
    p50: float
    p90: float
    p99: float
    elapsed_sec: float
    # Hausdorff 시각화용 — A→B 거리 히스토그램 (Editor/웹 UI 차트)
    hist_bins:   Optional[List[float]] = None    # bin edges (길이 = bins+1)
    hist_counts: Optional[List[int]]   = None    # count per bin (길이 = bins)
    mean:        Optional[float]       = None    # 평균 거리


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
        # assume x,y,z float32 first (may also have rgb/rgba — we skip)
        remaining = f.read()
    arr = np.frombuffer(remaining[: n * 12], dtype=np.float32).reshape(n, 3)
    return arr.copy()


@router.post("/compare", response_model=LccCompareResponse)
def compare(req: LccCompareRequest):
    d = Path(req.lcc_directory)
    ref = Path(req.reference_ply)
    if not d.exists(): raise HTTPException(404, f"LCC dir not found: {d}")
    if not ref.exists(): raise HTTPException(404, f"reference PLY not found: {ref}")
    try:
        lcc_pos, _, _, _ = lcc_loader.decode_lod(d, req.lod, with_scale=False, with_opacity=False)
        ref_v = _read_ply_vertices(ref)
        r = lcc_compare.compare_pointclouds(lcc_pos, ref_v,
                                             sample_a=req.sample, sample_b=req.sample,
                                             keep_per_point=True)
        # 거리 히스토그램 (24 bin, 0..p99 범위 — 꼬리 외곽치 표시 영향 줄임)
        hist_bins  = None
        hist_count = None
        mean_d     = None
        if r.distances_a is not None and len(r.distances_a) > 0:
            import numpy as _np
            d = _np.asarray(r.distances_a, dtype=_np.float32)
            top = float(r.percentiles_ab.get("p99", float(d.max())))
            top = max(top, 1e-6)
            counts, edges = _np.histogram(d, bins=24, range=(0.0, top))
            hist_bins  = [float(x) for x in edges.tolist()]
            hist_count = [int(x)   for x in counts.tolist()]
            mean_d     = float(d.mean())

        return LccCompareResponse(
            n_lcc=r.n_a, n_ref=r.n_b,
            chamfer_symmetric=r.chamfer,
            hausdorff=r.hausdorff,
            rms=r.rms_ab,
            p50=r.percentiles_ab["p50"],
            p90=r.percentiles_ab["p90"],
            p99=r.percentiles_ab["p99"],
            elapsed_sec=r.elapsed_sec,
            hist_bins=hist_bins,
            hist_counts=hist_count,
            mean=mean_d,
        )
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(422, f"compare failed: {e}")
