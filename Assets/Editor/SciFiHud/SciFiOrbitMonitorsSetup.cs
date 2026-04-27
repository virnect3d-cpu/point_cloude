using UnityEngine;
using UnityEngine.UI;
using UnityEditor;

// Player_VBot 자식으로 Sci-Fi UI atlas 기반 미니 모니터 4개를 캐릭터 주변에 배치.
// 메뉴: Tools/SciFi HUD/Setup orbiting monitors on Player_VBot
public static class SciFiOrbitMonitorsSetup
{
    const string PlayerName = "Player_VBot";
    const string RootName = "_OrbitMonitors";
    const string AtlasPath = "Assets/Sci-Fi UI/_SciFi_GUISkin_/atlas.png";
    const int MonitorCount = 4;
    const float OrbitRadius = 0.9f;     // m
    const float ChestHeight = 1.2f;     // m
    const float PanelSize = 0.35f;      // m (월드 단위)
    const float PanelHeightAspect = 0.62f; // 세로:가로

    [MenuItem("Tools/SciFi HUD/Setup orbiting monitors on Player_VBot")]
    public static void Setup()
    {
        var player = GameObject.Find(PlayerName);
        if (player == null)
        {
            EditorUtility.DisplayDialog("SciFi HUD", $"'{PlayerName}' 이 활성 씬에 없습니다.", "OK");
            return;
        }

        // 기존 _OrbitMonitors 있으면 제거 후 재생성
        var existing = player.transform.Find(RootName);
        if (existing != null)
        {
            if (!EditorUtility.DisplayDialog("SciFi HUD", $"'{RootName}' 이미 존재. 다시 만들까요?", "재생성", "취소"))
                return;
            Object.DestroyImmediate(existing.gameObject);
        }

        var sprites = LoadAtlasSprites();
        if (sprites == null) return;

        var rootGO = new GameObject(RootName);
        Undo.RegisterCreatedObjectUndo(rootGO, "Create OrbitMonitors root");
        rootGO.transform.SetParent(player.transform, false);
        rootGO.transform.localPosition = new Vector3(0, ChestHeight, 0);
        rootGO.transform.localRotation = Quaternion.identity;

        // 모니터 4개 — front / right / back / left
        for (int i = 0; i < MonitorCount; i++)
        {
            float angle = i * (360f / MonitorCount);
            CreateMonitor(rootGO.transform, i, angle, sprites);
        }

        Selection.activeGameObject = rootGO;
        EditorGUIUtility.PingObject(rootGO);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(player.scene);
        Debug.Log($"[SciFi HUD] {RootName} 생성 — Player_VBot 주변 {MonitorCount}개 모니터 (radius {OrbitRadius}m, height {ChestHeight}m)");
    }

    static AtlasSprites LoadAtlasSprites()
    {
        var all = AssetDatabase.LoadAllAssetsAtPath(AtlasPath);
        if (all == null || all.Length == 0)
        {
            EditorUtility.DisplayDialog("SciFi HUD", $"atlas 못 찾음: {AtlasPath}", "OK");
            return null;
        }
        var s = new AtlasSprites();
        foreach (var o in all)
        {
            if (o is Sprite sp)
            {
                switch (sp.name)
                {
                    case "window": s.window = sp; break;
                    case "window1": s.window1 = sp; break;
                    case "bar_blue1": s.barBlueH = sp; break;
                    case "bar_green1": s.barGreenH = sp; break;
                    case "bar_red1": s.barRedH = sp; break;
                    case "bar_purple1": s.barPurpleH = sp; break;
                    case "background_bar1": s.barBgH = sp; break;
                    case "progress_bar": s.progress = sp; break;
                    case "progress_bar_background": s.progressBg = sp; break;
                    case "joystikc": s.joystick = sp; break;
                    case "jotstick_back": s.joystickBack = sp; break;
                    case "rocket": s.iconRocket = sp; break;
                    case "bullets": s.iconBullets = sp; break;
                    case "plus": s.iconPlus = sp; break;
                    case "pause": s.iconPause = sp; break;
                }
            }
        }
        if (s.window == null)
        {
            EditorUtility.DisplayDialog("SciFi HUD", "atlas의 'window' sprite 못 찾음 — Sci-Fi UI 임포트 확인.", "OK");
            return null;
        }
        return s;
    }

