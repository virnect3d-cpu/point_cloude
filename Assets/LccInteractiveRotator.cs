using UnityEngine;

// Play 모드에서 마우스 좌클릭+드래그로 GameObject 회전. (선택 옵션 — 디폴트 OFF)
//
// ⚠ 중요: 이 컴포넌트는 default disabled 상태로 추가됨. 회전이 필요할 때만 사용자가
//   Inspector 에서 enabled 체크 + requireSelection 체크 후 사용.
//   디폴트로 켜면 Scene5 5개 Splat 동시 회전 + V-Bot 카메라 마우스와 충돌 → 정합 깨짐.
//
// 키 매핑:
//   좌클릭 + 드래그       → Y축 (좌우) + X축 (상하) 회전
//   우클릭 + 드래그       → Z축 회전 (롤)
//   Shift + 드래그        → 정밀 모드 (속도 1/4)
//   R 키                  → 회전 리셋 (초기 rotation)
[AddComponentMenu("Virnect/LCC Interactive Rotator")]
public sealed class LccInteractiveRotator : MonoBehaviour
{
    [Tooltip("회전 속도 (도/초)")]
    public float speed = 250f;

    [Tooltip("선택된 GameObject 만 회전 (default true — 안전. false 면 모든 LccInteractiveRotator 가 동시 회전 ⚠)")]
    public bool requireSelection = true;   // 안전 디폴트 (이전 false 가 splat 동시 회전 버그 원인)

    [Tooltip("Editor 모드 / Edit (not Playing) 에서만 동작 (Play 시 V-Bot 카메라 마우스와 충돌 방지)")]
    public bool editorOnlyMode = true;

    Quaternion _initialRot;

    void OnEnable()
    {
        // 첫 활성화 때 baseline 캡처 (Play 시 transform 리셋 방지)
        _initialRot = transform.rotation;
    }

    void Update()
    {
        if (editorOnlyMode && Application.isPlaying) return;
#if UNITY_EDITOR
        if (requireSelection && UnityEditor.Selection.activeGameObject != gameObject) return;
#else
        if (requireSelection) return;   // 빌드 환경에선 Selection 없음 → requireSelection=true 면 회전 차단
#endif
        float mul = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? 0.25f : 1f;

        if (Input.GetMouseButton(0))
        {
            float dx = Input.GetAxis("Mouse X") * speed * mul * Time.deltaTime;
            float dy = Input.GetAxis("Mouse Y") * speed * mul * Time.deltaTime;
            transform.Rotate(Vector3.up,    -dx, Space.World);
            transform.Rotate(Vector3.right, -dy, Space.World);
        }
        else if (Input.GetMouseButton(1))
        {
            float dx = Input.GetAxis("Mouse X") * speed * mul * Time.deltaTime;
            transform.Rotate(Vector3.forward, -dx, Space.World);
        }

        if (Input.GetKeyDown(KeyCode.R)) transform.rotation = _initialRot;
    }
}
