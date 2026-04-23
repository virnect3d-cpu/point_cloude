"""
메쉬 콜라이더 생성 — page 2용.

포인트 클라우드에 실제로 '착 붙는' 콜라이더 메쉬를 생성.
단순 AABB나 클러스터별 Convex Hull 대신:

  1) BPA 또는 Poisson으로 실제 표면 메쉬 생성
  2) 간단화 (quadric decimation)
  3) 필요시 ACD로 여러 convex 파트로 분해 (Unity 동적 물체용)

반환 스키마:
{
  "mode": "mesh" | "convex_parts",
  "parts": [
    {"vertices": [[x,y,z],...], "triangles": [[a,b,c],...]}, ...
  ],
  "verts_total": int,
  "tris_total": int,
}

mode="mesh"       : 단일 파트, Unity MeshCollider (convex=false, 정적만)
mode="convex_parts": 여러 convex 파트, 동적 rigidbody에도 사용 가능
"""
from __future__ import annotations

import numpy as np
from typing import Dict, List, Optional


def _ensure_pcd(pts: np.ndarray, normals: Optional[np.ndarray]):
    import open3d as o3d
    pcd = o3d.geometry.PointCloud()
    pcd.points = o3d.utility.Vector3dVector(pts.astype(np.float64))
    span = pts.max(axis=0) - pts.min(axis=0)
    diag = float(np.linalg.norm(span)) + 1e-9
    if normals is not None and len(normals) == len(pts) and normals.shape[1] == 3:
        n = normals.astype(np.float64)
        ln = np.linalg.norm(n, axis=1, keepdims=True)
        ln = np.maximum(ln, 1e-12)
        pcd.normals = o3d.utility.Vector3dVector(n / ln)
    else:
        pcd.estimate_normals(
            search_param=o3d.geometry.KDTreeSearchParamHybrid(
                radius=max(diag * 0.02, 1e-6), max_nn=40,
            )
        )
        try:
            pcd.orient_normals_consistent_tangent_plane(20)
        except Exception:
            pass
    return pcd, diag


def _reconstruct_surface(
    pts: np.ndarray,
    normals: Optional[np.ndarray],
    method: str = "poisson",
    depth: int = 8,
    density_trim: float = 0.08,
) -> Dict[str, np.ndarray]:
    """포인트 → 표면 메쉬. method: 'poisson' | 'bpa'.

    density_trim: Poisson 밀도 하위 비율(0~0.5) 제거. 0.08이면 하위 8% 제거.
    클수록 오목·구멍 영역 브리징 억제, 너무 크면 정상 표면도 잘림.
    """
    import open3d as o3d

    pcd, diag = _ensure_pcd(pts, normals)

    if method == "bpa":
        dists = np.asarray(pcd.compute_nearest_neighbor_distance())
        avg = float(np.mean(dists)) if dists.size else diag * 0.001
        avg = max(avg, 1e-9)
        radii = o3d.utility.DoubleVector(
            [avg * 0.8, avg * 1.2, avg * 2.0, avg * 3.5, avg * 6.0]
        )
        m = o3d.geometry.TriangleMesh.create_from_point_cloud_ball_pivoting(pcd, radii)
    else:  # poisson
        m, dens = o3d.geometry.TriangleMesh.create_from_point_cloud_poisson(
            pcd, depth=int(max(6, min(11, depth))), width=0, scale=1.0, linear_fit=True,
        )
        if len(dens) and density_trim > 0:
            d = np.asarray(dens)
            cut = np.quantile(d, float(min(0.5, density_trim)))
            m.remove_vertices_by_mask(d <= cut)

    m.remove_duplicated_vertices()
    m.remove_duplicated_triangles()
    m.remove_degenerate_triangles()
    m.remove_unreferenced_vertices()
    if len(m.triangles) < 1:
        raise ValueError("표면 재구성 결과가 비었습니다")
    return {
        "verts": np.asarray(m.vertices, dtype=np.float32),
        "faces": np.asarray(m.triangles, dtype=np.int32),
    }


