"""
FastAPI backend for PointCloud Optimizer
- POST /api/upload          → 파일 업로드 (청크 스트리밍, 최대 4GB)
- POST /api/upload-path     → PyWebView JsApi 경유 직접 경로 업로드
- GET  /api/process/{sid}   → SSE 파이프라인 진행 스트림
- GET  /api/mesh/{sid}      → 처리된 OBJ 메쉬 다운로드
- GET  /api/stats/{sid}     → 최신 검증 통계 JSON
- GET  /api/sessions        → 현재 살아있는 세션 목록
- DELETE /api/session/{sid} → 세션 명시적 해제
- GET  /api/health          → 패키지 상태 + 용량 정보
- /*                        → frontend/index.html (static)
"""
from __future__ import annotations

import asyncio
import json
import re
import sys
import time
import uuid
from pathlib import Path
from typing import Optional

import numpy as np
from fastapi import FastAPI, File, Request, UploadFile, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel, Field
from fastapi.responses import Response, StreamingResponse
from fastapi.staticfiles import StaticFiles

ROOT = Path(__file__).parent.parent
sys.path.insert(0, str(ROOT))

from backend.core import bpa, loader, pipeline, export, collider as collider_core
from backend.core import instant_meshes as im_core
from backend.core import uv_bake as uvb_core
from backend.core import fbx_export as fbx_core
from backend.core import fbx_binary_export as fbx_bin_core
from backend.core import glb_export as glb_core
from backend.core import unitypackage as unitypkg_core
from backend.core import photo_texture as photo_tex_core

app = FastAPI(title="PointCloud Optimizer", version="3.0.0")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)

# ── 세션 스토어 ────────────────────────────────────────────────────────────
# sid → {"pts", "normals"?, "filename", "mesh", "obj", "val", "_ts"}
_sessions: dict[str, dict] = {}

# 세션 TTL — PyWebView 앱은 장시간 실행되므로 2시간 후 자동 해제
_SESSION_TTL = 7200  # seconds

# 업로드 제한 — 로컬 PyWebView 데스크톱 전용: 16GB
# (브라우저 업로드는 ~2GB 넘으면 OOM 위험 — JS 측에서 경고/차단)
MAX_UPLOAD_BYTES = 16 * 1024 * 1024 * 1024

ALLOWED_EXTENSIONS = frozenset({
    ".ply", ".xyz", ".pts", ".pcd", ".las", ".laz", ".obj", ".ptx",
    ".csv", ".txt", ".splat", ".ksplat",
})


# ── 내부 유틸 ──────────────────────────────────────────────────────────────
def _ext_ok(filename: str) -> bool:
    if not filename or not filename.strip():
        return False
    return Path(filename.strip().lower()).suffix in ALLOWED_EXTENSIONS


def _safe_stem(filename: str) -> str:
    stem = Path(filename or "mesh").stem
    stem = re.sub(r"[^\w\-.]", "_", stem, flags=re.ASCII)[:80]
    return stem or "mesh"


def _im_to_triangles(im_result: dict) -> np.ndarray:
    """Instant Meshes 결과(quad+tri)를 삼각형 전용 배열로 변환 (validate용)."""
    tris = list(im_result.get("tris", []))
    for q in im_result.get("quads", []):
        tris.append([q[0], q[1], q[2]])
        tris.append([q[0], q[2], q[3]])
    return np.asarray(tris, dtype=np.int32) if tris else np.zeros((0, 3), dtype=np.int32)


def _gc_sessions() -> None:
    """만료된 세션을 제거합니다 (호출 시점에 동기적으로 실행)."""
    now = time.time()
    expired = [sid for sid, s in _sessions.items()
               if now - s.get("_ts", now) > _SESSION_TTL]
    for sid in expired:
        _sessions.pop(sid, None)


def _new_session(pts: np.ndarray, normals: Optional[np.ndarray],
                 fname: str, colors: Optional[np.ndarray] = None) -> str:
    _gc_sessions()
    sid = uuid.uuid4().hex[:12]
    _sessions[sid] = {
        "pts": pts, "normals": normals, "colors": colors,
        "filename": fname, "_ts": time.time(),
    }
    return sid


# ── Upload (스트리밍 청크 읽기, 4 GB 허용) ─────────────────────────────────
@app.post("/api/upload")
async def upload(file: UploadFile = File(...)):
    fname = file.filename or "upload.ply"
    if not _ext_ok(fname):
        raise HTTPException(
            415,
            f"지원하지 않는 확장자입니다. 허용: {', '.join(sorted(ALLOWED_EXTENSIONS))}",
        )

    # 청크 단위 읽기 → 메모리 효율 향상 (64 MB 청크)
    CHUNK = 64 * 1024 * 1024
    chunks: list[bytes] = []
    total = 0
    while True:
        chunk = await file.read(CHUNK)
        if not chunk:
            break
        total += len(chunk)
        if total > MAX_UPLOAD_BYTES:
            raise HTTPException(
                413,
                f"파일이 너무 큽니다 (최대 {MAX_UPLOAD_BYTES // 1024 // 1024 // 1024}GB)",
            )
        chunks.append(chunk)

    data = b"".join(chunks)
    if not data:
        raise HTTPException(400, "빈 파일입니다")

    try:
        loaded = loader.load_full(fname, data)
        pts    = loaded["pts"]
        normals = loaded.get("normals")
        colors  = loaded.get("colors")
    except Exception as e:
        raise HTTPException(422, f"파싱 오류: {e}")

    if len(pts) < 4:
        raise HTTPException(422, "포인트가 4개 미만입니다")

    sid = _new_session(pts, normals, fname, colors=colors)
    bb_min = pts.min(axis=0).tolist()
    bb_max = pts.max(axis=0).tolist()

    return {
        "session_id": sid,
        "point_count": len(pts),
        "filename": fname,
        "bbox": {"min": bb_min, "max": bb_max},
        "has_normals": normals is not None,
        "has_colors":  colors is not None,
        "size_bytes": total,
    }


# ── Points binary fetch (Page 1 LAZ 경로: 백엔드 디코드 → 프론트 전달) ─────
@app.get("/api/points-binary/{sid}")
async def get_points_binary(sid: str):
    """세션의 포인트/법선/색상을 바이너리로 반환합니다.

    프런트엔드에서 LAZ 파일처럼 JS 파서로 해독이 어려운 포맷일 때
    백엔드가 laspy+lazrs로 디코드한 결과를 Float32Array binary 로 받아
    기존 파이프라인(parseLAS 결과와 동일 스키마)에 주입합니다.

    Binary layout (little endian):
      - header: 3 × int32 = [n_points, has_normals (0/1), has_colors (0/1)]
      - verts  : n_points × 3 × float32
      - normals: n_points × 3 × float32  (has_normals 일 때만)
      - colors : n_points × 3 × float32  (has_colors 일 때만, 0~1 정규화)
    """
    s = _sessions.get(sid)
    if not s:
        raise HTTPException(404, "세션을 찾을 수 없습니다")
    pts = s.get("pts")
    if pts is None or len(pts) == 0:
        raise HTTPException(404, "세션에 포인트가 없습니다")
    pts = np.ascontiguousarray(pts, dtype=np.float32)
    n = pts.shape[0]
    nrm = s.get("normals")
    col = s.get("colors")
    has_n = 1 if (nrm is not None and len(nrm) == n) else 0
    has_c = 1 if (col is not None and len(col) == n) else 0
    header = np.array([n, has_n, has_c], dtype=np.int32).tobytes()
    buf = [header, pts.tobytes()]
    if has_n:
        buf.append(np.ascontiguousarray(nrm, dtype=np.float32).tobytes())
    if has_c:
        buf.append(np.ascontiguousarray(col, dtype=np.float32).tobytes())
    s["_ts"] = time.time()
    return Response(content=b"".join(buf), media_type="application/octet-stream")


# ── Upload-Path (PyWebView JsApi 경유 — 로컬 파일 경로 직접 전달) ──────────
@app.post("/api/upload-path")
async def upload_path(request: Request):
    """JsApi.upload_file_dialog() 가 이미 파일을 읽어서 전송한 경우와
    동일한 multipart 처리를 합니다.
    또는 JSON body {"path": "..."} 로 전달하면 서버 측에서 직접 읽습니다.
    """
    content_type = request.headers.get("content-type", "")
    if "application/json" in content_type:
        body = await request.json()
        fpath = Path(body.get("path", ""))
        if not fpath.exists():
            raise HTTPException(404, f"파일을 찾을 수 없습니다: {fpath}")
        if not _ext_ok(fpath.name):
            raise HTTPException(415, f"지원하지 않는 확장자: {fpath.suffix}")
        data = fpath.read_bytes()
        fname = fpath.name
    else:
        raise HTTPException(400, "Content-Type: application/json 이 필요합니다")

    if not data:
        raise HTTPException(400, "빈 파일입니다")
    if len(data) > MAX_UPLOAD_BYTES:
        raise HTTPException(413, "파일이 너무 큽니다")

    try:
        loaded  = loader.load_full(fname, data)
        pts     = loaded["pts"]
        normals = loaded.get("normals")
        colors  = loaded.get("colors")
    except Exception as e:
        raise HTTPException(422, f"파싱 오류: {e}")

    if len(pts) < 4:
        raise HTTPException(422, "포인트가 4개 미만입니다")

    sid = _new_session(pts, normals, fname, colors=colors)
    bb_min = pts.min(axis=0).tolist()
    bb_max = pts.max(axis=0).tolist()

    return {
        "session_id": sid,
        "point_count": len(pts),
        "filename": fname,
        "bbox": {"min": bb_min, "max": bb_max},
        "has_normals": normals is not None,
        "has_colors":  colors is not None,
        "size_bytes": len(data),
    }


# ── Pipeline (SSE) ─────────────────────────────────────────────────────────
# POST 바디에 실리는 파이프라인 파라미터 스키마.
# 이전엔 42개가 URL 쿼리였는데 길이 제한 + 캐시 문제 + JSON validation 부재로 POST로 이동.
# Frontend 에서는 fetch() + ReadableStream 으로 SSE 수신 (EventSource 대체).
class ProcessParams(BaseModel):
    algorithm:        str   = "mc"          # mc | bpa | poisson | sdf | alpha
    denoise:          bool  = True
    sigma:            float = 2.0
    mc_res:           int   = 50
    bpa_radii_scale:  float = 1.0
    poisson_depth:    int   = 9
    smooth:           bool  = True
    smooth_iter:      int   = 2
    smooth_type:      str   = "taubin"      # taubin | laplacian
    mirror_x:         bool  = False
    mirror_axis:      str   = "x"           # x | y | z
    mirror_center:    str   = "centroid"    # centroid | bbox | origin
    quadify:          bool  = True
    icp_snap:         int   = 0
    merge_verts:      bool  = True
    orient_normals:   bool  = True
    uniform_remesh:   bool  = False
    smooth_normals:   bool  = True
    prune_edges:      float = 0.0
    remove_fragments: bool  = True
    target_tris:      int   = 0
    voxel_remesh:     bool  = False
    voxel_res:        int   = 60
    instant_meshes:   bool  = False
    im_target_faces:  int   = 10000
    im_pure_quad:     bool  = True
    im_crease:        float = 0.0
    fake_hole_fill:   bool  = True
    fake_hole_size:   float = 0.15
    color_groups:     int   = 0
    surface_mode:     str   = "smooth"      # smooth | hard
    alpha_ratio:      float = 0.015
    plane_snap:       bool  = True


