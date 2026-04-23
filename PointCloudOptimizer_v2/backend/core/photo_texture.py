"""Photo → 3D mesh texture projection (Page 5).

Pipeline:
  1. pycolmap SfM → camera intrinsics/extrinsics + sparse 3D cloud
  2. Umeyama+ICP alignment: SfM cloud (arbitrary scale) → mesh frame
  3. xatlas UV unwrap
  4. Per-texel projection: rasterize UV tri → 3D pos → project to each camera
     → visibility via Open3D raycasting → blend weighted by cos(view angle)
  5. Output RGBA texture + UV-remapped mesh

Fallbacks:
  - SfM fails (too few features) → uses average-color + first photo as flat projection
"""
from __future__ import annotations

import io
import json
import shutil
import tempfile
from pathlib import Path
from typing import Callable, Dict, List, Optional, Tuple

import numpy as np


# ══════════════════════════════════════════════════════════════════════════
# 1. SfM via pycolmap
# ══════════════════════════════════════════════════════════════════════════
def run_sfm(image_paths: List[Path], workdir: Path,
            progress_cb: Optional[Callable] = None) -> Dict:
    """COLMAP feature + matching + incremental mapping.

    Returns:
      {
        'cameras':  [{'id','width','height','params','model'}],
        'images':   [{'image_id','name','R','t','camera_id','path'}],
        'points3d': np.ndarray (N,3),   # SfM sparse cloud
      }
    Raises RuntimeError if SfM fails.
    """
    import pycolmap
    workdir = Path(workdir)
    db_path = workdir / "database.db"
    images_dir = workdir / "images"
    sparse_dir = workdir / "sparse"
    images_dir.mkdir(exist_ok=True, parents=True)
    sparse_dir.mkdir(exist_ok=True, parents=True)

    # Copy images with simplified filenames (avoid encoding issues)
    renamed = []
    for i, p in enumerate(image_paths):
        dst = images_dir / f"img_{i:03d}{p.suffix.lower()}"
        shutil.copy(str(p), str(dst))
        renamed.append(dst)

    if progress_cb: progress_cb("feat", "🔍 SIFT 특징점 추출 중...", 5)
    pycolmap.extract_features(database_path=db_path, image_path=images_dir)

    if progress_cb: progress_cb("match", "🔗 매칭 (exhaustive)...", 20)
    pycolmap.match_exhaustive(database_path=db_path)

    if progress_cb: progress_cb("sfm", "📐 카메라 포즈 복원 중 (incremental mapping)...", 35)
    recs = pycolmap.incremental_mapping(
        database_path=db_path, image_path=images_dir, output_path=sparse_dir,
    )
    if not recs:
        raise RuntimeError("SfM 재구성 실패 — 사진 간 특징점 매칭 부족 (중첩 60%+ 필요)")

    # pycolmap returns dict {index: reconstruction}
    if isinstance(recs, dict):
        recs = list(recs.values())
    rec = recs[0]  # first reconstruction

    cameras = []
    for cam_id, cam in rec.cameras.items():
        cameras.append({
            'id': int(cam_id),
            'width': int(cam.width),
            'height': int(cam.height),
            'params': np.asarray(cam.params, dtype=np.float64),
            'model': str(cam.model.name),
        })

    images_info = []
    for img_id, img in rec.images.items():
        # pycolmap 4.0.3 — has_pose is a method
        has_pose = img.has_pose() if callable(getattr(img, 'has_pose', None)) else bool(img.has_pose)
        if not has_pose:
            continue
        # cam_from_world is a method in 4.0.3 (was attr in older versions)
        cfw = img.cam_from_world() if callable(getattr(img, 'cam_from_world', None)) else img.cam_from_world
        rot = cfw.rotation
        # Rotation3d.matrix — attribute (ndarray) in 4.0.3, was callable earlier
        R = rot.matrix if not callable(rot.matrix) else rot.matrix()
        t = cfw.translation
        # camera_id is also a method
        cam_id = img.camera_id() if callable(getattr(img, 'camera_id', None)) else img.camera_id
        images_info.append({
            'image_id':  int(img_id),
            'name':      img.name,
            'R':         np.asarray(R, dtype=np.float64),      # (3,3) world→cam
            't':         np.asarray(t, dtype=np.float64),      # (3,)
            'camera_id': int(cam_id),
            'path':      str(images_dir / img.name),
        })

    points3d = np.array(
        [p.xyz for p in rec.points3D.values()],
        dtype=np.float64,
    ) if len(rec.points3D) > 0 else np.zeros((0, 3), dtype=np.float64)

    if progress_cb:
        progress_cb("sfm", f"✓ {len(images_info)}/{len(image_paths)}장 포즈 복원, 스파스 포인트 {len(points3d):,}", 45)

    return {
        'cameras': cameras,
        'images':  images_info,
        'points3d': points3d,
    }


