using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Virnect.Lcc.Editor
{
    /// 공장 맵 (LCC 스플랫) + 콜라이더 + Third Person 캐릭터를 한 번에 스폰하는 런처.
    /// LccSceneInspector 의 "Launch Playground" 버튼에서 호출.
    public sealed class LccPlaygroundWindow : EditorWindow
    {
        const string ThirdPersonPrefabPath = "Assets/Invector-3rdPersonController_LITE/Prefabs/ThirdPersonController_LITE.prefab";
        const string ThirdPersonCameraPath = "Assets/Invector-3rdPersonController_LITE/Prefabs/vThirdPersonCamera_LITE.prefab";

        LccScene _target;

        [Range(0, 4)] int _lodLevel = 0;
        bool _addCollider = true;
        bool _colorizeMesh = true;
        int  _colorSourceLod = 3;         // 색상 소스 LOD — LOD3 (≈641K) 가 속도 대비 품질 좋음
        float _colorizerCellSize = 1.0f;  // voxel grid cell (m)
        int  _colorizerK = 6;             // k-NN 이웃 수
        bool _hideSplatAfterColorize = false;
        bool _spawnCharacter = true;
        bool _frameCamera = true;
        bool _cleanLaunch = true;
        float _heightOffset = 1.0f;

        // ───── Blue/White sci-fi palette ─────
        static readonly Color BgDark     = new(0.035f, 0.086f, 0.157f);  // #091628
        static readonly Color Panel      = new(0.082f, 0.157f, 0.263f);  // #152843
        static readonly Color Accent     = new(0.231f, 0.510f, 0.965f);  // #3B82F6
        static readonly Color AccentSoft = new(0.376f, 0.647f, 0.980f);  // #609EFA
        static readonly Color TextBright = new(0.961f, 0.980f, 1.000f);  // #F5FAFF
        static readonly Color TextDim    = new(0.702f, 0.800f, 0.898f);  // #B3CCE5
        static readonly Color Divider    = new(0.118f, 0.251f, 0.427f);  // #1E406D

        Texture2D _texBg, _texPanel, _texAccent, _texAccentHover, _texDivider;
        GUIStyle _hStyle, _sectionStyle, _valueStyle, _launchStyle, _cancelStyle, _chipStyle;
        bool _stylesBuilt;

        public static void Open(LccScene scene)
        {
            var w = GetWindow<LccPlaygroundWindow>(true, "LCC Playground Launcher", true);
            w._target = scene;
            w.minSize = new Vector2(460, 520);
            w.maxSize = new Vector2(460, 700);
            w.ShowUtility();
        }

        void OnDisable()
        {
            _DestroyTex(ref _texBg);
            _DestroyTex(ref _texPanel);
            _DestroyTex(ref _texAccent);
            _DestroyTex(ref _texAccentHover);
            _DestroyTex(ref _texDivider);
            _stylesBuilt = false;
        }

        static void _DestroyTex(ref Texture2D t)
        {
            if (t != null) { DestroyImmediate(t); t = null; }
        }

        void _BuildStyles()
        {
            if (_stylesBuilt) return;
            _texBg           = _Solid(BgDark);
            _texPanel        = _Solid(Panel);
            _texAccent       = _Solid(Accent);
            _texAccentHover  = _Solid(AccentSoft);
            _texDivider      = _Solid(Divider);

            _hStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                normal = { textColor = TextBright },
                padding = new RectOffset(12, 12, 8, 4),
                alignment = TextAnchor.MiddleLeft,
            };
            _sectionStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 10,
                fontStyle = FontStyle.Bold,
                normal = { textColor = AccentSoft },
                padding = new RectOffset(12, 12, 8, 2),
            };
            _valueStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                normal = { textColor = TextDim },
                padding = new RectOffset(12, 12, 0, 0),
            };
            _launchStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                fixedHeight = 40,
                alignment = TextAnchor.MiddleCenter,
                normal  = { background = _texAccent,      textColor = TextBright },
                hover   = { background = _texAccentHover, textColor = TextBright },
                active  = { background = _texAccentHover, textColor = TextBright },
                focused = { background = _texAccent,      textColor = TextBright },
                border = new RectOffset(2, 2, 2, 2),
            };
            _cancelStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12,
                fixedHeight = 32,
                alignment = TextAnchor.MiddleCenter,
                normal  = { background = _texPanel,   textColor = TextDim },
                hover   = { background = _texDivider, textColor = TextBright },
                border = new RectOffset(2, 2, 2, 2),
            };
            _chipStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = AccentSoft },
                padding = new RectOffset(8, 8, 2, 2),
                alignment = TextAnchor.MiddleCenter,
            };
            _stylesBuilt = true;
        }

        static Texture2D _Solid(Color c)
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            var px = new Color[4]; for (int i = 0; i < 4; i++) px[i] = c;
            t.SetPixels(px); t.Apply();
            return t;
        }

        void OnGUI()
        {
            _BuildStyles();

            // Full bg
            var full = new Rect(0, 0, position.width, position.height);
            GUI.DrawTexture(full, _texBg, ScaleMode.StretchToFill);

            if (_target == null || _target.manifest == null)
            {
                EditorGUILayout.LabelField("LccScene target is null.", _valueStyle);
                return;
            }
            var m = _target.manifest;

            // Title
            GUILayout.Space(6);
            GUILayout.Label("◤ LCC  PLAYGROUND  LAUNCHER ◢", _hStyle);
            _DrawDivider();

            // Scene summary
            GUILayout.Label("TARGET SCENE", _sectionStyle);
            using (new GUILayout.VerticalScope(GetPanelStyle(), GUILayout.ExpandWidth(true)))
            {
                _KV("name",   m.name ?? "-");
                _KV("splats", m.totalSplats.ToString("N0"));
                _KV("LODs",   m.totalLevel.ToString());
                if (m.boundingBox != null)
                {
                    var size = new Vector3(
                        m.boundingBox.max[0] - m.boundingBox.min[0],
                        m.boundingBox.max[1] - m.boundingBox.min[1],
                        m.boundingBox.max[2] - m.boundingBox.min[2]);
                    _KV("size",  $"{size.x:F1} × {size.y:F1} × {size.z:F1} m");
                }
                _KV("mesh PLY", _ProxyPlyAbsPath(out _) ?? "(not found)");
            }

            // Options
            GUILayout.Space(8);
            GUILayout.Label("SPAWN OPTIONS", _sectionStyle);
            using (new GUILayout.VerticalScope(GetPanelStyle()))
            {
                GUILayout.Space(4);
                _lodLevel       = EditorGUILayout.IntSlider("LOD Level", _lodLevel, 0, 4);
                GUILayout.Label(
                    _lodLevel == 0 ? "0 = 원본 (최고 품질 · 메모리 많음)"
                  : _lodLevel == 4 ? "4 = 최저 품질 (빠른 프리뷰)"
                  : $"{_lodLevel} = 중간 품질",
                    _valueStyle);

                GUILayout.Space(6);
                _addCollider    = EditorGUILayout.ToggleLeft("☑ Auto MeshCollider  (mesh-files/*.ply)", _addCollider);
                using (new EditorGUI.DisabledScope(!_addCollider))
                {
                    EditorGUI.indentLevel++;
                    _colorizeMesh = EditorGUILayout.ToggleLeft("🎨 Colorize Mesh  (k-NN from splat RGB)", _colorizeMesh);
                    using (new EditorGUI.DisabledScope(!_colorizeMesh))
                    {
                        _colorSourceLod       = EditorGUILayout.IntSlider("  color source LOD", _colorSourceLod, 0, 4);
                        _colorizerCellSize    = EditorGUILayout.Slider("  voxel cell (m)", _colorizerCellSize, 0.2f, 3f);
                        _colorizerK           = EditorGUILayout.IntSlider("  k neighbors", _colorizerK, 1, 16);
                        _hideSplatAfterColorize = EditorGUILayout.ToggleLeft("  hide splats after colorize", _hideSplatAfterColorize);
                    }
                    EditorGUI.indentLevel--;
                }
                _spawnCharacter = EditorGUILayout.ToggleLeft("☑ Spawn Third Person Character  (Invector LITE)", _spawnCharacter);
                _frameCamera    = EditorGUILayout.ToggleLeft("☑ Frame Main Camera on bounds", _frameCamera);
                _cleanLaunch    = EditorGUILayout.ToggleLeft("☑ Clean Launch  (기존 스폰 오브젝트 삭제)", _cleanLaunch);

                GUILayout.Space(6);
                using (new EditorGUI.DisabledScope(!_spawnCharacter))
                    _heightOffset = EditorGUILayout.Slider("Character Height Offset", _heightOffset, 0f, 5f);
            }

            // Status chips
            GUILayout.Space(8);
            string plyAbs = _ProxyPlyAbsPath(out string plyStatus);
            bool plyFound = !string.IsNullOrEmpty(plyAbs);
            bool tpOk = File.Exists(ThirdPersonPrefabPath);
            using (new GUILayout.HorizontalScope())
            {
                _Chip(plyStatus);
                _Chip(tpOk ? "TP Controller · ready" : "TP Controller · MISSING");
            }
            if (!plyFound && _addCollider)
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Space(12);
                    if (GUILayout.Button("📁 Browse PLY…  (프로젝트로 복사)", _cancelStyle, GUILayout.ExpandWidth(true)))
                        _BrowseAndCopyPly();
                    GUILayout.Space(12);
                }
            }

            // Actions
            GUILayout.FlexibleSpace();
            _DrawDivider();
            GUILayout.Space(6);
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Space(12);
                if (GUILayout.Button("🚀  LAUNCH", _launchStyle, GUILayout.ExpandWidth(true)))
                    _Launch();
                GUILayout.Space(8);
                if (GUILayout.Button("Cancel", _cancelStyle, GUILayout.Width(90)))
                    Close();
                GUILayout.Space(12);
            }
            GUILayout.Space(10);
        }

        GUIStyle GetPanelStyle()
        {
            return new GUIStyle(GUIStyle.none)
            {
                normal = { background = _texPanel },
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(10, 10, 2, 2),
            };
        }

        void _KV(string k, string v)
        {
            using (new GUILayout.HorizontalScope())
            {
                var k1 = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = TextDim }, fixedWidth = 90 };
                var v1 = new GUIStyle(EditorStyles.label)     { normal = { textColor = TextBright }, fontStyle = FontStyle.Bold };
                GUILayout.Label(k, k1);
                GUILayout.Label(v, v1);
            }
        }

        void _DrawDivider()
        {
            var r = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
            r.x += 12; r.width -= 24;
            GUI.DrawTexture(r, _texDivider);
        }

        void _Chip(string text)
        {
            var c = new GUIStyle(GUIStyle.none)
            {
                normal = { background = _texPanel, textColor = AccentSoft },
                padding = new RectOffset(10, 10, 4, 4),
                margin = new RectOffset(12, 4, 2, 2),
                alignment = TextAnchor.MiddleCenter,
                fontSize = 10,
            };
            GUILayout.Label("⟦ " + text + " ⟧", c);
        }

        string _ProxyPlyAbsPath(out string status)
        {
            string assetPath = _target?.ResolveProxyMeshPlyAssetPath();
            if (string.IsNullOrEmpty(assetPath)) { status = "proxy PLY · NOT found"; return null; }
            string abs = Path.GetFullPath(assetPath);
            long size = new FileInfo(abs).Length;
            status = $"proxy PLY · {(size / 1024f / 1024f):F1} MB";
            return abs;
        }

        void _BrowseAndCopyPly()
        {
            string picked = EditorUtility.OpenFilePanel("Select proxy mesh PLY for " + _target.name, "", "ply");
            if (string.IsNullOrEmpty(picked)) return;

            // 저장 위치: <rootPath>/<name>.ply (ResolveProxyMeshPlyAssetPath 의 첫 번째 후보)
            string destAssetPath = Path.Combine(_target.rootPath, _target.manifest.name + ".ply").Replace('\\', '/');
            string destAbs = Path.GetFullPath(destAssetPath);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destAbs) ?? "");
                File.Copy(picked, destAbs, overwrite: true);
                AssetDatabase.Refresh();
                Debug.Log($"[LccPlayground] PLY 복사됨 → {destAssetPath}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[LccPlayground] PLY 복사 실패: {ex.Message}");
            }
            Repaint();
        }

        // ─────────── Launch ───────────
        // 팝업 UI 없이 프로그래매틱하게 Playground 를 스폰하고 싶을 때 사용하는 엔트리.
        // 테스트 / 메뉴 아이템 / 배치 임포트에서 호출.
        public static GameObject QuickLaunch(
            LccScene scene,
            int lodLevel = 0,
            bool addCollider = true,
            bool spawnCharacter = true,
            bool frameCamera = true,
            bool cleanLaunch = true,
            float heightOffset = 1f,
            bool colorizeMesh = false,
            int colorSourceLod = 3,
            float colorizerCellSize = 1.0f,
            int colorizerK = 6,
            bool hideSplatAfterColorize = false)
        {
            var w = CreateInstance<LccPlaygroundWindow>();
            try
            {
                w._target         = scene;
                w._lodLevel       = lodLevel;
                w._addCollider    = addCollider;
                w._spawnCharacter = spawnCharacter;
                w._frameCamera    = frameCamera;
                w._cleanLaunch    = cleanLaunch;
                w._heightOffset   = heightOffset;
                w._colorizeMesh   = colorizeMesh;
                w._colorSourceLod = colorSourceLod;
                w._colorizerCellSize = colorizerCellSize;
                w._colorizerK     = colorizerK;
                w._hideSplatAfterColorize = hideSplatAfterColorize;
                return w._LaunchCore();
            }
            finally
            {
                DestroyImmediate(w);
            }
        }

        void _Launch()
        {
            _LaunchCore();
            Close();
        }

        GameObject _LaunchCore()
        {
            if (_target == null) { Debug.LogError("[LccPlayground] No target scene."); return null; }

            if (_cleanLaunch) _CleanExisting();

            // 1. Splat GameObject
            string goName = $"Virnect_{_target.name}";
            var splatGO = new GameObject(goName);
            splatGO.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);

            var splat = splatGO.AddComponent<LccSplatRenderer>();
            splat.scene = _target;
            splat.lodLevel = _lodLevel;
            splat.scaleMultiplier = 1.5f;
            splat.opacityBoost = 0f;
            splat.tint = Color.white;
            splat.enabled = false;
            splat.enabled = true;

            Undo.RegisterCreatedObjectUndo(splatGO, "LCC Playground · Splat");

            // 2. Collider (child → inherits -90X rotation so PLY verts align with splat)
            if (_addCollider)
            {
                string plyAbs = _ProxyPlyAbsPath(out _);
                if (!string.IsNullOrEmpty(plyAbs) && File.Exists(plyAbs))
                {
                    try
                    {
                        var mesh = LccMeshPlyLoader.Load(plyAbs);
                        var colGO = new GameObject("__LccCollider");
                        colGO.transform.SetParent(splatGO.transform, false);
                        var mc = colGO.AddComponent<MeshCollider>();
                        mc.sharedMesh = mesh;
                        Undo.RegisterCreatedObjectUndo(colGO, "LCC Playground · Collider");
                        Debug.Log($"[LccPlayground] MeshCollider · {mesh.vertexCount:N0} verts / {mesh.triangles.Length / 3:N0} tris");

                        // 2b. Colorized visualization mesh (splat RGB → vertex color)
                        if (_colorizeMesh)
                        {
                            _SpawnColoredMesh(mesh, splatGO);
                            if (_hideSplatAfterColorize)
                            {
                                var mr = splatGO.GetComponent<MeshRenderer>();
                                if (mr != null) mr.enabled = false;
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError($"[LccPlayground] PLY 로드 실패: {ex.Message}");
                    }
                }
                else
                {
                    Debug.LogWarning($"[LccPlayground] Proxy PLY 없음 — MeshCollider 건너뜀. 예상 경로: {_target.ProxyMeshPlyPath(_target.manifest.name)}");
                }
            }

            // 3. Character + Camera
            GameObject player = null;
            if (_spawnCharacter)
            {
                player = _SpawnCharacter(splat);
            }

            // 4. Frame main camera (only if we didn't hand off to TP camera)
            if (_frameCamera && player == null)
                _FrameCameraOnBounds(splat);

            EditorSceneManager.MarkSceneDirty(splatGO.scene);
            Selection.activeGameObject = player != null ? player : splatGO;

            Debug.Log($"[LccPlayground] Launched · scene='{_target.name}' · LOD {_lodLevel} · collider={_addCollider} · character={_spawnCharacter}");
            return splatGO;
        }

        void _SpawnColoredMesh(Mesh colliderMesh, GameObject splatGO)
        {
            try
            {
                // colliderMesh 는 MeshCollider.sharedMesh 로 이미 참조됨 → 별도 Mesh 인스턴스 복제
                // (colors32 쓰는 순간 collider 쪽 인스턴싱 발동하는 걸 피하려고 분리)
                var vizMesh = new Mesh { name = colliderMesh.name + "_colored", indexFormat = colliderMesh.indexFormat };
                vizMesh.vertices  = colliderMesh.vertices;
                vizMesh.triangles = colliderMesh.triangles;
                vizMesh.RecalculateBounds();
                vizMesh.RecalculateNormals();

                double t0 = EditorApplication.timeSinceStartup;
                var splats = LccSplatDecoder.DecodeLod(_target, _colorSourceLod);
                double t1 = EditorApplication.timeSinceStartup;

                var opts = LccMeshColorizer.Options.Default;
                opts.cellSize = _colorizerCellSize;
                opts.k = _colorizerK;
                opts.maxRadius = _colorizerCellSize * 3f;

                LccMeshColorizer.Colorize(vizMesh, splats, opts);
                double t2 = EditorApplication.timeSinceStartup;

                var vizGO = new GameObject("__LccColoredMesh");
                vizGO.transform.SetParent(splatGO.transform, false);
                var mf = vizGO.AddComponent<MeshFilter>();
                mf.sharedMesh = vizMesh;
                var mr = vizGO.AddComponent<MeshRenderer>();
                var shader = Shader.Find("Virnect/LccVertexColorUnlit");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
                mr.sharedMaterial = new Material(shader) { name = "LccColored_" + _target.name };

                Undo.RegisterCreatedObjectUndo(vizGO, "LCC Playground · Colored Mesh");
                Debug.Log($"[LccPlayground] ColoredMesh · {vizMesh.vertexCount:N0} verts · decode {(t1 - t0) * 1000:F0} ms · colorize {(t2 - t1) * 1000:F0} ms · LOD {_colorSourceLod} ({splats.Length:N0} splats)");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[LccPlayground] Colorize 실패: {ex.Message}\n{ex.StackTrace}");
            }
        }

        void _CleanExisting()
        {
            string[] prefixes = { "Virnect_", "GS_", "__Lcc_", "__LccRoot", "vThirdPersonCamera_LITE", "ThirdPersonController_LITE" };
            var scene = EditorSceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (prefixes.Any(p => root.name.StartsWith(p)))
                {
                    Undo.DestroyObjectImmediate(root);
                }
            }
        }

        GameObject _SpawnCharacter(LccSplatRenderer splat)
        {
            var playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ThirdPersonPrefabPath);
            var cameraPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ThirdPersonCameraPath);
            if (playerPrefab == null)
            {
                Debug.LogError($"[LccPlayground] ThirdPerson prefab not found at {ThirdPersonPrefabPath}");
                return null;
            }

            // Bounds in splat-local → world (accounts for -90X rotation)
            var localBounds = splat.GetWorldBounds();
            Vector3 worldCenter = splat.transform.TransformPoint(localBounds.center);
            Vector3 c0 = splat.transform.TransformPoint(localBounds.min);
            Vector3 c1 = splat.transform.TransformPoint(localBounds.max);
            float floorY = Mathf.Min(c0.y, c1.y);

            Vector3 spawn = new Vector3(worldCenter.x, floorY + _heightOffset, worldCenter.z);

            var player = (GameObject)PrefabUtility.InstantiatePrefab(playerPrefab);
            player.transform.position = spawn;
            Undo.RegisterCreatedObjectUndo(player, "LCC Playground · Player");

            if (cameraPrefab != null)
            {
                var cam = (GameObject)PrefabUtility.InstantiatePrefab(cameraPrefab);
                Undo.RegisterCreatedObjectUndo(cam, "LCC Playground · Camera");

                // Invector 는 Assembly-CSharp 소속이라 이 에디터 에셈블리에서 타입 참조 불가
                // → 모든 MonoBehaviour 를 훑어 GetType().Name == "vThirdPersonCamera" 로 매칭.
                MonoBehaviour camScript = null;
                foreach (var mb in cam.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb != null && mb.GetType().Name == "vThirdPersonCamera")
                    { camScript = mb; break; }
                }
                if (camScript != null)
                {
                    var so = new SerializedObject(camScript);
                    var targetProp = so.FindProperty("target");
                    if (targetProp != null)
                    {
                        targetProp.objectReferenceValue = player.transform;
                        so.ApplyModifiedProperties();
                    }
                }

                // 기존 Main Camera 비활성 → Invector 카메라가 연출 전담
                var oldMain = Camera.main;
                if (oldMain != null && !oldMain.transform.IsChildOf(cam.transform))
                    oldMain.gameObject.SetActive(false);
            }
            else
            {
                Debug.LogWarning($"[LccPlayground] vThirdPersonCamera prefab not found at {ThirdPersonCameraPath} — 기본 카메라 유지");
            }

            Debug.Log($"[LccPlayground] Character spawned at {spawn}");
            return player;
        }

        void _FrameCameraOnBounds(LccSplatRenderer r)
        {
            var cam = Camera.main;
            if (cam == null) return;

            var lb = r.GetWorldBounds();
            var wc = r.transform.TransformPoint(lb.center);
            var we = r.transform.TransformVector(lb.extents);
            float maxExt = Mathf.Max(Mathf.Abs(we.x), Mathf.Abs(we.y), Mathf.Abs(we.z));
            float fit = maxExt / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.2f;

            cam.transform.position = wc + new Vector3(0f, 0f, -fit);
            cam.transform.LookAt(wc);
            cam.nearClipPlane = Mathf.Max(0.1f, fit * 0.001f);
            cam.farClipPlane  = Mathf.Max(1000f, fit * 10f);
            EditorUtility.SetDirty(cam);
        }
    }
}
