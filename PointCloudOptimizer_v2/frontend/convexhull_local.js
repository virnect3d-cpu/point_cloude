/**
 * convexhull_local.js — Pure JavaScript 3D Convex Hull
 * CDN 없이 로컬에서 동작하는 THREE.ConvexBufferGeometry 구현
 * Algorithm: Incremental Quickhull (Barber et al. 1996)
 *
 * Usage: THREE.ConvexBufferGeometry(pointsArray)
 *   where pointsArray is an Array of THREE.Vector3 or {x,y,z}
 */
(function (THREE) {
  'use strict';

  const EPS = 1e-10;

  // ── 수학 헬퍼 ──────────────────────────────────────────────────────────
  function cross(ux, uy, uz, vx, vy, vz) {
    return [uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx];
  }

  // ── Face (반평면 포함 체크 + 방향 뒤집기) ───────────────────────────
  function Face(ia, ib, ic, verts) {
    this.a = ia; this.b = ib; this.c = ic;
    this.above = [];    // 이 면 위에 있는 점 인덱스 목록
    this._calc(verts);
  }

  Face.prototype._calc = function (verts) {
    const pa = verts[this.a], pb = verts[this.b], pc = verts[this.c];
    const ux = pb[0] - pa[0], uy = pb[1] - pa[1], uz = pb[2] - pa[2];
    const vx = pc[0] - pa[0], vy = pc[1] - pa[1], vz = pc[2] - pa[2];
    let [nx, ny, nz] = cross(ux, uy, uz, vx, vy, vz);
    const l = Math.sqrt(nx * nx + ny * ny + nz * nz);
    if (l > EPS) { nx /= l; ny /= l; nz /= l; }
    this.nx = nx; this.ny = ny; this.nz = nz;
    this.d = nx * pa[0] + ny * pa[1] + nz * pa[2];
  };

  Face.prototype.dist = function (p) {
    return this.nx * p[0] + this.ny * p[1] + this.nz * p[2] - this.d;
  };

  Face.prototype.flip = function () {
    const tmp = this.a; this.a = this.c; this.c = tmp;
    this.nx = -this.nx; this.ny = -this.ny; this.nz = -this.nz; this.d = -this.d;
    return this;
  };

  // 방향성 엣지 포함 여부
  Face.prototype.hasEdge = function (ia, ib) {
    const v = [this.a, this.b, this.c];
    for (let i = 0; i < 3; i++) {
      if (v[i] === ia && v[(i + 1) % 3] === ib) return true;
    }
    return false;
  };

  // ── 초기 사면체 찾기 ───────────────────────────────────────────────
  function buildSimplex(verts) {
    const n = verts.length;

    // ±X ±Y ±Z 극점
    const ext = [];
    for (let ax = 0; ax < 3; ax++) {
      let lo = 0, hi = 0;
      for (let i = 1; i < n; i++) {
        if (verts[i][ax] < verts[lo][ax]) lo = i;
        if (verts[i][ax] > verts[hi][ax]) hi = i;
      }
      ext.push(lo, hi);
    }

    // 극점 중 가장 먼 쌍 (AB)
    let iA = ext[0], iB = ext[1], maxD2 = 0;
    for (let i = 0; i < ext.length; i++) {
      for (let j = i + 1; j < ext.length; j++) {
        const ei = ext[i], ej = ext[j];
        const dx = verts[ei][0] - verts[ej][0];
        const dy = verts[ei][1] - verts[ej][1];
        const dz = verts[ei][2] - verts[ej][2];
        const d2 = dx * dx + dy * dy + dz * dz;
        if (d2 > maxD2) { maxD2 = d2; iA = ei; iB = ej; }
      }
    }
    if (maxD2 < EPS) return null;

    // AB 직선에서 가장 먼 점 (C)
    const [ax, ay, az] = verts[iA];
    const ABx = verts[iB][0] - ax, ABy = verts[iB][1] - ay, ABz = verts[iB][2] - az;
    let iC = -1; maxD2 = 0;
    for (let i = 0; i < n; i++) {
      if (i === iA || i === iB) continue;
      const px = verts[i][0] - ax, py = verts[i][1] - ay, pz = verts[i][2] - az;
      const [cx, cy, cz] = cross(ABx, ABy, ABz, px, py, pz);
      const d2 = cx * cx + cy * cy + cz * cz;
      if (d2 > maxD2) { maxD2 = d2; iC = i; }
    }
    if (iC < 0 || maxD2 < EPS) return null;

    // 평면 ABC에서 가장 먼 점 (D)
    const f0 = new Face(iA, iB, iC, verts);
    let iD = -1; maxD2 = 0;
    for (let i = 0; i < n; i++) {
      if (i === iA || i === iB || i === iC) continue;
      const d = Math.abs(f0.dist(verts[i]));
      if (d > maxD2) { maxD2 = d; iD = i; }
    }
    if (iD < 0 || maxD2 < EPS) return null;

    return [iA, iB, iC, iD];
  }

  // ── 엣지 맵 (방향성 엣지 → Face) ──────────────────────────────────
  function makeEdgeMap(faces) {
    const map = new Map();
    for (const f of faces) {
      for (const [ea, eb] of [[f.a, f.b], [f.b, f.c], [f.c, f.a]]) {
        const key = ea + '|' + eb;
        if (!map.has(key)) map.set(key, []);
        map.get(key).push(f);
      }
    }
    return map;
  }

  function addFaceToEdgeMap(map, f) {
    for (const [ea, eb] of [[f.a, f.b], [f.b, f.c], [f.c, f.a]]) {
      const key = ea + '|' + eb;
      if (!map.has(key)) map.set(key, []);
      map.get(key).push(f);
    }
  }

  function removeFaceFromEdgeMap(map, f) {
    for (const [ea, eb] of [[f.a, f.b], [f.b, f.c], [f.c, f.a]]) {
      const key = ea + '|' + eb;
      const arr = map.get(key);
      if (arr) {
        const idx = arr.indexOf(f);
        if (idx >= 0) arr.splice(idx, 1);
        if (arr.length === 0) map.delete(key);
      }
    }
  }

  // ── Quickhull 메인 ────────────────────────────────────────────────
  function quickhull(verts) {
    const n = verts.length;
    if (n < 4) return [];

    const simplex = buildSimplex(verts);
    if (!simplex) return [];
    const [iA, iB, iC, iD] = simplex;

    // 사면체 무게중심
    const center = [
      (verts[iA][0] + verts[iB][0] + verts[iC][0] + verts[iD][0]) / 4,
      (verts[iA][1] + verts[iB][1] + verts[iC][1] + verts[iD][1]) / 4,
      (verts[iA][2] + verts[iB][2] + verts[iC][2] + verts[iD][2]) / 4,
    ];

    // 사면체 4개 면 (법선이 바깥을 향하도록)
    const facesSet = new Set();
    for (const [a, b, c] of [[iA, iB, iC], [iA, iC, iD], [iA, iD, iB], [iB, iD, iC]]) {
      const f = new Face(a, b, c, verts);
      if (f.dist(center) > 0) f.flip(); // 중심이 뒤에 있도록
      facesSet.add(f);
    }

    // 엣지 맵 초기화
    const edgeMap = makeEdgeMap(facesSet);

    const simpSet = new Set(simplex);

    // 각 점을 그 위에 있는 면에 할당
    for (let i = 0; i < n; i++) {
      if (simpSet.has(i)) continue;
      for (const f of facesSet) {
        if (f.dist(verts[i]) > EPS) { f.above.push(i); break; }
      }
    }

    // 처리 큐 (위에 점이 있는 면)
    const queue = [...facesSet].filter(f => f.above.length > 0);

    while (queue.length > 0) {
      const face = queue.pop();
      if (!facesSet.has(face)) continue;          // 이미 제거됨
      if (!face.above || face.above.length === 0) continue;

      // 이 면에서 가장 높이 있는 점(apex) 찾기
      let apex = -1, apexD = -Infinity;
      for (const i of face.above) {
        const d = face.dist(verts[i]);
        if (d > apexD) { apexD = d; apex = i; }
      }
      if (apex < 0) continue;

      // apex에서 보이는 면 전체 찾기 O(faces)
      const visible = new Set([...facesSet].filter(f => f.dist(verts[apex]) > EPS));
      if (visible.size === 0) continue;

      // 호라이즌 엣지: visible face의 엣지 중 반대 방향 엣지가 비가시 면에 있는 것
      const horizon = [];
      for (const vf of visible) {
        for (const [ea, eb] of [[vf.a, vf.b], [vf.b, vf.c], [vf.c, vf.a]]) {
          // 역방향 엣지 (eb→ea)가 비가시 면에 속하는지 확인
          const revKey = eb + '|' + ea;
          const revFaces = edgeMap.get(revKey);
          if (revFaces) {
            for (const rf of revFaces) {
              if (!visible.has(rf)) { horizon.push([ea, eb]); break; }
            }
          }
        }
      }
      if (horizon.length === 0) continue;

      // 고아 점 수집 (가시 면의 above 목록 — apex 제외)
      const orphanSet = new Set();
      for (const vf of visible) {
        for (const i of vf.above) { if (i !== apex) orphanSet.add(i); }
      }

      // 가시 면 제거
      for (const vf of visible) {
        removeFaceFromEdgeMap(edgeMap, vf);
        facesSet.delete(vf);
      }

      // 호라이즌 엣지 → apex 새 면 생성
      const newFaces = [];
      for (const [ea, eb] of horizon) {
        const nf = new Face(ea, eb, apex, verts);
        if (nf.dist(center) > 0) nf.flip(); // 바깥쪽 법선 유지
        nf.above = [];
        facesSet.add(nf);
        addFaceToEdgeMap(edgeMap, nf);
        newFaces.push(nf);
      }

      // 고아 점을 새 면에 재분배
      for (const i of orphanSet) {
        for (const nf of newFaces) {
          if (nf.dist(verts[i]) > EPS) { nf.above.push(i); break; }
        }
      }

      // 위에 점이 있는 새 면을 큐에 추가
      for (const nf of newFaces) {
        if (nf.above.length > 0) queue.push(nf);
      }
    }

    return [...facesSet].map(f => [f.a, f.b, f.c]);
  }

  // ── THREE.ConvexBufferGeometry ──────────────────────────────────────
  // ES6 class 상속 방식 — THREE.js r128+ 호환 (Function.call 방식 제거)
  class ConvexBufferGeometry extends THREE.BufferGeometry {
    constructor(points) {
      super();

      if (!points || points.length < 4) {
        this.setAttribute('position', new THREE.BufferAttribute(new Float32Array(0), 3));
        return;
      }

      // 입력 포인트 → [x,y,z] 배열, 과도한 경우 서브샘플
      let pts = points.map(p => Array.isArray(p) ? p : [p.x, p.y, p.z]);
      if (pts.length > 2000) {
        const stride = Math.ceil(pts.length / 2000);
        pts = pts.filter((_, i) => i % stride === 0);
      }

      let triangles;
      try {
        triangles = quickhull(pts);
      } catch (e) {
        console.warn('[ConvexHull] 오류:', e);
        triangles = [];
      }

      if (triangles.length === 0) {
        this.setAttribute('position', new THREE.BufferAttribute(new Float32Array(0), 3));
        return;
      }

      // 위치 버퍼 (전체 점 — 인덱스 버퍼로 참조)
      const posArr = new Float32Array(pts.length * 3);
      for (let i = 0; i < pts.length; i++) {
        posArr[i * 3] = pts[i][0];
        posArr[i * 3 + 1] = pts[i][1];
        posArr[i * 3 + 2] = pts[i][2];
      }

      const flatIdx = triangles.flat();
      const maxIdx = flatIdx.length > 0 ? Math.max(...flatIdx) : 0;
      const idxArr = maxIdx > 65535 ? new Uint32Array(flatIdx) : new Uint16Array(flatIdx);

      this.setAttribute('position', new THREE.BufferAttribute(posArr, 3));
      this.setIndex(new THREE.BufferAttribute(idxArr, 1));
      this.computeVertexNormals();
    }
  }

  THREE.ConvexBufferGeometry = ConvexBufferGeometry;

})(window.THREE || (window.THREE = {}));