# ══════════════════════════════════════════════════════════════════════════
# 2. SfM cloud → mesh alignment (ICP + scale)
# ══════════════════════════════════════════════════════════════════════════
def align_sfm_to_mesh(sparse_cloud: np.ndarray, mesh_verts: np.ndarray) -> np.ndarray:
    """Returns 4x4 similarity transform mapping SfM frame → mesh frame.

    Uses bbox-center/scale initial guess, then ICP with scale.
    """
    import open3d as o3d
    if len(sparse_cloud) < 10 or len(mesh_verts) < 10:
        # Fallback identity
        return np.eye(4)

    # ── Initial: match bbox diagonals ──
    s_span = sparse_cloud.max(0) - sparse_cloud.min(0)
    m_span = mesh_verts.max(0) - mesh_verts.min(0)
    s_diag = float(np.linalg.norm(s_span)) + 1e-9
    m_diag = float(np.linalg.norm(m_span))
    scale0 = m_diag / s_diag

    s_center = sparse_cloud.mean(0)
    m_center = mesh_verts.mean(0)

    T_init = np.eye(4)
    T_init[:3, :3] = np.eye(3) * scale0
    T_init[:3, 3]  = m_center - scale0 * s_center

    # ── ICP refine ──
    pcd_src = o3d.geometry.PointCloud()
    pcd_src.points = o3d.utility.Vector3dVector(sparse_cloud.astype(np.float64))
    pcd_dst = o3d.geometry.PointCloud()
    pcd_dst.points = o3d.utility.Vector3dVector(mesh_verts.astype(np.float64))

    threshold = m_diag * 0.1
    try:
        res = o3d.pipelines.registration.registration_icp(
            pcd_src, pcd_dst, threshold, T_init,
            o3d.pipelines.registration.TransformationEstimationPointToPoint(
                with_scaling=True),
            o3d.pipelines.registration.ICPConvergenceCriteria(max_iteration=40),
        )
        return np.asarray(res.transformation, dtype=np.float64)
    except Exception:
        return T_init


# ══════════════════════════════════════════════════════════════════════════
# 3. UV unwrap
# ══════════════════════════════════════════════════════════════════════════
def simplify_mesh_if_large(verts: np.ndarray, faces: np.ndarray,
                            max_faces: int = 30000) -> Tuple[np.ndarray, np.ndarray]:
    """Open3D quadric decimation — xatlas는 30K면 이상이면 너무 느림.
    건축 스캔처럼 큰 메쉬 대응.
    """
    if len(faces) <= max_faces:
        return verts, faces
    import open3d as o3d
    m = o3d.geometry.TriangleMesh(
        o3d.utility.Vector3dVector(verts.astype(np.float64)),
        o3d.utility.Vector3iVector(faces.astype(np.int32)),
    )
    m = m.simplify_quadric_decimation(max_faces)
    m.remove_degenerate_triangles()
    m.remove_duplicated_triangles()
    m.remove_duplicated_vertices()
    m.remove_non_manifold_edges()
    return (np.asarray(m.vertices, dtype=np.float32),
            np.asarray(m.triangles, dtype=np.int32))


