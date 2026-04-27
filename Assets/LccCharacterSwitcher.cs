using UnityEngine;

// Scene2: LCC splat 돌아다니는 캐릭터 / Mesh 돌아다니는 캐릭터 런타임 전환.
//
// 사용:
//   Scene 에 LccCharacterSwitcher 오브젝트 하나 추가 → playerA / playerB /
//   camera (vThirdPersonCamera GameObject) 필드 지정. 플레이 모드 진입하면
//   우상단에 버튼 + 단축키로 전환/리셋.
//
// 단축키 (Input Handling = Both 또는 Legacy 필수):
//   [1]  LCC 캐릭터로 전환
//   [2]  Mesh 캐릭터로 전환
//   [3]  두 캐릭터 모두 초기 스폰 위치로 리셋
public sealed class LccCharacterSwitcher : MonoBehaviour
{
    [Header("Players")]
    public GameObject playerA;      // 예: Player_Splat_ShinWon_Facility_01
    public string     labelA = "LCC";
    public GameObject playerB;      // 예: Player_Mesh_ShinWon_Facility_01
    public string     labelB = "Mesh";

    [Header("Third Person Camera")]
    public GameObject thirdPersonCamera;  // Cam_Shared (vThirdPersonCamera 컴포넌트 내장)

    [Header("Spawn Points (auto-captured on Awake · 필요시 수동 오버라이드)")]
    public Vector3 spawnPosA;
    public Vector3 spawnEulerA;
    public Vector3 spawnPosB;
    public Vector3 spawnEulerB;
    public bool    useManualSpawn = false;    // true 면 Inspector 값 사용, false 면 Awake 캡처값 사용

    [Header("Environment (선택 — 토글 시 SetActive · 무거운 양측 환경 동시 렌더 방지)")]
    public GameObject[] envA;   // Splat 측 환경 묶음 (예: Splat_ShinWon_Facility_01, __LccCollider …)
    public GameObject[] envB;   // Mesh 측 환경 묶음 (예: Mesh_ShinWon_Facility_01, ColoredMesh_PhotoReal …)

    [Header("Camera Snap")]
    public Vector3 camOffset = new Vector3(0f, 2.5f, -3.5f);   // active 캐릭터 기준 카메라 즉시 스냅 오프셋

    bool _isA = true;

    void Awake()
    {
        // Rigidbody 가 FixedUpdate 로 흔들기 전에 가장 이른 시점에 스폰 기록
        if (!useManualSpawn)
        {
            if (playerA != null) { spawnPosA = playerA.transform.position; spawnEulerA = playerA.transform.eulerAngles; }
            if (playerB != null) { spawnPosB = playerB.transform.position; spawnEulerB = playerB.transform.eulerAngles; }
        }

        // envA/envB 둘 다 비어 있으면 씬 root 스캔으로 자동 매핑 (이름 prefix 기준)
        if ((envA == null || envA.Length == 0) && (envB == null || envB.Length == 0))
            _AutoMapEnv();
    }

    void _AutoMapEnv()
    {
        var scene = gameObject.scene;
        if (!scene.IsValid()) return;

        var listA = new System.Collections.Generic.List<GameObject>();
        var listB = new System.Collections.Generic.List<GameObject>();
        foreach (var go in scene.GetRootGameObjects())
        {
            if (go == null || go == gameObject || go == playerA || go == playerB || go == thirdPersonCamera) continue;
            string n = go.name;
            // Splat 측: "Splat_" 또는 "__Lcc" prefix
            if (n.StartsWith("Splat_") || n.StartsWith("__Lcc"))
                listA.Add(go);
            // Mesh 측: "Mesh_" 또는 "ColoredMesh" prefix
            else if (n.StartsWith("Mesh_") || n.StartsWith("ColoredMesh"))
                listB.Add(go);
        }
        envA = listA.ToArray();
        envB = listB.ToArray();
        Debug.Log($"[Switcher] AutoMapEnv → envA({listA.Count})=[{string.Join(", ", listA.ConvertAll(g => g.name))}]  envB({listB.Count})=[{string.Join(", ", listB.ConvertAll(g => g.name))}]");
    }

    void Start()
    {
        _Apply();
    }

