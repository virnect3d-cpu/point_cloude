using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using Virnect.Lcc;
using Virnect.Lcc.Editor;

namespace LccDropForge
{
    /// 백엔드 v1 Python 서버의 `/api/mesh-collider` 엔드포인트를 호출하여
    /// Poisson/BPA 기반 메쉬 콜라이더를 생성. XGrids proxy mesh PLY 직결과 대비됨.
    ///
    /// 사용:
    ///   GenerateAsync(lccAssetPath, (mesh, err) => { ... })
    ///   Menu_BackendBakeAllSplats — 활성 씬의 모든 Splat_ 일괄 처리
    public static class LccBackendColliderBuilder
    {
        [Serializable] class MeshRespFloat  { public float[] vertices; public int[] triangles; }
        [Serializable] class MeshRespNested { public NestedV[] vertices; public NestedI[] triangles; }
        [Serializable] class NestedV { public float[] v; }
        [Serializable] class NestedI { public int[] t; }
        [Serializable] class PartsResp      { public MeshRespFloat[] parts; }

        public const int    DefaultDepth      = 7;
        public const int    DefaultTargetTris = 3000;
        public const bool   DefaultConvexParts = false;

        // ─── 백엔드 호출 ───────────────────────────────────────────────────
        public static void GenerateAsync(string lccAssetPath, Action<Mesh, string> onDone,
                                         int depth = DefaultDepth, int targetTris = DefaultTargetTris,
                                         bool convexParts = DefaultConvexParts)
        {
            if (!LccServerManager.IsRunning())
            {
                onDone?.Invoke(null,
                    "v1 백엔드 서버 안 켜짐. LCC Importer 창 (Tools > LCC > Importer) → 'Start' 버튼 또는 메뉴 ' Backend · Start v1 server'");
                return;
            }
            string absPath = Path.GetFullPath(lccAssetPath);
            string label = Path.GetFileNameWithoutExtension(absPath);

            Debug.Log($"[Backend Collider] {label} → upload 시작...");
            LccV1Client.UploadPath(absPath, (sid, err) =>
            {
                if (err != null) { onDone?.Invoke(null, $"upload 실패: {err}"); return; }
                Debug.Log($"[Backend Collider] {label} sid={sid} · /api/mesh-collider 호출");

                string url = LccV1Client.BaseUrl + "/api/mesh-collider/" + sid +
                             $"?method=poisson&depth={depth}" +
                             $"&target_tris={targetTris}&snap=2" +
                             $"&convex_parts={(convexParts ? "true" : "false")}";
                var req = UnityWebRequest.Get(url);
                req.timeout = 600;
                var op = req.SendWebRequest();
                op.completed += _ =>
                {
                    try
                    {
                        if (req.result != UnityWebRequest.Result.Success)
                        {
                            onDone?.Invoke(null, $"HTTP {req.responseCode}: {req.downloadHandler.text?.Substring(0, Mathf.Min(300, req.downloadHandler.text.Length))}");
                            return;
                        }
                        var json = req.downloadHandler.text;
                        Debug.Log($"[Backend Collider] {label} 응답 {json.Length / 1024.0:F1} KB · 처음 300바이트:\n{json.Substring(0, Mathf.Min(300, json.Length))}");
                        var mesh = ParseMesh(json, label);
                        onDone?.Invoke(mesh, mesh != null ? null : "JSON 파싱 실패 (위 로그의 응답 형식을 확인 후 ParseMesh 보강 필요)");
                    }
                    finally { req.Dispose(); }
                };
            });
        }

        // ─── JSON 파싱 (여러 형식 시도) ────────────────────────────────────
        static Mesh ParseMesh(string json, string label)
        {
            // 형식 1: { "vertices": [x,y,z, x,y,z, ...], "triangles": [a,b,c, a,b,c, ...] }
            try
            {
                var single = JsonUtility.FromJson<MeshRespFloat>(json);
                if (single != null && single.vertices != null && single.vertices.Length >= 9 && single.triangles != null && single.triangles.Length >= 3)
                    return BuildMesh(single.vertices, single.triangles, label);
            }
            catch (Exception e) { Debug.LogWarning($"[Backend Collider] 형식 1 시도 실패: {e.Message}"); }

            // 형식 2: { "parts": [{vertices, triangles}, ...] } — convex parts (ACD)
            try
            {
                var parts = JsonUtility.FromJson<PartsResp>(json);
                if (parts != null && parts.parts != null && parts.parts.Length > 0)
                    return BuildMeshFromParts(parts.parts, label);
            }
            catch (Exception e) { Debug.LogWarning($"[Backend Collider] 형식 2 시도 실패: {e.Message}"); }

            return null;
        }