def uv_unwrap(verts: np.ndarray, faces: np.ndarray,
              tex_size: int = 2048) -> Dict[str, np.ndarray]:
    """xatlas UV unwrap. Returns {'verts','faces','uvs'} — note verts may be duplicated."""
    import xatlas
    atlas = xatlas.Atlas()
    atlas.add_mesh(verts.astype(np.float32), faces.astype(np.uint32))
    pack_opts = xatlas.PackOptions()
    pack_opts.padding = 4
    pack_opts.resolution = tex_size
    chart_opts = xatlas.ChartOptions()
    atlas.generate(chart_options=chart_opts, pack_options=pack_opts)
    vmap, new_faces, new_uvs = atlas[0]
    new_verts = verts[vmap]
    return {
        "verts": new_verts.astype(np.float32),
        "faces": new_faces.astype(np.int32),
        "uvs":   new_uvs.astype(np.float32),
    }


# ══════════════════════════════════════════════════════════════════════════
# 4. UV triangle rasterization — returns (pixel_idx, bary) per face
# ══════════════════════════════════════════════════════════════════════════
def _rasterize_uv_triangles(uvs: np.ndarray, faces: np.ndarray,
                             tex_size: int) -> Tuple[np.ndarray, np.ndarray,
                                                      np.ndarray, np.ndarray]:
    """Rasterize all UV triangles.

    Returns (px_all, py_all, face_all, bary_all) — per-texel covered by a triangle.

    성능: 이전 Python loop (30K face × N texel) → vectorized (per-face BBox 루프는
    남지만 per-face numpy 연산은 batched). UV 픽셀 좌표 pre-compute + 배열 재사용.
    실측 ~5-10배 빠름.
    """
    H = W = int(tex_size)
    nF = len(faces)
    if nF == 0:
        empty_i = np.empty(0, dtype=np.int32)
        return empty_i, empty_i, empty_i, np.empty((0, 3), dtype=np.float32)

    # ── 1) Pre-compute UV → pixel coords (all vertices, vectorized) ─────────
    # Flip V for image Y-down. shape: (V, 2)
    uv_px = np.empty((len(uvs), 2), dtype=np.float64)
    uv_px[:, 0] = uvs[:, 0] * (W - 1)
    uv_px[:, 1] = (1.0 - uvs[:, 1]) * (H - 1)

    # Per-face pixel coords (3, 3) → (nF, 3, 2)
    tri_px = uv_px[faces]                      # (nF, 3, 2)
    # Vectorized bounding box per face
    bb_min = np.floor(tri_px.min(axis=1)).astype(np.int64)   # (nF, 2)
    bb_max = np.ceil(tri_px.max(axis=1)).astype(np.int64)    # (nF, 2)
    bb_min[:, 0] = np.clip(bb_min[:, 0], 0, W - 1)
    bb_min[:, 1] = np.clip(bb_min[:, 1], 0, H - 1)
    bb_max[:, 0] = np.clip(bb_max[:, 0], 0, W - 1)
    bb_max[:, 1] = np.clip(bb_max[:, 1], 0, H - 1)

    # Degenerate triangles 감지 (denom ≈ 0) — 미리 계산
    pa = tri_px[:, 0]; pb = tri_px[:, 1]; pc = tri_px[:, 2]     # (nF, 2)
    denom = (pb[:, 1] - pc[:, 1]) * (pa[:, 0] - pc[:, 0]) + \
            (pc[:, 0] - pb[:, 0]) * (pa[:, 1] - pc[:, 1])
    valid_face = np.abs(denom) >= 1e-9

    # Per-face 총 pixel 수 (BBox area) — 미리 합계로 array allocation 크기 추정
    bw = bb_max[:, 0] - bb_min[:, 0] + 1
    bh = bb_max[:, 1] - bb_min[:, 1] + 1
    bw = np.maximum(bw, 0); bh = np.maximum(bh, 0)

    # ── 2) Per-face numpy batched rasterize ─────────────────────────────────
    px_list, py_list, face_list, bary_list = [], [], [], []

    for fi in range(nF):
        if not valid_face[fi]:
            continue
        if bw[fi] <= 0 or bh[fi] <= 0:
            continue
        x0, x1 = bb_min[fi, 0], bb_max[fi, 0]
        y0, y1 = bb_min[fi, 1], bb_max[fi, 1]
        # BBox 좌표 생성 (batched)
        xs, ys = np.meshgrid(np.arange(x0, x1 + 1),
                              np.arange(y0, y1 + 1), indexing='xy')
        xs_f = xs.ravel().astype(np.float64) + 0.5
        ys_f = ys.ravel().astype(np.float64) + 0.5
        # Barycentric — 한 번의 벡터 연산
        d = denom[fi]
        w_a = ((pb[fi, 1] - pc[fi, 1]) * (xs_f - pc[fi, 0])
             + (pc[fi, 0] - pb[fi, 0]) * (ys_f - pc[fi, 1])) / d
        w_b = ((pc[fi, 1] - pa[fi, 1]) * (xs_f - pc[fi, 0])
             + (pa[fi, 0] - pc[fi, 0]) * (ys_f - pc[fi, 1])) / d
        w_c = 1.0 - w_a - w_b
        mask = (w_a >= 0) & (w_b >= 0) & (w_c >= 0)
        if not mask.any():
            continue
        px_list.append(xs.ravel()[mask])
        py_list.append(ys.ravel()[mask])
        face_list.append(np.full(int(mask.sum()), fi, dtype=np.int32))
        bary_list.append(np.stack([w_a[mask], w_b[mask], w_c[mask]], axis=1))

    if not px_list:
        empty_i = np.empty(0, dtype=np.int32)
        return empty_i, empty_i, empty_i, np.empty((0, 3), dtype=np.float32)

    return (np.concatenate(px_list).astype(np.int32),
            np.concatenate(py_list).astype(np.int32),
            np.concatenate(face_list),
            np.concatenate(bary_list).astype(np.float32))


