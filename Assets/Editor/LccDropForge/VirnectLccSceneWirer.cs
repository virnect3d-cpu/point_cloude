using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Virnect.Lcc;
using Virnect.Lcc.Editor;

namespace LccDropForge
{
    internal static class VirnectLccSceneWirer
    {
        [MenuItem("Tools/Lcc Drop Forge/Dev · Auto-Launch Playground (ShinWon · LOD0 + Collider + Character)")]
        static void DevAutoLaunchShinWon()
        {
            const string path = "Assets/LCC_Drops/ShinWon_1st_Cutter/ShinWon_1st_Cutter.lcc";
            var scene = AssetDatabase.LoadAssetAtPath<LccScene>(path);
            if (scene == null) { Debug.LogError($"[DevLauncher] LccScene not found at {path}"); return; }
            var go = LccPlaygroundWindow.QuickLaunch(scene, lodLevel: 0, addCollider: true, spawnCharacter: true, frameCamera: true, cleanLaunch: true, heightOffset: 1f);
            if (go != null) Selection.activeGameObject = go;
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · Auto-Launch Playground (ShinWon · Colored Mesh · Splat Hidden)")]
        static void DevAutoLaunchShinWonColored()
        {
            const string path = "Assets/LCC_Drops/ShinWon_1st_Cutter/ShinWon_1st_Cutter.lcc";
            var scene = AssetDatabase.LoadAssetAtPath<LccScene>(path);
            if (scene == null) { Debug.LogError($"[DevLauncher] LccScene not found at {path}"); return; }
            var go = LccPlaygroundWindow.QuickLaunch(
                scene,
                lodLevel: 0,
                addCollider: true,
                spawnCharacter: true,
                frameCamera: true,
                cleanLaunch: true,
                heightOffset: 1f,
                colorizeMesh: true,
                colorSourceLod: 3,
                colorizerCellSize: 1.0f,
                colorizerK: 6,
                hideSplatAfterColorize: true);
            if (go != null) Selection.activeGameObject = go;
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · Build Colored Mesh Asset (ShinWon, PhotoReal)")]
        static void DevBuildColoredMeshAsset()
        {
            const string path = "Assets/LCC_Drops/ShinWon_1st_Cutter/ShinWon_1st_Cutter.lcc";
            var scene = AssetDatabase.LoadAssetAtPath<LccScene>(path);
            if (scene == null) { Debug.LogError($"[DevBuild] LccScene not found at {path}"); return; }

            // 1) proxy PLY → Mesh.asset
            string plyAssetPath = scene.ResolveProxyMeshPlyAssetPath();
            if (string.IsNullOrEmpty(plyAssetPath))
            { Debug.LogError("[DevBuild] proxy PLY 찾지 못함 (Assets/LCC_Drops/.../*.ply 확인)"); return; }

            string outDir = "Assets/LCC_Generated";
            System.IO.Directory.CreateDirectory(outDir);

            var proxyMesh = Virnect.Lcc.LccMeshPlyLoader.Load(System.IO.Path.GetFullPath(plyAssetPath));
            proxyMesh.name = scene.name + "_ProxyMesh";
            string proxyPath = $"{outDir}/{proxyMesh.name}.asset";
            AssetDatabase.CreateAsset(proxyMesh, proxyPath);
            Debug.Log($"[DevBuild] ProxyMesh saved · {proxyMesh.vertexCount:N0} verts · {proxyPath}");

            // 2) Colored clone
            var vizMesh = new UnityEngine.Mesh { name = proxyMesh.name + "_Colored", indexFormat = proxyMesh.indexFormat };
            vizMesh.SetVertices(proxyMesh.vertices);
            vizMesh.SetTriangles(proxyMesh.triangles, 0, calculateBounds: true);

            var opts = Virnect.Lcc.LccMeshColorizer.Options.PhotoReal;
            double t0 = UnityEditor.EditorApplication.timeSinceStartup;
            var splats = Virnect.Lcc.LccSplatDecoder.DecodeLod(scene, 0);
            double t1 = UnityEditor.EditorApplication.timeSinceStartup;
            Virnect.Lcc.LccMeshColorizer.Colorize(vizMesh, splats, opts);
            double t2 = UnityEditor.EditorApplication.timeSinceStartup;

            string colPath = $"{outDir}/{proxyMesh.name}_Colored.asset";
            AssetDatabase.CreateAsset(vizMesh, colPath);

            var shader = Shader.Find("Virnect/LccVertexColorUnlit") ?? Shader.Find("Universal Render Pipeline/Unlit");
            var mat = new Material(shader) { name = proxyMesh.name + "_ColoredMat" };
            string matPath = colPath.Replace(".asset", "_Mat.mat");
            AssetDatabase.CreateAsset(mat, matPath);
            AssetDatabase.SaveAssets(); AssetDatabase.Refresh();

            Debug.Log($"[DevBuild] ColoredMesh · {vizMesh.vertexCount:N0} verts · decode {(t1-t0)*1000:F0}ms · colorize {(t2-t1)*1000:F0}ms");
            Debug.Log($"[DevBuild] Saved: {colPath} + {matPath}");
            Selection.activeObject = vizMesh;
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · Aras-P · Swap Scene2 Splats (Virnect OFF → Aras-P child ON)")]
        static void DevSwapScene2ToAras()
        {
            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            var roots = sm.GetRootGameObjects();

            var pairs = new (string goName, string assetPath)[] {
                ("Splat_ShinWon_Facility_01",     "Assets/GaussianAssets/Facility_01.asset"),
                ("Splat_ShinWon_Facility_Middle", "Assets/GaussianAssets/Facility_Middle.asset"),
                ("Splat_ShinWon_1st_Cutter",      "Assets/GaussianAssets/ShinWon_1st_Cutter_lod0.asset"),
            };

            System.Type lccRendererType = null, gsRendererType = null;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                if (lccRendererType == null) lccRendererType = asm.GetType("Virnect.Lcc.LccSplatRenderer");
                if (gsRendererType  == null) gsRendererType  = asm.GetType("GaussianSplatting.Runtime.GaussianSplatRenderer");
                if (lccRendererType != null && gsRendererType != null) break;
            }
            if (gsRendererType == null) { Debug.LogError("[Swap] GaussianSplatRenderer 타입 못 찾음"); return; }

            var bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            int swapped = 0;
            foreach (var (goName, assetPath) in pairs)
            {
                var go = System.Array.Find(roots, r => r.name == goName);
                if (go == null) { Debug.LogWarning($"[Swap] {goName} 없음 — skip"); continue; }
                var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (asset == null) { Debug.LogError($"[Swap] {assetPath} 없음 — skip"); continue; }

                // 1) LccSplatRenderer + MeshRenderer disable (GO 자체는 active — 자식 __LccCollider 살리기)
                if (lccRendererType != null)
                {
                    var lcc = go.GetComponent(lccRendererType) as MonoBehaviour;
                    if (lcc != null) lcc.enabled = false;
                }
                var mr = go.GetComponent<MeshRenderer>();
                if (mr != null) mr.enabled = false;

                // 2) child "_ArasP" 만들기 (또는 기존 재사용) — world transform identity 로
                var arasTr = go.transform.Find("_ArasP");
                GameObject arasGO;
                if (arasTr == null)
                {
                    arasGO = new GameObject("_ArasP");
                    Undo.RegisterCreatedObjectUndo(arasGO, "Add Aras-P child");
                    arasGO.transform.SetParent(go.transform, false);
                }
                else arasGO = arasTr.gameObject;

                // 부모 회전 상쇄 → world identity (Aras-P PLY는 이미 Y-up)
                arasGO.transform.localPosition = Vector3.zero;
                arasGO.transform.localRotation = Quaternion.Inverse(go.transform.rotation);
                arasGO.transform.localScale    = Vector3.one;

                // 3) GaussianSplatRenderer + asset
                var gsr = arasGO.GetComponent(gsRendererType) as MonoBehaviour;
                if (gsr == null) gsr = arasGO.AddComponent(gsRendererType) as MonoBehaviour;
                gsr.enabled = false;
                var assetField = gsRendererType.GetField("m_Asset", bf) ?? gsRendererType.GetField("asset", bf);
                if (assetField != null) assetField.SetValue(gsr, asset);
                gsr.enabled = true;

                EditorUtility.SetDirty(go);
                EditorUtility.SetDirty(arasGO);
                swapped++;
                Debug.Log($"[Swap] {goName}: Virnect OFF · child '_ArasP' (Aras-P) → {asset.name}");
            }

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(sm);
            Debug.Log($"[Swap] {swapped}/{pairs.Length} 교체 완료. 시각 확인 후 Ctrl+S로 씬 저장");
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · NEW LCC pipeline · Facility_02 + 03 (refresh → build → spawn → ICP)")]
        static void DevNewLccPipeline_02_03()
        {
            AssetDatabase.Refresh();

            string projectRoot = System.IO.Path.GetDirectoryName(Application.dataPath);
            var newCases = new (string lccAssetPath, string plyRelPath, string assetPath, string spawnGoName, Vector3 initialPos)[] {
                ("Assets/LCC_Drops/Shinwon_Facility_02/ShinWon_Facility_02.lcc",
                 System.IO.Path.Combine(projectRoot, "Library", "lcc_to_ply", "Facility_02.ply"),
                 "Assets/GaussianAssets/Facility_02.asset",
                 "Splat_ShinWon_Facility_02",
                 new Vector3(200f, 0f, 0f)),
                ("Assets/LCC_Drops/Shinwon_Facility_03/ShinWon_Facility_03.lcc",
                 System.IO.Path.Combine(projectRoot, "Library", "lcc_to_ply", "Facility_03.ply"),
                 "Assets/GaussianAssets/Facility_03.asset",
                 "Splat_ShinWon_Facility_03",
                 new Vector3(300f, 0f, 0f)),
            };

            // 1) Aras-P asset 빌드 (없으면)
            System.Type creatorType = null;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            { creatorType = asm.GetType("GaussianSplatting.Editor.GaussianSplatAssetCreator"); if (creatorType != null) break; }
            if (creatorType == null) { Debug.LogError("[NEW] AssetCreator 타입 못 찾음"); return; }
            var qualityEnum = creatorType.GetNestedType("DataQuality", System.Reflection.BindingFlags.NonPublic);
            var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var qHigh = System.Enum.Parse(qualityEnum, "High");

            foreach (var c in newCases)
            {
                if (AssetDatabase.LoadMainAssetAtPath(c.assetPath) != null)
                { Debug.Log($"[NEW] asset 이미 있음 → skip build: {c.assetPath}"); continue; }
                if (!System.IO.File.Exists(c.plyRelPath)) { Debug.LogError($"[NEW] PLY 없음: {c.plyRelPath}"); continue; }

                var creator = ScriptableObject.CreateInstance(creatorType);
                try
                {
                    creatorType.GetField("m_InputFile", bf).SetValue(creator, c.plyRelPath);
                    creatorType.GetField("m_OutputFolder", bf).SetValue(creator, "Assets/GaussianAssets");
                    creatorType.GetField("m_Quality", bf).SetValue(creator, qHigh);
                    creatorType.GetField("m_ImportCameras", bf).SetValue(creator, false);
                    creatorType.GetMethod("ApplyQualityLevel", bf).Invoke(creator, null);
                    Debug.Log($"[NEW] CreateAsset → {System.IO.Path.GetFileName(c.plyRelPath)}");
                    creatorType.GetMethod("CreateAsset", bf).Invoke(creator, null);
                }
                catch (System.Exception e) { Debug.LogError($"[NEW] CreateAsset 실패: {e.InnerException?.Message ?? e.Message}"); }
                finally { Object.DestroyImmediate(creator); }
            }
            AssetDatabase.Refresh();

            // 2) Scene2 (현재 활성 씬) 에 Splat GameObject spawn (없으면)
            System.Type gsRendererType = null;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            { gsRendererType = asm.GetType("GaussianSplatting.Runtime.GaussianSplatRenderer"); if (gsRendererType != null) break; }
            if (gsRendererType == null) { Debug.LogError("[NEW] GaussianSplatRenderer 타입 못 찾음"); return; }

            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            foreach (var c in newCases)
            {
                var roots = sm.GetRootGameObjects();
                var existing = System.Array.Find(roots, r => r.name == c.spawnGoName);
                GameObject splatGO;
                if (existing != null) splatGO = existing;
                else
                {
                    splatGO = new GameObject(c.spawnGoName);
                    splatGO.transform.position = c.initialPos;
                    splatGO.transform.rotation = Quaternion.Euler(-180f, 0f, 0f);  // 다른 splat과 동일 패턴
                    Undo.RegisterCreatedObjectUndo(splatGO, "Spawn new Splat root");
                }
                var asset = AssetDatabase.LoadMainAssetAtPath(c.assetPath);
                if (asset == null) { Debug.LogError($"[NEW] asset 없음: {c.assetPath}"); continue; }

                // _ArasP child + GaussianSplatRenderer
                var arasTr = splatGO.transform.Find("_ArasP");
                GameObject arasGO = arasTr != null ? arasTr.gameObject : new GameObject("_ArasP");
                if (arasTr == null)
                {
                    arasGO.transform.SetParent(splatGO.transform, false);
                    Undo.RegisterCreatedObjectUndo(arasGO, "Add _ArasP child");
                }
                arasGO.transform.localPosition = Vector3.zero;
                arasGO.transform.localRotation = Quaternion.Inverse(splatGO.transform.rotation);
                arasGO.transform.localScale = Vector3.one;

                var gsr = arasGO.GetComponent(gsRendererType) as MonoBehaviour;
                if (gsr == null) gsr = arasGO.AddComponent(gsRendererType) as MonoBehaviour;
                gsr.enabled = false;
                var assetField = gsRendererType.GetField("m_Asset", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                 ?? gsRendererType.GetField("asset", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (assetField != null) assetField.SetValue(gsr, asset);
                gsr.enabled = true;
                Debug.Log($"[NEW] Spawned/Updated {c.spawnGoName} @ {c.initialPos} · asset={asset.name}");
            }

            // 3) ICP BRUTE: Facility_01 기준으로 02 / 03 정합
            var baseLcc = AssetDatabase.LoadMainAssetAtPath("Assets/LCC_Drops/ShinWon_Facility_01/ShinWon_Facility_01.lcc") as LccScene;
            if (baseLcc == null) { Debug.LogError("[NEW] base Facility_01 LccScene 없음"); return; }
            foreach (var c in newCases)
            {
                var t = AssetDatabase.LoadMainAssetAtPath(c.lccAssetPath) as LccScene;
                if (t == null) { Debug.LogError($"[NEW] LccScene 없음: {c.lccAssetPath}"); continue; }
                Debug.Log($"[NEW] ICP BRUTE Facility_01 ← {t.name}");
                _RunIcpBrute(baseLcc, t);

                // 4) ICP 후 부모 rotation이 바뀌었으니 _ArasP child의 localRotation 재계산 (world identity 유지)
                var roots = sm.GetRootGameObjects();
                var splatGO = System.Array.Find(roots, r => r.name == c.spawnGoName);
                if (splatGO != null)
                {
                    var arasTr = splatGO.transform.Find("_ArasP");
                    if (arasTr != null)
                    {
                        arasTr.localRotation = Quaternion.Inverse(splatGO.transform.rotation);
                        Debug.Log($"[NEW] {c.spawnGoName}/_ArasP localRotation 재계산");
                    }
                }
            }

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(sm);
            Debug.Log("[NEW] Pipeline 완료. 씬 저장 권장 + Scene View 확인");
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · Aras-P · Build GaussianSplatAssets (Quality=High) for Facility_01 + Middle")]
        static void DevBuildArasAssetsHigh()
        {
            string projectRoot = System.IO.Path.GetDirectoryName(Application.dataPath);
            string[] plies = {
                System.IO.Path.Combine(projectRoot, "Library", "lcc_to_ply", "Facility_01.ply"),
                System.IO.Path.Combine(projectRoot, "Library", "lcc_to_ply", "Facility_Middle.ply"),
            };

            // 사전 검증
            foreach (var p in plies)
                if (!System.IO.File.Exists(p)) { Debug.LogError($"[Aras-Build] PLY 없음: {p}"); return; }

            // GaussianSplatAssetCreator 타입 + DataQuality enum 찾기
            System.Type creatorType = null;
            System.Type qualityEnum = null;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                if (creatorType == null) creatorType = asm.GetType("GaussianSplatting.Editor.GaussianSplatAssetCreator");
                if (creatorType != null) break;
            }
            if (creatorType == null) { Debug.LogError("[Aras-Build] GaussianSplatAssetCreator 타입 못 찾음"); return; }
            qualityEnum = creatorType.GetNestedType("DataQuality", System.Reflection.BindingFlags.NonPublic);
            if (qualityEnum == null) { Debug.LogError("[Aras-Build] DataQuality enum 못 찾음"); return; }

            var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var qHigh = System.Enum.Parse(qualityEnum, "High");

            foreach (var plyPath in plies)
            {
                var creator = ScriptableObject.CreateInstance(creatorType);
                try
                {
                    creatorType.GetField("m_InputFile",    bf).SetValue(creator, plyPath);
                    creatorType.GetField("m_OutputFolder", bf).SetValue(creator, "Assets/GaussianAssets");
                    creatorType.GetField("m_Quality",      bf).SetValue(creator, qHigh);
                    creatorType.GetField("m_ImportCameras", bf).SetValue(creator, false);
                    creatorType.GetMethod("ApplyQualityLevel", bf).Invoke(creator, null);

                    var t0 = Time.realtimeSinceStartup;
                    Debug.Log($"[Aras-Build] CreateAsset 시작 → {System.IO.Path.GetFileName(plyPath)} (Quality=High)");
                    creatorType.GetMethod("CreateAsset", bf).Invoke(creator, null);
                    Debug.Log($"[Aras-Build] 완료 ({Time.realtimeSinceStartup - t0:F1}s) → Assets/GaussianAssets/");
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[Aras-Build] {plyPath} 실패: {e.InnerException?.Message ?? e.Message}");
                }
                finally
                {
                    Object.DestroyImmediate(creator);
                }
            }

            AssetDatabase.Refresh();
            Debug.Log("[Aras-Build] All done. 결과: Assets/GaussianAssets/Facility_01.asset, Facility_Middle.asset");
        }

        // ═════════════════════════════════════════════════════════════════
        //  Lighten / Collider / Generalized Aras-P swap
        // ═════════════════════════════════════════════════════════════════

        [MenuItem("Tools/Lcc Drop Forge/Scene · Build Scene5_LccRotate (click-to-select + BoxCollider · no auto-ICP)")]
        static void DevBuildScene5LccRotate()
        {
            const string scenePath = "Assets/Scenes/Scene5_LccRotate.unity";

            var currentScene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            if (currentScene.isDirty &&
                !UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            { Debug.LogWarning("[Scene5Rotate] 현재 씬 저장 취소 — 빌드 중단"); return; }

            // 1) LccScene paths 캐싱 (NewScene 후 무효화 방지)
            var lccGuids = AssetDatabase.FindAssets("t:LccScene");
            var lccPaths = new System.Collections.Generic.List<string>();
            foreach (var g in lccGuids)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (!p.StartsWith("Assets/LCC_Drops/")) continue;
                lccPaths.Add(p);
            }
            if (lccPaths.Count == 0) { Debug.LogError("[Scene5Rotate] LccScene 없음"); return; }

            // 2) 새 씬
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.DefaultGameObjects,
                UnityEditor.SceneManagement.NewSceneMode.Single);

            // 3) 타입 + helper
            System.Type gsRendererType = null;
            System.Type gsAssetType = null;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                if (gsRendererType == null) gsRendererType = asm.GetType("GaussianSplatting.Runtime.GaussianSplatRenderer");
                if (gsAssetType == null)    gsAssetType    = asm.GetType("GaussianSplatting.Runtime.GaussianSplatAsset");
                if (gsRendererType != null && gsAssetType != null) break;
            }
            if (gsRendererType == null || gsAssetType == null) { Debug.LogError("[Scene5Rotate] Aras-P 타입 없음"); return; }
            var bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

            // 4) 각 LCC spawn (X축 200m 간격으로 spread — 회전 자유롭게)
            int idx = 0;
            int spawned = 0;
            foreach (var lccPath in lccPaths)
            {
                var lcc = AssetDatabase.LoadAssetAtPath<LccScene>(lccPath);
                if (lcc == null) continue;

                var arasAssetPath = _ResolveArasAssetPath(lcc.name);
                if (arasAssetPath == null) { Debug.LogWarning($"[Scene5Rotate] {lcc.name}: Aras-P asset 없음 — skip"); idx++; continue; }
                var asset = AssetDatabase.LoadMainAssetAtPath(arasAssetPath);
                if (asset == null) { idx++; continue; }

                // 부모 Splat_<name>: 회전 (270, 0, 0) — Z-up→Y-up
                var splatGO = new GameObject($"Splat_{lcc.name}");
                splatGO.transform.position = new Vector3(idx * 200f, 0f, 0f);
                splatGO.transform.rotation = Quaternion.Euler(270f, 0f, 0f);

                // SelectionBase: 자식 (_ArasP) 클릭 시 부모 Splat_<name> 자동 선택
                var selBaseType = System.Type.GetType("LccSelectionBase, Assembly-CSharp");
                if (selBaseType != null) splatGO.AddComponent(selBaseType);
                else Debug.LogWarning("[Scene5Rotate] LccSelectionBase 타입 없음 — 자식 클릭 시 부모 자동 선택 안 됨");

                // Play 모드 마우스 회전 컴포넌트
                var rotType = System.Type.GetType("LccInteractiveRotator, Assembly-CSharp");
                if (rotType != null) splatGO.AddComponent(rotType);

                // _ArasP child: world identity (splat 정상 정렬)
                var arasGO = new GameObject("_ArasP");
                arasGO.transform.SetParent(splatGO.transform, false);
                arasGO.transform.localPosition = Vector3.zero;
                arasGO.transform.localRotation = Quaternion.Inverse(splatGO.transform.rotation);
                arasGO.transform.localScale = Vector3.one;
                var gsr = (MonoBehaviour)arasGO.AddComponent(gsRendererType);
                gsr.enabled = false;
                var assetField = gsRendererType.GetField("m_Asset", bf) ?? gsRendererType.GetField("asset", bf);
                if (assetField != null) assetField.SetValue(gsr, asset);
                gsr.enabled = true;

                // BoxCollider — Aras-P asset bounds 기반. _ArasP local space (world identity).
                // → splat 형상에 정확히 fit. proxy mesh decimation 문제 없음.
                var boundsMinField = gsAssetType.GetField("m_BoundsMin", bf);
                var boundsMaxField = gsAssetType.GetField("m_BoundsMax", bf);
                if (boundsMinField != null && boundsMaxField != null)
                {
                    Vector3 bMin = (Vector3)boundsMinField.GetValue(asset);
                    Vector3 bMax = (Vector3)boundsMaxField.GetValue(asset);
                    var box = arasGO.AddComponent<BoxCollider>();
                    box.center = (bMin + bMax) * 0.5f;
                    box.size = bMax - bMin;
                    Debug.Log($"[Scene5Rotate] {splatGO.name}: BoxCollider center={box.center} size={box.size}");
                }
                else Debug.LogWarning($"[Scene5Rotate] {lcc.name}: asset bounds 필드 못 찾음 — BoxCollider 안 만듦");

                idx++;
                spawned++;
            }

            // 5) Camera 두기 — 첫 객체 정면
            var camGO = GameObject.Find("Main Camera");
            if (camGO != null && spawned > 0)
            {
                camGO.transform.position = new Vector3(0f, 30f, -120f);
                camGO.transform.rotation = Quaternion.Euler(15f, 0f, 0f);
                var cam = camGO.GetComponent<Camera>();
                if (cam != null) { cam.fieldOfView = 60f; cam.farClipPlane = 5000f;
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0.12f, 0.13f, 0.16f); }
            }
            var lightGO = GameObject.Find("Directional Light");
            if (lightGO != null) lightGO.transform.rotation = Quaternion.Euler(45f, -30f, 0f);

            // 6) 첫 Splat 자동 선택 + SceneView Frame Selected
            GameObject firstSplat = null;
            foreach (var g in scene.GetRootGameObjects()) { if (g.name.StartsWith("Splat_")) { firstSplat = g; break; } }
            if (firstSplat != null)
            {
                Selection.activeGameObject = firstSplat;
                EditorApplication.delayCall += () =>
                {
                    var sv = SceneView.lastActiveSceneView;
                    if (sv != null) { sv.FrameSelected(); sv.Repaint(); }
                };
            }

            // 7) 저장
            System.IO.Directory.CreateDirectory("Assets/Scenes");
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, scenePath);
            Debug.Log($"[Scene5Rotate] 빌드 완료 → {scenePath} · {spawned}개 LCC, X축 200m 간격");
            Debug.Log("[Scene5Rotate] 사용법:");
            Debug.Log("  · Hierarchy 에서 Splat_<name> 클릭 → Inspector Transform 또는 E (Rotate) gizmo");
            Debug.Log("  · ▶ Play 모드 + 좌클릭 드래그 = Y/X 회전 · 우클릭 드래그 = Z 회전 · Shift = 정밀 · R = 리셋");
            Debug.Log("  · 다음/이전 LCC 선택: Hierarchy 에서 ↑/↓ 또는 다른 Splat_ 클릭");
        }

        [MenuItem("Tools/Lcc Drop Forge/Scene · Build Scene4_LccBrowse (all LCCs, colliders, auto-ICP)")]
        static void DevBuildScene4LccBrowse()
        {
            const string scenePath = "Assets/Scenes/Scene4_LccBrowse.unity";

            // 0) 현재 씬 dirty 확인
            var currentScene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            if (currentScene.isDirty &&
                !UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            { Debug.LogWarning("[Scene4Browse] 현재 씬 저장 취소 — 빌드 중단"); return; }

            // 1) 모든 LccScene asset path 수집 (Assets/LCC_Drops/ 안) — path만 캐싱 (NewScene 후 객체 무효화 방지)
            var lccGuids = AssetDatabase.FindAssets("t:LccScene");
            var lccPaths = new System.Collections.Generic.List<string>();
            var lccNames = new System.Collections.Generic.List<string>();
            foreach (var g in lccGuids)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (!p.StartsWith("Assets/LCC_Drops/")) continue;
                var preview = AssetDatabase.LoadAssetAtPath<LccScene>(p);
                if (preview != null) { lccPaths.Add(p); lccNames.Add(preview.name); }
            }
            if (lccPaths.Count == 0) { Debug.LogError("[Scene4Browse] Assets/LCC_Drops/ 안에 LccScene 없음"); return; }
            int baseIdx = lccNames.IndexOf("ShinWon_Facility_01");
            if (baseIdx < 0) baseIdx = lccNames.IndexOf("ShinWon_Facility_Middle");
            if (baseIdx < 0) baseIdx = 0;
            Debug.Log($"[Scene4Browse] {lccPaths.Count}개 LCC 발견. base = {lccNames[baseIdx]}");

            // 2) 새 씬 (Camera + Light)
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.DefaultGameObjects,
                UnityEditor.SceneManagement.NewSceneMode.Single);

            // 새 씬 진입 후 LccScene asset 재로드
            var lccs = new System.Collections.Generic.List<LccScene>();
            foreach (var p in lccPaths)
            {
                var s = AssetDatabase.LoadAssetAtPath<LccScene>(p);
                if (s != null) lccs.Add(s);
            }
            if (lccs.Count == 0) { Debug.LogError("[Scene4Browse] NewScene 후 LccScene 재로드 실패"); return; }
            LccScene baseLcc = lccs[baseIdx];

            // 3) 타입 reflection
            System.Type gsRendererType = null;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            { gsRendererType = asm.GetType("GaussianSplatting.Runtime.GaussianSplatRenderer"); if (gsRendererType != null) break; }
            if (gsRendererType == null) { Debug.LogError("[Scene4Browse] GaussianSplatRenderer 타입 없음"); return; }
            var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

            // 4) 각 LCC spawn (initialPos: X축 spread 100m 간격)
            int idx = 0;
            foreach (var lcc in lccs)
            {
                var arasAssetPath = _ResolveArasAssetPath(lcc.name);
                if (arasAssetPath == null) { Debug.LogWarning($"[Scene4Browse] {lcc.name}: Aras-P asset 없음 — 'NEW LCC pipeline' 또는 'Build GaussianSplatAssets' 메뉴 먼저 실행 필요. skip."); continue; }
                var asset = AssetDatabase.LoadMainAssetAtPath(arasAssetPath);
                if (asset == null) continue;

                // 부모 Splat_<name>: identity rotation (LCC-only 단순 배치)
                var splatGO = new GameObject($"Splat_{lcc.name}");
                splatGO.transform.position = new Vector3(idx * 100f, 0f, 0f);
                splatGO.transform.rotation = Quaternion.Euler(-180f, 0f, 0f);   // X=-180 (사용자 지정)
                idx++;

                // __LccCollider 자식 (proxy mesh)
                try
                {
                    var plyPath = lcc.ResolveProxyMeshPlyAssetPath();
                    if (!string.IsNullOrEmpty(plyPath))
                    {
                        var mesh = Virnect.Lcc.LccMeshPlyLoader.Load(System.IO.Path.GetFullPath(plyPath));
                        if (mesh != null)
                        {
                            var colGO = new GameObject("__LccCollider");
                            colGO.transform.SetParent(splatGO.transform, false);
                            colGO.AddComponent<MeshCollider>().sharedMesh = mesh;
                        }
                    }
                }
                catch (System.Exception e) { Debug.LogWarning($"[Scene4Browse] {lcc.name} collider 실패: {e.Message}"); }

                // _ArasP 자식 (world identity 위해 inverse rotation) + GaussianSplatRenderer
                var arasGO = new GameObject("_ArasP");
                arasGO.transform.SetParent(splatGO.transform, false);
                arasGO.transform.localPosition = Vector3.zero;
                arasGO.transform.localRotation = Quaternion.Inverse(splatGO.transform.rotation);
                arasGO.transform.localScale = Vector3.one;
                var gsr = (MonoBehaviour)arasGO.AddComponent(gsRendererType);
                gsr.enabled = false;
                var assetField = gsRendererType.GetField("m_Asset", bf) ?? gsRendererType.GetField("asset", bf);
                if (assetField != null) assetField.SetValue(gsr, asset);
                gsr.enabled = true;

                Debug.Log($"[Scene4Browse] spawned Splat_{lcc.name} @ x={(idx-1)*100f}");
            }

            // 5) ICP BRUTE: base 외 모든 LCC를 base 좌표계로 정합
            foreach (var lcc in lccs)
            {
                if (lcc == baseLcc) continue;
                Debug.Log($"[Scene4Browse] ICP BRUTE {baseLcc.name} ← {lcc.name}");
                _RunIcpBrute(baseLcc, lcc);
                // ICP 후 _ArasP child localRotation 재계산
                foreach (var go in scene.GetRootGameObjects())
                {
                    if (go.name == $"Splat_{lcc.name}")
                    {
                        var arasTr = go.transform.Find("_ArasP");
                        if (arasTr != null) arasTr.localRotation = Quaternion.Inverse(go.transform.rotation);
                    }
                }
            }

            // 6) Camera fit-all
            Bounds all = default; bool any = false;
            foreach (var go in scene.GetRootGameObjects())
            {
                if (!go.name.StartsWith("Splat_")) continue;
                var mr = go.GetComponentInChildren<MeshCollider>();
                if (mr == null || mr.sharedMesh == null) continue;
                var b = new Bounds(mr.transform.position, mr.sharedMesh.bounds.size);
                if (!any) { all = b; any = true; } else all.Encapsulate(b);
            }
            if (any)
            {
                var cam = GameObject.Find("Main Camera");
                if (cam != null)
                {
                    float r = all.extents.magnitude;
                    cam.transform.position = all.center + new Vector3(0f, r * 0.6f, -r * 1.8f);
                    cam.transform.LookAt(all.center, Vector3.up);
                    var camComp = cam.GetComponent<Camera>();
                    if (camComp != null) { camComp.farClipPlane = r * 5f; camComp.fieldOfView = 60f; }
                }
            }

            // 7) Directional Light 살짝 기울이기
            var lightGO = GameObject.Find("Directional Light");
            if (lightGO != null) lightGO.transform.rotation = Quaternion.Euler(45f, -30f, 0f);

            // 8) 저장
            System.IO.Directory.CreateDirectory("Assets/Scenes");
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, scenePath);
            Debug.Log($"[Scene4Browse] 빌드 완료 → {scenePath} · {lccs.Count}개 LCC, base={baseLcc.name}");
        }