@app.post("/api/process/{sid}")
async def process(sid: str, params: ProcessParams = ProcessParams()):
    # 개별 필드로 unpack — 아래 본문은 예전 시그니처 이름을 그대로 재사용.
    algorithm       = params.algorithm
    denoise         = params.denoise
    sigma           = params.sigma
    mc_res          = params.mc_res
    bpa_radii_scale = params.bpa_radii_scale
    poisson_depth   = params.poisson_depth
    smooth          = params.smooth
    smooth_iter     = params.smooth_iter
    smooth_type     = params.smooth_type
    mirror_x        = params.mirror_x
    mirror_axis     = params.mirror_axis
    mirror_center   = params.mirror_center
    quadify         = params.quadify
    icp_snap        = params.icp_snap
    merge_verts     = params.merge_verts
    orient_normals  = params.orient_normals
    uniform_remesh  = params.uniform_remesh
    smooth_normals  = params.smooth_normals
    prune_edges     = params.prune_edges
    remove_fragments = params.remove_fragments
    target_tris     = params.target_tris
    voxel_remesh    = params.voxel_remesh
    voxel_res       = params.voxel_res
    instant_meshes  = params.instant_meshes
    im_target_faces = params.im_target_faces
    im_pure_quad    = params.im_pure_quad
    im_crease       = params.im_crease
    fake_hole_fill  = params.fake_hole_fill
    fake_hole_size  = params.fake_hole_size
    color_groups    = params.color_groups
    surface_mode    = params.surface_mode
    alpha_ratio     = params.alpha_ratio
    plane_snap      = params.plane_snap

    if sid not in _sessions:
        raise HTTPException(404, "세션을 찾을 수 없습니다")

    # 세션 타임스탬프 갱신
    _sessions[sid]["_ts"] = time.time()

    sm = (surface_mode or "smooth").lower().strip()
    if sm not in ("smooth", "hard"):
        raise HTTPException(400, "surface_mode는 smooth | hard 중 하나여야 합니다")

    # ── DoS 방지: 파라미터 sanity clamp ─────────────────────────────────────
    # 악의적/실수 입력(음수·무한대)이 서버 hang 걸리는 것 방지.
    # 상한은 "일반 사용자가 UI에서 선택 가능한 최대값 + 여유"로 잡음.
    # 너무 높이면 DoS, 너무 낮으면 실사용 거부.
    try:
        mc_res           = max(8, min(int(mc_res), 128))         # UI 최대 ~80 → 128 OK
        poisson_depth    = max(4, min(int(poisson_depth), 10))   # 9는 이미 dense, 10은 절대 상한
        smooth_iter      = max(0, min(int(smooth_iter), 20))     # 20회 이상은 형태 뭉개짐
        sigma            = max(0.1, min(float(sigma), 5.0))
        bpa_radii_scale  = max(0.1, min(float(bpa_radii_scale), 5.0))
        target_tris      = max(0, min(int(target_tris), 2_000_000))
        icp_snap         = max(0, min(int(icp_snap), 10))
        voxel_res        = max(8, min(int(voxel_res), 160))
        im_target_faces  = max(100, min(int(im_target_faces), 200_000))
        im_crease        = max(0.0, min(float(im_crease), 180.0))
        alpha_ratio      = max(0.001, min(float(alpha_ratio), 0.3))
        fake_hole_size   = max(0.0, min(float(fake_hole_size), 1.0))
        color_groups     = max(0, min(int(color_groups), 32))
        prune_edges      = max(0.0, min(float(prune_edges), 50.0))
    except (ValueError, TypeError):
        raise HTTPException(400, "파라미터 타입이 올바르지 않습니다 (정수·실수 기대)")

    # hard 모드: Poisson(밀도 채움) + 평면 스냅 후처리 + Taubin OFF
    # (Alpha-shape는 희박 영역에서 구멍이 생겨 건물 스캔엔 부적합 — 실험 결과)
    algo = (algorithm or "mc").lower().strip()
    if sm == "hard":
        algo = "poisson"             # 밀도 확보 — 구멍 없음
        smooth = False               # 스무딩 OFF (평면 스냅 후 엣지 보존)
        uniform_remesh = False       # isotropic remesh OFF (평면 스냅 효과 희석됨)
    elif algo not in ("mc", "bpa", "poisson", "sdf", "alpha"):
        raise HTTPException(400, "algorithm은 mc | bpa | poisson | sdf | alpha 중 하나여야 합니다")

    async def _stream():
        def evt(data: dict) -> str:
            return f"data: {json.dumps(data, ensure_ascii=False)}\n\n"

        session = _sessions[sid]
        pts: np.ndarray = session["pts"].copy()
        nrm: Optional[np.ndarray] = session.get("normals")
        if nrm is not None:
            nrm = np.asarray(nrm, dtype=np.float32).copy()

        try:
            # ── Step 1: Denoise ─────────────────────────────────────────
            yield evt({"step": "denoise", "status": "active", "progress": 8})
            await asyncio.sleep(0)

            if denoise:
                if nrm is not None and len(nrm) == len(pts):
                    pts, nrm = await asyncio.get_event_loop().run_in_executor(
                        None,
                        lambda: pipeline.sor_with_normals(pts, nrm, sigma=sigma),
                    )
                else:
                    pts = await asyncio.get_event_loop().run_in_executor(
                        None, lambda: pipeline.sor(pts, sigma=sigma)
                    )
                    nrm = None
                yield evt({"step": "denoise", "status": "done", "progress": 20,
                           "count": len(pts), "msg": f"→ {len(pts):,} pts 남음"})
            else:
                yield evt({"step": "denoise", "status": "done", "progress": 20,
                           "count": len(pts), "msg": "스킵"})
            await asyncio.sleep(0)

            # ── mirror: 지정 축 기준 반전 복사본 추가 ────────────────────
            if mirror_x:
                ax = {"x": 0, "y": 1, "z": 2}.get((mirror_axis or "x").lower(), 0)
                if (mirror_center or "centroid").lower() == "bbox":
                    c_val = float((pts[:, ax].min() + pts[:, ax].max()) / 2)
                elif (mirror_center or "centroid").lower() == "origin":
                    c_val = 0.0
                else:  # centroid (기본값) — 질량 중심 기준이라 비대칭 스캔에도 자연스러움
                    c_val = float(pts[:, ax].mean())
                mirrored = pts.copy()
                mirrored[:, ax] = 2.0 * c_val - pts[:, ax]
                pts = np.vstack([pts, mirrored]).astype(np.float32)
                if nrm is not None:
                    nrm_m = nrm.copy(); nrm_m[:, ax] = -nrm[:, ax]
                    nrm = np.vstack([nrm, nrm_m]).astype(np.float32)
                yield evt({"step": "denoise", "status": "done", "progress": 21,
                           "count": len(pts),
                           "msg": f"🔀 미러: {mirror_axis}축 @ {mirror_center} (c={c_val:.2f}) → {len(pts):,} pts"})
                await asyncio.sleep(0)

            # ── Step 2: Surface — MC | BPA | Poisson | SDF ──────────────
            if algo == "bpa":
                yield evt({"step": "bpa", "status": "active", "progress": 22,
                           "msg": "Ball-Pivoting (BPA) 표면 재구성..."})
                await asyncio.sleep(0)
                raw = await asyncio.get_event_loop().run_in_executor(
                    None,
                    lambda: bpa.reconstruct_bpa(pts, nrm, radii_scale=bpa_radii_scale),
                )
                step_key = "bpa"; tag = "BPA"
            elif algo == "poisson":
                yield evt({"step": "poisson", "status": "active", "progress": 22,
                           "msg": f"Poisson 재구성 (depth={poisson_depth})..."})
                await asyncio.sleep(0)
                raw = await asyncio.get_event_loop().run_in_executor(
                    None,
                    lambda: pipeline.build_poisson_mesh(pts, nrm, depth=poisson_depth),
                )
                # Poisson은 빈 공간에 표면 확장 → 원본 포인트 멀리 떨어진 영역 즉시 제거
                # 0.015 = 대각선의 1.5% 이내만 유지 (공격적 — "붕어빵 반죽" 현상 차단)
                raw = await asyncio.get_event_loop().run_in_executor(
                    None,
                    lambda: pipeline.trim_far_from_points(
                        raw["verts"], raw["faces"], pts, max_dist_ratio=0.015,
                    ),
                )
                # 트림 후 남은 파편 제거 (확장 영역이 섬처럼 남을 수 있음)
                raw = await asyncio.get_event_loop().run_in_executor(
                    None,
                    lambda: pipeline.keep_largest_components(
                        raw["verts"], raw["faces"], min_ratio=0.05,
                    ),
                )
                # ★ 공갈 구멍 1차 메우기 — IM 전에 최대한 채워넣어야 IM이 quad로 통합
                # IM이 메워진 영역을 통째로 quad로 재구성하니 fan 자국 안 남음
                if fake_hole_fill:
                    raw = await asyncio.get_event_loop().run_in_executor(
                        None,
                        lambda: pipeline.smart_fill_holes(
                            raw["verts"], raw["faces"], pts,
                            max_size_ratio=float(fake_hole_size),
                            auto_fill_small_ratio=0.10,   # 10% 이하는 무조건 메움 (공격적)
                            support_radius_ratio=0.08,
                            min_support_points=1,
                        ),
                    )
                step_key = "poisson"; tag = "Poisson"
            elif algo == "sdf":
                yield evt({"step": "sdf", "status": "active", "progress": 22,
                           "msg": f"SDF Marching Cubes (grid={mc_res})..."})
                await asyncio.sleep(0)
                raw = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: pipeline.build_sdf_mc_mesh(pts, grid_res=mc_res)
                )
                step_key = "sdf"; tag = "SDF-MC"
            elif algo == "alpha":
                # Alpha Shape (고급 모드 — 공벌레, 바위 등 특수 케이스)
                yield evt({"step": "alpha", "status": "active", "progress": 22,
                           "msg": f"Alpha Shape 재구성 (α={alpha_ratio:.3f})..."})
                await asyncio.sleep(0)
                raw = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: pipeline.build_alpha_shape_mesh(pts, alpha_ratio=float(alpha_ratio))
                )
                if raw.get("faces") is not None and len(raw["faces"]) > 0:
                    raw = await asyncio.get_event_loop().run_in_executor(
                        None,
                        lambda: pipeline.keep_largest_components(
                            raw["verts"], raw["faces"], min_ratio=0.02,
                        ),
                    )
                step_key = "alpha"; tag = "Alpha-Shape"
            else:
                yield evt({"step": "mc", "status": "active", "progress": 22,
                           "msg": f"Marching Cubes (해상도: {mc_res})..."})
                await asyncio.sleep(0)
                raw = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: pipeline.build_mc_mesh(pts, grid_res=mc_res)
                )
                step_key = "mc"; tag = "MC"

            # Hard 모드 — Poisson 베이스 유지 + RANSAC 평면 검출 → 버텍스 스냅
            # (OBB 박스 overlay는 실험했으나 평면이 scene 크기만큼 커져서 Poisson을 통째로 삼킴)
            # 스냅 방식: density·디테일 다 보존하면서 벽만 평평해짐
            if sm == "hard" and plane_snap and len(raw.get("faces", [])) > 0:
                yield evt({"step": step_key, "status": "active", "progress": 40,
                           "msg": "🔷 RANSAC 평면 검출 + 버텍스 스냅 (벽·바닥 평탄화)..."})
                await asyncio.sleep(0)
                raw["verts"] = await asyncio.get_event_loop().run_in_executor(
                    None,
                    lambda: pipeline.snap_verts_to_planes(raw["verts"], pts),
                )

            V0, F0 = len(raw["verts"]), len(raw["faces"])
            yield evt({"step": step_key, "status": "done", "progress": 46,
                       "V": V0, "F": F0, "msg": f"{tag}: {V0:,}V · {F0:,}F"})
            await asyncio.sleep(0)

            # ── Step 3: Validate ────────────────────────────────────────
            yield evt({"step": "validate", "status": "active", "progress": 50})
            await asyncio.sleep(0)

            val = await asyncio.get_event_loop().run_in_executor(
                None, lambda: pipeline.validate(raw["verts"], raw["faces"])
            )
            val_pub = {k: v for k, v in val.items() if not k.startswith("_")}
            session["val"] = val_pub

            issues = []
            if not val["watertight"]:             issues.append(f"열린경계 {val['boundary_edges']}")
            if val["non_manifold_edges"] > 0:     issues.append(f"Non-manifold {val['non_manifold_edges']}")
            if val["components"] > 1:             issues.append(f"파편 {val['components']}개")
            if val["normal_consistency"] < 0.95:  issues.append(f"노멀불일치 {val['normal_consistency']*100:.0f}%")

            yield evt({"step": "validate", "status": "done", "progress": 62,
                       "val": val_pub, "issues": issues,
                       "msg": "검증 통과 ✓" if not issues else f"이슈 {len(issues)}건 → 자동복구"})
            await asyncio.sleep(0)

            # ── Step 4: Repair + Smooth ─────────────────────────────────
            yield evt({"step": "repair", "status": "active", "progress": 65,
                       "msg": "Non-manifold 제거 → 컴포넌트 → 노멀 → 구멍 메우기..."})
            await asyncio.sleep(0)

            fixed = await asyncio.get_event_loop().run_in_executor(
                None, lambda: pipeline.repair(raw["verts"], raw["faces"], val)
            )

            if smooth:
                stype = (smooth_type or "taubin").lower()
                if stype == "taubin":
                    yield evt({"step": "repair", "status": "active", "progress": 80,
                               "msg": f"✨ Taubin 스무딩 x{smooth_iter} (부피 보존)..."})
                    await asyncio.sleep(0)
                    fixed["verts"] = await asyncio.get_event_loop().run_in_executor(
                        None, lambda: pipeline.taubin_smooth(
                            fixed["verts"], fixed["faces"],
                            iterations=max(1, int(smooth_iter)),
                        )
                    )
                else:
                    yield evt({"step": "repair", "status": "active", "progress": 80,
                               "msg": f"Laplacian 스무딩 x{smooth_iter}..."})
                    await asyncio.sleep(0)
                    fixed["verts"] = await asyncio.get_event_loop().run_in_executor(
                        None, lambda: pipeline.laplacian_smooth(
                            fixed["verts"], fixed["faces"], smooth_iter
                        )
                    )

            # ── Step 4.25: 긴-엣지 프루닝 (공간 가로지르는 실 제거) ─────
            if float(prune_edges) > 0:
                yield evt({"step": "repair", "status": "active", "progress": 82,
                           "msg": f"🧹 긴-엣지 프루닝 x{prune_edges}..."})
                await asyncio.sleep(0)
                fixed = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: pipeline.prune_long_edges(
                        fixed["verts"], fixed["faces"],
                        max_edge_ratio=float(prune_edges), abs_cap_ratio=0.08,
                    )
                )

            # ── Step 4.27: 파편 제거 ─────────────────────────────────────
            if remove_fragments:
                fixed = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: pipeline.keep_largest_components(
                        fixed["verts"], fixed["faces"], min_ratio=0.02,
                    )
                )

            # ── Step 4.28: 축-정렬 복셀 리메시 (Instant Meshes 스타일) ─
            # Poisson/BPA의 Voronoi식 무작위 토폴로지를 XYZ 격자 토폴로지로 교체
            if voxel_remesh:
                yield evt({"step": "repair", "status": "active", "progress": 82,
                           "msg": f"⬜ 축-정렬 리메시 (res={voxel_res})..."})
                await asyncio.sleep(0)
                rm = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: pipeline.voxel_remesh(
                        fixed["verts"], fixed["faces"],
                        resolution=int(voxel_res), fill_interior=True,
                    )
                )
                if len(rm["faces"]) > 4:
                    fixed = rm
                    # 복셀 리메시 후 Taubin 재적용해 계단 현상 부드럽게
                    if smooth:
                        fixed["verts"] = await asyncio.get_event_loop().run_in_executor(
                            None, lambda: pipeline.taubin_smooth(
                                fixed["verts"], fixed["faces"],
                                iterations=max(2, int(smooth_iter)),
                            )
                        )

            # ── Step 4.29: Decimation 목표 삼각형 수까지 간단화 ─────────
            if int(target_tris) > 0 and len(fixed["faces"]) > int(target_tris):
                yield evt({"step": "repair", "status": "active", "progress": 83,
                           "msg": f"◈ 간단화 -> {target_tris:,} F 목표..."})
                await asyncio.sleep(0)
                fixed = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: pipeline.decimate_to_target(
                        fixed["verts"], fixed["faces"], int(target_tris),
                    )
                )
                # decimation이 새로운 긴-엣지를 만들 수 있음 → 2차 프루닝
                if float(prune_edges) > 0:
                    fixed = await asyncio.get_event_loop().run_in_executor(
                        None, lambda: pipeline.prune_long_edges(
                            fixed["verts"], fixed["faces"],
                            max_edge_ratio=float(prune_edges) * 1.3,
                            abs_cap_ratio=0.10,
                        )
                    )

            # ── Step 4.3: 중복 버텍스 병합 (끊어진 면 방지) ─────────────
            if merge_verts:
                fixed = await asyncio.get_event_loop().run_in_executor(
                    None,
                    lambda: pipeline.merge_close_vertices(
                        fixed["verts"], fixed["faces"], eps_ratio=1e-5,
                    ),
                )

            # ── Step 4.35: Isotropic remesh (선택) ──────────────────────
            if uniform_remesh:
                yield evt({"step": "repair", "status": "active", "progress": 82,
                           "msg": "◇ 면 균일화 (isotropic remesh)..."})
                await asyncio.sleep(0)
                fixed = await asyncio.get_event_loop().run_in_executor(
                    None,
                    lambda: pipeline.remesh_uniform(
                        fixed["verts"], fixed["faces"],
                        target_edge_ratio=1.0, max_iters=2,
                    ),
                )

            # ── Step 4.4: ICP Snap (원본 포인트로 정합 강화) ────────────
            if int(icp_snap) > 0:
                yield evt({"step": "repair", "status": "active", "progress": 85,
                           "msg": f"🎯 ICP 정합 x{int(icp_snap)}..."})
                await asyncio.sleep(0)
                fixed["verts"] = await asyncio.get_event_loop().run_in_executor(
                    None,
                    lambda: pipeline.snap_verts_to_points(
                        fixed["verts"], pts,
                        iterations=int(icp_snap), strength=0.55,
                    ),
                )

            # ── Step 4.45: 노멀 일관성 재보정 (winding 통일 + 바깥쪽) ───
            if orient_normals:
                yield evt({"step": "repair", "status": "active", "progress": 87,
                           "msg": "🧭 노멀 일관성 재보정..."})
                await asyncio.sleep(0)
                fixed["faces"] = await asyncio.get_event_loop().run_in_executor(
                    None,
                    lambda: pipeline.orient_outward(fixed["verts"], fixed["faces"]),
                )

            rV, rF = len(fixed["verts"]), len(fixed["faces"])
            session["mesh"] = fixed
            yield evt({"step": "repair", "status": "done", "progress": 88,
                       "V": rV, "F": rF, "msg": f"복구: {rV:,}V · {rF:,}F"})
            await asyncio.sleep(0)

            # ── Step 4.47: IM 전 마지막 트림 (Taubin/스무딩이 경계 번지게 했을 수 있음) ─
            if instant_meshes:
                fixed = await asyncio.get_event_loop().run_in_executor(
                    None,
                    lambda: pipeline.trim_far_from_points(
                        fixed["verts"], fixed["faces"], pts, max_dist_ratio=0.02,
                    ),
                )
                fixed = await asyncio.get_event_loop().run_in_executor(
                    None,
                    lambda: pipeline.keep_largest_components(
                        fixed["verts"], fixed["faces"], min_ratio=0.05,
                    ),
                )

            # ── Step 4.48: Instant Meshes 리토폴로지 (진짜 field-aligned quad) ──
            # 성공 시 quadify 대신 IM의 quad/tri 결과를 직접 사용
            im_result = None
            if instant_meshes:
                if not im_core.is_available():
                    yield evt({"step": "repair", "status": "active", "progress": 89,
                               "msg": "⚠ Instant Meshes 바이너리 없음 — 스킵"})
                    await asyncio.sleep(0)
                else:
                    yield evt({"step": "repair", "status": "active", "progress": 89,
                               "msg": f"🔷 Instant Meshes 리토폴로지 → {im_target_faces:,} F..."})
                    await asyncio.sleep(0)
                    im_result = await asyncio.get_event_loop().run_in_executor(
                        None, lambda: im_core.retopologize(
                            fixed["verts"], fixed["faces"],
                            target_faces=int(im_target_faces),
                            smooth_iter=2,
                            crease_degrees=float(im_crease),
                            pure_quad=bool(im_pure_quad),
                            align_boundaries=True,
                        )
                    )
                    if im_result.get("ok"):
                        st = im_result["stats"]
                        # IM의 faces는 quad/tri 혼합 — "대표 삼각형" 버전도 같이 만들어둠
                        tri_faces = _im_to_triangles(im_result)
                        fixed = {"verts": im_result["verts"].astype("float32"),
                                 "faces": tri_faces}
                        # IM 결과 후처리: ICP 재적용 (원본 포인트 정합)
                        if int(icp_snap) > 0:
                            fixed["verts"] = await asyncio.get_event_loop().run_in_executor(
                                None, lambda: pipeline.snap_verts_to_points(
                                    fixed["verts"], pts,
                                    iterations=int(icp_snap), strength=0.35,
                                )
                            )
                        # IM 후 2차 메우기 — "아주 작은 핀홀"만 처리해서 fan 자국 최소화
                        # 큰 구멍은 메우지 않음. IM 결과의 quad 토폴로지를 깨뜨리지 않기 위함.
                        # (메워진 부분이 방사형 fan이라 visible하면 오히려 지저분해짐)
                        if fake_hole_fill:
                            before = len(fixed["faces"])
                            fixed = await asyncio.get_event_loop().run_in_executor(
                                None, lambda: pipeline.smart_fill_holes(
                                    fixed["verts"], fixed["faces"], pts,
                                    max_size_ratio=0.03,           # 3% 이하만 — fan 거의 안 보임
                                    auto_fill_small_ratio=0.025,   # 2.5% 이하는 무조건
                                    support_radius_ratio=0.05,
                                    min_support_points=1,
                                ),
                            )
                            added = len(fixed["faces"]) - before
                            if added > 0:
                                # IM 결과는 quad였는데 새로 메운 건 삼각형 — im_result 삼각형에 합침
                                new_tris = [list(f) for f in fixed["faces"][before:]]
                                im_result["tris"].extend(new_tris)
                                st["tris"] = len(im_result["tris"])
                        yield evt({"step": "repair", "status": "done", "progress": 91,
                                   "V": len(fixed["verts"]), "F": st["quads"] + st["tris"],
                                   "msg": f"✨ IM quad={st['quads']:,} · tri={st['tris']:,}"})
                    else:
                        yield evt({"step": "repair", "status": "active", "progress": 89,
                                   "msg": f"⚠ IM 실패: {im_result.get('error','?')} — 기존 quadify로 폴백"})
                    await asyncio.sleep(0)

            # ── Step 4.5: Quadify 준비 (IM 경로만 여기서 처리) ──────────
            # BUG FIX: 이전 버전은 IM이 아닌 경로에서 quadify를 여기 (line 685) 호출하고
            # 이후 QA 패스의 merge/fill/keep_largest가 verts 인덱스를 재배열하는 바람에
            # quads_data의 인덱스가 엉뚱한 vertex를 가리키게 되어 winding이 깨졌다.
            # → 비-IM 경로의 quadify는 QA 패스 "뒤"로 이동 (아래 Step 4.95 참조).
            quads_data = None
            if im_result and im_result.get("ok"):
                quads_data = {
                    "quads": im_result["quads"],
                    "triangles": im_result["tris"],
                }

            # ── Step 4.9: 🔍 최종 QA 패스 (사용자 요청: "한 번 더 검토") ────
            # IM 경로인 경우 quad 인덱스 보존 필요 → 파괴적 수정 스킵, 검증만
            # 비-IM 경로는 full cleanup (merge/fill/fragment/orient 모두)
            yield evt({"step": "repair", "status": "active", "progress": 90,
                       "msg": "🔍 최종 QA 패스 — 한 번 더 검토하는 중..."})
            await asyncio.sleep(0)

            im_active = bool(im_result and im_result.get("ok"))

            if not im_active:
                # 비-IM 경로 — 인덱스 바뀌어도 안전, 완전 정리
                if merge_verts:
                    fixed = await asyncio.get_event_loop().run_in_executor(
                        None,
                        lambda: pipeline.merge_close_vertices(
                            fixed["verts"], fixed["faces"], eps_ratio=1e-5,
                        ),
                    )
                if fake_hole_fill:
                    fixed = await asyncio.get_event_loop().run_in_executor(
                        None,
                        lambda: pipeline.smart_fill_holes(
                            fixed["verts"], fixed["faces"], pts,
                            max_size_ratio=float(fake_hole_size),
                            auto_fill_small_ratio=0.05,
                        ),
                    )
                fixed = await asyncio.get_event_loop().run_in_executor(
                    None,
                    lambda: pipeline.keep_largest_components(
                        fixed["verts"], fixed["faces"], min_ratio=0.01,
                    ),
                )
                if orient_normals:
                    fixed["faces"] = await asyncio.get_event_loop().run_in_executor(
                        None,
                        lambda: pipeline.orient_outward(fixed["verts"], fixed["faces"]),
                    )
            # IM 경로는 이미 위에서 정리됨 (verts/quads 인덱스 불변 유지)

            # ── Step 4.95: Quadify (QA 패스 이후) — 비-IM 경로만 ─────────
            # 이 단계는 반드시 orient_outward 뒤에 와야 함.
            # QA 패스에서 verts/faces가 재배열된 최종 상태에서 quad 생성.
            if not im_active and quadify:
                yield evt({"step": "repair", "status": "active", "progress": 92,
                           "msg": "◻ 삼각면 → 사각면 변환..."})
                await asyncio.sleep(0)
                quads_data = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: pipeline.triangles_to_quads(fixed["verts"], fixed["faces"])
                )
                qCount = len(quads_data["quads"]) if quads_data else 0
                yield evt({"step": "repair", "status": "done", "progress": 93,
                           "msg": f"Quad: {qCount:,}개 변환"})
                await asyncio.sleep(0)

            # 최종 검증 통계 (IM/non-IM 공통)
            final_val = await asyncio.get_event_loop().run_in_executor(
                None, lambda: pipeline.validate(fixed["verts"], fixed["faces"]),
            )
            qa_summary = {
                "watertight": bool(final_val.get("watertight", False)),
                "boundary_edges": int(final_val.get("boundary_edges", 0)),
                "non_manifold": int(final_val.get("non_manifold_edges", 0)),
                "components": int(final_val.get("components", 1)),
                "V": len(fixed["verts"]),
                "F": len(fixed["faces"]),
            }
            qa_msg = (
                f"✅ QA 통과 · {qa_summary['V']:,}V · {qa_summary['F']:,}F · "
                + ("watertight" if qa_summary["watertight"] else f"열린엣지 {qa_summary['boundary_edges']}")
                + (f" · 파편 {qa_summary['components']}" if qa_summary["components"] > 1 else "")
            )
            yield evt({"step": "repair", "status": "done", "progress": 93,
                       "msg": qa_msg, "qa": qa_summary})
            await asyncio.sleep(0)

            session["mesh"] = fixed

            # ── Step 5: OBJ Build ───────────────────────────────────────
            yield evt({"step": "export", "status": "active", "progress": 94})
            await asyncio.sleep(0)

            # ── 색상 그룹 쉐이더 분리 (원본 포인트 색 → K-means → 쉐이더별 그룹) ──
            mtl_text = None
            session_colors = session.get("colors")
            use_color_groups = int(color_groups) >= 2 and session_colors is not None

            if use_color_groups:
                yield evt({"step": "export", "status": "active", "progress": 95,
                           "msg": f"🎨 색상 그룹 {color_groups}개로 클러스터링..."})
                await asyncio.sleep(0)

                V_mesh = fixed["verts"]
                # 색 이식: 원본 포인트 k-NN
                vert_colors = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: pipeline.transfer_colors_knn(
                        V_mesh, session["pts"], session_colors, k=3,
                    ),
                )
                # K-means 클러스터링
                vc_ids, centers = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: pipeline.cluster_colors_kmeans(
                        vert_colors, k=int(color_groups),
                    ),
                )
                # face/quad 별 클러스터 배정 (다수결)
                def _face_cluster(face_list):
                    out = []
                    for f in face_list:
                        c0 = int(vc_ids[f[0]]); c1 = int(vc_ids[f[1]]); c2 = int(vc_ids[f[2]])
                        # 다수결
                        out.append(c0 if (c0 == c1 or c0 == c2) else (c1 if c1 == c2 else c0))
                    return out

                def _quad_cluster(quads_list):
                    out = []
                    for q in quads_list:
                        ids = [int(vc_ids[q[i]]) for i in range(4)]
                        # 4개 중 최빈값
                        from collections import Counter
                        out.append(Counter(ids).most_common(1)[0][0])
                    return out

                if quads_data:
                    quad_clusters = _quad_cluster(quads_data["quads"]) if quads_data["quads"] else []
                    tri_left_clusters = _face_cluster(quads_data["triangles"]) if quads_data["triangles"] else []
                    face_clusters = np.zeros(0, dtype=np.int32)
                    tri_faces_np = np.zeros((0, 3), dtype=np.int32)
                else:
                    tri_faces_np = fixed["faces"]
                    face_clusters = np.asarray(_face_cluster(fixed["faces"]), dtype=np.int32)
                    quad_clusters = None
                    tri_left_clusters = None

                # MTL 텍스트 생성
                mtl_text = export.build_mtl_from_clusters(centers, mtl_prefix="mat")
                # MTL 파일명은 stem + .mtl
                stem = _safe_stem(str(session.get("filename", "mesh")))
                mtl_filename = f"{stem}_mesh.mtl"

                obj_text = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: export.to_obj_multi_material(
                        fixed["verts"], tri_faces_np, face_clusters,
                        quads=(quads_data["quads"] if quads_data else None),
                        quad_clusters=quad_clusters,
                        tris_leftover=(quads_data["triangles"] if quads_data else None),
                        tri_leftover_clusters=tri_left_clusters,
                        mtl_name=mtl_filename,
                        mtl_prefix="mat",
                        smooth_normals=bool(smooth_normals),
                    ),
                )
                session["mtl"] = mtl_text
                session["mtl_name"] = mtl_filename
                session["cluster_count"] = int(len(centers))
            else:
                obj_text = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: export.to_obj_with_quads(
                        fixed["verts"], fixed["faces"],
                        quads=quads_data["quads"] if quads_data else None,
                        tris_leftover=quads_data["triangles"] if quads_data else None,
                        smooth_normals=bool(smooth_normals),
                    )
                )
                # 이전 저장된 mtl 제거
                session.pop("mtl", None)
                session.pop("mtl_name", None)
                session.pop("cluster_count", None)

            session["obj"] = obj_text
            session["_ts"] = time.time()   # 완료 시 TTL 리셋

            done_evt = {"step": "export", "status": "done", "progress": 100,
                       "V": rV, "F": rF, "session_id": sid,
                       "msg": "✅ 완료! OBJ 준비됨"}
            if use_color_groups:
                done_evt["msg"] = f"✅ 완료! OBJ + MTL ({session['cluster_count']}개 쉐이더)"
                done_evt["cluster_count"] = session["cluster_count"]
                done_evt["has_mtl"] = True
            yield evt(done_evt)

        except Exception as e:
            yield evt({"error": str(e), "step": "error"})

    return StreamingResponse(
        _stream(),
        media_type="text/event-stream",
        headers={
            "Cache-Control":    "no-cache",
            "X-Accel-Buffering":"no",
            "Connection":       "keep-alive",
        },
    )


