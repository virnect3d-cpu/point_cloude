using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Virnect.Lcc.Editor
{
    /// 씬에 배치된 LCC Splat 오브젝트 (`Splat_*`) 마다 자식 `__LccCollider` 를
    /// 만들고 ProxyMesh 자산을 베이크해 MeshCollider 에 연결한다.
    ///
    /// 컨벤션:
    ///   GameObject "Splat_<sceneName>"
    ///     └─ __LccCollider                 ← 신규/기존 자식
    ///          MeshCollider.sharedMesh = LCC_Generated/<sceneName>_ProxyMesh.asset
    ///
    /// 멱등(idempotent) — 이미 메쉬가 정확히 연결돼 있으면 skip.
    public static class LccColliderBuilder
    {
        public const string SplatNamePrefix = "Splat_";
        public const string ColliderChildName = "__LccCollider";

        public struct Report
        {
            public int  baked;       // 새로 베이크
            public int  reused;      // 기존 자산 재사용
            public int  wired;       // MeshCollider 새로 연결
            public int  alreadyOk;   // 이미 정상
            public int  missingPly;  // PLY 못 찾음
            public List<string> messages;
        }

        [MenuItem("Virnect/LCC/Bake Mesh Colliders (Active Scene)", priority = 100)]
        public static void Menu_BakeActiveScene()
        {
            var scn = SceneManager.GetActiveScene();
            var rep = BakeScene(scn, forceRebuild: false);
            EditorSceneManager.MarkSceneDirty(scn);
            _LogReport(rep, scn.name);
        }

        [MenuItem("Virnect/LCC/Bake Mesh Colliders (Active Scene · Rebuild Assets)", priority = 101)]
        public static void Menu_BakeActiveSceneRebuild()
        {
            var scn = SceneManager.GetActiveScene();
            var rep = BakeScene(scn, forceRebuild: true);
            EditorSceneManager.MarkSceneDirty(scn);
            _LogReport(rep, scn.name + " (rebuild)");
        }

        [MenuItem("Virnect/LCC/Open Scene5 + Auto Bake", priority = 110)]
        public static void Menu_OpenScene5AndBake()
        {
            var path = _FindScene5Path();
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError("[LCC] Scene5 (*Scene5_LccRotate*.unity) 를 찾지 못했습니다.");
                return;
            }
            EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            var scn = SceneManager.GetActiveScene();
            var rep = BakeScene(scn, forceRebuild: false);
            EditorSceneManager.MarkSceneDirty(scn);
            EditorSceneManager.SaveScene(scn);
            _LogReport(rep, scn.name + " (auto)");
        }

        public static Report BakeScene(Scene scene, bool forceRebuild)
        {
            var rep = new Report { messages = new List<string>() };
            var roots = scene.GetRootGameObjects();

            // 모든 Splat_* 수집
            var splats = new List<GameObject>();
            foreach (var root in roots) _CollectSplats(root.transform, splats);

            foreach (var go in splats)
            {
                string sceneName = go.name.Substring(SplatNamePrefix.Length);
                var bake = LccProxyMeshBaker.BakeBySceneName(sceneName, forceRebuild);
                if (bake.mesh == null)
                {
                    rep.missingPly++;
                    rep.messages.Add($"  ✗ {go.name} — PLY 없음 (LCC_Drops/<{sceneName}> 확인)");
                    continue;
                }
                if (bake.reused) rep.reused++; else rep.baked++;

                // collider 자식 보장
                var col = _FindChild(go.transform, ColliderChildName);
                if (col == null)
                {
                    var child = new GameObject(ColliderChildName);
                    Undo.RegisterCreatedObjectUndo(child, "LCC bake collider");
                    child.transform.SetParent(go.transform, worldPositionStays: false);
                    child.transform.localPosition = Vector3.zero;
                    child.transform.localRotation = Quaternion.identity;
                    child.transform.localScale    = Vector3.one;
                    col = child.transform;
                }

                var mc = col.GetComponent<MeshCollider>();
                if (mc == null) mc = Undo.AddComponent<MeshCollider>(col.gameObject);

                if (mc.sharedMesh == bake.mesh)
                {
                    rep.alreadyOk++;
                    rep.messages.Add($"  · {go.name} — already wired ({bake.vertexCount:N0} v / {bake.triangleCount:N0} t)");
                }
                else
                {
                    Undo.RecordObject(mc, "LCC wire collider");
                    mc.sharedMesh = bake.mesh;
                    mc.convex = false;       // 정적 환경 콜라이더
                    EditorUtility.SetDirty(mc);
                    rep.wired++;
                    rep.messages.Add($"  ✓ {go.name} → {bake.assetPath} ({bake.vertexCount:N0} v / {bake.triangleCount:N0} t)");
                }
            }
            return rep;
        }

        // splat 카운트만 빠르게 — 자동 healer 가 사용.
        public static int CountUnwiredColliders(Scene scene)
        {
            int unwired = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                var splats = new List<GameObject>();
                _CollectSplats(root.transform, splats);
                foreach (var s in splats)
                {
                    var col = _FindChild(s.transform, ColliderChildName);
                    if (col == null) { unwired++; continue; }
                    var mc = col.GetComponent<MeshCollider>();
                    if (mc == null || mc.sharedMesh == null) unwired++;
                }
            }
            return unwired;
        }

        // ── helpers ──────────────────────────────────────────────────────
        static void _CollectSplats(Transform t, List<GameObject> outList)
        {
            if (t.name.StartsWith(SplatNamePrefix)) outList.Add(t.gameObject);
            for (int i = 0; i < t.childCount; i++) _CollectSplats(t.GetChild(i), outList);
        }

        static Transform _FindChild(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var c = parent.GetChild(i);
                if (c.name == name) return c;
            }
            return null;
        }

        static string _FindScene5Path()
        {
            // 우선 GUID 검색
            var guids = AssetDatabase.FindAssets("Scene5_LccRotate t:SceneAsset");
            if (guids.Length > 0) return AssetDatabase.GUIDToAssetPath(guids[0]);
            // fallback: 패턴 검색
            guids = AssetDatabase.FindAssets("t:SceneAsset Scene5");
            if (guids.Length > 0) return AssetDatabase.GUIDToAssetPath(guids[0]);
            return null;
        }

        static void _LogReport(Report r, string sceneLabel)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[LCC] Mesh Collider bake — {sceneLabel}");
            sb.AppendLine($"  baked={r.baked}  reused={r.reused}  wired={r.wired}  alreadyOk={r.alreadyOk}  missingPly={r.missingPly}");
            foreach (var line in r.messages) sb.AppendLine(line);
            if (r.missingPly > 0) Debug.LogWarning(sb.ToString());
            else                  Debug.Log(sb.ToString());
        }
    }
}
