"""
Poisson Surface Reconstruction + SDF-based Marching Cubes.

포인트 클라우드 -> 정밀 메쉬 변환의 고급 경로.
Marching Cubes의 splat 방식보다 정합·연속성이 월등히 좋음.
"""
from __future__ import annotations

import numpy as np
from typing import Dict, Optional


def reconstruct_poisson(
    pts: np.ndarray,
    normals: Optional[np.ndarray],
    depth: int = 9,
    density_threshold: float = 0.08,
) -> Dict[str, np.ndarray]:
    """
    Poisson Surface Reconstruction (Kazhdan et al.) via Open3D.

    pts: (N,3)
    normals: (N,3) or None — 없으면 KD-tree 기반 추정 후 일관성 있게 정렬
    depth: octree 깊이 (9=기본, 10=고해상도, 11=초고해상도 — 메모리 주의)
    density_threshold: 저밀도 영역 제거 비율 (0 = 제거 안 함)

    반환 {"verts": (V,3), "faces": (F,3)}
    Poisson은 워터타이트를 보장해서 후처리 구멍 메우기가 거의 불필요.
    """
    try:
        import open3d as o3d
    except ImportError as e:
        raise RuntimeError("Poisson(Open3D)을 쓰려면 설치하세요: pip install open3d") from e

    if len(pts) < 16:
        raise ValueError("포인트가 너무 적습니다 (Poisson 최소 16개)")

    span = pts.max(axis=0) - pts.min(axis=0)
    diag = float(np.linalg.norm(span)) + 1e-9

    pcd = o3d.geometry.PointCloud()
    pcd.points = o3d.utility.Vector3dVector(pts.astype(np.float64))

    if normals is not None and len(normals) == len(pts) and normals.shape[1] == 3:
        n = normals.astype(np.float64)
        norms = np.linalg.norm(n, axis=1, keepdims=True)
        norms = np.maximum(norms, 1e-12)
        pcd.normals = o3d.utility.Vector3dVector(n / norms)
    else:
        radius = max(diag * 0.02, 1e-6)
        pcd.estimate_normals(
            search_param=o3d.geometry.KDTreeSearchParamHybrid(radius=radius, max_nn=40),
        )
        try:
            pcd.orient_normals_consistent_tangent_plane(20)
        except Exception:
            pass

    depth = int(max(5, min(12, depth)))
    # scale=1.0 + linear_fit=True: 확장 영역 최소화 (기본 1.1은 빈 공간으로 표면을 밀어냄)
    mesh, densities = o3d.geometry.TriangleMesh.create_from_point_cloud_poisson(
        pcd, depth=depth, width=0, scale=1.0, linear_fit=True,
    )

    # 저밀도(확장 영역) 제거 — 포인트 없는 허공에 생긴 가짜 표면 잘라내기
    if density_threshold > 0 and len(densities):
        d = np.asarray(densities)
        cut = np.quantile(d, float(density_threshold))
        keep = d > cut
        mesh.remove_vertices_by_mask(~keep)

    mesh.remove_duplicated_vertices()
    mesh.remove_duplicated_triangles()
    mesh.remove_degenerate_triangles()
    mesh.remove_unreferenced_vertices()

    verts = np.asarray(mesh.vertices, dtype=np.float32)
    faces = np.asarray(mesh.triangles, dtype=np.int32)

    if len(faces) < 1:
        raise ValueError(
            "Poisson 결과가 비었습니다. depth를 낮추거나 포인트를 확인하세요."
        )

    return {"verts": verts, "faces": faces}


