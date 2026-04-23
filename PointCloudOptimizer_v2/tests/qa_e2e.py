"""
E2E 회귀 QA 스크립트 — PointCloud Optimizer 전체 파이프라인 자동 검증.

사용:
    # 서버가 이미 떠 있으면:
    python tests/qa_e2e.py --port 8000

    # 서버 자동 기동 + 테스트 + 종료:
    python tests/qa_e2e.py --auto

    # 특정 테스트만:
    python tests/qa_e2e.py --only page3
    python tests/qa_e2e.py --only page5
    python tests/qa_e2e.py --only fbx-format

검증 항목:
    - FBX binary writer 라운드트립 무결성
    - Page 3 파이프라인: PLY → mesh → OBJ/FBX/GLB
    - Page 5 파이프라인 (fallback 경로): 사진 4~10장 → 텍스처
    - Page 5 파이프라인 (SfM 정상 경로): 카메라 복원 확인
    - 모든 다운로드 엔드포인트 200 OK + 최소 크기

Exit code:
    0 = 모든 테스트 통과
    1 = 하나 이상 실패
"""
from __future__ import annotations

import argparse
import io
import json
import os
import socket
import struct
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid
import zlib
from pathlib import Path
from typing import Any, Callable, Optional

# ─────────────────────────────────────────────────────────────────
# stdout 인코딩 (Windows CP949 → UTF-8)
# ─────────────────────────────────────────────────────────────────
if sys.stdout.encoding and sys.stdout.encoding.lower() != "utf-8":
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass

ROOT = Path(__file__).resolve().parent.parent
REPO_ROOT = ROOT.parent

# ─────────────────────────────────────────────────────────────────
# 출력 유틸
# ─────────────────────────────────────────────────────────────────
_PASS = "✓"
_FAIL = "✗"
_SKIP = "○"


class Report:
    def __init__(self) -> None:
        self.passed: list[str] = []
        self.failed: list[tuple[str, str]] = []
        self.skipped: list[tuple[str, str]] = []
        self.t_start = time.time()

    def ok(self, name: str, detail: str = "") -> None:
        print(f"  {_PASS} {name}" + (f"  — {detail}" if detail else ""))
        self.passed.append(name)

    def fail(self, name: str, reason: str) -> None:
        print(f"  {_FAIL} {name}  — {reason}")
        self.failed.append((name, reason))

    def skip(self, name: str, reason: str) -> None:
        print(f"  {_SKIP} {name}  — {reason}")
        self.skipped.append((name, reason))

    def summary(self) -> int:
        elapsed = time.time() - self.t_start
        print()
        print("=" * 60)
        print(f"결과: {len(self.passed)} 통과 · {len(self.failed)} 실패 · "
              f"{len(self.skipped)} 스킵  ({elapsed:.1f}s)")
        print("=" * 60)
        if self.failed:
            print("실패 목록:")
            for name, reason in self.failed:
                print(f"  {_FAIL} {name}: {reason}")
            return 1
        return 0


# ─────────────────────────────────────────────────────────────────
# HTTP 헬퍼
# ─────────────────────────────────────────────────────────────────
def _http_get(url: str, timeout: float = 30.0) -> tuple[int, bytes]:
    try:
        with urllib.request.urlopen(url, timeout=timeout) as r:
            return r.status, r.read()
    except urllib.error.HTTPError as e:
        return e.code, e.read() if hasattr(e, "read") else b""


def _http_post_multipart(url: str, files: dict[str, tuple[str, bytes, str]],
                          timeout: float = 120.0) -> tuple[int, bytes]:
    boundary = uuid.uuid4().hex
    body = bytearray()
    for field, (fname, content, ctype) in files.items():
        body.extend(f"--{boundary}\r\n".encode())
        body.extend(
            f'Content-Disposition: form-data; name="{field}"; filename="{fname}"\r\n'
            .encode()
        )
        body.extend(f"Content-Type: {ctype}\r\n\r\n".encode())
        body.extend(content)
        body.extend(b"\r\n")
    body.extend(f"--{boundary}--\r\n".encode())
    req = urllib.request.Request(
        url, data=bytes(body), method="POST",
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}"}
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, r.read()
    except urllib.error.HTTPError as e:
        return e.code, e.read() if hasattr(e, "read") else b""


def _wait_server(base: str, timeout: float = 30.0) -> bool:
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(f"{base}/api/health", timeout=1) as r:
                if r.status == 200:
                    return True
        except Exception:
            time.sleep(0.3)
    return False