def _prune_long_edges(
    verts: np.ndarray,
    faces: np.ndarray,
    max_edge_ratio: float = 4.0,
    abs_cap_ratio: float = 0.08,
) -> Dict[str, np.ndarray]:
    """
    긴 엣지 프루닝 — Poisson/BPA의 "공간을 가로지르는 실" 삼각형 제거.

    각 삼각형의 최대 엣지가
      max(median_edge * max_edge_ratio, diag * abs_cap_ratio)
    보다 길면 해당 삼각형 제거.

    max_edge_ratio : median 엣지의 몇 배까지 허용할지 (3~5가 보통)
    abs_cap_ratio  : 절대 상한 (대각선의 몇 %). 전체가 희박해도 다리 안 만들게
    """
    if len(faces) < 4:
        return {"verts": verts, "faces": faces}

    V = verts.astype(np.float64)
    F = faces.astype(np.int64)

    # 삼각형 3 엣지 길이
    e0 = np.linalg.norm(V[F[:, 1]] - V[F[:, 0]], axis=1)
    e1 = np.linalg.norm(V[F[:, 2]] - V[F[:, 1]], axis=1)
    e2 = np.linalg.norm(V[F[:, 0]] - V[F[:, 2]], axis=1)
    emax = np.maximum(np.maximum(e0, e1), e2)

    # 모든 엣지 길이의 중앙값
    all_edges = np.concatenate([e0, e1, e2])
    med = float(np.median(all_edges)) if all_edges.size else 1.0
    if med < 1e-12:
        med = float(np.mean(all_edges)) if all_edges.size else 1.0
        if med < 1e-12:
            return {"verts": verts, "faces": faces}

    span = V.max(0) - V.min(0)
    diag = float(np.linalg.norm(span)) + 1e-9

    limit_rel = med * float(max_edge_ratio)
    limit_abs = diag * float(abs_cap_ratio)
    limit = min(limit_rel, limit_abs) if abs_cap_ratio > 0 else limit_rel

    keep = emax <= limit
    new_faces = F[keep].astype(np.int32)

    if len(new_faces) < 4:
        # 너무 공격적 → 원본 유지
        return {"verts": verts, "faces": faces}

    # 고립된 버텍스 제거
    used = np.zeros(len(V), dtype=bool)
    used[new_faces.flatten()] = True
    old2new = -np.ones(len(V), dtype=np.int64)
    old2new[used] = np.arange(int(used.sum()))
    new_verts = V[used].astype(np.float32)
    new_faces_r = old2new[new_faces].astype(np.int32)
    return {"verts": new_verts, "faces": new_faces_r}


def _keep_largest_components(
    verts: np.ndarray, faces: np.ndarray, min_ratio: float = 0.02,
) -> Dict[str, np.ndarray]:
    """파편 정리 — 가장 큰 컴포넌트 + 상대 크기 min_ratio 이상 컴포넌트 유지."""
    try:
        import trimesh
        m = trimesh.Trimesh(vertices=verts, faces=faces, process=True)
        comps = m.split(only_watertight=False)
        if len(comps) <= 1:
            return {"verts": np.asarray(m.vertices, dtype=np.float32),
                    "faces": np.asarray(m.faces, dtype=np.int32)}
        comps = sorted(comps, key=lambda c: len(c.faces), reverse=True)
        big = comps[0]
        cutoff = max(int(len(big.faces) * min_ratio), 8)
        kept = [c for c in comps if len(c.faces) >= cutoff]
        if not kept:
            kept = [big]
        merged = trimesh.util.concatenate(kept)
        return {"verts": np.asarray(merged.vertices, dtype=np.float32),
                "faces": np.asarray(merged.faces, dtype=np.int32)}
    except Exception:
        return {"verts": verts, "faces": faces}


def _decimate(verts: np.ndarray, faces: np.ndarray, target: int) -> Dict[str, np.ndarray]:
    """메쉬 단순화 (quadric decimation). target = 목표 삼각형 수."""
    try:
        import open3d as o3d
        m = o3d.geometry.TriangleMesh(
            o3d.utility.Vector3dVector(verts.astype(np.float64)),
            o3d.utility.Vector3iVector(faces.astype(np.int32)),
        )
        out = m.simplify_quadric_decimation(int(max(100, target)))
        return {
            "verts": np.asarray(out.vertices, dtype=np.float32),
            "faces": np.asarray(out.triangles, dtype=np.int32),
        }
    except Exception:
        return {"verts": verts.astype(np.float32), "faces": faces.astype(np.int32)}


