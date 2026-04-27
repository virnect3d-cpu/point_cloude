using UnityEngine;

// Play 모드에서 마우스 좌클릭+드래그로 GameObject 회전.
// LCC splat 본 후 직접 정렬할 때 사용. Scene5 자동 부착.
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

    [Tooltip("선택된 GameObject만 회전 (false 면 활성화된 모든 LccInteractiveRotator 가 동시 회전)")]
    public bool requireSelection = false;

    Quaternion _initialRot;

    void Start() { _initialRot = transform.rotation; }

    void Update()
    {
#if UNITY_EDITOR
        if (requireSelection && UnityEditor.Selection.activeGameObject != gameObject) return;
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
