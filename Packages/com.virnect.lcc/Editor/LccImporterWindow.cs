using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Virnect.Lcc.Editor
{
    /// LCC 전용 Editor Window — UPM 패키지 사용자의 메인 허브.
    /// 기능:
    ///   · 씬 리스트 관리 / 렌더 설정 / 합치기 플랜·인스턴스화
    ///   · 파이썬 백엔드 서버 (installed ↔ started) 토글  ← 실행.bat 의 역할 내재화
    ///   · v2 API 호출 (Hausdorff 비교)
    ///   · 선택된 씬의 매니페스트 요약
    public sealed class LccImporterWindow : EditorWindow
    {
        [MenuItem("Virnect/LCC Importer")]
        public static void Open()
        {
            var w = GetWindow<LccImporterWindow>("LCC Importer");
            w.minSize = new Vector2(420, 620);
            w.Show();
        }

        // ── State ──────────────────────────────────────────────────────────
        enum RenderMode { SplatBillboard, PointCloud }

        [System.Serializable]
        class SceneSlot { public LccScene scene; public bool enabled = true; }

        List<SceneSlot> _slots = new List<SceneSlot>();
        RenderMode _renderMode = RenderMode.SplatBillboard;
        int   _lodLevel = 4;
        float _scaleMultiplier = 1.5f;
        float _opacityBoost = 0.3f;
        Color _tint = Color.white;
        bool  _frameCameraAfterInstantiate = true;
        bool  _attachLodStreamer = true;
        int _selectedIndex = -1;

        // Tabs
        enum Tab { Scenes, Server, Compare, Info }
        Tab _tab = Tab.Scenes;

        // Server state
        bool   _serverHealthy = false;
        string _serverInfo = "";
        double _lastHealthTime = 0;
        string _serverLog = "";

        // Compare
        string _cmpReferencePly = "";
        int    _cmpLod = 2;
        int    _cmpSample = 200000;
        string _cmpResult = "";
        bool   _cmpBusy = false;

        Vector2 _scroll;

        // ── Lifecycle ─────────────────────────────────────────────────────
        void OnEnable()
        {
            if (_slots.Count == 0) _slots.Add(new SceneSlot());
            _HealthCheck();
        }

        void OnFocus() => _HealthCheck();

        void Update()
        {
            // 5초마다 자동 헬스체크
            if (EditorApplication.timeSinceStartup - _lastHealthTime > 5) _HealthCheck();
        }

        // ── GUI ───────────────────────────────────────────────────────────
        void OnGUI()
        {
            _TopBar();
            _TabBar();
            EditorGUILayout.Space(4);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            switch (_tab)
            {
                case Tab.Scenes:
                    _ScenesSection();
                    _RenderSettingsSection();
                    _ActionsSection();
                    break;
                case Tab.Server:
                    _ServerSection();
                    break;
                case Tab.Compare:
                    _ApiCompareSection();
                    break;
                case Tab.Info:
                    _QuickStartSection();
                    _InspectSelectedSection();
                    break;
            }
            EditorGUILayout.EndScrollView();
        }

        // ── Tab bar ───────────────────────────────────────────────────────
        void _TabBar()
        {
            var tabStyle = new GUIStyle(EditorStyles.toolbarButton)
            { fixedHeight = 26, fontSize = 12 };
            var activeStyle = new GUIStyle(tabStyle)
            { fontStyle = FontStyle.Bold,
              normal = { textColor = new Color(0.55f, 0.85f, 1.0f) } };

            int active = 0; foreach (var s in _slots) if (s.enabled && s.scene != null) active++;
            string scenesLabel  = $"📂 Scenes ({_slots.Count})";
            string serverLabel  = _serverHealthy ? "🐍 Server ●" : "🐍 Server ○";
            string compareLabel = "📐 Compare";
            string infoLabel    = "🔍 Info";

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _TabButton(Tab.Scenes,  scenesLabel,  tabStyle, activeStyle);
                _TabButton(Tab.Server,  serverLabel,  tabStyle, activeStyle);
                _TabButton(Tab.Compare, compareLabel, tabStyle, activeStyle);
                _TabButton(Tab.Info,    infoLabel,    tabStyle, activeStyle);
            }
        }
        void _TabButton(Tab t, string label, GUIStyle normal, GUIStyle active)
        {
            if (GUILayout.Button(label, _tab == t ? active : normal, GUILayout.MinWidth(90)))
                _tab = t;
        }

        // ── Top status bar ────────────────────────────────────────────────
        void _TopBar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("◆ Virnect LCC Importer",
                                EditorStyles.toolbarButton, GUILayout.Width(175));

                // Server status pill
                string pill = _serverHealthy ? "● API online" : "○ API offline";
                var style = new GUIStyle(EditorStyles.toolbarButton)
                {
                    normal = { textColor = _serverHealthy ? new Color(0.45f, 0.85f, 0.55f) : new Color(0.85f, 0.55f, 0.55f) },
                    alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold
                };
                if (GUILayout.Button(pill, style, GUILayout.Width(110))) _HealthCheck();

                // scene count
                int active = 0; foreach (var s in _slots) if (s.enabled && s.scene != null) active++;
                GUILayout.Label($"· {active} active scene" + (active == 1 ? "" : "s"),
                                EditorStyles.toolbarButton, GUILayout.Width(110));

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Docs", EditorStyles.toolbarButton, GUILayout.Width(46)))
                    Application.OpenURL("https://github.com/virnect3d-cpu/point_cloude/blob/v2-main/docs/pipeline.md");
                if (GUILayout.Button("Repo", EditorStyles.toolbarButton, GUILayout.Width(46)))
                    Application.OpenURL("https://github.com/virnect3d-cpu/point_cloude/tree/v2-main");
            }
        }

        // ── Quick start card ──────────────────────────────────────────────
        void _QuickStartSection()
        {
            EditorGUILayout.LabelField("🚀 Quick Start", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    "1. `.lcc` 파일이 든 폴더를 Project 에 복사\n" +
                    "2. 'Scenes' 탭의 리스트에 `LccScene` 에셋 드래그\n" +
                    "3. Render Settings 에서 모드/LOD 선택\n" +
                    "4. ▶ 월드로 인스턴스화 → Scene 뷰에 바로 렌더\n" +
                    "5. (선택) 'Server' 탭에서 Start → 'Compare' 탭에서 3D 스캔 Hausdorff",
                    EditorStyles.wordWrappedMiniLabel);
            }
            EditorGUILayout.Space(2);
        }

        // ── Scenes ────────────────────────────────────────────────────────
        void _ScenesSection()
        {
            EditorGUILayout.LabelField("📂 LCC Scenes", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                {
                    int removeIdx = -1;
                    for (int i = 0; i < _slots.Count; i++)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            _slots[i].enabled = EditorGUILayout.Toggle(_slots[i].enabled, GUILayout.Width(16));
                            var picked = (LccScene)EditorGUILayout.ObjectField(_slots[i].scene, typeof(LccScene), false);
                            if (picked != _slots[i].scene) { _slots[i].scene = picked; _selectedIndex = i; }
                            GUI.enabled = _slots[i].scene != null;
                            if (GUILayout.Button("▸", GUILayout.Width(24))) _selectedIndex = i;
                            GUI.enabled = true;
                            var prev = GUI.backgroundColor;
                            GUI.backgroundColor = new Color(1f, 0.55f, 0.55f);
                            if (GUILayout.Button("✕", GUILayout.Width(24))) removeIdx = i;
                            GUI.backgroundColor = prev;
                        }
                    }
                    if (removeIdx >= 0) _slots.RemoveAt(removeIdx);
                    EditorGUILayout.Space(4);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("＋ Add slot", GUILayout.Height(22)))
                            _slots.Add(new SceneSlot());
                        if (GUILayout.Button("Clear all", GUILayout.Height(22), GUILayout.Width(90)))
                            _slots.Clear();
                    }
                }
            }
            EditorGUILayout.Space(2);
        }

        // ── Render settings ───────────────────────────────────────────────
        void _RenderSettingsSection()
        {
            EditorGUILayout.LabelField("🎨 Render Settings", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                {
                    _renderMode = (RenderMode)EditorGUILayout.EnumPopup(
                        new GUIContent("모드", "Splat: 3D 가우시안 빌보드 · Point: 하드웨어 1px 포인트"), _renderMode);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("LOD", GUILayout.Width(40));
                        _lodLevel = EditorGUILayout.IntSlider(_lodLevel, 0, 4);
                    }
                    string lodHint = _lodLevel switch
                    {
                        0 => "5.1M splats — 최고 디테일 (무거움)",
                        1 => "2.6M splats",
                        2 => "1.3M splats — 권장",
                        3 => "642K splats",
                        _ => "321K splats — 가장 가벼움",
                    };
                    EditorGUILayout.LabelField(lodHint, EditorStyles.miniLabel);

                    if (_renderMode == RenderMode.SplatBillboard)
                    {
                        EditorGUILayout.Space(2);
                        _scaleMultiplier = EditorGUILayout.Slider("Scale 배율", _scaleMultiplier, 0.2f, 5f);
                        _opacityBoost    = EditorGUILayout.Slider("Opacity 부스트", _opacityBoost, 0f, 1f);
                    }

                    _tint = EditorGUILayout.ColorField("Tint", _tint);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        _attachLodStreamer = EditorGUILayout.ToggleLeft(
                            new GUIContent("LOD Streamer 부착",
                                "카메라 거리 기반으로 LOD 자동 스위치 (히스테리시스 포함)"),
                            _attachLodStreamer);
                        _frameCameraAfterInstantiate = EditorGUILayout.ToggleLeft(
                            new GUIContent("자동 카메라 프레이밍", "인스턴스화 직후 카메라 정렬"),
                            _frameCameraAfterInstantiate);
                    }
                }
            }
            EditorGUILayout.Space(2);
        }

        // ── Actions ───────────────────────────────────────────────────────
        void _ActionsSection()
        {
            EditorGUILayout.LabelField("⚡ Actions", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                {
                    var active = _ActiveSlots();
                    var big = new GUIStyle(GUI.skin.button)
                    { fontSize = 12, fontStyle = FontStyle.Bold, fixedHeight = 34 };

                    using (new EditorGUI.DisabledScope(active.Count == 0))
                    {
                        var prev = GUI.backgroundColor;
                        GUI.backgroundColor = new Color(0.45f, 0.85f, 0.55f);
                        if (GUILayout.Button($"▶  월드로 인스턴스화  ({active.Count} 씬)", big))
                            _Instantiate(active);
                        GUI.backgroundColor = prev;
                    }
                    EditorGUILayout.Space(3);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("플랜 미리보기 (Console)", GUILayout.Height(24)))
                            _PreviewMergePlan();
                        if (GUILayout.Button("카메라 포커스", GUILayout.Height(24), GUILayout.Width(100)))
                            _FrameCameraToExisting();
                    }

                    var redPrev = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(0.85f, 0.55f, 0.55f);
                    if (GUILayout.Button("LCC GameObject 모두 제거", GUILayout.Height(22)))
                        _ClearAllInstances();
                    GUI.backgroundColor = redPrev;
                }
            }
            EditorGUILayout.Space(2);
        }

        // ── Python backend server ─────────────────────────────────────────
        void _ServerSection()
        {
            EditorGUILayout.LabelField($"🐍 Python 백엔드 · {(_serverHealthy ? "online" : "offline")}", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                {
                    EditorGUILayout.LabelField(
                        "실행.bat 의 Python 설치·실행을 창 안에서 처리. 3D 스캔 비교 기능을 쓰려면 이 서버가 필요합니다.",
                        EditorStyles.wordWrappedMiniLabel);
                    EditorGUILayout.Space(3);

                    LccServerManager.PythonPath = EditorGUILayout.TextField(
                        new GUIContent("Python", "'python' 이면 PATH 탐색. 절대 경로도 가능."),
                        LccServerManager.PythonPath);
                    LccServerManager.Port = EditorGUILayout.IntField(
                        new GUIContent("Port", "기본 8001 — 충돌 시 변경"),
                        LccServerManager.Port);

                    EditorGUILayout.Space(3);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("1) Install deps", GUILayout.Height(26)))
                            _InstallDeps();

                        bool running = LccServerManager.IsRunning();
                        var prev = GUI.backgroundColor;
                        if (!running)
                        {
                            GUI.backgroundColor = new Color(0.45f, 0.85f, 0.55f);
                            if (GUILayout.Button("2) Start server", GUILayout.Height(26)))
                                _StartServer();
                        }
                        else
                        {
                            GUI.backgroundColor = new Color(0.85f, 0.55f, 0.55f);
                            if (GUILayout.Button("Stop server", GUILayout.Height(26)))
                                _StopServer();
                        }
                        GUI.backgroundColor = prev;

                        if (GUILayout.Button("🔄 Health", GUILayout.Height(26), GUILayout.Width(90)))
                            _HealthCheck();
                        if (GUILayout.Button("📖 Docs", GUILayout.Height(26), GUILayout.Width(70)))
                            LccServerManager.OpenBrowser();
                    }

                    EditorGUILayout.Space(3);
                    if (!string.IsNullOrEmpty(_serverInfo))
                        EditorGUILayout.LabelField("상태: " + _serverInfo, EditorStyles.miniLabel);

                    if (!string.IsNullOrEmpty(_serverLog))
                    {
                        EditorGUILayout.LabelField("설치 로그 (끝 10줄):", EditorStyles.miniBoldLabel);
                        var shown = _TailLines(_serverLog, 10);
                        using (new EditorGUI.DisabledScope(true))
                            EditorGUILayout.TextArea(shown, GUILayout.MinHeight(60));
                    }
                }
            }
            EditorGUILayout.Space(2);
        }

        // ── v2 API compare ────────────────────────────────────────────────
        void _ApiCompareSection()
        {
            EditorGUILayout.LabelField("📐 3D 스캔 비교 (Hausdorff)", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                {
                    using (new EditorGUI.DisabledScope(!_serverHealthy))
                    {
                        if (!_serverHealthy)
                            EditorGUILayout.HelpBox("서버 오프라인 — 위 '🐍 Python 백엔드' 에서 Start 하세요.", MessageType.Warning);

                        _cmpReferencePly = EditorGUILayout.TextField(
                            new GUIContent("reference PLY",
                                "기존 3D 스캔본 PLY 경로 (절대경로)"),
                            _cmpReferencePly);
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField("비교 LOD", GUILayout.Width(60));
                            _cmpLod = EditorGUILayout.IntSlider(_cmpLod, 0, 4);
                        }
                        _cmpSample = EditorGUILayout.IntSlider("Sample N", _cmpSample, 10000, 500000);

                        var sc = _SelectedScene();
                        GUI.enabled = _serverHealthy && !_cmpBusy && sc != null
                                      && !string.IsNullOrEmpty(_cmpReferencePly);
                        var prev = GUI.backgroundColor;
                        GUI.backgroundColor = new Color(0.55f, 0.75f, 1.0f);
                        if (GUILayout.Button(_cmpBusy ? "⏳ 비교 중..." : "▶ 비교 실행",
                                             GUILayout.Height(28)))
                            _RunCompare(sc);
                        GUI.backgroundColor = prev;
                        GUI.enabled = true;
                    }

                    if (!string.IsNullOrEmpty(_cmpResult))
                    {
                        EditorGUILayout.Space(3);
                        using (new EditorGUI.DisabledScope(true))
                            EditorGUILayout.TextArea(_cmpResult, GUILayout.MinHeight(90));
                    }
                }
            }
            EditorGUILayout.Space(2);
        }

        // ── Inspect selected scene ────────────────────────────────────────
        void _InspectSelectedSection()
        {
            var s = _SelectedScene();
            EditorGUILayout.LabelField("🔍 Selected scene" + (s != null ? " · " + s.name : ""),
                                       EditorStyles.boldLabel);
            if (s == null)
            {
                EditorGUILayout.HelpBox("Scenes 탭에서 ▸ 버튼을 눌러 씬을 선택하면 여기 정보가 뜹니다.", MessageType.Info);
                return;
            }
            if (s.manifest == null) { EditorGUILayout.HelpBox("매니페스트 파싱 실패", MessageType.Warning); return; }

            var m = s.manifest;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("name", m.name);
                EditorGUILayout.LabelField("totalSplats", m.totalSplats.ToString("N0"));
                EditorGUILayout.LabelField("source / type", $"{m.source} / {m.dataType}");
                EditorGUILayout.LabelField("LOD count", m.totalLevel.ToString());
                if (m.splats != null)
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < m.splats.Length; i++)
                        sb.Append(i == 0 ? "" : ", ").Append(m.splats[i].ToString("N0"));
                    EditorGUILayout.LabelField("splats/LOD", sb.ToString());
                }
                if (m.boundingBox != null)
                {
                    var sz = new Vector3(m.boundingBox.max[0] - m.boundingBox.min[0],
                                         m.boundingBox.max[1] - m.boundingBox.min[1],
                                         m.boundingBox.max[2] - m.boundingBox.min[2]);
                    EditorGUILayout.LabelField("bbox size", $"{sz.x:F2} × {sz.y:F2} × {sz.z:F2} m");
                }
                EditorGUILayout.LabelField("encoding / epsg", $"{m.encoding} / {m.epsg}");
                EditorGUILayout.LabelField("data.bin",
                    System.IO.File.Exists(s.DataBinPath) ? "✓ exists" : "✗ missing");
            }
        }

        // ── Helpers / actions ─────────────────────────────────────────────
        List<SceneSlot> _ActiveSlots()
        {
            var a = new List<SceneSlot>();
            foreach (var s in _slots) if (s.enabled && s.scene != null) a.Add(s);
            return a;
        }
        LccScene _SelectedScene()
            => (_selectedIndex >= 0 && _selectedIndex < _slots.Count) ? _slots[_selectedIndex].scene : null;

        void _PreviewMergePlan()
        {
            var scenes = new List<LccScene>();
            foreach (var s in _ActiveSlots()) scenes.Add(s.scene);
            if (scenes.Count == 0) { ShowNotification(new GUIContent("씬이 없습니다.")); return; }
            var plan = LccSceneMerger.Plan(scenes);
            UnityEngine.Debug.Log($"[LCC] === 합치기 플랜 ({plan.Count} 씬) ===");
            foreach (var p in plan)
                UnityEngine.Debug.Log($"  {p.scene?.name} → world=({p.worldOffset.x:F2},{p.worldOffset.y:F2},{p.worldOffset.z:F2}) " +
                                      $"rot={p.rotation} scale={p.scale}");
            ShowNotification(new GUIContent($"플랜 출력 ({plan.Count}) → Console"));
        }

        void _Instantiate(List<SceneSlot> active)
        {
            var root = GameObject.Find("__LccRoot") ?? new GameObject("__LccRoot");
            var scenes = new List<LccScene>();
            foreach (var s in active) scenes.Add(s.scene);
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
                    r.scene = p.scene; r.lodLevel = _lodLevel;
                    r.scaleMultiplier = _scaleMultiplier; r.opacityBoost = _opacityBoost; r.tint = _tint;
                    r.enabled = false; r.enabled = true;
                    if (_attachLodStreamer)
                    {
                        var s = go.AddComponent<LccLodStreamer>();
                        s.targetRenderer = r;
                    }
                }
                else
                {
                    var r = go.AddComponent<LccPointCloudRenderer>();
                    r.scene = p.scene; r.lodLevel = _lodLevel; r.tint = _tint;
                    r.enabled = false; r.enabled = true;
                    if (_attachLodStreamer)
                    {
                        var s = go.AddComponent<LccLodStreamer>();
                        s.targetRenderer = r;
                    }
                }
                created++;
            }
            Undo.RegisterCreatedObjectUndo(root, "Instantiate LCC Scenes");
            UnityEngine.Debug.Log($"[LCC] {created} scenes under __LccRoot ({_renderMode}, LOD {_lodLevel}).");

            if (_frameCameraAfterInstantiate) _FrameCameraToExisting();
            ShowNotification(new GUIContent($"{created} 씬 배치 완료"));
        }

        void _ClearAllInstances()
        {
            foreach (var n in new[] { "__LccRoot", "__LccMergeRoot", "__LccMerge_A", "__LccMerge_B",
                                      "__LccTest", "__LccSplatTest" })
            {
                var g = GameObject.Find(n);
                if (g != null) Undo.DestroyObjectImmediate(g);
            }
            ShowNotification(new GUIContent("LCC 오브젝트 제거"));
        }

        void _FrameCameraToExisting()
        {
            var root = GameObject.Find("__LccRoot");
            if (root == null) return;
            Bounds? combined = null;
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
            {
                if (!combined.HasValue) combined = r.bounds;
                else { var b = combined.Value; b.Encapsulate(r.bounds); combined = b; }
            }
            if (!combined.HasValue) return;
            var cam = Camera.main ?? FindFirstObjectByType<Camera>();
            if (cam == null) return;
            var c = combined.Value.center;
            float s = Mathf.Max(combined.Value.size.x, Mathf.Max(combined.Value.size.y, combined.Value.size.z));
            cam.transform.position = c + new Vector3(s * 0.9f, s * 0.6f, -s * 0.9f);
            cam.transform.LookAt(c);
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = Mathf.Max(cam.farClipPlane, s * 5f);
            SceneView.lastActiveSceneView?.Frame(combined.Value, false);
        }

        // ── Server actions ────────────────────────────────────────────────
        void _HealthCheck()
        {
            _lastHealthTime = EditorApplication.timeSinceStartup;
            LccServerManager.HealthCheckAsync((ok, msg) =>
            {
                _serverHealthy = ok;
                _serverInfo = ok ? (msg.Length > 140 ? msg.Substring(0, 140) + "..." : msg)
                                 : (string.IsNullOrEmpty(msg) ? "offline" : "offline — " + msg);
                Repaint();
            });
        }
        void _StartServer()
        {
            if (!LccServerManager.ServerFilesExist())
            {
                EditorUtility.DisplayDialog("LCC",
                    "Server~/ 폴더의 server.py 를 찾을 수 없습니다. 패키지 재설치가 필요합니다.", "OK");
                return;
            }
            if (LccServerManager.StartServer(out var err))
            {
                EditorApplication.delayCall += () => { System.Threading.Thread.Sleep(1500); _HealthCheck(); };
                ShowNotification(new GUIContent("server starting..."));
            }
            else UnityEngine.Debug.LogError("[LCC] start failed: " + err);
        }
        void _StopServer()
        {
            if (LccServerManager.StopServer(out var err)) { _serverHealthy = false; _serverInfo = "stopped"; Repaint(); }
            else UnityEngine.Debug.LogError("[LCC] stop failed: " + err);
        }
        void _InstallDeps()
        {
            _serverLog = "running: python -m pip install -r requirements.txt ...\n";
            Repaint();
            EditorApplication.delayCall += () =>
            {
                int rc = LccServerManager.InstallDependencies(line =>
                    { _serverLog += line + "\n"; Repaint(); });
                _serverLog += rc == 0 ? "[ok] install complete\n" : $"[fail] exit {rc}\n";
                Repaint();
            };
        }
        static string _TailLines(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var arr = s.Split('\n');
            int start = System.Math.Max(0, arr.Length - n);
            var sb = new StringBuilder();
            for (int i = start; i < arr.Length; i++) sb.AppendLine(arr[i]);
            return sb.ToString();
        }

        // ── Compare ───────────────────────────────────────────────────────
        void _RunCompare(LccScene sc)
        {
            _cmpBusy = true;
            _cmpResult = "요청 전송 중...";
            var payload = $"{{\"lcc_directory\":\"{sc.rootPath.Replace("\\","/")}\","
                        + $"\"reference_ply\":\"{_cmpReferencePly.Replace("\\","/")}\","
                        + $"\"lod\":{_cmpLod},\"sample\":{_cmpSample}}}";

            var req = new UnityWebRequest(LccServerManager.BaseUrl + "/api/lcc/compare", "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                try
                {
                    if (req.result != UnityWebRequest.Result.Success)
                    { _cmpResult = "❌ " + req.error + "\n" + req.downloadHandler.text; return; }
                    var r = JsonUtility.FromJson<CompareResp>(req.downloadHandler.text);
                    _cmpResult =
                        $"n_lcc = {r.n_lcc:N0}   n_ref = {r.n_ref:N0}\n" +
                        $"chamfer symmetric = {r.chamfer_symmetric:F4} m\n" +
                        $"Hausdorff         = {r.hausdorff:F4} m\n" +
                        $"RMS               = {r.rms:F4} m\n" +
                        $"p50 / p90 / p99   = {r.p50:F3} / {r.p90:F3} / {r.p99:F3} m\n" +
                        $"elapsed           = {r.elapsed_sec:F2} s";
                }
                catch (System.Exception e) { _cmpResult = "parse error: " + e.Message; }
                finally { _cmpBusy = false; req.Dispose(); Repaint(); }
            };
        }

        [System.Serializable]
        class CompareResp
        {
            public int n_lcc, n_ref;
            public float chamfer_symmetric, hausdorff, rms, p50, p90, p99, elapsed_sec;
        }
    }
}
