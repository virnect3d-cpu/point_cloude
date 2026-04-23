using System;
using Unity.Mathematics;
using UnityEngine;

namespace Virnect.Lcc
{
    // 포인트 클라우드 렌더러 (URP/Built-in 모두 지원).
    //
    // 구현 방식: Mesh(topology=Points) + MeshRenderer.
    //   - 스플랫 하나당 버텍스 1개
    //   - 하드웨어 포인트 래스터라이저로 1픽셀 점 렌더
    //   - MeshRenderer 이므로 URP 의 일반 오파크 패스에 자연스럽게 편입
    //
    // 큰 점(빌보드 쿼드) 은 v2.1 에서 확장 예정 (현재는 포인트 렌더로 충분).
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [AddComponentMenu("Virnect/LCC Point Cloud Renderer")]
    public sealed class LccPointCloudRenderer : MonoBehaviour
    {
        [Header("Source")]
        public LccScene scene;

        [Header("LOD")]
        [Range(0, 4)] public int lodLevel = 2;

        [Header("Rendering")]
        public Color tint = Color.white;
        [Tooltip("Override shader. Null → auto-pick URP or Built-in unlit color.")]
        public Shader shader;

        Mesh _mesh;
        Material _mat;
        LccScene _loadedScene;
        int _loadedLod = -1;

        void OnEnable() { _TryLoad(); }
        void OnDisable() { _Release(); }
        void OnValidate()
        {
            if (_loadedScene != scene || _loadedLod != lodLevel)
            {
                _Release();
                if (isActiveAndEnabled) _TryLoad();
            }
            if (_mat != null) _mat.color = tint;
        }

        void _TryLoad()
        {
            if (scene == null || scene.manifest == null) return;
            try
            {
                var pts = LccSplatDecoder.DecodeLod(scene, lodLevel);
                int n = pts.Length;

                var vertices = new Vector3[n];
                var colors = new Color32[n];
                var indices = new int[n];
                for (int i = 0; i < n; i++)
                {
                    var p = pts[i].position;
                    vertices[i] = new Vector3(p.x, p.y, p.z);
                    colors[i] = pts[i].color;
                    indices[i] = i;
                }

                _mesh = new Mesh
                {
                    name = $"LccCloud_LOD{lodLevel}",
                    hideFlags = HideFlags.HideAndDontSave,
                    indexFormat = (n > 65535)
                        ? UnityEngine.Rendering.IndexFormat.UInt32
                        : UnityEngine.Rendering.IndexFormat.UInt16,
                };
                _mesh.SetVertices(vertices);
                _mesh.SetColors(colors);
                _mesh.SetIndices(indices, MeshTopology.Points, 0);
                _mesh.bounds = GetWorldBounds();

                GetComponent<MeshFilter>().sharedMesh = _mesh;

                var mr = GetComponent<MeshRenderer>();
                if (_mat == null)
                {
                    var sh = shader;
                    if (sh == null) sh = Shader.Find("Virnect/LccPointCloud");
                    if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
                    if (sh == null) sh = Shader.Find("Unlit/Color");
                    _mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave, color = tint };
                    _mat.enableInstancing = true;
                }
                mr.sharedMaterial = _mat;

                _loadedScene = scene;
                _loadedLod = lodLevel;

                Debug.Log($"[LccPointCloudRenderer] loaded {n:N0} pts (LOD {lodLevel}), bounds={_mesh.bounds}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[LccPointCloudRenderer] Load failed: {e.Message}\n{e.StackTrace}");
            }
        }

        void _Release()
        {
            if (_mesh != null)
            {
                if (Application.isPlaying) Destroy(_mesh); else DestroyImmediate(_mesh);
                _mesh = null;
            }
            if (_mat != null)
            {
                if (Application.isPlaying) Destroy(_mat); else DestroyImmediate(_mat);
                _mat = null;
            }
            _loadedScene = null;
            _loadedLod = -1;
            var mf = GetComponent<MeshFilter>();   if (mf != null) mf.sharedMesh = null;
            var mr = GetComponent<MeshRenderer>(); if (mr != null) mr.sharedMaterial = null;
        }

        public Bounds GetWorldBounds()
        {
            if (scene?.manifest?.boundingBox == null) return new Bounds(Vector3.zero, Vector3.one);
            var bb = scene.manifest.boundingBox;
            var min = new Vector3(bb.min[0], bb.min[1], bb.min[2]);
            var max = new Vector3(bb.max[0], bb.max[1], bb.max[2]);
            return new Bounds((min + max) * 0.5f, max - min);
        }
    }
}