        // 같은 LCC 이름에 대응하는 Aras-P asset 경로 찾기 (네이밍 변형 4가지 시도)
        static string _ResolveArasAssetPath(string lccName)
        {
            string[] candidates = {
                $"Assets/GaussianAssets/{lccName}.asset",
                $"Assets/GaussianAssets/{lccName}_lod0.asset",
                $"Assets/GaussianAssets/{lccName.Replace("ShinWon_", "")}.asset",
                $"Assets/GaussianAssets/{lccName.Replace("Shinwon_", "")}.asset",
            };
            foreach (var c in candidates)
                if (AssetDatabase.LoadMainAssetAtPath(c) != null) return c;
            return null;
        }

        [MenuItem("Tools/Lcc Drop Forge/Collider · DIAG · proxy mesh vs Aras-P bounds (per LCC)")]
        static void DevDiagColliderAlignment()
        {
            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            System.Type gsAsset = null;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            { gsAsset = asm.GetType("GaussianSplatting.Runtime.GaussianSplatAsset"); if (gsAsset != null) break; }
            var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

            foreach (var go in sm.GetRootGameObjects())
                _DiagOneSplat(go, gsAsset, bf);
            // LccGroup 자식도 검사
            var grp = System.Array.Find(sm.GetRootGameObjects(), r => r.name == "LccGroup");
            if (grp != null) foreach (Transform t in grp.transform) _DiagOneSplat(t.gameObject, gsAsset, bf);
        }

