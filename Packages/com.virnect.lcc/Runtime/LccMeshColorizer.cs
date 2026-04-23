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
            public float cellSize;      // 권장 0.5 ~ 2.0 m (factory 수준)
            public int   k;             // 이웃 수 (4~8 권장)
            public float maxRadius;     // 이 반경 초과 splat 은 무시 (0 = 무제한)
            public Color fallbackColor; // splat 없는 vertex 기본색
            public bool  parallel;

            public static Options Default => new Options
            {
                cellSize      = 1.0f,
                k             = 6,
                maxRadius     = 3.0f,
                fallbackColor = new Color(0.5f, 0.5f, 0.5f, 1f),
                parallel      = true,
            };
        }

        /// <summary>
        /// Mesh 의 vertex 들에 splat 색을 입혀 Color32[] 반환 & mesh.colors32 에 쓰기.
        /// splats 좌표계는 splat 원본 좌표계 (Unity world 회전 적용 전) 와 동일해야 함.
        /// mesh 는 collider 로 이미 씬에 적재된 상태일 수 있으니, 원본 정점 위치를 그대로 사용.
        /// </summary>
        public static Color32[] Colorize(Mesh mesh, LccSplatDecoder.Point[] splats, Options opts)
        {
            if (mesh == null || splats == null || splats.Length == 0)
                return null;

            var verts = mesh.vertices;
            int n = verts.Length;
            var colors = new Color32[n];

            float cs = opts.cellSize;
            float invCs = 1f / cs;
            float maxR2 = opts.maxRadius > 0 ? opts.maxRadius * opts.maxRadius : float.PositiveInfinity;

            // 1) 빌드: sparse hash-grid
            var grid = new Dictionary<int3, List<int>>(capacity: splats.Length / 128);
            for (int i = 0; i < splats.Length; i++)
            {
                var p = splats[i].position;
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
                // thread-local top-k heap (simple flat arrays — k 작음)
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
                            var sp = splats[si].position;
                            float dxv = sp.x - v.x, dyv = sp.y - v.y, dzv = sp.z - v.z;
                            float d2 = dxv * dxv + dyv * dyv + dzv * dzv;
                            if (d2 > maxR2) continue;

                            // insert into top-k (bubble up)
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

                    // inverse-distance blend
                    if (bestI[0] < 0) { colors[vi] = fallback; continue; }
                    float wsum = 0f, r = 0f, g = 0f, b = 0f;
                    for (int i = 0; i < k; i++)
                    {
                        if (bestI[i] < 0) break;
                        float w = 1f / math.max(1e-4f, math.sqrt(bestD2[i]));
                        var c = splats[bestI[i]].color;
                        r += c.r * w; g += c.g * w; b += c.b * w; wsum += w;
                    }
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
