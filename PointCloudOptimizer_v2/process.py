#!/usr/bin/env python
"""
PointCloud Optimizer — Local CLI processor
Usage:
    python process.py input.ply
    python process.py input.ply --output result.obj --mc-res 60 --no-smooth
    python process.py input.ply --sigma 1.5 --iter 3

Pipeline:
    PLY/XYZ/PCD/OBJ → SOR 노이즈제거 → Marching Cubes
    → Geometry Validation → Mesh Repair → Laplacian Smooth → OBJ 저장
"""
import argparse
import sys
import time
from pathlib import Path

ROOT = Path(__file__).parent
sys.path.insert(0, str(ROOT))


def _check_imports():
    missing = []
    for mod, pkg in [("numpy","numpy"),("scipy","scipy"),
                     ("skimage","scikit-image"),("trimesh","trimesh")]:
        try: __import__(mod)
        except ImportError: missing.append(pkg)
    if missing:
        print(f"\n[ERROR] 필요 패키지 없음:\n  pip install {' '.join(missing)}\n")
        sys.exit(1)


def _bar(label: str, pct: int, width: int = 30):
    filled = int(width * pct / 100)
    bar = "█" * filled + "░" * (width - filled)
    print(f"\r  {label:<22} [{bar}] {pct:3d}%", end="", flush=True)


def run(args):
    import numpy as np
    from backend.core import loader, pipeline, export

    src = Path(args.input)
    if not src.exists():
        print(f"[ERROR] 파일 없음: {src}")
        sys.exit(1)

    out = Path(args.output) if args.output else src.with_name(src.stem + "_mesh.obj")
    out.parent.mkdir(parents=True, exist_ok=True)

    print(f"\n{'─'*55}")
    print(f"  PointCloud Optimizer  v2.0  (로컬 실행)")
    print(f"{'─'*55}")
    print(f"  입력: {src.name}")
    print(f"  출력: {out.name}")
    print(f"{'─'*55}\n")

    t0 = time.time()

    # ── Load ────────────────────────────────────────────────────────
    _bar("1/5  파일 로드", 0)
    data = src.read_bytes()
    pts = loader.load(src.name, data)
    _bar("1/5  파일 로드", 100)
    print(f"\n      → {len(pts):,} pts  ({src.suffix.upper()})")

    # ── SOR ─────────────────────────────────────────────────────────
    _bar("2/5  노이즈 제거", 0)
    if not args.no_denoise:
        pts = pipeline.sor(pts, sigma=args.sigma)
        _bar("2/5  노이즈 제거", 100)
        print(f"\n      → {len(pts):,} pts (σ={args.sigma})")
    else:
        _bar("2/5  노이즈 제거", 100)
        print("\n      → 스킵")

    # ── Marching Cubes ───────────────────────────────────────────────
    _bar("3/5  Marching Cubes", 0)
    mesh = pipeline.build_mc_mesh(pts, grid_res=args.mc_res)
    _bar("3/5  Marching Cubes", 100)
    print(f"\n      → V:{len(mesh['verts']):,}  F:{len(mesh['faces']):,}  (res={args.mc_res})")

    # ── Validate + Repair ────────────────────────────────────────────
    _bar("4/5  검증 + 복구", 0)
    val = pipeline.validate(mesh["verts"], mesh["faces"])
    issues = []
    if not val["watertight"]:           issues.append(f"열린경계 {val['boundary_edges']}")
    if val["non_manifold_edges"] > 0:   issues.append(f"Non-manifold {val['non_manifold_edges']}")
    if val["components"] > 1:           issues.append(f"파편 {val['components']}개")
    if val["normal_consistency"] < 0.9: issues.append(f"노멀불일치")

    if issues:
        mesh = pipeline.repair(mesh["verts"], mesh["faces"], val)
        _bar("4/5  검증 + 복구", 100)
        print(f"\n      ⚠  이슈 {len(issues)}건 수정: {', '.join(issues)}")
        print(f"      → V:{len(mesh['verts']):,}  F:{len(mesh['faces']):,}")
    else:
        _bar("4/5  검증 + 복구", 100)
        print("\n      ✓ 검증 통과 (watertight, manifold)")

    # ── Smooth ──────────────────────────────────────────────────────
    _bar("5/5  스무딩 + 저장", 0)
    verts, faces = mesh["verts"], mesh["faces"]
    if not args.no_smooth:
        verts = pipeline.laplacian_smooth(verts, faces, args.iter)

    obj_text = export.to_obj(verts, faces)
    out.write_text(obj_text, encoding="utf-8")
    _bar("5/5  스무딩 + 저장", 100)
    print(f"\n      → {out.name}  ({len(obj_text)//1024} KB)\n")

    elapsed = time.time() - t0
    print(f"{'─'*55}")
    print(f"  ✅ 완료!  {elapsed:.1f}s")
    print(f"  출력: {out.resolve()}")
    print(f"{'─'*55}")
    print(f"\n  📌 다음 단계:")
    print(f"     1. 브라우저 → Page 3 → OBJ 뷰어에 파일 드래그")
    print(f"     2. (선택) Instant Meshes 로 6k~9k V 리토폴")
    print(f"     3. (선택) Blender Decimate → 4k~10k F 타겟\n")


if __name__ == "__main__":
    _check_imports()

    p = argparse.ArgumentParser(
        description="PointCloud → OBJ 메쉬 변환기",
        formatter_class=argparse.RawTextHelpFormatter,
    )
    p.add_argument("input", help="입력 파일 (PLY / XYZ / PTS / PCD / OBJ / CSV)")
    p.add_argument("-o", "--output", default=None, help="출력 OBJ 경로 (기본: 입력파일명_mesh.obj)")
    p.add_argument("--no-denoise", action="store_true", help="노이즈 제거 스킵")
    p.add_argument("--sigma", type=float, default=2.0, help="SOR σ 값 (기본: 2.0)")
    p.add_argument("--mc-res", type=int, default=50, help="Marching Cubes 해상도 30~80 (기본: 50)")
    p.add_argument("--no-smooth", action="store_true", help="Laplacian 스무딩 스킵")
    p.add_argument("--iter", type=int, default=2, help="스무딩 반복 횟수 (기본: 2)")
    args = p.parse_args()
    run(args)