# ── Mesh Collider (page 2용 정밀 콜라이더) ────────────────────────────────
@app.get("/api/mesh-collider/{sid}")
async def mesh_collider(
    sid: str,
    method: str = "poisson",      # poisson | bpa
    depth: int = 8,
    target_tris: int = 4000,
    snap: int = 3,
    convex_parts: bool = False,   # True → ACD 분해
    max_parts: int = 12,
    zup_to_yup: bool = False,     # Z-up → Y-up 변환
    max_edge_ratio: float = 4.0,  # 긴 엣지 프루닝 (공간 가로지르는 실 제거)
    density_trim: float = 0.08,   # Poisson 저밀도 트림
    keep_fragments: bool = False, # 파편 유지 여부
    instant_meshes: bool = False, # IM 리토폴로지 (토폴로지 균일화)
    im_target_faces: int = 2000,  # IM 목표 면 수
    im_pure_quad: bool = False,   # 콜라이더는 tri로 써야 하니 기본 False
):
    """page 2: 포인트 클라우드에 실제로 밀착하는 메쉬 콜라이더 생성.
    Convex Hull보다 훨씬 정확 — 오목한 영역까지 따라감."""
    s = _sessions.get(sid)
    if not s:
        raise HTTPException(404, "세션을 찾을 수 없습니다")
    s["_ts"] = time.time()

    pts = s["pts"]
    nrm = s.get("normals")

    if zup_to_yup:
        # Z-up → Y-up (Unity/Three.js 좌표계)
        pts_t = pts.copy()
        pts_t[:, [1, 2]] = pts_t[:, [2, 1]]
        pts_t[:, 2] = -pts_t[:, 2]
        pts = pts_t.astype(np.float32)
        if nrm is not None:
            nn = nrm.copy()
            nn[:, [1, 2]] = nn[:, [2, 1]]
            nn[:, 2] = -nn[:, 2]
            nrm = nn.astype(np.float32)

    try:
        result = await asyncio.get_event_loop().run_in_executor(
            None,
            lambda: collider_core.build_mesh_collider(
                pts, nrm,
                method=method, depth=depth,
                target_tris=target_tris,
                snap_strength=snap,
                convex_parts=convex_parts,
                max_parts=max_parts,
                max_edge_ratio=max_edge_ratio,
                density_trim=density_trim,
                keep_fragments=keep_fragments,
                instant_meshes=instant_meshes,
                im_target_faces=im_target_faces,
                im_pure_quad=im_pure_quad,
            ),
        )
    except Exception as e:
        raise HTTPException(500, f"메쉬 콜라이더 생성 실패: {e}")

    return result


