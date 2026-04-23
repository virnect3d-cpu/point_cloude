"""
Point Cloud → Mesh pipeline (Python/NumPy backend)

Pipeline:
  1. Statistical Outlier Removal (SOR)
  2. Marching Cubes surface reconstruction  (skimage or manual)
  3. Geometry Validation  (watertight / manifold / components / normals)
  4. Mesh Repair          (non-manifold fix / largest-component / normals / holes)
  5. Laplacian Smooth

All functions take / return np.ndarray (float32 verts, int32 faces).
"""
import numpy as np
from scipy.spatial import KDTree
from typing import Tuple, Dict, Any, Optional


# ══════════════════════════════════════════════════════════════════════════════
# 1. Statistical Outlier Removal (SOR)
# ══════════════════════════════════════════════════════════════════════════════
def _sor_mask(pts: np.ndarray, k: int, sigma: float) -> np.ndarray:
    """SOR 필터 마스크만 계산 (shared core).

    k-NN mean distance > (μ + σ·std) 인 포인트를 outlier로 판정.
    """
    if len(pts) < k + 1:
        return np.ones(len(pts), dtype=bool)
    tree = KDTree(pts)
    dists, _ = tree.query(pts, k=k + 1)        # k+1 to include self
    mean_d = dists[:, 1:].mean(axis=1)         # exclude self (dist=0)
    mu, sd = mean_d.mean(), mean_d.std()
    return mean_d <= (mu + sigma * sd)


def sor(pts: np.ndarray, k: int = 20, sigma: float = 2.0) -> np.ndarray:
    """Remove statistical outliers using k-NN mean distances."""
    return sor_with_normals(pts, None, k=k, sigma=sigma)[0]


def sor_with_normals(
    pts: np.ndarray,
    normals: Optional[np.ndarray],
    k: int = 20,
    sigma: float = 2.0,
) -> Tuple[np.ndarray, Optional[np.ndarray]]:
    """SOR. normals가 주어지면 동일 마스크로 함께 필터링 (BPA용).

    단일 코어 구현으로 통합 — sor()와 내부 로직 공유.
    """
    if normals is not None and (normals.shape[0] != len(pts) or normals.shape[1] != 3):
        normals = None
    mask = _sor_mask(pts, k, sigma)
    p2 = pts[mask].astype(np.float32)
    if normals is None:
        return p2, None
    return p2, normals[mask].astype(np.float32)


# ══════════════════════════════════════════════════════════════════════════════
# 2. Surface Reconstruction — Poisson / SDF-MC / splat-MC
# ══════════════════════════════════════════════════════════════════════════════
def build_poisson_mesh(
    pts: np.ndarray,
    normals: Optional[np.ndarray] = None,
    depth: int = 9,
) -> Dict[str, np.ndarray]:
    """Poisson 표면 재구성 (Open3D). MC보다 정합 우수, watertight 보장."""
    from backend.core import poisson
    return poisson.reconstruct_poisson(pts, normals, depth=depth)


def build_sdf_mc_mesh(pts: np.ndarray, grid_res: int = 80) -> Dict[str, np.ndarray]:
    """거리장 기반 MC — 기존 splat 방식보다 계단 현상 적음."""
    from backend.core import poisson
    return poisson.sdf_marching_cubes(pts, grid_res=grid_res)


# ══════════════════════════════════════════════════════════════════════════════
# Hard-surface 재구성 — Alpha Shape + 평면 스냅 (건물·설비·로봇)
# ══════════════════════════════════════════════════════════════════════════════
def build_alpha_shape_mesh(
    pts: np.ndarray,
    alpha_ratio: float = 0.015,
) -> Dict[str, np.ndarray]:
    """Open3D alpha-shape 재구성 — Poisson보다 평면·모서리 살리기 좋음.

    alpha_ratio: 대각선 대비 alpha (0.01~0.03 권장). 작을수록 디테일·구멍 많음.
    """
    import open3d as o3d
    V = np.asarray(pts, dtype=np.float64)
    if len(V) < 4:
        return {"verts": np.zeros((0, 3), np.float32),
                "faces": np.zeros((0, 3), np.int32)}
    span = V.max(axis=0) - V.min(axis=0)
    diag = float(np.linalg.norm(span)) + 1e-9
    alpha = max(1e-4, diag * float(alpha_ratio))

    pcd = o3d.geometry.PointCloud()
    pcd.points = o3d.utility.Vector3dVector(V)
    mesh = o3d.geometry.TriangleMesh.create_from_point_cloud_alpha_shape(pcd, alpha)
    mesh.remove_degenerate_triangles()
    mesh.remove_duplicated_triangles()
    mesh.remove_duplicated_vertices()
    mesh.remove_non_manifold_edges()

    verts = np.asarray(mesh.vertices, dtype=np.float32)
    faces = np.asarray(mesh.triangles, dtype=np.int32)
    return {"verts": verts, "faces": faces}


def detect_planar_obbs(
    pts: np.ndarray,
    max_planes: int = 40,
    min_support_ratio: float = 0.005,   # 포인트의 0.5% 이상 (작은 벽도 포함)
    distance_ratio: float = 0.003,      # RANSAC inlier 임계값 (0.3% = 대각선 127m에서 38cm)
    min_extent_ratio: float = 0.03,     # OBB 최소 크기 (3% 미만은 너무 작음)
    max_extent_ratio: float = 1.2,      # OBB 최대 (평면 OBB는 자연스럽게 큼 — guard 완화)
) -> list:
    """Hybrid hard mode — RANSAC 평면 검출 → 각 평면 inlier의 OBB.

    DBSCAN보다 건물 스캔에 강건 (floor/ceiling/walls 각각 독립 평면으로 잡힘).
    결과 OBB는 벽 하나 크기 — 씬 전체를 덮는 거대 OBB 안 나옴.
    반환: [{'center','extent','R'}, ...]
    """
    import open3d as o3d
    V = np.asarray(pts, dtype=np.float64)
    if len(V) < 50:
        return []
    span = V.max(axis=0) - V.min(axis=0)
    diag = float(np.linalg.norm(span)) + 1e-9
    dist_th = max(1e-4, diag * float(distance_ratio))
    min_support = max(50, int(len(V) * float(min_support_ratio)))
    min_ext = diag * float(min_extent_ratio)
    max_ext = diag * float(max_extent_ratio)

    pcd = o3d.geometry.PointCloud()
    pcd.points = o3d.utility.Vector3dVector(V.copy())

    result = []
    remaining = pcd
    for _ in range(max_planes):
        if len(remaining.points) < min_support:
            break
        try:
            _, inliers = remaining.segment_plane(
                distance_threshold=dist_th, ransac_n=3, num_iterations=200,
            )
        except Exception:
            break
        if len(inliers) < min_support:
            break
        inlier_pcd = remaining.select_by_index(inliers)
        remaining = remaining.select_by_index(inliers, invert=True)
        try:
            obb = inlier_pcd.get_oriented_bounding_box()
        except Exception:
            continue
        extent = np.asarray(obb.extent, dtype=np.float32)
        mx = float(extent.max())
        if mx < min_ext or mx > max_ext:
            continue  # too small/large — skip
        result.append({
            "center": np.asarray(obb.center, dtype=np.float32),
            "extent": extent,
            "R":      np.asarray(obb.R, dtype=np.float32),
        })
    return result


