using UnityEngine;

// 3인칭 카메라 follow + 마우스 룩.
//   Lock 상태에서 마우스로 yaw/pitch 회전 → LccPlayerController가 카메라 forward 기준으로 이동.
//   ESC: lock 해제 (Editor에서 빠져나오기 편함)
//   좌클릭: lock 다시
[AddComponentMenu("Virnect/LCC Camera Follow")]
public sealed class LccCameraFollow : MonoBehaviour
{
    public Transform target;
    public Vector3   offsetLocal      = new Vector3(0f, 1f, -5f);   // target+up 1.5 기준 카메라 위치
    public float     yawSensitivity   = 220f;
    public float     pitchSensitivity = 160f;
    public float     minPitch         = -30f;
    public float     maxPitch         = 60f;
    public float     headHeight       = 1.5f;   // target.position 위로 카메라 lookAt 기준점

    float _yaw;
    float _pitch = 10f;

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void LateUpdate()
    {
        if (target == null) return;

        if (Cursor.lockState == CursorLockMode.Locked)
        {
            _yaw   += Input.GetAxis("Mouse X") * yawSensitivity   * Time.deltaTime;
            _pitch -= Input.GetAxis("Mouse Y") * pitchSensitivity * Time.deltaTime;
            _pitch = Mathf.Clamp(_pitch, minPitch, maxPitch);
        }

        if (Input.GetKeyDown(KeyCode.Escape)) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        if (Input.GetMouseButtonDown(0) && Cursor.lockState == CursorLockMode.None)
        { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }

        var rot = Quaternion.Euler(_pitch, _yaw, 0f);
        var anchor = target.position + Vector3.up * headHeight;
        transform.position = anchor + rot * offsetLocal;
        transform.LookAt(anchor);
    }
}
