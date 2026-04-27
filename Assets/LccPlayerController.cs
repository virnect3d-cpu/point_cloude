using UnityEngine;

// 단순 1인칭/3인칭 캐릭터. CharacterController 기반.
//   WASD            이동 (카메라 forward 기준)
//   Shift           달리기
//   Space           점프
//   ESC             마우스 lock 해제
//   좌클릭          마우스 다시 lock
[RequireComponent(typeof(CharacterController))]
[AddComponentMenu("Virnect/LCC Player Controller")]
public sealed class LccPlayerController : MonoBehaviour
{
    [Header("Movement")]
    public float walkSpeed  = 5f;
    public float runSpeed   = 10f;
    public float jumpHeight = 2f;
    public float gravity    = -19.62f;     // earth × 2 — splat 높은 곳에서 빠르게 착지

    [Header("Reference (auto-fill)")]
    public Transform cameraTr;

    CharacterController _cc;
    Vector3 _vel;

    void Awake()
    {
        _cc = GetComponent<CharacterController>();
        if (cameraTr == null && Camera.main != null) cameraTr = Camera.main.transform;
    }

    void Update()
    {
        Vector3 fwd, right;
        if (cameraTr != null)
        {
            fwd   = Vector3.ProjectOnPlane(cameraTr.forward, Vector3.up).normalized;
            right = Vector3.ProjectOnPlane(cameraTr.right,   Vector3.up).normalized;
        }
        else { fwd = transform.forward; right = transform.right; }

        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");
        bool  run = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        Vector3 dir = (fwd * v + right * h);
        if (dir.sqrMagnitude > 1f) dir.Normalize();
        _cc.Move(dir * (run ? runSpeed : walkSpeed) * Time.deltaTime);

        if (_cc.isGrounded)
        {
            if (_vel.y < 0f) _vel.y = -2f;
            if (Input.GetButtonDown("Jump")) _vel.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }
        _vel.y += gravity * Time.deltaTime;
        _cc.Move(_vel * Time.deltaTime);
    }
}