def _icp_snap(verts: np.ndarray, pts: np.ndarray, iters: int = 3) -> np.ndarray:
    """메쉬 버텍스를 원본 포인트 쪽으로 당김 (정합 향상)."""
    try:
        from scipy.spatial import cKDTree
    except ImportError:
        return verts
    if len(pts) < 8 or len(verts) < 1:
        return verts
    span = pts.max(axis=0) - pts.min(axis=0)
    diag = float(np.linalg.norm(span)) + 1e-9
    tree = cKDTree(pts.astype(np.float64))
    v = verts.astype(np.float64).copy()
    k = 6
    for _ in range(iters):
        d, idx = tree.query(v, k=k, workers=-1)
        w = np.exp(-(d ** 2) / (2.0 * (diag * 0.006) ** 2))
        w /= np.maximum(w.sum(axis=1, keepdims=True), 1e-12)
        tgt = np.einsum("vk,vkd->vd", w, pts[idx].astype(np.float64))
        v += 0.55 * (tgt - v)
    return v.astype(np.float32)


def _decompose_into_convex_parts(
    verts: np.ndarray, faces: np.ndarray, max_parts: int = 16,
) -> List[Dict[str, np.ndarray]]:
    """
    ACD — trimesh + vhacd 가용 시 사용, 없으면 단일 메쉬 반환.

    반환: [{"vertices": (V,3), "triangles": (F,3)}, ...]
    """
    try:
        import trimesh  # noqa: F401
        m = _try_trimesh_vhacd(verts, faces, max_parts)
        if m is not None:
            return m
    except ImportError:
        pass

    # 폴백: 연결 성분만 분리 (항상 가능)
    return _split_components(verts, faces)


def _try_trimesh_vhacd(
    verts: np.ndarray, faces: np.ndarray, max_parts: int,
) -> Optional[List[Dict[str, np.ndarray]]]:
    """trimesh.decomposition.convex_decomposition 시도.
    V-HACD 바이너리가 PATH에 있어야 성공, 아니면 None 반환."""
    try:
        import trimesh
        m = trimesh.Trimesh(vertices=verts, faces=faces, process=True)
        parts = trimesh.decomposition.convex_decomposition(m, maxhulls=int(max_parts))
        if parts is None:
            return None
        if isinstance(parts, trimesh.Trimesh):
            parts = [parts]
        out: List[Dict[str, np.ndarray]] = []
        for p in parts:
            out.append({
                "vertices": np.asarray(p.vertices, dtype=np.float32),
                "triangles": np.asarray(p.faces, dtype=np.int32),
            })
        return out if out else None
    except Exception:
        return None


def _split_components(
    verts: np.ndarray, faces: np.ndarray,
) -> List[Dict[str, np.ndarray]]:
    """연결 성분 분리 — ACD 대안. 각 컴포넌트를 파트로 반환."""
    try:
        import trimesh
        m = trimesh.Trimesh(vertices=verts, faces=faces, process=True)
        comps = m.split(only_watertight=False)
        if len(comps) == 0:
            return [{"vertices": verts, "triangles": faces}]
        out = []
        # 큰 컴포넌트만
        comps = sorted(comps, key=lambda c: len(c.faces), reverse=True)[:24]
        for c in comps:
            if len(c.faces) < 4:
                continue
            out.append({
                "vertices": np.asarray(c.vertices, dtype=np.float32),
                "triangles": np.asarray(c.faces, dtype=np.int32),
            })
        if not out:
            out = [{"vertices": verts, "triangles": faces}]
        return out
    except ImportError:
        return [{"vertices": verts, "triangles": faces}]


