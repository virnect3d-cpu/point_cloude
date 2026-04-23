using Unity.Mathematics;
using UnityEngine;

namespace Virnect.Lcc
{
    // Graphics.DrawProceduralNow 기반 점 클라우드 렌더러.
    // 스플랫 대신 단순 포인트로 렌더 (v2 범위 — 기존 기능 재사용).
    // 진짜 Gaussian Splatting 렌더는 v3.
    [AddComponentMenu("Virnect/LCC Point Cloud Renderer")]
    public sealed class LccPointCloudRenderer : MonoBehaviour
    {
        [Header("Source")]
        public LccScene scene;

        [Header("Rendering")]
        [Range(0, 4)] public int lodLevel = 2;
        [Range(0.001f, 0.2f)] public float pointSize = 0.02f;
        public Color tint = Color.white;

        GraphicsBuffer _positions;
        GraphicsBuffer _colors;
        int _pointCount;
        Material _mat;

        void OnEnable()
        {
            // TODO: scene 로드 → LccSplatDecoder 로 lodLevel 추출 → GraphicsBuffer 업로드
            // TODO: URP/BiRP 양쪽 다 지원되는 point shader 확보
        }

        void OnDisable()
        {
            _positions?.Release();
            _colors?.Release();
        }

        void OnRenderObject()
        {
            if (_mat == null || _pointCount == 0) return;
            _mat.SetFloat("_PointSize", pointSize);
            _mat.SetColor("_Tint", tint);
            _mat.SetPass(0);
            Graphics.DrawProceduralNow(MeshTopology.Points, _pointCount, 1);
        }
    }
}
