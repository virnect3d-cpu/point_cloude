"""Blender headless를 통한 Autodesk-compatible FBX export.

scratch Python writer (fbx_binary_export.py)는 Blender는 OK이지만 Autodesk FBX SDK
(Unity/Maya)에서 "File is corrupted"로 거부됨. SceneInfo/FileId/PropertyTemplate 등
100% 호환하려면 수만 줄 코드 필요.

실용적 대안: Blender CLI(headless)를 서브프로세스로 호출해서 OBJ/GLB → FBX 변환.
Blender 4.x의 FBX exporter는 Autodesk SDK 호환 (Unity/Maya 공식 import 성공).

사용 조건:
  - Blender 3.x+ 설치 (일반 경로 자동 탐색)
  - 환경변수 BLENDER_EXE 또는 디폴트 Program Files 경로 사용

Fallback:
  Blender 없으면 None 반환 → 호출측에서 scratch writer로 자동 fallback.
"""
from __future__ import annotations

import os
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Optional


def find_blender_exe() -> Optional[Path]:
    """Blender 실행파일 위치 탐색."""
    # 1. 환경변수
    env = os.environ.get("BLENDER_EXE", "").strip().strip('"')
    if env and Path(env).exists():
        return Path(env)

    # 2. Windows 표준 경로들
    candidates = []
    if sys.platform == "win32":
        pf = [Path(r"C:/Program Files/Blender Foundation"),
              Path(r"C:/Program Files (x86)/Blender Foundation")]
        for base in pf:
            if base.exists():
                for sub in sorted(base.iterdir(), reverse=True):
                    exe = sub / "blender.exe"
                    if exe.exists():
                        candidates.append(exe)
    else:
        # Linux / macOS
        candidates.extend([
            Path("/usr/bin/blender"),
            Path("/usr/local/bin/blender"),
            Path("/Applications/Blender.app/Contents/MacOS/Blender"),
        ])

    for c in candidates:
        if c.exists():
            return c
    return None


# ─────────────────────────────────────────────────────────────────────────────
# Blender 내부에서 실행할 Python 스크립트 (인라인)
# ─────────────────────────────────────────────────────────────────────────────
_BLENDER_SCRIPT = r"""
import bpy, sys, os

def _parse_args():
    args = sys.argv
    if "--" in args:
        args = args[args.index("--") + 1:]
    else:
        args = []
    kw = {}
    for a in args:
        if "=" in a:
            k, v = a.split("=", 1)
            kw[k] = v
    return kw

kw = _parse_args()
input_path = kw["input"]
output_path = kw["output"]
ext = os.path.splitext(input_path)[1].lower()

# 씬 초기화
bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.delete(use_global=False)
for mat in list(bpy.data.materials):
    bpy.data.materials.remove(mat)

# Import
if ext == ".obj":
    if hasattr(bpy.ops.wm, "obj_import"):
        bpy.ops.wm.obj_import(filepath=input_path)
    else:
        bpy.ops.import_scene.obj(filepath=input_path)
elif ext == ".glb" or ext == ".gltf":
    bpy.ops.import_scene.gltf(filepath=input_path)
elif ext == ".ply":
    if hasattr(bpy.ops.wm, "ply_import"):
        bpy.ops.wm.ply_import(filepath=input_path)
    else:
        bpy.ops.import_mesh.ply(filepath=input_path)
else:
    raise SystemExit(f"unsupported input: {ext}")

# 모든 mesh 오브젝트 선택
bpy.ops.object.select_all(action="SELECT")

# FBX export (Blender 4.x Autodesk-compatible, binary)
# path_mode='COPY', embed_textures=True → 텍스처까지 포함
# bake_space_transform=False: Unity/Maya와 axis 맞추기 위해 기본 Y-up 사용
bpy.ops.export_scene.fbx(
    filepath=output_path,
    use_selection=False,
    path_mode="COPY",
    embed_textures=True,
    object_types={"MESH"},
    mesh_smooth_type="FACE",
    add_leaf_bones=False,
    bake_anim=False,
    axis_forward="-Z",
    axis_up="Y",
)
print(f"[FBX-BRIDGE] exported {output_path}", flush=True)
"""


def export_via_blender(
    input_mesh_path: str | Path,
    output_fbx_path: str | Path,
    blender_exe: Optional[Path] = None,
    timeout_sec: int = 120,
) -> tuple[bool, str]:
    """OBJ/GLB/PLY → Autodesk-compatible FBX 변환.

    Returns:
        (ok: bool, message: str)
    """
    if blender_exe is None:
        blender_exe = find_blender_exe()
    if not blender_exe or not Path(blender_exe).exists():
        return False, "Blender 실행파일을 찾을 수 없습니다 (BLENDER_EXE 환경변수 설정 권장)"

    input_mesh_path = Path(input_mesh_path).resolve()
    output_fbx_path = Path(output_fbx_path).resolve()
    if not input_mesh_path.exists():
        return False, f"입력 파일 없음: {input_mesh_path}"

    # 임시 스크립트 파일
    with tempfile.NamedTemporaryFile(
        mode="w", suffix=".py", delete=False, encoding="utf-8",
    ) as tf:
        tf.write(_BLENDER_SCRIPT)
        script_path = Path(tf.name)

    try:
        cmd = [
            str(blender_exe),
            "--background",
            "--factory-startup",
            "--python", str(script_path),
            "--",
            f"input={input_mesh_path}",
            f"output={output_fbx_path}",
        ]
        proc = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            timeout=timeout_sec,
            cwd=str(input_mesh_path.parent),
        )
        if proc.returncode != 0:
            return False, f"Blender 실패 (exit {proc.returncode}): {proc.stderr[-500:]}"
        if not output_fbx_path.exists():
            return False, "Blender 실행됐지만 출력 파일 생성 안 됨"
        if output_fbx_path.stat().st_size < 100:
            return False, f"출력 파일 너무 작음: {output_fbx_path.stat().st_size}B"
        return True, f"OK ({output_fbx_path.stat().st_size} bytes)"
    except subprocess.TimeoutExpired:
        return False, f"Blender 타임아웃 ({timeout_sec}s)"
    except Exception as e:
        return False, f"예외: {e}"
    finally:
        try:
            script_path.unlink()
        except Exception:
            pass


def is_available() -> bool:
    """Blender bridge 사용 가능한지 (Blender 설치 여부)."""
    return find_blender_exe() is not None
