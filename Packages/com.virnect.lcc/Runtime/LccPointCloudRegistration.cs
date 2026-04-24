using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Virnect.Lcc
{
    // Point-to-point ICP + Kabsch rigid alignment.
    //
    // 각 LCC 스캔이 서로 다른 로컬 원점을 쓸 때 world 정합을 맞추기 위한 도구.
    // 전통적 레시피:
    //   1) 두 클라우드 voxel-downsample (속도 + noise 저감)
    //   2) 매 iteration 마다 source 각 점의 target nearest neighbor 찾기 (voxel hash-grid)
    //   3) outlier 거르기 (distance > rejectRadius)
    //   4) Kabsch: centroid 제거 → H = Σ p·qᵀ → SVD(H) → R=V Uᵀ → t = q̄ − R p̄
    //   5) source 에 (R,t) 누적 적용, RMSE 수렴 체크
    //
    // 이 구현:
    //   · SVD 는 3×3 symmetric Jacobi eigendecomposition 으로 대체 (Hᵀ H 의 고유벡터 = V)
    //   · multi-resolution 은 외부 루프로 호출측에서 cellSize 바꿔가며 2-pass 권장
    //   · 대각도 rotation 은 FPFH 없이 local minimum 빠지기 쉬움 — 초기값 합리적일 때 유용
    public static class LccPointCloudRegistration
    {
        public struct Options
        {
            public float voxelSize;      // downsample cell (m). 0.5~2m 권장.
            public float rejectRadius;   // outlier 거리 cutoff (m). 0 = 무제한.
            public int   maxIterations;  // ICP 반복
            public float translationTol; // 수렴 판정: |Δt| (m)
            public float rotationTolDeg; // 수렴 판정: |Δθ| (도)

            public static Options Default => new Options
            {
                voxelSize      = 1.0f,
                rejectRadius   = 5.0f,
                maxIterations  = 40,
                translationTol = 0.01f,
                rotationTolDeg = 0.05f,
            };

            public static Options Coarse => new Options
            {
                voxelSize      = 3.0f,
                rejectRadius   = 15f,
                maxIterations  = 25,
                translationTol = 0.05f,
                rotationTolDeg = 0.2f,
            };

            public static Options Fine => new Options
            {
                voxelSize      = 0.5f,
                rejectRadius   = 2f,
                maxIterations  = 40,
                translationTol = 0.005f,
                rotationTolDeg = 0.02f,
            };
        }

        public struct Result
        {
            public Matrix4x4 transform;   // source → target (world-to-world)
            public float     rmseBefore;
            public float     rmseAfter;
            public int       iterations;
            public int       correspondences;
            public bool      converged;
        }

        /// <summary>
        /// source cloud 를 target cloud 에 맞추는 rigid transform 계산.
        /// 반환 transform 은 row-major 4x4 — source vertex 에 적용하면 target world 에 정합.
        /// sourceInit 은 이미 알고 있는 초기 추정 (e.g. 현재 GameObject world transform).
        /// </summary>
        public static Result Align(
            Vector3[] sourcePoints, Vector3[] targetPoints,
            Options opts, Matrix4x4 sourceInit)
        {
            if (sourcePoints == null || sourcePoints.Length == 0 ||
                targetPoints == null || targetPoints.Length == 0)
                throw new ArgumentException("empty point cloud");

            // 1) Voxel downsample (centroid per cell)
            var srcDn = _VoxelDownsample(sourcePoints, opts.voxelSize);
            var tgtDn = _VoxelDownsample(targetPoints, opts.voxelSize);

            // 2) Apply initial transform to source
            for (int i = 0; i < srcDn.Length; i++)
                srcDn[i] = sourceInit.MultiplyPoint3x4(srcDn[i]);

            // 3) Voxel hash-grid on target (for nearest neighbor)
            float cs = opts.voxelSize;
            float invCs = 1f / cs;
            var tgtGrid = new Dictionary<int3, List<int>>(tgtDn.Length / 8);
            for (int i = 0; i < tgtDn.Length; i++)
            {
                var p = tgtDn[i];
                var key = new int3(
                    (int)math.floor(p.x * invCs),
                    (int)math.floor(p.y * invCs),
                    (int)math.floor(p.z * invCs));
                if (!tgtGrid.TryGetValue(key, out var list))
                {
                    list = new List<int>(4);
                    tgtGrid[key] = list;
                }
                list.Add(i);
            }

            float rejR2 = opts.rejectRadius > 0 ? opts.rejectRadius * opts.rejectRadius : float.PositiveInfinity;

            Matrix4x4 totalTransform = sourceInit;
            int iter;
            float rmseBefore = _Rmse(srcDn, tgtDn, tgtGrid, invCs, rejR2, out int matchedInit);
            float prevRmse = rmseBefore;
            bool converged = false;
            int matched = matchedInit;

            for (iter = 0; iter < opts.maxIterations; iter++)
            {
                var srcMatched = new List<Vector3>(srcDn.Length);
                var tgtMatched = new List<Vector3>(srcDn.Length);

                for (int i = 0; i < srcDn.Length; i++)
                {
                    int nn = _NearestInGrid(srcDn[i], tgtDn, tgtGrid, invCs, rejR2);
                    if (nn < 0) continue;
                    srcMatched.Add(srcDn[i]);
                    tgtMatched.Add(tgtDn[nn]);
                }
                matched = srcMatched.Count;
                if (matched < 6)
                    break;

                // 4) Kabsch
                var delta = _Kabsch(srcMatched, tgtMatched);

                // 5) Apply delta to src and accumulate
                for (int i = 0; i < srcDn.Length; i++)
                    srcDn[i] = delta.MultiplyPoint3x4(srcDn[i]);
                totalTransform = delta * totalTransform;

                // Convergence
                float tMag = new Vector3(delta.m03, delta.m13, delta.m23).magnitude;
                Quaternion rq = _ExtractRotation(delta);
                float angDeg = Quaternion.Angle(Quaternion.identity, rq);
                float rmse = _Rmse(srcDn, tgtDn, tgtGrid, invCs, rejR2, out _);
                if (tMag < opts.translationTol && angDeg < opts.rotationTolDeg)
                {
                    converged = true;
                    iter++;
                    prevRmse = rmse;
                    break;
                }
                if (Mathf.Abs(prevRmse - rmse) < 1e-5f)
                {
                    converged = true;
                    iter++;
                    prevRmse = rmse;
                    break;
                }
                prevRmse = rmse;
            }

            return new Result
            {
                transform       = totalTransform,
                rmseBefore      = rmseBefore,
                rmseAfter       = prevRmse,
                iterations      = iter,
                correspondences = matched,
                converged       = converged,
            };
        }

        // ──────── helpers ────────

        static Vector3[] _VoxelDownsample(Vector3[] pts, float cellSize)
        {
            if (cellSize <= 0f) return pts;
            float inv = 1f / cellSize;
            var acc = new Dictionary<int3, (Vector3 sum, int count)>(pts.Length / 8);
            for (int i = 0; i < pts.Length; i++)
            {
                var p = pts[i];
                var key = new int3(
                    (int)math.floor(p.x * inv),
                    (int)math.floor(p.y * inv),
                    (int)math.floor(p.z * inv));
                if (acc.TryGetValue(key, out var e)) { e.sum += p; e.count++; acc[key] = e; }
                else acc[key] = (p, 1);
            }
            var outPts = new Vector3[acc.Count];
            int k = 0;
            foreach (var kv in acc)
                outPts[k++] = kv.Value.sum / kv.Value.count;
            return outPts;
        }

        static int _NearestInGrid(Vector3 q, Vector3[] tgt, Dictionary<int3, List<int>> grid, float invCs, float rejR2)
        {
            int cx = (int)math.floor(q.x * invCs);
            int cy = (int)math.floor(q.y * invCs);
            int cz = (int)math.floor(q.z * invCs);
            int best = -1;
            float bestD2 = rejR2;
            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (!grid.TryGetValue(new int3(cx + dx, cy + dy, cz + dz), out var bucket)) continue;
                for (int bi = 0; bi < bucket.Count; bi++)
                {
                    int idx = bucket[bi];
                    var p = tgt[idx];
                    float dxv = p.x - q.x, dyv = p.y - q.y, dzv = p.z - q.z;
                    float d2 = dxv * dxv + dyv * dyv + dzv * dzv;
                    if (d2 < bestD2) { bestD2 = d2; best = idx; }
                }
            }
            return best;
        }

        static float _Rmse(Vector3[] src, Vector3[] tgt, Dictionary<int3, List<int>> grid, float invCs, float rejR2, out int matched)
        {
            double sum = 0; matched = 0;
            for (int i = 0; i < src.Length; i++)
            {
                int nn = _NearestInGrid(src[i], tgt, grid, invCs, rejR2);
                if (nn < 0) continue;
                var d = tgt[nn] - src[i];
                sum += d.sqrMagnitude;
                matched++;
            }
            if (matched == 0) return float.PositiveInfinity;
            return Mathf.Sqrt((float)(sum / matched));
        }

        // Kabsch algorithm — p_i, q_i 쌍에서 q = R p + t 를 best-fit 하는 R, t 계산
        static Matrix4x4 _Kabsch(List<Vector3> src, List<Vector3> tgt)
        {
            int n = src.Count;
            Vector3 cp = Vector3.zero, cq = Vector3.zero;
            for (int i = 0; i < n; i++) { cp += src[i]; cq += tgt[i]; }
            cp /= n; cq /= n;

            // H = Σ (p - cp)(q - cq)^T  — 3x3 matrix, row-major
            float h00=0,h01=0,h02=0, h10=0,h11=0,h12=0, h20=0,h21=0,h22=0;
            for (int i = 0; i < n; i++)
            {
                float px = src[i].x - cp.x, py = src[i].y - cp.y, pz = src[i].z - cp.z;
                float qx = tgt[i].x - cq.x, qy = tgt[i].y - cq.y, qz = tgt[i].z - cq.z;
                h00 += px * qx; h01 += px * qy; h02 += px * qz;
                h10 += py * qx; h11 += py * qy; h12 += py * qz;
                h20 += pz * qx; h21 += pz * qy; h22 += pz * qz;
            }

            // SVD via Jacobi on H^T H (symmetric 3x3) → eigenvecs = V
            //     R = V * U^T, U = H * V * diag(1/s)
            var HtH = new float3x3(
                h00*h00 + h10*h10 + h20*h20,  h00*h01 + h10*h11 + h20*h21,  h00*h02 + h10*h12 + h20*h22,
                h00*h01 + h10*h11 + h20*h21,  h01*h01 + h11*h11 + h21*h21,  h01*h02 + h11*h12 + h21*h22,
                h00*h02 + h10*h12 + h20*h22,  h01*h02 + h11*h12 + h21*h22,  h02*h02 + h12*h12 + h22*h22);

            _JacobiEigen3x3Sym(HtH, out float3 evals, out float3x3 V);

            // singular values s_i = sqrt(evals_i)
            float s0 = Mathf.Sqrt(Mathf.Max(0f, evals.x));
            float s1 = Mathf.Sqrt(Mathf.Max(0f, evals.y));
            float s2 = Mathf.Sqrt(Mathf.Max(0f, evals.z));
            // U = H * V * diag(1/s)
            float3x3 HV = math.mul(new float3x3(h00,h01,h02, h10,h11,h12, h20,h21,h22), V);
            float3x3 U = new float3x3(
                s0 > 1e-9f ? HV.c0.x/s0 : 0f, s1 > 1e-9f ? HV.c1.x/s1 : 0f, s2 > 1e-9f ? HV.c2.x/s2 : 0f,
                s0 > 1e-9f ? HV.c0.y/s0 : 0f, s1 > 1e-9f ? HV.c1.y/s1 : 0f, s2 > 1e-9f ? HV.c2.y/s2 : 0f,
                s0 > 1e-9f ? HV.c0.z/s0 : 0f, s1 > 1e-9f ? HV.c1.z/s1 : 0f, s2 > 1e-9f ? HV.c2.z/s2 : 0f);

            // R = V * U^T, with determinant check for reflection
            float3x3 R = math.mul(V, math.transpose(U));
            float det = math.determinant(R);
            if (det < 0f)
            {
                // Flip last column of V
                V = new float3x3(V.c0, V.c1, -V.c2);
                R = math.mul(V, math.transpose(U));
            }

            Vector3 t = cq - new Vector3(
                R.c0.x * cp.x + R.c1.x * cp.y + R.c2.x * cp.z,
                R.c0.y * cp.x + R.c1.y * cp.y + R.c2.y * cp.z,
                R.c0.z * cp.x + R.c1.z * cp.y + R.c2.z * cp.z);

            // Unity Matrix4x4 — column vectors c0/c1/c2 → columns 0/1/2
            Matrix4x4 m = Matrix4x4.identity;
            m.m00 = R.c0.x; m.m01 = R.c1.x; m.m02 = R.c2.x; m.m03 = t.x;
            m.m10 = R.c0.y; m.m11 = R.c1.y; m.m12 = R.c2.y; m.m13 = t.y;
            m.m20 = R.c0.z; m.m21 = R.c1.z; m.m22 = R.c2.z; m.m23 = t.z;
            return m;
        }

        // Jacobi eigendecomposition on 3x3 symmetric matrix.
        // Returns eigenvalues (not sorted) and eigenvectors as column vectors of V.
        static void _JacobiEigen3x3Sym(float3x3 A, out float3 evals, out float3x3 V)
        {
            V = float3x3.identity;
            // A 는 symmetric 이어야 함
            float a00 = A.c0.x, a01 = A.c1.x, a02 = A.c2.x;
            float a11 = A.c1.y, a12 = A.c2.y;
            float a22 = A.c2.z;

            for (int iter = 0; iter < 50; iter++)
            {
                float off = Mathf.Abs(a01) + Mathf.Abs(a02) + Mathf.Abs(a12);
                if (off < 1e-12f) break;

                int p, q; float apq;
                float abs01 = Mathf.Abs(a01), abs02 = Mathf.Abs(a02), abs12 = Mathf.Abs(a12);
                if (abs01 >= abs02 && abs01 >= abs12) { p = 0; q = 1; apq = a01; }
                else if (abs02 >= abs12)              { p = 0; q = 2; apq = a02; }
                else                                   { p = 1; q = 2; apq = a12; }

                float app = p == 0 ? a00 : (p == 1 ? a11 : a22);
                float aqq = q == 0 ? a00 : (q == 1 ? a11 : a22);

                float theta = (aqq - app) / (2f * apq);
                float t = (theta >= 0 ? 1f : -1f) / (Mathf.Abs(theta) + Mathf.Sqrt(1f + theta * theta));
                float c = 1f / Mathf.Sqrt(1f + t * t);
                float s = t * c;

                // Update A: A' = G^T A G  (Givens rotation)
                float newApp = app - t * apq;
                float newAqq = aqq + t * apq;

                if (p == 0 && q == 1)
                {
                    a00 = newApp; a11 = newAqq; a01 = 0f;
                    float na02 = c * a02 - s * a12;
                    float na12 = s * a02 + c * a12;
                    a02 = na02; a12 = na12;
                }
                else if (p == 0 && q == 2)
                {
                    a00 = newApp; a22 = newAqq; a02 = 0f;
                    float na01 = c * a01 - s * a12;
                    float na12 = s * a01 + c * a12;
                    a01 = na01; a12 = na12;
                }
                else // p==1, q==2
                {
                    a11 = newApp; a22 = newAqq; a12 = 0f;
                    float na01 = c * a01 - s * a02;
                    float na02 = s * a01 + c * a02;
                    a01 = na01; a02 = na02;
                }

                // Update V: V = V * G
                Vector3 vp = new Vector3(V.c0[p], V.c1[p], V.c2[p]);
                Vector3 vq = new Vector3(V.c0[q], V.c1[q], V.c2[q]);
                // We need to rotate columns p and q.
                // Easier: reconstruct V by explicitly selecting columns.
                float3 col0 = V.c0, col1 = V.c1, col2 = V.c2;
                float3[] cols = { col0, col1, col2 };
                float3 newCp = c * cols[p] - s * cols[q];
                float3 newCq = s * cols[p] + c * cols[q];
                cols[p] = newCp;
                cols[q] = newCq;
                V = new float3x3(cols[0], cols[1], cols[2]);
            }
            evals = new float3(a00, a11, a22);
        }

        static Quaternion _ExtractRotation(Matrix4x4 m)
        {
            var r = new Matrix4x4(
                new Vector4(m.m00, m.m10, m.m20, 0),
                new Vector4(m.m01, m.m11, m.m21, 0),
                new Vector4(m.m02, m.m12, m.m22, 0),
                new Vector4(0, 0, 0, 1));
            return Quaternion.LookRotation(
                new Vector3(r.m02, r.m12, r.m22),
                new Vector3(r.m01, r.m11, r.m21));
        }
    }
}
