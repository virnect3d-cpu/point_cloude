using UnityEngine;

namespace Virnect.Lcc
{
    /// 카메라 거리 기반 LOD 동적 교체기.
    /// 같은 GameObject 에 붙은 LccPointCloudRenderer 또는 LccSplatRenderer 의
    /// lodLevel 을 거리에 따라 바꿔 렌더를 가볍게 유지.
    ///
    /// 임계값은 scene bbox 의 대각 길이에 비례한 상대 배수로 계산:
    ///   distance / bbox_diag 비율이 작을수록 고해상도(LOD 0), 멀수록 저해상도(LOD 4).
    [ExecuteAlways]
    [AddComponentMenu("Virnect/LCC LOD Streamer")]
    public sealed class LccLodStreamer : MonoBehaviour
    {
        [Tooltip("동일 GameObject 에 붙은 LccSplatRenderer 또는 LccPointCloudRenderer. 비워두면 자동 탐색.")]
        public MonoBehaviour targetRenderer;

        [Tooltip("평가에 사용할 카메라. 비워두면 Camera.main.")]
        public Camera evalCamera;

        [Header("거리 × bbox 대각 비율 임계값 (작을수록 가까움 = 고해상도)")]
        [Range(0.0f, 1.0f)] public float lod0Threshold = 0.3f;
        [Range(0.0f, 2.0f)] public float lod1Threshold = 0.6f;
        [Range(0.0f, 3.0f)] public float lod2Threshold = 1.0f;
        [Range(0.0f, 5.0f)] public float lod3Threshold = 1.8f;

        [Header("Hysteresis (LOD 진동 방지)")]
        [Range(0.0f, 0.5f)] public float hysteresis = 0.08f;

        [Tooltip("매 프레임 대신 N 프레임마다 체크 (에디터에서는 0.2초 주기).")]
        [Range(1, 30)] public int checkIntervalFrames = 6;

        int   _lastAppliedLod = -1;
        int   _frameCounter = 0;
        float _nextCheckTime = 0f;

        void Update()
        {
            if (Application.isPlaying)
            {
                _frameCounter++;
                if (_frameCounter % checkIntervalFrames != 0) return;
            }
            else
            {
                if (Time.realtimeSinceStartup < _nextCheckTime) return;
                _nextCheckTime = Time.realtimeSinceStartup + 0.2f;
            }

            _Evaluate();
        }

        void _Evaluate()
        {
            var cam = evalCamera;
            if (cam == null) cam = Camera.main ?? UnityEngine.Object.FindFirstObjectByType<Camera>();
            if (cam == null) return;

            var r = targetRenderer;
            if (r == null)
            {
                r = GetComponent<LccSplatRenderer>() as MonoBehaviour
                  ?? GetComponent<LccPointCloudRenderer>() as MonoBehaviour;
                targetRenderer = r;
            }
            if (r == null) return;

            Bounds b;
            if (r is LccSplatRenderer sr) b = sr.GetWorldBounds();
            else if (r is LccPointCloudRenderer pr) b = pr.GetWorldBounds();
            else return;

            b.center += transform.position;
            float diag = b.size.magnitude;
            if (diag < 0.001f) return;

            var camPos = cam.transform.position;
            var ctr = b.center;
            float distance = Vector3.Distance(camPos, ctr);
            float ratio = distance / diag;

            int newLod = _RatioToLod(ratio, _lastAppliedLod);
            if (newLod != _lastAppliedLod)
                _ApplyLod(r, newLod);
        }

        int _RatioToLod(float ratio, int prev)
        {
            // thresholds (with hysteresis to prevent flip-flop near boundaries)
            float bias = prev >= 0 ? hysteresis : 0f;
            if (ratio < lod0Threshold - bias) return 0;
            if (ratio < lod1Threshold - bias) return 1;
            if (ratio < lod2Threshold - bias) return 2;
            if (ratio < lod3Threshold - bias) return 3;
            return 4;
        }

        void _ApplyLod(MonoBehaviour r, int lod)
        {
            _lastAppliedLod = lod;
            if (r is LccSplatRenderer sr)
            {
                if (sr.lodLevel == lod) return;
                sr.lodLevel = lod;
                sr.enabled = false; sr.enabled = true;  // 리로드 트리거
            }
            else if (r is LccPointCloudRenderer pr)
            {
                if (pr.lodLevel == lod) return;
                pr.lodLevel = lod;
                pr.enabled = false; pr.enabled = true;
            }
        }
    }
}