def snap_to_points(
    verts: np.ndarray,
    pts: np.ndarray,
    iterations: int = 3,
    strength: float = 0.5,
    max_dist_ratio: float = 0.05,
) -> np.ndarray:
    """
    메쉬 버텍스를 원본 포인트 클라우드 표면 쪽으로 투영 (ICP-like).

    각 버텍스에 대해 k-NN 원본 포인트의 가중평균 위치를 구하고,
    그 방향으로 strength 만큼 이동. 대각선 대비 max_dist_ratio 이상 떨어진
    버텍스는 건드리지 않음 (Poisson 확장 영역 보호).

    정확도 체감 크게 향상 (특히 BPA의 떨림·MC의 계단 현상 제거).
    """
    if len(verts) < 1 or len(pts) < 8:
        return verts.astype(np.float32)
    try:
        from scipy.spatial import cKDTree
    except ImportError:
        return verts.astype(np.float32)

    span = pts.max(axis=0) - pts.min(axis=0)
    diag = float(np.linalg.norm(span)) + 1e-9
    max_dist = diag * float(max_dist_ratio)

    tree = cKDTree(pts.astype(np.float64))
    v = verts.astype(np.float64).copy()
    k = 8

    for _ in range(int(max(1, iterations))):
        dists, idx = tree.query(v, k=k, workers=-1)
        # dists: (V,k), idx: (V,k)
        w = np.exp(-(dists ** 2) / (2.0 * (diag * 0.005) ** 2))
        w_sum = w.sum(axis=1, keepdims=True)
        w_sum = np.where(w_sum < 1e-12, 1.0, w_sum)
        w /= w_sum
        target = np.einsum("vk,vkd->vd", w, pts[idx].astype(np.float64))

        delta = target - v
        dl = np.linalg.norm(delta, axis=1)
        mask = dl < max_dist
        v[mask] += strength * delta[mask]

    return v.astype(np.float32)


def sdf_marching_cubes(
    pts: np.ndarray,
    grid_res: int = 80,
    iso: float = 0.0,
    k: int = 8,
) -> Dict[str, np.ndarray]:
    """
    거리장(SDF) 기반 Marching Cubes — splat 방식보다 계단 현상 없고 정확.

    각 그리드 셀 중심에서 k-NN 평균 거리를 구해 부호화.
    iso=0 이면 포인트 밀도가 k-NN 평균 이내인 영역 표면.
    """
    try:
        from scipy.spatial import cKDTree
        from skimage.measure import marching_cubes
    except ImportError as e:
        raise RuntimeError(
            "SDF-MC는 scipy+scikit-image가 필요합니다: pip install scipy scikit-image"
        ) from e

    mn = pts.min(axis=0).astype(np.float64)
    mx = pts.max(axis=0).astype(np.float64)
    span = mx - mn
    diag = float(np.linalg.norm(span)) + 1e-9
    pad = span * 0.04 + 1e-6
    mn -= pad
    mx += pad
    span = mx - mn
    GS = int(max(16, min(256, grid_res)))
    cell = span / GS

    # 그리드 중심 좌표
    xs = mn[0] + (np.arange(GS) + 0.5) * cell[0]
    ys = mn[1] + (np.arange(GS) + 0.5) * cell[1]
    zs = mn[2] + (np.arange(GS) + 0.5) * cell[2]
    X, Y, Z = np.meshgrid(xs, ys, zs, indexing="ij")
    grid_pts = np.stack([X.ravel(), Y.ravel(), Z.ravel()], axis=1)

    tree = cKDTree(pts.astype(np.float64))
    dists, _ = tree.query(grid_pts, k=int(max(4, k)), workers=-1)
    # 거리장 = k-NN 평균 거리
    d_mean = dists.mean(axis=1).reshape(GS, GS, GS)

    # 부호화: 기준 거리 = 전역 평균의 배수
    base = float(d_mean[d_mean > 0].mean()) if np.any(d_mean > 0) else 1.0
    sdf = d_mean - base * 1.2      # 양=바깥, 음=안쪽
    level = float(iso)

    try:
        verts, faces, _, _ = marching_cubes(
            sdf, level=level,
            spacing=(float(cell[0]), float(cell[1]), float(cell[2])),
        )
    except (ValueError, RuntimeError) as e:
        raise RuntimeError(f"SDF-MC 실패: {e}")

    verts = (verts + mn).astype(np.float32)
    faces = faces.astype(np.int32)

    if len(faces) < 3:
        raise ValueError(
            "SDF-MC: 표면 생성 실패. 그리드 해상도를 높이거나 포인트 밀도를 확인하세요."
        )

    return {"verts": verts, "faces": faces}