# ── Mesh download ──────────────────────────────────────────────────────────
@app.get("/api/mesh/{sid}")
async def get_mesh(sid: str):
    s = _sessions.get(sid)
    if not s or "obj" not in s:
        raise HTTPException(404, "메쉬가 없습니다. 먼저 파이프라인을 실행하세요.")
    base = _safe_stem(str(s.get("filename", "mesh")))
    fname = f"{base}_mesh.obj"
    return Response(
        content=s["obj"],
        media_type="text/plain",
        headers={"Content-Disposition": f'attachment; filename="{fname}"'},
    )


# ══════════════════════════════════════════════════════════════════════════════
# 🚀 전체 자동 처리 — Page 1~4 순차 실행, 세션에 모든 결과 저장
# ══════════════════════════════════════════════════════════════════════════════
class AutomateParams(BaseModel):
    lod: str = "quality"            # fast | balanced | quality
    tex_size: int = 2048            # Page 4 텍스처 크기
    collider_tris: int = 4000       # Page 2 collider 목표 면 수


@app.post("/api/automate/{sid}")
async def automate(sid: str, params: AutomateParams = AutomateParams()):
    lod           = params.lod
    tex_size      = params.tex_size
    collider_tris = params.collider_tris
    """
    PLY 하나 업로드 + 자동화 버튼 누르면 Page 2/3/4 전부 SSE로 순차 실행.
    각 단계 결과는 세션에 저장돼서 각 페이지에서 바로 다운로드/미리보기 가능.
    """
    if sid not in _sessions:
        raise HTTPException(404, "세션을 찾을 수 없습니다")
    session = _sessions[sid]
    session["_ts"] = time.time()

    # LOD 프리셋 매핑 (Page 3 기본)
    lod_map = {
        "fast":     dict(algo="mc", depth=7, iter=1, im_faces=1200, icp=0,
                          fake_hole=True, merge=True, orient=True),
        "balanced": dict(algo="poisson", depth=7, iter=2, im_faces=3700, icp=0,
                          fake_hole=True, merge=True, orient=True),
        "quality":  dict(algo="poisson", depth=8, iter=2, im_faces=6200, icp=3,
                          fake_hole=True, merge=True, orient=True),
    }
    params = lod_map.get(lod, lod_map["quality"])

    async def _stream():
        def evt(data):
            return f"data: {json.dumps(data, ensure_ascii=False)}\n\n"

        try:
            pts = session["pts"]
            nrm = session.get("normals")

            # ── Phase 1: Page 2 자동 메쉬 콜라이더 ────────────────────
            yield evt({"phase": "collider", "step": "active", "progress": 5,
                       "msg": "🎮 Page 2: 메쉬 콜라이더 생성 중..."})
            await asyncio.sleep(0)
            try:
                col_result = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: collider_core.build_mesh_collider(
                        pts, nrm, method="poisson", depth=8,
                        target_tris=int(collider_tris), snap_strength=3,
                        convex_parts=False,
                    ),
                )
                session["auto_collider"] = col_result
                yield evt({"phase": "collider", "step": "done", "progress": 20,
                           "msg": f"✓ 메쉬 콜라이더: V={col_result['verts_total']:,} F={col_result['tris_total']:,}"})
            except Exception as e:
                yield evt({"phase": "collider", "step": "skipped",
                           "msg": f"⚠ 콜라이더 생성 스킵: {str(e)[:60]}"})

            # ── Phase 2: Page 3 메쉬 변환 ────────────────────────────
            yield evt({"phase": "mesh", "step": "active", "progress": 25,
                       "msg": f"🔺 Page 3: 메쉬 변환 ({lod.upper()}) 중..."})
            await asyncio.sleep(0)

            # SOR
            if nrm is not None and len(nrm) == len(pts):
                p2, n2 = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: pipeline.sor_with_normals(pts, nrm, sigma=2.0),
                )
            else:
                p2 = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: pipeline.sor(pts, sigma=2.0),
                )
                n2 = None

            # Surface reconstruction
            if params["algo"] == "poisson":
                raw = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: pipeline.build_poisson_mesh(p2, n2, depth=params["depth"]),
                )
                raw = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: pipeline.trim_far_from_points(raw["verts"], raw["faces"], p2, 0.015),
                )
                raw = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: pipeline.keep_largest_components(raw["verts"], raw["faces"], 0.05),
                )
                raw = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: pipeline.smart_fill_holes(raw["verts"], raw["faces"], p2,
                                                             max_size_ratio=0.15, auto_fill_small_ratio=0.10,
                                                             support_radius_ratio=0.08, min_support_points=1),
                )
            else:
                raw = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: pipeline.build_mc_mesh(p2, grid_res=50),
                )

            yield evt({"phase": "mesh", "step": "active", "progress": 40,
                       "msg": "  ↳ repair + Taubin + prune..."})
            await asyncio.sleep(0)

            val = await asyncio.get_event_loop().run_in_executor(
                None, lambda: pipeline.validate(raw["verts"], raw["faces"]),
            )
            m = await asyncio.get_event_loop().run_in_executor(
                None, lambda: pipeline.repair(raw["verts"], raw["faces"], val),
            )
            m["verts"] = await asyncio.get_event_loop().run_in_executor(
                None, lambda: pipeline.taubin_smooth(m["verts"], m["faces"], params["iter"]),
            )
            m = await asyncio.get_event_loop().run_in_executor(
                None, lambda: pipeline.prune_long_edges(m["verts"], m["faces"]),
            )
            m = await asyncio.get_event_loop().run_in_executor(
                None, lambda: pipeline.keep_largest_components(m["verts"], m["faces"], 0.05),
            )

            # IM 리토폴로지
            if im_core.is_available():
                yield evt({"phase": "mesh", "step": "active", "progress": 55,
                           "msg": f"  ↳ Instant Meshes → {params['im_faces']:,} quad..."})
                await asyncio.sleep(0)
                im_result = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: im_core.retopologize(
                        m["verts"], m["faces"],
                        target_faces=params["im_faces"], pure_quad=True,
                        align_boundaries=False, smooth_iter=2,
                    ),
                )
                if im_result.get("ok"):
                    tris_list = list(im_result.get("tris", []))
                    for q in im_result.get("quads", []):
                        tris_list.append([q[0], q[1], q[2]])
                        tris_list.append([q[0], q[2], q[3]])
                    m = {
                        "verts": im_result["verts"].astype(np.float32),
                        "faces": np.asarray(tris_list, dtype=np.int32),
                    }
                    if int(params["icp"]) > 0:
                        m["verts"] = await asyncio.get_event_loop().run_in_executor(
                            None, lambda: pipeline.snap_verts_to_points(
                                m["verts"], pts, iterations=int(params["icp"]), strength=0.4,
                            ),
                        )

            # merge + orient
            if params["merge"]:
                m = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: pipeline.merge_close_vertices(m["verts"], m["faces"], 1e-5),
                )
            if params["orient"]:
                m["faces"] = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: pipeline.orient_outward(m["verts"], m["faces"]),
                )

            session["mesh"] = m

            # OBJ 생성
            quads_data = await asyncio.get_event_loop().run_in_executor(
                None, lambda: pipeline.triangles_to_quads(m["verts"], m["faces"]),
            )
            obj_text = await asyncio.get_event_loop().run_in_executor(
                None, lambda: export.to_obj_with_quads(
                    m["verts"], m["faces"],
                    quads=quads_data["quads"], tris_leftover=quads_data["triangles"],
                    smooth_normals=True,
                ),
            )
            session["obj"] = obj_text

            yield evt({"phase": "mesh", "step": "done", "progress": 65,
                       "msg": f"✓ 메쉬: V={len(m['verts']):,}  F={len(m['faces']):,}"})

            # ── Phase 3: Page 4 텍스처 베이크 ────────────────────────
            colors = session.get("colors")
            if colors is None:
                yield evt({"phase": "bake", "step": "skipped",
                           "msg": "⚠ PLY에 색상 없음 — 텍스처 베이크 스킵"})
            else:
                yield evt({"phase": "bake", "step": "active", "progress": 70,
                           "msg": f"🖼 Page 4: {tex_size}×{tex_size} 텍스처 베이킹..."})
                await asyncio.sleep(0)
                try:
                    bake_result = await asyncio.get_event_loop().run_in_executor(
                        None, lambda: uvb_core.bake_texture_pipeline(
                            m["verts"], m["faces"], pts, colors,
                            tex_size=int(tex_size), ao_strength=0.5,
                            bake_lighting_on=True,
                        ),
                    )
                    session["bake_base_tex"] = bake_result["texture"]
                    session["bake_cur_tex"]  = bake_result["texture"]
                    session["bake_verts"] = bake_result["verts"]
                    session["bake_faces"] = bake_result["faces"]
                    session["bake_uvs"]   = bake_result["uvs"]
                    session["bake_mesh_verts"] = m["verts"]
                    session["bake_mesh_faces"] = m["faces"]
                    session["bake_hsv"] = {"hue": 0.0, "saturation": 1.0, "brightness": 1.0}
                    session["_kind"] = "auto"
                    yield evt({"phase": "bake", "step": "done", "progress": 95,
                               "msg": f"✓ 텍스처: {bake_result['stats']['tex_size']}×{bake_result['stats']['tex_size']}"})
                except Exception as e:
                    yield evt({"phase": "bake", "step": "skipped",
                               "msg": f"⚠ 베이크 실패: {str(e)[:80]}"})

            session["auto_done"] = True
            session["_ts"] = time.time()
            yield evt({"phase": "complete", "step": "done", "progress": 100,
                       "msg": "🎉 전체 자동 처리 완료! 각 페이지에서 결과 확인/다운로드"})

        except Exception as e:
            yield evt({"phase": "error", "error": str(e)})

    return StreamingResponse(
        _stream(),
        media_type="text/event-stream",
        headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"},
    )


