"""Python-side LCC reader (mirrors Unity C# LccSplatDecoder).

Usage:
    manifest = read_manifest(dir_path)
    pts, rgba, scale, opacity = decode_lod(dir_path, lod)
"""
from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Tuple

import numpy as np

RECORD = 32


@dataclass
class LccManifest:
    name: str
    total_splats: int
    splats_per_lod: list[int]
    bbox_min: list[float]
    bbox_max: list[float]
    scale_min: list[float]
    scale_max: list[float]
    opacity_min: float
    opacity_max: float
    raw: dict


def read_manifest(lcc_dir: Path) -> LccManifest:
    lcc_dir = Path(lcc_dir)
    lcc_path = next(lcc_dir.glob("*.lcc"))
    raw = json.loads(lcc_path.read_text(encoding="utf-8-sig"))
    attrs = {a["name"]: a for a in raw["attributes"]}
    return LccManifest(
        name=raw["name"],
        total_splats=int(raw["totalSplats"]),
        splats_per_lod=list(raw["splats"]),
        bbox_min=list(raw["boundingBox"]["min"]),
        bbox_max=list(raw["boundingBox"]["max"]),
        scale_min=list(attrs["scale"]["min"]),
        scale_max=list(attrs["scale"]["max"]),
        opacity_min=float(attrs["opacity"]["min"][0]),
        opacity_max=float(attrs["opacity"]["max"][0]),
        raw=raw,
    )


def lod_byte_range(manifest: LccManifest, lod: int) -> Tuple[int, int, int]:
    n = manifest.splats_per_lod[lod]
    start = sum(manifest.splats_per_lod[:lod]) * RECORD
    length = n * RECORD
    return start, length, n


def decode_lod(
    lcc_dir: Path,
    lod: int = 0,
    *,
    with_scale: bool = True,
    with_opacity: bool = True,
) -> Tuple[np.ndarray, np.ndarray, np.ndarray | None, np.ndarray | None]:
    """
    Returns:
        positions : (N, 3) float32
        colors    : (N, 4) uint8 RGBA
        scale     : (N, 3) float32 or None (if with_scale=False)
        opacity   : (N,)   float32 or None (if with_opacity=False)
    """
    lcc_dir = Path(lcc_dir)
    m = read_manifest(lcc_dir)
    start, length, n = lod_byte_range(m, lod)

    with (lcc_dir / "data.bin").open("rb") as f:
        f.seek(start)
        raw = f.read(length)

    recs = np.frombuffer(raw, dtype=np.uint8).reshape(n, RECORD)
    positions = np.frombuffer(recs[:, :12].tobytes(), dtype=np.float32).reshape(n, 3)
    colors = recs[:, 12:16].copy()  # RGBA8

    scale = None
    if with_scale:
        u16s = np.frombuffer(recs[:, 16:22].tobytes(), dtype=np.uint16).reshape(n, 3)
        sRng = np.array([
            m.scale_max[i] - m.scale_min[i] for i in range(3)
        ], dtype=np.float32)
        sMin = np.array(m.scale_min, dtype=np.float32)
        scale = (u16s.astype(np.float32) / 65535.0) * sRng + sMin

    opacity = None
    if with_opacity:
        u16o = np.frombuffer(recs[:, 22:24].tobytes(), dtype=np.uint16).reshape(n)
        opacity = (
            u16o.astype(np.float32) / 65535.0
            * (m.opacity_max - m.opacity_min) + m.opacity_min
        )

    return positions, colors, scale, opacity