        static Mesh BuildMesh(float[] verts, int[] tris, string label)
        {
            int vn = verts.Length / 3;
            var v = new Vector3[vn];
            for (int i = 0; i < vn; i++) v[i] = new Vector3(verts[i * 3], verts[i * 3 + 1], verts[i * 3 + 2]);
            var m = new Mesh
            {
                name = label + "_BackendCollider",
                indexFormat = (vn > 65535) ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16,
                hideFlags = HideFlags.DontSave,
            };
            m.vertices = v;
            m.triangles = tris;
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        static Mesh BuildMeshFromParts(MeshRespFloat[] parts, string label)
        {
            var allV = new List<Vector3>();
            var allT = new List<int>();
            foreach (var p in parts)
            {
                if (p.vertices == null || p.triangles == null) continue;
                int baseIdx = allV.Count;
                for (int i = 0; i < p.vertices.Length / 3; i++)
                    allV.Add(new Vector3(p.vertices[i * 3], p.vertices[i * 3 + 1], p.vertices[i * 3 + 2]));
                foreach (var t in p.triangles) allT.Add(baseIdx + t);
            }
            if (allV.Count == 0) return null;
            var m = new Mesh
            {
                name = label + "_BackendColliderParts",
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
                hideFlags = HideFlags.DontSave,
            };
            m.SetVertices(allV);
            m.SetTriangles(allT, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        // ─── 메뉴: 모든 Splat 백엔드 콜라이더 일괄 생성 ───────────────────
        [MenuItem("Tools/Lcc Drop Forge/Collider · BACKEND · regenerate via api mesh-collider for all Splat (current scene)")]
        public static void Menu_BackendBakeAllSplats()
        {
            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            var splats = new List<GameObject>();
            void CollectSplat(GameObject go)
            {
                if (go == null || !go.name.StartsWith("Splat_") || go.name.StartsWith("Splat_ArasP_")) return;
                if (!splats.Contains(go)) splats.Add(go);
            }
            foreach (var go in sm.GetRootGameObjects()) CollectSplat(go);
            var grp = System.Array.Find(sm.GetRootGameObjects(), r => r.name == "LccGroup");
            if (grp != null) foreach (Transform t in grp.transform) CollectSplat(t.gameObject);
            if (splats.Count == 0) { Debug.LogError("[Backend All] Splat_ 객체 없음"); return; }

            Debug.Log($"[Backend All] {splats.Count}개 Splat — 백엔드 호출 시작 (각 LCC 마다 upload + Poisson 처리, 수십초~수분)");

            int doneCount = 0, okCount = 0, failCount = 0;
            foreach (var s in splats)
            {
                string lccName = s.name.Substring("Splat_".Length);
                var lcc = _FindLccSceneByName(lccName);
                if (lcc == null) { Debug.LogWarning($"[Backend All] {s.name}: LccScene 없음 — skip"); doneCount++; failCount++; continue; }
                string lccAssetPath = AssetDatabase.GetAssetPath(lcc);
                if (string.IsNullOrEmpty(lccAssetPath)) { doneCount++; failCount++; continue; }

                var splatRef = s;   // closure capture
                GenerateAsync(lccAssetPath, (mesh, err) =>
                {
                    doneCount++;
                    if (mesh == null) { Debug.LogError($"[Backend All] {splatRef.name}: {err}"); failCount++; }
                    else
                    {
                        _ApplyColliderToSplat(splatRef, mesh);
                        okCount++;
                        Debug.Log($"[Backend All] ✓ {splatRef.name} ← {mesh.vertexCount:N0} verts (백엔드)");
                    }
                    if (doneCount == splats.Count)
                    {
                        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(sm);
                        Debug.Log($"[Backend All] 완료 — ok={okCount}, fail={failCount}");
                    }
                });
            }
        }

        // ─── 헬퍼 ──────────────────────────────────────────────────────────
        static LccScene _FindLccSceneByName(string name)
        {
            var guids = AssetDatabase.FindAssets($"{name} t:LccScene");
            foreach (var g in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                var s = AssetDatabase.LoadAssetAtPath<LccScene>(p);
                if (s != null && s.name == name) return s;
            }
            return null;
        }

        static void _ApplyColliderToSplat(GameObject splat, Mesh mesh)
        {
            var colTr = splat.transform.Find("__LccCollider");
            GameObject colGO;
            if (colTr == null)
            {
                colGO = new GameObject("__LccCollider");
                colGO.transform.SetParent(splat.transform, false);
                Undo.RegisterCreatedObjectUndo(colGO, "Backend collider");
                colTr = colGO.transform;
            }
            else colGO = colTr.gameObject;

            // _ArasP 와 동일한 world identity (Z-up→Y-up 변환은 백엔드가 처리한다고 가정)
            Undo.RecordObject(colTr, "Backend collider transform");
            colTr.localPosition = Vector3.zero;
            colTr.localRotation = Quaternion.Inverse(splat.transform.rotation);
            colTr.localScale = Vector3.one;

            var mc = colGO.GetComponent<MeshCollider>();
            if (mc == null) mc = colGO.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.convex = false;
            mc.enabled = true;
            EditorUtility.SetDirty(colGO);
            EditorUtility.SetDirty(mc);
        }
    }
}
