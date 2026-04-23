// ═══════════════════════════════════════════════════════════════════════
//  HSV 조정 — Canonical 구현
// ═══════════════════════════════════════════════════════════════════════
//
// 이 파일은 프런트엔드 HSV 프리뷰의 **단일 소스**다.
// 서버 측(backend/core/uv_bake.py::apply_hsv_adjust)은 이 공식의 NumPy 포팅이며,
// 두 구현은 tests/test_hsv_parity.py 의 골든 벡터로 비트 동등성을 검증한다.
//
// ── FORMULA CONTRACT ────────────────────────────────────────────────────
// 입력:  r, g, b ∈ [0, 1]  (각 채널 /255)
//        hueShift ∈ ℝ      (degrees; 결과 hue 를 360° modulo)
//        sat, bri ∈ [0, ∞) (1.0 이 원본)
//
// RGB → HSV:
//   mx = max(r,g,b); mn = min(r,g,b); df = mx - mn
//   if df > 1e-9:
//     if mx == r: h = (g - b) / df
//     elif mx == g: h = 2 + (b - r) / df
//     else:         h = 4 + (r - g) / df
//     h = (h * 60) mod 360; if h < 0: h += 360
//   else: h = 0
//   s = (mx == 0) ? 0 : df / mx
//   v = mx
//
// Adjust:
//   h' = (h + hueShift) mod 360; if h' < 0: h' += 360
//   s' = clip(s * sat, 0, 1)
//   v' = clip(v * bri, 0, 1)
//
// HSV → RGB:
//   c = v' * s'
//   x = c * (1 - |((h' / 60) mod 2) - 1|)
//   m = v' - c
//   hi = floor(h' / 60) mod 6
//   hi 0: (r2,g2,b2) = (c, x, 0)
//   hi 1: (x, c, 0)
//   hi 2: (0, c, x)
//   hi 3: (0, x, c)
//   hi 4: (x, 0, c)
//   hi 5: (c, 0, x)
//   out.rgb = round((r2+m, g2+m, b2+m) * 255)   (banker's round 아니라 +0.5 | 0 — 서버와 동일)
//   out.a   = 원본 alpha 보존
//
// 이 공식을 바꾸면 반드시:
//   1) backend/core/uv_bake.py::apply_hsv_adjust 도 동일하게 갱신
//   2) tests/test_hsv_parity.py 의 골든 벡터 재계산
// ════════════════════════════════════════════════════════════════════════

(function(global){
  'use strict';

  /**
   * 단일 RGB 픽셀에 HSV 조정 적용.
   * @param {number} r - 0..255
   * @param {number} g - 0..255
   * @param {number} b - 0..255
   * @param {number} hueDeg - hue shift in degrees (e.g. -180..+180)
   * @param {number} sat - saturation multiplier (0=gray, 1=원본, 2=과포화)
   * @param {number} bri - brightness multiplier (0=검정, 1=원본, 2=밝게)
   * @returns {[number, number, number]} [r, g, b] in 0..255 (uint8-clamped)
   */
  function adjustPixel(r, g, b, hueDeg, sat, bri){
    const rf = r / 255, gf = g / 255, bf = b / 255;
    const mx = Math.max(rf, gf, bf);
    const mn = Math.min(rf, gf, bf);
    const df = mx - mn;
    let h = 0;
    if(df > 1e-9){
      if(mx === rf)      h = ((gf - bf) / df);
      else if(mx === gf) h = 2 + (bf - rf) / df;
      else               h = 4 + (rf - gf) / df;
      h = (h * 60) % 360; if(h < 0) h += 360;
    }
    const s = mx === 0 ? 0 : df / mx;
    const v = mx;

    let h2 = (h + hueDeg) % 360; if(h2 < 0) h2 += 360;
    const s2 = Math.min(1, Math.max(0, s * sat));
    const v2 = Math.min(1, Math.max(0, v * bri));

    const c = v2 * s2;
    const x = c * (1 - Math.abs(((h2 / 60) % 2) - 1));
    const m = v2 - c;
    let r2 = 0, g2 = 0, b2 = 0;
    const hi = Math.floor(h2 / 60) % 6;
    if(hi === 0){ r2 = c; g2 = x; }
    else if(hi === 1){ r2 = x; g2 = c; }
    else if(hi === 2){ g2 = c; b2 = x; }
    else if(hi === 3){ g2 = x; b2 = c; }
    else if(hi === 4){ r2 = x; b2 = c; }
    else            { r2 = c; b2 = x; }

    return [
      (r2 + m) * 255 + 0.5 | 0,
      (g2 + m) * 255 + 0.5 | 0,
      (b2 + m) * 255 + 0.5 | 0,
    ];
  }

  /**
   * ImageData 전체에 HSV 조정 적용. alpha 는 보존.
   * @param {ImageData} src
   * @param {number} hueDeg
   * @param {number} sat
   * @param {number} bri
   * @returns {ImageData}
   */
  function adjustImageData(src, hueDeg, sat, bri){
    const d = src.data, n = d.length;
    const out = new Uint8ClampedArray(n);
    for(let i = 0; i < n; i += 4){
      const p = adjustPixel(d[i], d[i+1], d[i+2], hueDeg, sat, bri);
      out[i] = p[0]; out[i+1] = p[1]; out[i+2] = p[2]; out[i+3] = d[i+3];
    }
    return new ImageData(out, src.width, src.height);
  }

  global.HSV = { adjustPixel, adjustImageData };

  // 개발자 콘솔 검증 — 서버 골든 벡터와 비교 (tests/test_hsv_parity.py 와 동일)
  // 필요하면 브라우저 콘솔에서 HSV._selfTest() 실행.
  global.HSV._selfTest = function(){
    // 골든 벡터 — tests/test_hsv_parity.py 와 bit-identical 이어야 함.
    // 값 수정 시 양쪽 동시 갱신 필수.
    const cases = [
      // [r,g,b, hue, sat, bri,   exp_r, exp_g, exp_b]
      [255,   0,   0,    0, 1.0, 1.0,   255,   0,   0],   // 원본 red, no-op
      [255,   0,   0,  120, 1.0, 1.0,     0, 255,   0],   // red → green
      [255,   0,   0, -120, 1.0, 1.0,     0,   0, 255],   // red → blue
      [255, 128,  64,    0, 0.0, 1.0,   255, 255, 255],   // 채도 0 + v=1 = 흰색
      [  0, 128, 255,  -60, 1.5, 0.8,     0, 204, 102],   // 종합
      [128, 128, 128,   90, 1.0, 1.0,   128, 128, 128],   // 회색
      [200, 100,  50,   30, 1.2, 0.9,   180, 153,  18],
      [ 50, 200, 100,   45, 0.8, 1.1,    88, 209, 220],
      [  0,   0,   0,    0, 1.0, 1.0,     0,   0,   0],
      [255, 255, 255,  180, 1.0, 0.5,   128, 128, 128],
    ];
    let pass = 0, fail = 0;
    for(const [r,g,b,h,s,v, er,eg,eb] of cases){
      const [or,og,ob] = adjustPixel(r,g,b,h,s,v);
      const ok = or === er && og === eg && ob === eb;
      if(ok) pass++; else { fail++; console.warn('FAIL', {in:[r,g,b,h,s,v], got:[or,og,ob], exp:[er,eg,eb]}); }
    }
    console.log(`HSV selfTest: ${pass}/${pass+fail} passed`);
    return fail === 0;
  };
})(typeof window !== 'undefined' ? window : globalThis);
