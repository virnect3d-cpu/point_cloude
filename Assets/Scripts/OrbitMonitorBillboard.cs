using UnityEngine;

// 캐릭터 주변에 떠있는 미니 모니터용 — 메인 카메라 향해 빌보드 + 살짝 부유 + 천천히 궤도 이동.
[ExecuteAlways]
public sealed class OrbitMonitorBillboard : MonoBehaviour
{
    [Tooltip("궤도 회전 속도 (deg/sec). 0이면 고정.")]
    public float orbitSpeed = 8f;

    [Tooltip("부유 진폭 (m).")]
    public float floatAmplitude = 0.05f;

    [Tooltip("부유 주기 (sec).")]
    public float floatPeriod = 2.4f;

    [Tooltip("위상 오프셋 (각 모니터마다 다르게 — Inspector에서 0~6.28).")]
    public float phase = 0f;

    Vector3 _baseLocalPos;
    bool _captured;

    void OnEnable()
    {
        _baseLocalPos = transform.localPosition;
        _captured = true;
    }

    void LateUpdate()
    {
        if (!_captured) { _baseLocalPos = transform.localPosition; _captured = true; }

        // 1) 부모 기준 궤도 회전 (Y축)
        if (orbitSpeed != 0f && transform.parent != null)
        {
            float a = orbitSpeed * Time.deltaTime;
            Quaternion rot = Quaternion.AngleAxis(a, Vector3.up);
            Vector3 local = transform.localPosition;
            local = rot * local;
            _baseLocalPos = rot * _baseLocalPos;
            transform.localPosition = local;
        }

        // 2) 부유 (local Y)
        if (floatAmplitude > 0f)
        {
            float t = (Application.isPlaying ? Time.time : (float)UnityEditor.EditorApplication.timeSinceStartup);
            float y = Mathf.Sin((t / Mathf.Max(0.01f, floatPeriod)) * Mathf.PI * 2f + phase) * floatAmplitude;
            var p = transform.localPosition;
            p.y = _baseLocalPos.y + y;
            transform.localPosition = p;
        }

        // 3) 카메라 향해 빌보드
        Camera cam = Camera.main;
#if UNITY_EDITOR
        if (cam == null)
        {
            var sv = UnityEditor.SceneView.lastActiveSceneView;
            if (sv != null) cam = sv.camera;
        }
#endif
        if (cam != null)
        {
            Vector3 dir = transform.position - cam.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
        }
    }
}