# 하위 호환 alias — 기존 이름 유지
def detect_planar_clusters(
    pts: np.ndarray,
    eps_ratio: float = 0.015,
    min_cluster_size: int = 500,
    planarity_thresh: float = 0.08,
    min_extent_ratio: float = 0.02,
) -> list:
    """Hybrid hard mode용 — DBSCAN + PCA로 평면 클러스터 → OBB 반환.

    반환: [{'center','extent','R','mask'}, ...]
      - center: OBB 중심 (3,)
      - extent: OBB 가로/세로/두께 (3,)
      - R:      OBB 회전행렬 (3,3)
      - mask:   이 클러스터에 속한 point index mask
    """
    import open3d as o3d
    V = np.asarray(pts, dtype=np.float64)
    if len(V) < 50:
        return []
    span = V.max(axis=0) - V.min(axis=0)
    diag = float(np.linalg.norm(span)) + 1e-9
    eps = max(1e-3, diag * float(eps_ratio))
    min_ext = diag * float(min_extent_ratio)

    pcd = o3d.geometry.PointCloud()
    pcd.points = o3d.utility.Vector3dVector(V)
    try:
        labels = np.asarray(pcd.cluster_dbscan(
            eps=eps, min_points=30, print_progress=False))
    except Exception:
        return []

    result = []
    unique = sorted(set(int(x) for x in labels.tolist()) - {-1})
    for lbl in unique:
        mask = labels == lbl
        if int(mask.sum()) < min_cluster_size:
            continue
        pts_c = V[mask]
        # PCA — 가장 작은 고유값 / 가장 큰 고유값
        centered = pts_c - pts_c.mean(axis=0)
        try:
            cov = np.cov(centered.T)
            eigvals = np.sort(np.linalg.eigvalsh(cov))   # ascending
        except Exception:
            continue
        if eigvals[-1] < 1e-12:
            continue
        planarity = float(eigvals[0] / eigvals[-1])
        if planarity > planarity_thresh:
            continue  # 너무 굴곡짐 — 유기체일 가능성
        # OBB 계산
        try:
            pcd_c = o3d.geometry.PointCloud()
            pcd_c.points = o3d.utility.Vector3dVector(pts_c)
            obb = pcd_c.get_oriented_bounding_box()
        except Exception:
            continue
        extent = np.asarray(obb.extent, dtype=np.float32)
        if float(max(extent)) < min_ext:
            continue
        result.append({
            "center": np.asarray(obb.center, dtype=np.float32),
            "extent": extent,
            "R":      np.asarray(obb.R, dtype=np.float32),
            "mask":   mask,
        })
    return result


def build_box_mesh_from_obbs(obbs: list) -> Dict[str, np.ndarray]:
    """OBB list → 직육면체들 합쳐진 (verts, faces) 메쉬."""
    if not obbs:
        return {"verts": np.zeros((0, 3), np.float32),
                "faces": np.zeros((0, 3), np.int32)}
    all_V, all_F = [], []
    offset = 0
    # 단위 박스 8 꼭짓점 (half-unit)
    base_corners = np.array([
        [-1, -1, -1], [ 1, -1, -1], [ 1,  1, -1], [-1,  1, -1],
        [-1, -1,  1], [ 1, -1,  1], [ 1,  1,  1], [-1,  1,  1],
    ], dtype=np.float64)
    # 12 삼각형 — 모두 바깥쪽 winding (CCW from outside)
    base_faces = np.array([
        [0, 2, 1], [0, 3, 2],   # z-
        [4, 5, 6], [4, 6, 7],   # z+
        [0, 1, 5], [0, 5, 4],   # y-
        [2, 3, 7], [2, 7, 6],   # y+
        [1, 2, 6], [1, 6, 5],   # x+
        [0, 4, 7], [0, 7, 3],   # x-
    ], dtype=np.int32)

    for o in obbs:
        c = np.asarray(o["center"], dtype=np.float64)
        e = np.asarray(o["extent"], dtype=np.float64) / 2.0
        R = np.asarray(o["R"], dtype=np.float64)
        V = (base_corners * e) @ R.T + c
        all_V.append(V.astype(np.float32))
        all_F.append(base_faces + offset)
        offset += 8

    return {
        "verts": np.vstack(all_V).astype(np.float32),
        "faces": np.vstack(all_F).astype(np.int32),
    }


def remove_faces_inside_obbs(
    verts: np.ndarray, faces: np.ndarray, obbs: list,
    padding_ratio: float = 1.08,
) -> Dict[str, np.ndarray]:
    """OBB 내부(padding 포함)에 들어가는 삼각형을 제거 — 박스가 덮을 영역 비우기."""
    V = np.asarray(verts, dtype=np.float32)
    F = np.asarray(faces, dtype=np.int32)
    if not obbs or len(F) == 0:
        return {"verts": V, "faces": F}
    centroids = (V[F[:, 0]] + V[F[:, 1]] + V[F[:, 2]]) / 3.0
    remove = np.zeros(len(F), dtype=bool)
    for o in obbs:
        c = np.asarray(o["center"], dtype=np.float32)
        e = np.asarray(o["extent"], dtype=np.float32) / 2.0 * float(padding_ratio)
        R = np.asarray(o["R"], dtype=np.float32)
        # 로컬 좌표: (centroid - c) @ R
        local = (centroids - c) @ R
        inside = (np.abs(local[:, 0]) <= e[0]) & \
                 (np.abs(local[:, 1]) <= e[1]) & \
                 (np.abs(local[:, 2]) <= e[2])
        remove |= inside
    return {"verts": V, "faces": F[~remove]}


def merge_two_meshes(
    v1: np.ndarray, f1: np.ndarray,
    v2: np.ndarray, f2: np.ndarray,
) -> Dict[str, np.ndarray]:
    """단순 concat — boolean union 없이 (z-fighting 감수, 뷰어엔 OK)."""
    if len(v1) == 0:
        return {"verts": np.asarray(v2, np.float32), "faces": np.asarray(f2, np.int32)}
    if len(v2) == 0:
        return {"verts": np.asarray(v1, np.float32), "faces": np.asarray(f1, np.int32)}
    V = np.vstack([v1, v2]).astype(np.float32)
    F = np.vstack([f1, f2 + len(v1)]).astype(np.int32)
    return {"verts": V, "faces": F}


def _dedupe_planes(planes: list, angle_thresh_deg: float = 10.0,
                   offset_thresh: float = 1.0) -> list:
    """중복/유사 평면 병합 — 법선 각도 유사 + offset 가까우면 하나로 취급."""
    if len(planes) <= 1:
        return planes
    import math
    unique = []
    cos_th = math.cos(math.radians(angle_thresh_deg))
    for p in planes:
        n = np.asarray(p[:3], dtype=np.float64)
        nlen = np.linalg.norm(n)
        if nlen < 1e-9:
            continue
        n_unit = n / nlen
        d_norm = float(p[3]) / nlen
        dup = False
        for q in unique:
            nq = np.asarray(q[:3], dtype=np.float64)
            nqlen = np.linalg.norm(nq)
            if nqlen < 1e-9: continue
            nq_unit = nq / nqlen
            if abs(float(n_unit @ nq_unit)) < cos_th:
                continue  # 방향 다름 — 다른 평면
            if abs(d_norm - (float(q[3]) / nqlen)) > offset_thresh:
                continue  # 같은 방향이지만 offset 다름 — 다른 평면
            dup = True
            break
        if not dup:
            unique.append(p)
    return unique


def snap_verts_to_planes(
    verts: np.ndarray,
    pts: np.ndarray,
    max_planes: int = 15,               # 중복 제거하면 충분
    min_support_ratio: float = 0.008,   # 0.8% — 의미있는 평면만
    distance_ratio: float = 0.010,      # 대각선 대비 1% (127m 씬에서 ~127cm — floor noise 포함)
    snap_ratio: float = 0.005,          # 스냅 반경 0.5% (점잖게)
) -> np.ndarray:
    """RANSAC 평면 검출 → 검출된 평면 근처 버텍스를 평면 위로 투영.

    건물·벽·책상처럼 평면이 많은 씬에서 파도치는 Poisson/Alpha 결과를
    "납작하게" 펴줌. 평면이 거의 없으면 원본 verts 반환 (노옵).
    """
    import open3d as o3d
    V = np.asarray(verts, dtype=np.float64)
    P = np.asarray(pts, dtype=np.float64)
    if len(V) < 3 or len(P) < 20:
        return V.astype(np.float32)

    span = P.max(axis=0) - P.min(axis=0)
    diag = float(np.linalg.norm(span)) + 1e-9
    dist_th = max(1e-4, diag * float(distance_ratio))
    snap_th = max(1e-4, diag * float(snap_ratio))
    min_support = max(50, int(len(P) * float(min_support_ratio)))

    pcd = o3d.geometry.PointCloud()
    pcd.points = o3d.utility.Vector3dVector(P.copy())

    planes: list = []
    remaining = pcd
    for _ in range(max_planes):
        if len(remaining.points) < min_support:
            break
        try:
            model, inliers = remaining.segment_plane(
                distance_threshold=dist_th, ransac_n=3, num_iterations=200,
            )
        except Exception:
            break
        if len(inliers) < min_support:
            break
        planes.append(np.asarray(model, dtype=np.float64))
        remaining = remaining.select_by_index(inliers, invert=True)

    if not planes:
        return V.astype(np.float32)

    # 중복/유사 평면 병합 — 같은 floor가 여러 번 검출되는 "계단 스냅" 방지
    # offset_thresh는 scene 크기 대비 (대각선 0.8%)
    planes = _dedupe_planes(planes, angle_thresh_deg=10.0,
                             offset_thresh=diag * 0.008)

    # 각 버텍스에 대해 가장 가까운 평면 찾고 스냅
    planes_arr = np.vstack(planes)                           # (K, 4)
    normals = planes_arr[:, :3]
    nlen = np.linalg.norm(normals, axis=1, keepdims=True)
    normals_unit = normals / np.maximum(nlen, 1e-9)
    d_norm = planes_arr[:, 3] / np.maximum(nlen.squeeze(1), 1e-9)

    # signed distance: V @ n.T + d  (broadcast)
    signed = V @ normals_unit.T + d_norm[None, :]            # (N, K)
    abs_d = np.abs(signed)
    closest = abs_d.argmin(axis=1)
    best_d = abs_d[np.arange(len(V)), closest]
    mask = best_d < snap_th
    if not mask.any():
        return V.astype(np.float32)

    n_closest = normals_unit[closest]
    sd = signed[np.arange(len(V)), closest]
    V_snap = V - sd[:, None] * n_closest
    V_out = V.copy()
    V_out[mask] = V_snap[mask]
    return V_out.astype(np.float32)