    static void CreateMonitor(Transform parent, int index, float orbitAngleDeg, AtlasSprites s)
    {
        // 1) 패널 루트 (Canvas 부착) — 궤도 위치 + 빌보드 컴포넌트
        var go = new GameObject($"Monitor_{index:D2}");
        Undo.RegisterCreatedObjectUndo(go, "Create monitor panel");
        go.transform.SetParent(parent, false);

        // 궤도 좌표 — Y축 회전으로 XZ 평면 원형 배치
        Vector3 orbitPos = Quaternion.Euler(0, orbitAngleDeg, 0) * new Vector3(0, 0, OrbitRadius);
        go.transform.localPosition = orbitPos;
        go.transform.localRotation = Quaternion.Euler(0, orbitAngleDeg, 0);

        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 10;

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 4f;
        scaler.referencePixelsPerUnit = 100f;

        go.AddComponent<GraphicRaycaster>();

        // World Space Canvas의 RectTransform sizeDelta는 픽셀 단위 — scale로 월드 m로 환산
        var rt = (RectTransform)go.transform;
        float w = 800f;
        float h = 800f * PanelHeightAspect;
        rt.sizeDelta = new Vector2(w, h);
        rt.pivot = new Vector2(0.5f, 0.5f);
        // 월드 크기 = sizeDelta * lossyScale → scale로 PanelSize에 맞춤
        float scale = PanelSize / w;
        rt.localScale = new Vector3(scale, scale, scale);

        // 부유 위상 — 모니터마다 다르게
        var bb = go.AddComponent<OrbitMonitorBillboard>();
        bb.orbitSpeed = 8f;
        bb.floatAmplitude = 0.04f;
        bb.floatPeriod = 2.4f;
        bb.phase = (index / (float)MonitorCount) * Mathf.PI * 2f;

        // 2) 배경 윈도우
        var bgSprite = (index % 2 == 0) ? s.window : (s.window1 != null ? s.window1 : s.window);
        var bg = NewImage("Background", go.transform, bgSprite, Image.Type.Sliced);
        Stretch(bg.rectTransform);
        bg.color = new Color(0.6f, 0.85f, 1f, 0.92f); // 시원한 시안 톤

        // 3) 내용물 — 모니터마다 다른 레이아웃
        switch (index)
        {
            case 0: BuildBarsPanel(go.transform, s, "STATUS"); break;
            case 1: BuildIconsPanel(go.transform, s, "WEAPONS"); break;
            case 2: BuildProgressPanel(go.transform, s, "DATA"); break;
            case 3: BuildJoystickPanel(go.transform, s, "RADAR"); break;
        }
    }

    // --- 패널 빌더 ---
    static void BuildBarsPanel(Transform parent, AtlasSprites s, string title)
    {
        AddTitle(parent, title);
        var bars = new (Sprite, float)[] {
            (s.barBlueH, 0.85f),
            (s.barGreenH, 0.6f),
            (s.barRedH, 0.35f),
            (s.barPurpleH, 0.7f),
        };
        for (int i = 0; i < bars.Length; i++)
        {
            var bar = bars[i];
            float y = -120f + i * -60f;
            // 배경
            var bg = NewImage($"BarBg_{i}", parent, s.barBgH, Image.Type.Sliced);
            Anchor(bg.rectTransform, 80, y, 460, 32);
            bg.color = new Color(0.1f, 0.12f, 0.18f, 0.6f);
            // 채움
            var fill = NewImage($"BarFill_{i}", parent, bar.Item1, Image.Type.Filled);
            Anchor(fill.rectTransform, 80, y, 460, 32);
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillAmount = bar.Item2;
        }
    }