        static void _DiagOneSplat(GameObject splat, System.Type gsAsset, System.Reflection.BindingFlags bf)
        {
            if (splat == null || !splat.name.StartsWith("Splat_") || splat.name.StartsWith("Splat_ArasP_")) return;
            // Aras-P bounds
            var arasTr = splat.transform.Find("_ArasP");
            Bounds? arasB = null;
            if (arasTr != null)
            {
                var gsr = arasTr.GetComponentInChildren<MonoBehaviour>();
                foreach (var mb in arasTr.GetComponents<MonoBehaviour>())
                {
                    if (mb == null || mb.GetType().Name != "GaussianSplatRenderer") continue;
                    var assetField = mb.GetType().GetField("m_Asset", bf);
                    if (assetField == null) continue;
                    var asset = assetField.GetValue(mb);
                    if (asset == null || gsAsset == null) break;
                    var bMin = (Vector3)gsAsset.GetField("m_BoundsMin", bf).GetValue(asset);
                    var bMax = (Vector3)gsAsset.GetField("m_BoundsMax", bf).GetValue(asset);
                    arasB = new Bounds((bMin + bMax) * 0.5f, bMax - bMin);
                    break;
                }
            }
            // Proxy mesh bounds (local + world)
            var colTr = splat.transform.Find("__LccCollider");
            Bounds? proxyLocal = null, proxyWorld = null;
            if (colTr != null)
            {
                var mc = colTr.GetComponent<MeshCollider>();
                if (mc != null && mc.sharedMesh != null)
                {
                    proxyLocal = mc.sharedMesh.bounds;
                    proxyWorld = mc.bounds;
                }
            }
            Debug.Log($"[Diag] {splat.name}\n  parent.rot={splat.transform.eulerAngles}  parent.pos={splat.transform.position}\n  Aras-P bounds (asset local) center={arasB?.center} size={arasB?.size}\n  Proxy mesh local center={proxyLocal?.center} size={proxyLocal?.size}\n  Proxy mesh WORLD center={proxyWorld?.center} size={proxyWorld?.size}\n  → 일치 여부: center 차={(arasB.HasValue && proxyLocal.HasValue ? (arasB.Value.center - proxyLocal.Value.center).ToString() : "n/a")}");
        }

        [MenuItem("Tools/Lcc Drop Forge/Collider · BAKE v3 · convert + save mesh asset (efficient, permanent)")]
        static void DevBakeColliderMeshAsset()
        {
            const string outFolder = "Assets/LCC_Generated";
            System.IO.Directory.CreateDirectory(outFolder);

            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            int n = 0, reused = 0;

            void BakeOne(GameObject splat)
            {
                if (splat == null || !splat.name.StartsWith("Splat_") || splat.name.StartsWith("Splat_ArasP_")) return;

                string lccName = splat.name.Substring("Splat_".Length);
                string assetPath = $"{outFolder}/{lccName}_ColliderYup.asset";

                Mesh bakedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
                if (bakedMesh == null)
                {
                    var lcc = _FindLccSceneByName(lccName);
                    if (lcc == null) { Debug.LogWarning($"[Bake v3] {splat.name}: LccScene 없음"); return; }
                    var plyPath = lcc.ResolveProxyMeshPlyAssetPath();
                    if (string.IsNullOrEmpty(plyPath) || !System.IO.File.Exists(System.IO.Path.GetFullPath(plyPath)))
                    { Debug.LogWarning($"[Bake v3] {splat.name}: proxy PLY 없음"); return; }

                    Mesh srcMesh;
                    try { srcMesh = Virnect.Lcc.LccMeshPlyLoader.Load(System.IO.Path.GetFullPath(plyPath)); }
                    catch (System.Exception e) { Debug.LogError($"[Bake v3] {splat.name} PLY load 실패: {e.Message}"); return; }
                    if (srcMesh == null) return;

                    // Z-up → Y-up 변환 + asset 으로 저장 (한 번 굽고 영구 사용)
                    bakedMesh = _ConvertZupToYupMesh(srcMesh, lccName);
                    bakedMesh.hideFlags = HideFlags.None;   // asset 저장 위해 normal flag
                    AssetDatabase.CreateAsset(bakedMesh, assetPath);
                    AssetDatabase.SaveAssets();
                    Debug.Log($"[Bake v3] {splat.name}: 새 mesh asset 저장 → {assetPath} ({bakedMesh.vertexCount:N0} verts)");
                    n++;
                }
                else
                {
                    Debug.Log($"[Bake v3] {splat.name}: 기존 mesh asset 재사용 → {assetPath}");
                    reused++;
                }

                // __LccCollider child + MeshCollider 보장
                var colTr = splat.transform.Find("__LccCollider");
                GameObject colGO;
                if (colTr == null)
                {
                    colGO = new GameObject("__LccCollider");
                    colGO.transform.SetParent(splat.transform, false);
                    Undo.RegisterCreatedObjectUndo(colGO, "Add __LccCollider");
                    colTr = colGO.transform;
                }
                else colGO = colTr.gameObject;

                // 옵션 B+C: localRotation = Inverse(parent) → world identity (mesh 자체가 변환된 Y-up이므로 Aras-P와 같은 좌표계)
                Undo.RecordObject(colTr, "Bake v3 transform");
                colTr.localPosition = Vector3.zero;
                colTr.localRotation = Quaternion.Inverse(splat.transform.rotation);
                colTr.localScale = Vector3.one;

                var mc = colGO.GetComponent<MeshCollider>();
                if (mc == null) mc = colGO.AddComponent<MeshCollider>();
                mc.sharedMesh = bakedMesh;
                mc.convex = false;
                mc.enabled = true;
                EditorUtility.SetDirty(colGO);
                EditorUtility.SetDirty(mc);
            }

            foreach (var go in sm.GetRootGameObjects()) BakeOne(go);
            var grp = System.Array.Find(sm.GetRootGameObjects(), r => r != null && r.name == "LccGroup");
            if (grp != null) foreach (Transform t in grp.transform) BakeOne(t.gameObject);

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(sm);
            Debug.Log($"[Bake v3] 완료 — 새 baked={n}, 재사용={reused}. transform 만져도 안 깨짐 (mesh asset 영구 저장됨)");
        }

        // Z-up (LCC native) → Y-up (Unity / Aras-P) mesh 변환. (x, y, z) → (x, z, -y)
        static Mesh _ConvertZupToYupMesh(Mesh src, string label)
        {
            var v  = src.vertices;
            var ny = new Vector3[v.Length];
            for (int i = 0; i < v.Length; i++) ny[i] = new Vector3(v[i].x, v[i].z, -v[i].y);

            var n  = src.normals;
            Vector3[] nn = null;
            if (n != null && n.Length == v.Length)
            {
                nn = new Vector3[n.Length];
                for (int i = 0; i < n.Length; i++) nn[i] = new Vector3(n[i].x, n[i].z, -n[i].y);
            }

            var dst = new Mesh
            {
                name = src.name + "_Yup_" + label,
                indexFormat = src.indexFormat,
                hideFlags = HideFlags.DontSave,
            };
            dst.vertices = ny;
            if (nn != null) dst.normals = nn;
            dst.triangles = src.triangles;   // tri winding 유지 (Z-up→Y-up 미러 X축 → winding 반전 필요할 수 있음, 일단 보존)
            dst.RecalculateBounds();
            return dst;
        }

        [MenuItem("Tools/Lcc Drop Forge/Scene5 · Save current Player_VBot pos as default spawn (EditorPref)")]
        static void DevScene5SavePlayerSpawn()
        {
            const string kSpawnXKey = "LccDropForge.Scene5.PlayerSpawnX";
            const string kSpawnYKey = "LccDropForge.Scene5.PlayerSpawnY";
            const string kSpawnZKey = "LccDropForge.Scene5.PlayerSpawnZ";
            const string kSpawnSetKey = "LccDropForge.Scene5.PlayerSpawnSet";
            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            var p = System.Array.Find(sm.GetRootGameObjects(), r => r != null && r.name == "Player_VBot");
            if (p == null) { Debug.LogError("[Scene5 SavePos] Player_VBot 없음"); return; }
            var pos = p.transform.position;
            EditorPrefs.SetFloat(kSpawnXKey, pos.x);
            EditorPrefs.SetFloat(kSpawnYKey, pos.y);
            EditorPrefs.SetFloat(kSpawnZKey, pos.z);
            EditorPrefs.SetBool(kSpawnSetKey, true);
            Debug.Log($"[Scene5 SavePos] 현재 Player_VBot 위치 {pos} → EditorPref 저장. Walkable v3 메뉴 재실행 시 자동 적용");
        }

        [MenuItem("Tools/Lcc Drop Forge/Scene5 · Reset saved player spawn (use bounds top fallback)")]
        static void DevScene5ResetPlayerSpawn()
        {
            EditorPrefs.DeleteKey("LccDropForge.Scene5.PlayerSpawnSet");
            EditorPrefs.DeleteKey("LccDropForge.Scene5.PlayerSpawnX");
            EditorPrefs.DeleteKey("LccDropForge.Scene5.PlayerSpawnY");
            EditorPrefs.DeleteKey("LccDropForge.Scene5.PlayerSpawnZ");
            Debug.Log("[Scene5 ResetPos] EditorPref spawn 위치 초기화 — 다음 빌드는 첫 Splat bounds top 사용");
        }

        [MenuItem("Tools/Lcc Drop Forge/Scene5 · Walkable v3 (per-Splat MeshColliders + Invector V-Bot robot)")]
        static void DevScene5Walkable_V3()
        {
            const string playerPrefabPath = "Assets/Invector-3rdPersonController_LITE/Prefabs/ThirdPersonController_LITE.prefab";
            const string camPrefabPath    = "Assets/Invector-3rdPersonController_LITE/Prefabs/vThirdPersonCamera_LITE.prefab";

            // EditorPref keys — 사용자 커스텀 spawn 위치 보존 (메뉴 재실행해도 유지)
            const string kSpawnXKey = "LccDropForge.Scene5.PlayerSpawnX";
            const string kSpawnYKey = "LccDropForge.Scene5.PlayerSpawnY";
            const string kSpawnZKey = "LccDropForge.Scene5.PlayerSpawnZ";
            const string kSpawnSetKey = "LccDropForge.Scene5.PlayerSpawnSet";

            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            var roots = sm.GetRootGameObjects();

            // 0a) 기존 Player_VBot 있으면 → 위치 EditorPref 에 캐시 (사용자가 옮긴 위치 보존)
            var existingPlayer = System.Array.Find(roots, r => r != null && r.name == "Player_VBot");
            Vector3? customSpawnPos = null;
            if (existingPlayer != null)
            {
                var p = existingPlayer.transform.position;
                EditorPrefs.SetFloat(kSpawnXKey, p.x);
                EditorPrefs.SetFloat(kSpawnYKey, p.y);
                EditorPrefs.SetFloat(kSpawnZKey, p.z);
                EditorPrefs.SetBool(kSpawnSetKey, true);
                customSpawnPos = p;
                Debug.Log($"[Walkable v3] 기존 Player_VBot 위치 캐시 → {p} (EditorPref 저장, 다음 빌드 시 자동 적용)");
            }
            else if (EditorPrefs.GetBool(kSpawnSetKey, false))
            {
                customSpawnPos = new Vector3(
                    EditorPrefs.GetFloat(kSpawnXKey, 0f),
                    EditorPrefs.GetFloat(kSpawnYKey, 30f),
                    EditorPrefs.GetFloat(kSpawnZKey, 0f));
                Debug.Log($"[Walkable v3] 저장된 spawn 위치 사용 → {customSpawnPos.Value}");
            }

            // 0b) 옛날 setup 잔재 정리 (Player_VBot 포함)
            foreach (var r in roots)
            {
                if (r == null) continue;
                if (r.name == "Player_LccWalker" || r.name == "__CombinedCollider" ||
                    r.name == "Player_VBot" || r.name == "Cam_VBot")
                    Undo.DestroyObjectImmediate(r);
            }

            // LccGroup 부모 GameObject 의 MeshCollider 제거 (단일 통합 폐기). LccGroup 자체는 keep.
            var groupGO = System.Array.Find(roots, r => r != null && r.name == "LccGroup");
            if (groupGO != null)
            {
                var mc = groupGO.GetComponent<MeshCollider>();
                if (mc != null) Undo.DestroyObjectImmediate(mc);
            }

            // 1) 모든 Splat 수집
            var splats = new System.Collections.Generic.List<GameObject>();
            // LccGroup 자식 / root 둘 다 검색
            if (groupGO != null)
                foreach (Transform t in groupGO.transform)
                    if (t.name.StartsWith("Splat_") && !t.name.StartsWith("Splat_ArasP_")) splats.Add(t.gameObject);
            foreach (var go in roots)
                if (go != null && go.name.StartsWith("Splat_") && !go.name.StartsWith("Splat_ArasP_") && !splats.Contains(go))
                    splats.Add(go);
            if (splats.Count == 0) { Debug.LogError("[Walkable v3] Splat_ 객체 없음"); return; }

            // 2) 각 Splat에 __LccCollider 자식 + MeshCollider (proxy mesh) 보장 (각자 개별 colliders)
            int colAdded = 0, colReused = 0;
            foreach (var s in splats)
            {
                var colTr = s.transform.Find("__LccCollider");
                GameObject colGO;
                if (colTr != null) { colGO = colTr.gameObject; colReused++; }
                else
                {
                    colGO = new GameObject("__LccCollider");
                    colGO.transform.SetParent(s.transform, false);
                    colGO.transform.localPosition = Vector3.zero;
                    colGO.transform.localRotation = Quaternion.identity;
                    colGO.transform.localScale = Vector3.one;
                    Undo.RegisterCreatedObjectUndo(colGO, "Add __LccCollider");
                    colAdded++;
                }
                var mc = colGO.GetComponent<MeshCollider>();
                if (mc == null) mc = colGO.AddComponent<MeshCollider>();
                if (mc.sharedMesh == null)
                {
                    string lccName = s.name.Substring("Splat_".Length);
                    var lcc = _FindLccSceneByName(lccName);
                    if (lcc != null)
                    {
                        string plyPath = lcc.ResolveProxyMeshPlyAssetPath();
                        if (!string.IsNullOrEmpty(plyPath) && System.IO.File.Exists(System.IO.Path.GetFullPath(plyPath)))
                            try { mc.sharedMesh = Virnect.Lcc.LccMeshPlyLoader.Load(System.IO.Path.GetFullPath(plyPath)); }
                            catch (System.Exception e) { Debug.LogWarning($"[Walkable v3] {s.name} mesh load 실패: {e.Message}"); }
                    }
                }
                mc.enabled = true;
                mc.convex = false;
            }
            Debug.Log($"[Walkable v3] __LccCollider: 새로 {colAdded}개, 재사용 {colReused}개 ({splats.Count}개 Splat)");

            // 3) Player + Camera prefab instantiate
            var playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(playerPrefabPath);
            var camPrefab    = AssetDatabase.LoadAssetAtPath<GameObject>(camPrefabPath);
            if (playerPrefab == null) { Debug.LogError($"[Walkable v3] Player prefab 없음: {playerPrefabPath}"); return; }
            if (camPrefab == null)    { Debug.LogError($"[Walkable v3] Camera prefab 없음: {camPrefabPath}"); return; }

            // spawn 위치 우선순위: ① 사용자가 옮긴 위치(EditorPref) → ② 첫 Splat 콜라이더 bounds top + 5m
            Vector3 spawnPos;
            if (customSpawnPos.HasValue)
            {
                spawnPos = customSpawnPos.Value;
            }
            else
            {
                spawnPos = new Vector3(0f, 30f, 0f);   // fallback
                foreach (var s in splats)
                {
                    var colTr = s.transform.Find("__LccCollider");
                    if (colTr == null) continue;
                    var mc = colTr.GetComponent<MeshCollider>();
                    if (mc == null || mc.sharedMesh == null) continue;
                    var b = mc.bounds;
                    spawnPos = new Vector3(b.center.x, b.max.y + 5f, b.center.z);
                    break;
                }
            }

            var player = (GameObject)PrefabUtility.InstantiatePrefab(playerPrefab);
            Undo.RegisterCreatedObjectUndo(player, "Spawn V-Bot Player");
            player.name = "Player_VBot";
            player.transform.position = spawnPos;
            player.transform.rotation = Quaternion.identity;

            var cam = (GameObject)PrefabUtility.InstantiatePrefab(camPrefab);
            Undo.RegisterCreatedObjectUndo(cam, "Spawn vThirdPersonCamera");
            cam.name = "Cam_VBot";

            // vThirdPersonCamera.target = player (reflection — Invector 스키마)
            foreach (var mb in cam.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null || mb.GetType().Name != "vThirdPersonCamera") continue;
                var field = mb.GetType().GetField("target");
                if (field != null) field.SetValue(mb, player.transform);
                break;
            }