    void Update()
    {
        // 1 → LCC / 2 → Mesh / 3 → reset (가드 제거: 상태 어긋나도 항상 _Apply 보장)
        if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1))
        {
            _isA = true;  _Apply(); Debug.Log("[Switcher] → " + labelA);
        }
        else if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2))
        {
            _isA = false; _Apply(); Debug.Log("[Switcher] → " + labelB);
        }
        else if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3))
        {
            _ResetSpawns();
        }
    }

    void _ResetSpawns()
    {
        _Teleport(playerA, spawnPosA, Quaternion.Euler(spawnEulerA));
        _Teleport(playerB, spawnPosB, Quaternion.Euler(spawnEulerB));
        Debug.Log($"[Switcher] 🔄 초기 위치 리셋 · A={spawnPosA} · B={spawnPosB}");
    }

    static void _Teleport(GameObject go, Vector3 pos, Quaternion rot)
    {
        if (go == null) return;
        var rb = go.GetComponent<Rigidbody>();
        bool wasKinematic = rb != null && rb.isKinematic;
        // kinematic 아니면 잠시 세워서 속도 제로
        if (rb != null) { rb.isKinematic = true; rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
        go.transform.SetPositionAndRotation(pos, rot);
        if (rb != null && !wasKinematic) rb.isKinematic = false;
    }

    void _Apply()
    {
        var active = _isA ? playerA : playerB;
        var idle   = _isA ? playerB : playerA;

        // 1) 캐릭터 컨트롤러 토글
        _ToggleController(active, true);
        _ToggleController(idle,   false);

        // 2) idle 캐릭터 자체도 비활성 — 멀리 떨어진 측 RB/렌더 부하 제거
        if (active != null && !active.activeSelf) active.SetActive(true);
        if (idle   != null &&  idle.activeSelf)   idle.SetActive(false);

        // 3) 환경 토글 (선택 — Inspector envA/envB 채워진 경우만)
        _SetEnv(envA,  _isA);
        _SetEnv(envB, !_isA);

        // 4) 카메라 target 재바인딩 + 즉시 스냅 (117m smooth lerp 방지)
        if (thirdPersonCamera != null && active != null)
        {
            // 4a) 카메라 위치를 active 캐릭터 옆으로 즉시 이동
            var camTr = thirdPersonCamera.transform;
            var head  = active.transform.position + Vector3.up * 1.5f;
            camTr.position = active.transform.position + active.transform.TransformDirection(camOffset);
            camTr.LookAt(head);

            // 4b) vThirdPersonCamera.target 갱신 + 가능한 경우 Init 호출로 lerp 캐시 리셋
            foreach (var mb in thirdPersonCamera.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null || mb.GetType().Name != "vThirdPersonCamera") continue;

                var field = mb.GetType().GetField("target");
                if (field == null) { Debug.LogWarning("[Switcher] vThirdPersonCamera.target 필드 없음 (Invector 업데이트?)"); break; }
                field.SetValue(mb, active.transform);

                // Init() 있으면 호출 — 내부 currentTarget/lerp 캐시 리셋 (Invector LITE 표준)
                var init = mb.GetType().GetMethod("Init",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (init != null && init.GetParameters().Length == 0) init.Invoke(mb, null);
                break;
            }
        }
    }

    static void _SetEnv(GameObject[] arr, bool on)
    {
        if (arr == null) return;
        for (int i = 0; i < arr.Length; i++)
            if (arr[i] != null && arr[i].activeSelf != on) arr[i].SetActive(on);
    }

    static void _ToggleController(GameObject go, bool on)
    {
        if (go == null) return;
        foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null) continue;
            string n = mb.GetType().Name;
            if (n.StartsWith("v") && (n.Contains("Controller") || n.Contains("Input")))
                mb.enabled = on;
        }
        var rb = go.GetComponent<Rigidbody>();
        if (rb != null) rb.isKinematic = !on;
    }

    void OnGUI()
    {
        if (playerA == null || playerB == null) return;

        var btn = new GUIStyle(GUI.skin.button)
        {
            fontSize = 16,
            fontStyle = FontStyle.Bold,
            padding = new RectOffset(12, 12, 8, 8),
            alignment = TextAnchor.MiddleCenter,
        };
        var lbl = new GUIStyle(GUI.skin.label) { fontSize = 11, normal = { textColor = Color.white } };

        float x = Screen.width - 260;
        float y = 20;
        GUI.Box(new Rect(x - 10, y - 10, 250, 160), "");

        // 1: LCC
        GUI.enabled = !_isA;
        if (GUI.Button(new Rect(x, y, 230, 36), $"[1] ▶ {labelA}", btn))
        { _isA = true; _Apply(); }
        y += 40;

        // 2: Mesh
        GUI.enabled = _isA;
        if (GUI.Button(new Rect(x, y, 230, 36), $"[2] ▶ {labelB}", btn))
        { _isA = false; _Apply(); }
        y += 40;

        // 3: Reset
        GUI.enabled = true;
        if (GUI.Button(new Rect(x, y, 230, 36), $"[3] 🔄 초기화", btn))
        { _ResetSpawns(); }
        y += 42;

        GUI.Label(new Rect(x, y, 230, 18),
            _isA ? $"현재: {labelA}" : $"현재: {labelB}", lbl);
    }
}
