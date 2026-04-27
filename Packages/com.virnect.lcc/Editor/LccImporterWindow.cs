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
                }
            }
            EditorGUILayout.Space(2);
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

        void _ColliderTab()
        {
            // ── A. In-Editor 베이크 (Python 서버 불필요) ────────────────
            EditorGUILayout.LabelField("🎮 In-Editor 메쉬 콜라이더 (LCC proxy PLY 직결)", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.HelpBox(
                    "LCC 가 함께 export 한 mesh-files/<scene>.ply 를 읽어서\n" +
                    " · LCC_Generated/<scene>_ProxyMesh.asset 생성\n" +
                    " · 씬의 Splat_<scene> 자식 __LccCollider 의 MeshCollider 에 자동 연결\n" +
                    "(서버/네트워크/Python 의존성 없음)",
                    MessageType.Info);
                using (new EditorGUILayout.HorizontalScope())
                {
                    var prev = GUI.backgroundColor; GUI.backgroundColor = kAccent;
                    if (GUILayout.Button("▶ 현재 씬 자동 베이크", GUILayout.Height(28)))
                        LccColliderBuilder.Menu_BakeActiveScene();
                    GUI.backgroundColor = prev;
                    if (GUILayout.Button("Scene5 열고 자동 베이크", GUILayout.Height(28)))
                        LccColliderBuilder.Menu_OpenScene5AndBake();
                }
                if (GUILayout.Button("자산 강제 재생성 (현재 씬)", GUILayout.Height(22)))
                    LccColliderBuilder.Menu_BakeActiveSceneRebuild();
            }
            EditorGUILayout.Space(6);

            // ── B. v1 백엔드 (Poisson/BPA 재구성 — point cloud → mesh) ──
            EditorGUILayout.LabelField("🐍 v1 서버 메쉬 콜라이더 (포인트클라우드 재구성)", EditorStyles.boldLabel);
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
