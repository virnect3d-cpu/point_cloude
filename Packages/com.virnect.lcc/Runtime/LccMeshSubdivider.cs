using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Virnect.Lcc
{
    // Loop-style midpoint triangle subdivision (geometric midpoint only, smoothing 없음).
    // 한 pass 당 각 삼각형 → 4 개 삼각형, 정점은 edge 개수만큼 증가.
    //
    //      v0                 v0
    //      /\                 /\
    //     /  \               /m0\
    //    /    \      →      /----\
    //   /      \           / \  / \
    //  v1------v2         /m1\/m2 \
    //                    v1---m-----v2
    //
    // 용도: k-NN colorize 전에 mesh vertex 밀도 올려 컬러 해상도 향상.
    // 성능: 1 pass ~ 4x triangles / ~2x verts. 2 pass 부터는 급격히 무거워지니 1 권장.
    public static class LccMeshSubdivider
    {
        public static Mesh Subdivide(Mesh src, int passes, bool recalcNormals = false)
        {
            if (src == null) return null;
            if (passes <= 0) return _Clone(src, recalcNormals);

            Vector3[] verts = src.vertices;
            int[] tris      = src.triangles;

            for (int pass = 0; pass < passes; pass++)
            {
                _SubdivideOnce(verts, tris, out verts, out tris);
            }

            var m = new Mesh { name = src.name + $"_subdiv{passes}" };
            m.indexFormat = (verts.Length > 65535) ? IndexFormat.UInt32 : src.indexFormat;
            m.SetVertices(verts);
            m.SetTriangles(tris, 0, calculateBounds: true);
            // Unlit vertex-color shader 용이면 normals 불필요 (큰 메쉬에서 초~수십초 절약)
            if (recalcNormals) m.RecalculateNormals();
            return m;
        }

        static void _SubdivideOnce(Vector3[] v, int[] tris, out Vector3[] v2, out int[] t2)
        {
            int triCount = tris.Length / 3;
            // 상한: 각 tri 당 3 개 edge, 공유 고려 안 하면 최대 3*triCount midpoints.
            // 실제로는 ≈ 1.5*triCount (manifold 메쉬).
            int maxNewV = v.Length + triCount * 3 + 16;
            var vOut = new Vector3[maxNewV];
            System.Array.Copy(v, vOut, v.Length);
            int vCount = v.Length;

            // 초기 capacity = tri count × 2 (manifold estimate) · rehash 방지
            var edgeMid = new Dictionary<long, int>(triCount * 2);

            t2 = new int[triCount * 4 * 3];
            int wi = 0;

            for (int t = 0; t < triCount; t++)
            {
                int a = tris[t * 3 + 0];
                int b = tris[t * 3 + 1];
                int c = tris[t * 3 + 2];

                int mab = _Midpoint(vOut, ref vCount, edgeMid, a, b);
                int mbc = _Midpoint(vOut, ref vCount, edgeMid, b, c);
                int mca = _Midpoint(vOut, ref vCount, edgeMid, c, a);

                t2[wi++] = a;   t2[wi++] = mab; t2[wi++] = mca;
                t2[wi++] = mab; t2[wi++] = b;   t2[wi++] = mbc;
                t2[wi++] = mca; t2[wi++] = mbc; t2[wi++] = c;
                t2[wi++] = mab; t2[wi++] = mbc; t2[wi++] = mca;
            }

            if (vCount == vOut.Length) { v2 = vOut; }
            else { v2 = new Vector3[vCount]; System.Array.Copy(vOut, v2, vCount); }
        }

        static int _Midpoint(Vector3[] verts, ref int vCount, Dictionary<long, int> cache, int i, int j)
        {
            long key = i < j ? ((long)i << 32) | (uint)j : ((long)j << 32) | (uint)i;
            if (cache.TryGetValue(key, out int mid)) return mid;
            mid = vCount;
            verts[vCount++] = (verts[i] + verts[j]) * 0.5f;
            cache[key] = mid;
            return mid;
        }

        static Mesh _Clone(Mesh src, bool recalcNormals)
        {
            var m = new Mesh { name = src.name + "_clone", indexFormat = src.indexFormat };
            m.SetVertices(src.vertices);
            m.SetTriangles(src.triangles, 0, calculateBounds: true);
            if (recalcNormals) m.RecalculateNormals();
            return m;
        }
    }
}