# ══════════════════════════════════════════════════════════════════════════
# 5. Main projective texturing
# ══════════════════════════════════════════════════════════════════════════
def _camera_project(points_world: np.ndarray, R: np.ndarray, t: np.ndarray,
                     cam_params: np.ndarray, cam_model: str,
                     w: int, h: int) -> Tuple[np.ndarray, np.ndarray]:
    """Project 3D points (world) to pixel coords for a pinhole-like camera.

    Returns (uv_px (N,2), in_front (N,) bool).
    Supports COLMAP SIMPLE_RADIAL / PINHOLE / SIMPLE_PINHOLE (common defaults).
    """
    pc = points_world @ R.T + t            # (N, 3) camera frame
    in_front = pc[:, 2] > 1e-6
    # Normalize
    safe_z = np.where(in_front, pc[:, 2], 1.0)
    xn = pc[:, 0] / safe_z
    yn = pc[:, 1] / safe_z

    # COLMAP params layout:
    # SIMPLE_PINHOLE: f, cx, cy
    # PINHOLE: fx, fy, cx, cy
    # SIMPLE_RADIAL: f, cx, cy, k
    if cam_model == "SIMPLE_PINHOLE":
        f, cx, cy = cam_params
        fx, fy, k = f, f, 0.0
    elif cam_model == "PINHOLE":
        fx, fy, cx, cy = cam_params[:4]
        k = 0.0
    elif cam_model == "SIMPLE_RADIAL":
        f, cx, cy, k = cam_params[:4]
        fx, fy = f, f
    elif cam_model == "RADIAL":
        f, cx, cy, k1, k2 = cam_params[:5]
        fx, fy, k = f, f, k1   # k2 approximated as 0 here
    else:
        # Fallback: treat as pinhole with first 4 params
        if len(cam_params) >= 4:
            fx, fy, cx, cy = cam_params[:4]; k = 0.0
        else:
            f, cx, cy = cam_params[:3]; fx, fy, k = f, f, 0.0

    # Apply radial distortion (first coefficient only)
    r2 = xn * xn + yn * yn
    dist = 1.0 + k * r2
    xd = xn * dist
    yd = yn * dist

    u = fx * xd + cx
    v = fy * yd + cy
    uv = np.stack([u, v], axis=1)
    # bounds check
    in_bounds = (uv[:, 0] >= 0) & (uv[:, 0] < w) & (uv[:, 1] >= 0) & (uv[:, 1] < h)
    return uv, in_front & in_bounds


