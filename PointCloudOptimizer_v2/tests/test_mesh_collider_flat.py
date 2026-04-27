"""mesh-collider flat / OBJ 응답 스키마 테스트.

backend/core/collider.py::build_mesh_collider 가 반환하는 dict 가
backend/app.py 의 _merge_collider_parts / _mesh_to_obj 와 정상 동작하는지 검증.

검증 포인트:
  1. 합성 sphere PC → Poisson 콜라이더 정상 생성 (mode='mesh', parts ≥ 1)
  2. parts 머지 시 triangle 인덱스가 verts 범위를 절대 벗어나지 않음
  3. flat 형식: verts_flat.size = verts_total*3, tris_flat.size = tris_total*3
  4. OBJ 직렬화: 1-base 인덱스, 'v ' 라인 수 == verts_total, 'f ' 라인 수 == tris_total

Usage:
    python tests/test_mesh_collider_flat.py        # exit 0 = pass, 1 = fail
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np

if sys.stdout.encoding and sys.stdout.encoding.lower() != "utf-8":
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

from backend.core.collider import build_mesh_collider


# ── 합성 sphere 점군 ────────────────────────────────────────────────────
def _sphere_points(n: int = 4000, radius: float = 1.0, seed: int = 7) -> np.ndarray:
    rng = np.random.default_rng(seed)
    # 균일 구면
    u = rng.uniform(-1.0, 1.0, n)
    th = rng.uniform(0.0, 2.0 * np.pi, n)
    r = np.sqrt(np.maximum(0.0, 1.0 - u * u))
    x = r * np.cos(th)
    y = r * np.sin(th)
    z = u
    return (np.stack([x, y, z], axis=1) * radius).astype(np.float32)


def _merge_collider_parts(result: dict):
    """app.py 와 동일 로직 (테스트 대상). FastAPI 모듈 안 끌어오려 복사."""
    all_v, all_t = [], []
    v_offset = 0
    for p in result.get("parts", []) or []:
        v = np.asarray(p["vertices"], dtype=np.float32).reshape(-1, 3)
        t = np.asarray(p["triangles"], dtype=np.int32).reshape(-1, 3) + v_offset
        all_v.append(v)
        all_t.append(t)
        v_offset += len(v)
    if not all_v:
        return np.zeros((0, 3), np.float32), np.zeros((0, 3), np.int32)
    return np.concatenate(all_v, 0), np.concatenate(all_t, 0)


def _mesh_to_obj(verts: np.ndarray, tris: np.ndarray) -> str:
    lines = ["# test"]
    for v in verts:
        lines.append(f"v {v[0]:.6f} {v[1]:.6f} {v[2]:.6f}")
    for t in tris:
        lines.append(f"f {int(t[0]) + 1} {int(t[1]) + 1} {int(t[2]) + 1}")
    return "\n".join(lines)


# ── 테스트들 ──────────────────────────────────────────────────────────
def test_build_returns_valid_parts():
    pts = _sphere_points()
    out = build_mesh_collider(
        pts, normals=None,
        method="poisson", depth=6, target_tris=1000,
        snap_strength=0,            # ICP 끄면 빨라짐
        max_edge_ratio=0,
        density_trim=0,
        keep_fragments=True,
    )
    assert out.get("mode") == "mesh", f"mode != mesh: {out.get('mode')}"
    parts = out.get("parts") or []
    assert len(parts) >= 1, f"parts 비어있음"
    assert int(out.get("verts_total", 0)) > 100, "verts_total 너무 적음"
    assert int(out.get("tris_total", 0))  > 100, "tris_total  너무 적음"
    return out


def test_merge_indices_in_range(out):
    v, t = _merge_collider_parts(out)
    assert v.shape[1] == 3 and t.shape[1] == 3
    assert int(t.min()) >= 0,                      f"음수 인덱스: {t.min()}"
    assert int(t.max()) <  len(v),                 f"인덱스 범위 초과: max={t.max()} verts={len(v)}"
    return v, t


def test_flat_lengths_match(v, t, out):
    verts_flat = v.reshape(-1).tolist()
    tris_flat  = t.reshape(-1).tolist()
    expected_v = int(out.get("verts_total", len(v))) * 3
    expected_t = int(out.get("tris_total",  len(t))) * 3
    # parts 가 여러 개면 verts_total 은 머지 전 합과 같음 (이 케이스에선 단일 part 가정).
    # 머지 후엔 len(v) 와 동일해야 함.
    assert len(verts_flat) == len(v) * 3 == expected_v, \
        f"verts_flat 길이 불일치: {len(verts_flat)} vs {expected_v}"
    assert len(tris_flat)  == len(t) * 3 == expected_t, \
        f"tris_flat 길이 불일치: {len(tris_flat)} vs {expected_t}"


def test_obj_round_trip(v, t):
    obj = _mesh_to_obj(v, t)
    v_lines = [ln for ln in obj.splitlines() if ln.startswith("v ")]
    f_lines = [ln for ln in obj.splitlines() if ln.startswith("f ")]
    assert len(v_lines) == len(v), f"OBJ v lines: {len(v_lines)} vs {len(v)}"
    assert len(f_lines) == len(t), f"OBJ f lines: {len(f_lines)} vs {len(t)}"
    # 1-base 인덱스 확인
    first_f = f_lines[0].split()[1:]
    idx = [int(x) for x in first_f]
    assert min(idx) >= 1, "OBJ 인덱스가 1-base 아님"
    assert max(idx) <= len(v), f"OBJ 인덱스 범위 초과: max={max(idx)} verts={len(v)}"


def test_empty_result_safe():
    """parts 빈 케이스도 머지 함수가 폭발 안 함."""
    v, t = _merge_collider_parts({"parts": []})
    assert v.shape == (0, 3) and t.shape == (0, 3)
    v, t = _merge_collider_parts({})
    assert v.shape == (0, 3) and t.shape == (0, 3)


def main() -> int:
    print("[test_mesh_collider_flat] 시작...")
    try:
        out = test_build_returns_valid_parts()
        print(f"  ✓ build_mesh_collider — verts={out['verts_total']} tris={out['tris_total']}")
        v, t = test_merge_indices_in_range(out)
        print(f"  ✓ merge — V={len(v)} T={len(t)} (인덱스 범위 OK)")
        test_flat_lengths_match(v, t, out)
        print(f"  ✓ flat 길이 검증")
        test_obj_round_trip(v, t)
        print(f"  ✓ OBJ 직렬화 / 1-base 인덱스")
        test_empty_result_safe()
        print(f"  ✓ 빈 parts 안전")
    except AssertionError as e:
        print(f"  ✗ 실패: {e}")
        return 1
    except Exception as e:
        print(f"  ✗ 예외: {type(e).__name__}: {e}")
        return 1
    print("[test_mesh_collider_flat] 통과")
    return 0


if __name__ == "__main__":
    sys.exit(main())
