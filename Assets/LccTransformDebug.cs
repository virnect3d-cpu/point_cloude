using UnityEngine;

// transform.localRotation / position / scale 가 변경되는 매 시점을 콘솔에 stack trace 로 출력.
//
// 사용법:
//   1. 의심되는 GameObject (LccGroup, Splat_*, _ArasP, __LccCollider) 에 Component 메뉴 → Virnect → LCC Transform Debug
//   2. ▶ Play
//   3. Console 에 LogWarning 으로 변경 출력 (Console 우측 ⓘ 버튼 클릭하면 stack trace → 호출자 식별)
//
// requireSelection 같은 옵션 없음 — 정확히 부착한 GameObject 만 추적.
[AddComponentMenu("Virnect/LCC Transform Debug")]
[DefaultExecutionOrder(-10000)]   // 가능한 한 먼저 — 다른 컴포넌트가 변경 전에 baseline 캡처
public sealed class LccTransformDebug : MonoBehaviour
{
    [Tooltip("local rotation 변경 추적")]
    public bool trackRotation = true;
    [Tooltip("local position 변경 추적")]
    public bool trackPosition = false;
    [Tooltip("local scale 변경 추적")]
    public bool trackScale = false;
    [Tooltip("threshold 이하 변화는 무시 (jitter)")]
    public float epsilon = 0.0001f;

    Quaternion _lastRot;
    Vector3 _lastPos;
    Vector3 _lastScl;

    void Awake()
    {
        _lastRot = transform.localRotation;
        _lastPos = transform.localPosition;
        _lastScl = transform.localScale;
        Debug.Log($"[LccTransformDebug:{name}] Awake — baseline localRot=euler({_lastRot.eulerAngles}) pos={_lastPos} scl={_lastScl}", this);
    }

    void OnEnable()  { _Snap("OnEnable"); }
    void Start()     { _Snap("Start"); }

    void LateUpdate()
    {
        if (trackRotation && Quaternion.Angle(transform.localRotation, _lastRot) > epsilon)
        {
            var oldEuler = _lastRot.eulerAngles;
            var newEuler = transform.localRotation.eulerAngles;
            Debug.LogWarning(
                $"[LccTransformDebug:{name}] localRotation 변경:\n" +
                $"  euler({oldEuler.x:F2}, {oldEuler.y:F2}, {oldEuler.z:F2})  quat({_lastRot.x:F3},{_lastRot.y:F3},{_lastRot.z:F3},{_lastRot.w:F3})\n" +
                $"→ euler({newEuler.x:F2}, {newEuler.y:F2}, {newEuler.z:F2})  quat({transform.localRotation.x:F3},{transform.localRotation.y:F3},{transform.localRotation.z:F3},{transform.localRotation.w:F3})\n" +
                $"  Δ = {Quaternion.Angle(transform.localRotation, _lastRot):F3}°", this);
            _lastRot = transform.localRotation;
        }
        if (trackPosition && Vector3.Distance(transform.localPosition, _lastPos) > epsilon)
        {
            Debug.LogWarning($"[LccTransformDebug:{name}] localPosition 변경: {_lastPos} → {transform.localPosition}  Δ={Vector3.Distance(transform.localPosition, _lastPos):F4}m", this);
            _lastPos = transform.localPosition;
        }
        if (trackScale && Vector3.Distance(transform.localScale, _lastScl) > epsilon)
        {
            Debug.LogWarning($"[LccTransformDebug:{name}] localScale 변경: {_lastScl} → {transform.localScale}", this);
            _lastScl = transform.localScale;
        }
    }

    void _Snap(string label)
    {
        // 변화 감지 — 이미 Awake/OnEnable 사이에 다른 컴포넌트가 만진 게 있는지
        if (Quaternion.Angle(transform.localRotation, _lastRot) > epsilon)
        {
            Debug.LogWarning($"[LccTransformDebug:{name}] {label} 시점 rotation 이미 변경됨: euler({_lastRot.eulerAngles}) → euler({transform.localRotation.eulerAngles})", this);
            _lastRot = transform.localRotation;
        }
    }
}
