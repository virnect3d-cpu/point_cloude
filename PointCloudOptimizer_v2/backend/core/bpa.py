"""
Ball-Pivoting Algorithm (BPA) surface reconstruction.

Open3D 기반. 법선이 있으면 그대로 사용하고, 없으면 추정합니다.
Delaunay 3D(사면체)와 달리 삼각 메쉬를 직접 생성합니다.
"""
from __future__ import annotations

import numpy as np
from typing import Dict, Optional, Tuple


def reconstruct_bpa(
    pts: np.ndarray,
    normals: Optional[np.ndarray],
    radii_scale: float = 1.0,
) -> Dict[str, np.ndarray]:
    """
    pts: (N, 3) float32
    normals: (N, 3) float32 or None
    radii_scale: 인접 거리 대비 BPA 반경 배율 (기본 1.0)
    Returns {"verts": (V,3), "faces": (F,3)}
    """
    try:
        import open3d as o3d
    except ImportError as e:
        raise RuntimeError("BPA(Open3D)를 쓰려면 설치하세요: pip install open3d") from e

    if len(pts) < 4:
        raise ValueError("포인트가 너무 적습니다 (BPA 최소 4개 이상)")

    span = pts.max(axis=0) - pts.min(axis=0)
    diag = float(np.linalg.norm(span)) + 1e-9

    pcd = o3d.geometry.PointCloud()
    pcd.points = o3d.utility.Vector3dVector(pts.astype(np.float64))

    if normals is not None and len(normals) == len(pts) and normals.shape[1] == 3:
        n = normals.astype(np.float64)
        norms = np.linalg.norm(n, axis=1, keepdims=True)
        norms = np.maximum(norms, 1e-12)
        n = n / norms
        pcd.normals = o3d.utility.Vector3dVector(n)
    else:
        # 인접 기반 법선 추정 (비구조화 클라우드용)
        radius = max(diag * 0.02, 1e-6)
        pcd.estimate_normals(
            search_param=o3d.geometry.KDTreeSearchParamHybrid(radius=radius, max_nn=40),
        )
        try:
            pcd.orient_normals_consistent_tangent_plane(20)
        except Exception:
            pass

    dists = np.asarray(pcd.compute_nearest_neighbor_distance())
    avg = float(np.mean(dists)) if dists.size else diag * 0.001
    avg = max(avg, 1e-9)
    s = float(radii_scale)
    if s <= 0:
        s = 1.0
    base = avg * s
    radii_list = [base * 0.6, base * 1.0, base * 1.8, base * 3.2, base * 5.5]
    radii = o3d.utility.DoubleVector(radii_list)

    mesh = o3d.geometry.TriangleMesh.create_from_point_cloud_ball_pivoting(
        pcd,
        radii,
    )

    if len(mesh.triangles) < 1:
        raise ValueError(
            "BPA가 빈 메쉬를 반환했습니다. 포인트 밀도·스케일을 확인하거나 Marching Cubes를 시도하세요."
        )

    mesh.remove_duplicated_vertices()
    mesh.remove_duplicated_triangles()
    mesh.remove_degenerate_triangles()
    mesh.remove_unreferenced_vertices()

    verts = np.asarray(mesh.vertices, dtype=np.float32)
    faces = np.asarray(mesh.triangles, dtype=np.int32)

    if len(faces) < 1:
        raise ValueError("BPA 결과에 면이 없습니다.")

    return {"verts": verts, "faces": faces}
