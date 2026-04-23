using System;
using Unity.Mathematics;
using UnityEngine;

namespace Virnect.Lcc
{
    // v2.2 — 실제 Gaussian-style 스플랫 빌보드 렌더러.
    // 포인트당 4 버텍스(quad) 를 가진 Mesh 를 구성하고, 뷰 공간에서 카메라 정면으로
    // scale 반지름만큼 확장. Opacity 기반 알파 블렌딩 + 중심 가중 falloff.
    //
    // 메쉬 구조 (퍼-버텍스):
    //   POSITION  : splat world position (4 vert 공유)
    //   COLOR     : RGBA8
    //   TEXCOORD0 : 쿼드 코너 uv (-1..1)
    //   TEXCOORD1 : (scale_avg, opacity, 0, 0)  ← 스플랫당 4 vert 동일값
    //
    // LOD 4 (320K) ≈ 1.28M verts ≈ 50 MB 메쉬 — OK.
    // LOD 0 은 ~20M verts → 메모리 제한. 기본 LOD 를 4 로 낮춤.
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [AddComponentMenu("Virnect/LCC Splat Renderer")]
    public sealed class LccSplatRenderer : MonoBehaviour
    {
        [Header("Source")]
        public LccScene scene;

        [Header("LOD (splat 빌보드는 LOD 4 권장)")]
        [Range(0, 4)] public int lodLevel = 4;

        [Header("Rendering")]
        [Range(0.2f, 5.0f)] public float scaleMultiplier = 1.5f;
        [Range(0.0f, 1.0f)] public float opacityBoost = 0.0f;
        public Color tint = Color.white;
        public Shader shader;

        Mesh _mesh;
        Material _mat;
        LccScene _loadedScene;
        int _loadedLod = -1;

        void OnEnable()  { _TryLoad(); }
        void OnDisable() { _Release(); }
        void OnValidate()
        {
            if (_loadedScene != scene || _loadedLod != lodLevel)
            {
                _Release();
                if (isActiveAndEnabled) _TryLoad();
            }
            if (_mat != null)
            {
                _mat.SetColor("_Tint", tint);
                _mat.SetFloat("_ScaleMul", scaleMultiplier);
                _mat.SetFloat("_OpacityBoost", opacityBoost);
            }
        }

        void _TryLoad()
        {
            if (scene == null || scene.manifest == null) return;
            try
            {
                var pts = LccSplatDecoder.DecodeLod(scene, lodLevel);
                int n = pts.Length;
                int vCount = n * 4;
                int iCount = n * 6;

                var verts  = new Vector3[vCount];
                var cols   = new Color32[vCount];
                var uv0    = new Vector2[vCount];  // corner
                var uv1    = new Vector2[vCount];  // (scale_avg, opacity)
                var tris   = new int[iCount];

                // 쿼드 코너 오프셋 (-1..1)
                var co = new Vector2[] {
                    new Vector2(-1, -1), new Vector2( 1, -1),
                    new Vector2( 1,  1), new Vector2(-1,  1),
                };

                for (int i = 0; i < n; i++)
                {
                    var p  = pts[i].position;
                    var c  = pts[i].color;
                    var s  = pts[i].scale;
                    float sAvg = (s.x + s.y + s.z) / 3f;
                    float op = pts[i].opacity;

                    int v = i * 4;
                    for (int k = 0; k < 4; k++)
                    {
                        verts[v + k] = new Vector3(p.x, p.y, p.z);
                        cols[v + k]  = c;
                        uv0[v + k]   = co[k];
                        uv1[v + k]   = new Vector2(sAvg, op);
                    }
                    int t = i * 6;
                    tris[t + 0] = v + 0;
                    tris[t + 1] = v + 2;
                    tris[t + 2] = v + 1;
                    tris[t + 3] = v + 0;
                    tris[t + 4] = v + 3;
                    tris[t + 5] = v + 2;
                }

                _mesh = new Mesh
                {
                    name = $"LccSplats_LOD{lodLevel}",
                    hideFlags = HideFlags.HideAndDontSave,
                    indexFormat = (vCount > 65535)
                        ? UnityEngine.Rendering.IndexFormat.UInt32
                        : UnityEngine.Rendering.IndexFormat.UInt16,
                };
                _mesh.SetVertices(verts);
                _mesh.SetColors(cols);
                _mesh.SetUVs(0, uv0);
                _mesh.SetUVs(1, uv1);
                _mesh.SetTriangles(tris, 0);
                _mesh.bounds = GetWorldBounds();

                GetComponent<MeshFilter>().sharedMesh = _mesh;

                if (_mat == null)
                {
                    var sh = shader;
                    if (sh == null) sh = Shader.Find("Virnect/LccSplat");
                    if (sh == null) { Debug.LogError("[LccSplatRenderer] Shader 'Virnect/LccSplat' not found."); return; }
                    _mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                }
                _mat.SetColor("_Tint", tint);
                _mat.SetFloat("_ScaleMul", scaleMultiplier);
                _mat.SetFloat("_OpacityBoost", opacityBoost);
                GetComponent<MeshRenderer>().sharedMaterial = _mat;

                _loadedScene = scene; _loadedLod = lodLevel;
                Debug.Log($"[LccSplatRenderer] {n:N0} splats ({vCount:N0} verts) · bounds={_mesh.bounds}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[LccSplatRenderer] {e.Message}\n{e.StackTrace}");
            }
        }

        void _Release()
        {
            if (_mesh != null) { if (Application.isPlaying) Destroy(_mesh); else DestroyImmediate(_mesh); _mesh = null; }
            if (_mat  != null) { if (Application.isPlaying) Destroy(_mat);  else DestroyImmediate(_mat);  _mat  = null; }
            var mf = GetComponent<MeshFilter>();   if (mf != null) mf.sharedMesh = null;
            var mr = GetComponent<MeshRenderer>(); if (mr != null) mr.sharedMaterial = null;
            _loadedScene = null; _loadedLod = -1;
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