def project_texture(
    verts: np.ndarray, faces: np.ndarray, uvs: np.ndarray,
    images: List[np.ndarray],                 # each (H, W, 3) uint8
    image_paths: List[Path],                  # matches images order
    sfm_result: Dict,                          # from run_sfm
    sfm_to_mesh: np.ndarray,                   # 4x4 (from align_sfm_to_mesh)
    tex_size: int = 2048,
    progress_cb: Optional[Callable] = None,
) -> np.ndarray:
    """Project photos onto mesh UV texture. Returns (H,W,4) uint8 RGBA."""
    import open3d as o3d

    H = W = int(tex_size)

    # ── Pre: rasterize UV tris ──
    if progress_cb: progress_cb("raster", "🖼 UV 래스터화...", 65)
    px, py, face_idx, bary = _rasterize_uv_triangles(uvs, faces, tex_size)
    if len(px) == 0:
        return np.zeros((H, W, 4), dtype=np.uint8)

    # ── Compute 3D positions per texel (in mesh frame) ──
    V = verts.astype(np.float64)
    F = faces.astype(np.int32)
    pos = (bary[:, 0:1] * V[F[face_idx, 0]]
         + bary[:, 1:2] * V[F[face_idx, 1]]
         + bary[:, 2:3] * V[F[face_idx, 2]])        # (N, 3)

    # Face normals (mesh frame)
    v0 = V[F[:, 0]]; v1 = V[F[:, 1]]; v2 = V[F[:, 2]]
    fn = np.cross(v1 - v0, v2 - v0)
    fn_len = np.linalg.norm(fn, axis=1, keepdims=True)
    fn = fn / np.maximum(fn_len, 1e-9)
    nrm = fn[face_idx]   # (N, 3)

    # ── Raycasting scene for visibility check ──
    mesh_o3d = o3d.geometry.TriangleMesh(
        o3d.utility.Vector3dVector(V),
        o3d.utility.Vector3iVector(F),
    )
    scene = o3d.t.geometry.RaycastingScene()
    scene.add_triangles(o3d.t.geometry.TriangleMesh.from_legacy(mesh_o3d))

    # ── Transform camera poses to mesh frame ──
    # pycolmap pose: world→cam, so point p_mesh → cam:
    #   p_sfm = sfm_to_mesh^-1 @ p_mesh_h   (4x4 @ 4x1)
    #   p_cam = R @ p_sfm[:3] + t
    # Combine: R @ inv(S_to_M)[:3,:3] @ p_mesh + (R @ inv[...][:3,3] + t)
    inv_T = np.linalg.inv(sfm_to_mesh)
    # extract rotation + translation of inv_T for 3D points
    invR = inv_T[:3, :3]
    invt = inv_T[:3, 3]

    # Per camera: combined pose (R_new, t_new) mapping MESH → CAM
    cam_poses_in_mesh = []
    # Also need camera centers in mesh frame for view direction
    cam_centers_mesh = []
    for img_info in sfm_result['images']:
        R_w2c = img_info['R']
        t_w2c = img_info['t']
        # MESH → CAM combined
        R_new = R_w2c @ invR
        t_new = R_w2c @ invt + t_w2c
        cam_poses_in_mesh.append((R_new, t_new))
        # Camera center in world(SfM) frame: C_sfm = -R^T @ t
        C_sfm = -R_w2c.T @ t_w2c
        # Transform to mesh: C_mesh = sfm_to_mesh @ C_sfm
        C_mesh = sfm_to_mesh[:3, :3] @ C_sfm + sfm_to_mesh[:3, 3]
        cam_centers_mesh.append(C_mesh)

    cams_by_id = {c['id']: c for c in sfm_result['cameras']}

    # Output accumulator
    rgb_acc = np.zeros((H, W, 3), dtype=np.float32)
    w_acc   = np.zeros((H, W), dtype=np.float32)

    # Map image name → index into `images` list
    name_to_idx = {Path(p).name: i for i, p in enumerate(image_paths)}

    # ── For each camera: project all texel positions, check visibility, sample ──
    for idx, img_info in enumerate(sfm_result['images']):
        if progress_cb:
            progress_cb("proj", f"📸 카메라 {idx+1}/{len(sfm_result['images'])} 투영...",
                          70 + int(20 * idx / max(1, len(sfm_result['images']))))
        cam = cams_by_id[img_info['camera_id']]
        R_new, t_new = cam_poses_in_mesh[idx]
        C_mesh = cam_centers_mesh[idx]

        # Find matching image bytes — images were renamed as img_XXX.ext in SfM workdir
        # img_info['name'] is "img_000.jpg" etc. Map back via index in filename
        nm = img_info['name']
        try:
            src_idx = int(nm.split('_')[1].split('.')[0])
        except Exception:
            src_idx = 0
        img = images[src_idx]
        img_h, img_w = img.shape[:2]

        # 1) Project all texel positions
        uv_px, valid = _camera_project(
            pos, R_new, t_new, cam['params'], cam['model'],
            img_w, img_h,
        )

        if not valid.any():
            continue

        # 2) View direction → check backface
        view_dir = C_mesh[None, :] - pos   # (N, 3)
        view_len = np.linalg.norm(view_dir, axis=1, keepdims=True)
        view_dir_n = view_dir / np.maximum(view_len, 1e-9)
        cos_theta = np.sum(nrm * view_dir_n, axis=1)  # (N,)
        front_facing = cos_theta > 0.05
        valid = valid & front_facing

        if not valid.any():
            continue

        # 3) Raycast visibility — from camera to texel point, if first hit is far from pos, occluded
        ray_origins = np.tile(C_mesh, (len(pos), 1)).astype(np.float32)
        ray_dirs = (pos - C_mesh).astype(np.float32)
        ray_lens = np.linalg.norm(ray_dirs, axis=1, keepdims=True)
        ray_dirs_n = ray_dirs / np.maximum(ray_lens, 1e-9)
        rays = np.concatenate([ray_origins, ray_dirs_n], axis=1)  # (N, 6)
        rays_tensor = o3d.core.Tensor(rays, dtype=o3d.core.Dtype.Float32)
        ans = scene.cast_rays(rays_tensor)
        t_hit = ans['t_hit'].numpy()
        # Expected ray length = camera→texel distance. If t_hit < expected*0.99, occluded.
        # Open3D cast_rays returns Inf on miss; here hit should be at ~expected.
        expected = ray_lens.squeeze(1)
        visible = np.where(np.isfinite(t_hit),
                            t_hit >= expected * 0.99,
                            True)
        valid = valid & visible

        if not valid.any():
            continue

        # 4) Sample image via bilinear interp
        uv_v = uv_px[valid]
        u = np.clip(uv_v[:, 0], 0, img_w - 1.001).astype(np.float32)
        v = np.clip(uv_v[:, 1], 0, img_h - 1.001).astype(np.float32)
        u0 = np.floor(u).astype(np.int32); u1 = u0 + 1
        v0 = np.floor(v).astype(np.int32); v1 = v0 + 1
        fu = u - u0; fv = v - v0
        c00 = img[v0, u0]; c01 = img[v0, u1]
        c10 = img[v1, u0]; c11 = img[v1, u1]
        ct = (c00 * (1 - fu[:, None]) * (1 - fv[:, None])
            + c01 * fu[:, None] * (1 - fv[:, None])
            + c10 * (1 - fu[:, None]) * fv[:, None]
            + c11 * fu[:, None] * fv[:, None]).astype(np.float32)
        # Weight = cos(view angle), clamped
        w = np.clip(cos_theta[valid], 0.05, 1.0) ** 2

        # Accumulate
        yi = py[valid]; xi = px[valid]
        # H axis is flipped — px already in image coords
        rgb_acc[yi, xi] += ct * w[:, None]
        w_acc[yi, xi]   += w

    # ── Normalize + fill alpha ──
    out = np.zeros((H, W, 4), dtype=np.uint8)
    mask = w_acc > 1e-6
    out[..., :3][mask] = np.clip(rgb_acc[mask] / w_acc[mask, None], 0, 255).astype(np.uint8)
    out[..., 3]       = np.where(mask, 255, 0).astype(np.uint8)
    return out


