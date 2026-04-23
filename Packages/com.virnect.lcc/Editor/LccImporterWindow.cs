using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Virnect.Lcc.Editor
{
    /// LCC 전용 Editor Window — UPM 패키지 사용자가 실제로 쓰게 될 메인 허브.
    /// 기능:
    ///   - 씬 리스트 관리 (추가/제거/옵션)
    ///   - 렌더 모드 선택 (Point vs Splat Billboard)
    ///   - LOD / 크기 / 불투명도 조절
    ///   - 합치기 플랜 미리보기 (동일 EPSG 좌표계 가정)
    ///   - 월드로 인스턴스화 (LccSceneMerger.Plan 결과를 Hierarchy 에 실제 GameObject 로)
    ///   - 기존 LCC GameObject 전체 제거
    ///   - 선택된 씬 상세 정보
    public sealed class LccImporterWindow : EditorWindow
    {
        [MenuItem("Virnect/LCC Importer")]
        public static void Open() => GetWindow<LccImporterWindow>("LCC Importer").minSize = new Vector2(380, 500);

        enum RenderMode { PointCloud, SplatBillboard }

        [System.Serializable]
        class SceneSlot
        {
            public LccScene scene;
            public bool enabled = true;
        }

        List<SceneSlot> _slots = new List<SceneSlot>();
        RenderMode _renderMode = RenderMode.SplatBillboard;
        int   _lodLevel = 4;
        float _pointSize = 0.03f;
        float _scaleMultiplier = 1.5f;
        float _opacityBoost = 0.3f;
        Color _tint = Color.white;
        bool  _frameCameraAfterInstantiate = true;

        int _selectedIndex = -1;
        Vector2 _scroll;

        void OnEnable()
        {
            if (_slots.Count == 0) _slots.Add(new SceneSlot());
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            _HeaderSection();
            EditorGUILayout.Space(6);
            _ScenesSection();
            EditorGUILayout.Space(6);
            _RenderSettingsSection();
            EditorGUILayout.Space(6);
            _ActionsSection();
            EditorGUILayout.Space(6);
            _InspectSelected();

            EditorGUILayout.EndScrollView();
        }

        // ── Header ─────────────────────────────────────────────────────────
        void _HeaderSection()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Virnect LCC Importer", EditorStyles.toolbarButton,
                                GUILayout.Width(170));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Docs", EditorStyles.toolbarButton, GUILayout.Width(50)))
                    Application.OpenURL("https://github.com/virnect3d-cpu/point_cloude/blob/v2-main/docs/pipeline.md");
                if (GUILayout.Button("Repo", EditorStyles.toolbarButton, GUILayout.Width(50)))
                    Application.OpenURL("https://github.com/virnect3d-cpu/point_cloude/tree/v2-main");
            }
            EditorGUILayout.HelpBox(
                "XGrids PortalCam .lcc 를 드래그해 여러 씬을 합치고, 포인트/스플랫 형태로 렌더합니다. 모든 씬이 동일 EPSG 좌표계라 가정.",
                MessageType.Info);
        }

        // ── Scenes list ───────────────────────────────────────────────────
        void _ScenesSection()
        {
            EditorGUILayout.LabelField("LCC Scenes", EditorStyles.boldLabel);
            int removeIdx = -1;
            for (int i = 0; i < _slots.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope(GUI.skin.box))
                {
                    _slots[i].enabled = EditorGUILayout.Toggle(_slots[i].enabled, GUILayout.Width(16));
                    var picked = (LccScene)EditorGUILayout.ObjectField(
                        _slots[i].scene, typeof(LccScene), false);
                    if (picked != _slots[i].scene) { _slots[i].scene = picked; _selectedIndex = i; }
                    GUI.enabled = _slots[i].scene != null;
                    if (GUILayout.Button("▸", GUILayout.Width(22))) _selectedIndex = i;
                    GUI.enabled = true;
                    if (GUILayout.Button("✕", GUILayout.Width(22))) removeIdx = i;
                }
            }
            if (removeIdx >= 0) _slots.RemoveAt(removeIdx);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("+ 씬 추가")) _slots.Add(new SceneSlot());
                if (GUILayout.Button("모두 지우기", GUILayout.Width(100))) _slots.Clear();
            }

            // 활성 개수 표시
            int active = 0; foreach (var s in _slots) if (s.enabled && s.scene != null) active++;
            EditorGUILayout.LabelField($"활성 씬: {active} / {_slots.Count}",
                                       EditorStyles.miniLabel);
        }

        // ── Render settings ───────────────────────────────────────────────
        void _RenderSettingsSection()
        {
            EditorGUILayout.LabelField("Render Settings", EditorStyles.boldLabel);
            _renderMode    = (RenderMode)EditorGUILayout.EnumPopup("모드", _renderMode);
            _lodLevel      = EditorGUILayout.IntSlider("LOD", _lodLevel, 0, 4);

            if (_renderMode == RenderMode.PointCloud)
            {
                EditorGUILayout.LabelField("Point renderer 는 하드웨어 1 px 포인트. LOD 0 도 OK.", EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField("Splat renderer 는 쿼드 4 verts/splat. LOD 4 권장 (메모리).", EditorStyles.miniLabel);
                _scaleMultiplier = EditorGUILayout.Slider("Scale 배율", _scaleMultiplier, 0.2f, 5f);
                _opacityBoost    = EditorGUILayout.Slider("Opacity 부스트", _opacityBoost, 0f, 1f);
            }
            _tint = EditorGUILayout.ColorField("Tint", _tint);
            _frameCameraAfterInstantiate = EditorGUILayout.Toggle(
                "인스턴스화 후 카메라 자동 프레이밍", _frameCameraAfterInstantiate);
        }

        // ── Actions ───────────────────────────────────────────────────────
        void _ActionsSection()
        {
            EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);

            if (GUILayout.Button("합치기 플랜 미리보기", GUILayout.Height(26)))
                _PreviewMergePlan();

            var activeSlots = _ActiveSlots();
            using (new EditorGUI.DisabledScope(activeSlots.Count == 0))
            {
                if (GUILayout.Button($"▶ 월드로 인스턴스화 ({activeSlots.Count} 씬)",
                                     GUILayout.Height(32)))
                    _Instantiate(activeSlots);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("기존 LCC GameObject 모두 제거"))
                    _ClearAllInstances();
                if (GUILayout.Button("포커스", GUILayout.Width(70)))
                    _FrameCameraToExisting();
            }
        }

        List<SceneSlot> _ActiveSlots()
        {
            var a = new List<SceneSlot>();
            foreach (var s in _slots) if (s.enabled && s.scene != null) a.Add(s);
            return a;
        }

        void _PreviewMergePlan()
        {
            var scenes = new List<LccScene>();
            foreach (var s in _ActiveSlots()) scenes.Add(s.scene);
            if (scenes.Count == 0) { ShowNotification(new GUIContent("씬이 없습니다.")); return; }
            var plan = LccSceneMerger.Plan(scenes);
            Debug.Log($"[LCC] === 합치기 플랜 ({plan.Count} 씬) ===");
            foreach (var p in plan)
                Debug.Log($"  {p.scene?.name} → world=({p.worldOffset.x:F2},{p.worldOffset.y:F2},{p.worldOffset.z:F2})  " +
                          $"rot={p.rotation}  scale={p.scale}");
            ShowNotification(new GUIContent($"플랜 출력 ({plan.Count}) → Console"));
        }

        void _Instantiate(List<SceneSlot> activeSlots)
        {
            var root = GameObject.Find("__LccRoot");
            if (root == null) root = new GameObject("__LccRoot");

            var scenes = new List<LccScene>();
            foreach (var s in activeSlots) scenes.Add(s.scene);
            var plan = LccSceneMerger.Plan(scenes);

            int created = 0;
            foreach (var p in plan)
            {
                if (p.scene == null) continue;
                var go = new GameObject("__Lcc_" + p.scene.name);
                go.transform.SetParent(root.transform, false);
                go.transform.position   = new Vector3((float)p.worldOffset.x, (float)p.worldOffset.y, (float)p.worldOffset.z);
                go.transform.rotation   = new Quaternion(p.rotation.value.x, p.rotation.value.y, p.rotation.value.z, p.rotation.value.w);
                go.transform.localScale = new Vector3(p.scale.x, p.scale.y, p.scale.z);

                if (_renderMode == RenderMode.SplatBillboard)
                {
                    var r = go.AddComponent<LccSplatRenderer>();
                    r.scene = p.scene;
                    r.lodLevel = _lodLevel;
                    r.scaleMultiplier = _scaleMultiplier;
                    r.opacityBoost = _opacityBoost;
                    r.tint = _tint;
                    r.enabled = false; r.enabled = true;
                }
                else
                {
                    var r = go.AddComponent<LccPointCloudRenderer>();
                    r.scene = p.scene;
                    r.lodLevel = _lodLevel;
                    r.tint = _tint;
                    r.enabled = false; r.enabled = true;
                }
                created++;
            }

            Undo.RegisterCreatedObjectUndo(root, "Instantiate LCC Scenes");
            Debug.Log($"[LCC] Instantiated {created} scenes under '__LccRoot' ({_renderMode}, LOD {_lodLevel}).");

            if (_frameCameraAfterInstantiate) _FrameCameraToExisting();
            ShowNotification(new GUIContent($"{created} 씬 배치 완료"));
        }

        void _ClearAllInstances()
        {
            var root = GameObject.Find("__LccRoot");
            if (root != null) Undo.DestroyObjectImmediate(root);
            // 이전 데모 오브젝트들도 정리
            foreach (var n in new[] { "__LccTest", "__LccSplatTest",
                                       "__LccMergeRoot", "__LccMerge_A", "__LccMerge_B" })
            {
                var g = GameObject.Find(n);
                if (g != null) Undo.DestroyObjectImmediate(g);
            }
            ShowNotification(new GUIContent("LCC 오브젝트 제거 완료"));
        }

        void _FrameCameraToExisting()
        {
            var root = GameObject.Find("__LccRoot");
            if (root == null) return;

            // Combined bounds
            Bounds? combined = null;
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
            {
                if (!combined.HasValue) combined = r.bounds;
                else { var b = combined.Value; b.Encapsulate(r.bounds); combined = b; }
            }
            if (!combined.HasValue) return;

            var cam = Camera.main;
            if (cam == null)
            {
                var camGO = GameObject.Find("Main Camera");
                if (camGO != null) cam = camGO.GetComponent<Camera>();
            }
            if (cam == null) { Debug.LogWarning("[LCC] Main Camera 없음 — 프레이밍 스킵"); return; }

            var c = combined.Value.center;
            float s = Mathf.Max(combined.Value.size.x, Mathf.Max(combined.Value.size.y, combined.Value.size.z));
            cam.transform.position = c + new Vector3(s * 0.9f, s * 0.6f, -s * 0.9f);
            cam.transform.LookAt(c);
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane  = Mathf.Max(cam.farClipPlane, s * 5f);
            SceneView.lastActiveSceneView?.Frame(combined.Value, false);
        }

        // ── Inspector-like view for the selected scene ─────────────────────
        void _InspectSelected()
        {
            if (_selectedIndex < 0 || _selectedIndex >= _slots.Count) return;
            var s = _slots[_selectedIndex].scene;
            if (s == null) return;

            EditorGUILayout.LabelField($"Selected: {s.name}", EditorStyles.boldLabel);
            var m = s.manifest;
            if (m == null) { EditorGUILayout.HelpBox("매니페스트 파싱 실패", MessageType.Warning); return; }

            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                EditorGUILayout.LabelField("name", m.name ?? "-");
                EditorGUILayout.LabelField("source/type", $"{m.source} / {m.dataType}");
                EditorGUILayout.LabelField("totalSplats",
                    m.totalSplats.ToString("N0") + $"  (LOD {m.totalLevel} 단계)");
                if (m.splats != null)
                {
                    string sp = "";
                    for (int i = 0; i < m.splats.Length; i++)
                        sp += (i == 0 ? "" : ", ") + m.splats[i].ToString("N0");
                    EditorGUILayout.LabelField("splats/LOD", sp);
                }
                if (m.boundingBox != null)
                {
                    EditorGUILayout.LabelField("bbox min",
                        $"[{m.boundingBox.min[0]:F2}, {m.boundingBox.min[1]:F2}, {m.boundingBox.min[2]:F2}]");
                    EditorGUILayout.LabelField("bbox max",
                        $"[{m.boundingBox.max[0]:F2}, {m.boundingBox.max[1]:F2}, {m.boundingBox.max[2]:F2}]");
                }
                EditorGUILayout.LabelField("epsg / encoding", $"{m.epsg} / {m.encoding}");
                EditorGUILayout.LabelField("rootPath", s.rootPath ?? "-");
                EditorGUILayout.LabelField("data.bin",
                    System.IO.File.Exists(s.DataBinPath) ? "✓ exists" : "✗ missing");
            }

            if (s.attrs != null)
            {
                using (new EditorGUILayout.VerticalScope(GUI.skin.box))
                {
                    EditorGUILayout.LabelField("attrs.lcp", EditorStyles.miniBoldLabel);
                    if (s.attrs.transform?.position != null)
                        EditorGUILayout.LabelField("transform.pos",
                            $"[{s.attrs.transform.position[0]}, {s.attrs.transform.position[1]}, {s.attrs.transform.position[2]}]");
                    if (s.attrs.spawnPoint?.position != null)
                        EditorGUILayout.LabelField("spawnPoint.pos",
                            $"[{s.attrs.spawnPoint.position[0]}, {s.attrs.spawnPoint.position[1]}, {s.attrs.spawnPoint.position[2]}]");
                }
            }
        }
    }
}