# ══════════════════════════════════════════════════════════════════════════════
# Page 4 — 텍스처 베이킹 (UV unwrap + 포인트클라우드 색 → 2K texture)
# ══════════════════════════════════════════════════════════════════════════════
def _parse_obj_simple(text: str):
    """텍스트 OBJ 파싱 → (V, F). quad는 2 tri로 분해."""
    import numpy as _np
    V = []
    F = []
    for ln in text.split("\n"):
        p = ln.strip().split()
        if not p:
            continue
        if p[0] == "v" and len(p) >= 4:
            try:
                V.append([float(p[1]), float(p[2]), float(p[3])])
            except ValueError:
                pass
        elif p[0] == "f" and len(p) >= 4:
            idx = [int(s.split("/")[0]) - 1 for s in p[1:]]
            if len(idx) == 3:
                F.append(idx)
            elif len(idx) == 4:
                F.append([idx[0], idx[1], idx[2]])
                F.append([idx[0], idx[2], idx[3]])
            elif len(idx) > 4:
                for i in range(1, len(idx) - 1):
                    F.append([idx[0], idx[i], idx[i + 1]])
    return (_np.asarray(V, dtype=_np.float32),
            _np.asarray(F, dtype=_np.int32))


@app.post("/api/bake/upload")
async def bake_upload(
    ply: UploadFile = File(...),
    obj: UploadFile = File(...),
):
    """Page 4 입력: PLY + 메쉬(OBJ/FBX/GLB) 동시 업로드 → 베이크 세션."""
    if not _ext_ok(ply.filename or ""):
        raise HTTPException(415, "PLY 확장자가 지원되지 않습니다")
    mesh_name = (obj.filename or "").lower()
    if not (mesh_name.endswith(".obj") or mesh_name.endswith(".fbx") or mesh_name.endswith(".glb")):
        raise HTTPException(415, "메쉬는 .obj, .fbx, .glb 중 하나여야 합니다")

    ply_data = await ply.read()
    mesh_data = await obj.read()

    try:
        loaded = loader.load_full(ply.filename, ply_data)
    except Exception as e:
        raise HTTPException(422, f"PLY 파싱 실패: {e}")
    pts = loaded["pts"]
    colors = loaded.get("colors")
    if colors is None:
        colors = np.ones((len(pts), 3), dtype=np.float32) * 0.6

    # 메쉬 파서 — 확장자 기반 자동 선택
    try:
        if mesh_name.endswith(".obj"):
            obj_text = mesh_data.decode("utf-8", errors="replace")
            mv, mf = _parse_obj_simple(obj_text)
        elif mesh_name.endswith(".fbx"):
            from backend.core import fbx_binary_export as fbx_bin_core
            mv, mf = fbx_bin_core.parse_fbx_binary(mesh_data)
        elif mesh_name.endswith(".glb"):
            from backend.core import glb_export as glb_core
            mv, mf = glb_core.parse_glb(mesh_data)
        else:
            raise HTTPException(415, "지원하지 않는 메쉬 형식")
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(422, f"메쉬 파싱 실패: {e}")

    if len(mv) < 4 or len(mf) < 2:
        raise HTTPException(422, "메쉬에 유효한 지오메트리가 없습니다")

    _gc_sessions()
    sid = uuid.uuid4().hex[:12]
    _sessions[sid] = {
        "pts": pts, "colors": colors,
        "bake_mesh_verts": mv, "bake_mesh_faces": mf,
        "filename": ply.filename, "_ts": time.time(),
        "_kind": "bake",
    }
    return {
        "session_id": sid,
        "ply_points": int(len(pts)),
        "mesh_verts": int(len(mv)),
        "mesh_faces": int(len(mf)),
        "has_colors": bool(loaded.get("colors") is not None),
    }


@app.post("/api/bake/run/{sid}")
async def bake_run(
    sid: str,
    tex_size: int = 2048,
    ao_strength: float = 0.5,
    lighting: bool = True,
    light_x: float = 0.3,
    light_y: float = 1.0,
    light_z: float = 0.3,
):
    """베이크 실행. 결과 PNG는 세션에 저장. 재실행 시 덮어씀."""
    s = _sessions.get(sid)
    if not s or "bake_mesh_verts" not in s:
        raise HTTPException(404, "베이크 세션을 찾을 수 없습니다")
    s["_ts"] = time.time()

    try:
        result = await asyncio.get_event_loop().run_in_executor(
            None,
            lambda: uvb_core.bake_texture_pipeline(
                s["bake_mesh_verts"], s["bake_mesh_faces"],
                s["pts"], s["colors"],
                tex_size=int(max(256, min(4096, tex_size))),
                ao_strength=float(ao_strength),
                bake_lighting_on=bool(lighting),
                light_dir=(float(light_x), float(light_y), float(light_z)),
            ),
        )
    except Exception as e:
        raise HTTPException(500, f"베이크 실패: {e}")

    s["bake_base_tex"] = result["texture"]        # 원본 베이크 (HSV 조정 기준)
    s["bake_cur_tex"]  = result["texture"]        # 현재 표시용 (조정 적용됨)
    s["bake_verts"] = result["verts"]
    s["bake_faces"] = result["faces"]
    s["bake_uvs"]   = result["uvs"]
    s["bake_hsv"] = {"hue": 0.0, "saturation": 1.0, "brightness": 1.0}

    return {
        "ok": True,
        "stats": result["stats"],
    }


class BakeRunParams(BaseModel):
    tex_size: int    = 2048
    ao_strength: float = 0.5
    lighting: bool    = True


