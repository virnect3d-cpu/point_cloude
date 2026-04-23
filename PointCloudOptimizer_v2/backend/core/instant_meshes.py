"""
Instant Meshes 바이너리 래퍼 — page 3용 진짜 field-aligned quad 리토폴로지.

Wenzel Jakob의 Instant Meshes (SIGGRAPH Asia 2015)를 subprocess로 호출.
사용자가 빨간 와이어로 그린 것 같은 격자 Quad 토폴로지를 뽑아줌.

tools/Instant Meshes.exe 가 프로젝트에 번들되어 있어야 동작.
"""
from __future__ import annotations

import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Dict, Optional

import numpy as np


ROOT = Path(__file__).resolve().parent.parent.parent
_EXE_NAME_WIN = "Instant Meshes.exe"
_EXE_NAME_NIX = "Instant Meshes"


def find_exe() -> Optional[Path]:
    """번들된 Instant Meshes 바이너리 경로 찾기."""
    tools = ROOT / "tools"
    candidates = []
    if sys.platform == "win32":
        candidates.append(tools / _EXE_NAME_WIN)
    else:
        candidates.append(tools / _EXE_NAME_NIX)
    # 혹시 PATH에 있으면 그것도 허용
    for c in candidates:
        if c.exists():
            return c
    return None


def is_available() -> bool:
    return find_exe() is not None


def _write_obj(path: Path, verts: np.ndarray, faces: np.ndarray) -> None:
    """간단한 OBJ 쓰기 (vn 없음 — IM이 재계산함)."""
    lines = [f"# IM input V={len(verts)} F={len(faces)}"]
    for v in verts:
        lines.append(f"v {v[0]:.6f} {v[1]:.6f} {v[2]:.6f}")
    for f in faces:
        lines.append(f"f {f[0]+1} {f[1]+1} {f[2]+1}")
    path.write_text("\n".join(lines), encoding="utf-8")


def _read_obj_quads(path: Path) -> Dict:
    """
    Instant Meshes가 출력한 OBJ를 읽음. quad와 tri 혼합.
    반환: {"verts": (V,3), "quads": [[a,b,c,d]...], "tris": [[a,b,c]...]}
    """
    text = path.read_text(encoding="utf-8", errors="replace")
    verts_list = []
    quads = []
    tris = []
    for ln in text.split("\n"):
        p = ln.strip().split()
        if not p:
            continue
        if p[0] == "v":
            verts_list.append([float(p[1]), float(p[2]), float(p[3])])
        elif p[0] == "f":
            idx = [int(s.split("/")[0]) - 1 for s in p[1:]]
            if len(idx) == 4:
                quads.append(idx)
            elif len(idx) == 3:
                tris.append(idx)
            elif len(idx) > 4:
                # n-gon — fan triangulation
                for i in range(1, len(idx) - 1):
                    tris.append([idx[0], idx[i], idx[i + 1]])
    verts = np.asarray(verts_list, dtype=np.float32)
    return {"verts": verts, "quads": quads, "tris": tris}


def retopologize(
    verts: np.ndarray,
    faces: np.ndarray,
    *,
    target_faces: int = 10000,
    smooth_iter: int = 2,
    crease_degrees: float = 0.0,      # 0=끔, 30~60 = 날카로운 엣지 보존
    pure_quad: bool = True,           # True = quad 전용 (posy=4), False = dominant
    align_boundaries: bool = True,
    timeout_sec: int = 300,
) -> Dict:
    """
    Instant Meshes로 리토폴로지 실행. 입력 삼각형 메쉬 → quad-dominant 출력.

    반환:
      {
        "ok": bool,
        "verts": (V,3) float32,
        "quads": [[a,b,c,d]...],
        "tris":  [[a,b,c]...],
        "stats": {"quads": N, "tris": N, "verts": N},
        "error": "..." (실패 시),
      }
    """
    exe = find_exe()
    if exe is None:
        return {"ok": False, "error": "Instant Meshes 바이너리를 tools/ 폴더에서 찾을 수 없습니다"}

    if len(faces) < 10 or len(verts) < 10:
        return {"ok": False, "error": "입력 메쉬가 너무 작습니다"}

    with tempfile.TemporaryDirectory(prefix="im_") as td:
        td = Path(td)
        in_path = td / "input.obj"
        out_path = td / "output.obj"
        _write_obj(in_path, verts, faces)

        cmd = [
            str(exe),
            "-o", str(out_path),
            "-f", str(int(max(200, target_faces))),
            "-S", str(int(max(0, smooth_iter))),
            "-r", "4",                      # rotational symmetry (4 = quad)
            "-p", "4" if pure_quad else "4",
        ]
        if not pure_quad:
            cmd.append("-D")                # dominant mode (섞인 tri/quad 허용)
        if crease_degrees > 0:
            cmd += ["-c", str(float(crease_degrees))]
        if align_boundaries:
            cmd.append("-b")
        cmd.append(str(in_path))

        try:
            res = subprocess.run(
                cmd, capture_output=True, text=True,
                timeout=int(timeout_sec),
                encoding="utf-8", errors="replace",
            )
        except subprocess.TimeoutExpired:
            return {"ok": False, "error": f"Instant Meshes 타임아웃 ({timeout_sec}s)"}
        except Exception as e:
            return {"ok": False, "error": f"실행 실패: {e}"}

        if res.returncode != 0 or not out_path.exists():
            err = (res.stderr or res.stdout or "").strip()
            return {"ok": False,
                    "error": f"Instant Meshes 에러 (rc={res.returncode}): {err[:500]}"}

        parsed = _read_obj_quads(out_path)
        V = parsed["verts"]
        if len(V) < 4:
            return {"ok": False, "error": "IM 출력이 비었거나 너무 작음"}

        return {
            "ok": True,
            "verts": V,
            "quads": parsed["quads"],
            "tris": parsed["tris"],
            "stats": {
                "verts": len(V),
                "quads": len(parsed["quads"]),
                "tris":  len(parsed["tris"]),
            },
        }
