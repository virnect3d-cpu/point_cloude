using System.Collections.Generic;
using System.IO;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;
using Virnect.Lcc;
using Virnect.Lcc.Editor;

namespace LccDropForge
{
    public class LccDropAutoImporter : AssetPostprocessor
    {
        public const string WatchedFolder = "Assets/LCC_Drops";
        public const int DefaultLodLevel = 0;

        // EditorPrefs key — 새 LCC import 시 자동으로 활성 씬에 Splat 객체 + 콜라이더 spawn할지
        const string kAutoSpawnPref = "LccDropForge.AutoSpawnOnImport";
        public static bool AutoSpawnOnImport
        {
            get => EditorPrefs.GetBool(kAutoSpawnPref, true);
            set => EditorPrefs.SetBool(kAutoSpawnPref, value);
        }

        [MenuItem("Tools/Lcc Drop Forge/Settings · Toggle auto-spawn on .lcc import")]
        static void ToggleAutoSpawn()
        {
            AutoSpawnOnImport = !AutoSpawnOnImport;
            Debug.Log($"[LccDropForge] AutoSpawnOnImport = {AutoSpawnOnImport}");
        }

        static readonly Queue<string> s_Pending = new();
        static bool s_Scheduled;

        static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            foreach (string path in importedAssets)
                TryEnqueue(path);
            foreach (string path in movedAssets)
                TryEnqueue(path);

            if (!s_Scheduled && s_Pending.Count > 0)
            {
                s_Scheduled = true;
                EditorApplication.delayCall += ProcessQueue;
            }
        }

        static void TryEnqueue(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return;
            string normalized = assetPath.Replace('\\', '/');
            if (!normalized.StartsWith(WatchedFolder + "/", System.StringComparison.OrdinalIgnoreCase)) return;
            if (!normalized.EndsWith(".lcc", System.StringComparison.OrdinalIgnoreCase)) return;
            if (!s_Pending.Contains(normalized))
                s_Pending.Enqueue(normalized);
        }

        static void ProcessQueue()
        {
            s_Scheduled = false;
            while (s_Pending.Count > 0)
            {
                string lccAssetPath = s_Pending.Dequeue();
                ProcessOne(lccAssetPath);
            }
        }