@app.post("/api/bake/run-sse/{sid}")
async def bake_run_sse(sid: str, params: BakeRunParams = BakeRunParams()):
    tex_size    = params.tex_size
    ao_strength = params.ao_strength
    lighting    = params.lighting
    """베이크를 SSE로 스트리밍 — 단계별 진행률 % 전송."""
    s = _sessions.get(sid)
    if not s or "bake_mesh_verts" not in s:
        raise HTTPException(404, "베이크 세션 없음")
    s["_ts"] = time.time()

    async def _stream():
        def evt(data):
            return f"data: {json.dumps(data, ensure_ascii=False)}\n\n"

        try:
            mv, mf = s["bake_mesh_verts"], s["bake_mesh_faces"]
            pts, colors = s["pts"], s["colors"]
            ts = int(max(256, min(4096, tex_size)))

            # 1. UV unwrap
            yield evt({"step": "uv", "progress": 5, "msg": "⚙️ UV 언랩 중 (xatlas)..."})
            await asyncio.sleep(0)
            uv = await asyncio.get_event_loop().run_in_executor(
                None, lambda: uvb_core.uv_unwrap(mv, mf, resolution=ts, padding=4),
            )
            yield evt({"step": "uv", "progress": 20,
                       "msg": f"✓ UV 언랩 완료 (V={len(uv['verts']):,})"})

            # 2. 색 베이크
            yield evt({"step": "bake", "progress": 30, "msg": f"🎨 {ts}×{ts} 컬러 베이킹..."})
            await asyncio.sleep(0)
            color_tex = await asyncio.get_event_loop().run_in_executor(
                None, lambda: uvb_core.bake_color_texture(
                    uv["verts"], uv["faces"], uv["uvs"],
                    pts, colors, tex_size=ts, knn=4,
                ),
            )
            yield evt({"step": "bake", "progress": 70, "msg": "✓ 컬러 베이크 완료"})
            await asyncio.sleep(0)

            # 3. 라이팅
            if lighting:
                yield evt({"step": "light", "progress": 80, "msg": "💡 라이팅 베이킹 (AO + directional)..."})
                await asyncio.sleep(0)
                shading = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: uvb_core.bake_lighting(
                        uv["verts"], uv["faces"], uv["uvs"],
                        tex_size=ts, ao_strength=float(ao_strength),
                    ),
                )
                final_tex = uvb_core.apply_lighting(color_tex, shading)
                yield evt({"step": "light", "progress": 95, "msg": "✓ 라이팅 적용"})
            else:
                final_tex = color_tex

            # 저장
            s["bake_base_tex"] = final_tex
            s["bake_cur_tex"]  = final_tex
            s["bake_verts"] = uv["verts"]
            s["bake_faces"] = uv["faces"]
            s["bake_uvs"]   = uv["uvs"]
            s["bake_hsv"] = {"hue": 0.0, "saturation": 1.0, "brightness": 1.0}
            s["_ts"] = time.time()
            filled = float((final_tex[..., 3] > 0).mean())

            yield evt({"step": "done", "progress": 100,
                       "msg": f"🎉 완료 ({filled*100:.0f}% 채움)",
                       "stats": {
                           "tex_size": ts,
                           "verts": int(len(uv["verts"])),
                           "faces": int(len(uv["faces"])),
                           "filled_ratio": filled,
                       }})
        except Exception as e:
            yield evt({"step": "error", "error": str(e)})

    return StreamingResponse(
        _stream(),
        media_type="text/event-stream",
        headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"},
    )


@app.get("/api/bake/texture/{sid}")
async def bake_get_texture(sid: str, v: int = 0):
    """현재 텍스처 PNG 다운로드. v 파라미터는 캐시 버스터 용."""
    s = _sessions.get(sid)
    if not s or "bake_cur_tex" not in s:
        raise HTTPException(404, "베이크된 텍스처가 없습니다")
    tex = s["bake_cur_tex"]
    png = uvb_core.texture_to_png_bytes(tex)
    return Response(
        content=png,
        media_type="image/png",
        headers={"Cache-Control": "no-store"},
    )


@app.post("/api/bake/adjust/{sid}")
async def bake_adjust(
    sid: str,
    hue: float = 0.0,            # -180 ~ +180
    saturation: float = 1.0,     # 0 ~ 2
    brightness: float = 1.0,     # 0 ~ 2
):
    """원본 베이크에 HSV 조정 적용. 여러 번 눌러도 baseline에서 다시 계산됨."""
    s = _sessions.get(sid)
    if not s or "bake_base_tex" not in s:
        raise HTTPException(404, "베이크된 텍스처가 없습니다")
    s["_ts"] = time.time()

    try:
        adjusted = await asyncio.get_event_loop().run_in_executor(
            None,
            lambda: uvb_core.apply_hsv_adjust(
                s["bake_base_tex"],
                hue_shift=float(hue),
                saturation=float(saturation),
                brightness=float(brightness),
            ),
        )
    except Exception as e:
        raise HTTPException(500, f"HSV 조정 실패: {e}")

    s["bake_cur_tex"] = adjusted
    s["bake_hsv"] = {"hue": float(hue), "saturation": float(saturation), "brightness": float(brightness)}
    return {"ok": True, "hsv": s["bake_hsv"]}


@app.get("/api/bake/mesh/{sid}")
async def bake_get_mesh(sid: str):
    """UV unwrap 된 메쉬를 OBJ로. MTL은 텍스처 1장 참조."""
    s = _sessions.get(sid)
    if not s or "bake_verts" not in s:
        raise HTTPException(404, "베이크 메쉬가 없습니다")

    V = s["bake_verts"]
    F = s["bake_faces"]
    UV = s["bake_uvs"]
    stem = _safe_stem(str(s.get("filename", "mesh")))
    tex_name = f"{stem}_baked.png"
    mtl_name = f"{stem}_baked.mtl"

    lines = [
        "# PointCloud Optimizer — baked texture mesh",
        f"mtllib {mtl_name}",
        "",
    ]
    for v in V:
        lines.append(f"v {v[0]:.6f} {v[1]:.6f} {v[2]:.6f}")
    lines.append("")
    for uv in UV:
        lines.append(f"vt {uv[0]:.6f} {uv[1]:.6f}")
    lines.append("")
    lines.append("usemtl baked_mat")
    for f in F:
        a, b, c = int(f[0]) + 1, int(f[1]) + 1, int(f[2]) + 1
        lines.append(f"f {a}/{a} {b}/{b} {c}/{c}")

    return Response(
        content="\n".join(lines),
        media_type="text/plain",
        headers={"Content-Disposition": f'attachment; filename="{stem}_baked.obj"'},
    )


@app.get("/api/bake/mesh-fbx/{sid}")
async def bake_get_mesh_fbx(sid: str):
    """베이크된 메쉬를 Binary FBX로 다운로드 (UV + 임베드 텍스처 포함)."""
    s = _sessions.get(sid)
    if not s or "bake_verts" not in s:
        raise HTTPException(404, "베이크 메쉬 없음")
    V = s["bake_verts"]; F = s["bake_faces"]; UV = s["bake_uvs"]
    tex = s.get("bake_cur_tex")
    stem = _safe_stem(str(s.get("filename", "mesh")))

    tex_png = None
    if tex is not None:
        tex_png = uvb_core.texture_to_png_bytes(tex)

    try:
        payload = await asyncio.get_event_loop().run_in_executor(
            None, lambda: fbx_bin_core.export_fbx_binary(
                V, F, uvs=UV, materials=[("baked_mat", (1.0, 1.0, 1.0))],
                texture_png=tex_png, texture_name=f"{stem}_baked",
            ),
        )
    except Exception as e:
        raise HTTPException(500, f"FBX 생성 실패: {e}")

    return Response(
        content=payload, media_type="application/octet-stream",
        headers={"Content-Disposition": f'attachment; filename="{stem}_baked.fbx"'},
    )


@app.get("/api/bake/mesh-glb/{sid}")
async def bake_get_mesh_glb(sid: str):
    """베이크된 메쉬를 GLB로 다운로드 (UV + 텍스처 임베드)."""
    s = _sessions.get(sid)
    if not s or "bake_verts" not in s:
        raise HTTPException(404, "베이크 메쉬 없음")
    V = s["bake_verts"]; F = s["bake_faces"]; UV = s["bake_uvs"]
    tex = s.get("bake_cur_tex")

    # 텍스처 PNG bytes (GLB 임베드용)
    tex_png = None
    if tex is not None:
        tex_png = uvb_core.texture_to_png_bytes(tex)

    try:
        payload = await asyncio.get_event_loop().run_in_executor(
            None, lambda: _build_baked_glb(V, F, UV, tex_png),
        )
    except Exception as e:
        raise HTTPException(500, f"GLB 생성 실패: {e}")

    stem = _safe_stem(str(s.get("filename", "mesh")))
    return Response(
        content=payload, media_type="model/gltf-binary",
        headers={"Content-Disposition": f'attachment; filename="{stem}_baked.glb"'},
    )


def _build_baked_glb(V, F, UV, tex_png):
    """베이크 결과를 GLB로 — 텍스처를 바이너리 blob에 임베드."""
    import pygltflib
    from pygltflib import (
        GLTF2, Scene, Node, Mesh, Primitive, Attributes,
        Accessor, BufferView, Buffer, Material, PbrMetallicRoughness,
        Image as GLTFImage, Texture as GLTFTexture, Sampler, TextureInfo, Asset,
    )

    V32 = V.astype(np.float32, copy=False)
    I32 = F.astype(np.uint32, copy=False).flatten()
    UV32 = UV.astype(np.float32, copy=False)

    bin_bytes = bytearray()

    def _add_bv(data, target=None):
        while len(bin_bytes) % 4 != 0:
            bin_bytes.append(0)
        off = len(bin_bytes)
        bin_bytes.extend(data)
        bv = BufferView(buffer=0, byteOffset=off, byteLength=len(data))
        if target is not None:
            bv.target = target
        return bv

    buffer_views = []
    accessors = []

    # POSITION
    buffer_views.append(_add_bv(V32.tobytes(), target=34962))
    accessors.append(Accessor(bufferView=0, byteOffset=0, componentType=5126,
                              count=len(V32), type="VEC3",
                              min=V32.min(axis=0).tolist(),
                              max=V32.max(axis=0).tolist()))

    # INDICES
    buffer_views.append(_add_bv(I32.tobytes(), target=34963))
    accessors.append(Accessor(bufferView=1, byteOffset=0, componentType=5125,
                              count=len(I32), type="SCALAR"))

    # TEXCOORD_0
    buffer_views.append(_add_bv(UV32.tobytes(), target=34962))
    accessors.append(Accessor(bufferView=2, byteOffset=0, componentType=5126,
                              count=len(UV32), type="VEC2"))

    # 텍스처 이미지 (PNG 바이트)
    images = []
    textures = []
    samplers = []
    materials = []
    if tex_png:
        buffer_views.append(_add_bv(tex_png))
        img_bv_idx = len(buffer_views) - 1
        images.append(GLTFImage(bufferView=img_bv_idx, mimeType="image/png"))
        samplers.append(Sampler(magFilter=9729, minFilter=9987, wrapS=10497, wrapT=10497))
        textures.append(GLTFTexture(source=0, sampler=0))
        pbr = PbrMetallicRoughness(
            baseColorTexture=TextureInfo(index=0),
            metallicFactor=0.0, roughnessFactor=0.9,
        )
        materials.append(Material(name="baked", pbrMetallicRoughness=pbr, doubleSided=True))
    else:
        materials.append(Material(name="baked",
                                  pbrMetallicRoughness=PbrMetallicRoughness(
                                      baseColorFactor=[1,1,1,1],
                                      metallicFactor=0.0, roughnessFactor=0.9,
                                  ),
                                  doubleSided=True))

    prim = Primitive(
        attributes=Attributes(POSITION=0, TEXCOORD_0=2),
        indices=1, material=0,
    )

    gltf = GLTF2(
        asset=Asset(version="2.0", generator="PointCloud Optimizer"),
        buffers=[Buffer(byteLength=len(bin_bytes))],
        bufferViews=buffer_views,
        accessors=accessors,
        images=images,
        samplers=samplers,
        textures=textures,
        materials=materials,
        meshes=[Mesh(primitives=[prim], name="BakedMesh")],
        nodes=[Node(mesh=0, name="BakedMesh")],
        scenes=[Scene(nodes=[0])],
        scene=0,
    )
    gltf.set_binary_blob(bytes(bin_bytes))
    return b"".join(gltf.save_to_bytes())


@app.get("/api/bake/mtl/{sid}")
async def bake_get_mtl(sid: str):
    """베이크 메쉬용 MTL (텍스처 1장 참조)."""
    s = _sessions.get(sid)
    if not s or "bake_base_tex" not in s:
        raise HTTPException(404, "베이크 MTL 없음")
    stem = _safe_stem(str(s.get("filename", "mesh")))
    tex_name = f"{stem}_baked.png"
    text = (
        "newmtl baked_mat\n"
        "Ka 0.1 0.1 0.1\n"
        "Kd 1.0 1.0 1.0\n"
        "Ks 0.0 0.0 0.0\n"
        "d 1.0\n"
        "illum 1\n"
        f"map_Kd {tex_name}\n"
    )
    return Response(
        content=text,
        media_type="text/plain",
        headers={"Content-Disposition": f'attachment; filename="{stem}_baked.mtl"'},
    )


