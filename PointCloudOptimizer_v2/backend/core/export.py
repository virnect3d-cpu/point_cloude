"""OBJ mesh exporter — smooth vertex normals 포함."""
from __future__ import annotations
import numpy as np
from typing import Optional


def compute_vertex_normals(
    verts: np.ndarray, faces: np.ndarray,
) -> np.ndarray:
    """
    면적 가중 평균 vertex normal 계산 (Maya "Soften Edge All" = smooth shading).

    각 버텍스는 자기를 포함하는 모든 삼각형의 face normal을 면적 비율로 평균.
    """
    V = np.asarray(verts, dtype=np.float64)
    F = np.asarray(faces, dtype=np.int64)
    v0 = V[F[:, 0]]; v1 = V[F[:, 1]]; v2 = V[F[:, 2]]
    # cross product 크기 = 2 * triangle area, 방향 = face normal
    fn = np.cross(v1 - v0, v2 - v0)      # (F, 3), 면적 가중됨

    vn = np.zeros_like(V, dtype=np.float64)
    # np.add.at 는 중복 인덱스 누적 지원
    for k in range(3):
        np.add.at(vn, F[:, k], fn)

    ln = np.linalg.norm(vn, axis=1, keepdims=True)
    ln = np.maximum(ln, 1e-12)
    return (vn / ln).astype(np.float32)


def to_obj(
    verts: np.ndarray, faces: np.ndarray,
    smooth_normals: bool = True,
) -> str:
    """기본 OBJ 출력. smooth_normals=True면 vn 포함 (Maya smooth)."""
    lines = [
        "# PointCloud Optimizer — exported mesh",
        f"# V={len(verts)} F={len(faces)}"
        + (" smooth_normals=ON" if smooth_normals else ""),
        "",
    ]
    for v in verts:
        lines.append(f"v {v[0]:.6f} {v[1]:.6f} {v[2]:.6f}")
    lines.append("")

    if smooth_normals:
        vn = compute_vertex_normals(verts, faces)
        for n in vn:
            lines.append(f"vn {n[0]:.6f} {n[1]:.6f} {n[2]:.6f}")
        lines.append("")
        for f in faces:
            a, b, c = int(f[0]) + 1, int(f[1]) + 1, int(f[2]) + 1
            lines.append(f"f {a}//{a} {b}//{b} {c}//{c}")
    else:
        for f in faces:
            lines.append(f"f {f[0]+1} {f[1]+1} {f[2]+1}")
    return "\n".join(lines)


def build_mtl_from_clusters(
    cluster_colors: np.ndarray, mtl_prefix: str = "mat",
) -> str:
    """
    클러스터 센터(RGB 0~1) → Wavefront MTL 텍스트.
    각 material은 Lambert 기본값 + Kd = 클러스터 색.
    Unity 임포트 시 base color (Albedo)로 매핑됨.
    """
    lines = ["# PointCloud Optimizer — auto-generated from color clusters", ""]
    for i, c in enumerate(cluster_colors):
        r, g, b = float(c[0]), float(c[1]), float(c[2])
        lines.append(f"newmtl {mtl_prefix}_{i}")
        lines.append("Ka 0.100000 0.100000 0.100000")   # 앰비언트 낮게
        lines.append(f"Kd {r:.6f} {g:.6f} {b:.6f}")       # 디퓨즈 = 클러스터 색 (Unity Albedo)
        lines.append("Ks 0.000000 0.000000 0.000000")   # 스펙큘러 0 (Lambert)
        lines.append("Ns 10.0")
        lines.append("d 1.0")
        lines.append("illum 1")                         # illum 1 = Lambert
        lines.append("")
    return "\n".join(lines)