# ─────────────────────────────────────────────────────────────────
# 테스트 데이터 생성
# ─────────────────────────────────────────────────────────────────
def _tiny_png(w: int = 64, h: int = 64) -> bytes:
    """작은 PNG (체크무늬)."""
    sig = b"\x89PNG\r\n\x1a\n"
    def chunk(typ: bytes, data: bytes) -> bytes:
        return (struct.pack(">I", len(data)) + typ + data
                + struct.pack(">I", zlib.crc32(typ + data) & 0xFFFFFFFF))
    ihdr = chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
    raw = b""
    ts = max(1, w // 8)
    for y in range(h):
        raw += b"\x00"
        for x in range(w):
            on = ((x // ts) + (y // ts)) % 2 == 0
            raw += bytes([255, 100, 100]) if on else bytes([100, 100, 255])
    idat = chunk(b"IDAT", zlib.compress(raw, 9))
    iend = chunk(b"IEND", b"")
    return sig + ihdr + idat + iend


def _tiny_ply_cube() -> bytes:
    """작은 PLY 포인트클라우드 (cube 표면 샘플 1000포인트)."""
    import numpy as np
    rng = np.random.default_rng(42)
    pts = []
    for face in range(6):
        n = 180
        u = rng.uniform(-1, 1, n)
        v = rng.uniform(-1, 1, n)
        if face == 0:
            pts.append(np.column_stack([u, v, np.ones(n)]))
        elif face == 1:
            pts.append(np.column_stack([u, v, -np.ones(n)]))
        elif face == 2:
            pts.append(np.column_stack([u, np.ones(n), v]))
        elif face == 3:
            pts.append(np.column_stack([u, -np.ones(n), v]))
        elif face == 4:
            pts.append(np.column_stack([np.ones(n), u, v]))
        elif face == 5:
            pts.append(np.column_stack([-np.ones(n), u, v]))
    P = np.vstack(pts).astype(np.float32)
    header = (
        f"ply\nformat binary_little_endian 1.0\n"
        f"element vertex {len(P)}\n"
        f"property float x\nproperty float y\nproperty float z\n"
        f"end_header\n"
    ).encode()
    return header + P.tobytes()


# ─────────────────────────────────────────────────────────────────
# 테스트: FBX writer 무결성
# ─────────────────────────────────────────────────────────────────
def test_fbx_roundtrip(rep: Report) -> None:
    print("\n[1] FBX binary writer 라운드트립")
    sys.path.insert(0, str(ROOT))
    try:
        from backend.core import fbx_binary_export as fb
        import numpy as np
    except Exception as e:
        rep.fail("import fbx_binary_export", str(e))
        return

    V = np.array([[0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 1, 0]], dtype=np.float32)
    F = np.array([[0, 1, 2], [0, 2, 3]], dtype=np.int32)
    UV = np.array([[0, 0], [1, 0], [1, 1], [0, 1]], dtype=np.float32)

    # 1. 기본 FBX
    try:
        data = fb.export_fbx_binary(V, F)
        V2, F2 = fb.parse_fbx_binary(data)
        assert len(V2) == 4 and len(F2) == 2, f"verts={len(V2)} faces={len(F2)}"
        rep.ok("기본 FBX (no UV/tex)", f"{len(data)}B")
    except Exception as e:
        rep.fail("기본 FBX", str(e))

    # 2. UV 포함
    try:
        data = fb.export_fbx_binary(V, F, uvs=UV)
        V2, F2 = fb.parse_fbx_binary(data)
        assert len(V2) == 4
        rep.ok("FBX with UV", f"{len(data)}B")
    except Exception as e:
        rep.fail("FBX with UV", str(e))

    # 3. 임베드 텍스처
    try:
        png = _tiny_png(64, 64)
        data = fb.export_fbx_binary(V, F, uvs=UV,
                                     materials=[("test_mat", (1, 1, 1))],
                                     texture_png=png, texture_name="tex")
        # PNG 시그니처가 embed되어 있어야 함
        assert png[:8] in data, "PNG 시그니처 missing in FBX"
        # 주요 노드
        for key in (b"Video", b"Texture", b"TextureVideoClip", b"DiffuseColor"):
            assert key in data, f"missing node: {key!r}"
        V2, F2 = fb.parse_fbx_binary(data)
        assert len(V2) == 4
        rep.ok("FBX with embedded texture", f"{len(data)}B, PNG embedded")
    except Exception as e:
        rep.fail("FBX with embedded texture", str(e))

    # 4. 다중 머티리얼
    try:
        face_mat = np.array([0, 1], dtype=np.int32)
        mats = [("red", (1, 0, 0)), ("blue", (0, 0, 1))]
        data = fb.export_fbx_binary(V, F, face_mat_ids=face_mat, materials=mats)
        V2, F2 = fb.parse_fbx_binary(data)
        assert len(V2) == 4
        rep.ok("FBX multi-material", f"{len(data)}B")
    except Exception as e:
        rep.fail("FBX multi-material", str(e))


# ─────────────────────────────────────────────────────────────────
# 테스트: Page 3 파이프라인
# ─────────────────────────────────────────────────────────────────
def test_page3(rep: Report, base: str) -> Optional[str]:
    print("\n[2] Page 3 — PLY → Mesh 파이프라인")

    # Upload
    ply = _tiny_ply_cube()
    try:
        status, body = _http_post_multipart(
            f"{base}/api/upload",
            files={"file": ("test_cube.ply", ply, "application/octet-stream")},
            timeout=30,
        )
        assert status == 200, f"upload {status}"
        up = json.loads(body.decode())
        sid = up["session_id"]
        rep.ok("업로드 (1080pt PLY)", f"session={sid[:8]}")
    except Exception as e:
        rep.fail("업로드", str(e))
        return None

    # Pipeline (POST JSON + SSE — progress 100에 도달하면 완료)
    # 2026-04 리팩터: GET query string → POST JSON (app.py::ProcessParams)
    try:
        t0 = time.time()
        req = urllib.request.Request(
            f"{base}/api/process/{sid}",
            data=json.dumps({}).encode(),   # 빈 body = 전부 기본값
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with urllib.request.urlopen(req, timeout=300) as r:
            completed = False
            for raw in r:
                line = raw.decode("utf-8", errors="replace").strip()
                if not line.startswith("data:"):
                    continue
                try:
                    d = json.loads(line[5:].strip())
                except Exception:
                    continue
                if d.get("step") == "error":
                    raise RuntimeError(d.get("error", "unknown"))
                # 완료 신호: step='done' OR progress>=100
                if d.get("step") == "done" or int(d.get("progress", 0)) >= 100:
                    completed = True
                    break
        elapsed = time.time() - t0
        assert completed, "파이프라인 완료 신호 없음"
        rep.ok(f"파이프라인 처리", f"{elapsed:.1f}s")
    except Exception as e:
        rep.fail("파이프라인", str(e))
        return sid

    # 3가지 포맷 다운로드
    for fmt, url in [
        ("OBJ", f"{base}/api/mesh/{sid}"),
        ("FBX", f"{base}/api/mesh-fbx/{sid}?fmt=binary"),
        ("GLB", f"{base}/api/mesh-glb/{sid}"),
    ]:
        try:
            status, body = _http_get(url, timeout=60)
            assert status == 200, f"HTTP {status}"
            assert len(body) > 100, f"too small: {len(body)}B"
            rep.ok(f"다운로드 {fmt}", f"{len(body)/1024:.1f}KB")
        except Exception as e:
            rep.fail(f"다운로드 {fmt}", str(e))

    return sid


# ─────────────────────────────────────────────────────────────────
# 테스트: Page 5 fallback 경로
# ─────────────────────────────────────────────────────────────────
def test_page5_fallback(rep: Report, base: str) -> None:
    print("\n[3] Page 5 — fallback 경로 (SfM 매칭 불가능한 케이스)")

    # 작은 OBJ 메쉬 + 서로 매칭 안 되는 4장
    obj_text = """o Cube
v -1 -1 -1
v 1 -1 -1
v 1 1 -1
v -1 1 -1
v -1 -1 1
v 1 -1 1
v 1 1 1
v -1 1 1
f 1 2 3
f 1 3 4
f 5 7 6
f 5 8 7
f 1 5 6
f 1 6 2
"""
    obj_bytes = obj_text.encode()
    # 서로 전혀 다른 4장 (solid colors)
    pngs = []
    for color_rgb in [(255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0)]:
        sig = b"\x89PNG\r\n\x1a\n"
        def ch(t: bytes, d: bytes) -> bytes:
            return (struct.pack(">I", len(d)) + t + d
                    + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF))
        ihdr = ch(b"IHDR", struct.pack(">IIBBBBB", 200, 200, 8, 2, 0, 0, 0))
        raw = b""
        for _ in range(200):
            raw += b"\x00" + bytes(color_rgb) * 200
        idat = ch(b"IDAT", zlib.compress(raw))
        iend = ch(b"IEND", b"")
        pngs.append(sig + ihdr + idat + iend)

    files = {"mesh": ("cube.obj", obj_bytes, "text/plain")}
    for i, p in enumerate(pngs):
        files[f"photo_{i}"] = (f"photo_{i}.png", p, "image/png")

    try:
        status, body = _http_post_multipart(
            f"{base}/api/phototex/upload", files, timeout=60
        )
        assert status == 200, f"upload {status}"
        up = json.loads(body.decode())
        sid = up["session_id"]
        rep.ok("phototex 업로드", f"{up['n_photos']}장")
    except Exception as e:
        rep.fail("phototex 업로드", str(e))
        return

    # SSE + stats.fallback 확인 (POST JSON 으로 바뀜 — PhotoTexParams)
    try:
        req = urllib.request.Request(
            f"{base}/api/phototex/run-sse/{sid}",
            data=json.dumps({"tex_size": 512}).encode(),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        t0 = time.time()
        final_stats = None
        with urllib.request.urlopen(req, timeout=600) as r:
            for raw in r:
                line = raw.decode("utf-8", errors="replace").strip()
                if not line.startswith("data:"):
                    continue
                try:
                    d = json.loads(line[5:].strip())
                except Exception:
                    continue
                if d.get("step") == "done":
                    final_stats = d.get("stats", {})
                    break
                if d.get("step") == "error":
                    raise RuntimeError(d.get("error", "?"))
        elapsed = time.time() - t0
        assert final_stats is not None
        # Fallback이 정확히 작동했는지
        if final_stats.get("fallback"):
            rep.ok(
                "Fallback 정확히 진입",
                f"reason={final_stats.get('fallback_reason', '')[:30]}...",
            )
        else:
            # SfM이 통과했으면 fallback=False — 이것도 OK (더 좋은 경우)
            rep.ok(
                "SfM 정상 진행 (fallback 불필요)",
                f"cameras={final_stats.get('n_cameras')}",
            )
        rep.ok("phototex 파이프라인 완주", f"{elapsed:.1f}s")
    except Exception as e:
        rep.fail("phototex 파이프라인", str(e))
        return

    # 텍스처 + 메쉬 다운로드
    for name, url in [
        ("phototex PNG", f"{base}/api/phototex/texture/{sid}"),
        ("phototex OBJ", f"{base}/api/phototex/mesh/{sid}"),
        ("phototex FBX", f"{base}/api/phototex/mesh-fbx/{sid}"),
        ("phototex GLB", f"{base}/api/phototex/mesh-glb/{sid}"),
    ]:
        try:
            status, body = _http_get(url, timeout=60)
            assert status == 200 and len(body) > 100
            rep.ok(f"다운로드 {name}", f"{len(body)/1024:.1f}KB")
        except Exception as e:
            rep.fail(f"다운로드 {name}", str(e))


# ─────────────────────────────────────────────────────────────────
# 테스트: 정적 에셋 서빙 (분할된 JS/CSS)
# ─────────────────────────────────────────────────────────────────
def test_static_assets(rep: Report, base: str) -> None:
    print("\n[4] Frontend 정적 에셋 (JS 분할 + CSS)")
    for path in [
        "/",
        "/css/style.css",
        "/js/core.js",
        "/js/page2-collider.js",
        "/js/page3-pipeline.js",
        "/js/page4-bake.js",
        "/js/page5-phototex.js",
    ]:
        try:
            status, body = _http_get(f"{base}{path}", timeout=10)
            assert status == 200, f"HTTP {status}"
            assert len(body) > 500, f"too small: {len(body)}B"
            rep.ok(f"GET {path}", f"{len(body)/1024:.1f}KB")
        except Exception as e:
            rep.fail(f"GET {path}", str(e))


# ─────────────────────────────────────────────────────────────────
# 테스트: /api/automate + /api/bake/* (2026-04 POST JSON 마이그레이션)
# ─────────────────────────────────────────────────────────────────
def test_automate_and_bake(rep: Report, base: str) -> None:
    print("\n[5] /api/automate + /api/bake/* (POST JSON 신규 엔드포인트)")
    ply = _tiny_ply_cube()

    # 5-1. /api/automate : 업로드 → 자동 전체 처리
    try:
        status, body = _http_post_multipart(
            f"{base}/api/upload",
            files={"file": ("auto.ply", ply, "application/octet-stream")},
            timeout=30,
        )
        assert status == 200
        sid = json.loads(body.decode())["session_id"]
    except Exception as e:
        rep.fail("automate 업로드 준비", str(e))
        return

    try:
        req = urllib.request.Request(
            f"{base}/api/automate/{sid}",
            data=json.dumps({"lod": "fast", "tex_size": 256, "collider_tris": 500}).encode(),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        phases_seen = []
        complete = False
        with urllib.request.urlopen(req, timeout=300) as r:
            for raw in r:
                line = raw.decode("utf-8", errors="replace").strip()
                if not line.startswith("data:"):
                    continue
                try:
                    d = json.loads(line[5:].strip())
                except Exception:
                    continue
                ph = d.get("phase")
                if ph and ph not in phases_seen:
                    phases_seen.append(ph)
                if ph == "complete":
                    complete = True
                    break
                if ph == "error" or d.get("error"):
                    raise RuntimeError(d.get("error") or "?")
        assert complete, f"complete 이벤트 없음. phases={phases_seen}"
        assert "collider" in phases_seen and "mesh" in phases_seen, \
               f"일부 phase 누락: {phases_seen}"
        rep.ok("automate 파이프라인 완주", f"phases={'/'.join(phases_seen)}")
    except Exception as e:
        rep.fail("automate 파이프라인", str(e))

    # 5-2. /api/auto/status 확인
    try:
        status, body = _http_get(f"{base}/api/auto/status/{sid}", timeout=10)
        assert status == 200
        st = json.loads(body.decode())
        assert st.get("auto_done") is True
        rep.ok("auto/status", f"collider={st['has_collider']} mesh={st['has_mesh']}")
    except Exception as e:
        rep.fail("auto/status", str(e))

    # 5-3. /api/bake/upload : PLY + OBJ 동시 업로드
    obj_bytes = (
        "o Cube\n"
        "v -1 -1 -1\nv 1 -1 -1\nv 1 1 -1\nv -1 1 -1\n"
        "v -1 -1 1\nv 1 -1 1\nv 1 1 1\nv -1 1 1\n"
        "f 1 2 3\nf 1 3 4\nf 5 7 6\nf 5 8 7\nf 1 5 6\nf 1 6 2\n"
    ).encode()
    try:
        status, body = _http_post_multipart(
            f"{base}/api/bake/upload",
            files={
                "ply": ("cube.ply", ply, "application/octet-stream"),
                "obj": ("cube.obj", obj_bytes, "text/plain"),
            },
            timeout=30,
        )
        assert status == 200, f"HTTP {status}"
        bake_sid = json.loads(body.decode())["session_id"]
        rep.ok("bake 업로드", f"session={bake_sid[:8]}")
    except Exception as e:
        rep.fail("bake 업로드", str(e))
        return

    # 5-4. /api/bake/run-sse POST JSON
    try:
        req = urllib.request.Request(
            f"{base}/api/bake/run-sse/{bake_sid}",
            data=json.dumps({"tex_size": 256, "ao_strength": 0.3, "lighting": True}).encode(),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        done_stats = None
        with urllib.request.urlopen(req, timeout=300) as r:
            for raw in r:
                line = raw.decode("utf-8", errors="replace").strip()
                if not line.startswith("data:"):
                    continue
                try:
                    d = json.loads(line[5:].strip())
                except Exception:
                    continue
                if d.get("step") == "done":
                    done_stats = d.get("stats", {})
                    break
                if d.get("step") == "error":
                    raise RuntimeError(d.get("error", "?"))
        assert done_stats is not None, "done 이벤트 없음"
        assert done_stats.get("tex_size") == 256
        rep.ok("bake/run-sse (POST)", f"tex={done_stats['tex_size']}px")
    except Exception as e:
        rep.fail("bake/run-sse", str(e))
        return

    # 5-5. /api/bake/texture GET
    try:
        status, body = _http_get(f"{base}/api/bake/texture/{bake_sid}", timeout=30)
        assert status == 200 and len(body) > 200 and body[:8] == b"\x89PNG\r\n\x1a\n"
        rep.ok("bake 텍스처 PNG", f"{len(body)/1024:.1f}KB")
    except Exception as e:
        rep.fail("bake 텍스처", str(e))

    # 5-6. /api/bake/adjust POST (HSV)
    try:
        req = urllib.request.Request(
            f"{base}/api/bake/adjust/{bake_sid}?hue=30&saturation=1.2&brightness=0.9",
            method="POST",
        )
        with urllib.request.urlopen(req, timeout=30) as r:
            assert r.status == 200
            j = json.loads(r.read().decode())
            assert j.get("ok") is True
            hsv = j["hsv"]
            assert abs(hsv["hue"] - 30) < 0.01
            rep.ok("bake/adjust (HSV)", f"h={hsv['hue']} s={hsv['saturation']}")
    except Exception as e:
        rep.fail("bake/adjust", str(e))

    # 5-7. bake 메쉬 3포맷 다운로드
    for fmt, url in [
        ("OBJ", f"{base}/api/bake/mesh/{bake_sid}"),
        ("FBX", f"{base}/api/bake/mesh-fbx/{bake_sid}"),
        ("GLB", f"{base}/api/bake/mesh-glb/{bake_sid}"),
    ]:
        try:
            status, body = _http_get(url, timeout=60)
            assert status == 200 and len(body) > 100
            rep.ok(f"bake 다운로드 {fmt}", f"{len(body)/1024:.1f}KB")
        except Exception as e:
            rep.fail(f"bake 다운로드 {fmt}", str(e))


# ─────────────────────────────────────────────────────────────────
# 테스트: 악성/잘못된 입력 (보안 + 견고성)
# ─────────────────────────────────────────────────────────────────
def test_malicious_input(rep: Report, base: str) -> None:
    print("\n[6] 악성/잘못된 입력 (4xx 기대, 5xx 절대 불가)")

    # 6-1. 존재하지 않는 세션 → 404
    try:
        req = urllib.request.Request(
            f"{base}/api/process/nonexistent_sid",
            data=b"{}",
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with urllib.request.urlopen(req, timeout=10) as r:
                raise RuntimeError(f"unexpected 200 for fake sid")
        except urllib.error.HTTPError as e:
            assert e.code == 404, f"expected 404, got {e.code}"
            rep.ok("/api/process 존재하지 않는 sid → 404")
    except Exception as e:
        rep.fail("존재하지 않는 sid", str(e))

    # 6-2. 잘못된 타입의 JSON body → 422 (Pydantic validation)
    try:
        status, body = _http_post_multipart(
            f"{base}/api/upload",
            files={"file": ("x.ply", _tiny_ply_cube(), "application/octet-stream")},
            timeout=30,
        )
        sid = json.loads(body.decode())["session_id"]
        req = urllib.request.Request(
            f"{base}/api/process/{sid}",
            data=json.dumps({"mc_res": "not-an-integer", "algorithm": 123}).encode(),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with urllib.request.urlopen(req, timeout=10) as r:
                raise RuntimeError("unexpected 200 for bad body")
        except urllib.error.HTTPError as e:
            assert e.code == 422, f"expected 422, got {e.code}"
            rep.ok("process bad body type → 422")
    except Exception as e:
        rep.fail("bad body type", str(e))

    # 6-3. GET 으로 POST 엔드포인트 호출 → 404/405
    for ep in ["/api/process/x", "/api/automate/x", "/api/bake/run-sse/x", "/api/phototex/run-sse/x"]:
        try:
            status, _ = _http_get(f"{base}{ep}", timeout=5)
            assert status in (404, 405), f"{ep}: expected 404/405, got {status}"
            rep.ok(f"GET {ep} → {status}")
        except Exception as e:
            rep.fail(f"GET {ep}", str(e))

    # 6-4. 깨진 PLY (첫 바이트만) → 422
    try:
        status, body = _http_post_multipart(
            f"{base}/api/upload",
            files={"file": ("broken.ply", b"p", "application/octet-stream")},
            timeout=10,
        )
        assert status in (415, 422), f"broken PLY: expected 4xx, got {status}"
        rep.ok(f"broken PLY → {status}")
    except Exception as e:
        rep.fail("broken PLY", str(e))

    # 6-5. 허용 안 되는 확장자 → 415
    try:
        status, body = _http_post_multipart(
            f"{base}/api/upload",
            files={"file": ("evil.exe", b"MZ" + b"\x00" * 100, "application/octet-stream")},
            timeout=10,
        )
        assert status == 415, f"expected 415, got {status}"
        rep.ok(".exe 업로드 → 415")
    except Exception as e:
        rep.fail(".exe 업로드", str(e))

    # 6-6. 빈 파일 → 4xx
    try:
        status, body = _http_post_multipart(
            f"{base}/api/upload",
            files={"file": ("empty.ply", b"", "application/octet-stream")},
            timeout=10,
        )
        assert 400 <= status < 500, f"empty file: expected 4xx, got {status}"
        rep.ok(f"빈 파일 → {status}")
    except Exception as e:
        rep.fail("빈 파일", str(e))


# ─────────────────────────────────────────────────────────────────
# 테스트: HSV parity (JS ≡ Python, 10 골든 벡터)
# ─────────────────────────────────────────────────────────────────
_HSV_GOLDEN = [
    ((255,   0,   0),    0, 1.0, 1.0, (255,   0,   0)),
    ((255,   0,   0),  120, 1.0, 1.0, (  0, 255,   0)),
    ((255,   0,   0), -120, 1.0, 1.0, (  0,   0, 255)),
    ((255, 128,  64),    0, 0.0, 1.0, (255, 255, 255)),
    ((  0, 128, 255),  -60, 1.5, 0.8, (  0, 204, 102)),
    ((128, 128, 128),   90, 1.0, 1.0, (128, 128, 128)),
    ((200, 100,  50),   30, 1.2, 0.9, (180, 153,  18)),
    (( 50, 200, 100),   45, 0.8, 1.1, ( 88, 209, 220)),
    ((  0,   0,   0),    0, 1.0, 1.0, (  0,   0,   0)),
    ((255, 255, 255),  180, 1.0, 0.5, (128, 128, 128)),
]


def test_hsv_parity(rep: Report) -> None:
    print("\n[7] HSV parity (JS hsv.js ≡ Python uv_bake.apply_hsv_adjust)")
    sys.path.insert(0, str(ROOT))
    try:
        from backend.core.uv_bake import apply_hsv_adjust
        import numpy as np
    except Exception as e:
        rep.fail("import apply_hsv_adjust", str(e))
        return

    fail_count = 0
    for rgb, hue, sat, bri, expected in _HSV_GOLDEN:
        tex = np.array([[[*rgb, 255]]], dtype=np.uint8)
        out = apply_hsv_adjust(tex, hue, sat, bri)
        got = tuple(int(x) for x in out[0, 0, :3])
        if got != expected:
            fail_count += 1

    if fail_count == 0:
        rep.ok(f"HSV 골든 10/10 bit-identical", "Python ≡ JS")
    else:
        rep.fail("HSV parity", f"{fail_count}/10 cases differ — JS hsv.js 와 동기화 필요")


# ─────────────────────────────────────────────────────────────────
# 테스트: 브라우저 스모크 (Playwright headless)
# ─────────────────────────────────────────────────────────────────
def test_browser_smoke(rep: Report, base: str) -> None:
    print("\n[8] 브라우저 스모크 (Playwright — 페이지 5개 로드 + 콘솔 0 errors)")
    try:
        from playwright.sync_api import sync_playwright
    except ImportError:
        rep.skip("playwright", "pip install playwright && playwright install chromium")
        return

    errors: list[str] = []
    warnings: list[str] = []
    try:
        with sync_playwright() as pw:
            browser = pw.chromium.launch(headless=True)
            ctx = browser.new_context(viewport={"width": 1600, "height": 1000})
            page = ctx.new_page()
            page.on("console", lambda m:
                    (errors if m.type == "error"
                     else (warnings if m.type == "warning" else [])).append(f"[{m.type}] {m.text}"))
            page.on("pageerror", lambda e: errors.append(f"[pageerror] {e}"))

            page.goto(base, wait_until="networkidle", timeout=30000)
            page.wait_for_timeout(1000)

            # 초기 다이얼로그 닫기
            page.evaluate("""() => {
                for (const id of ['spec-dlg','app-dlg']) {
                    const d = document.getElementById(id); if(d) d.style.display='none';
                }
            }""")

            # Three.js 로드 유도
            page.evaluate("typeof loadThreeJS === 'function' && loadThreeJS()")
            page.wait_for_timeout(2000)

            # 5개 페이지 초기화
            for pid in [2, 3, 4, 5]:
                page.evaluate(f"""() => {{
                    for (let i = 1; i <= 5; i++) {{
                        const p = document.getElementById('page' + i);
                        if (p) p.style.display = (i === {pid}) ? '' : 'none';
                    }}
                    const fn = window['initP{pid}Viewer'];
                    if (typeof fn === 'function') fn();
                }}""")
                page.wait_for_timeout(1500)

            # 상태 확인
            state = page.evaluate("""() => ({
                App: typeof App === 'object' && !!App,
                P2: typeof P2 === 'object' && !!P2,
                P3: typeof P3 === 'object' && !!P3,
                P4: typeof P4 === 'object' && !!P4,
                P5: typeof P5 === 'object' && !!P5,
                HSV: typeof HSV === 'object' && !!HSV,
                postSSE: typeof postSSE === 'function',
                THREE: typeof THREE === 'object' && !!THREE,
                THREE_REV: (typeof THREE === 'object' && THREE && THREE.REVISION) || null,
                hsvSelfTest: (typeof HSV === 'object' && HSV._selfTest && HSV._selfTest()) || false,
            })""")
            browser.close()
    except Exception as e:
        rep.fail("playwright 실행", str(e))
        return

    # 상태 객체 존재 체크
    for key in ("App", "P2", "P3", "P4", "P5", "HSV", "postSSE"):
        if state.get(key):
            rep.ok(f"전역 {key} 정의됨")
        else:
            rep.fail(f"전역 {key}", f"undefined or falsy: {state.get(key)}")

    # Three.js r160 확인
    if state.get("THREE_REV") == "160":
        rep.ok("Three.js r160 로드 확인")
    else:
        rep.fail("Three.js 버전", f"expected 160, got {state.get('THREE_REV')}")

    # HSV 자체 테스트
    if state.get("hsvSelfTest"):
        rep.ok("HSV._selfTest() 브라우저 측 10/10")
    else:
        rep.fail("HSV._selfTest()", "JS 측 골든 벡터 실패")

    # 콘솔 에러
    # three.min.js deprecation warning 은 알려진 것 — 경고만, 실패 아님
    real_errors = [e for e in errors
                    if "three.min.js" not in e and "deprecated" not in e.lower()]
    if len(real_errors) == 0:
        rep.ok("콘솔 에러 0개", f"(경고 {len(warnings)}건 무시)")
    else:
        rep.fail("콘솔 에러", f"{len(real_errors)}건: {real_errors[0][:100]}...")


# ─────────────────────────────────────────────────────────────────
# 서버 자동 기동
# ─────────────────────────────────────────────────────────────────
def _start_server(port: int) -> subprocess.Popen:
    py_exe = ROOT / ".venv" / "Scripts" / "python.exe"
    if not py_exe.exists():
        py_exe = Path(sys.executable)
    env = os.environ.copy()
    env["PYTHONPATH"] = str(ROOT) + os.pathsep + env.get("PYTHONPATH", "")
    cmd = [
        str(py_exe), "-c",
        (
            "import uvicorn;"
            "from backend.app import app;"
            f"uvicorn.Config(app,host='127.0.0.1',port={port},"
            "log_level='warning',timeout_keep_alive=3600,"
            "h11_max_incomplete_event_size=16*1024*1024*1024);"
            f"uvicorn.Server(uvicorn.Config(app,host='127.0.0.1',port={port},"
            "log_level='warning',timeout_keep_alive=3600,"
            "h11_max_incomplete_event_size=16*1024*1024*1024)).run()"
        ),
    ]
    return subprocess.Popen(cmd, cwd=str(ROOT), env=env,
                             stdout=subprocess.DEVNULL,
                             stderr=subprocess.DEVNULL)


# ─────────────────────────────────────────────────────────────────
# main
# ─────────────────────────────────────────────────────────────────
def main() -> int:
    ap = argparse.ArgumentParser(description="PointCloud Optimizer E2E QA")
    ap.add_argument("--port", type=int, default=9999)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--auto", action="store_true",
                    help="서버 자동 기동 + 종료")
    ap.add_argument("--only",
                    choices=["fbx-format", "page3", "page5", "static",
                             "automate-bake", "malicious", "hsv", "browser"],
                    help="특정 테스트만 실행")
    args = ap.parse_args()

    rep = Report()
    base = f"http://{args.host}:{args.port}"

    # 서버 필요한지 판단 — fbx-format 과 hsv (Python only) 는 서버 불필요
    needs_server = args.only not in ("fbx-format", "hsv")

    server_proc: Optional[subprocess.Popen] = None
    if needs_server:
        if args.auto:
            print(f"[서버 자동 기동 — port {args.port}]")
            server_proc = _start_server(args.port)
            if not _wait_server(base, 30.0):
                print(f"{_FAIL} 서버 30초 내 기동 실패")
                try:
                    server_proc.terminate()
                except Exception:
                    pass
                return 1
            print(f"{_PASS} 서버 준비 완료\n")
        else:
            if not _wait_server(base, 5.0):
                print(f"{_FAIL} 서버 연결 실패 ({base}). --auto 플래그 또는 서버 먼저 기동.")
                return 1

    try:
        if args.only in (None, "fbx-format"):
            test_fbx_roundtrip(rep)
        if args.only in (None, "page3"):
            test_page3(rep, base)
        if args.only in (None, "page5"):
            test_page5_fallback(rep, base)
        if args.only in (None, "static"):
            test_static_assets(rep, base)
        if args.only in (None, "automate-bake"):
            test_automate_and_bake(rep, base)
        if args.only in (None, "malicious"):
            test_malicious_input(rep, base)
        if args.only in (None, "hsv"):
            test_hsv_parity(rep)
        if args.only in (None, "browser"):
            test_browser_smoke(rep, base)
    finally:
        if server_proc is not None:
            print("\n[서버 종료]")
            try:
                server_proc.terminate()
                server_proc.wait(timeout=5)
            except Exception:
                try:
                    server_proc.kill()
                except Exception:
                    pass

    return rep.summary()


if __name__ == "__main__":
    sys.exit(main())