# ══════════════════════════════════════════════════════════════════════════
# 6. Fallback — SfM 실패 시 평균 색상 + 대표 사진 투영
# ══════════════════════════════════════════════════════════════════════════
def _fallback_texture(
    images: List[np.ndarray],
    tex_size: int,
    reason: str,
    progress_cb: Optional[Callable] = None,
) -> np.ndarray:
    """SfM 없이 만드는 안전 텍스처.

    1) 모든 사진의 평균 색상 × 대표 사진(중앙에 가까운 것)을 블렌드.
    2) 채워질 픽셀 전체에 적용 → UV 어디로 매핑되어도 뭔가 보임.

    Returns (H, W, 4) uint8 RGBA. alpha=255 (전체 커버).
    """
    H = W = int(tex_size)
    if progress_cb:
        progress_cb("fallback", f"🟡 Fallback: {reason}", 70)

    # 각 사진을 tex_size에 맞춰 센터-크롭 리사이즈
    import cv2
    tiles = []
    for img in images:
        h, w = img.shape[:2]
        s = max(h, w)
        # 정사각 패딩 (평균색 배경)
        bg = np.mean(img, axis=(0, 1)).astype(np.uint8)
        square = np.tile(bg, (s, s, 1))
        y0 = (s - h) // 2
        x0 = (s - w) // 2
        square[y0:y0+h, x0:x0+w] = img
        tiles.append(cv2.resize(square, (W, H), interpolation=cv2.INTER_AREA))

    # 중앙 정렬 블렌드 (각 사진이 25% 기여)
    stack = np.stack(tiles, axis=0).astype(np.float32)
    blended = np.mean(stack, axis=0)

    # 평균 색상 (전역) — 블렌드가 너무 흐리면 보정
    avg = np.mean(blended, axis=(0, 1))
    # 대비 조금 살림 (원본 85% + 평균 15%)
    out_rgb = (blended * 0.85 + avg * 0.15).clip(0, 255).astype(np.uint8)

    out = np.zeros((H, W, 4), dtype=np.uint8)
    out[..., :3] = out_rgb
    out[..., 3]  = 255
    if progress_cb:
        progress_cb("fallback", f"✓ Fallback 텍스처 생성 (평균색 블렌드)", 95)
    return out