def snap_verts_to_points(
    verts: np.ndarray, pts: np.ndarray,
    iterations: int = 3, strength: float = 0.5,
) -> np.ndarray:
    """메쉬 버텍스를 원본 포인트 쪽으로 투영 (정합 강화)."""
    from backend.core import poisson
    return poisson.snap_to_points(verts, pts, iterations=iterations, strength=strength)


def merge_close_vertices(
    verts: np.ndarray, faces: np.ndarray, eps_ratio: float = 1e-5,
) -> Dict[str, np.ndarray]:
    """
    동일 좌표 버텍스 병합 — decimation 후 '끊어진 면' 현상 제거.

    eps_ratio: 대각선 대비 병합 거리 (1e-5 = 매우 타이트, 1e-4 = 관대).
    두 버텍스가 eps 이내면 같은 버텍스로 취급해 인덱스를 공유.
    """
    V = np.asarray(verts, dtype=np.float32)
    F = np.asarray(faces, dtype=np.int32)
    if len(V) < 2:
        return {"verts": V, "faces": F}

    span = V.max(0) - V.min(0)
    diag = float(np.linalg.norm(span)) + 1e-9
    eps = max(diag * float(eps_ratio), 1e-7)

    # 정수 격자로 양자화해 중복 감지
    q = np.round(V / eps).astype(np.int64)
    # 유니크 행 얻고 역 인덱스
    _, inverse = np.unique(q, axis=0, return_inverse=True)
    # 새 버텍스 = 각 그룹의 평균 좌표
    n_new = int(inverse.max()) + 1
    new_V = np.zeros((n_new, 3), dtype=np.float64)
    cnt = np.zeros(n_new, dtype=np.int32)
    np.add.at(new_V, inverse, V.astype(np.float64))
    np.add.at(cnt, inverse, 1)
    new_V /= np.maximum(cnt, 1)[:, None]
    new_F = inverse[F].astype(np.int32)

    # 퇴화 삼각형 (두 버텍스가 같아진 경우) 제거
    a = new_F[:, 0]; b = new_F[:, 1]; c = new_F[:, 2]
    keep = (a != b) & (b != c) & (a != c)
    new_F = new_F[keep]

    if len(new_F) < 1:
        return {"verts": V, "faces": F}
    return {"verts": new_V.astype(np.float32), "faces": new_F}


def orient_outward(
    verts: np.ndarray, faces: np.ndarray,
) -> np.ndarray:
    """
    모든 face normal이 메쉬 바깥을 향하도록 winding 통일.

    전략 (강화됨 — 2-stage):
      1. trimesh.repair.fix_winding + fix_normals: BFS로 인접 face winding 일관성
         (non-manifold edge가 있어도 각 컴포넌트별로 처리)
      2. 중심에서 바깥쪽 vote: 메쉬 중심에서 각 face로 가는 벡터와 face normal의
         내적이 대다수 음수이면 전체 메쉬가 뒤집힌 상태 → 전체 flip.

    Open3D의 `orient_triangles`는 non-manifold에서 silently 부분 실패하므로
    trimesh를 primary로 사용 (Blender Shift+N과 동일한 BFS 알고리즘).
    """
    V_in = np.asarray(verts, dtype=np.float64)
    F_in = np.asarray(faces, dtype=np.int32)
    if len(F_in) == 0:
        return F_in

    F_out: np.ndarray = F_in
    try:
        import trimesh
        m = trimesh.Trimesh(vertices=V_in, faces=F_in, process=False)
        # fix_winding: 인접 face 간 공유 엣지 방향이 반대가 되도록 BFS propagation
        trimesh.repair.fix_winding(m)
        # fix_normals: winding을 바깥쪽으로 향하게 (volume-based heuristic)
        trimesh.repair.fix_normals(m)
        F_out = np.asarray(m.faces, dtype=np.int32)
    except Exception:
        # 폴백: Open3D (trimesh 미설치 등)
        try:
            import open3d as o3d
            m = o3d.geometry.TriangleMesh(
                o3d.utility.Vector3dVector(V_in),
                o3d.utility.Vector3iVector(F_in),
            )
            m.orient_triangles()
            F_out = np.asarray(m.triangles, dtype=np.int32)
        except Exception:
            return F_in

    # 최종 안전망: 중심 기반 vote (전체 뒤집힘 방지)
    if len(F_out):
        ctr = V_in.mean(axis=0)
        v0 = V_in[F_out[:, 0]]; v1 = V_in[F_out[:, 1]]; v2 = V_in[F_out[:, 2]]
        fn = np.cross(v1 - v0, v2 - v0)
        pos = (v0 + v1 + v2) / 3.0
        out = pos - ctr
        vote = float(np.sum(np.einsum("ij,ij->i", fn, out) > 0))
        if vote < len(F_out) * 0.5:
            F_out = F_out[:, [0, 2, 1]]
    return F_out.astype(np.int32)


def taubin_smooth(
    verts: np.ndarray, faces: np.ndarray,
    iterations: int = 5, lam: float = 0.50, mu: float = -0.53,
) -> np.ndarray:
    """
    Taubin smoothing — Laplacian 기반이지만 부피 수축 없음.

    한 반복당: v = v + λ*(Lv - v)    (수축)
              v = v + μ*(Lv - v)    (확장, μ<0)
    λ + μ ≈ 0 이면 저주파는 살리고 고주파(노이즈)만 제거.
    Laplacian x2 보다 훨씬 깔끔 — 메쉬가 쪼그라들지 않음.
    """
    try:
        from scipy.sparse import lil_matrix, diags
    except ImportError:
        return laplacian_smooth(verts, faces, iterations=iterations * 2, lam=0.3)

    V = len(verts)
    adj = lil_matrix((V, V), dtype=np.float32)
    for f in faces:
        for k in range(3):
            a, b = int(f[k]), int(f[(k + 1) % 3])
            adj[a, b] = 1.0
            adj[b, a] = 1.0
    adj = adj.tocsr()
    deg = np.array(adj.sum(axis=1)).flatten()
    deg[deg == 0] = 1.0
    L = diags(1.0 / deg) @ adj    # 행 정규화 인접행렬

    v = verts.astype(np.float64)
    for _ in range(int(max(1, iterations))):
        v = v + lam * (L @ v - v)  # 수축
        v = v + mu  * (L @ v - v)  # 확장 (μ<0)
    return v.astype(np.float32)


