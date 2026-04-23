using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;

namespace Virnect.Lcc
{
    // Gaussian Splat → Mesh 컬러 트랜스퍼.
    // 프록시 PLY 메쉬 (position only) 에 대해, 가장 가까운 k개 splat 의 색을
    // inverse-distance 가중 평균으로 블렌드하여 Mesh.colors32 에 기록.
    //
    // 왜 k-NN 방식인가:
    //   LCC 는 학습된 splat cloud + (선택) 프록시 메쉬 페어로 구성.
    //   SuGaR/2DGS/GOF 같은 surface-reconstruction 은 재학습이 필요해 에디터-타임엔 과함.
    //   실제 surface geometry 는 XGrids 가 이미 제공 (mesh-files/*.ply),
    //   빠진 건 색상뿐이라 splat DC RGB 를 surface 에 프로젝트하는 것으로 충분.
    //
    // 공간 검색: sparse voxel hash-grid.
    //   cellSize ~ 스케일 종속 (권장: 0.5 ~ 2 m).
    //   각 query 는 주변 3×3×3 cell 만 스캔 → 수십~수천 후보 중 k개 top-distance 선택.
    public static class LccMeshColorizer
    {
        public struct Options
        {
            public float cellSize;          // 권장 0.3 ~ 2.0 m
            public int   k;                 // 이웃 수. photoreal: 1~3, 부드럽게: 6~8
            public float maxRadius;         // 반경 초과 splat 은 무시 (0 = 무제한)
            public Color fallbackColor;     // splat 없는 vertex 기본색
            public bool  parallel;
            public bool  useSourceOpacity;  // splat path 에서 opacity 를 가중치로 반영
            public float distanceFalloff;   // 1/d^falloff — 1=inverse, 2=inverse-square (더 날카로움)

            public static Options Default => new Options
            {
                cellSize         = 1.0f,
                k                = 6,
                maxRadius        = 3.0f,
                fallbackColor    = new Color(0.5f, 0.5f, 0.5f, 1f),
                parallel         = true,
                useSourceOpacity = false,
                distanceFalloff  = 1f,
            };

            public static Options PhotoReal => new Options
            {
                cellSize         = 0.3f,
                k                = 3,
                maxRadius        = 0.5f,
                fallbackColor    = new Color(0.35f, 0.35f, 0.35f, 1f),
                parallel         = true,
                useSourceOpacity = true,
                distanceFalloff  = 2f,
            };
        }

        /// <summary>
        /// Mesh 의 vertex 들에 splat 색을 입혀 Color32[] 반환 & mesh.colors32 에 쓰기.
        /// splats 좌표계는 splat 원본 좌표계 (Unity world 회전 적용 전) 와 동일해야 함.
        /// </summary>
        public static Color32[] Colorize(Mesh mesh, LccSplatDecoder.Point[] splats, Options opts)
        {
            if (splats == null || splats.Length == 0) return null;
            var positions = new Vector3[splats.Length];
            var srcColors = new Color32[splats.Length];
            var srcWeights = opts.useSourceOpacity ? new float[splats.Length] : null;
            for (int i = 0; i < splats.Length; i++)
            {
                var p = splats[i].position;
                positions[i] = new Vector3(p.x, p.y, p.z);
                srcColors[i] = splats[i].color;
                if (srcWeights != null) srcWeights[i] = Mathf.Clamp01(splats[i].opacity);
            }
            return Colorize(mesh, positions, srcColors, opts, srcWeights);
        }

