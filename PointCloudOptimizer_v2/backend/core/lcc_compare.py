"""LCC ↔ reference-scan point-cloud comparison utilities.

v2 목표 — "LCC 포인트 vs 원본 3D 스캔" 정합·오차 분석:
    1. chamfer distance (양방향 최근접 평균)
    2. per-point Hausdorff (one-sided) → 점별 색상 히트맵
    3. RMS + percentile 통계

모두 numpy + scipy.spatial.cKDTree 로 단독 구현 — Open3D 의존 없음.
"""
from __future__ import annotations

import time
from dataclasses import dataclass
from typing import Optional

import numpy as np
from scipy.spatial import cKDTree


@dataclass
class CompareResult:
    n_a: int
    n_b: int
    chamfer_ab: float    # mean distance A→B
    chamfer_ba: float    # mean distance B→A
    chamfer: float       # (chamfer_ab + chamfer_ba) * 0.5
    hausdorff_ab: float  # max A→B (one-sided)
    hausdorff_ba: float  # max B→A (one-sided)
    hausdorff: float     # symmetric Hausdorff
    rms_ab: float
    percentiles_ab: dict[str, float]   # {"p50":..., "p90":..., "p99":...}
    elapsed_sec: float
    # Per-point heatmap on A (signed NN distance to B). None unless keep_per_point=True.
    distances_a: Optional[np.ndarray] = None


def compare_pointclouds(
    a: np.ndarray,
    b: np.ndarray,
    *,
    sample_a: int = 200_000,
    sample_b: int = 200_000,
    keep_per_point: bool = False,
    random_state: int = 42,
) -> CompareResult:
    """
    Parameters
    ----------
    a, b          : (N, 3) float32/float64 arrays.
    sample_a/b    : stochastic subsample sizes (for memory + speed).  0 = use all.
    keep_per_point: attach per-point A→B distances for heatmap rendering.

    Returns
    -------
    CompareResult
    """
    t0 = time.time()
    rng = np.random.default_rng(random_state)
    a = np.asarray(a, dtype=np.float64).reshape(-1, 3)
    b = np.asarray(b, dtype=np.float64).reshape(-1, 3)
    if a.shape[0] == 0 or b.shape[0] == 0:
        raise ValueError("empty input")

    if sample_a and a.shape[0] > sample_a:
        idx = rng.choice(a.shape[0], sample_a, replace=False)
        a_s = a[idx]
    else:
        a_s = a
    if sample_b and b.shape[0] > sample_b:
        idx = rng.choice(b.shape[0], sample_b, replace=False)
        b_s = b[idx]
    else:
        b_s = b

    tree_b = cKDTree(b_s)
    tree_a = cKDTree(a_s)
    d_ab, _ = tree_b.query(a_s, k=1)
    d_ba, _ = tree_a.query(b_s, k=1)

    ch_ab = float(np.mean(d_ab))
    ch_ba = float(np.mean(d_ba))
    ha_ab = float(np.max(d_ab))
    ha_ba = float(np.max(d_ba))
    rms_ab = float(np.sqrt(np.mean(d_ab * d_ab)))

    pcts = {
        "p50": float(np.percentile(d_ab, 50)),
        "p90": float(np.percentile(d_ab, 90)),
        "p99": float(np.percentile(d_ab, 99)),
    }

    return CompareResult(
        n_a=int(a.shape[0]),
        n_b=int(b.shape[0]),
        chamfer_ab=ch_ab,
        chamfer_ba=ch_ba,
        chamfer=(ch_ab + ch_ba) * 0.5,
        hausdorff_ab=ha_ab,
        hausdorff_ba=ha_ba,
        hausdorff=max(ha_ab, ha_ba),
        rms_ab=rms_ab,
        percentiles_ab=pcts,
        elapsed_sec=time.time() - t0,
        distances_a=d_ab if keep_per_point else None,
    )


def distances_to_rgb(distances: np.ndarray, dmax: Optional[float] = None) -> np.ndarray:
    """
    Map per-point scalar distances → RGB heatmap (blue → green → yellow → red).
    Returns (N, 3) uint8.
    """
    d = np.asarray(distances, dtype=np.float32).reshape(-1)
    if dmax is None:
        dmax = float(np.percentile(d, 99)) or float(d.max()) or 1.0
    t = np.clip(d / max(dmax, 1e-6), 0.0, 1.0)
    # 4-stop gradient
    # 0.00 blue  (0,0,255)
    # 0.33 green (0,255,0)
    # 0.66 yellow(255,255,0)
    # 1.00 red   (255,0,0)
    stops = np.array([
        [0.00, 0, 0, 255],
        [0.33, 0, 255, 0],
        [0.66, 255, 255, 0],
        [1.00, 255, 0, 0],
    ], dtype=np.float32)
    r = np.interp(t, stops[:, 0], stops[:, 1])
    g = np.interp(t, stops[:, 0], stops[:, 2])
    b = np.interp(t, stops[:, 0], stops[:, 3])
    return np.stack([r, g, b], axis=1).astype(np.uint8)
