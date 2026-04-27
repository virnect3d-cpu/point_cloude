using UnityEngine;

namespace Virnect.Lcc
{
    /// <summary>
    /// 플레이 시 LCC 관련 GameObject 의 회전이 갑자기 바뀌는 원인을 추적하는 디버그 컴포넌트.
    ///
    /// 사용법:
    ///   1. 회전이 바뀌는 GameObject 에 붙이기 (LccGroup, Splat_*, _ArasP, __LccCollider 등 의심되는 곳)
    ///   2. 플레이 누르고 콘솔 보면 매 프레임 회전 변화가 발생할 때마다 다음을 출력:
    ///      [LccTransformDebug] Frame N · localRotation 변경: (이전) → (현재)  Δ=...°
    ///   3. 변경 직후 호출 스택을 함께 출력 → 어떤 컴포넌트의 어떤 메서드가 만진 건지 식별
    ///
    /// 일반적인 범인 후보:
    ///   - [ExecuteAlways] / Awake / Start / OnEnable 에서 transform 을 reset 하는 컴포넌트
    ///   - GaussianSplatRenderer (Aras-P) 또는 LccDropAutoImporter 류가 부모 회전 보정을 재적용
    ///   - 부모 GameObject 의 Update 가 자식 회전을 강제 동기화
    ///   - Animator / Constraint / Rigidbody (회전 잠금/적용)
    ///
    /// 디버그 끝나면 컴포넌트 제거하면 됩니다 (퍼포먼스 영향 없음, 단지 노이즈).
    /// </summary>
    [DefaultExecutionOrder(-10000)]   // 다른 컴포넌트보다 먼저 Awake → 초기 baseline 기록
    [AddComponentMenu("Virnect/LCC Transform Debug")]
    public sealed class LccTransformDebug : MonoBehaviour
    {
        [Tooltip("local 또는 world 회전 둘 다 감시. 보통 local 만으로 충분.")]
        public bool watchWorldRotation = true;

        [Tooltip("얼마나 작은 변화부터 로그할지 (도). 작은 노이즈 무시.")]
        [Range(0.001f, 5.0f)] public float minDeltaDegrees = 0.05f;

        [Tooltip("변경 시 stack trace 도 함께 출력 (어떤 컴포넌트가 만졌는지).")]
        public bool logStackTrace = true;

        [Tooltip("위치/스케일 변화도 같이 감시.")]
        public bool watchPositionAndScale = false;

        Quaternion _lastLocalRot;
        Quaternion _lastWorldRot;
        Vector3    _lastLocalPos;
        Vector3    _lastLocalScale;
        int        _frame;

        void Awake()
        {
            _Capture();
            Debug.Log($"[LccTransformDebug:{name}] Awake — baseline localRot={_FmtEuler(_lastLocalRot)} worldRot={_FmtEuler(_lastWorldRot)}");
        }

        void Start()
        {
            // Start 시점이 Awake 와 다르면 그 사이에 누군가 만진 것
            _CompareAndLog("Start");
        }

        void OnEnable()
        {
            _CompareAndLog("OnEnable");
        }

        void Update()
        {
            _frame++;
            _CompareAndLog($"Update#{_frame}");
        }

        void LateUpdate()
        {
            _CompareAndLog($"LateUpdate#{_frame}");
        }

        void _Capture()
        {
            _lastLocalRot   = transform.localRotation;
            _lastWorldRot   = transform.rotation;
            _lastLocalPos   = transform.localPosition;
            _lastLocalScale = transform.localScale;
        }

        void _CompareAndLog(string phase)
        {
            var lr = transform.localRotation;
            var wr = transform.rotation;
            float dLocal = Quaternion.Angle(lr, _lastLocalRot);
            float dWorld = Quaternion.Angle(wr, _lastWorldRot);

            if (dLocal >= minDeltaDegrees)
            {
                _LogChange(phase, "localRotation",
                    _FmtEuler(_lastLocalRot), _FmtEuler(lr), dLocal);
            }
            if (watchWorldRotation && dWorld >= minDeltaDegrees && dWorld - dLocal > minDeltaDegrees)
            {
                _LogChange(phase, "worldRotation (부모 영향 포함)",
                    _FmtEuler(_lastWorldRot), _FmtEuler(wr), dWorld);
            }

            if (watchPositionAndScale)
            {
                var lp = transform.localPosition;
                var ls = transform.localScale;
                if ((lp - _lastLocalPos).sqrMagnitude > 1e-6f)
                    _LogChange(phase, "localPosition", _Fmt(_lastLocalPos), _Fmt(lp), (lp - _lastLocalPos).magnitude);
                if ((ls - _lastLocalScale).sqrMagnitude > 1e-6f)
                    _LogChange(phase, "localScale",    _Fmt(_lastLocalScale), _Fmt(ls), (ls - _lastLocalScale).magnitude);
            }

            _Capture();
        }

        void _LogChange(string phase, string field, string before, string after, float delta)
        {
            string msg = $"[LccTransformDebug:{name}] {phase} · {field} 변경:\n  {before}\n→ {after}\n  Δ = {delta:F3}";
            if (logStackTrace)
            {
                // Application.GetStackTraceLogType + LogWarning 으로 stack trace 강제 노출
                Debug.LogWarning(msg, this);
            }
            else
            {
                Debug.Log(msg, this);
            }
        }

        static string _FmtEuler(Quaternion q)
        {
            var e = q.eulerAngles;
            return $"euler({e.x:F2}, {e.y:F2}, {e.z:F2})  quat({q.x:F3},{q.y:F3},{q.z:F3},{q.w:F3})";
        }

        static string _Fmt(Vector3 v) => $"({v.x:F3}, {v.y:F3}, {v.z:F3})";
    }
}
