using System;
using System.Collections.Generic;
using System.IO;
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

        // Tabs — LCC + v1 5-page 흡수
        enum Tab { Scenes, Optimize, Collider, Mesh, Bake, Photo, Server, Compare, Info }
        Tab _tab = Tab.Scenes;

        // ── v1 tab states ─────────────────────────────────────────────────
        string _v1InputFile = "";
        string _v1OutputDir = "";
        bool   _v1Busy = false;
        string _v1Log = "";
        string _v1SessionId = "";

        // Optimize (page 1)
        bool _optQ60 = true, _optQ40 = true, _optQ20 = true;

        // Mesh (page 3)
        int   _meshPreset = 1;       // 0=LOD1, 1=LOD2, 2=LOD3
        int   _meshSmoothHard = 0;   // 0=smooth, 1=hard
        bool  _meshMirrorX = false;
        string _meshFormat = "obj";  // obj / fbx / glb

        // Bake (page 4)
        string _bakePly = "", _bakeMesh = "";
        int    _bakeRes = 2048;
        bool   _bakeLighting = true, _bakeHdri = false;

        // PhotoTex (page 5)
        string _photoMesh = "", _photoImagesDir = "";
        int    _photoRes = 2048;

        // Collider (page 2)
        string _colliderMode = "box";  // box / mesh

        // Palette — 무채색 + 하늘색 포인트 컬러 (v1 실행파일 디자인 일치)
        static readonly Color kAccent    = new Color(0.55f, 0.82f, 1.00f); // sky blue
        static readonly Color kAccentDim = new Color(0.35f, 0.55f, 0.75f);

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
                    _ScenesSection(); _RenderSettingsSection(); _ActionsSection();
                    break;
                case Tab.Optimize: _OptimizeTab();   break;
                case Tab.Collider: _ColliderTab();   break;
                case Tab.Mesh:     _MeshTab();       break;
                case Tab.Bake:     _BakeTab();       break;
                case Tab.Photo:    _PhotoTab();      break;
                case Tab.Server:   _ServerSection(); break;
                case Tab.Compare:  _ApiCompareSection(); break;
                case Tab.Info:     _QuickStartSection(); _InspectSelectedSection(); break;
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
              normal = { textColor = kAccent } };

            int active = 0; foreach (var s in _slots) if (s.enabled && s.scene != null) active++;
            string scenesLabel  = $"📂 Scenes ({_slots.Count})";
            string serverLabel  = _serverHealthy ? "🐍 Server ●" : "🐍 Server ○";
            string compareLabel = "📐 Compare";
            string infoLabel    = "🔍 Info";

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _TabButton(Tab.Scenes,   scenesLabel,   tabStyle, activeStyle);
                _TabButton(Tab.Optimize, "📦 자동처리",   tabStyle, activeStyle);
                _TabButton(Tab.Collider, "🎮 콜라이더",  tabStyle, activeStyle);
                _TabButton(Tab.Mesh,     "🔺 메쉬 변환", tabStyle, activeStyle);
                _TabButton(Tab.Bake,     "🖼 베이크",    tabStyle, activeStyle);
                _TabButton(Tab.Photo,    "📷 사진텍스처", tabStyle, activeStyle);
                _TabButton(Tab.Server,   serverLabel,   tabStyle, activeStyle);
                _TabButton(Tab.Compare,  compareLabel,  tabStyle, activeStyle);
                _TabButton(Tab.Info,     infoLabel,     tabStyle, activeStyle);
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

                // Server status pill (무채색 + 하늘색만)
                string pill = _serverHealthy ? "● API online" : "○ API offline";
                var style = new GUIStyle(EditorStyles.toolbarButton)
                {
                    normal = { textColor = _serverHealthy ? kAccent : new Color(0.55f,0.55f,0.55f) },
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
                            if (GUILayout.Button("✕", GUILayout.Width(24))) removeIdx = i;
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
                        GUI.backgroundColor = kAccent;  // 하늘색 포인트 컬러
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

                    // 삭제 버튼은 무채색 (v1 처럼 강조 없음)
                    if (GUILayout.Button("LCC GameObject 모두 제거", GUILayout.Height(22)))
                        _ClearAllInstances();
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
                        GUI.backgroundColor = running ? Color.white : kAccent;
                        if (!running)
                        {
                            if (GUILayout.Button("2) Start server", GUILayout.Height(26)))
                                _StartServer();
                        }
                        else
                        {
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
            EditorGUILayout.Space(6);

            // v1 루트 폴더 설정 — v1 서버를 띄우려면 필요한 경로
            EditorGUILayout.LabelField("◆ v1 백엔드 루트 (PointCloudOptimizer 폴더)", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    "'Start server' 를 누르면 이 폴더의 backend/app.py 를 uvicorn 으로 기동. 5페이지(최적화/콜라이더/메쉬/베이크/사진) 전 기능이 LCC Importer 의 각 탭에서 바로 사용 가능합니다.",
                    EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    LccServerManager.V1Root = EditorGUILayout.TextField("v1 루트",
                        LccServerManager.V1Root);
                    if (GUILayout.Button("…", GUILayout.Width(32)))
                    {
                        var p = EditorUtility.OpenFolderPanel("v1 PointCloudOptimizer 폴더", "", "");
                        if (!string.IsNullOrEmpty(p)) LccServerManager.V1Root = p.Replace("\\","/");
                    }
                }
                EditorGUILayout.LabelField($"Python: {LccServerManager.V1EffectivePython}",
                                           EditorStyles.miniLabel);
                if (GUILayout.Button("v1 웹 UI 열기 (브라우저)", GUILayout.Height(22)))
                    Application.OpenURL(LccServerManager.BaseUrl + "/");
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
                        GUI.backgroundColor = kAccent;  // 하늘색 포인트 컬러 (주요 액션)
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

                    if (_cmpHistCounts != null && _cmpHistCounts.Length > 0)
                    {
                        EditorGUILayout.Space(4);
                        EditorGUILayout.LabelField("거리 분포 (A→B, 0..p99)", EditorStyles.miniBoldLabel);
                        _DrawHistogram(_cmpHistBins, _cmpHistCounts, _cmpP50, _cmpP90, _cmpP99);
                    }
                }
            }
            EditorGUILayout.Space(2);
        }

        // ── Hausdorff 히스토그램 렌더링 ────────────────────────────────────
        float[] _cmpHistBins;
        int[]   _cmpHistCounts;
        float   _cmpP50, _cmpP90, _cmpP99;

        void _DrawHistogram(float[] bins, int[] counts, float p50, float p90, float p99)
        {
            const float h = 90f;
            var rect = GUILayoutUtility.GetRect(0, h, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, new Color(0.12f, 0.12f, 0.12f, 1f));

            int n = counts.Length;
            if (n == 0 || bins == null || bins.Length < 2) return;
            int maxC = 1;
            foreach (var c in counts) if (c > maxC) maxC = c;
            float xMin = bins[0], xMax = bins[bins.Length - 1];
            float xRange = Mathf.Max(1e-6f, xMax - xMin);

            float w = rect.width / n;
            // 막대
            for (int i = 0; i < n; i++)
            {
                float frac = counts[i] / (float)maxC;
                float bh   = Mathf.Max(1f, frac * (h - 14f));
                var bar = new Rect(rect.x + i * w, rect.yMax - bh - 2, Mathf.Max(1f, w - 1f), bh);
                EditorGUI.DrawRect(bar, new Color(0.55f, 0.82f, 1.00f, 0.85f));  // kAccent
            }

            // 퍼센타일 마커 (p50/p90/p99)
            DrawMarker(rect, xMin, xRange, p50, new Color(0.4f, 1f, 0.4f, 0.9f), "p50");
            DrawMarker(rect, xMin, xRange, p90, new Color(1f, 0.85f, 0.3f, 0.9f), "p90");
            DrawMarker(rect, xMin, xRange, p99, new Color(1f, 0.4f, 0.4f, 0.9f), "p99");

            // x축 레이블
            var lblStyle = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(0.7f,0.7f,0.7f) } };
            GUI.Label(new Rect(rect.x, rect.yMax - 12, 60, 12), $"0", lblStyle);
            var rightStyle = new GUIStyle(lblStyle) { alignment = TextAnchor.MiddleRight };
            GUI.Label(new Rect(rect.xMax - 80, rect.yMax - 12, 78, 12), $"{xMax:F3}m", rightStyle);
        }

        static void DrawMarker(Rect rect, float xMin, float xRange, float val, Color col, string label)
        {
            if (val <= xMin) return;
            float t = Mathf.Clamp01((val - xMin) / xRange);
            float x = rect.x + t * rect.width;
            EditorGUI.DrawRect(new Rect(x, rect.y, 1.5f, rect.height), col);
            var st = new GUIStyle(EditorStyles.miniLabel)
            { normal = { textColor = col }, alignment = TextAnchor.UpperLeft };
            GUI.Label(new Rect(x + 2, rect.y, 36, 12), label, st);
        }

        // ════════════════════════════════════════════════════════════════
        //  v1 흡수 탭들 — 백엔드는 LccServerManager 가 띄운 v1 full server
        // ════════════════════════════════════════════════════════════════
        void _V1CommonInputOutput()
        {
            EditorGUILayout.BeginHorizontal();
            _v1InputFile = EditorGUILayout.TextField("입력 파일", _v1InputFile);
            if (GUILayout.Button("…", GUILayout.Width(32)))
            {
                var p = EditorUtility.OpenFilePanel("포인트 클라우드 선택", "",
                    "ply,xyz,pts,pcd,las,laz,obj,csv,txt,ptx");
                if (!string.IsNullOrEmpty(p)) _v1InputFile = p.Replace("\\", "/");
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _v1OutputDir = EditorGUILayout.TextField("출력 폴더", _v1OutputDir);
            if (GUILayout.Button("…", GUILayout.Width(32)))
            {
                var p = EditorUtility.OpenFolderPanel("출력 폴더", "", "");
                if (!string.IsNullOrEmpty(p)) _v1OutputDir = p.Replace("\\","/");
            }
            EditorGUILayout.EndHorizontal();
        }

        void _V1LogBox()
        {
            if (string.IsNullOrEmpty(_v1Log)) return;
            EditorGUILayout.Space(3);
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.TextArea(_v1Log, GUILayout.MinHeight(80));
        }

        bool _V1Ready()
        {
            if (!_serverHealthy) { EditorGUILayout.HelpBox("v1 서버 오프라인 — 'Server' 탭에서 Start Server.", MessageType.Warning); return false; }
            return true;
        }

        void _V1AppendLog(string msg) { _v1Log += msg + "\n"; Repaint(); }
        void _V1SetBusy(bool b) { _v1Busy = b; Repaint(); }

        // ──────── 📦 AUTOMATE (v1 전체 자동 처리) ────────────────────────
        //
        // v1 page 1 "품질별 PLY 생성" 은 브라우저 JS 만의 로직이라 Editor 직접 실행 불가.
        // 대신 /api/automate 엔드포인트를 호출 — 콜라이더 + 메쉬 + 베이크 전 자동 처리.
        // 완료 후 메쉬 OBJ 다운로드.
        void _OptimizeTab()
        {
            EditorGUILayout.LabelField("📦 전체 자동 처리 (v1 automate)", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!_V1Ready()) return;
                _V1CommonInputOutput();
                EditorGUILayout.HelpBox(
                    "자동 파이프라인: 메쉬 콜라이더 → 포인트→메쉬 변환 → (PLY 에 색상 있으면 텍스처 베이크) → 결과 OBJ 다운로드. 품질별 PLY 다운샘플은 v1 웹 UI 에서만 가능합니다.",
                    MessageType.Info);
                if (GUILayout.Button("웹 UI 에서 품질별 PLY 다운샘플 (v1 page 1)", GUILayout.Height(22)))
                    Application.OpenURL(LccServerManager.BaseUrl + "/");
                EditorGUILayout.Space(4);
                GUI.enabled = !_v1Busy && !string.IsNullOrEmpty(_v1InputFile);
                var prev = GUI.backgroundColor; GUI.backgroundColor = kAccent;
                if (GUILayout.Button(_v1Busy ? "⏳ 처리 중..." : "▶ 전체 자동 처리 (automate)", GUILayout.Height(30)))
                    _RunAutomate();
                GUI.backgroundColor = prev; GUI.enabled = true;
            }
            _V1LogBox();
        }

        void _RunAutomate()
        {
            _V1SetBusy(true); _v1Log = ""; _V1AppendLog("업로드 중: " + _v1InputFile);
            LccV1Client.UploadPath(_v1InputFile, (sid, err) =>
            {
                if (err != null) { _V1AppendLog("❌ upload: " + err); _V1SetBusy(false); return; }
                _V1AppendLog("sid=" + sid + " · automate 파이프라인 시작 (콜라이더+메쉬+베이크)");
                var body = "{}";
                LccV1Client.PostJsonReadSse(LccV1Client.BaseUrl + "/api/automate/" + sid, body,
                    finalEvent => {
                        _V1AppendLog("✓ automate 완료: " + finalEvent);
                        // Download mesh OBJ
                        string outDir = string.IsNullOrEmpty(_v1OutputDir)
                            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
                            : _v1OutputDir;
                        var savePath = Path.Combine(outDir,
                            LccV1Client.SafeStem(_v1InputFile) + "_automate.obj");
                        LccV1Client.DownloadBinary(LccV1Client.BaseUrl + "/api/mesh/" + sid, savePath,
                            bytes => { _V1AppendLog($"✓ OBJ 저장: {savePath} ({bytes/1024.0/1024.0:F1} MB)"); _V1SetBusy(false); },
                            e => { _V1AppendLog("⚠ OBJ download skipped: " + e); _V1SetBusy(false); });
                    },
                    msg => { _V1AppendLog("❌ " + msg); _V1SetBusy(false); });
            });
        }

        // ──────── 🎮 COLLIDER (v1 page 2) ────────────────────────────────
        // /api/mesh-collider 는 단독으로 실행 가능 — Poisson/BPA 기반 메쉬 콜라이더 생성.
        // /api/auto/collider 는 automate 가 먼저 돌아야 하므로 여기서는 사용 안 함.
        bool _colliderConvexParts = false;
        int  _colliderDepth = 7;
        int  _colliderTargetTris = 3000;

        // Bulk regen — LccGroup 의 모든 Splat 자식에 대해 백엔드 콜라이더 재생성
        GameObject _bulkColRoot;
        string _bulkColPlyDir = "";
        int    _bulkColTargetTris = 8000;
        int    _bulkColDepth = 8;
        bool   _bulkColKeepAll = true;          // density_trim=0, keep_fragments=true, max_edge_ratio=0
        bool   _bulkColZupToYup = true;         // XGrids/photogrammetry PLY 좌표계 보정
        bool   _bulkColShowMesh = true;         // 디버그용 MeshRenderer 추가
        bool   _bulkColReplaceExisting = true;  // 기존 __LccCollider 자식 제거
        bool   _bulkColAutoClear = true;        // 재생성 전에 자동 정리
        bool   _bulkColClearAssets = false;     // 자동 정리 시 캐시 .asset 까지 삭제
        int    _bulkColRunId = 0;               // 취소/덮어쓰기용 generation counter

        // 입력 소스 — XGrids proxy PLY 가 작은 경우 splat 본체를 직접 추출
        enum BulkColSource { ProxyPly, LccSplat }
        BulkColSource _bulkColSource = BulkColSource.LccSplat;
        int _bulkColLod = 1;          // LCC LOD 레벨 (0 = 풀, 1~4 = 점차 다운샘플)
        int _bulkColMaxPts = 200000;  // 콜라이더 입력으로 충분 (Poisson 처리 시간 ↓)

        void _ColliderTab()
        {
            EditorGUILayout.LabelField("🎮 유니티 메쉬 콜라이더 (v1 page 2)", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!_V1Ready()) return;
                _V1CommonInputOutput();
                _colliderDepth      = EditorGUILayout.IntSlider("Poisson depth", _colliderDepth, 5, 10);
                _colliderTargetTris = EditorGUILayout.IntSlider("target 삼각형",  _colliderTargetTris, 500, 20000);
                _colliderConvexParts = EditorGUILayout.ToggleLeft(
                    new GUIContent("ACD 분해 (다중 Convex 파트 — Unity 동적 Rigidbody 호환)",
                        "체크하면 mesh 를 여러 convex part 로 분해해 반환. Unity 의 물리 엔진에서 동적 콜라이더로 쓰기 좋음."),
                    _colliderConvexParts);
                EditorGUILayout.HelpBox(
                    "Poisson+ICP 기반 메쉬 콜라이더를 JSON 으로 저장. 수동 드래그 배치·박스는 웹 UI 2페이지 사용.",
                    MessageType.Info);
                GUI.enabled = !_v1Busy && !string.IsNullOrEmpty(_v1InputFile);
                var prev = GUI.backgroundColor; GUI.backgroundColor = kAccent;
                if (GUILayout.Button(_v1Busy ? "⏳" : "▶ 자동 메쉬 콜라이더 생성 + JSON 저장", GUILayout.Height(28)))
                    _RunCollider();
                GUI.backgroundColor = prev; GUI.enabled = true;
                if (GUILayout.Button("웹 UI 에서 2페이지 열기 (수동 박스 드래그)", GUILayout.Height(22)))
                    Application.OpenURL(LccServerManager.BaseUrl + "/");
            }
            _BulkColliderSection();
            _V1LogBox();
        }

        void _RunCollider()
        {
            _V1SetBusy(true); _v1Log = ""; _V1AppendLog("업로드 중...");
            LccV1Client.UploadPath(_v1InputFile, (sid, err) =>
            {
                if (err != null) { _V1AppendLog("❌ upload: " + err); _V1SetBusy(false); return; }
                _V1AppendLog("sid=" + sid + " · mesh-collider 생성 중...");
                var url = LccV1Client.BaseUrl + "/api/mesh-collider/" + sid +
                          $"?method=poisson&depth={_colliderDepth}" +
                          $"&target_tris={_colliderTargetTris}&snap=2" +
                          $"&convex_parts={(_colliderConvexParts?"true":"false")}";
                var req = UnityWebRequest.Get(url); req.timeout = 600;
                var op = req.SendWebRequest();
                op.completed += _ =>
                {
                    try {
                        if (req.result != UnityWebRequest.Result.Success)
                        {
                            _V1AppendLog($"❌ HTTP {req.responseCode}: {req.downloadHandler.text}");
                            return;
                        }
                        string outDir = string.IsNullOrEmpty(_v1OutputDir)
                            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
                            : _v1OutputDir;
                        Directory.CreateDirectory(outDir);
                        var savePath = Path.Combine(outDir,
                            LccV1Client.SafeStem(_v1InputFile) + "_collider.json");
                        File.WriteAllText(savePath, req.downloadHandler.text);
                        long b = req.downloadHandler.text.Length;
                        _V1AppendLog($"✓ 저장: {savePath} ({b/1024.0:F1} KB)");
                    } finally { req.Dispose(); _V1SetBusy(false); }
                };
            });
        }

        // ──────── 🔁 BULK COLLIDER REGEN ────────────────────────────────
        // 대상 루트 (예: LccGroup) 의 모든 자식 (각 자식 = Splat_* 그룹) 에 대해
        // 백엔드 /api/mesh-collider 로 콜라이더를 재생성한 뒤, 자식의 __LccCollider 를 교체.
        void _BulkColliderSection()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("🔁 그룹 일괄 재생성 (XGrids 콜라이더 → 백엔드 메쉬-콜라이더)",
                EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _bulkColRoot = (GameObject)EditorGUILayout.ObjectField(
                    new GUIContent("대상 루트",
                        "이 GameObject 의 모든 자식 (각 자식 = 하나의 Splat 그룹) 에 대해 콜라이더 재생성. 보통 LccGroup."),
                    _bulkColRoot, typeof(GameObject), true);

                _bulkColSource = (BulkColSource)EditorGUILayout.EnumPopup(
                    new GUIContent("입력 소스",
                        "ProxyPly: mesh-files/*.ply (XGrids 자동 동봉, 작을 수 있음)\n" +
                        "LccSplat: data.bin 의 실제 splat 점을 LOD 별 추출 — 본체 전체 커버 (권장)"),
                    _bulkColSource);

                using (new EditorGUILayout.HorizontalScope())
                {
                    string label = _bulkColSource == BulkColSource.ProxyPly
                        ? "소스 PLY 폴더"
                        : "LCC 루트 폴더 (.lcc 들이 있는 곳)";
                    _bulkColPlyDir = EditorGUILayout.TextField(
                        new GUIContent(label,
                            _bulkColSource == BulkColSource.ProxyPly
                                ? "각 자식 이름과 매칭되는 .ply 가 있는 폴더 (예: <lcc>/mesh-files)"
                                : "각 Splat 자식 이름의 LCC 디렉토리들이 들어있는 부모 폴더. " +
                                  "예: <root>/ShinWon_1st_Cutter/data.bin · <root>/ShinWon_Facility_01/data.bin · ..."),
                        _bulkColPlyDir);
                    if (GUILayout.Button("📁", GUILayout.Width(30)))
                    {
                        var p = EditorUtility.OpenFolderPanel(label,
                            string.IsNullOrEmpty(_bulkColPlyDir) ? "" : _bulkColPlyDir, "");
                        if (!string.IsNullOrEmpty(p)) _bulkColPlyDir = p.Replace("\\", "/");
                    }
                }

                if (_bulkColSource == BulkColSource.LccSplat)
                {
                    _bulkColLod = EditorGUILayout.IntSlider(
                        new GUIContent("LCC LOD",
                            "0 = 최고 해상도 (느림, 큰 메모리), 1~2 = 콜라이더용 권장, 4 = 가장 가벼움"),
                        _bulkColLod, 0, 4);
                    _bulkColMaxPts = EditorGUILayout.IntSlider(
                        new GUIContent("최대 점 개수 (다운샘플)",
                            "LOD 추출 후 이 개수로 무작위 다운샘플. Poisson 입력은 50~200K 면 충분."),
                        _bulkColMaxPts, 50000, 1000000);
                }

                _bulkColDepth      = EditorGUILayout.IntSlider("Poisson depth", _bulkColDepth, 6, 10);
                _bulkColTargetTris = EditorGUILayout.IntSlider("target 삼각형", _bulkColTargetTris, 1000, 30000);

                _bulkColKeepAll = EditorGUILayout.ToggleLeft(
                    new GUIContent("전체 보존 모드 (density_trim=0, keep_fragments=true, max_edge_ratio=0)",
                        "체크 시 백엔드의 공격적 노이즈 제거를 끔 — 흩어진 splat 도 콜라이더에 포함. 건물 전체 덮을 때 ON."),
                    _bulkColKeepAll);
                _bulkColZupToYup = EditorGUILayout.ToggleLeft(
                    new GUIContent("Z-up → Y-up 변환 (XGrids/photogrammetry PLY)",
                        "PLY 가 Z-up 좌표계인 경우 Unity 의 Y-up 으로 자동 변환. XGrids mesh-files 는 보통 ON."),
                    _bulkColZupToYup);
                _bulkColShowMesh = EditorGUILayout.ToggleLeft(
                    new GUIContent("__LccCollider 에 MeshRenderer 추가 (디버그 시각화)",
                        "체크 시 콜라이더 메쉬가 씬에 반투명 녹색으로 표시됨."),
                    _bulkColShowMesh);
                _bulkColReplaceExisting = EditorGUILayout.ToggleLeft(
                    new GUIContent("기존 __LccCollider 자식 제거 후 새로 생성",
                        "체크 해제 시 동명 자식이 있어도 새로 만들지 않고 건너뜀."),
                    _bulkColReplaceExisting);

                EditorGUILayout.HelpBox(
                    "각 자식 이름의 'Splat_' 프리픽스를 떼고 폴더에서 동명 .ply 를 찾아 백엔드로 전송. " +
                    "결과 메쉬는 Assets/LCC_GeneratedColliders/ 에 .asset 으로 저장.",
                    MessageType.Info);

                bool ready = !_v1Busy && _bulkColRoot != null && !string.IsNullOrEmpty(_bulkColPlyDir);
                GUI.enabled = ready;
                var prev = GUI.backgroundColor; GUI.backgroundColor = kAccent;
                if (GUILayout.Button(_v1Busy ? "⏳ 처리 중..." : "▶ 그룹 콜라이더 일괄 재생성", GUILayout.Height(28)))
                    _RunBulkColliderRegen();
                GUI.backgroundColor = prev; GUI.enabled = true;
            }

            // ── 정리 / 취소 ─────────────────────────────────────────────
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("🧹 정리 · 캐시 비우기", EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _bulkColAutoClear = EditorGUILayout.ToggleLeft(
                    new GUIContent("재생성 전 자동 정리 (대상 루트 하위 __LccCollider 모두 제거)",
                        "체크 시 ▶ 버튼 누르면 기존 __LccCollider 들을 먼저 비우고 재생성. 꼬인 상태 방지."),
                    _bulkColAutoClear);
                _bulkColClearAssets = EditorGUILayout.ToggleLeft(
                    new GUIContent("자동 정리에 'Assets/LCC_GeneratedColliders/*.asset' 도 포함",
                        "체크 시 캐시된 콜라이더 메쉬 에셋과 머티리얼까지 모두 삭제. Stale 참조 100% 제거."),
                    _bulkColClearAssets);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUI.enabled = _bulkColRoot != null;
                    if (GUILayout.Button(new GUIContent("씬의 __LccCollider 제거",
                        "대상 루트 하위 모든 자식의 __LccCollider GameObject 를 삭제."), GUILayout.Height(22)))
                        _ClearSceneColliders(false);
                    if (GUILayout.Button(new GUIContent("씬 + 에셋 모두 제거",
                        "씬의 __LccCollider 와 Assets/LCC_GeneratedColliders/ 의 모든 메쉬·머티리얼 .asset 삭제."),
                        GUILayout.Height(22)))
                        _ClearSceneColliders(true);
                    GUI.enabled = true;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUI.enabled = _v1Busy;
                    if (GUILayout.Button(new GUIContent("⏹ 진행 중 작업 취소",
                        "현재 돌고 있는 일괄 재생성 루프를 중단. 이미 보낸 HTTP 요청 응답은 무시됨."),
                        GUILayout.Height(22)))
                        _CancelBulkRegen();
                    GUI.enabled = true;
                }
            }
        }

        void _ClearSceneColliders(bool alsoAssets)
        {
            if (_bulkColRoot == null)
            {
                EditorUtility.DisplayDialog("대상 루트 없음", "대상 루트를 먼저 지정하세요.", "OK");
                return;
            }
            int removedGo = 0;
            foreach (Transform c in _bulkColRoot.transform)
            {
                if (c == null) continue;
                var existing = c.Find("__LccCollider");
                if (existing == null) continue;
                // sharedMesh 참조부터 끊어 stale 회피
                var mc = existing.GetComponent<MeshCollider>();
                if (mc != null) mc.sharedMesh = null;
                var mf = existing.GetComponent<MeshFilter>();
                if (mf != null) mf.sharedMesh = null;
                Undo.DestroyObjectImmediate(existing.gameObject);
                removedGo++;
            }

            int deletedAssets = 0;
            if (alsoAssets)
            {
                const string dir = "Assets/LCC_GeneratedColliders";
                if (AssetDatabase.IsValidFolder(dir))
                {
                    var seen = new HashSet<string>();
                    foreach (var filter in new[] { "t:Mesh", "t:Material" })
                    {
                        var guids = AssetDatabase.FindAssets(filter, new[] { dir });
                        foreach (var g in guids)
                        {
                            var path = AssetDatabase.GUIDToAssetPath(g);
                            if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;
                            if (AssetDatabase.DeleteAsset(path)) deletedAssets++;
                        }
                    }
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                }
            }

            if (string.IsNullOrEmpty(_v1Log)) _v1Log = "";
            _V1AppendLog($"🧹 정리 — __LccCollider 제거 {removedGo}개" +
                         (alsoAssets ? $", 캐시 에셋 삭제 {deletedAssets}개" : ""));
            if (_bulkColRoot.scene.IsValid())
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(_bulkColRoot.scene);
        }

        void _CancelBulkRegen()
        {
            _bulkColRunId++;        // 진행 중 _ProcessNextBulk / 콜백은 mismatch 보고 abort
            _V1SetBusy(false);
            _V1AppendLog("⏹ 진행 중 작업 취소됨 (이후 들어오는 HTTP 응답 무시)");
        }

        void _RunBulkColliderRegen()
        {
            if (_bulkColRoot == null) return;
            if (!Directory.Exists(_bulkColPlyDir))
            {
                EditorUtility.DisplayDialog("PLY 폴더 없음",
                    "다음 폴더가 존재하지 않습니다:\n" + _bulkColPlyDir, "OK");
                return;
            }
            var children = new List<Transform>();
            foreach (Transform c in _bulkColRoot.transform) children.Add(c);
            if (children.Count == 0)
            {
                EditorUtility.DisplayDialog("자식 없음",
                    _bulkColRoot.name + " 아래에 자식 GameObject 가 없습니다.", "OK");
                return;
            }

            _v1Log = "";
            if (_bulkColAutoClear) _ClearSceneColliders(_bulkColClearAssets);

            int myRunId = ++_bulkColRunId;
            _V1AppendLog($"▶ 일괄 재생성 시작 (run #{myRunId}) — 자식 {children.Count}개, 소스: {_bulkColPlyDir}");
            _V1SetBusy(true);
            EditorApplication.delayCall += () => _ProcessNextBulk(children, 0, myRunId);
        }

        void _ProcessNextBulk(List<Transform> children, int idx, int runId)
        {
            if (runId != _bulkColRunId)
            {
                // 취소되었거나 새 실행으로 덮어써짐
                return;
            }
            if (idx >= children.Count)
            {
                _V1AppendLog($"✓ 완료 — {children.Count}개 항목 처리 (run #{runId})");
                _V1SetBusy(false);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                return;
            }
            var child = children[idx];
            if (child == null)
            {
                _ProcessNextBulk(children, idx + 1, runId);
                return;
            }

            string srcPath = _ResolveSourceForChild(child.name);
            if (srcPath == null)
            {
                _V1AppendLog($"  [{idx + 1}/{children.Count}] {child.name} — 소스 없음 ({_bulkColSource}), 건너뜀");
                _ProcessNextBulk(children, idx + 1, runId);
                return;
            }

            string srcLabel = _bulkColSource == BulkColSource.LccSplat
                ? $"LCC[{Path.GetFileName(srcPath)}, lod={_bulkColLod}]"
                : Path.GetFileName(srcPath);
            _V1AppendLog($"  [{idx + 1}/{children.Count}] {child.name} — 업로드: {srcLabel}");

            Action<string, string> onUpload = (sid, err) =>
            {
                if (runId != _bulkColRunId) return;
                if (err != null)
                {
                    _V1AppendLog($"    ❌ upload: {err}");
                    _ProcessNextBulk(children, idx + 1, runId);
                    return;
                }

                string url = LccV1Client.BaseUrl + "/api/mesh-collider/" + sid +
                             $"?method=poisson&depth={_bulkColDepth}" +
                             $"&target_tris={_bulkColTargetTris}&snap=2" +
                             $"&zup_to_yup={(_bulkColZupToYup ? "true" : "false")}" +
                             (_bulkColKeepAll
                                 ? "&density_trim=0&keep_fragments=true&max_edge_ratio=0"
                                 : "") +
                             "&flat=true";

                var req = UnityWebRequest.Get(url); req.timeout = 600;
                var op = req.SendWebRequest();
                op.completed += _ =>
                {
                    bool stillCurrent = runId == _bulkColRunId;
                    try
                    {
                        if (!stillCurrent) return;
                        if (req.result != UnityWebRequest.Result.Success)
                        {
                            _V1AppendLog($"    ❌ HTTP {req.responseCode}: {req.downloadHandler.text}");
                            return;
                        }
                        var resp = JsonUtility.FromJson<FlatColliderResponse>(req.downloadHandler.text);
                        if (resp == null || resp.verts_flat == null || resp.tris_flat == null
                            || resp.verts_flat.Length < 9 || resp.tris_flat.Length < 3)
                        {
                            _V1AppendLog("    ❌ 응답 비어있음 (verts/tris 부족)");
                            return;
                        }
                        _ApplyColliderToChild(child, resp);
                        _V1AppendLog($"    ✓ V={resp.verts_total} T={resp.tris_total}");
                    }
                    catch (Exception e) { if (stillCurrent) _V1AppendLog($"    ❌ {e.Message}"); }
                    finally
                    {
                        req.Dispose();
                        if (stillCurrent) _ProcessNextBulk(children, idx + 1, runId);
                    }
                };
            };

            if (_bulkColSource == BulkColSource.LccSplat)
                LccV1Client.UploadLccPath(srcPath, _bulkColLod, _bulkColMaxPts, onUpload);
            else
                LccV1Client.UploadPath(srcPath, onUpload);
        }

        string _ResolveSourceForChild(string childName)
        {
            return _bulkColSource == BulkColSource.LccSplat
                ? _ResolveLccDirForChild(childName)
                : _ResolvePlyForChild(childName);
        }

        string _ResolveLccDirForChild(string childName)
        {
            // 'Splat_' prefix 떼고 LCC 폴더 후보 검사.
            string baseName = childName.StartsWith("Splat_") ? childName.Substring(6) : childName;
            string[] candidates = { baseName, childName, "Splat_" + baseName };
            foreach (var c in candidates)
            {
                var dir = Path.Combine(_bulkColPlyDir, c);
                if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "data.bin")))
                    return dir.Replace("\\", "/");
                // .lcc 파일 직접 매칭도 시도
                var lccFile = Path.Combine(_bulkColPlyDir, c + ".lcc");
                if (File.Exists(lccFile)) return lccFile.Replace("\\", "/");
            }
            return null;
        }

        string _ResolvePlyForChild(string childName)
        {
            // 'Splat_' prefix 제거 후 매칭, 그 외 fallback 패턴.
            string[] candidates;
            if (childName.StartsWith("Splat_"))
            {
                var stripped = childName.Substring(6);
                candidates = new[] { stripped + ".ply", childName + ".ply" };
            }
            else
            {
                candidates = new[] { childName + ".ply", "Splat_" + childName + ".ply" };
            }
            foreach (var c in candidates)
            {
                var p = Path.Combine(_bulkColPlyDir, c);
                if (File.Exists(p)) return p.Replace("\\", "/");
            }
            return null;
        }

        void _ApplyColliderToChild(Transform parent, FlatColliderResponse resp)
        {
            int vc = resp.verts_flat.Length / 3;
            var verts = new Vector3[vc];
            for (int i = 0; i < vc; i++)
                verts[i] = new Vector3(
                    resp.verts_flat[i * 3 + 0],
                    resp.verts_flat[i * 3 + 1],
                    resp.verts_flat[i * 3 + 2]);

            var mesh = new Mesh();
            if (vc > 65535)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.triangles = resp.tris_flat;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.name = parent.name + "_collider";

            // 메쉬 에셋 저장
            const string assetDir = "Assets/LCC_GeneratedColliders";
            if (!AssetDatabase.IsValidFolder(assetDir))
                AssetDatabase.CreateFolder("Assets", "LCC_GeneratedColliders");
            var assetPath = $"{assetDir}/{parent.name}_collider.asset";
            // 기존 동명 에셋이 있으면 덮어씀
            var existingAsset = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            if (existingAsset != null) AssetDatabase.DeleteAsset(assetPath);
            AssetDatabase.CreateAsset(mesh, assetPath);

            // 기존 __LccCollider 자식 제거
            if (_bulkColReplaceExisting)
            {
                var existing = parent.Find("__LccCollider");
                if (existing != null) DestroyImmediate(existing.gameObject);
            }
            else if (parent.Find("__LccCollider") != null)
            {
                return;
            }

            var go = new GameObject("__LccCollider");
            Undo.RegisterCreatedObjectUndo(go, "Regen LCC Collider");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale    = Vector3.one;

            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;

            if (_bulkColShowMesh)
            {
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                const string matPath = "Assets/LCC_GeneratedColliders/__lcc_collider_mat.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null)
                {
                    var sh = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit");
                    mat = new Material(sh) { color = new Color(0.4f, 1.0f, 0.4f, 0.5f) };
                    AssetDatabase.CreateAsset(mat, matPath);
                }
                mr.sharedMaterial = mat;
            }

            EditorUtility.SetDirty(parent.gameObject);
            if (parent.gameObject.scene.IsValid())
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(parent.gameObject.scene);
        }

        [Serializable]
        class FlatColliderResponse
        {
            public string  mode;
            public float[] verts_flat;
            public int[]   tris_flat;
            public int     verts_total;
            public int     tris_total;
        }

        // ──────── 🔺 MESH (v1 page 3) ────────────────────────────────────
        void _MeshTab()
        {
            EditorGUILayout.LabelField("🔺 포인트 → 메쉬 변환 (v1 page 3)", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!_V1Ready()) return;
                _V1CommonInputOutput();
                _meshPreset = GUILayout.Toolbar(_meshPreset, new[] { "LOD1 완전 로우", "LOD2 중간 (권장)", "LOD3 고품질" });
                _meshSmoothHard = GUILayout.Toolbar(_meshSmoothHard, new[] { "스무스 (Poisson)", "하드 (Alpha)" });
                _meshMirrorX = EditorGUILayout.ToggleLeft("X축 미러", _meshMirrorX);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("저장 포맷", GUILayout.Width(80));
                    int fmtIdx = _meshFormat == "obj" ? 0 : (_meshFormat == "fbx" ? 1 : 2);
                    fmtIdx = GUILayout.Toolbar(fmtIdx, new[] { "OBJ", "FBX", "GLB" });
                    _meshFormat = fmtIdx == 0 ? "obj" : (fmtIdx == 1 ? "fbx" : "glb");
                }
                EditorGUILayout.Space(4);
                GUI.enabled = !_v1Busy && !string.IsNullOrEmpty(_v1InputFile);
                var prev = GUI.backgroundColor; GUI.backgroundColor = kAccent;
                if (GUILayout.Button(_v1Busy ? "⏳ 메쉬 생성 중..." : "▶ 메쉬 변환 실행", GUILayout.Height(30)))
                    _RunMesh();
                GUI.backgroundColor = prev; GUI.enabled = true;
            }
            _V1LogBox();
        }

        void _RunMesh()
        {
            _V1SetBusy(true); _v1Log = ""; _V1AppendLog("업로드 중...");
            LccV1Client.UploadPath(_v1InputFile, (sid, err) =>
            {
                if (err != null) { _V1AppendLog("❌ " + err); _V1SetBusy(false); return; }
                _V1AppendLog("sid=" + sid + " · 파이프라인...");
                // preset → (algorithm, mc_res, smooth_iter)
                string algo = _meshSmoothHard == 0 ? "poisson" : "bpa";
                int mcRes = new[] { 30, 45, 60 }[_meshPreset];
                int smoothIter = new[] { 0, 2, 3 }[_meshPreset];
                var body = $"{{\"algorithm\":\"{algo}\",\"denoise\":true,\"mc_res\":{mcRes}," +
                           $"\"smooth\":true,\"smooth_iter\":{smoothIter},\"quadify\":false," +
                           $"\"mirror_x\":{(_meshMirrorX?"true":"false")}}}";
                LccV1Client.PostJsonReadSse(LccV1Client.BaseUrl + "/api/process/" + sid, body,
                    finalEvent => {
                        _V1AppendLog("✓ 파이프라인 완료: " + finalEvent);
                        // final event may carry its own session_id — use that for download
                        string dlSid = sid;
                        var m = System.Text.RegularExpressions.Regex.Match(finalEvent,
                            "\"session_id\"\\s*:\\s*\"([^\"]+)\"");
                        if (m.Success) dlSid = m.Groups[1].Value;

                        string ep = _meshFormat == "obj" ? "/api/mesh/"
                                  : _meshFormat == "fbx" ? "/api/mesh-fbx/"
                                  : "/api/mesh-glb/";
                        string outDir = string.IsNullOrEmpty(_v1OutputDir)
                            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
                            : _v1OutputDir;
                        var savePath = Path.Combine(outDir,
                            LccV1Client.SafeStem(_v1InputFile) + "_mesh." + _meshFormat);
                        LccV1Client.DownloadBinary(LccV1Client.BaseUrl + ep + dlSid, savePath,
                            bytes => { _V1AppendLog($"✓ 저장: {savePath} ({bytes/1024.0:F0} KB)"); _V1SetBusy(false); },
                            e => { _V1AppendLog("❌ download: " + e); _V1SetBusy(false); });
                    },
                    msg => { _V1AppendLog("❌ " + msg); _V1SetBusy(false); });
            });
        }

        // ──────── 🖼 BAKE (v1 page 4) ───────────────────────────────────
        void _BakeTab()
        {
            EditorGUILayout.LabelField("🖼 텍스처 베이크 (v1 page 4)", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!_V1Ready()) return;
                EditorGUILayout.BeginHorizontal();
                _bakePly = EditorGUILayout.TextField("입력 PLY (색 포함)", _bakePly);
                if (GUILayout.Button("…", GUILayout.Width(32))) {
                    var p = EditorUtility.OpenFilePanel("PLY", "", "ply");
                    if (!string.IsNullOrEmpty(p)) _bakePly = p.Replace("\\","/");
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.BeginHorizontal();
                _bakeMesh = EditorGUILayout.TextField("메쉬 OBJ/FBX/GLB", _bakeMesh);
                if (GUILayout.Button("…", GUILayout.Width(32))) {
                    var p = EditorUtility.OpenFilePanel("Mesh", "", "obj,fbx,glb");
                    if (!string.IsNullOrEmpty(p)) _bakeMesh = p.Replace("\\","/");
                }
                EditorGUILayout.EndHorizontal();
                _bakeRes = EditorGUILayout.IntPopup("해상도", _bakeRes,
                    new[] { "512","1K","2K","4K" }, new[] { 512, 1024, 2048, 4096 });
                _bakeLighting = EditorGUILayout.ToggleLeft("라이팅 베이크 (실사느낌)", _bakeLighting);
                _bakeHdri = EditorGUILayout.ToggleLeft("HDRI 환경광 켜기", _bakeHdri);
                EditorGUILayout.HelpBox("⚠ v1 의 텍스처 베이크는 bake/upload + bake/run 2단계. Editor 에서는 업로드 필드가 2개 필요해 간소화 실행만 지원 (실패 시 웹 UI 사용).",
                    MessageType.Info);
                GUI.enabled = !_v1Busy && !string.IsNullOrEmpty(_bakePly) && !string.IsNullOrEmpty(_bakeMesh);
                var prev = GUI.backgroundColor; GUI.backgroundColor = kAccent;
                if (GUILayout.Button(_v1Busy ? "⏳" : "▶ 텍스처 생성 (웹 UI 권장)", GUILayout.Height(26)))
                    Application.OpenURL(LccServerManager.BaseUrl + "/");
                GUI.backgroundColor = prev; GUI.enabled = true;
            }
            _V1LogBox();
        }

        // ──────── 📷 PHOTO TEX (v1 page 5) ──────────────────────────────
        void _PhotoTab()
        {
            EditorGUILayout.LabelField("📷 사진 텍스처 투영 (v1 page 5)", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!_V1Ready()) return;
                EditorGUILayout.BeginHorizontal();
                _photoMesh = EditorGUILayout.TextField("메쉬 (OBJ/FBX/GLB/PLY)", _photoMesh);
                if (GUILayout.Button("…", GUILayout.Width(32))) {
                    var p = EditorUtility.OpenFilePanel("Mesh", "", "obj,fbx,glb,ply");
                    if (!string.IsNullOrEmpty(p)) _photoMesh = p.Replace("\\","/");
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.BeginHorizontal();
                _photoImagesDir = EditorGUILayout.TextField("사진 폴더 (4~10장)", _photoImagesDir);
                if (GUILayout.Button("…", GUILayout.Width(32))) {
                    var p = EditorUtility.OpenFolderPanel("사진 폴더", "", "");
                    if (!string.IsNullOrEmpty(p)) _photoImagesDir = p.Replace("\\","/");
                }
                EditorGUILayout.EndHorizontal();
                _photoRes = EditorGUILayout.IntPopup("해상도", _photoRes,
                    new[] { "1K","2K","4K" }, new[] { 1024, 2048, 4096 });
                EditorGUILayout.HelpBox("⚠ SfM(COLMAP) + UV 언랩 단계가 무거워 5~10 분 소요. Editor 에서는 웹 UI 오픈만 지원.",
                    MessageType.Info);
                var prev = GUI.backgroundColor; GUI.backgroundColor = kAccent;
                if (GUILayout.Button("▶ 웹 UI 의 5페이지에서 실행", GUILayout.Height(26)))
                    Application.OpenURL(LccServerManager.BaseUrl + "/");
                GUI.backgroundColor = prev;
            }
            _V1LogBox();
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
                        $"mean / RMS        = {r.mean:F4} / {r.rms:F4} m\n" +
                        $"p50 / p90 / p99   = {r.p50:F3} / {r.p90:F3} / {r.p99:F3} m\n" +
                        $"elapsed           = {r.elapsed_sec:F2} s";
                    _cmpHistBins   = r.hist_bins;
                    _cmpHistCounts = r.hist_counts;
                    _cmpP50 = r.p50; _cmpP90 = r.p90; _cmpP99 = r.p99;
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
            public float mean;
            public float[] hist_bins;
            public int[]   hist_counts;
        }
    }
}
