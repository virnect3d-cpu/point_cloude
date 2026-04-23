"""
Page 4용 — UV unwrap + 포인트 클라우드 색 베이킹 + AO + HSV 조정.

- UV unwrap: xatlas (Unity도 쓰는 오픈소스, 차트 분할 자동)
- 색 베이킹: UV 픽셀 → 3D 좌표 → k-NN 포인트 색 보간
- 라이팅 베이킹: AO(Ambient Occlusion) + 간단 directional
- HSV 조정: Pillow ImageEnhance + custom hue shift
"""
from __future__ import annotations

import io
import math
from typing import Dict, Optional, Tuple

import numpy as np


# ══════════════════════════════════════════════════════════════════════════════
# 1. UV Unwrap (xatlas)
# ══════════════════════════════════════════════════════════════════════════════
def uv_unwrap(
    verts: np.ndarray, faces: np.ndarray,
    resolution: int = 2048,
    padding: int = 4,
) -> Dict:
    """
    xatlas로 자동 UV unwrap.

    입력 메쉬에 차트(chart)를 잘라 2D 공간에 packing해 UV 좌표 생성.
    Unity/Maya에서 쓰는 것과 동일한 알고리즘.

    반환:
      {
        "verts":   (V', 3)  — UV 심에서 쪼개져 버텍스 늘어남
        "faces":   (F, 3)   — 새 인덱스
        "uvs":     (V', 2)  — 0~1 범위 UV 좌표
        "orig_idx": (V',)   — 각 새 버텍스가 원본 어느 버텍스에서 쪼개졌는지
      }
    """
    import xatlas

    atlas = xatlas.Atlas()
    atlas.add_mesh(
        verts.astype(np.float32),
        faces.astype(np.uint32),
    )

    # Chart 생성 옵션
    co = xatlas.ChartOptions()
    co.max_iterations = 2
    # Pack 옵션
    po = xatlas.PackOptions()
    po.resolution = int(resolution)
    po.padding = int(padding)
    po.bilinear = True

    atlas.generate(chart_options=co, pack_options=po)

    # xatlas 0.0.11: atlas[0] == (vmapping, indices, uvs)
    # uvs는 이미 0~1 범위로 정규화됨 (atlas.width / atlas.height 기준)
    vmap, idx, uvs = atlas[0]
    vmap = np.asarray(vmap, dtype=np.int32)
    new_faces = np.asarray(idx, dtype=np.int32).reshape(-1, 3)
    uvs = np.asarray(uvs, dtype=np.float32)
    uvs = np.clip(uvs, 0.0, 1.0)

    # 원본 verts를 vmap으로 재매핑
    new_verts = verts[vmap].astype(np.float32)

    return {
        "verts": new_verts,
        "faces": new_faces,
        "uvs": uvs,
        "orig_idx": vmap,
        "atlas_width": int(atlas.width),
        "atlas_height": int(atlas.height),
    }