def _prepare_mesh_for_export(s: dict):
    """세션에서 verts, faces, colors/클러스터 추출 (FBX/GLB 공용)."""
    mesh = s.get("mesh")
    if not mesh:
        raise HTTPException(404, "메쉬 없음. 먼저 파이프라인 실행")
    V = mesh["verts"]; F = mesh["faces"]
    vertex_colors = None
    materials = [("lambert1", (0.8, 0.8, 0.8))]
    face_mat_ids = None
    if s.get("colors") is not None:
        try:
            vc = pipeline.transfer_colors_knn(V, s["pts"], s["colors"], k=3)
            K = int(s.get("cluster_count") or 6)
            ids, centers = pipeline.cluster_colors_kmeans(vc, k=K)
            vertex_colors = centers[ids]
            materials = [(f"mat_{i}", tuple(c.tolist())) for i, c in enumerate(centers)]
            face_mat_ids = pipeline.assign_face_clusters(F, ids)
        except Exception:
            pass
    try:
        from backend.core.export import compute_vertex_normals
        vn = compute_vertex_normals(V, F)
    except Exception:
        vn = None
    return V, F, vn, vertex_colors, face_mat_ids, materials


@app.get("/api/mesh-fbx/{sid}")
async def get_mesh_fbx(sid: str, fmt: str = "autodesk"):
    """
    파이프라인 결과를 FBX로 다운로드.
    fmt=autodesk (default): Blender 4.x를 headless 호출해서 Autodesk SDK 완전 호환
                            FBX 생성 (Unity/Maya 공식 import OK).
                            Blender 미설치 시 자동으로 binary로 fallback.
    fmt=binary: scratch Python writer (Blender ✓, Unity/Maya ✗ corrupted).
    fmt=ascii:  ASCII FBX (Maya ✓, Unity ✓, Blender ✗).
    """
    s = _sessions.get(sid)
    if not s:
        raise HTTPException(404, "세션 없음")
    V, F, vn, vertex_colors, face_mat_ids, materials = _prepare_mesh_for_export(s)

    fmt_l = (fmt or "autodesk").lower()
    try:
        if fmt_l == "ascii":
            content = await asyncio.get_event_loop().run_in_executor(
                None, lambda: fbx_core.export_fbx_ascii(
                    V, F, normals=vn, vertex_colors=vertex_colors,
                    face_mat_ids=face_mat_ids, materials=materials,
                ),
            )
            media = "text/plain"
            payload = content.encode("utf-8")
        elif fmt_l == "autodesk":
            # Blender bridge 시도 — OBJ 만들어서 Blender로 FBX 변환
            from backend.core import fbx_blender_bridge as br
            if br.is_available():
                import tempfile
                from backend.core import export as exp_core
                with tempfile.TemporaryDirectory(prefix="pco_fbx_") as tdir:
                    tdir = Path(tdir)
                    obj_text = await asyncio.get_event_loop().run_in_executor(
                        None, lambda: exp_core.to_obj(V, F, smooth_normals=True),
                    )
                    obj_path = tdir / "mesh.obj"
                    fbx_path = tdir / "mesh.fbx"
                    obj_path.write_text(obj_text, encoding="utf-8")
                    ok, msg = await asyncio.get_event_loop().run_in_executor(
                        None, lambda: br.export_via_blender(
                            obj_path, fbx_path, timeout_sec=120,
                        ),
                    )
                    if ok:
                        payload = fbx_path.read_bytes()
                        media = "application/octet-stream"
                    else:
                        # fallback to scratch binary
                        payload = await asyncio.get_event_loop().run_in_executor(
                            None, lambda: fbx_bin_core.export_fbx_binary(
                                V, F, normals=vn, vertex_colors=vertex_colors,
                                face_mat_ids=face_mat_ids, materials=materials,
                            ),
                        )
                        media = "application/octet-stream"
            else:
                # Blender 없음 — binary로 fallback
                payload = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: fbx_bin_core.export_fbx_binary(
                        V, F, normals=vn, vertex_colors=vertex_colors,
                        face_mat_ids=face_mat_ids, materials=materials,
                    ),
                )
                media = "application/octet-stream"
        else:  # binary
            payload = await asyncio.get_event_loop().run_in_executor(
                None, lambda: fbx_bin_core.export_fbx_binary(
                    V, F, normals=vn, vertex_colors=vertex_colors,
                    face_mat_ids=face_mat_ids, materials=materials,
                ),
            )
            media = "application/octet-stream"
    except Exception as e:
        raise HTTPException(500, f"FBX 생성 실패: {e}")

    base = _safe_stem(str(s.get("filename", "mesh")))
    fname = f"{base}_mesh.fbx"
    return Response(
        content=payload, media_type=media,
        headers={"Content-Disposition": f'attachment; filename="{fname}"'},
    )


@app.get("/api/mesh-glb/{sid}")
async def get_mesh_glb(sid: str):
    """파이프라인 결과를 GLB(Binary glTF)로 다운로드 — Blender/Unity/three.js 모두 호환."""
    s = _sessions.get(sid)
    if not s:
        raise HTTPException(404, "세션 없음")
    V, F, vn, vertex_colors, face_mat_ids, materials = _prepare_mesh_for_export(s)

    try:
        payload = await asyncio.get_event_loop().run_in_executor(
            None, lambda: glb_core.export_glb(
                V, F, normals=vn, vertex_colors=vertex_colors,
                face_mat_ids=face_mat_ids, materials=materials,
            ),
        )
    except Exception as e:
        raise HTTPException(500, f"GLB 생성 실패: {e}")

    base = _safe_stem(str(s.get("filename", "mesh")))
    fname = f"{base}_mesh.glb"
    return Response(
        content=payload, media_type="model/gltf-binary",
        headers={"Content-Disposition": f'attachment; filename="{fname}"'},
    )


@app.get("/api/auto/collider/{sid}")
async def get_auto_collider(sid: str):
    """자동 파이프라인이 만든 Page 2 콜라이더 Unity JSON 다운로드."""
    s = _sessions.get(sid)
    if not s or "auto_collider" not in s:
        raise HTTPException(404, "자동 콜라이더 없음 — 자동 처리를 먼저 실행하세요")
    col = s["auto_collider"]
    payload = {
        "version": "2.0-auto",
        "generated": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "pointCount": len(s.get("pts", [])),
        "colliders": [{
            "type": "mesh" if col["mode"] == "mesh" else "convex_part",
            "name": f"AutoMeshCollider_{i}",
            "vertexCount": len(p["vertices"]),
            "triangleCount": len(p["triangles"]),
            "vertices": [{"x": v[0], "y": v[1], "z": v[2]} for v in p["vertices"]],
            "triangles": p["triangles"],
        } for i, p in enumerate(col["parts"])],
    }
    base = _safe_stem(str(s.get("filename", "mesh")))
    fname = f"{base}_colliders.json"
    return Response(
        content=json.dumps(payload, ensure_ascii=False, indent=2),
        media_type="application/json",
        headers={"Content-Disposition": f'attachment; filename="{fname}"'},
    )


@app.get("/api/auto/status/{sid}")
async def get_auto_status(sid: str):
    """세션이 자동처리 상태인지 각 페이지별 결과 체크."""
    s = _sessions.get(sid)
    if not s:
        raise HTTPException(404, "세션 없음")
    return {
        "auto_done": bool(s.get("auto_done")),
        "has_collider": "auto_collider" in s,
        "has_mesh": "mesh" in s and "obj" in s,
        "has_texture": "bake_cur_tex" in s,
        "has_colors_in_ply": s.get("colors") is not None,
    }


@app.get("/api/mesh-colors/{sid}")
async def get_mesh_colors(sid: str):
    """
    Page 3 뷰어용 — 클러스터 색을 per-vertex RGB로 반환.
    JSON: { "colors": [[r,g,b], ...] }  (0~1 float)
    색상 그룹 파이프라인이 돈 세션만 유효.
    """
    s = _sessions.get(sid)
    if not s:
        raise HTTPException(404, "세션 없음")
    # session 내부 'mesh' = {verts, faces}, 그리고 색 이식·클러스터 결과는 파이프라인 중간 단계에만 계산됨.
    # 즉시 반영 위해 여기서 재계산 (가볍고 결정론적).
    mesh = s.get("mesh")
    if not mesh or "verts" not in mesh:
        raise HTTPException(404, "메쉬 없음. 먼저 파이프라인 실행")
    if s.get("colors") is None:
        # 색 없으면 회색
        V = mesh["verts"]
        return {"colors": [[0.6, 0.6, 0.6]] * len(V), "has_colors": False}

    try:
        vc = pipeline.transfer_colors_knn(
            mesh["verts"], s["pts"], s["colors"], k=3,
        )
        # 클러스터 K: session에 저장된 값 있으면 사용, 없으면 6 기본
        K = int(s.get("cluster_count") or 6)
        ids, centers = pipeline.cluster_colors_kmeans(vc, k=K)
        # 각 버텍스에 해당 클러스터 센터 색 배정
        per_vertex = centers[ids]
        return {
            "colors": per_vertex.round(4).tolist(),
            "cluster_count": int(len(centers)),
            "cluster_palette": centers.round(4).tolist(),
            "has_colors": True,
        }
    except Exception as e:
        raise HTTPException(500, f"색 계산 실패: {e}")


@app.get("/api/mtl/{sid}")
async def get_mtl(sid: str):
    """색상 그룹 쉐이더 분리 시 생성된 MTL 다운로드."""
    s = _sessions.get(sid)
    if not s or not s.get("mtl"):
        raise HTTPException(404, "MTL이 없습니다. 색상 그룹 옵션을 켜고 파이프라인을 실행하세요.")
    fname = s.get("mtl_name") or f"{_safe_stem(str(s.get('filename','mesh')))}_mesh.mtl"
    return Response(
        content=s["mtl"],
        media_type="text/plain",
        headers={"Content-Disposition": f'attachment; filename="{fname}"'},
    )


# ── Page 5: 사진 → 메쉬 텍스처 투영 ────────────────────────────────────
# 1) 메쉬(.obj/.fbx/.glb) + 사진 4~10장 업로드 → 세션
# 2) SSE로 pycolmap SfM → ICP 정합 → UV 언랩 → projective texturing 실행
# 3) 결과 텍스처(PNG) + 메쉬(OBJ) 다운로드

@app.post("/api/phototex/upload")
async def phototex_upload(request: Request):
    """multipart: mesh (file) + photo_1..N (files). N in [4, 10]."""
    form = await request.form()
    mesh_file = form.get("mesh")
    if mesh_file is None:
        raise HTTPException(400, "메쉬 파일 필요 (field 'mesh')")

    # Collect photos (photo_0, photo_1, …)
    photos = []
    for k in sorted(form.keys()):
        if k.startswith("photo_"):
            photos.append(form[k])
    if not (4 <= len(photos) <= 10):
        raise HTTPException(400, f"사진 4~10장 필요 (현재 {len(photos)}장)")

    # Save to temp workdir (persists for session TTL)
    import tempfile
    sid = uuid.uuid4().hex[:12]
    workdir = Path(tempfile.gettempdir()) / f"pco_phototex_{sid}"
    workdir.mkdir(parents=True, exist_ok=True)

    mesh_name = (mesh_file.filename or "mesh.obj").lower()
    mesh_path = workdir / (mesh_file.filename or "mesh.obj")
    mesh_bytes = await mesh_file.read()
    mesh_path.write_bytes(mesh_bytes)

    def _parse_ply_mesh(data: bytes):
        """PLY (triangle mesh) 파서 — 해피/바이너리 둘 다."""
        import open3d as o3d
        tmp = workdir / "_mesh.ply"
        tmp.write_bytes(data)
        m = o3d.io.read_triangle_mesh(str(tmp))
        v = np.asarray(m.vertices, dtype=np.float32)
        f = np.asarray(m.triangles, dtype=np.int32)
        if len(f) < 2:
            raise RuntimeError("PLY에 삼각형 면이 없습니다 (포인트 클라우드 전용). Page 3에서 메쉬 변환 후 사용하세요.")
        return v, f

    photo_paths: list[Path] = []
    for i, ph in enumerate(photos):
        ext = Path(ph.filename or f"p{i}.jpg").suffix or ".jpg"
        if ext.lower() not in (".jpg", ".jpeg", ".png", ".webp"):
            ext = ".jpg"
        p = workdir / f"photo_{i:02d}{ext}"
        p.write_bytes(await ph.read())
        photo_paths.append(p)

    # Parse mesh
    try:
        if mesh_name.endswith(".obj"):
            mv, mf = _parse_obj_simple(mesh_bytes.decode("utf-8", errors="replace"))
        elif mesh_name.endswith(".fbx"):
            mv, mf = fbx_bin_core.parse_fbx_binary(mesh_bytes)
        elif mesh_name.endswith(".glb"):
            mv, mf = glb_core.parse_glb(mesh_bytes)
        elif mesh_name.endswith(".ply"):
            mv, mf = _parse_ply_mesh(mesh_bytes)
        else:
            raise HTTPException(415, "메쉬는 .obj, .fbx, .glb, .ply 중 하나여야 합니다")
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(422, f"메쉬 파싱 실패: {e}")

    if len(mv) < 4 or len(mf) < 2:
        raise HTTPException(422, "메쉬에 유효한 지오메트리가 없습니다")

    _gc_sessions()
    _sessions[sid] = {
        "_kind": "phototex",
        "mesh_verts": mv, "mesh_faces": mf,
        "photo_paths": photo_paths,
        "workdir": workdir,
        "filename": mesh_file.filename or "mesh",
        "_ts": time.time(),
    }
    return {
        "session_id": sid,
        "mesh_verts": int(len(mv)),
        "mesh_faces": int(len(mf)),
        "n_photos":   len(photo_paths),
    }