# ── Public API ────────────────────────────────────────────────────────────
def build_mesh_collider(
    pts: np.ndarray,
    normals: Optional[np.ndarray] = None,
    *,
    method: str = "poisson",
    depth: int = 8,
    target_tris: int = 4000,
    snap_strength: int = 3,
    convex_parts: bool = False,
    max_parts: int = 12,
    max_edge_ratio: float = 4.0,
    density_trim: float = 0.08,
    keep_fragments: bool = False,
    instant_meshes: bool = False,
    im_target_faces: int = 2000,
    im_pure_quad: bool = False,
) -> Dict:
    """
    page 2용 정밀 메쉬 콜라이더 파이프라인.

    method          : 'poisson' | 'bpa'  — Poisson이 더 매끄럽고 watertight.
    depth           : Poisson octree 깊이 (6~10). 높을수록 디테일, 느림.
    target_tris     : 간단화 목표 삼각형 수 (Unity 로드 가벼움).
    snap_strength   : ICP 반복 수 (0=비활성, 3=기본, 5=밀착).
    convex_parts    : True → ACD 분해 (Unity 동적 rigidbody용).
    max_edge_ratio  : 긴 엣지 프루닝 임계값 (median 엣지의 몇 배). 낮을수록 공격적
                      (3.0 = 강, 4.0 = 표준, 5.0 = 약). 공간 가로지르는 실 제거.
    density_trim    : Poisson 저밀도 영역 제거 비율 (0~0.5). 오목 영역 자동 트림.
    keep_fragments  : False면 가장 큰 컴포넌트만 유지 (프루닝 후 파편 정리).
    """
    # 1. 표면 재구성 (Poisson 저밀도 트림 포함)
    surf = _reconstruct_surface(
        pts, normals, method=method, depth=depth, density_trim=density_trim,
    )

    # 2. 긴-엣지 프루닝 (Poisson/BPA가 공간을 가로지르며 만든 브리지 제거)
    if max_edge_ratio > 0:
        surf = _prune_long_edges(
            surf["verts"], surf["faces"],
            max_edge_ratio=max_edge_ratio,
            abs_cap_ratio=0.08,
        )

    # 3. 파편 정리 (큰 컴포넌트만 유지)
    if not keep_fragments:
        surf = _keep_largest_components(surf["verts"], surf["faces"], min_ratio=0.02)

    # 4. 간단화
    dec = _decimate(surf["verts"], surf["faces"], target_tris)

    # 5. 간단화 후 다시 한 번 긴-엣지 프루닝 (decimation이 새 긴 엣지 만들 수 있음)
    if max_edge_ratio > 0:
        dec = _prune_long_edges(
            dec["verts"], dec["faces"],
            max_edge_ratio=max_edge_ratio * 1.2,  # 간단화 후엔 약간 관대하게
            abs_cap_ratio=0.10,
        )

    # 6. ICP 스냅으로 정합 강화
    if snap_strength > 0:
        dec["verts"] = _icp_snap(dec["verts"], pts, iters=int(snap_strength))

    # 6.5. Instant Meshes 리토폴로지 (선택) — 토폴로지 균일화
    # 정확도는 거의 동일하지만 면 배치가 깔끔해지고 긴 삼각형 사라짐
    if instant_meshes:
        try:
            from backend.core import instant_meshes as im_mod
            if im_mod.is_available():
                res = im_mod.retopologize(
                    dec["verts"], dec["faces"],
                    target_faces=int(im_target_faces),
                    pure_quad=bool(im_pure_quad),
                    smooth_iter=2,
                    align_boundaries=False,
                )
                if res.get("ok"):
                    # Unity 콜라이더는 삼각형만 쓰므로 quad → 2 tri로 쪼갬
                    tri_list = list(res.get("tris", []))
                    for q in res.get("quads", []):
                        tri_list.append([q[0], q[1], q[2]])
                        tri_list.append([q[0], q[2], q[3]])
                    dec = {
                        "verts": np.asarray(res["verts"], dtype=np.float32),
                        "faces": np.asarray(tri_list, dtype=np.int32),
                    }
                    # IM 후 한 번 더 ICP (리토폴이 표면을 살짝 이동시킴)
                    if snap_strength > 0:
                        dec["verts"] = _icp_snap(dec["verts"], pts, iters=max(1, int(snap_strength) // 2))
        except Exception:
            pass  # IM 실패 시 기존 결과 유지

    # 7. 분할 전략
    if convex_parts:
        parts_np = _decompose_into_convex_parts(
            dec["verts"], dec["faces"], max_parts=max_parts,
        )
        mode = "convex_parts"
    else:
        parts_np = [{"vertices": dec["verts"], "triangles": dec["faces"]}]
        mode = "mesh"

    # 5. 직렬화 (리스트로)
    parts_out = []
    v_total = 0
    f_total = 0
    for p in parts_np:
        V = p["vertices"]
        F = p["triangles"]
        parts_out.append({
            "vertices": V.round(4).tolist(),
            "triangles": F.astype(int).tolist(),
        })
        v_total += len(V)
        f_total += len(F)

    return {
        "mode": mode,
        "method": method,
        "parts": parts_out,
        "verts_total": int(v_total),
        "tris_total": int(f_total),
        "part_count": len(parts_out),
    }