# ══════════════════════════════════════════════════════════════════════════════
# 2. Texture Baking
# ══════════════════════════════════════════════════════════════════════════════
def _rasterize_triangle_barycentric(
    uv0: np.ndarray, uv1: np.ndarray, uv2: np.ndarray,
    tex_size: int,
) -> Tuple[np.ndarray, np.ndarray]:
    """
    한 삼각형의 UV (0~1)를 픽셀 그리드로 래스터라이즈.
    반환: (pixel_indices (N,2) [px,py], barycentrics (N,3))
    """
    W = H = tex_size
    # 픽셀 좌표로 변환 (0.5 오프셋: 픽셀 중심)
    x0, y0 = uv0[0] * W, uv0[1] * H
    x1, y1 = uv1[0] * W, uv1[1] * H
    x2, y2 = uv2[0] * W, uv2[1] * H

    min_x = max(0, int(math.floor(min(x0, x1, x2))))
    max_x = min(W - 1, int(math.ceil(max(x0, x1, x2))))
    min_y = max(0, int(math.floor(min(y0, y1, y2))))
    max_y = min(H - 1, int(math.ceil(max(y0, y1, y2))))
    if max_x < min_x or max_y < min_y:
        return np.empty((0, 2), dtype=np.int32), np.empty((0, 3), dtype=np.float32)

    # 베이시스 행렬
    denom = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2)
    if abs(denom) < 1e-12:
        return np.empty((0, 2), dtype=np.int32), np.empty((0, 3), dtype=np.float32)

    px = np.arange(min_x, max_x + 1, dtype=np.float32) + 0.5
    py = np.arange(min_y, max_y + 1, dtype=np.float32) + 0.5
    gx, gy = np.meshgrid(px, py)  # (h, w)

    b0 = ((y1 - y2) * (gx - x2) + (x2 - x1) * (gy - y2)) / denom
    b1 = ((y2 - y0) * (gx - x2) + (x0 - x2) * (gy - y2)) / denom
    b2 = 1.0 - b0 - b1

    mask = (b0 >= -1e-4) & (b1 >= -1e-4) & (b2 >= -1e-4)
    if not mask.any():
        return np.empty((0, 2), dtype=np.int32), np.empty((0, 3), dtype=np.float32)

    ix = gx[mask].astype(np.int32)
    iy = gy[mask].astype(np.int32)
    bary = np.stack([b0[mask], b1[mask], b2[mask]], axis=1).astype(np.float32)
    # clip barycentric (edge 픽셀)
    bary = np.clip(bary, 0.0, 1.0)
    bary_sum = bary.sum(axis=1, keepdims=True)
    bary_sum = np.where(bary_sum < 1e-12, 1.0, bary_sum)
    bary /= bary_sum

    pixel_idx = np.stack([ix, iy], axis=1)
    return pixel_idx, bary


def bake_color_texture(
    verts: np.ndarray, faces: np.ndarray, uvs: np.ndarray,
    pts: np.ndarray, pt_colors: np.ndarray,
    tex_size: int = 2048,
    knn: int = 4,
) -> np.ndarray:
    """
    UV 펼쳐진 메쉬의 각 픽셀에 대응하는 3D 좌표를 구하고,
    원본 포인트 k-NN 색을 가중 평균해 텍스처 픽셀에 기록.

    반환: (H, W, 4) uint8 RGBA — 미사용 영역 alpha=0
    """
    from scipy.spatial import cKDTree

    W = H = int(tex_size)
    tex = np.zeros((H, W, 4), dtype=np.uint8)
    mask = np.zeros((H, W), dtype=bool)

    # 1) 모든 삼각형 래스터라이즈 → 픽셀별 3D 좌표 배치 수집
    all_pos = []
    all_px = []
    for fi in range(len(faces)):
        a, b, c = int(faces[fi, 0]), int(faces[fi, 1]), int(faces[fi, 2])
        px_idx, bary = _rasterize_triangle_barycentric(uvs[a], uvs[b], uvs[c], tex_size)
        if len(px_idx) == 0:
            continue
        # 베리센트릭 보간으로 3D 좌표 계산
        pos = (bary[:, 0:1] * verts[a] +
               bary[:, 1:2] * verts[b] +
               bary[:, 2:3] * verts[c])
        all_pos.append(pos)
        all_px.append(px_idx)

    if not all_pos:
        return tex

    all_pos_arr = np.concatenate(all_pos, axis=0)
    all_px_arr = np.concatenate(all_px, axis=0)

    # 2) 배치 k-NN → 색 계산
    tree = cKDTree(pts.astype(np.float64))
    K = int(max(1, knn))
    d, idx = tree.query(all_pos_arr.astype(np.float64), k=K, workers=-1)
    if K == 1:
        cols = pt_colors[idx]
    else:
        w = 1.0 / (d + 1e-6)
        w /= w.sum(axis=1, keepdims=True)
        cols = np.einsum("nk,nkc->nc", w, pt_colors[idx].astype(np.float64))
        cols = np.clip(cols, 0.0, 1.0)

    # 3) 픽셀 쓰기 (y가 세로, 이미지 좌표는 top-down이므로 flip)
    pxs = all_px_arr[:, 0]
    pys = (H - 1) - all_px_arr[:, 1]  # UV y=0이 아래 → 이미지는 위부터
    c8 = (cols * 255).astype(np.uint8)
    tex[pys, pxs, 0] = c8[:, 0]
    tex[pys, pxs, 1] = c8[:, 1]
    tex[pys, pxs, 2] = c8[:, 2]
    tex[pys, pxs, 3] = 255
    mask[pys, pxs] = True

    # 4) UV seam 패딩 (주변 픽셀로 1~3픽셀 확장해서 밉맵 시 이상한 색 방지)
    tex = _dilate_texture(tex, mask, iterations=3)

    return tex