def to_obj_multi_material(
    verts: np.ndarray,
    tri_faces: np.ndarray,
    face_clusters: np.ndarray,
    quads: Optional[list] = None,
    quad_clusters: Optional[list] = None,
    tris_leftover: Optional[list] = None,
    tri_leftover_clusters: Optional[list] = None,
    mtl_name: str = "mesh.mtl",
    mtl_prefix: str = "mat",
    smooth_normals: bool = True,
) -> str:
    """
    클러스터별 multi-material OBJ.

    face_clusters: 순수 삼각면 모드일 때 각 face의 클러스터 id (F,)
    quads/tris_leftover 모드: quad_clusters + tri_leftover_clusters 각각 제공

    Unity/Maya 모두 usemtl 그룹을 읽어 material 분리.
    """
    lines = [
        "# PointCloud Optimizer — exported mesh (multi-material)",
        f"# V={len(verts)}" + (" smooth_normals=ON" if smooth_normals else ""),
        f"mtllib {mtl_name}",
        "",
    ]
    for v in verts:
        lines.append(f"v {v[0]:.6f} {v[1]:.6f} {v[2]:.6f}")
    lines.append("")

    if smooth_normals:
        # 노멀 계산은 전체 삼각형 기준
        tri_for_normals = list(tri_faces) if tri_faces is not None and len(tri_faces) else []
        if quads:
            for q in quads:
                tri_for_normals.append([q[0], q[1], q[2]])
                tri_for_normals.append([q[0], q[2], q[3]])
        if tris_leftover:
            tri_for_normals.extend(tris_leftover)
        tri_for_normals = np.asarray(tri_for_normals, dtype=np.int32)
        vn = compute_vertex_normals(verts, tri_for_normals)
        for n in vn:
            lines.append(f"vn {n[0]:.6f} {n[1]:.6f} {n[2]:.6f}")
        lines.append("")

    # ── 클러스터 id별로 face 그루핑 → usemtl 섹션 ─────────────────────
    def _emit_group(cluster_id: int, tri_list: list, quad_list: list):
        if not tri_list and not quad_list:
            return
        lines.append(f"usemtl {mtl_prefix}_{int(cluster_id)}")
        lines.append(f"g group_{int(cluster_id)}")
        if smooth_normals:
            for q in quad_list:
                a, b, c, d = int(q[0])+1, int(q[1])+1, int(q[2])+1, int(q[3])+1
                lines.append(f"f {a}//{a} {b}//{b} {c}//{c} {d}//{d}")
            for t in tri_list:
                a, b, c = int(t[0])+1, int(t[1])+1, int(t[2])+1
                lines.append(f"f {a}//{a} {b}//{b} {c}//{c}")
        else:
            for q in quad_list:
                lines.append(f"f {q[0]+1} {q[1]+1} {q[2]+1} {q[3]+1}")
            for t in tri_list:
                lines.append(f"f {t[0]+1} {t[1]+1} {t[2]+1}")

    # 모든 클러스터 id 모으기
    all_ids = set()
    if face_clusters is not None and len(face_clusters):
        all_ids.update(int(x) for x in face_clusters)
    if quad_clusters:
        all_ids.update(int(x) for x in quad_clusters)
    if tri_leftover_clusters:
        all_ids.update(int(x) for x in tri_leftover_clusters)
    if not all_ids:
        all_ids = {0}

    for cid in sorted(all_ids):
        t_group = []
        q_group = []
        if tri_faces is not None and len(tri_faces) and face_clusters is not None:
            mask = face_clusters == cid
            for f in tri_faces[mask]:
                t_group.append(f.tolist() if hasattr(f, "tolist") else list(f))
        if quads and quad_clusters:
            for q, qc in zip(quads, quad_clusters):
                if int(qc) == cid:
                    q_group.append(q)
        if tris_leftover and tri_leftover_clusters:
            for t, tc in zip(tris_leftover, tri_leftover_clusters):
                if int(tc) == cid:
                    t_group.append(t)
        _emit_group(cid, t_group, q_group)

    return "\n".join(lines)


def to_obj_with_quads(
    verts: np.ndarray,
    faces: np.ndarray,
    quads: Optional[list] = None,
    tris_leftover: Optional[list] = None,
    smooth_normals: bool = True,
) -> str:
    """
    사각면(quad) 포함 OBJ 출력.

    smooth_normals=True면 vertex normals(vn) 계산 후 f 라인에 //vn 참조 포함.
    Maya/Blender에서 Soften Edge All 상태처럼 매끈한 셰이딩으로 보임.

    노멀은 "모든 quad + 남은 triangle"을 다 삼각형으로 쪼갠 다음 계산해서
    quad 대각선 부근도 일관되게 평균됨.
    """
    if quads is None:
        return to_obj(verts, faces, smooth_normals=smooth_normals)

    q_count = len(quads)
    t_count = len(tris_leftover) if tris_leftover else 0
    lines = [
        "# PointCloud Optimizer — exported mesh (quad)",
        f"# V={len(verts)}  Quads={q_count}  Tris={t_count}"
        + (" smooth_normals=ON" if smooth_normals else ""),
        "",
    ]
    for v in verts:
        lines.append(f"v {v[0]:.6f} {v[1]:.6f} {v[2]:.6f}")
    lines.append("")

    if smooth_normals:
        # 노멀 계산용으로 quad를 임시 삼각화
        tri_list = list(tris_leftover) if tris_leftover else []
        for q in quads:
            tri_list.append([q[0], q[1], q[2]])
            tri_list.append([q[0], q[2], q[3]])
        tri_np = np.asarray(tri_list, dtype=np.int32)
        vn = compute_vertex_normals(verts, tri_np)
        for n in vn:
            lines.append(f"vn {n[0]:.6f} {n[1]:.6f} {n[2]:.6f}")
        lines.append("")

        # Quad (vn ref)
        for q in quads:
            a, b, c, d = int(q[0])+1, int(q[1])+1, int(q[2])+1, int(q[3])+1
            lines.append(f"f {a}//{a} {b}//{b} {c}//{c} {d}//{d}")
        # Triangle leftovers (vn ref)
        if tris_leftover:
            for t in tris_leftover:
                a, b, c = int(t[0])+1, int(t[1])+1, int(t[2])+1
                lines.append(f"f {a}//{a} {b}//{b} {c}//{c}")
    else:
        for q in quads:
            lines.append(f"f {q[0]+1} {q[1]+1} {q[2]+1} {q[3]+1}")
        if tris_leftover:
            for t in tris_leftover:
                lines.append(f"f {t[0]+1} {t[1]+1} {t[2]+1}")

    return "\n".join(lines)