# ── page 2 정리 로직을 page 3에도 쓸 수 있게 공용화 ──────────────────────
def transfer_colors_knn(
    mesh_verts: np.ndarray,
    src_pts: np.ndarray,
    src_colors: np.ndarray,
    k: int = 3,
) -> np.ndarray:
    """
    메쉬 버텍스에 원본 포인트 색 이식 — k-NN 거리 가중 평균.

    mesh_verts: (Vm, 3)
    src_pts:    (Np, 3)
    src_colors: (Np, 3)   float [0,1]
    k:          k-NN 개수
    반환: (Vm, 3) float32 [0,1]
    """
    try:
        from scipy.spatial import cKDTree
    except ImportError:
        # 폴백: 전체 평균색
        mean_c = src_colors.mean(axis=0)
        return np.tile(mean_c, (len(mesh_verts), 1)).astype(np.float32)

    tree = cKDTree(src_pts.astype(np.float64))
    d, idx = tree.query(mesh_verts.astype(np.float64), k=int(max(1, k)), workers=-1)
    if k == 1:
        return src_colors[idx].astype(np.float32)
    # 거리 가중 (1/(d+eps))
    w = 1.0 / (d + 1e-6)
    w /= w.sum(axis=1, keepdims=True)
    # (Vm,k) × (Vm,k,3) → (Vm,3)
    neigh_c = src_colors[idx].astype(np.float64)
    out = np.einsum("vk,vkd->vd", w, neigh_c)
    return np.clip(out, 0.0, 1.0).astype(np.float32)


def cluster_colors_kmeans(
    vert_colors: np.ndarray, k: int = 6, max_iter: int = 50, seed: int = 42,
) -> Tuple[np.ndarray, np.ndarray]:
    """
    버텍스 색을 K개 그룹으로 클러스터링.

    반환: (cluster_id[Vm], centers[K,3])
    scipy.cluster.vq.kmeans2 사용 (sklearn 의존 X).
    """
    try:
        from scipy.cluster.vq import kmeans2
    except ImportError:
        # 폴백: 단일 클러스터
        return (np.zeros(len(vert_colors), dtype=np.int32),
                vert_colors.mean(axis=0, keepdims=True).astype(np.float32))

    n = len(vert_colors)
    k = int(max(1, min(k, max(1, n - 1))))
    if n <= k:
        return (np.arange(n, dtype=np.int32),
                vert_colors.astype(np.float32))

    # kmeans2: minit='++' = k-means++ (좋은 초기화)
    np.random.seed(int(seed))
    centers, ids = kmeans2(
        vert_colors.astype(np.float64),
        k=k,
        iter=int(max_iter),
        minit="++",
        seed=int(seed),
    )
    # 빈 클러스터 제거 (kmeans2가 가끔 만듦)
    unique_ids, inverse = np.unique(ids, return_inverse=True)
    compact_centers = centers[unique_ids]
    # 재정렬: 밝기 오름차순으로 (matX 순서가 일관되게)
    brightness = compact_centers.sum(axis=1)
    order = np.argsort(brightness)
    rank = np.argsort(order)          # old_compact_id → new_id
    final_ids = rank[inverse].astype(np.int32)
    final_centers = compact_centers[order].astype(np.float32)
    return final_ids, final_centers


def assign_face_clusters(
    faces: np.ndarray, vert_clusters: np.ndarray,
) -> np.ndarray:
    """각 삼각형을 버텍스 다수결로 클러스터 id에 배정. (F,) int32."""
    F = np.asarray(faces, dtype=np.int64)
    vc = np.asarray(vert_clusters, dtype=np.int64)
    a = vc[F[:, 0]]; b = vc[F[:, 1]]; c = vc[F[:, 2]]
    # 다수결 (2표 이상이 같으면 그 id, 아니면 a)
    out = np.where(a == b, a, np.where(a == c, a, np.where(b == c, b, a)))
    return out.astype(np.int32)


def smart_fill_holes(
    verts: np.ndarray, faces: np.ndarray, pts: np.ndarray,
    max_size_ratio: float = 0.20,
    support_radius_ratio: float = 0.07,
    min_support_points: int = 1,
    auto_fill_small_ratio: float = 0.08,
) -> Dict[str, np.ndarray]:
    """
    "공갈 구멍" 자동 메우기 — 3단계 판정:

      A. 구멍 지름이 대각선 × auto_fill_small_ratio(기본 5%) 이하면
         → 무조건 메움 (너무 작으면 거의 무조건 공갈)
      B. 대각선 × max_size_ratio(기본 15%) 이하면
         → 주변에 원본 포인트 min_support_points개 이상 있을 때만 메움
      C. 그보다 크면
         → 진짜 오픈 영역으로 간주하고 그대로 둠 (뒷면 미스캔 등)

    파라미터:
      auto_fill_small_ratio : 포인트 없어도 자동 메우는 작은 구멍 기준 (5%)
      max_size_ratio        : 포인트 있으면 메우는 최대 구멍 크기 (15%)
      support_radius_ratio  : 주변 포인트 판정 반경 (4%)
      min_support_points    : 근처에 있어야 하는 최소 포인트 수 (3개)
    """
    if len(faces) < 4 or len(pts) < 4:
        return {"verts": verts, "faces": faces}
    try:
        from scipy.spatial import cKDTree
    except ImportError:
        return {"verts": verts, "faces": faces}

    V = np.asarray(verts, dtype=np.float64)
    F = np.asarray(faces, dtype=np.int32)

    span = pts.max(0) - pts.min(0)
    diag = float(np.linalg.norm(span)) + 1e-9
    max_hole_span = diag * float(max_size_ratio)
    auto_fill_span = diag * float(auto_fill_small_ratio)
    support_rad = diag * float(support_radius_ratio)

    # ── 1. 유향 엣지 카운트 → 경계 반-엣지 (directed cnt=1, reverse 없음) ─
    from collections import defaultdict
    e_cnt: dict = defaultdict(int)
    for f in F:
        a, b, c = int(f[0]), int(f[1]), int(f[2])
        e_cnt[(a, b)] += 1
        e_cnt[(b, c)] += 1
        e_cnt[(c, a)] += 1

    bnd_next: dict = {}
    for (a, b), cnt in e_cnt.items():
        if cnt == 1 and e_cnt.get((b, a), 0) == 0:
            bnd_next[a] = b

    if not bnd_next:
        return {"verts": V.astype(np.float32), "faces": F}

    # ── 2. boundary loop 추적 ─────────────────────────────────────────
    used: set = set()
    loops: list = []
    for start in list(bnd_next.keys()):
        if start in used:
            continue
        loop = [start]
        used.add(start)
        cur = start
        for _ in range(200_000):
            nxt = bnd_next.get(cur, -1)
            if nxt == -1 or nxt == start:
                break
            if nxt in used:
                break
            used.add(nxt)
            loop.append(nxt)
            cur = nxt
        if len(loop) >= 3:
            loops.append(loop)

    if not loops:
        return {"verts": V.astype(np.float32), "faces": F}

    # ── 3. 원본 포인트 트리 ───────────────────────────────────────────
    tree = cKDTree(pts.astype(np.float64))

    # ── 4. 각 loop 판정 + 공갈이면 fan 삼각화 ───────────────────────
    new_V = list(V)
    new_F = list(F)
    filled = 0
    left = 0
    for loop in loops:
        loop_pts = V[loop]
        # 구멍 크기: 최장 두 점 거리
        mn = loop_pts.min(0); mx = loop_pts.max(0)
        hole_span = float(np.linalg.norm(mx - mn))
        c = loop_pts.mean(0)

        # ── A) 아주 작은 구멍 → 포인트 체크 없이 무조건 메움 ──
        fill = False
        if hole_span <= auto_fill_span:
            fill = True
        # ── B) 중간 크기 → 주변에 원본 포인트 있으면 메움 ──
        elif hole_span <= max_hole_span:
            cnt = tree.query_ball_point(c, r=support_rad, return_length=True)
            if cnt >= int(min_support_points):
                fill = True
        # ── C) 그보다 크면 진짜 오픈 영역 → 놔둠 ──

        if not fill:
            left += 1
            continue

        # fan triangulation (중심점 삽입 + 방사형 삼각형)
        cv_idx = len(new_V)
        new_V.append(c.astype(np.float32))
        L = len(loop)
        for i in range(L):
            a = loop[i]
            b = loop[(i + 1) % L]
            new_F.append([b, cv_idx, a])
        filled += 1

    if filled == 0:
        return {"verts": V.astype(np.float32), "faces": F}

    nV = np.asarray(new_V, dtype=np.float32)
    nF = np.asarray(new_F, dtype=np.int32)
    return {"verts": nV, "faces": nF, "_filled": filled, "_left_open": left}


