using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Virnect.Lcc.Editor
{
    // PLY proxy 메쉬 → Unity Mesh asset 베이커.
    //
    // 사용처:
    //   1. LCC 임포트 시 자동 호출 (collider 슬롯에 곧바로 연결할 자산 필요).
    //   2. Editor 메뉴 / LccImporterWindow → "Bake Mesh Colliders" 액션.
    //
    // 결과 자산은 Assets/LCC_Generated/<sceneName>_ProxyMesh.asset.
    public static class LccProxyMeshBaker
    {
        public const string GeneratedFolder = "Assets/LCC_Generated";

        /// 베이크 결과. mesh 가 null 이면 PLY 가 못 찾혔거나 파싱 실패.
        public struct Bake
        {
            public Mesh   mesh;
            public string assetPath;
            public bool   reused;
            public int    vertexCount;
            public int    triangleCount;
            public Bounds bounds;
        }

        /// sceneName 으로 LCC_Drops 아래에서 PLY 자동 탐색 후 베이크.
        ///   1) Assets/LCC_Drops/<sceneName>/<sceneName>.ply
        ///   2) Assets/LCC_Drops/<sceneName>/mesh-files/<sceneName>.ply
        ///   3) Assets/LCC_Drops/<lower variant>/...   (대소문자 폴더 변종)
        public static Bake BakeBySceneName(string sceneName, bool forceRebuild = false)
        {
            var ply = LocateProxyPly(sceneName);
            if (ply == null)
            {
                return new Bake { mesh = null, assetPath = null };
            }
            return BakeFromPly(ply, sceneName, forceRebuild);
        }

        /// 명시 PLY 경로로 베이크.
        public static Bake BakeFromPly(string plyAbsOrRelPath, string sceneName, bool forceRebuild = false)
        {
            string assetPath = $"{GeneratedFolder}/{sceneName}_ProxyMesh.asset";

            if (!forceRebuild)
            {
                var existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
                if (existing != null)
                {
                    return new Bake
                    {
                        mesh = existing,
                        assetPath = assetPath,
                        reused = true,
                        vertexCount = existing.vertexCount,
                        triangleCount = existing.triangles.Length / 3,
                        bounds = existing.bounds,
                    };
                }
            }

            string absPly = Path.IsPathRooted(plyAbsOrRelPath)
                ? plyAbsOrRelPath
                : Path.GetFullPath(plyAbsOrRelPath);
            if (!File.Exists(absPly))
            {
                Debug.LogError($"[LCC] PLY 없음: {absPly}");
                return new Bake { mesh = null };
            }

            LccPlyTriMeshReader.Result r;
            try { r = LccPlyTriMeshReader.Read(absPly); }
            catch (System.Exception e)
            {
                Debug.LogError($"[LCC] PLY 파싱 실패 ({sceneName}): {e.Message}");
                return new Bake { mesh = null };
            }

            EnsureFolder(GeneratedFolder);

            var mesh = new Mesh { name = sceneName + "_ProxyMesh" };
            // 254k 같이 큰 메쉬는 UInt16 한도(65535) 초과 → 반드시 UInt32.
            if (r.vertices.Length > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(r.vertices);
            mesh.SetTriangles(r.triangles, 0, calculateBounds: true);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            // 기존 자산 덮어쓰기. AssetDatabase.CreateAsset 은 같은 경로면 실패하므로
            // overwrite 모드로 LoadMain → Replace 사용.
            var prev = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            if (prev != null)
            {
                EditorUtility.CopySerialized(mesh, prev);
                Object.DestroyImmediate(mesh);
                mesh = prev;
                EditorUtility.SetDirty(mesh);
            }
            else
            {
                AssetDatabase.CreateAsset(mesh, assetPath);
            }
            AssetDatabase.SaveAssetIfDirty(mesh);

            return new Bake
            {
                mesh = mesh,
                assetPath = assetPath,
                reused = false,
                vertexCount = mesh.vertexCount,
                triangleCount = mesh.triangles.Length / 3,
                bounds = mesh.bounds,
            };
        }

        public static string LocateProxyPly(string sceneName)
        {
            string root = "Assets/LCC_Drops";
            // 폴더 이름이 sceneName 과 다를 수 있어 (대소문자 'Shinwon' vs 'ShinWon')
            // 직접 매칭 + case-insensitive fallback.
            string[] candidateDirs =
            {
                $"{root}/{sceneName}",
                $"{root}/{sceneName.Replace("ShinWon", "Shinwon")}",
                $"{root}/{sceneName.Replace("Shinwon", "ShinWon")}",
            };
            foreach (var d in candidateDirs)
            {
                string a = $"{d}/{sceneName}.ply";
                string b = $"{d}/mesh-files/{sceneName}.ply";
                if (File.Exists(a)) return a;
                if (File.Exists(b)) return b;
            }
            // 마지막 fallback — LCC_Drops 전체 스캔
            if (Directory.Exists(root))
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    string a = Path.Combine(dir, sceneName + ".ply");
                    string b = Path.Combine(dir, "mesh-files", sceneName + ".ply");
                    if (File.Exists(a)) return a.Replace('\\', '/');
                    if (File.Exists(b)) return b.Replace('\\', '/');
                }
            }
            return null;
        }

        static void EnsureFolder(string assetFolder)
        {
            if (AssetDatabase.IsValidFolder(assetFolder)) return;
            string parent = Path.GetDirectoryName(assetFolder)?.Replace('\\', '/');
            string leaf   = Path.GetFileName(assetFolder);
            if (string.IsNullOrEmpty(parent) || !AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