class PhotoTexParams(BaseModel):
    tex_size: int = 2048


@app.post("/api/phototex/run-sse/{sid}")
async def phototex_run(sid: str, params: PhotoTexParams = PhotoTexParams()):
    tex_size = params.tex_size
    """SSE: SfM → align → UV → project."""
    s = _sessions.get(sid)
    if not s or s.get("_kind") != "phototex":
        raise HTTPException(404, "phototex 세션 없음")
    s["_ts"] = time.time()

    async def _stream():
        def evt(data):
            return f"data: {json.dumps(data, ensure_ascii=False)}\n\n"

        import asyncio, queue, threading

        q: asyncio.Queue = asyncio.Queue()
        loop = asyncio.get_event_loop()

        def prog(step, msg, pct):
            loop.call_soon_threadsafe(q.put_nowait, {"step": step, "msg": msg, "progress": pct})

        result_holder = {}
        def _run():
            try:
                r = photo_tex_core.run_pipeline(
                    s["mesh_verts"], s["mesh_faces"],
                    s["photo_paths"],
                    tex_size=int(max(256, min(4096, tex_size))),
                    progress_cb=prog,
                )
                result_holder["ok"] = r
            except Exception as e:
                import traceback
                result_holder["err"] = str(e)
                result_holder["tb"]  = traceback.format_exc()
            loop.call_soon_threadsafe(q.put_nowait, {"_done": True})

        threading.Thread(target=_run, daemon=True).start()

        while True:
            ev = await q.get()
            if ev.get("_done"):
                break
            yield evt(ev)
            await asyncio.sleep(0)

        if "err" in result_holder:
            yield evt({"step": "error", "error": result_holder["err"]})
            return

        r = result_holder["ok"]
        # Store in session
        s["tex"]   = r["texture"]
        s["verts"] = r["verts"]
        s["faces"] = r["faces"]
        s["uvs"]   = r["uvs"]
        s["stats"] = r["stats"]
        yield evt({
            "step": "done", "progress": 100,
            "msg":  "🎉 사진 텍스처 투영 완료",
            "stats": r["stats"],
        })

    return StreamingResponse(_stream(), media_type="text/event-stream",
                              headers={"Cache-Control": "no-cache",
                                        "X-Accel-Buffering": "no"})


@app.get("/api/phototex/texture/{sid}")
async def phototex_texture(sid: str):
    s = _sessions.get(sid)
    if not s or "tex" not in s:
        raise HTTPException(404, "아직 베이크 안 됨")
    s["_ts"] = time.time()
    png = uvb_core.texture_to_png_bytes(s["tex"])
    return Response(content=png, media_type="image/png")


@app.get("/api/phototex/mesh/{sid}")
async def phototex_mesh(sid: str):
    s = _sessions.get(sid)
    if not s or "verts" not in s:
        raise HTTPException(404, "메쉬가 없습니다")
    s["_ts"] = time.time()
    V = s["verts"]; F = s["faces"]; UV = s["uvs"]
    stem = _safe_stem(str(s.get("filename", "mesh")))
    tex_name = f"{stem}_photo.png"
    mtl_name = f"{stem}_photo.mtl"
    lines = [
        "# PointCloud Optimizer — photo-textured mesh",
        f"mtllib {mtl_name}", "",
    ]
    for v in V: lines.append(f"v {v[0]:.6f} {v[1]:.6f} {v[2]:.6f}")
    lines.append("")
    for uv in UV: lines.append(f"vt {uv[0]:.6f} {uv[1]:.6f}")
    lines.append("")
    lines.append("usemtl photo_mat")
    for f in F:
        a, b, c = int(f[0]) + 1, int(f[1]) + 1, int(f[2]) + 1
        lines.append(f"f {a}/{a} {b}/{b} {c}/{c}")
    return Response(
        content="\n".join(lines),
        media_type="text/plain",
        headers={"Content-Disposition": f'attachment; filename="{stem}_photo.obj"'},
    )


@app.get("/api/phototex/mtl/{sid}")
async def phototex_mtl(sid: str):
    s = _sessions.get(sid)
    if not s:
        raise HTTPException(404, "세션 없음")
    stem = _safe_stem(str(s.get("filename", "mesh")))
    mtl = (
        f"newmtl photo_mat\n"
        f"Ka 1 1 1\nKd 1 1 1\nKs 0 0 0\nillum 1\n"
        f"map_Kd {stem}_photo.png\n"
    )
    return Response(content=mtl, media_type="text/plain")


@app.get("/api/phototex/mesh-fbx/{sid}")
async def phototex_mesh_fbx(sid: str):
    """Page 5 결과를 Binary FBX로 (UV + 임베드 텍스처 포함)."""
    s = _sessions.get(sid)
    if not s or "verts" not in s:
        raise HTTPException(404, "메쉬가 없습니다")
    s["_ts"] = time.time()
    V = s["verts"]; F = s["faces"]; UV = s["uvs"]
    stem = _safe_stem(str(s.get("filename", "mesh")))

    tex_png = None
    if "tex" in s and s["tex"] is not None:
        tex_png = uvb_core.texture_to_png_bytes(s["tex"])

    try:
        payload = await asyncio.get_event_loop().run_in_executor(
            None, lambda: fbx_bin_core.export_fbx_binary(
                V, F, uvs=UV, materials=[("photo_mat", (1.0, 1.0, 1.0))],
                texture_png=tex_png, texture_name=f"{stem}_photo",
            ),
        )
    except Exception as e:
        raise HTTPException(500, f"FBX 생성 실패: {e}")

    return Response(
        content=payload, media_type="application/octet-stream",
        headers={"Content-Disposition": f'attachment; filename="{stem}_photo.fbx"'},
    )


@app.get("/api/phototex/mesh-glb/{sid}")
async def phototex_mesh_glb(sid: str):
    """Page 5 결과를 GLB로 (UV + 임베드 텍스처 포함)."""
    s = _sessions.get(sid)
    if not s or "verts" not in s:
        raise HTTPException(404, "메쉬가 없습니다")
    s["_ts"] = time.time()
    V = s["verts"]; F = s["faces"]; UV = s["uvs"]
    stem = _safe_stem(str(s.get("filename", "mesh")))

    tex_png = None
    if "tex" in s and s["tex"] is not None:
        tex_png = uvb_core.texture_to_png_bytes(s["tex"])

    try:
        payload = await asyncio.get_event_loop().run_in_executor(
            None, lambda: _build_baked_glb(V, F, UV, tex_png),
        )
    except Exception as e:
        raise HTTPException(500, f"GLB 생성 실패: {e}")

    return Response(
        content=payload, media_type="model/gltf-binary",
        headers={"Content-Disposition": f'attachment; filename="{stem}_photo.glb"'},
    )


# ── HDRI (Page 4 뷰포트 환경광) ────────────────────────────────────────────
@app.get("/api/hdri/default")
async def hdri_default():
    """PointCloudOptimizer/HDRI/ 폴더에서 기본 HDRI 반환.
    1K→2K→4K 우선순위 (용량 고려, 1K=1.4MB로 빠른 로딩)."""
    hdri_dir = ROOT / "HDRI"
    if not hdri_dir.exists():
        raise HTTPException(404, "HDRI 폴더 없음")
    # 선호 순서: 1K → 2K (4K는 24MB라 스킵)
    preferred = ["studio_kontrast_04_1k.hdr", "studio_kontrast_04_2k.hdr"]
    target = None
    for name in preferred:
        p = hdri_dir / name
        if p.exists():
            target = p
            break
    if target is None:
        # 폴백: .hdr 중 가장 작은 파일
        hdrs = sorted(hdri_dir.glob("*.hdr"), key=lambda p: p.stat().st_size)
        if not hdrs:
            raise HTTPException(404, "HDRI 파일 없음 (.hdr)")
        target = hdrs[0]
    return Response(
        content=target.read_bytes(),
        media_type="application/octet-stream",
        headers={
            "Content-Disposition": f'inline; filename="{target.name}"',
            "X-HDRI-Name": target.name,
            "X-HDRI-Size": str(target.stat().st_size),
        },
    )


# ── Unity .unitypackage 빌드 (Page 2 콜라이더 + PLY) ──────────────────────
@app.post("/api/unitypackage")
async def build_unitypackage(request: Request):
    """Body: multipart/form-data
      - ply  (file): 원본 PLY 바이트
      - colliders_json (str): Unity 콜라이더 JSON 문자열 (exportColliders 결과)
    Returns: application/octet-stream (.unitypackage 바이트)
    """
    form = await request.form()
    ply_file = form.get("ply")
    colliders_json = form.get("colliders_json")
    if ply_file is None or colliders_json is None:
        raise HTTPException(400, "ply 파일과 colliders_json 필드가 필요합니다")
    try:
        ply_name = getattr(ply_file, "filename", "Scene.ply") or "Scene.ply"
        ply_bytes = await ply_file.read()
    except Exception as e:
        raise HTTPException(400, f"PLY 읽기 실패: {e}")
    if not ply_bytes:
        raise HTTPException(400, "빈 PLY 파일")

    try:
        pkg = unitypkg_core.build_unity_package(
            ply_name=str(ply_name),
            ply_bytes=ply_bytes,
            colliders_json_text=str(colliders_json),
        )
    except Exception as e:
        raise HTTPException(500, f".unitypackage 빌드 실패: {e}")

    stem = _safe_stem(str(ply_name))
    return Response(
        content=pkg,
        media_type="application/gzip",
        headers={"Content-Disposition": f'attachment; filename="{stem}_colliders.unitypackage"'},
    )


# ── Validation stats ───────────────────────────────────────────────────────
@app.get("/api/stats/{sid}")
async def get_stats(sid: str):
    s = _sessions.get(sid)
    if not s:
        raise HTTPException(404, "세션을 찾을 수 없습니다")
    return s.get("val", {})


# ── Session management ─────────────────────────────────────────────────────
@app.get("/api/sessions")
async def list_sessions():
    _gc_sessions()
    now = time.time()
    return [
        {
            "sid": sid,
            "filename": s.get("filename", ""),
            "point_count": len(s.get("pts", [])),
            "has_mesh": "obj" in s,
            "age_sec": int(now - s.get("_ts", now)),
        }
        for sid, s in _sessions.items()
    ]


@app.delete("/api/session/{sid}")
async def delete_session(sid: str):
    if sid not in _sessions:
        raise HTTPException(404, "세션을 찾을 수 없습니다")
    _sessions.pop(sid, None)
    return {"ok": True, "deleted": sid}


# ── Health check ───────────────────────────────────────────────────────────
@app.get("/api/health")
async def health():
    _gc_sessions()
    pkg_status = {}
    for pkg in ["numpy", "scipy", "skimage", "trimesh", "open3d", "fastapi"]:
        try:
            __import__(pkg)
            pkg_status[pkg] = "ok"
        except ImportError:
            pkg_status[pkg] = "missing"

    return {
        "status": "ok",
        "version": "3.0.0",
        "packages": pkg_status,
        "sessions": len(_sessions),
        "limits": {
            "max_upload_gb":       MAX_UPLOAD_BYTES // 1024 // 1024 // 1024,
            "session_ttl_sec":     _SESSION_TTL,
            "allowed_extensions":  sorted(ALLOWED_EXTENSIONS),
        },
    }


# ── Serve frontend (must be last) ──────────────────────────────────────────
# index.html에는 no-cache 헤더를 붙여서 수정 즉시 반영되게 함
@app.middleware("http")
async def _no_cache_index(request, call_next):
    resp = await call_next(request)
    p = request.url.path
    if p == "/" or p.endswith(".html") or p.endswith(".js") or p.endswith(".css"):
        resp.headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0"
        resp.headers["Pragma"] = "no-cache"
        resp.headers["Expires"] = "0"
    return resp


_frontend = ROOT / "frontend"
if _frontend.exists():
    app.mount("/", StaticFiles(directory=str(_frontend), html=True), name="frontend")
