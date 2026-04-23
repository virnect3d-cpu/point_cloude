"""HSV 공식 JS/Python parity 테스트.

frontend/js/hsv.js (window.HSV.adjustPixel) 의 골든 벡터를 그대로 들고 와서
backend/core/uv_bake.py::apply_hsv_adjust 가 bit-identical 결과를 내는지 확인.

두 구현 중 하나라도 공식을 바꾸면 이 테스트가 깨지고, 양쪽 동시 수정 강제.

Usage:
    python tests/test_hsv_parity.py        # exit 0 = pass, 1 = fail
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

from backend.core.uv_bake import apply_hsv_adjust


# 골든 벡터 — frontend/js/hsv.js 의 HSV._selfTest() 와 동일해야 함.
# [(r, g, b), hue_deg, saturation, brightness, (exp_r, exp_g, exp_b)]
GOLDEN = [
    ((255,   0,   0),    0, 1.0, 1.0, (255,   0,   0)),   # no-op
    ((255,   0,   0),  120, 1.0, 1.0, (  0, 255,   0)),   # red → green
    ((255,   0,   0), -120, 1.0, 1.0, (  0,   0, 255)),   # red → blue
    ((255, 128,  64),    0, 0.0, 1.0, (255, 255, 255)),   # sat=0, v=1 → 흰색
    ((  0, 128, 255),  -60, 1.5, 0.8, (  0, 204, 102)),
    ((128, 128, 128),   90, 1.0, 1.0, (128, 128, 128)),   # 회색 (hue 무의미)
    ((200, 100,  50),   30, 1.2, 0.9, (180, 153,  18)),
    (( 50, 200, 100),   45, 0.8, 1.1, ( 88, 209, 220)),
    ((  0,   0,   0),    0, 1.0, 1.0, (  0,   0,   0)),
    ((255, 255, 255),  180, 1.0, 0.5, (128, 128, 128)),
]


def run():
    fails = []
    for rgb, hue, sat, bri, expected in GOLDEN:
        tex = np.array([[[*rgb, 255]]], dtype=np.uint8)  # 1×1 RGBA
        out = apply_hsv_adjust(tex, hue, sat, bri)
        got = tuple(int(x) for x in out[0, 0, :3])
        alpha = int(out[0, 0, 3])
        if got != expected or alpha != 255:
            fails.append((rgb, hue, sat, bri, expected, got, alpha))

    if fails:
        print(f"❌ {len(fails)}/{len(GOLDEN)} case failed:")
        for rgb, hue, sat, bri, exp, got, a in fails:
            print(f"  rgb={rgb} hue={hue} sat={sat} bri={bri}: "
                  f"expected={exp} got={got} alpha={a}")
        return 1
    print(f"✅ HSV parity: {len(GOLDEN)}/{len(GOLDEN)} golden cases — Python ≡ JS")
    return 0


if __name__ == "__main__":
    sys.exit(run())