    static void BuildIconsPanel(Transform parent, AtlasSprites s, string title)
    {
        AddTitle(parent, title);
        var icons = new (Sprite, string)[] {
            (s.iconRocket, "MSL"),
            (s.iconBullets, "AMM"),
            (s.iconPlus, "MED"),
        };
        for (int i = 0; i < icons.Length; i++)
        {
            var ic = icons[i];
            if (ic.Item1 == null) continue;
            float x = -180f + i * 180f;
            var img = NewImage($"Icon_{i}", parent, ic.Item1, Image.Type.Simple);
            Anchor(img.rectTransform, x, -100f, 110, 110);
            img.color = new Color(0.6f, 0.95f, 1f, 1f);
            var lbl = NewText($"Lbl_{i}", parent, ic.Item2, 28);
            Anchor(lbl.rectTransform, x, -200f, 130, 40);
        }
    }

    static void BuildProgressPanel(Transform parent, AtlasSprites s, string title)
    {
        AddTitle(parent, title);
        var labels = new[] { "CPU", "MEM", "GPU", "NET" };
        var values = new[] { 0.72f, 0.45f, 0.91f, 0.33f };
        for (int i = 0; i < labels.Length; i++)
        {
            float y = -100f + i * -56f;
            var lbl = NewText($"Lbl_{i}", parent, labels[i], 26);
            Anchor(lbl.rectTransform, -240f, y, 90, 36);
            // 배경
            var bg = NewImage($"PBg_{i}", parent, s.progressBg, Image.Type.Sliced);
            Anchor(bg.rectTransform, 60f, y, 380f, 22f);
            bg.color = new Color(0.15f, 0.18f, 0.25f, 0.7f);
            // 채움
            var fill = NewImage($"PFill_{i}", parent, s.progress, Image.Type.Filled);
            Anchor(fill.rectTransform, 60f, y, 380f, 22f);
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillAmount = values[i];
            fill.color = new Color(0.4f, 1f, 0.85f, 1f);
        }
    }

    static void BuildJoystickPanel(Transform parent, AtlasSprites s, string title)
    {
        AddTitle(parent, title);
        if (s.joystickBack != null)
        {
            var bg = NewImage("Radar_Bg", parent, s.joystickBack, Image.Type.Simple);
            Anchor(bg.rectTransform, 0, -110, 280, 280);
            bg.color = new Color(0.5f, 0.95f, 1f, 0.9f);
        }
        if (s.joystick != null)
        {
            var dot = NewImage("Radar_Dot", parent, s.joystick, Image.Type.Simple);
            Anchor(dot.rectTransform, 40, -90, 60, 60);
            dot.color = new Color(1f, 0.4f, 0.4f, 1f);
        }
    }

    static void AddTitle(Transform parent, string text)
    {
        var t = NewText("Title", parent, text, 36);
        Anchor(t.rectTransform, 0, 200, 700, 60);
        t.fontStyle = FontStyle.Bold;
    }

    static Image NewImage(string name, Transform parent, Sprite sp, Image.Type type)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.sprite = sp;
        img.type = type;
        img.raycastTarget = false;
        return img;
    }

    static Text NewText(string name, Transform parent, string text, int fontSize)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<Text>();
        t.text = text;
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = fontSize;
        t.alignment = TextAnchor.MiddleCenter;
        t.color = new Color(0.85f, 1f, 1f, 1f);
        t.raycastTarget = false;
        return t;
    }

    static void Anchor(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    sealed class AtlasSprites
    {
        public Sprite window, window1;
        public Sprite barBlueH, barGreenH, barRedH, barPurpleH, barBgH;
        public Sprite progress, progressBg;
        public Sprite joystick, joystickBack;
        public Sprite iconRocket, iconBullets, iconPlus, iconPause;
    }
}