def trim_far_from_points(
    verts: np.ndarray, faces: np.ndarray, pts: np.ndarray,
    max_dist_ratio: float = 0.025,
    k: int = 1,
) -> Dict[str, np.ndarray]:
    """
    원본 포인트 클라우드에서 멀리 떨어진 메쉬 영역 제거.

    Poisson이 watertight 유지하려고 포인트 없는 빈 공간에 확장시킨
    "번진 표면"(= 사용자가 본 '붕어빵 주변 굳은 반죽')을 잘라냄.

    각 메쉬 버텍스에 대해 가장 가까운 원본 포인트까지 거리를 구하고,
    대각선 × max_dist_ratio 보다 멀면 버텍스 제거 → 연결된 face도 제거.

    max_dist_ratio=0.025 (대각선의 2.5%) 정도가 균형적.
    너무 작으면(0.01) 메쉬 구멍 뚫림, 너무 크면(0.05) 확장 영역 살아남음.
    """
    if len(verts) < 4 or len(faces) < 4 or len(pts) < 4:
        return {"verts": verts, "faces": faces}
    try:
        from scipy.spatial import cKDTree
    except ImportError:
        return {"verts": verts, "faces": faces}

    span = pts.max(0) - pts.min(0)
    diag = float(np.linalg.norm(span)) + 1e-9
    max_dist = diag * float(max_dist_ratio)

    tree = cKDTree(pts.astype(np.float64))
    d, _ = tree.query(verts.astype(np.float64), k=int(max(1, k)), workers=-1)
    if k > 1:
        d = d.mean(axis=1)
    keep_v = d < max_dist

    # 세 버텍스 모두 살아있는 면만 유지
    kf = keep_v[faces[:, 0]] & keep_v[faces[:, 1]] & keep_v[faces[:, 2]]
    new_F = faces[kf]
    if len(new_F) < 4:
        # 너무 공격적 → 원본 유지
        return {"verts": verts, "faces": faces}

    used = np.zeros(len(verts), dtype=bool)
    used[new_F.flatten()] = True
    old2new = -np.ones(len(verts), dtype=np.int64)
    old2new[used] = np.arange(int(used.sum()))
    new_V = verts[used].astype(np.float32)
    new_F_r = old2new[new_F].astype(np.int32)
    return {"verts": new_V, "faces": new_F_r}


def prune_long_edges(
    verts: np.ndarray, faces: np.ndarray,
    max_edge_ratio: float = 4.0,
    abs_cap_ratio: float = 0.08,
) -> Dict[str, np.ndarray]:
    """
    긴 엣지 프루닝 — 공간 가로지르는 '실' 삼각형 제거.
    median 엣지 × max_edge_ratio 또는 대각선 × abs_cap_ratio 초과 시 제거.
    """
    if len(faces) < 4:
        return {"verts": verts, "faces": faces}
    V = verts.astype(np.float64)
    F = faces.astype(np.int64)
    e0 = np.linalg.norm(V[F[:, 1]] - V[F[:, 0]], axis=1)
    e1 = np.linalg.norm(V[F[:, 2]] - V[F[:, 1]], axis=1)
    e2 = np.linalg.norm(V[F[:, 0]] - V[F[:, 2]], axis=1)
    emax = np.maximum(np.maximum(e0, e1), e2)
    med = float(np.median(np.concatenate([e0, e1, e2])))
    if med < 1e-12:
        return {"verts": verts, "faces": faces}
    span = V.max(0) - V.min(0)
    diag = float(np.linalg.norm(span)) + 1e-9
    limit = min(med * max_edge_ratio, diag * abs_cap_ratio) if abs_cap_ratio > 0 \
            else med * max_edge_ratio
    keep = emax <= limit
    new_F = F[keep].astype(np.int32)
    if len(new_F) < 4:
        return {"verts": verts, "faces": faces}
    used = np.zeros(len(V), dtype=bool)
    used[new_F.flatten()] = True
    old2new = -np.ones(len(V), dtype=np.int64)
    old2new[used] = np.arange(int(used.sum()))
    new_V = V[used].astype(np.float32)
    new_F_r = old2new[new_F].astype(np.int32)
    return {"verts": new_V, "faces": new_F_r}


def decimate_to_target(
    verts: np.ndarray, faces: np.ndarray, target_tris: int,
) -> Dict[str, np.ndarray]:
    """Quadric decimation으로 목표 삼각형 수까지 간단화. Open3D → trimesh 폴백."""
    if target_tris <= 0 or len(faces) <= target_tris:
        return {"verts": verts, "faces": faces}
    try:
        import open3d as o3d
        m = o3d.geometry.TriangleMesh(
            o3d.utility.Vector3dVector(verts.astype(np.float64)),
            o3d.utility.Vector3iVector(faces.astype(np.int32)),
        )
        out = m.simplify_quadric_decimation(int(max(100, target_tris)))
        return {
            "verts": np.asarray(out.vertices, dtype=np.float32),
            "faces": np.asarray(out.triangles, dtype=np.int32),
        }
    except Exception:
        pass
    try:
        import trimesh
        m = trimesh.Trimesh(vertices=verts, faces=faces, process=False)
        if hasattr(m, "simplify_quadric_decimation"):
            out = m.simplify_quadric_decimation(int(target_tris))
            return {
                "verts": np.asarray(out.vertices, dtype=np.float32),
                "faces": np.asarray(out.faces, dtype=np.int32),
            }
    except Exception:
        pass
    return {"verts": verts, "faces": faces}


def keep_largest_components(
    verts: np.ndarray, faces: np.ndarray, min_ratio: float = 0.02,
) -> Dict[str, np.ndarray]:
    """가장 큰 컴포넌트 + min_ratio 이상 유지. 파편 제거용."""
    try:
        import trimesh
        m = trimesh.Trimesh(vertices=verts, faces=faces, process=True)
        comps = m.split(only_watertight=False)
        if len(comps) <= 1:
            return {"verts": np.asarray(m.vertices, dtype=np.float32),
                    "faces": np.asarray(m.faces, dtype=np.int32)}
        comps = sorted(comps, key=lambda c: len(c.faces), reverse=True)
        cutoff = max(int(len(comps[0].faces) * min_ratio), 8)
        kept = [c for c in comps if len(c.faces) >= cutoff] or [comps[0]]
        merged = trimesh.util.concatenate(kept)
        return {"verts": np.asarray(merged.vertices, dtype=np.float32),
                "faces": np.asarray(merged.faces, dtype=np.int32)}
    except Exception:
        return {"verts": verts, "faces": faces}


def voxel_remesh(
    verts: np.ndarray,
    faces: np.ndarray,
    resolution: int = 60,
    fill_interior: bool = True,
) -> Dict[str, np.ndarray]:
    """
    축-정렬(XYZ) 복셀 리메시 — Instant Meshes 스타일 격자 토폴로지.

    원리:
      1. 메쉬를 대각선/resolution 픽치의 복셀 그리드로 복셀화
      2. 속 채우기(fill) — 쉘만 있으면 마칭큐브 결과가 얇아짐
      3. marching_cubes로 표면 재추출 → 자동으로 XYZ 축에 정렬된 엣지

    resolution 클수록 디테일, 작을수록 Lego 블록 느낌.
    Poisson 같은 Voronoi식 무작위 토폴로지 대신 격자식 일관된 토폴로지.
    """
    try:
        import trimesh
    except ImportError:
        return {"verts": verts, "faces": faces}

    m = trimesh.Trimesh(
        vertices=np.asarray(verts, dtype=np.float64),
        faces=np.asarray(faces, dtype=np.int64),
        process=True,
    )
    if len(m.faces) < 8:
        return {"verts": verts, "faces": faces}

    span = m.bounds[1] - m.bounds[0]
    diag = float(np.linalg.norm(span)) + 1e-9
    pitch = diag / float(max(12, min(256, resolution)))

    try:
        vox = m.voxelized(pitch=pitch)
    except Exception:
        return {"verts": verts, "faces": faces}

    if fill_interior:
        try:
            vox = vox.fill()
        except Exception:
            pass

    try:
        out = vox.marching_cubes
    except Exception:
        return {"verts": verts, "faces": faces}

    V = np.asarray(out.vertices, dtype=np.float32)
    F = np.asarray(out.faces, dtype=np.int32)
    if len(F) < 4:
        return {"verts": verts, "faces": faces}
    return {"verts": V, "faces": F}