        [MenuItem("Tools/Lcc Drop Forge/Reimport Selected .lcc")]
        static void ReimportSelected()
        {
            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(path) && path.EndsWith(".lcc", System.StringComparison.OrdinalIgnoreCase))
                    ProcessOne(path);
            }
        }

        [MenuItem("Tools/Lcc Drop Forge/Process All LCC in LCC_Drops")]
        static void ProcessAllInDropFolder()
        {
            string absFolder = Path.GetFullPath(WatchedFolder);
            if (!Directory.Exists(absFolder))
            {
                Debug.LogWarning($"[LccDropForge] {WatchedFolder} not found.");
                return;
            }
            foreach (var file in Directory.GetFiles(absFolder, "*.lcc", SearchOption.AllDirectories))
            {
                string rel = "Assets" + file.Substring(Application.dataPath.Length).Replace('\\', '/');
                ProcessOne(rel);
            }
        }

        static void ProcessOne(string lccAssetPath)
        {
            string baseName = Path.GetFileNameWithoutExtension(lccAssetPath);
            string existingAssetPath = $"{GaussianAssetBuilder.DefaultOutputFolder}/{baseName}.asset";
            if (AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(existingAssetPath) != null)
            {
                Debug.Log($"[LccDropForge] Skipping {lccAssetPath}: Gaussian asset already exists at {existingAssetPath}. Delete it to reimport, or use Tools/Lcc Drop Forge/Reimport Selected.");
                return;
            }

            string lccAbs = Path.GetFullPath(lccAssetPath);
            string lccDir = Path.GetDirectoryName(lccAbs);
            string[] required = { "data.bin", "index.bin" };
            foreach (var req in required)
            {
                if (!File.Exists(Path.Combine(lccDir, req)))
                {
                    Debug.LogError($"[LccDropForge] {lccAssetPath} is missing sibling '{req}'. Drop the WHOLE lcc-result folder (containing .lcc + data.bin + index.bin + ...) into {WatchedFolder}, not just the .lcc header file.");
                    return;
                }
            }

            Debug.Log($"[LccDropForge] Converting {lccAssetPath} (LOD {DefaultLodLevel})...");

            if (!LccConverter.TryConvertToPly(lccAssetPath, DefaultLodLevel, out string plyPath, out string convertError))
            {
                Debug.LogError($"[LccDropForge] Convert failed for {lccAssetPath}:\n{convertError}");
                return;
            }

            var asset = GaussianAssetBuilder.BuildFromPly(plyPath, baseName, out string buildError);
            if (asset == null)
            {
                Debug.LogError($"[LccDropForge] GaussianSplatAsset creation failed for {lccAssetPath}:\n{buildError}");
                return;
            }

            try { File.Delete(plyPath); } catch { }

            // Virnect 런처 경로: Aras-P GS 오브젝트는 더 이상 자동 스폰 안 함.
            // LccScene 에셋 (scripted importer 가 만들어둔) 을 찾아 Playground 창을 띄워 사용자가 즉시 Launch.
            var lccScene = AssetDatabase.LoadAssetAtPath<LccScene>(lccAssetPath);

            // 새 hook: AutoSpawnOnImport 가 켜져 있으면 활성 씬에 Splat_<name> + _ArasP child + 콜라이더 자동 스폰
            if (AutoSpawnOnImport && lccScene != null)
            {
                EditorApplication.delayCall += () => _AutoSpawnInActiveScene(lccScene, asset);
            }

            if (lccScene != null)
            {
                EditorApplication.delayCall += () => LccPlaygroundWindow.Open(lccScene);
                Debug.Log($"[LccDropForge] Done. Gaussian asset '{asset.name}' built · {(AutoSpawnOnImport ? "활성 씬 자동 spawn 예정 + " : "")}Playground 창 열어 Launch 하세요.");
            }
            else
            {
                Debug.LogWarning($"[LccDropForge] LccScene at {lccAssetPath} 이 로드되지 않았습니다. 수동으로 .lcc 클릭 후 Launch Playground 눌러주세요.");
            }
        }

        // 새 .lcc import 후 활성 씬에 Aras-P 객체 자동 생성. 이미 같은 이름 GameObject 있으면 skip.
        static void _AutoSpawnInActiveScene(LccScene lccScene, GaussianSplatAsset asset)
        {
            if (lccScene == null || asset == null) return;
            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            if (!sm.IsValid()) { Debug.LogWarning("[LccDropForge] active scene 없음 — auto-spawn skip"); return; }

            string spawnName = $"Splat_{lccScene.name}";
            foreach (var existing in sm.GetRootGameObjects())
                if (existing != null && existing.name == spawnName)
                { Debug.Log($"[LccDropForge] '{spawnName}' 이미 존재 — auto-spawn skip"); return; }

            // 1) 부모 Splat_<name>: Z-up→Y-up 회전 적용 (Virnect 스킴 호환)
            var splatGO = new GameObject(spawnName);
            splatGO.transform.position = Vector3.zero;
            splatGO.transform.rotation = Quaternion.Euler(-180f, 0f, 0f);
            Undo.RegisterCreatedObjectUndo(splatGO, "Auto-spawn LCC Splat root");

            // 2) __LccCollider 자식 (proxy mesh PLY 사용)
            try
            {
                string plyPath = lccScene.ResolveProxyMeshPlyAssetPath();
                if (!string.IsNullOrEmpty(plyPath) && File.Exists(Path.GetFullPath(plyPath)))
                {
                    var mesh = Virnect.Lcc.LccMeshPlyLoader.Load(Path.GetFullPath(plyPath));
                    if (mesh != null)
                    {
                        var colGO = new GameObject("__LccCollider");
                        colGO.transform.SetParent(splatGO.transform, false);
                        var mc = colGO.AddComponent<MeshCollider>();
                        mc.sharedMesh = mesh;
                        mc.convex = false;
                        Debug.Log($"[LccDropForge] {spawnName}: __LccCollider ({mesh.vertexCount:N0} verts)");
                    }
                }
            }
            catch (System.Exception e) { Debug.LogWarning($"[LccDropForge] {spawnName} collider 추가 실패: {e.Message}"); }

            // 3) _ArasP 자식 (world transform identity → splat 정상 정렬) + GaussianSplatRenderer
            var arasGO = new GameObject("_ArasP");
            arasGO.transform.SetParent(splatGO.transform, false);
            arasGO.transform.localPosition = Vector3.zero;
            arasGO.transform.localRotation = Quaternion.Inverse(splatGO.transform.rotation);
            arasGO.transform.localScale = Vector3.one;
            var gsr = arasGO.AddComponent<GaussianSplatRenderer>();
            gsr.enabled = false;
            // GaussianSplatRenderer.m_Asset (private) → reflection
            var assetField = typeof(GaussianSplatRenderer).GetField("m_Asset",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (assetField != null) assetField.SetValue(gsr, asset);
            gsr.enabled = true;

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(sm);
            Selection.activeGameObject = splatGO;
            EditorGUIUtility.PingObject(splatGO);
            Debug.Log($"[LccDropForge] ✓ Auto-spawned {spawnName} (+_ArasP child + __LccCollider) in active scene · Hierarchy에서 선택 표시됨");
        }
    }
}
