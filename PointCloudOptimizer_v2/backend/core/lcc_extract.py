"""LCC (XGrids PortalCam) → 점 클라우드 추출.

XGrids 가 자동 동봉하는 mesh-files/*.ply 는 본체 일부만 담은 저해상도 proxy 라
콜라이더가 작게 잘려나오는 원인이 됨. 대신 data.bin 의 실제 splat 점을 LOD별로
추출해서 콜라이더 입력으로 사용.

확정된 32B 레코드 레이아웃 (docs/lcc-format.md):
    [ 0..12)  float32 LE × 3   position
    [12..16)  uint8   × 4      RGBA8
    [16..32)  (skipped)        scale/opacity/SH

LOD 경계는 manifest.splats[] 누적합 × 32.
"""
from __future__ import annotations

import json
from pathlib import Path
from typing import Optional, Tuple

import numpy as np

RECORD = 32


def find_lcc_root(path: str | Path) -> Path:
    """입력이 .lcc 파일이면 그 부모, 디렉토리면 그대로. data.bin 존재 검증."""
    p = Path(path)
    if p.is_file() and p.suffix.lower() == ".lcc":
        p = p.parent
    if not p.is_dir():
        raise ValueError(f"LCC 디렉토리 아님: {p}")
    if not (p / "data.bin").is_file():
        raise FileNotFoundError(f"data.bin 없음: {p}")
    return p


def read_manifest(root: Path) -> dict:
    candidates = list(root.glob("*.lcc"))
    if not candidates:
        raise FileNotFoundError(f"*.lcc manifest 없음: {root}")
    return json.loads(candidates[0].read_text(encoding="utf-8-sig"))


def extract_lod(
    root: str | Path,
    lod: int = 0,
    max_points: Optional[int] = None,
    seed: int = 7,
) -> Tuple[np.ndarray, np.ndarray, dict]:
    """LCC 디렉토리에서 특정 LOD 의 점/색상 추출.

    Returns:
        pts:      float32 (N, 3)
        colors:   uint8   (N, 4)  RGBA
        meta:     {'lod': int, 'total_splats': int, 'returned': int, 'name': str}

    max_points 가 주어지면 균일 무작위 다운샘플 (콜라이더 입력은 보통 50~200K 면 충분).
    """
    root = find_lcc_root(root)
    manifest = read_manifest(root)
    splats = manifest.get("splats") or []
    if not splats:
        raise ValueError(f"manifest 에 splats 배열 없음")
    if lod < 0 or lod >= len(splats):
        raise ValueError(f"lod 범위 벗어남: {lod} (0..{len(splats)-1})")

    start_splat = sum(splats[:lod])
    n_total     = int(splats[lod])
    byte_start  = start_splat * RECORD
    byte_len    = n_total * RECORD
    data_path   = root / "data.bin"

    with data_path.open("rb") as f:
        f.seek(byte_start)
        raw = f.read(byte_len)
    if len(raw) != byte_len:
        raise IOError(f"data.bin short read: {len(raw)}/{byte_len}")

    recs   = np.frombuffer(raw, dtype=np.uint8).reshape(n_total, RECORD)
    pts    = np.frombuffer(recs[:, :12].tobytes(), dtype=np.float32).reshape(n_total, 3).copy()
    colors = recs[:, 12:16].copy()  # uint8 RGBA

    if max_points is not None and n_total > max_points:
        rng = np.random.default_rng(seed)
        idx = rng.choice(n_total, size=int(max_points), replace=False)
        idx.sort()
        pts    = pts[idx]
        colors = colors[idx]

    meta = {
        "lod":          int(lod),
        "total_splats": int(n_total),
        "returned":     int(len(pts)),
        "name":         str(manifest.get("name", root.name)),
    }
    return pts, colors, meta