def remesh_uniform(
    verts: np.ndarray,
    faces: np.ndarray,
    target_edge_ratio: float = 1.0,
    max_iters: int = 3,
) -> Dict[str, np.ndarray]:
    """
    isotropic remesh — 삼각형 크기를 균일하게.

    긴 엣지는 subdivide, 짧은 엣지는 collapse를 반복해 평균 엣지 길이에 수렴.
    target_edge_ratio: 평균 엣지의 몇 배를 목표로 할지 (1.0=평균, 0.8=더 촘촘).
    """
    try:
        import trimesh
    except ImportError:
        return {"verts": verts, "faces": faces}

    m = trimesh.Trimesh(vertices=verts, faces=faces, process=True)
    if len(m.faces) < 8:
        return {"verts": verts, "faces": faces}

    # 평균 엣지 길이 기준
    edges = m.edges_unique
    if len(edges) == 0:
        return {"verts": verts, "faces": faces}
    e_lengths = np.linalg.norm(
        m.vertices[edges[:, 0]] - m.vertices[edges[:, 1]], axis=1,
    )
    target = float(np.median(e_lengths)) * float(target_edge_ratio)
    if target < 1e-9:
        return {"verts": verts, "faces": faces}

    # trimesh의 subdivide_to_size: max_edge 이상의 엣지를 잘라 세분화
    # 여러 번 호출해서 평균 엣지 길이가 target 근처에 오게 유도
    try:
        for _ in range(int(max(1, max_iters))):
            V2, F2 = trimesh.remesh.subdivide_to_size(
                m.vertices, m.faces, max_edge=target * 1.5,
            )
            m = trimesh.Trimesh(vertices=V2, faces=F2, process=True)
    except Exception:
        pass

    return {
        "verts": np.asarray(m.vertices, dtype=np.float32),
        "faces": np.asarray(m.faces, dtype=np.int32),
    }


def build_mc_mesh(pts: np.ndarray, grid_res: int = 50) -> Dict[str, np.ndarray]:
    """
    Build a surface mesh from an unordered point cloud using Marching Cubes.
    Uses skimage if available, otherwise uses trimesh.
    Returns {"verts": float32 (V,3), "faces": int32 (F,3)}
    """
    mn = pts.min(axis=0)
    mx = pts.max(axis=0)
    span = mx - mn
    pad = span * 0.05 + 1e-6
    mn -= pad
    mx += pad
    span = mx - mn

    GS = int(grid_res)
    cell = span / GS                           # (3,) cell sizes

    # ── Vectorised splat: each point → nearby voxels ─────────────────────
    grid = np.zeros((GS, GS, GS), dtype=np.float32)
    r = 1.5                                    # splat radius in voxels

    idx = np.clip(((pts - mn) / cell).astype(np.int32), 0, GS - 1)

    offsets = []
    for dx in range(-2, 3):
        for dy in range(-2, 3):
            for dz in range(-2, 3):
                d = (dx * dx + dy * dy + dz * dz) ** 0.5
                if d < r:
                    offsets.append((dx, dy, dz, float(1.0 - d * d / (r * r))))

    for dx, dy, dz, w in offsets:
        ix = np.clip(idx[:, 0] + dx, 0, GS - 1)
        iy = np.clip(idx[:, 1] + dy, 0, GS - 1)
        iz = np.clip(idx[:, 2] + dz, 0, GS - 1)
        np.maximum.at(grid, (ix, iy, iz), w)

    # ── Marching Cubes ────────────────────────────────────────────────────
    try:
        from skimage.measure import marching_cubes
        verts, faces, _, _ = marching_cubes(
            grid, level=0.3,
            spacing=(float(cell[0]), float(cell[1]), float(cell[2]))
        )
        verts = (verts + mn).astype(np.float32)
        faces = faces.astype(np.int32)
    except ImportError:
        # Fallback: use trimesh's built-in MC
        try:
            import trimesh
            mesh = trimesh.voxel.ops.matrix_to_marching_cubes(grid > 0.3)
            # scale back to world space
            verts = (np.array(mesh.vertices, dtype=np.float32) * cell + mn)
            faces = np.array(mesh.faces, dtype=np.int32)
        except Exception as e:
            raise RuntimeError(f"Marching Cubes 실패: {e}. pip install scikit-image 또는 trimesh")

    if len(faces) < 3:
        raise ValueError("MC: 충분한 surface를 생성하지 못했습니다. 해상도를 높이거나 포인트를 확인하세요.")

    return {"verts": verts, "faces": faces}


# ══════════════════════════════════════════════════════════════════════════════
# 3. Geometry Validation
# ══════════════════════════════════════════════════════════════════════════════
def validate(verts: np.ndarray, faces: np.ndarray) -> Dict[str, Any]:
    """
    Returns geometry health stats for Instant Meshes compatibility check.
    Criteria (실전 컷라인):
      - watertight          : boundary_edges == 0
      - manifold            : non_manifold_edges == 0
      - components          : == 1
      - normal_consistency  : >= 0.95
    """
    V = len(verts)
    F = len(faces)

    # Build edge → face list
    edge_faces: Dict[tuple, list] = {}
    for i in range(F):
        for k in range(3):
            a = int(faces[i, k])
            b = int(faces[i, (k + 1) % 3])
            key = (min(a, b), max(a, b))
            if key not in edge_faces:
                edge_faces[key] = []
            edge_faces[key].append(i)

    boundary_edges = sum(1 for v in edge_faces.values() if len(v) == 1)
    non_manifold_edges = sum(1 for v in edge_faces.values() if len(v) > 2)

    # Face adjacency for connected-components BFS
    face_adj: list[list[int]] = [[] for _ in range(F)]
    for val in edge_faces.values():
        if len(val) == 2:
            face_adj[val[0]].append(val[1])
            face_adj[val[1]].append(val[0])

    visited = np.zeros(F, dtype=bool)
    comp_of = np.full(F, -1, dtype=np.int32)
    comp_sizes: list[int] = []
    for start in range(F):
        if visited[start]:
            continue
        ci = len(comp_sizes)
        stack = [start]
        sz = 0
        visited[start] = True
        while stack:
            f = stack.pop()
            comp_of[f] = ci
            sz += 1
            for nb in face_adj[f]:
                if not visited[nb]:
                    visited[nb] = True
                    stack.append(nb)
        comp_sizes.append(sz)

    # Normal consistency: adjacent faces should have opposite edge winding
    consistent = 0
    total_pairs = 0
    for (a, b), fl in edge_faces.items():
        if len(fl) != 2:
            continue
        total_pairs += 1
        f0, f1 = fl
        # direction in f0
        d0 = 0
        for k in range(3):
            if faces[f0, k] == a and faces[f0, (k + 1) % 3] == b:
                d0 = 1; break
            if faces[f0, k] == b and faces[f0, (k + 1) % 3] == a:
                d0 = -1; break
        d1 = 0
        for k in range(3):
            if faces[f1, k] == a and faces[f1, (k + 1) % 3] == b:
                d1 = 1; break
            if faces[f1, k] == b and faces[f1, (k + 1) % 3] == a:
                d1 = -1; break
        if d0 != 0 and d1 != 0 and d0 != d1:
            consistent += 1

    nc = consistent / total_pairs if total_pairs > 0 else 1.0
    largest = max(comp_sizes) if comp_sizes else F

    return {
        "V": V,
        "F": F,
        "watertight": boundary_edges == 0,
        "boundary_edges": boundary_edges,
        "non_manifold_edges": non_manifold_edges,
        "components": len(comp_sizes),
        "largest_component": largest,
        "normal_consistency": round(nc, 4),
        # Pass/fail summary for frontend
        "pass": (boundary_edges == 0 and non_manifold_edges == 0
                 and len(comp_sizes) == 1 and nc >= 0.95),
        # For internal use
        "_edge_faces": edge_faces,
        "_face_adj": face_adj,
        "_comp_of": comp_of.tolist(),
        "_comp_sizes": comp_sizes,
    }


