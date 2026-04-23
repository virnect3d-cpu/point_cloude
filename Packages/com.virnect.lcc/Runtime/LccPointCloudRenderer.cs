using System;
using Unity.Mathematics;
using UnityEngine;

namespace Virnect.Lcc
{
    // Graphics.DrawProceduralNow 기반 포인트 클라우드 렌더러.
    // 스플랫 대신 카메라 정면 빌보드 쿼드로 렌더 (URP + Built-in 공통 지원).
    //
    // 사용법:
    //   1. 빈 GameObject 에 이 컴포넌트 추가
    //   2. Scene 필드에 LccScene 에셋 드래그
    //   3. Play (또는 Edit 모드 렌더도 됨)
    [ExecuteAlways]
    [AddComponentMenu("Virnect/LCC Point Cloud Renderer")]
    public sealed class LccPointCloudRenderer : MonoBehaviour
    {
        [Header("Source")]
        public LccScene scene;

        [Header("LOD")]
        [Range(0, 4)] public int lodLevel = 2;

        [Header("Rendering")]
        [Range(0.001f, 0.5f)] public float pointSize = 0.03f;
        public Color tint = Color.white;
        public Shader shader;

        GraphicsBuffer _positions;
        GraphicsBuffer _colors;
        int _count;
        Material _mat;
        LccScene _loadedScene;
        int _loadedLod = -1;

        void OnEnable()
        {
            _TryLoad();
        }

        void OnDisable()
        {
            _Release();
        }

        void OnValidate()
        {
            if (_loadedScene != scene || _loadedLod != lodLevel)
            {
                _Release();
                _TryLoad();
            }
        }

        void _TryLoad()
        {
            if (scene == null || scene.manifest == null) return;
            try
            {
                var pts = LccSplatDecoder.DecodeLod(scene, lodLevel);
                _count = pts.Length;

                var pos = new Vector3[_count];
                var col = new uint[_count];
                for (int i = 0; i < _count; i++)
                {
                    pos[i] = new Vector3(pts[i].position.x, pts[i].position.y, pts[i].position.z);
                    var c = pts[i].color;
                    col[i] = (uint)c.r | ((uint)c.g << 8) | ((uint)c.b << 16) | ((uint)c.a << 24);
                }

                _positions = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count, sizeof(float) * 3);
                _positions.SetData(pos);
                _colors = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count, sizeof(uint));
                _colors.SetData(col);

                if (shader == null) shader = Shader.Find("Virnect/LccPointCloud");
                if (shader == null)
                {
                    Debug.LogWarning("[LccPointCloudRenderer] Shader 'Virnect/LccPointCloud' not found.");
                    return;
                }
                _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                _mat.SetBuffer("_Positions", _positions);
                _mat.SetBuffer("_Colors",    _colors);

                _loadedScene = scene;
                _loadedLod = lodLevel;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[LccPointCloudRenderer] Load failed: {e.Message}");
            }
        }

        void _Release()
        {
            _positions?.Release(); _positions = null;
            _colors?.Release();    _colors = null;
            if (_mat != null)
            {
                if (Application.isPlaying) Destroy(_mat); else DestroyImmediate(_mat);
                _mat = null;
            }
            _count = 0;
            _loadedScene = null;
            _loadedLod = -1;
        }

        void OnRenderObject()
        {
            if (_mat == null || _count == 0 || _positions == null) return;
            _mat.SetFloat("_PointSize", pointSize);
            _mat.SetColor("_Tint", tint);
            _mat.SetPass(0);
            Graphics.DrawProceduralNow(MeshTopology.Triangles, 6, _count);
        }

        // 월드 bbox 알기 — 카메라 프레이밍 유틸용
        public Bounds GetWorldBounds()
        {
            if (scene?.manifest?.boundingBox == null) return new Bounds(Vector3.zero, Vector3.one);
            var bb = scene.manifest.boundingBox;
            var min = new Vector3(bb.min[0], bb.min[1], bb.min[2]);
            var max = new Vector3(bb.max[0], bb.max[1], bb.max[2]);
            var b = new Bounds((min + max) * 0.5f, max - min);
            return b;
        }
    }
}