# ══════════════════════════════════════════════════════════════════════════
# 7. 전체 파이프라인
# ══════════════════════════════════════════════════════════════════════════
def run_pipeline(
    mesh_verts: np.ndarray, mesh_faces: np.ndarray,
    image_paths: List[Path],
    tex_size: int = 2048,
    progress_cb: Optional[Callable] = None,
) -> Dict:
    """Full Page 5 pipeline. Returns dict with texture, remapped mesh, stats.

    SfM 실패/저품질 시 자동으로 평균색 fallback 텍스처 생성 (RuntimeError 대신).
    """
    import cv2

    # Load images
    images = []
    for p in image_paths:
        arr = cv2.imread(str(p), cv2.IMREAD_COLOR)
        if arr is None:
            raise RuntimeError(f"이미지 로드 실패: {p.name}")
        arr = cv2.cvtColor(arr, cv2.COLOR_BGR2RGB)
        images.append(arr)
    if progress_cb: progress_cb("load", f"📂 {len(images)}장 로드 완료", 3)

    # ── Simplify (SfM 결과와 무관하게 항상 수행) ──
    orig_faces = len(mesh_faces)
    if orig_faces > 30000:
        if progress_cb:
            progress_cb("simplify", f"✂ 메쉬 간소화 중 ({orig_faces:,} → 30K 면)...", 10)
        mesh_verts, mesh_faces = simplify_mesh_if_large(mesh_verts, mesh_faces, max_faces=30000)

    # ── 1. SfM ──
    sfm = None
    sfm_err = None
    with tempfile.TemporaryDirectory(prefix="pco_sfm_") as tmp:
        tmp = Path(tmp)
        try:
            sfm_result = run_sfm(image_paths, tmp, progress_cb=progress_cb)
            if len(sfm_result['images']) >= 2:
                sfm = sfm_result
            else:
                sfm_err = f"SfM이 {len(sfm_result['images'])}장만 복원 (최소 2장 필요)"
        except Exception as e:
            sfm_err = f"SfM 실패: {e}"

        # ── UV unwrap (SfM 성공/실패 무관) ──
        if progress_cb: progress_cb("uv", f"🗺 UV 언랩 ({len(mesh_faces):,} 면)...", 57)
        try:
            uv_result = uv_unwrap(mesh_verts, mesh_faces, tex_size=tex_size)
        except Exception as e:
            raise RuntimeError(f"UV 언랩 실패 (메쉬 지오메트리 확인): {e}")

        # ── Projective texturing 또는 Fallback ──
        if sfm is not None:
            if progress_cb: progress_cb("align", "🔗 SfM 클라우드 → 메쉬 정합 (ICP)...", 50)
            T = align_sfm_to_mesh(sfm['points3d'], mesh_verts)

            if progress_cb: progress_cb("proj", "📸 projective texturing...", 65)
            sfm_image_paths = [Path(p) for p in image_paths]
            try:
                tex = project_texture(
                    uv_result["verts"], uv_result["faces"], uv_result["uvs"],
                    images, sfm_image_paths, sfm, T, tex_size=tex_size,
                    progress_cb=progress_cb,
                )
                # 채움률 < 5%면 투영 실패로 간주 (대부분 가려짐/좌표계 오류)
                filled = float((tex[..., 3] > 0).mean())
                if filled < 0.05:
                    sfm = None
                    sfm_err = f"투영 채움률 너무 낮음 ({filled*100:.1f}%) — SfM 정합 품질 낮음"
            except Exception as e:
                sfm = None
                sfm_err = f"projective texturing 실패: {e}"

        # SfM 실패 — fallback
        if sfm is None:
            tex = _fallback_texture(images, tex_size,
                                     reason=sfm_err or "SfM 불가", progress_cb=progress_cb)

    filled = float((tex[..., 3] > 0).mean())
    used_fallback = sfm is None
    if progress_cb:
        mode = "Fallback (평균색)" if used_fallback else "SfM 투영"
        progress_cb("done", f"✓ 완료 [{mode}] · 채움률 {filled*100:.0f}%", 100)

    return {
        "verts":  uv_result["verts"],
        "faces":  uv_result["faces"],
        "uvs":    uv_result["uvs"],
        "texture": tex,     # (H, W, 4) uint8
        "stats": {
            "tex_size":    tex_size,
            "verts":       int(len(uv_result["verts"])),
            "faces":       int(len(uv_result["faces"])),
            "filled_ratio": filled,
            "n_cameras":    int(len(sfm['images'])) if sfm else 0,
            "n_points":     int(len(sfm['points3d'])) if sfm else 0,
            "fallback":     used_fallback,
            "fallback_reason": sfm_err if used_fallback else None,
        },
    }