# ══════════════════════════════════════════════════════════════════════════════
# 4. Mesh Repair
# ══════════════════════════════════════════════════════════════════════════════
def repair(verts: np.ndarray, faces: np.ndarray,
           val: Dict[str, Any] | None = None) -> Dict[str, np.ndarray]:
    """
    Repair pipeline (matches user spec):
      1. Remove non-manifold triangles
      2. Keep largest connected component  (파편 제거)
      3. Fix winding / normals (BFS propagation)
      4. Fill boundary holes              (구멍 메우기)
    """
    # Prefer trimesh for robust repair when available
    try:
        return _repair_trimesh(verts, faces)
    except ImportError:
        return _repair_manual(verts, faces, val)


def _repair_trimesh(verts: np.ndarray, faces: np.ndarray) -> Dict[str, np.ndarray]:
    import trimesh

    # process=True는 trimesh 4.x에서 merge_vertices / remove_degenerate_faces /
    # remove_duplicate_faces 를 자동 처리 (구버전 repair 모듈 함수가 4.x에서 제거됨)
    m = trimesh.Trimesh(vertices=verts, faces=faces, process=True)

    # trimesh <4.0 에는 모듈 수준 함수가 있었으나 4.x에서 제거 → try/except 대응
    for fn_name in ("remove_degenerate_faces", "remove_duplicate_faces"):
        fn = getattr(trimesh.repair, fn_name, None)
        if callable(fn):
            try:
                fn(m)
            except Exception:
                pass

    # Keep largest component
    components = m.split(only_watertight=False)
    if len(components) > 1:
        m = max(components, key=lambda c: len(c.faces))

    # Fix winding + normals + fill holes (trimesh 4.x에서도 유지되는 API)
    for fn_name in ("fix_winding", "fix_normals", "fill_holes"):
        fn = getattr(trimesh.repair, fn_name, None)
        if callable(fn):
            try:
                fn(m)
            except Exception:
                pass

    return {
        "verts": np.array(m.vertices, dtype=np.float32),
        "faces": np.array(m.faces, dtype=np.int32),
    }


def _repair_manual(verts: np.ndarray, faces: np.ndarray,
                   val: Dict[str, Any] | None = None) -> Dict[str, np.ndarray]:
    """Pure NumPy fallback when trimesh is not installed."""
    if val is None:
        val = validate(verts, faces)

    F = len(faces)
    V = len(verts)
    edge_faces = val.get("_edge_faces", {})
    face_adj = val.get("_face_adj", [[] for _ in range(F)])
    comp_of = val.get("_comp_of", list(range(F)))
    comp_sizes = val.get("_comp_sizes", [F])

    removed = np.zeros(F, dtype=bool)

    # 1. Remove non-manifold faces (keep first 2 per edge)
    for fl in edge_faces.values():
        if len(fl) > 2:
            for fi in fl[2:]:
                removed[fi] = True

    # 2. Keep largest component
    if len(comp_sizes) > 1:
        max_ci = int(np.argmax(comp_sizes))
        for i in range(F):
            if not removed[i] and comp_of[i] != max_ci:
                removed[i] = True

    # 3. Rebuild kept triangles
    kept = faces[~removed]

    # 4. BFS winding propagation
    nF = len(kept)
    edge_dir: Dict[tuple, list] = {}
    for i in range(nF):
        for k in range(3):
            a, b = int(kept[i, k]), int(kept[i, (k + 1) % 3])
            key = (min(a, b), max(a, b))
            if key not in edge_dir:
                edge_dir[key] = []
            edge_dir[key].append((i, a, b))

    nb2: list[list] = [[] for _ in range(nF)]
    for (ea, eb), ents in edge_dir.items():
        if len(ents) == 2:
            (i0, a0, b0), (i1, a1, b1) = ents
            nb2[i0].append((i1, a0, b0, a1, b1))
            nb2[i1].append((i0, a1, b1, a0, b0))

    flipped = np.zeros(nF, dtype=bool)
    vis = np.zeros(nF, dtype=bool)
    for seed in range(nF):
        if vis[seed]:
            continue
        vis[seed] = True
        q = [seed]
        while q:
            f = q.pop()
            for (nb, ea, eb, na, nb_) in nb2[f]:
                if vis[nb]:
                    continue
                vis[nb] = True
                # find winding of shared edge in f (accounting for flip)
                fd = 0
                fa, fb, fc = int(kept[f, 0]), int(kept[f, 1]), int(kept[f, 2])
                if flipped[f]:
                    fa, fb = fb, fa
                for u, v in [(fa, fb), (fb, fc), (fc, fa)]:
                    if u == ea and v == eb:
                        fd = 1; break
                    if u == eb and v == ea:
                        fd = -1; break
                nd = 0
                na_, nb_2, nc_ = int(kept[nb, 0]), int(kept[nb, 1]), int(kept[nb, 2])
                for u, v in [(na_, nb_2), (nb_2, nc_), (nc_, na_)]:
                    if u == na and v == nb_:
                        nd = 1; break
                    if u == nb_ and v == na:
                        nd = -1; break
                if fd != 0 and nd != 0 and fd == nd:
                    flipped[nb] = True
                q.append(nb)

    result = kept.copy()
    result[flipped] = result[flipped][:, [0, 2, 1]]  # swap b↔c

    # 5. Compact vertices
    used = np.zeros(V, dtype=bool)
    for vi in result.flatten():
        used[vi] = True
    old_to_new = np.full(V, -1, dtype=np.int32)
    new_idx = 0
    for i in range(V):
        if used[i]:
            old_to_new[i] = new_idx
            new_idx += 1
    new_verts = verts[used]
    new_faces = old_to_new[result]

    # 6. Fill holes (fan triangulation)
    new_verts, new_faces = _fill_holes(new_verts, new_faces)

    return {
        "verts": new_verts.astype(np.float32),
        "faces": new_faces.astype(np.int32),
    }


def _fill_holes(verts: np.ndarray, faces: np.ndarray):
    """Fan-triangulate boundary loops."""
    F = len(faces)
    # Count directed edges
    edge_cnt: Dict[tuple, int] = {}
    for i in range(F):
        for k in range(3):
            a, b = int(faces[i, k]), int(faces[i, (k + 1) % 3])
            edge_cnt[(a, b)] = edge_cnt.get((a, b), 0) + 1

    # Boundary half-edges: a→b exists but b→a does not
    bnd_next: Dict[int, int] = {}
    for (a, b), cnt in edge_cnt.items():
        if cnt == 1 and edge_cnt.get((b, a), 0) == 0:
            bnd_next[a] = b

    if not bnd_next:
        return verts, faces

    used_v: set = set()
    loops: list[list[int]] = []
    for start in bnd_next:
        if start in used_v:
            continue
        loop = [start]
        used_v.add(start)
        cur = start
        for _ in range(100_000):
            nxt = bnd_next.get(cur, -1)
            if nxt == -1 or nxt == start:
                break
            if nxt in used_v and nxt != start:
                break
            used_v.add(nxt)
            loop.append(nxt)
            cur = nxt
        if len(loop) >= 3:
            loops.append(loop)

    if not loops:
        return verts, faces

    new_verts = list(verts)
    new_faces = list(faces)

    for loop in loops:
        cx = np.mean([new_verts[vi][0] for vi in loop])
        cy = np.mean([new_verts[vi][1] for vi in loop])
        cz = np.mean([new_verts[vi][2] for vi in loop])
        cvi = len(new_verts)
        new_verts.append(np.array([cx, cy, cz], dtype=np.float32))
        for i in range(len(loop)):
            a = loop[i]
            b = loop[(i + 1) % len(loop)]
            new_faces.append(np.array([b, cvi, a], dtype=np.int32))

    return np.array(new_verts, dtype=np.float32), np.array(new_faces, dtype=np.int32)