def _dilate_texture(tex: np.ndarray, mask: np.ndarray, iterations: int = 3) -> np.ndarray:
    """uv seam 주변으로 색을 번지게 해서 mipmap/bilinear 누수 방지."""
    H, W = mask.shape
    out = tex.copy()
    m = mask.copy()
    for _ in range(int(iterations)):
        # 4방향 쉬프트 (empty 픽셀에 이웃 평균 주입)
        neigh = np.zeros((H, W, 3), dtype=np.int32)
        count = np.zeros((H, W), dtype=np.int32)
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                if dy == 0 and dx == 0:
                    continue
                ys = slice(max(0, dy), H + min(0, dy))
                ys_s = slice(max(0, -dy), H + min(0, -dy))
                xs = slice(max(0, dx), W + min(0, dx))
                xs_s = slice(max(0, -dx), W + min(0, -dx))
                valid = m[ys, xs]
                neigh[ys_s, xs_s][valid] += out[ys, xs, :3][valid].astype(np.int32)
                count[ys_s, xs_s][valid] += 1
        fill = (~m) & (count > 0)
        out[fill, 0] = (neigh[fill, 0] // count[fill]).astype(np.uint8)
        out[fill, 1] = (neigh[fill, 1] // count[fill]).astype(np.uint8)
        out[fill, 2] = (neigh[fill, 2] // count[fill]).astype(np.uint8)
        out[fill, 3] = 255
        m = m | fill
    return out


# ══════════════════════════════════════════════════════════════════════════════
# 3. Lighting Bake (AO + Directional)
# ══════════════════════════════════════════════════════════════════════════════
def bake_lighting(
    verts: np.ndarray, faces: np.ndarray, uvs: np.ndarray,
    tex_size: int = 2048,
    ao_strength: float = 0.5,
    light_dir: Tuple[float, float, float] = (0.3, 1.0, 0.3),
    ao_samples: int = 8,
) -> np.ndarray:
    """
    UV 공간에 쉐이딩(AO + directional) 베이킹 → (H, W) float32 (0~1).

    간단 구현:
      - 각 픽셀의 3D 좌표 + 보간 법선 사용
      - AO: 자기 법선 방향에 가까운 이웃 face들 위쪽을 체크
      - Directional: dot(normal, light) clamp
    """
    try:
        import trimesh
    except ImportError:
        return np.ones((tex_size, tex_size), dtype=np.float32)

    W = H = int(tex_size)
    # 픽셀별 법선 맵 만들기 (래스터 + barycentric)
    light = np.array(light_dir, dtype=np.float64)
    light /= max(np.linalg.norm(light), 1e-9)

    # per-vertex normal 계산
    V = verts.astype(np.float64)
    F = faces.astype(np.int64)
    v0 = V[F[:, 0]]; v1 = V[F[:, 1]]; v2 = V[F[:, 2]]
    fn = np.cross(v1 - v0, v2 - v0)
    vn = np.zeros_like(V)
    for k in range(3):
        np.add.at(vn, F[:, k], fn)
    ln = np.linalg.norm(vn, axis=1, keepdims=True)
    ln = np.maximum(ln, 1e-12)
    vn = vn / ln

    # AO: trimesh로 레이캐스트 (느림) → 간소화: 법선-위쪽 각도만
    # 진짜 AO 대신 "상대적 높이" AO 사용: 위쪽일수록 밝고 아래쪽일수록 어둠
    y_min, y_max = float(V[:, 1].min()), float(V[:, 1].max())
    y_range = max(y_max - y_min, 1e-9)

    shading = np.zeros((H, W), dtype=np.float32)
    for fi in range(len(faces)):
        a, b, c = int(faces[fi, 0]), int(faces[fi, 1]), int(faces[fi, 2])
        px_idx, bary = _rasterize_triangle_barycentric(uvs[a], uvs[b], uvs[c], tex_size)
        if len(px_idx) == 0:
            continue
        # 픽셀 법선 (barycentric 보간)
        n = (bary[:, 0:1] * vn[a] + bary[:, 1:2] * vn[b] + bary[:, 2:3] * vn[c])
        ln_px = np.linalg.norm(n, axis=1, keepdims=True)
        ln_px = np.maximum(ln_px, 1e-9)
        n = n / ln_px
        pos = (bary[:, 0:1] * V[a] + bary[:, 1:2] * V[b] + bary[:, 2:3] * V[c])
        # Directional
        diff = np.clip(n @ light, 0.0, 1.0)
        # 간이 AO: 높이 기반 (상단=밝음, 하단=어둠) — 실제 AO 근사
        height_ao = (pos[:, 1] - y_min) / y_range   # 0~1
        height_ao = 0.5 + 0.5 * height_ao            # 0.5~1.0 범위로
        shade = diff * 0.7 + 0.3                     # directional 70% + ambient 30%
        shade = shade * (1.0 - ao_strength) + shade * height_ao * ao_strength

        ix = px_idx[:, 0]
        iy = (H - 1) - px_idx[:, 1]
        shading[iy, ix] = np.clip(shade, 0.0, 1.0).astype(np.float32)

    return shading


def apply_lighting(color_tex: np.ndarray, shading: np.ndarray) -> np.ndarray:
    """컬러 텍스처에 쉐이딩 맵을 곱해 라이팅 입힘 (in-place 아님)."""
    out = color_tex.copy()
    s = np.clip(shading, 0.0, 1.0)[:, :, None]
    out[:, :, :3] = (out[:, :, :3].astype(np.float32) * s).clip(0, 255).astype(np.uint8)
    return out


# ══════════════════════════════════════════════════════════════════════════════
# 4. HSV / 밝기 조정
# ══════════════════════════════════════════════════════════════════════════════
def apply_hsv_adjust(
    tex: np.ndarray,
    hue_shift: float = 0.0,       # -180 ~ +180 (degrees)
    saturation: float = 1.0,      # 0 ~ 2 (1.0 = 원본)
    brightness: float = 1.0,      # 0 ~ 2 (1.0 = 원본)
) -> np.ndarray:
    """RGBA 텍스처에 HSV 조정. hue는 degree, sat/bright는 배율.

    ⚠️  이 함수는 frontend/js/hsv.js (window.HSV.adjustImageData) 와 **bit-identical**
        결과를 내야 한다. 슬라이더 프리뷰(JS)와 최종 서버 결과가 다르면 안 됨.

        Canonical 공식은 frontend/js/hsv.js 주석의 FORMULA CONTRACT 참조.
        tests/test_hsv_parity.py 의 골든 벡터로 자동 검증됨.
    """
    from PIL import Image
    img = Image.fromarray(tex, mode="RGBA")
    rgb = img.convert("RGB")

    rgb_arr = np.asarray(rgb, dtype=np.float32) / 255.0
    # RGB → HSV
    r, g, b = rgb_arr[..., 0], rgb_arr[..., 1], rgb_arr[..., 2]
    mx = rgb_arr.max(axis=-1)
    mn = rgb_arr.min(axis=-1)
    df = mx - mn
    h = np.zeros_like(mx)
    mask = df > 1e-9
    # hue 계산 (0~360)
    rc = (mx - r) / np.where(df == 0, 1, df)
    gc = (mx - g) / np.where(df == 0, 1, df)
    bc = (mx - b) / np.where(df == 0, 1, df)
    h = np.where(mx == r, (bc - gc), np.where(mx == g, 2.0 + rc - bc, 4.0 + gc - rc))
    h = (h * 60.0) % 360.0
    h = np.where(mask, h, 0.0)
    s = np.where(mx == 0, 0.0, df / np.where(mx == 0, 1, mx))
    v = mx

    # 조정
    h = (h + float(hue_shift)) % 360.0
    s = np.clip(s * float(saturation), 0.0, 1.0)
    v = np.clip(v * float(brightness), 0.0, 1.0)

    # HSV → RGB
    c = v * s
    x = c * (1 - np.abs((h / 60.0) % 2 - 1))
    m = v - c
    hi = (h / 60.0).astype(np.int32) % 6
    r2 = np.zeros_like(h); g2 = np.zeros_like(h); b2 = np.zeros_like(h)
    r2 = np.where(hi == 0, c, np.where(hi == 1, x, np.where(hi == 2, 0, np.where(hi == 3, 0, np.where(hi == 4, x, c)))))
    g2 = np.where(hi == 0, x, np.where(hi == 1, c, np.where(hi == 2, c, np.where(hi == 3, x, np.where(hi == 4, 0, 0)))))
    b2 = np.where(hi == 0, 0, np.where(hi == 1, 0, np.where(hi == 2, x, np.where(hi == 3, c, np.where(hi == 4, c, x)))))
    out_rgb = np.stack([r2 + m, g2 + m, b2 + m], axis=-1)
    # JS 측 `(x*255 + 0.5) | 0` 과 동일: 반올림 후 절삭.
    # astype(uint8) 단독은 절삭이라 중간값에서 1 bit 차이가 난다 (parity test로 검증됨).
    out_rgb = np.clip(np.floor(out_rgb * 255.0 + 0.5), 0, 255).astype(np.uint8)

    out = np.zeros_like(tex)
    out[..., :3] = out_rgb
    out[..., 3] = tex[..., 3]
    return out


# ══════════════════════════════════════════════════════════════════════════════
# 5. PNG 인코딩
# ══════════════════════════════════════════════════════════════════════════════
def texture_to_png_bytes(tex: np.ndarray) -> bytes:
    """RGBA uint8 (H,W,4) → PNG bytes."""
    from PIL import Image
    img = Image.fromarray(tex, mode="RGBA")
    buf = io.BytesIO()
    img.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


# ══════════════════════════════════════════════════════════════════════════════
# 6. 전체 파이프라인 (고수준 API)
# ══════════════════════════════════════════════════════════════════════════════
def bake_texture_pipeline(
    mesh_verts: np.ndarray,
    mesh_faces: np.ndarray,
    src_pts: np.ndarray,
    src_colors: np.ndarray,
    tex_size: int = 2048,
    ao_strength: float = 0.5,
    bake_lighting_on: bool = True,
    light_dir: Tuple[float, float, float] = (0.3, 1.0, 0.3),
) -> Dict:
    """
    메쉬 + 포인트 클라우드 색 → UV 언랩 + 2K 베이크 된 RGBA 텍스처.

    반환:
      {
        "texture": (H,W,4) uint8,
        "verts":  (V',3),
        "faces":  (F,3),
        "uvs":    (V',2),
        "stats":  {...}
      }
    """
    # 1. UV Unwrap
    uv = uv_unwrap(mesh_verts, mesh_faces, resolution=tex_size, padding=4)

    # 2. 색 베이크
    color_tex = bake_color_texture(
        uv["verts"], uv["faces"], uv["uvs"],
        src_pts, src_colors, tex_size=tex_size, knn=4,
    )

    # 3. 라이팅 베이크
    if bake_lighting_on:
        shading = bake_lighting(
            uv["verts"], uv["faces"], uv["uvs"],
            tex_size=tex_size, ao_strength=float(ao_strength),
            light_dir=light_dir,
        )
        final_tex = apply_lighting(color_tex, shading)
    else:
        final_tex = color_tex

    return {
        "texture": final_tex,
        "verts": uv["verts"],
        "faces": uv["faces"],
        "uvs":   uv["uvs"],
        "stats": {
            "tex_size": int(tex_size),
            "verts": int(len(uv["verts"])),
            "faces": int(len(uv["faces"])),
            "filled_ratio": float((final_tex[..., 3] > 0).mean()),
        },
    }