        /// <summary>
        /// Generic point-cloud → mesh 컬러 전송 (positions + colors).
        /// External colored PLY (XGrids_Splats_LODn.ply 등) 를 source 로 쓸 때 이 오버로드 사용.
        /// srcWeights 는 optional — splat opacity 같은 per-point 가중치를 k-NN blend 에 곱함.
        /// </summary>
        public static Color32[] Colorize(Mesh mesh, Vector3[] srcPositions, Color32[] srcColors, Options opts, float[] srcWeights = null)
        {
            if (mesh == null || srcPositions == null || srcPositions.Length == 0)
                return null;
            if (srcColors == null || srcColors.Length != srcPositions.Length)
                throw new System.ArgumentException("srcPositions 와 srcColors 길이 불일치");
            if (srcWeights != null && srcWeights.Length != srcPositions.Length)
                throw new System.ArgumentException("srcWeights 길이 불일치");

            var verts = mesh.vertices;
            int n = verts.Length;
            var colors = new Color32[n];

            float cs = opts.cellSize;
            float invCs = 1f / cs;
            float maxR2 = opts.maxRadius > 0 ? opts.maxRadius * opts.maxRadius : float.PositiveInfinity;

            // 1) 빌드: sparse hash-grid
            var grid = new Dictionary<int3, List<int>>(capacity: srcPositions.Length / 128);
            for (int i = 0; i < srcPositions.Length; i++)
            {
                var p = srcPositions[i];
                var key = new int3(
                    (int)math.floor(p.x * invCs),
                    (int)math.floor(p.y * invCs),
                    (int)math.floor(p.z * invCs));
                if (!grid.TryGetValue(key, out var list))
                {
                    list = new List<int>(8);
                    grid[key] = list;
                }
                list.Add(i);
            }

            // 2) 쿼리: per-vertex k-NN blend
            int k = math.max(1, opts.k);
            Color32 fallback = opts.fallbackColor;

            void ProcessRange(int from, int to)
            {
                var bestD2 = new float[k];
                var bestI  = new int[k];

                for (int vi = from; vi < to; vi++)
                {
                    var v = verts[vi];
                    int cx = (int)math.floor(v.x * invCs);
                    int cy = (int)math.floor(v.y * invCs);
                    int cz = (int)math.floor(v.z * invCs);

                    for (int i = 0; i < k; i++) { bestD2[i] = float.PositiveInfinity; bestI[i] = -1; }

                    for (int dz = -1; dz <= 1; dz++)
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (!grid.TryGetValue(new int3(cx + dx, cy + dy, cz + dz), out var bucket))
                            continue;
                        for (int bi = 0; bi < bucket.Count; bi++)
                        {
                            int si = bucket[bi];
                            var sp = srcPositions[si];
                            float dxv = sp.x - v.x, dyv = sp.y - v.y, dzv = sp.z - v.z;
                            float d2 = dxv * dxv + dyv * dyv + dzv * dzv;
                            if (d2 > maxR2) continue;

                            if (d2 >= bestD2[k - 1]) continue;
                            int slot = k - 1;
                            while (slot > 0 && bestD2[slot - 1] > d2)
                            {
                                bestD2[slot] = bestD2[slot - 1];
                                bestI[slot]  = bestI[slot - 1];
                                slot--;
                            }
                            bestD2[slot] = d2;
                            bestI[slot]  = si;
                        }
                    }

                    if (bestI[0] < 0) { colors[vi] = fallback; continue; }
                    float wsum = 0f, r = 0f, g = 0f, b = 0f;
                    float falloff = math.max(0.5f, opts.distanceFalloff);
                    for (int i = 0; i < k; i++)
                    {
                        if (bestI[i] < 0) break;
                        float d = math.max(1e-4f, math.sqrt(bestD2[i]));
                        float w = 1f / math.pow(d, falloff);
                        if (srcWeights != null) w *= math.max(0f, srcWeights[bestI[i]]);
                        var c = srcColors[bestI[i]];
                        r += c.r * w; g += c.g * w; b += c.b * w; wsum += w;
                    }
                    if (wsum <= 0f) { colors[vi] = fallback; continue; }
                    float inv = 1f / wsum;
                    colors[vi] = new Color32(
                        (byte)math.clamp(r * inv, 0f, 255f),
                        (byte)math.clamp(g * inv, 0f, 255f),
                        (byte)math.clamp(b * inv, 0f, 255f),
                        255);
                }
            }

            if (opts.parallel && n > 4096)
            {
                int threads = System.Environment.ProcessorCount;
                int chunk = (n + threads - 1) / threads;
                Parallel.For(0, threads, t =>
                {
                    int from = t * chunk;
                    int to = math.min(n, from + chunk);
                    if (from < to) ProcessRange(from, to);
                });
            }
            else
            {
                ProcessRange(0, n);
            }

            mesh.colors32 = colors;
            return colors;
        }
    }
}