# ══════════════════════════════════════════════════════════════════════════════
# 5. Laplacian Smooth
# ══════════════════════════════════════════════════════════════════════════════
def laplacian_smooth(verts: np.ndarray, faces: np.ndarray,
                     iterations: int = 2, lam: float = 0.5) -> np.ndarray:
    """Uniform Laplacian smoothing using sparse matrix multiply."""
    try:
        from scipy.sparse import lil_matrix, diags
        V = len(verts)
        adj = lil_matrix((V, V), dtype=np.float32)
        for f in faces:
            for k in range(3):
                a, b = int(f[k]), int(f[(k + 1) % 3])
                adj[a, b] = 1.0
                adj[b, a] = 1.0
        adj = adj.tocsr()
        deg = np.array(adj.sum(axis=1)).flatten()
        deg[deg == 0] = 1.0
        D_inv = diags(1.0 / deg)
        L = D_inv @ adj             # row-normalized adjacency

        v = verts.copy()
        for _ in range(iterations):
            v = v + lam * (L @ v - v)
        return v.astype(np.float32)

    except ImportError:
        # Fallback: pure numpy (slower)
        V = len(verts)
        v = verts.copy()
        for _ in range(iterations):
            new_v = v.copy()
            deg = np.zeros(V, dtype=np.int32)
            acc = np.zeros_like(v)
            for f in faces:
                for k in range(3):
                    a, b = int(f[k]), int(f[(k + 1) % 3])
                    acc[a] += v[b]
                    deg[a] += 1
            mask = deg > 0
            new_v[mask] = v[mask] + lam * (acc[mask] / deg[mask, None] - v[mask])
            v = new_v
        return v.astype(np.float32)


# ══════════════════════════════════════════════════════════════════════════════
# 6. Triangles → Quads (인접·공면 삼각형 쌍을 사각형으로 병합)
# ══════════════════════════════════════════════════════════════════════════════
def triangles_to_quads(
    verts: np.ndarray,
    faces: np.ndarray,
    coplanar_thresh: float = 0.98,   # 법선 내적 임계값 (1에 가까울수록 엄격)
    max_quad_ratio: float  = 2.5,    # 사각형 대각선 비율 상한 (왜곡 방지)
) -> Dict[str, Any]:
    """
    인접한 삼각형 쌍 중 공면이고 합리적인 형태를 가진 것을 사각형으로 병합.
    Returns {"quads": list[[a,b,c,d]], "triangles": list[[a,b,c]]}
    """
    V = len(verts)
    F = len(faces)

    # ── 면 법선 계산 ─────────────────────────────────────────────────────
    v0 = verts[faces[:, 0]]
    v1 = verts[faces[:, 1]]
    v2 = verts[faces[:, 2]]
    nrm = np.cross(v1 - v0, v2 - v0).astype(np.float64)
    lengths = np.linalg.norm(nrm, axis=1, keepdims=True)
    lengths = np.where(lengths < 1e-12, 1e-12, lengths)
    nrm /= lengths                                           # (F, 3) 단위 법선

    # ── 엣지 → 면 인덱스 맵 (비방향성) ──────────────────────────────────
    edge_to_faces: Dict[tuple, list] = {}
    for fi in range(F):
        for k in range(3):
            a = int(faces[fi, k])
            b = int(faces[fi, (k + 1) % 3])
            key = (min(a, b), max(a, b))
            if key not in edge_to_faces:
                edge_to_faces[key] = []
            edge_to_faces[key].append(fi)

    used = np.zeros(F, dtype=bool)
    quads: list = []
    triangles_left: list = []

    for key, flist in edge_to_faces.items():
        if len(flist) != 2:
            continue
        fi, fj = flist
        if used[fi] or used[fj]:
            continue

        # ── 공면 확인 ──────────────────────────────────────────────────
        dot = float(np.dot(nrm[fi], nrm[fj]))
        if dot < coplanar_thresh:
            continue

        # ── 4개 고유 버텍스 식별 ────────────────────────────────────────
        vi = set(int(x) for x in faces[fi])
        vj = set(int(x) for x in faces[fj])
        shared = vi & vj          # 공유 버텍스 2개
        ua_set = vi - shared      # 면 fi의 고유 버텍스 1개
        ub_set = vj - shared      # 면 fj의 고유 버텍스 1개
        if len(shared) != 2 or len(ua_set) != 1 or len(ub_set) != 1:
            continue

        ua = list(ua_set)[0]
        ub = list(ub_set)[0]

        # ── 사각형 꼭짓점 순서 결정 (CCW 와인딩 유지) ───────────────────
        # BUG FIX v2: winding consistency + convex fan 검증.
        #
        # Triangle fi CCW = [v0, v1, v2] → ua의 cyclic next=sa, next-next=sb.
        # Consistent manifold mesh는 fi-fj 공유 엣지가 반대 방향.
        # 즉 fi에 sa→sb 엣지 있으면 fj에는 sb→sa 엣지가 있어야 함.
        # 이 경우 fj의 vertex order에서 sb→sa 다음 vertex가 ub.
        # Quad CCW: ua → sa → ub → sb (fi의 winding 보존).
        #
        # 만약 fj에 sa→sb 같은 방향이 있으면(winding inconsistent),
        # 위 순서로 quad를 만들면 bowtie(self-intersect)가 됨.
        # → 그런 pair는 quad 변환 skip, tri로 유지.
        fi_verts = [int(faces[fi, k]) for k in range(3)]
        try:
            i_ua = fi_verts.index(ua)
        except ValueError:
            continue
        sa = fi_verts[(i_ua + 1) % 3]   # ua 다음
        sb = fi_verts[(i_ua + 2) % 3]   # sa 다음 (= ua 이전)
        if sa not in shared or sb not in shared:
            continue

        # fj에서 sb→sa 엣지가 있어야 winding consistent
        fj_verts = [int(faces[fj, k]) for k in range(3)]
        j_sb = fj_verts.index(sb)
        fj_next_of_sb = fj_verts[(j_sb + 1) % 3]
        if fj_next_of_sb != sa:
            # fj에서 sb 다음이 sa가 아니면 winding inconsistent → skip
            continue

        # fan-triangulate 검증: [ua,sa,ub] + [ua,ub,sb] 두 tri의 normal이 같은 방향인가?
        # 같지 않으면 bowtie (self-intersecting quad).
        p_ua = verts[ua]; p_sa = verts[sa]; p_ub = verts[ub]; p_sb = verts[sb]
        n_tri1 = np.cross(p_sa - p_ua, p_ub - p_ua)
        n_tri2 = np.cross(p_ub - p_ua, p_sb - p_ua)
        len1 = float(np.linalg.norm(n_tri1))
        len2 = float(np.linalg.norm(n_tri2))
        if len1 < 1e-12 or len2 < 1e-12:
            continue
        if float(np.dot(n_tri1, n_tri2)) / (len1 * len2) < 0.5:
            # Bowtie — 두 내부 tri의 normal이 어긋남
            continue

        # BUG FIX v3: 원본 fi의 normal 방향이 CCW 기준 정방향인지 검증.
        # Marching Cubes/Taubin/repair 후에도 일부 face가 뒤집혀 있을 수 있음.
        # → 원본 fi의 face normal과 quad tri1 normal이 반대 방향이면
        #   quad winding을 반전해서 내보내야 렌더 일관성 확보.
        fi_p0 = verts[fi_verts[0]]
        fi_p1 = verts[fi_verts[1]]
        fi_p2 = verts[fi_verts[2]]
        n_fi = np.cross(fi_p1 - fi_p0, fi_p2 - fi_p0)
        # n_fi가 pre-computed nrm[fi]와 같은지 sanity check 불필요 (정의상 동일)
        if float(np.dot(n_tri1, n_fi)) < 0:
            # fi가 CW winding → quad도 반전 (sa ↔ sb swap)
            a_order = [ua, sb, ub, sa]
        else:
            a_order = [ua, sa, ub, sb]

        # ── 형태 검사: 대각선 비율로 왜곡된 사각형 걸러냄 ───────────────
        pa = verts[ua]; pb = verts[sa]; pc = verts[ub]; pd = verts[sb]
        d1 = float(np.linalg.norm(pa - pc))   # 대각선 1
        d2 = float(np.linalg.norm(pb - pd))   # 대각선 2
        if d2 < 1e-12 or d1 / d2 > max_quad_ratio or d2 / max(d1, 1e-12) > max_quad_ratio:
            continue

        quads.append(a_order)
        used[fi] = True
        used[fj] = True

    # 미변환 삼각형
    for fi in range(F):
        if not used[fi]:
            triangles_left.append([int(faces[fi, 0]), int(faces[fi, 1]), int(faces[fi, 2])])

    return {"quads": quads, "triangles": triangles_left}