            // 기본 Main Camera 가 있으면 disable (vThirdPersonCamera 사용)
            var mainCam = GameObject.Find("Main Camera");
            if (mainCam != null && mainCam != cam) mainCam.SetActive(false);

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(sm);
            Selection.activeGameObject = player;
            EditorApplication.delayCall += () => { var sv = SceneView.lastActiveSceneView; if (sv != null) sv.FrameSelected(); };

            Debug.Log($"[Walkable v3] ✓ Setup 완료 · Player_VBot @ {spawnPos}");
            Debug.Log("  · ▶ Play → WASD 이동, Shift 달리기, Space 점프, 마우스 카메라 회전");
            Debug.Log("  · 옛 통합 MeshCollider 제거 · 각 Splat에 __LccCollider (proxy mesh) 보장");

            // 더 이상 코드 진행 안함 — 옛 v2 메뉴 코드는 무시
            return;
        }

        [MenuItem("Tools/Lcc Drop Forge/Scene · ⚠ Reset ALL Splat positions to (0,0,0) — destructive (정합 깨짐 주의)")]
        static void DevResetSplatPositionsExplicit()
        {
            if (!EditorUtility.DisplayDialog(
                "Reset Splat positions",
                "모든 Splat_ GameObject 의 position 을 (0,0,0) 으로 초기화합니다. 사용자 수동 정합 결과가 깨집니다.\n\n계속할까요?",
                "위치 초기화", "취소")) return;
            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            int n = 0;
            foreach (var go in sm.GetRootGameObjects())
            {
                if (go == null || !go.name.StartsWith("Splat_") || go.name.StartsWith("Splat_ArasP_")) continue;
                Undo.RecordObject(go.transform, "Reset Splat position (explicit)");
                go.transform.position = Vector3.zero;
                EditorUtility.SetDirty(go);
                n++;
            }
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(sm);
            Debug.Log($"[Reset] {n}개 Splat position → (0,0,0)");
        }

        [MenuItem("Tools/Lcc Drop Forge/Scene · Set ALL Splat rotation to (-180, 0, 0)")]
        static void DevSetAllSplatRotation_Minus180()
        {
            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            var newRot = Quaternion.Euler(-180f, 0f, 0f);
            int updated = 0;
            foreach (var go in sm.GetRootGameObjects())
            {
                if (go == null || !go.name.StartsWith("Splat_") || go.name.StartsWith("Splat_ArasP_")) continue;
                Undo.RecordObject(go.transform, "Set Splat rotation -180");
                go.transform.rotation = newRot;
                // _ArasP child localRotation 재계산 (world identity 유지)
                var arasTr = go.transform.Find("_ArasP");
                if (arasTr != null)
                {
                    Undo.RecordObject(arasTr, "Recalc _ArasP localRotation");
                    arasTr.localRotation = Quaternion.Inverse(newRot);
                }
                EditorUtility.SetDirty(go);
                updated++;
            }
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(sm);
            Debug.Log($"[Rot-180] {updated}개 Splat 회전 → (-180, 0, 0). _ArasP child localRotation 자동 재계산 완료.");
        }

        [MenuItem("Tools/Lcc Drop Forge/Scene · Lighten current (disable all Mesh and ColoredMesh roots)")]
        static void DevLightenSceneMeshOff()
        {
            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            int n = 0;
            foreach (var go in sm.GetRootGameObjects())
            {
                if (go == null) continue;
                if ((go.name.StartsWith("Mesh_") || go.name.StartsWith("ColoredMesh")) && go.activeSelf)
                {
                    Undo.RecordObject(go, "Lighten · Mesh OFF");
                    go.SetActive(false);
                    n++;
                }
            }
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(sm);
            Debug.Log($"[Lighten] Mesh_/ColoredMesh_ {n}개 비활성. (다시 켜려면 Hierarchy에서 직접)");
        }

        [MenuItem("Tools/Lcc Drop Forge/Scene · Ensure MeshColliders on all Splat (clickable in Scene View)")]
        static void DevEnsureSplatColliders()
        {
            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            int added = 0, kept = 0, fail = 0;
            foreach (var go in sm.GetRootGameObjects())
            {
                if (go == null || !go.name.StartsWith("Splat_")) continue;

                // 이미 자식 __LccCollider 가 있고 sharedMesh 도 살아있으면 skip
                var existing = go.transform.Find("__LccCollider");
                if (existing != null)
                {
                    var mc0 = existing.GetComponent<MeshCollider>();
                    if (mc0 != null && mc0.sharedMesh != null) { kept++; continue; }
                }

                // 이름에서 LccScene 찾기. "Splat_<name>" → LCC_Drops 안 폴더 검색
                string lccName = go.name.Substring("Splat_".Length);
                LccScene lcc = _FindLccSceneByName(lccName);
                if (lcc == null) { Debug.LogWarning($"[Colliders] {go.name}: LccScene '{lccName}' 못 찾음"); fail++; continue; }

                string plyPath = lcc.ResolveProxyMeshPlyAssetPath();
                if (string.IsNullOrEmpty(plyPath) || !System.IO.File.Exists(System.IO.Path.GetFullPath(plyPath)))
                { Debug.LogWarning($"[Colliders] {go.name}: proxy PLY 없음"); fail++; continue; }

                Mesh mesh;
                try { mesh = Virnect.Lcc.LccMeshPlyLoader.Load(System.IO.Path.GetFullPath(plyPath)); }
                catch (System.Exception e) { Debug.LogError($"[Colliders] {go.name}: PLY load 실패 — {e.Message}"); fail++; continue; }
                if (mesh == null) { fail++; continue; }

                GameObject colGO;
                if (existing != null) colGO = existing.gameObject;
                else
                {
                    colGO = new GameObject("__LccCollider");
                    Undo.RegisterCreatedObjectUndo(colGO, "Add __LccCollider");
                    colGO.transform.SetParent(go.transform, false);
                    colGO.transform.localPosition = Vector3.zero;
                    colGO.transform.localRotation = Quaternion.identity;
                    colGO.transform.localScale = Vector3.one;
                }

                var mc = colGO.GetComponent<MeshCollider>();
                if (mc == null) mc = colGO.AddComponent<MeshCollider>();
                mc.sharedMesh = mesh;
                mc.convex = false;
                added++;
                Debug.Log($"[Colliders] {go.name}: __LccCollider {(existing == null ? "추가" : "갱신")} ({mesh.vertexCount:N0} verts)");
            }
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(sm);
            Debug.Log($"[Colliders] added={added}, kept={kept}, fail={fail}. 이제 Scene View에서 splat 영역 클릭 → 부모 Splat_ 선택");
        }

        // LccScene asset 을 이름으로 검색 (대소문자 변형 + 폴더명 변형 포함)
        static LccScene _FindLccSceneByName(string name)
        {
            // candidate 폴더명: 정확히 그 이름, 첫 글자 소문자 (Shinwon_), Shinwon→ShinWon 등
            string[] folders = { name, name.Replace("ShinWon", "Shinwon"), name.Replace("Shinwon", "ShinWon") };
            foreach (var f in folders)
            {
                string p = $"Assets/LCC_Drops/{f}/{name}.lcc";
                var s = AssetDatabase.LoadAssetAtPath<LccScene>(p);
                if (s != null) return s;
            }
            // FindAssets fallback
            var guids = AssetDatabase.FindAssets($"{name} t:LccScene");
            foreach (var g in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                var s = AssetDatabase.LoadAssetAtPath<LccScene>(p);
                if (s != null && s.name == name) return s;
            }
            return null;
        }

        [MenuItem("Tools/Lcc Drop Forge/Scene · Auto-swap ALL Splat to Aras-P (current scene, any LCC)")]
        static void DevAutoSwapAllSplatsToAras()
        {
            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            System.Type lccRendererType = null, gsRendererType = null;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                if (lccRendererType == null) lccRendererType = asm.GetType("Virnect.Lcc.LccSplatRenderer");
                if (gsRendererType  == null) gsRendererType  = asm.GetType("GaussianSplatting.Runtime.GaussianSplatRenderer");
                if (lccRendererType != null && gsRendererType != null) break;
            }
            if (gsRendererType == null) { Debug.LogError("[Swap-All] GaussianSplatRenderer 타입 없음"); return; }
            var bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

            int swapped = 0, skipped = 0;
            foreach (var go in sm.GetRootGameObjects())
            {
                if (go == null || !go.name.StartsWith("Splat_") || go.name.StartsWith("Splat_ArasP_")) continue;
                string lccName = go.name.Substring("Splat_".Length);

                // asset 경로 후보 (다양한 작명 시도)
                string[] candidates = {
                    $"Assets/GaussianAssets/{lccName}.asset",
                    $"Assets/GaussianAssets/{lccName}_lod0.asset",
                    $"Assets/GaussianAssets/{lccName.Replace("ShinWon_", "")}.asset",
                    $"Assets/GaussianAssets/{lccName.Replace("Shinwon_", "")}.asset",
                };
                Object asset = null;
                foreach (var c in candidates)
                {
                    asset = AssetDatabase.LoadMainAssetAtPath(c);
                    if (asset != null) break;
                }
                if (asset == null) { Debug.LogWarning($"[Swap-All] {go.name}: asset 없음 (built/swap skip)"); skipped++; continue; }

                if (lccRendererType != null)
                {
                    var lcc = go.GetComponent(lccRendererType) as MonoBehaviour;
                    if (lcc != null) lcc.enabled = false;
                }
                var mr = go.GetComponent<MeshRenderer>();
                if (mr != null) mr.enabled = false;

                var arasTr = go.transform.Find("_ArasP");
                GameObject arasGO;
                if (arasTr == null)
                {
                    arasGO = new GameObject("_ArasP");
                    Undo.RegisterCreatedObjectUndo(arasGO, "Add _ArasP");
                    arasGO.transform.SetParent(go.transform, false);
                }
                else arasGO = arasTr.gameObject;
                arasGO.transform.localPosition = Vector3.zero;
                arasGO.transform.localRotation = Quaternion.Inverse(go.transform.rotation);
                arasGO.transform.localScale = Vector3.one;

                var gsr = arasGO.GetComponent(gsRendererType) as MonoBehaviour;
                if (gsr == null) gsr = arasGO.AddComponent(gsRendererType) as MonoBehaviour;
                gsr.enabled = false;
                var assetField = gsRendererType.GetField("m_Asset", bf) ?? gsRendererType.GetField("asset", bf);
                if (assetField != null) assetField.SetValue(gsr, asset);
                gsr.enabled = true;

                swapped++;
                Debug.Log($"[Swap-All] {go.name} → Aras-P ({asset.name})");
            }
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(sm);
            Debug.Log($"[Swap-All] swapped={swapped}, skipped={skipped}. 콘솔 경고는 GaussianSplatAsset 빌드부터 필요한 LCC.");
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · Aras-P · Register URP RendererFeature (PC_Renderer)")]
        static void DevRegisterArasURPFeature()
        {
            var rendererPath = "Assets/Settings/PC_Renderer.asset";
            var rendererData = AssetDatabase.LoadMainAssetAtPath(rendererPath) as ScriptableObject;
            if (rendererData == null) { Debug.LogError($"[Aras-URP] {rendererPath} load 실패"); return; }

            // Find ScriptableRendererFeature base type via reflection (URP)
            System.Type srpFeatBase = null;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                srpFeatBase = asm.GetType("UnityEngine.Rendering.Universal.ScriptableRendererFeature");
                if (srpFeatBase != null) break;
            }
            if (srpFeatBase == null) { Debug.LogError("[Aras-URP] ScriptableRendererFeature 타입 못 찾음"); return; }

            // Find Aras-P GaussianSplatting feature subclass
            System.Type featType = null;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var t in asm.GetTypes())
                {
                    if (t.Namespace != null && t.Namespace.Contains("GaussianSplatting") &&
                        srpFeatBase.IsAssignableFrom(t) && !t.IsAbstract)
                    { featType = t; break; }
                }
                if (featType != null) break;
            }
            if (featType == null) { Debug.LogError("[Aras-URP] GaussianSplatting의 ScriptableRendererFeature 타입 못 찾음 (패키지 미설치?)"); return; }

            // rendererFeatures 리스트 (UniversalRendererData.m_RendererFeatures, public List<ScriptableRendererFeature> rendererFeatures)
            var prop = rendererData.GetType().GetProperty("rendererFeatures",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            System.Collections.IList list = prop != null
                ? prop.GetValue(rendererData) as System.Collections.IList
                : null;
            if (list == null)
            {
                var fld = rendererData.GetType().GetField("m_RendererFeatures",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (fld != null) list = fld.GetValue(rendererData) as System.Collections.IList;
            }
            if (list == null) { Debug.LogError("[Aras-URP] rendererFeatures 리스트 접근 실패"); return; }

            foreach (var f in list)
                if (f != null && f.GetType() == featType)
                { Debug.Log($"[Aras-URP] {featType.Name} 이미 등록됨 — skip"); return; }

            var feat = ScriptableObject.CreateInstance(featType);
            feat.name = featType.Name;
            // SetActive(true) via reflection
            var setActiveMethod = srpFeatBase.GetMethod("SetActive", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (setActiveMethod != null) setActiveMethod.Invoke(feat, new object[] { true });

            AssetDatabase.AddObjectToAsset(feat, rendererData);
            list.Add(feat);

            EditorUtility.SetDirty(rendererData);
            EditorUtility.SetDirty(feat);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[Aras-URP] {featType.FullName} 추가 완료 → {rendererPath}");
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · A/B Auto-compare 1st_Cutter (Virnect SDK vs Aras-P)")]
        static void DevAutoCompareAB()
        {
            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            var roots = sm.GetRootGameObjects();

            var virnectGO = System.Array.Find(roots, r => r.name == "Splat_ShinWon_1st_Cutter");
            if (virnectGO == null) { Debug.LogError("[A/B] Splat_ShinWon_1st_Cutter 없음 (Scene2_MeshVsSplat 활성?)"); return; }

            // 1) Spawn Aras-P sibling (없으면)
            var arasName = "Splat_ArasP_1st_Cutter";
            var arasGO = System.Array.Find(roots, r => r.name == arasName);
            bool freshlySpawned = false;
            if (arasGO == null)
            {
                arasGO = new GameObject(arasName);
                arasGO.transform.position = virnectGO.transform.position;
                arasGO.transform.rotation = virnectGO.transform.rotation;
                arasGO.transform.localScale = virnectGO.transform.localScale;
                Undo.RegisterCreatedObjectUndo(arasGO, "Spawn Aras-P sibling");
                freshlySpawned = true;
            }

            // 2) Aras-P GaussianSplatRenderer 타입 찾기 (reflection)
            System.Type gsType = null;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                gsType = asm.GetType("GaussianSplatting.Runtime.GaussianSplatRenderer");
                if (gsType != null) break;
            }
            if (gsType == null) { Debug.LogError("[A/B] GaussianSplatRenderer 타입 못 찾음 (Aras-P 패키지 확인)"); return; }

            var gsr = arasGO.GetComponent(gsType);
            if (gsr == null) gsr = arasGO.AddComponent(gsType);

            // 3) Asset 필드에 ShinWon_1st_Cutter_lod0.asset 할당
            var assetPath = "Assets/GaussianAssets/ShinWon_1st_Cutter_lod0.asset";
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null) { Debug.LogError($"[A/B] asset 없음: {assetPath}"); return; }

            var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
            var assetField = gsType.GetField("m_Asset", bf);
            if (assetField == null) assetField = gsType.GetField("asset", bf);
            if (assetField == null) { Debug.LogError("[A/B] GaussianSplatRenderer.m_Asset/asset 필드 못 찾음"); return; }
            assetField.SetValue(gsr, asset);
            EditorUtility.SetDirty(gsr as UnityEngine.Object);
            Debug.Log($"[A/B] Aras-P sibling {(freshlySpawned ? "spawned" : "updated")} · asset={asset.name} · gsType={gsType.FullName}");

            // 4) 캡처 카메라 위치 — 1st_Cutter 단독 bounds 기준 3/4 perspective close-up
            var virnectMR = virnectGO.GetComponent<MeshRenderer>();
            Bounds b = virnectMR != null ? virnectMR.bounds : new Bounds(virnectGO.transform.position, Vector3.one * 30f);
            float radius = b.extents.magnitude;
            var dir = Quaternion.Euler(35f, -20f, 0f) * Vector3.forward;
            var camPos = b.center - dir * radius * 1.6f;
            var camRot = Quaternion.LookRotation(b.center - camPos, Vector3.up);

            // 5) 다른 모든 Splat_/Mesh_ 객체 임시 OFF (1st_Cutter 단독 비교)
            var others = new System.Collections.Generic.List<(GameObject go, bool wasActive)>();
            foreach (var r in roots)
            {
                if (r == null || r == virnectGO || r == arasGO) continue;
                if (r.name.StartsWith("Splat_") || r.name.StartsWith("Mesh_") || r.name.StartsWith("ColoredMesh"))
                {
                    others.Add((r, r.activeSelf));
                    r.SetActive(false);
                }
            }

            try
            {
                // 6) Virnect ON / Aras OFF → capture
                virnectGO.SetActive(true);
                arasGO.SetActive(false);
                _RenderView("Library/tmp_compare_VIRNECT_SDK.png", camPos, camRot, radius * 5f, 1920, 1080, false, radius);

                // 7) Virnect OFF / Aras ON → capture
                virnectGO.SetActive(false);
                arasGO.SetActive(true);
                _RenderView("Library/tmp_compare_ARAS_P.png", camPos, camRot, radius * 5f, 1920, 1080, false, radius);
            }
            finally
            {
                // 8) Restore 모든 객체
                virnectGO.SetActive(true);
                arasGO.SetActive(true);
                foreach (var (g, wasActive) in others) g.SetActive(wasActive);
            }

            Debug.Log("[A/B] 캡처 완료. 비교: Library/tmp_compare_VIRNECT_SDK.png vs Library/tmp_compare_ARAS_P.png");
            Debug.Log($"[A/B] 카메라 pos={camPos} look→{b.center}  radius={radius:F1}");
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · Switcher self-test (AutoMapEnv dry-run)")]
        static void DevSwitcherSelfTest()
        {
            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            var roots = sm.GetRootGameObjects();

            var switcher = System.Array.Find(roots, r => r.name == "__LccCharSwitcher");
            if (switcher == null) { Debug.LogError("[SelfTest] __LccCharSwitcher 없음"); return; }

            var swType = System.Type.GetType("LccCharacterSwitcher, Assembly-CSharp");
            if (swType == null) { Debug.LogError("[SelfTest] LccCharacterSwitcher 타입 못 찾음 (Assembly-CSharp 컴파일 필요)"); return; }
            var sw  = switcher.GetComponent(swType);
            var pA  = swType.GetField("playerA")?.GetValue(sw) as GameObject;
            var pB  = swType.GetField("playerB")?.GetValue(sw) as GameObject;
            var cam = swType.GetField("thirdPersonCamera")?.GetValue(sw) as GameObject;
            var envA = swType.GetField("envA")?.GetValue(sw) as GameObject[];
            var envB = swType.GetField("envB")?.GetValue(sw) as GameObject[];
            var camOff = swType.GetField("camOffset") != null
                ? (Vector3)swType.GetField("camOffset").GetValue(sw)
                : Vector3.zero;

            Debug.Log($"[SelfTest] Fields · playerA={(pA?pA.name:"null")}  playerB={(pB?pB.name:"null")}  cam={(cam?cam.name:"null")}  camOffset={camOff}");
            Debug.Log($"[SelfTest] Inspector envA={(envA==null?"null":envA.Length.ToString())}  envB={(envB==null?"null":envB.Length.ToString())}  → {(((envA==null||envA.Length==0)&&(envB==null||envB.Length==0))?"비어있음 → Awake에서 자동 매핑 예정":"수동 지정됨 (자동 매핑 스킵)")}");

            // Dry-run: LccCharacterSwitcher._AutoMapEnv 와 동일 로직
            var listA = new System.Collections.Generic.List<GameObject>();
            var listB = new System.Collections.Generic.List<GameObject>();
            var skipped = new System.Collections.Generic.List<string>();
            foreach (var go in roots)
            {
                if (go == null || go == switcher || go == pA || go == pB || go == cam) continue;
                string n = go.name;
                if (n.StartsWith("Splat_") || n.StartsWith("__Lcc"))      listA.Add(go);
                else if (n.StartsWith("Mesh_") || n.StartsWith("ColoredMesh")) listB.Add(go);
                else skipped.Add(n);
            }
            Debug.Log($"[SelfTest] DryRun envA ({listA.Count}) = [{string.Join(", ", listA.ConvertAll(g=>g.name))}]");
            Debug.Log($"[SelfTest] DryRun envB ({listB.Count}) = [{string.Join(", ", listB.ConvertAll(g=>g.name))}]");
            if (skipped.Count > 0)
                Debug.Log($"[SelfTest] Skipped (prefix 미매칭, 토글 안 됨) = [{string.Join(", ", skipped)}]");

            // 카메라 스냅 시뮬레이션 — A→B / B→A 두 케이스
            if (cam != null && pA != null && pB != null)
            {
                Vector3 snapToA = pA.transform.position + pA.transform.TransformDirection(camOff);
                Vector3 snapToB = pB.transform.position + pB.transform.TransformDirection(camOff);
                Debug.Log($"[SelfTest] Camera snap target · [1]→A pos={snapToA}  ·  [2]→B pos={snapToB}  (현재 cam pos={cam.transform.position})");
                Debug.Log($"[SelfTest] Player 거리 = {Vector3.Distance(pA.transform.position, pB.transform.position):F2}m  (smooth lerp 회피용 즉시 스냅 OK)");
            }

            // _Apply 시 effect 요약
            Debug.Log("[SelfTest] [1] 누르면: playerA active, playerB SetActive(false), envA on, envB off, camera→A");
            Debug.Log("[SelfTest] [2] 누르면: playerB active, playerA SetActive(false), envB on, envA off, camera→B");
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · Verify colliders + character spawn (Raycast test)")]
        static void DevVerifyColliders()
        {
            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            var roots = sm.GetRootGameObjects();

            string[] pairs = { "ShinWon_Facility_01" };
            foreach (var key in pairs)
            {
                foreach (var prefix in new[] { "Splat_", "Mesh_" })
                {
                    string target = prefix + key;
                    var go = System.Array.Find(roots, r => r.name == target);
                    if (go == null) { Debug.LogWarning($"[Verify] {target} 없음"); continue; }

                    // Collider 확보
                    var mc = go.GetComponentInChildren<MeshCollider>();
                    Debug.Log($"[Verify] {target} MeshCollider: " +
                        (mc == null ? "❌ 없음" :
                         mc.sharedMesh == null ? "⚠ sharedMesh null" :
                         $"✓ {mc.sharedMesh.name} ({mc.sharedMesh.vertexCount:N0} verts) enabled={mc.enabled}"));

                    // Character
                    string playerName = "Player_" + target;
                    var player = System.Array.Find(roots, r => r.name == playerName);
                    if (player == null) { Debug.LogWarning($"[Verify]   Player_{target} 없음"); continue; }

                    // 캐릭터의 collider 들을 잠시 disable 해서 Raycast 가 바닥 collider 맞게
                    var playerCols = player.GetComponentsInChildren<Collider>(true);
                    bool[] prevEnabled = new bool[playerCols.Length];
                    for (int i = 0; i < playerCols.Length; i++)
                    {
                        prevEnabled[i] = playerCols[i].enabled;
                        playerCols[i].enabled = false;
                    }

                    Vector3 origin = player.transform.position + Vector3.up * 20f;
                    var all = Physics.RaycastAll(origin, Vector3.down, 500f, ~0, QueryTriggerInteraction.Ignore);
                    System.Array.Sort(all, (a, b) => a.distance.CompareTo(b.distance));

                    if (all.Length == 0)
                        Debug.LogWarning($"[Verify]   Player@{player.transform.position.y:F2}  Raycast↓ MISS — 바닥 collider 없음");
                    else
                    {
                        var hit = all[0];
                        float d = hit.point.y - player.transform.position.y;
                        bool hitsTarget = mc != null && (hit.collider == mc);
                        Debug.Log($"[Verify]   Player@{player.transform.position.y:F2}  Raycast↓ 바닥 '{hit.collider.name}' @ y={hit.point.y:F2} (Δ={d:F2}m)  {(hitsTarget ? "✓ target collider" : "⚠ other: " + hit.collider.transform.root.name)}");
                    }

                    for (int i = 0; i < playerCols.Length; i++) playerCols[i].enabled = prevEnabled[i];

                    // CharacterController 또는 Rigidbody 상태
                    var cc = player.GetComponent<CharacterController>();
                    var rb = player.GetComponent<Rigidbody>();
                    Debug.Log($"[Verify]   CC={(cc == null ? "none" : $"enabled={cc.enabled}")}  RB={(rb == null ? "none" : $"kinematic={rb.isKinematic} useGravity={rb.useGravity}")}");
                }
            }

            // Switcher state
            var switcher = System.Array.Find(roots, r => r.name == "__LccCharSwitcher");
            if (switcher != null)
            {
                var swType = System.Type.GetType("LccCharacterSwitcher, Assembly-CSharp");
                if (swType != null)
                {
                    var sw = switcher.GetComponent(swType);
                    var pA = swType.GetField("playerA")?.GetValue(sw) as GameObject;
                    var pB = swType.GetField("playerB")?.GetValue(sw) as GameObject;
                    var cam = swType.GetField("thirdPersonCamera")?.GetValue(sw) as GameObject;
                    Debug.Log($"[Verify] Switcher: playerA={(pA?pA.name:"null")}  playerB={(pB?pB.name:"null")}  cam={(cam?cam.name:"null")}");
                }
            }
            else Debug.LogWarning("[Verify] __LccCharSwitcher 없음");
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · Keep only 2 colliders (Facility_01 Splat/Mesh)")]
        static void DevKeepTwoColliders()
        {
            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            int removed = 0, kept = 0;
            foreach (var root in sm.GetRootGameObjects())
            {
                bool isFacility01 = root.name == "Splat_ShinWon_Facility_01" || root.name == "Mesh_ShinWon_Facility_01";
                if (isFacility01)
                {
                    // Splat 쪽: __LccCollider 자식의 MeshCollider 유지
                    var mc = root.GetComponentInChildren<MeshCollider>();
                    if (mc != null) { Undo.RecordObject(mc, "Enable Collider"); mc.enabled = true; kept++; }
                }
                else if (root.name.StartsWith("Splat_") || root.name.StartsWith("Mesh_"))
                {
                    // 나머지 4개의 collider 제거 (자식 포함)
                    foreach (var mc in root.GetComponentsInChildren<MeshCollider>(true))
                    {
                        Undo.DestroyObjectImmediate(mc);
                        removed++;
                    }
                    // __LccCollider 자식도 빈 오브젝트라 제거
                    foreach (Transform child in root.transform)
                    {
                        if (child.name == "__LccCollider")
                            Undo.DestroyObjectImmediate(child.gameObject);
                    }
                }
            }
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(sm);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(sm);
            Debug.Log($"[2Colliders] removed={removed} · kept={kept} · Splat Facility_01 의 __LccCollider + Mesh Facility_01 의 MeshCollider 만 남김");
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · Keep only 2 chars (Facility_01 Splat + Mesh) + Switcher UI")]
        static void DevKeepTwoCharsWithSwitcher()
        {
            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();

            GameObject playerA = null, playerB = null;
            foreach (var root in sm.GetRootGameObjects())
            {
                if (root.name == "Player_Splat_ShinWon_Facility_01") playerA = root;
                else if (root.name == "Player_Mesh_ShinWon_Facility_01") playerB = root;
                else if (root.name.StartsWith("Player_"))
                {
                    Debug.Log($"[2Chars] 제거: {root.name}");
                    Undo.DestroyObjectImmediate(root);
                }
            }
            if (playerA == null || playerB == null)
            { Debug.LogError("[2Chars] Player_Splat_ShinWon_Facility_01 / Player_Mesh_ShinWon_Facility_01 중 하나 없음"); return; }

            // Switcher 오브젝트 (없으면 생성)
            GameObject switcher = null;
            foreach (var root in sm.GetRootGameObjects())
                if (root.name == "__LccCharSwitcher") { switcher = root; break; }
            if (switcher == null)
            {
                switcher = new GameObject("__LccCharSwitcher");
                Undo.RegisterCreatedObjectUndo(switcher, "Create Switcher");
            }
            // LccCharacterSwitcher 는 Assembly-CSharp 소속 → 타입을 이름으로 찾아 reflection 으로 설정
            var swType = System.Type.GetType("LccCharacterSwitcher, Assembly-CSharp");
            if (swType == null) { Debug.LogError("[2Chars] LccCharacterSwitcher 타입 없음 — Assets/LccCharacterSwitcher.cs 확인"); return; }

            var sw = switcher.GetComponent(swType);
            if (sw == null) sw = switcher.AddComponent(swType);

            GameObject cam = null;
            foreach (var root in sm.GetRootGameObjects())
                if (root.name == "Cam_Shared") { cam = root; break; }
            if (cam == null) Debug.LogWarning("[2Chars] Cam_Shared 없음 — 수동 Cam 지정 필요");

            _SetFieldSafe(swType, sw, "playerA", playerA);
            _SetFieldSafe(swType, sw, "labelA",  "LCC Splat");
            _SetFieldSafe(swType, sw, "playerB", playerB);
            _SetFieldSafe(swType, sw, "labelB",  "Colored Mesh");
            _SetFieldSafe(swType, sw, "thirdPersonCamera", cam);

            // 리셋 기준점: 현재 씬의 Player 위치를 명시적으로 serialize (Play 시 물리로 흔들려도 고정)
            _SetFieldSafe(swType, sw, "spawnPosA",    playerA.transform.position);
            _SetFieldSafe(swType, sw, "spawnEulerA", playerA.transform.eulerAngles);
            _SetFieldSafe(swType, sw, "spawnPosB",    playerB.transform.position);
            _SetFieldSafe(swType, sw, "spawnEulerB", playerB.transform.eulerAngles);
            _SetFieldSafe(swType, sw, "useManualSpawn", true);

            EditorUtility.SetDirty(switcher);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(sm);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(sm);
            Debug.Log($"[2Chars] 캐릭터 2명 유지 · Switcher 세팅 완료 (플레이 모드 → 우상단 버튼)");
        }

        static void _SetFieldSafe(System.Type t, object target, string name, object value)
        {
            var f = t.GetField(name);
            if (f == null) { Debug.LogWarning($"[2Chars] {t.Name}.{name} 필드 없음 — skip"); return; }
            f.SetValue(target, value);
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · Clean Scene2 duplicates + set Input Both")]
        static void DevCleanAndFixInput()
        {
            // 1) Input System = Both (legacy + new) — Invector 가 legacy Input 쓰므로
            UnityEditor.PlayerSettings.SetAdditionalIl2CppArgs("");
            // Unity 6 LTS 에서 ActiveInputHandler 는 internal API — SerializedObject 로 접근
            var playerSettingsObj = UnityEditor.AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            foreach (var o in playerSettingsObj)
            {
                if (o == null) continue;
                var so = new SerializedObject(o);
                var prop = so.FindProperty("activeInputHandler");
                if (prop != null)
                {
                    prop.intValue = 2; // 0=Legacy, 1=New, 2=Both
                    so.ApplyModifiedProperties();
                    Debug.Log("[Clean] activeInputHandler = Both (2)");
                }
            }

            // 2) 씬 중복 제거: 같은 이름의 root GameObject 가 여러 개면 첫 번째만 남기고 삭제
            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            var roots = sm.GetRootGameObjects();
            var seen = new System.Collections.Generic.HashSet<string>();
            int removed = 0;
            foreach (var go in roots)
            {
                if (seen.Contains(go.name))
                {
                    Debug.Log($"[Clean] 중복 제거: {go.name}");
                    Undo.DestroyObjectImmediate(go);
                    removed++;
                }
                else seen.Add(go.name);
            }

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(sm);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(sm);
            Debug.Log($"[Clean] {removed} 중복 오브젝트 삭제 · 이제 Unity 재시작 후 Input 설정 적용됨 (또는 Edit > Project Settings > Player > Active Input Handling 수동 확인)");
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · Reduce heavy splats to LOD 2 (Facility_01/Middle)")]
        static void DevReduceHeavySplats()
        {
            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            int n = 0;
            foreach (var go in sm.GetRootGameObjects())
            {
                if (!go.name.StartsWith("Splat_")) continue;
                var splat = go.GetComponent<Virnect.Lcc.LccSplatRenderer>();
                if (splat == null) continue;
                Undo.RecordObject(splat, "Reduce LOD");
                splat.lodLevel = 2;
                splat.enabled = false; splat.enabled = true;
                EditorUtility.SetDirty(splat);
                n++;
            }
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(sm);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(sm);
            Debug.Log($"[Reduce] {n} heavy splats → LOD 2 (대폭 경량화)");
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · Spawn ThirdPerson characters on all Splat+Mesh (6 chars)")]
        static void DevSpawnCharactersOnAll()
        {
            const string playerPrefabPath = "Assets/Invector-3rdPersonController_LITE/Prefabs/ThirdPersonController_LITE.prefab";
            const string camPrefabPath    = "Assets/Invector-3rdPersonController_LITE/Prefabs/vThirdPersonCamera_LITE.prefab";
            var playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(playerPrefabPath);
            var camPrefab    = AssetDatabase.LoadAssetAtPath<GameObject>(camPrefabPath);
            if (playerPrefab == null) { Debug.LogError("[Chars] ThirdPerson prefab 없음"); return; }

            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();

            // 기존 Player/Camera 제거
            foreach (var root in sm.GetRootGameObjects())
            {
                if (root.name.StartsWith("Player_") || root.name.StartsWith("Cam_") ||
                    root.name == "ThirdPersonController_LITE" || root.name == "vThirdPersonCamera_LITE")
                    Undo.DestroyObjectImmediate(root);
            }

            GameObject firstPlayer = null;
            int n = 0;
            foreach (var go in sm.GetRootGameObjects())
            {
                if (!go.name.StartsWith("Splat_") && !go.name.StartsWith("Mesh_")) continue;
                var mr = go.GetComponent<MeshRenderer>();
                if (mr == null) continue;
                // Collider 확보: Mesh_ 는 직접, Splat_ 는 __LccCollider 자식에 — 이미 있으면 skip
                if (go.name.StartsWith("Mesh_") && go.GetComponent<MeshCollider>() == null)
                {
                    var mf = go.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null)
                        go.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
                }
                // Splat 쪽 __LccCollider 는 Rebuild 가 이미 만들어둠 (없으면 skip)

                // 캐릭터 스폰 위치 = bounds xz 중심 위에서 Raycast down → 실제 바닥 collider hit + 1.2m
                Vector3 origin = new Vector3(mr.bounds.center.x, mr.bounds.max.y + 5f, mr.bounds.center.z);
                Vector3 spawn;
                if (Physics.Raycast(origin, Vector3.down, out var hit, mr.bounds.size.y + 20f, ~0, QueryTriggerInteraction.Ignore))
                    spawn = hit.point + Vector3.up * 1.2f;
                else
                    spawn = new Vector3(mr.bounds.center.x, mr.bounds.min.y + 1.2f, mr.bounds.center.z);

                var player = (GameObject)PrefabUtility.InstantiatePrefab(playerPrefab);
                player.name = "Player_" + go.name;
                player.transform.position = spawn;
                Undo.RegisterCreatedObjectUndo(player, "Spawn Char");

                if (firstPlayer == null) firstPlayer = player;
                n++;
                Debug.Log($"[Chars] {player.name} @ {spawn}");
            }

            // 한 개의 ThirdPersonCamera 만 firstPlayer 따라감 (여러 개 두면 Main Camera 충돌)
            if (camPrefab != null && firstPlayer != null)
            {
                var cam = (GameObject)PrefabUtility.InstantiatePrefab(camPrefab);
                cam.name = "Cam_Shared";
                Undo.RegisterCreatedObjectUndo(cam, "Spawn Cam");
                foreach (var mb in cam.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb != null && mb.GetType().Name == "vThirdPersonCamera")
                    {
                        var so = new SerializedObject(mb);
                        var target = so.FindProperty("target");
                        if (target != null) { target.objectReferenceValue = firstPlayer.transform; so.ApplyModifiedProperties(); }
                        break;
                    }
                }
                var oldMain = Camera.main;
                if (oldMain != null && !oldMain.transform.IsChildOf(cam.transform))
                    oldMain.gameObject.SetActive(false);
            }

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(sm);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(sm);
            Debug.Log($"[Chars] {n} characters spawned · first player= {(firstPlayer?firstPlayer.name:"none")}");
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · Mirror Splat → Mesh (bounds 기준 3m gap, X+)")]
        static void DevMirrorSplatToMesh()
        {
            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            var roots = sm.GetRootGameObjects();
            int n = 0;
            foreach (var splatGO in roots)
            {
                if (!splatGO.name.StartsWith("Splat_")) continue;
                string sceneName = splatGO.name.Substring("Splat_".Length);
                string meshName = "Mesh_" + sceneName;
                var meshGO = System.Array.Find(roots, g => g.name == meshName);
                if (meshGO == null) { Debug.LogWarning($"[Mirror] {meshName} 없음"); continue; }

                // bounds 기반 3m gap 계산 — splat 의 world bounds max.x + 3m 에 mesh min.x 정렬
                var splatMR = splatGO.GetComponent<MeshRenderer>();
                var meshMR  = meshGO.GetComponent<MeshRenderer>();
                if (splatMR == null || meshMR == null) continue;

                // mesh 의 MeshRenderer 가 꺼져 있으면 bounds 업데이트를 위해 잠시 켬
                bool prev = meshMR.enabled; if (!prev) meshMR.enabled = true;

                // splat 과 같은 rotation 먼저 적용 (mesh bounds 를 splat 좌표계에 맞춰 계산)
                Undo.RecordObject(meshGO.transform, "Mirror splat→mesh");
                meshGO.transform.rotation = splatGO.transform.rotation;

                // 현재 mesh 의 world bounds 중심과 splat world bounds 기준으로 delta 계산
                float splatMaxX = splatMR.bounds.max.x;
                float meshHalfX = meshMR.bounds.extents.x;
                float meshCenterX = meshMR.bounds.center.x;
                float desiredMeshCenterX = splatMaxX + 3f + meshHalfX;
                float deltaX = desiredMeshCenterX - meshCenterX;
                // Y,Z 는 splat 과 맞추기 위해 mesh 중심 Y/Z 를 splat 중심에 정렬 (지면, 깊이)
                float splatCenterY = splatMR.bounds.center.y;
                float splatCenterZ = splatMR.bounds.center.z;
                float meshCenterY = meshMR.bounds.center.y;
                float meshCenterZ = meshMR.bounds.center.z;
                float deltaY = splatCenterY - meshCenterY;
                float deltaZ = splatCenterZ - meshCenterZ;

                meshGO.transform.position += new Vector3(deltaX, deltaY, deltaZ);

                Undo.RecordObject(meshMR, "Enable Mesh");
                meshMR.enabled = true;
                EditorUtility.SetDirty(meshGO);
                n++;
                Debug.Log($"[Mirror] {meshName} → pos={meshGO.transform.position} · splat.max.x={splatMaxX:F1} mesh.halfX={meshHalfX:F1} gap=3m");
            }
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(sm);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(sm);
            Debug.Log($"[Mirror] {n} Mesh 배치 완료");
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · Capture Scene View (top-down, 1920×1080)")]
        static void DevCaptureSceneViewTopDown()
        {
            Bounds all = _AllSplatBounds(out bool any);
            if (!any) { Debug.LogError("[Capture] no Splat_ objects"); return; }
            // orthoSize = half of vertical view (Z-axis for top-down). 화면 16:9 고려해 X 축 fit 도 체크.
            float halfZ = all.extents.z * 1.1f;
            float halfX = all.extents.x * 1.1f / (1920f / 1080f);
            float orthoSize = Mathf.Max(halfZ, halfX);
            var camPos = new Vector3(all.center.x, all.max.y + 10f, all.center.z);
            var camRot = Quaternion.Euler(90f, 0f, 0f);
            _RenderView("Library/tmp_scene_topdown.png", camPos, camRot, 1000f, 1920, 1080, true, orthoSize);
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · Capture Scene View (3/4 persp, 1920×1080)")]
        static void DevCaptureSceneView34()
        {
            Bounds all = _AllSplatBounds(out bool any);
            if (!any) { Debug.LogError("[Capture] no Splat_ objects"); return; }
            float radius = all.extents.magnitude;
            // 45° 내려다보기 + 앞에서 들어오기
            var dir = Quaternion.Euler(45f, 0f, 0f) * Vector3.forward;  // 위에서 45° 앞아래
            var camPos = all.center - dir * radius * 2.2f;
            var camRot = Quaternion.LookRotation(all.center - camPos, Vector3.up);
            _RenderView("Library/tmp_scene_persp.png", camPos, camRot, radius * 5f, 1920, 1080, false, radius);
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · Capture Scene View (front, 1920×1080)")]
        static void DevCaptureSceneViewFront()
        {
            Bounds all = _AllSplatBounds(out bool any);
            if (!any) { Debug.LogError("[Capture] no Splat_ objects"); return; }
            float halfY = all.extents.y * 1.1f;
            float halfX = all.extents.x * 1.1f / (1920f / 1080f);
            float orthoSize = Mathf.Max(halfY, halfX);
            var camPos = new Vector3(all.center.x, all.center.y, all.min.z - 10f);
            var camRot = Quaternion.identity;
            _RenderView("Library/tmp_scene_front.png", camPos, camRot, 1000f, 1920, 1080, true, orthoSize);
        }

        static Bounds _AllSplatBounds(out bool any)
        {
            Bounds all = new Bounds();
            any = false;
            foreach (var go in UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().GetRootGameObjects())
            {
                // Splat_ + Mesh_ 둘 다 포함
                if (!go.name.StartsWith("Splat_") && !go.name.StartsWith("Mesh_")) continue;
                var mr = go.GetComponent<MeshRenderer>();
                if (mr == null || !mr.enabled) continue;
                if (!any) { all = mr.bounds; any = true; }
                else all.Encapsulate(mr.bounds);
            }
            return all;
        }

        // Scene view 카메라 의존 없이 임시 Camera GameObject 로 직접 렌더.
        static void _RenderView(string relPath, Vector3 camPos, Quaternion camRot, float farDist, int w, int h, bool ortho, float orthoSize)
        {
            GameObject camGO = null;
            RenderTexture rt = null;
            Texture2D tex = null;
            var prevActive = RenderTexture.active;
            try
            {
                camGO = new GameObject("__CaptureCamera");
                camGO.hideFlags = HideFlags.HideAndDontSave;
                var cam = camGO.AddComponent<Camera>();
                cam.transform.position = camPos;
                cam.transform.rotation = camRot;
                cam.orthographic = ortho;
                cam.orthographicSize = orthoSize;
                cam.fieldOfView = 60f;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = Mathf.Max(1000f, farDist * 3f);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.15f, 0.15f, 0.18f);
                cam.cullingMask = -1;

                // URP: 명시적으로 renderer index 0 (PC_Renderer) 지정 — Aras-P GaussianSplatURPFeature가 이 renderer에 붙어있음
                var urpAddCamDataType = System.Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
                if (urpAddCamDataType != null)
                {
                    var addData = camGO.AddComponent(urpAddCamDataType);
                    var setRendererMethod = urpAddCamDataType.GetMethod("SetRenderer", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (setRendererMethod != null) setRendererMethod.Invoke(addData, new object[] { 0 });
                }

                rt = new RenderTexture(w, h, 24);
                cam.targetTexture = rt;
                // Aras-P warmup: 첫 Render에서 자체 등록/리소스 prep, 두 번째 Render에서 실제 splat 출력
                cam.Render();
                cam.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                var bytes = tex.EncodeToPNG();
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(relPath));
                System.IO.File.WriteAllBytes(relPath, bytes);
                Debug.Log($"[Capture] saved {relPath} ({bytes.Length / 1024} KB) · camPos={camPos} rot={camRot.eulerAngles} ortho={ortho}/{orthoSize:F1}");

                // RT leak fix: targetTexture 해제 → RT.Release → DestroyImmediate
                cam.targetTexture = null;
            }
            catch (System.Exception e) { Debug.LogError($"[Capture] {e.Message}\n{e.StackTrace}"); }
            finally
            {
                RenderTexture.active = prevActive;
                if (tex != null) Object.DestroyImmediate(tex);
                if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
                if (camGO != null) Object.DestroyImmediate(camGO);
            }
            return;
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · Test ICP (Facility_01 ← 1st_Cutter · Coarse→Fine)")]
        static void DevTestIcp1st()
        {
            var b = AssetDatabase.LoadAssetAtPath<LccScene>("Assets/LCC_Drops/ShinWon_Facility_01/ShinWon_Facility_01.lcc");
            var t = AssetDatabase.LoadAssetAtPath<LccScene>("Assets/LCC_Drops/ShinWon_1st_Cutter/ShinWon_1st_Cutter.lcc");
            _RunIcpPair(b, t);
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · Test ICP BRUTE (Facility_01 ← 1st_Cutter · 4-rotation search)")]
        static void DevTestIcp1stBrute()
        {
            var b = AssetDatabase.LoadAssetAtPath<LccScene>("Assets/LCC_Drops/ShinWon_Facility_01/ShinWon_Facility_01.lcc");
            var t = AssetDatabase.LoadAssetAtPath<LccScene>("Assets/LCC_Drops/ShinWon_1st_Cutter/ShinWon_1st_Cutter.lcc");
            _RunIcpBrute(b, t);
        }
        [MenuItem("Tools/Lcc Drop Forge/Dev · Test ICP BRUTE (Facility_01 ← Middle · 4-rotation search)")]
        static void DevTestIcpMiddleBrute()
        {
            var b = AssetDatabase.LoadAssetAtPath<LccScene>("Assets/LCC_Drops/ShinWon_Facility_01/ShinWon_Facility_01.lcc");
            var t = AssetDatabase.LoadAssetAtPath<LccScene>("Assets/LCC_Drops/ShinWon_Facility_Middle/ShinWon_Facility_Middle.lcc");
            _RunIcpBrute(b, t);
        }

        static void _RunIcpBrute(LccScene b, LccScene t)
        {
            if (b == null || t == null) { Debug.LogError("[ICP Brute] LccScene 없음"); return; }
            string bPly = b.ResolveProxyMeshPlyAssetPath();
            string tPly = t.ResolveProxyMeshPlyAssetPath();
            if (string.IsNullOrEmpty(bPly) || string.IsNullOrEmpty(tPly)) { Debug.LogError("[ICP Brute] proxy PLY 없음"); return; }

            var baseMesh   = Virnect.Lcc.LccMeshPlyLoader.Load(System.IO.Path.GetFullPath(bPly));
            var targetMesh = Virnect.Lcc.LccMeshPlyLoader.Load(System.IO.Path.GetFullPath(tPly));
            Vector3[] srcP = targetMesh.vertices;
            Vector3[] tgtP = baseMesh.vertices;

            // proxy PLY 는 LCC 로컬 (Z-up) 좌표계. Z 축 = 수직축.
            // 평면 상의 회전 candidate 4개 (0/90/180/270 around Z) 로 coarse ICP → 최저 RMSE 선택
            float[] zAngles = { 0f, 90f, 180f, 270f };
            Virnect.Lcc.LccPointCloudRegistration.Result best = default;
            float bestRmse = float.PositiveInfinity;
            float bestAngle = 0f;
            foreach (var ang in zAngles)
            {
                // rotation around Z (수직축) by ang degrees, then identity translation
                Matrix4x4 init = Matrix4x4.Rotate(Quaternion.Euler(0f, 0f, ang));
                var r = Virnect.Lcc.LccPointCloudRegistration.Align(srcP, tgtP,
                    Virnect.Lcc.LccPointCloudRegistration.Options.Coarse, init);
                Debug.Log($"[ICP Brute]   Z={ang:F0}°  coarse iter={r.iterations} rmse {r.rmseBefore:F2}→{r.rmseAfter:F2}m matched={r.correspondences}");
                if (r.rmseAfter < bestRmse) { bestRmse = r.rmseAfter; best = r; bestAngle = ang; }
            }
            Debug.Log($"[ICP Brute] 🏆 best initial Z={bestAngle:F0}°  rmse {bestRmse:F2}m");

            // Fine refine
            var rF = Virnect.Lcc.LccPointCloudRegistration.Align(srcP, tgtP,
                Virnect.Lcc.LccPointCloudRegistration.Options.Fine, best.transform);
            Debug.Log($"[ICP Brute] Fine: iter={rF.iterations} rmse {rF.rmseBefore:F2}→{rF.rmseAfter:F2}m matched={rF.correspondences} converged={rF.converged}");

            // Apply
            Matrix4x4 M = rF.transform;
            Vector3 pos = new Vector3(M.m03, M.m13, M.m23);
            Quaternion rot = M.rotation;
            var zUpToYUp = Quaternion.Euler(-90f, 0f, 0f);
            var worldPos = zUpToYUp * pos;
            var worldRot = zUpToYUp * rot;
            int applied = 0;
            foreach (var go in UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (go.name == "Splat_" + t.name || go.name == "Mesh_" + t.name)
                {
                    Undo.RecordObject(go.transform, "ICP Brute Align");
                    go.transform.position = worldPos;
                    go.transform.rotation = worldRot;
                    EditorUtility.SetDirty(go);
                    applied++;
                }
            }
            if (applied > 0)
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                    UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
                Debug.Log($"[ICP Brute] {applied} GameObjects updated · final Δpos={pos} Δeuler={rot.eulerAngles}");
            }
        }
        [MenuItem("Tools/Lcc Drop Forge/Dev · Test ICP (Facility_01 ← Middle · Coarse→Fine)")]
        static void DevTestIcpMiddle()
        {
            var b = AssetDatabase.LoadAssetAtPath<LccScene>("Assets/LCC_Drops/ShinWon_Facility_01/ShinWon_Facility_01.lcc");
            var t = AssetDatabase.LoadAssetAtPath<LccScene>("Assets/LCC_Drops/ShinWon_Facility_Middle/ShinWon_Facility_Middle.lcc");
            _RunIcpPair(b, t);
        }
        static void _RunIcpPair(LccScene b, LccScene t)
        {
            if (b == null || t == null) { Debug.LogError("[ICP Test] LccScene 없음"); return; }
            string bPly = b.ResolveProxyMeshPlyAssetPath();
            string tPly = t.ResolveProxyMeshPlyAssetPath();
            if (string.IsNullOrEmpty(bPly) || string.IsNullOrEmpty(tPly)) { Debug.LogError("[ICP Test] proxy PLY 없음"); return; }

            double t0 = UnityEditor.EditorApplication.timeSinceStartup;
            var baseMesh   = Virnect.Lcc.LccMeshPlyLoader.Load(System.IO.Path.GetFullPath(bPly));
            var targetMesh = Virnect.Lcc.LccMeshPlyLoader.Load(System.IO.Path.GetFullPath(tPly));
            double t1 = UnityEditor.EditorApplication.timeSinceStartup;

            Vector3[] srcP = targetMesh.vertices;
            Vector3[] tgtP = baseMesh.vertices;

            var rC = Virnect.Lcc.LccPointCloudRegistration.Align(srcP, tgtP,
                Virnect.Lcc.LccPointCloudRegistration.Options.Coarse, Matrix4x4.identity);
            var rF = Virnect.Lcc.LccPointCloudRegistration.Align(srcP, tgtP,
                Virnect.Lcc.LccPointCloudRegistration.Options.Fine, rC.transform);
            double t2 = UnityEditor.EditorApplication.timeSinceStartup;

            Matrix4x4 M = rF.transform;
            Vector3 pos = new Vector3(M.m03, M.m13, M.m23);
            Quaternion rot = M.rotation;

            Debug.Log($"[ICP Test] {t.name} → {b.name}\n" +
                $"  load {(t1-t0)*1000:F0}ms · icp total {(t2-t1)*1000:F0}ms\n" +
                $"  Coarse: iter={rC.iterations} rmse {rC.rmseBefore:F2}→{rC.rmseAfter:F2}m matched={rC.correspondences}\n" +
                $"  Fine:   iter={rF.iterations} rmse {rF.rmseBefore:F2}→{rF.rmseAfter:F2}m matched={rF.correspondences} converged={rF.converged}\n" +
                $"  Δpos={pos}  Δeuler={rot.eulerAngles}");

            // Apply to scene objects: LCC-local point p → world: p_world = zUpToYUp * (R * p + t)
            //   → worldRot = zUpToYUp * icpRot, worldPos = zUpToYUp * icpPos
            //   (기존 씬 rotation 은 zUpToYUp 이었으므로 icpRot=I 일 때 변화 없음)
            var zUpToYUp = Quaternion.Euler(-90f, 0f, 0f);
            var worldPos = zUpToYUp * pos;
            var worldRot = zUpToYUp * rot;
            int applied = 0;
            foreach (var go in UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (go.name == "Splat_" + t.name || go.name == "Mesh_" + t.name)
                {
                    Undo.RecordObject(go.transform, "ICP Align Test");
                    go.transform.position = worldPos;
                    go.transform.rotation = worldRot;
                    EditorUtility.SetDirty(go);
                    applied++;
                }
            }
            if (applied > 0)
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                    UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
                Debug.Log($"[ICP Test] Scene2 GameObject {applied}개 적용 완료");
            }
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · Rebuild Scene2 (ALL LCC scenes · splat+mesh pairs)")]
        static void DevRebuildScene2All()
        {
            // LCC_Drops 의 모든 LccScene 자동 발견 + 각각 colored mesh 빌드 + 배치
            var guids = AssetDatabase.FindAssets("t:LccScene", new[] { "Assets/LCC_Drops" });
            if (guids.Length == 0) { Debug.LogError("[Scene2 Rebuild] LccScene 없음"); return; }
            var scenes = new System.Collections.Generic.List<LccScene>();
            foreach (var g in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                var s = AssetDatabase.LoadAssetAtPath<LccScene>(p);
                if (s != null) scenes.Add(s);
            }
            Debug.Log($"[Scene2 Rebuild] 발견된 LccScene: {scenes.Count}개 — {string.Join(", ", scenes.ConvertAll(s => s.name))}");

            string outDir = "Assets/LCC_Generated";
            System.IO.Directory.CreateDirectory(outDir);

            // 각 씬별로 proxy mesh + colored mesh asset (없으면 생성, 있으면 재사용)
            var proxyMeshes   = new System.Collections.Generic.Dictionary<string, UnityEngine.Mesh>();
            var coloredMeshes = new System.Collections.Generic.Dictionary<string, UnityEngine.Mesh>();
            var coloredMats   = new System.Collections.Generic.Dictionary<string, Material>();

            foreach (var s in scenes)
            {
                string proxyPath = $"{outDir}/{s.name}_ProxyMesh.asset";
                string coloredPath = $"{outDir}/{s.name}_ProxyMesh_Colored.asset";
                string matPath = $"{outDir}/{s.name}_ProxyMesh_Colored_Mat.mat";

                var proxy = AssetDatabase.LoadAssetAtPath<UnityEngine.Mesh>(proxyPath);
                if (proxy == null)
                {
                    string plyAssetPath = s.ResolveProxyMeshPlyAssetPath();
                    if (string.IsNullOrEmpty(plyAssetPath))
                    { Debug.LogWarning($"[{s.name}] proxy PLY 없음 — skip"); continue; }
                    proxy = Virnect.Lcc.LccMeshPlyLoader.Load(System.IO.Path.GetFullPath(plyAssetPath));
                    proxy.name = s.name + "_ProxyMesh";
                    AssetDatabase.CreateAsset(proxy, proxyPath);
                    Debug.Log($"[{s.name}] ProxyMesh 생성 · {proxy.vertexCount:N0} verts");
                }
                proxyMeshes[s.name] = proxy;

                var colored = AssetDatabase.LoadAssetAtPath<UnityEngine.Mesh>(coloredPath);
                if (colored == null)
                {
                    colored = new UnityEngine.Mesh { name = proxy.name + "_Colored", indexFormat = proxy.indexFormat };
                    colored.SetVertices(proxy.vertices);
                    colored.SetTriangles(proxy.triangles, 0, calculateBounds: true);

                    var opts = Virnect.Lcc.LccMeshColorizer.Options.PhotoReal;
                    double t0 = UnityEditor.EditorApplication.timeSinceStartup;
                    var splats = Virnect.Lcc.LccSplatDecoder.DecodeLod(s, 0);
                    double t1 = UnityEditor.EditorApplication.timeSinceStartup;
                    Virnect.Lcc.LccMeshColorizer.Colorize(colored, splats, opts);
                    double t2 = UnityEditor.EditorApplication.timeSinceStartup;

                    AssetDatabase.CreateAsset(colored, coloredPath);
                    Debug.Log($"[{s.name}] ColoredMesh 생성 · decode {(t1-t0)*1000:F0}ms · colorize {(t2-t1)*1000:F0}ms");
                }
                coloredMeshes[s.name] = colored;

                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null)
                {
                    var shader = Shader.Find("Virnect/LccVertexColorUnlit") ?? Shader.Find("Universal Render Pipeline/Unlit");
                    mat = new Material(shader) { name = s.name + "_ColoredMat" };
                    AssetDatabase.CreateAsset(mat, matPath);
                }
                coloredMats[s.name] = mat;
            }
            AssetDatabase.SaveAssets(); AssetDatabase.Refresh();

            // Scene2 로드 (없으면 생성)
            string scene2Path = "Assets/Scenes/Scene2_MeshVsSplat.unity";
            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            if (sm.path != scene2Path)
            {
                if (sm.isDirty) UnityEditor.SceneManagement.EditorSceneManager.SaveScene(sm);
                if (System.IO.File.Exists(scene2Path))
                    UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scene2Path);
                else
                {
                    var ns = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                        UnityEditor.SceneManagement.NewSceneSetup.DefaultGameObjects,
                        UnityEditor.SceneManagement.NewSceneMode.Single);
                    System.IO.Directory.CreateDirectory("Assets/Scenes");
                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(ns, scene2Path);
                }
                sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            }

            // 기존 Splat_/Mesh_ 오브젝트 제거 (이름 prefix 기반 — 멀티 scene 대응)
            foreach (var root in sm.GetRootGameObjects())
            {
                if (root.name.StartsWith("Splat_") || root.name.StartsWith("Mesh_"))
                    Undo.DestroyObjectImmediate(root);
            }

            // 월드 정합 배치 — 모든 LCC 가 동일한 1공장 월드 좌표계를 공유하므로 원점 그대로.
            // splat/mesh 는 같은 위치에 겹쳐 올라가고 시각 비교는 MeshRenderer toggle 로.
            // 뷰 선택 편의를 위해 각 씬 Splat/Mesh 는 별도 root GameObject 로 group.
            Bounds allBounds = new Bounds();
            bool hasBounds = false;
            foreach (var s in scenes)
            {
                if (!proxyMeshes.ContainsKey(s.name)) continue;
                var proxy = proxyMeshes[s.name];
                var colored = coloredMeshes[s.name];
                var mat = coloredMats[s.name];

                // Splat (좌/우 offset 없음 — 월드 원점)
                var splatGO = new GameObject("Splat_" + s.name);
                splatGO.transform.position = Vector3.zero;
                splatGO.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
                var splat = splatGO.AddComponent<Virnect.Lcc.LccSplatRenderer>();
                splat.scene = s;
                splat.lodLevel = 0;
                splat.scaleMultiplier = 1.5f;
                splat.opacityBoost = 0f;
                splat.tint = Color.white;
                splat.enabled = false; splat.enabled = true;
                var splatCol = new GameObject("__LccCollider");
                splatCol.transform.SetParent(splatGO.transform, false);
                splatCol.AddComponent<MeshCollider>().sharedMesh = proxy;
                Undo.RegisterCreatedObjectUndo(splatGO, "Scene2 · Splat");

                // ColoredMesh (동일 위치 · 시각 비교용)
                var meshGO = new GameObject("Mesh_" + s.name);
                meshGO.transform.position = Vector3.zero;
                meshGO.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
                meshGO.AddComponent<MeshFilter>().sharedMesh = colored;
                meshGO.AddComponent<MeshRenderer>().sharedMaterial = mat;
                meshGO.AddComponent<MeshCollider>().sharedMesh = colored;
                Undo.RegisterCreatedObjectUndo(meshGO, "Scene2 · ColoredMesh");

                // 씬 전체 월드 bounds 추적 (카메라 프레이밍용)
                var b = colored.bounds; // local bounds — rotation 후 world y/z swap
                var worldCenter = meshGO.transform.TransformPoint(b.center);
                var worldSize = new Vector3(b.size.x, b.size.z, b.size.y); // Z-up→Y-up swap
                var worldB = new Bounds(worldCenter, worldSize);
                if (!hasBounds) { allBounds = worldB; hasBounds = true; }
                else allBounds.Encapsulate(worldB);
            }

            // 카메라 전체 프레이밍 — 합친 bounds 기준
            var cam = Camera.main;
            if (cam != null && hasBounds)
            {
                float maxExt = Mathf.Max(allBounds.extents.x, allBounds.extents.y, allBounds.extents.z);
                float dist = maxExt / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.5f;
                cam.transform.position = allBounds.center + new Vector3(0f, maxExt * 0.5f, -dist);
                cam.transform.LookAt(allBounds.center);
                cam.nearClipPlane = Mathf.Max(0.1f, dist * 0.001f);
                cam.farClipPlane = Mathf.Max(500f, dist * 5f);
            }

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(sm);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(sm);
            Debug.Log($"[Scene2 Rebuild] {scenes.Count} LCC scenes 배치 완료");
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · Rebuild Scene2 (both 1m apart + colliders)")]
        static void DevRebuildScene2()
        {
            const string lccPath = "Assets/LCC_Drops/ShinWon_1st_Cutter/ShinWon_1st_Cutter.lcc";
            var lccScene = AssetDatabase.LoadAssetAtPath<LccScene>(lccPath);
            if (lccScene == null) { Debug.LogError("[Scene2 Rebuild] LccScene not found"); return; }

            string coloredMeshPath = "Assets/LCC_Generated/ShinWon_1st_Cutter_ProxyMesh_Colored.asset";
            string coloredMatPath  = "Assets/LCC_Generated/ShinWon_1st_Cutter_ProxyMesh_Colored_Mat.mat";
            string proxyMeshPath   = "Assets/LCC_Generated/ShinWon_1st_Cutter_ProxyMesh.asset";
            var coloredMesh = AssetDatabase.LoadAssetAtPath<UnityEngine.Mesh>(coloredMeshPath);
            var coloredMat  = AssetDatabase.LoadAssetAtPath<Material>(coloredMatPath);
            var proxyMesh   = AssetDatabase.LoadAssetAtPath<UnityEngine.Mesh>(proxyMeshPath);
            if (coloredMesh == null || coloredMat == null || proxyMesh == null)
            { Debug.LogError("[Scene2 Rebuild] 에셋 누락 — 먼저 'Build Colored Mesh Asset' 메뉴 실행"); return; }

            // 이미 Scene2 에 있다면 재사용. 아니면 새로 만듦.
            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            if (sm.path != "Assets/Scenes/Scene2_MeshVsSplat.unity")
            {
                if (sm.isDirty) UnityEditor.SceneManagement.EditorSceneManager.SaveScene(sm);
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/Scene2_MeshVsSplat.unity");
                sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            }

            // 기존 Splat/Mesh 오브젝트 제거
            foreach (var root in sm.GetRootGameObjects())
            {
                if (root.name == "Splat_Original" || root.name == "ColoredMesh_PhotoReal")
                    Undo.DestroyObjectImmediate(root);
            }

            // mesh bounds (proxy) — 35m wide on X after -90X rotation 기준으로 1m gap 간격 계산
            var b = proxyMesh.bounds; // local, pre-rotation
            // 로테이션 -90X 후의 world X extent 는 local X extent 와 같음
            float halfX = b.extents.x;
            float offsetX = halfX + 0.5f; // 각 오브젝트가 0.5m 씩 벌어져 centers 사이 거리 = 2*offsetX = mesh width + 1m

            // 1) Splat_Original — 좌측
            var splatGO = new GameObject("Splat_Original");
            splatGO.transform.position = new Vector3(-offsetX, 0f, 0f);
            splatGO.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
            var splat = splatGO.AddComponent<Virnect.Lcc.LccSplatRenderer>();
            splat.scene = lccScene;
            splat.lodLevel = 0;
            splat.scaleMultiplier = 1.5f;
            splat.opacityBoost = 0f;
            splat.tint = Color.white;
            splat.enabled = false; splat.enabled = true;
            // splat 에도 collider — 자식으로 (로테이션 자동 상속)
            var splatColGO = new GameObject("__LccCollider");
            splatColGO.transform.SetParent(splatGO.transform, false);
            var splatMC = splatColGO.AddComponent<MeshCollider>();
            splatMC.sharedMesh = proxyMesh;
            Undo.RegisterCreatedObjectUndo(splatGO, "Scene2 · Splat");

            // 2) ColoredMesh_PhotoReal — 우측
            var meshGO = new GameObject("ColoredMesh_PhotoReal");
            meshGO.transform.position = new Vector3(+offsetX, 0f, 0f);
            meshGO.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
            var mf = meshGO.AddComponent<MeshFilter>();
            mf.sharedMesh = coloredMesh;
            var mr = meshGO.AddComponent<MeshRenderer>();
            mr.sharedMaterial = coloredMat;
            var meshMC = meshGO.AddComponent<MeshCollider>();
            meshMC.sharedMesh = coloredMesh;
            Undo.RegisterCreatedObjectUndo(meshGO, "Scene2 · ColoredMesh");

            // Camera 프레이밍
            var cam = Camera.main;
            if (cam != null)
            {
                float dist = halfX * 4f;
                cam.transform.position = new Vector3(0f, 20f, -dist);
                cam.transform.LookAt(new Vector3(0f, 10f, 0f));
                cam.farClipPlane = Mathf.Max(500f, dist * 4f);
            }

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(sm);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(sm);

            Debug.Log($"[Scene2 Rebuild] offsetX={offsetX:F2}m (mesh halfX={halfX:F2}m + 0.5m gap) · both rotated -90°X · colliders attached.");
            Selection.activeGameObject = meshGO;
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · Build Scene2 (Mesh + LCC side-by-side)")]
        static void DevBuildScene2()
        {
            const string lccPath = "Assets/LCC_Drops/ShinWon_1st_Cutter/ShinWon_1st_Cutter.lcc";
            var lccScene = AssetDatabase.LoadAssetAtPath<LccScene>(lccPath);
            if (lccScene == null) { Debug.LogError($"[Scene2] LccScene not found"); return; }

            string coloredMeshPath = "Assets/LCC_Generated/ShinWon_1st_Cutter_ProxyMesh_Colored.asset";
            string coloredMatPath  = "Assets/LCC_Generated/ShinWon_1st_Cutter_ProxyMesh_Colored_Mat.mat";
            var coloredMesh = AssetDatabase.LoadAssetAtPath<UnityEngine.Mesh>(coloredMeshPath);
            var coloredMat  = AssetDatabase.LoadAssetAtPath<Material>(coloredMatPath);
            if (coloredMesh == null || coloredMat == null)
            {
                Debug.LogError("[Scene2] Colored mesh/mat 없음 — 먼저 'Build Colored Mesh Asset' 메뉴 실행");
                return;
            }

            // Save current scene if dirty → 새 씬
            var sm = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            if (sm.isDirty) UnityEditor.SceneManagement.EditorSceneManager.SaveScene(sm);

            var s2 = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.DefaultGameObjects,
                UnityEditor.SceneManagement.NewSceneMode.Single);

            System.IO.Directory.CreateDirectory("Assets/Scenes");
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(s2, "Assets/Scenes/Scene2_MeshVsSplat.unity");

            // LCC splat — 좌측
            var splatGO = new GameObject("Splat_Original");
            splatGO.transform.position = new Vector3(-40f, 0f, 0f);
            splatGO.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
            var splat = splatGO.AddComponent<Virnect.Lcc.LccSplatRenderer>();
            splat.scene = lccScene;
            splat.lodLevel = 0;
            splat.scaleMultiplier = 1.5f;
            splat.opacityBoost = 0f;
            splat.tint = Color.white;
            splat.enabled = false; splat.enabled = true;

            // Colored Mesh — 우측
            var meshGO = new GameObject("ColoredMesh_PhotoReal");
            meshGO.transform.position = new Vector3(40f, 0f, 0f);
            meshGO.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
            var mf = meshGO.AddComponent<MeshFilter>();
            mf.sharedMesh = coloredMesh;
            var mr = meshGO.AddComponent<MeshRenderer>();
            mr.sharedMaterial = coloredMat;

            // 카메라 중앙 프레이밍
            var cam = Camera.main;
            if (cam != null)
            {
                cam.transform.position = new Vector3(0f, 20f, -80f);
                cam.transform.LookAt(new Vector3(0f, 10f, 0f));
                cam.farClipPlane = 500f;
            }

            Selection.activeGameObject = meshGO;
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(s2);
            Debug.Log("[Scene2] Splat_Original (left, x=-40) + ColoredMesh_PhotoReal (right, x=+40) · saved Assets/Scenes/Scene2_MeshVsSplat.unity");
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · Auto-Launch Playground (ShinWon · ✨ PHOTO-REAL)")]
        static void DevAutoLaunchShinWonPhotoReal()
        {
            const string path = "Assets/LCC_Drops/ShinWon_1st_Cutter/ShinWon_1st_Cutter.lcc";
            var scene = AssetDatabase.LoadAssetAtPath<LccScene>(path);
            if (scene == null) { Debug.LogError($"[DevLauncher] LccScene not found at {path}"); return; }
            var go = LccPlaygroundWindow.QuickLaunch(
                scene,
                lodLevel: 0,
                addCollider: true,
                spawnCharacter: true,
                frameCamera: true,
                cleanLaunch: true,
                heightOffset: 1f,
                colorizeMesh: true,
                photoRealMode: true);   // 프리셋 내부에서 LOD0/k=3/subdiv=1/opacity/tight 세팅
            if (go != null) Selection.activeGameObject = go;
        }

        [MenuItem("Tools/Lcc Drop Forge/Dev · Auto-Launch Playground (ShinWon · Colored Mesh from EXTERNAL PLY)")]
        static void DevAutoLaunchShinWonColoredFromPly()
        {
            const string path = "Assets/LCC_Drops/ShinWon_1st_Cutter/ShinWon_1st_Cutter.lcc";
            const string plyAbs = @"C:/Users/VIRNECT/Desktop/lcc/ply/LCC/LCC/lcc-result/XGrids_Splats_LOD2.ply";
            var scene = AssetDatabase.LoadAssetAtPath<LccScene>(path);
            if (scene == null) { Debug.LogError($"[DevLauncher] LccScene not found at {path}"); return; }
            var go = LccPlaygroundWindow.QuickLaunch(
                scene,
                lodLevel: 0,
                addCollider: true,
                spawnCharacter: true,
                frameCamera: true,
                cleanLaunch: true,
                heightOffset: 1f,
                colorizeMesh: true,
                colorizerCellSize: 1.0f,
                colorizerK: 6,
                hideSplatAfterColorize: true,
                colorSource: LccPlaygroundWindow.ColorSourceMode.ExternalPly,
                externalPlyPath: plyAbs);
            if (go != null) Selection.activeGameObject = go;
        }


        [MenuItem("Tools/Lcc Drop Forge/Wire Virnect LccSplatRenderer From Selection")]
        static void WireFromSelection()
        {
            var lccScene = Selection.activeObject as LccScene;
            if (lccScene == null)
            {
                Debug.LogError("[Wirer] Select an LccScene asset (the .lcc file) in the Project window first.");
                return;
            }
            Wire(lccScene);
        }

        [MenuItem("Tools/Lcc Drop Forge/Wire Virnect Renderer — ShinWon_1st_Cutter")]
        static void WireShinWon()
        {
            const string path = "Assets/LCC_Drops/ShinWon_1st_Cutter/ShinWon_1st_Cutter.lcc";
            var lccScene = AssetDatabase.LoadAssetAtPath<LccScene>(path);
            if (lccScene == null)
            {
                Debug.LogError($"[Wirer] No LccScene found at {path}.");
                return;
            }
            Wire(lccScene);
        }

        static void Wire(LccScene lccScene)
        {
            string goName = $"Virnect_{lccScene.name}";
            var go = GameObject.Find(goName) ?? new GameObject(goName);

            // XGrids PortalCam exports with Z-up. Unity is Y-up.
            go.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);

            var renderer = go.GetComponent<LccSplatRenderer>();
            if (renderer == null) renderer = go.AddComponent<LccSplatRenderer>();

            renderer.scene = lccScene;
            renderer.lodLevel = 4;
            renderer.scaleMultiplier = 1.5f;
            renderer.opacityBoost = 0f;
            renderer.tint = Color.white;

            EditorUtility.SetDirty(renderer);

            // Trigger OnDisable → OnEnable so _TryLoad() runs with scene now assigned.
            renderer.enabled = false;
            renderer.enabled = true;

            FrameCameraOnBounds(renderer);

            EditorSceneManager.MarkSceneDirty(go.scene);
            Selection.activeGameObject = go;

            Debug.Log($"[Wirer] '{goName}' → LccSplatRenderer · scene='{lccScene.name}' · LOD {renderer.lodLevel}");
        }

        static void FrameCameraOnBounds(LccSplatRenderer r)
        {
            var cam = Camera.main;
            if (cam == null) return;

            var localBounds = r.GetWorldBounds();
            var worldCenter = r.transform.TransformPoint(localBounds.center);
            var worldExtent = r.transform.TransformVector(localBounds.extents);
            float maxExt = Mathf.Max(Mathf.Abs(worldExtent.x), Mathf.Abs(worldExtent.y), Mathf.Abs(worldExtent.z));
            float fitDist = maxExt / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.2f;

            cam.transform.position = worldCenter + new Vector3(0f, 0f, -fitDist);
            cam.transform.LookAt(worldCenter);
            cam.nearClipPlane = Mathf.Max(0.1f, fitDist * 0.001f);
            cam.farClipPlane  = Mathf.Max(1000f, fitDist * 10f);
            EditorUtility.SetDirty(cam);

            Debug.Log($"[Wirer] Camera framed: center={worldCenter} extent={maxExt:F2} dist={fitDist:F2}");
        }
    }
}
