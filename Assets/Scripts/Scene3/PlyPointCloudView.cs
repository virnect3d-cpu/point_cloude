using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace LixelScene3
{
    // Scene3 전용 범용 PLY 포인트 클라우드 뷰.
    //
    // Lixel Universal Converter 가 내보내는 두 가지 PLY 포맷을 모두 지원:
    //   (1) Potree 경유: double x/y/z + float scalar_Intensity (+ scalar_Classification)
    //   (2) LCC 경유  : float  x/y/z + uchar red/green/blue
    //
    // 포인트는 Mesh(topology=Points) 로 구성 — 하드웨어 래스터라이저로 1픽셀 점 렌더.
    // URP 기본 셰이더 사용.
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class PlyPointCloudView : MonoBehaviour
    {
        [Header("Source PLY (absolute path)")]
        [Tooltip("E.g. C:/Users/VIRNECT/Desktop/lcc/.../converted/xxx.ply")]
        public string absolutePath = "";

        [Header("Intensity coloring (used when PLY has no RGB)")]
        [Range(0f, 10f)] public float intensityGain = 1f;
        public Color intensityTint = Color.white;

        [Header("Transform")]
        [Tooltip("Recenter the cloud around origin so it's framed by the camera")]
        public bool recenterToOrigin = true;

        [Header("Point size (URP)")]
        [Tooltip("PointsDefault shader is single-pixel — use a custom material for larger points.")]
        public Material materialOverride;

        [Header("Status (read-only)")]
        public int loadedPointCount;
        public Vector3 loadedBoundsMin;
        public Vector3 loadedBoundsMax;

        Mesh _mesh;
        Material _mat;
        string _loadedPath;

        void OnEnable()
        {
            Rebuild();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update += _EditorTick;
#endif
        }
        void OnValidate()
        {
            if (isActiveAndEnabled) Rebuild();
        }
        void OnDisable()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= _EditorTick;
#endif
            Release();
        }

        // Detects late-assigned `absolutePath` (e.g. via MCP after component add)
        // and reloads without needing a manual inspector edit.
        void _EditorTick()
        {
            if (!isActiveAndEnabled) return;
            if (_loadedPath != absolutePath)
                Rebuild();
        }

        public void Rebuild()
        {
            Release();
            _loadedPath = absolutePath;
            if (string.IsNullOrEmpty(absolutePath)) return; // silent while path empty
            if (!File.Exists(absolutePath))
            {
                Debug.LogWarning($"[PlyPointCloudView] File not found: {absolutePath}", this);
                return;
            }

            try
            {
                _LoadPly(absolutePath, out var positions, out var colors);
                if (positions == null || positions.Length == 0)
                {
                    Debug.LogWarning("[PlyPointCloudView] PLY loaded but contains zero points.", this);
                    return;
                }

                // Bounds
                Vector3 mn = positions[0], mx = positions[0];
                for (int i = 1; i < positions.Length; i++)
                {
                    var p = positions[i];
                    if (p.x < mn.x) mn.x = p.x; if (p.y < mn.y) mn.y = p.y; if (p.z < mn.z) mn.z = p.z;
                    if (p.x > mx.x) mx.x = p.x; if (p.y > mx.y) mx.y = p.y; if (p.z > mx.z) mx.z = p.z;
                }
                loadedBoundsMin = mn;
                loadedBoundsMax = mx;
                loadedPointCount = positions.Length;

                if (recenterToOrigin)
                {
                    var c = (mn + mx) * 0.5f;
                    for (int i = 0; i < positions.Length; i++) positions[i] -= c;
                    mn -= c; mx -= c;
                }

                int n = positions.Length;
                var indices = new int[n];
                for (int i = 0; i < n; i++) indices[i] = i;

                _mesh = new Mesh
                {
                    name = $"Ply_{Path.GetFileNameWithoutExtension(absolutePath)}",
                    hideFlags = HideFlags.HideAndDontSave,
                    indexFormat = (n > 65535) ? IndexFormat.UInt32 : IndexFormat.UInt16,
                };
                _mesh.SetVertices(positions);
                _mesh.SetColors(colors);
                _mesh.SetIndices(indices, MeshTopology.Points, 0);
                _mesh.bounds = new Bounds((mn + mx) * 0.5f, mx - mn);

                GetComponent<MeshFilter>().sharedMesh = _mesh;

                _mat = materialOverride != null
                    ? new Material(materialOverride)
                    : _CreateDefaultMaterial();
                GetComponent<MeshRenderer>().sharedMaterial = _mat;
            }
            catch (Exception e)
            {
                Debug.LogError($"[PlyPointCloudView] Load failed: {e}", this);
            }
        }

        void Release()
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
        }

        static Material _CreateDefaultMaterial()
        {
            // URP: use "Universal Render Pipeline/Unlit" with vertex color.
            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                            ?? Shader.Find("Universal Render Pipeline/Unlit")
                            ?? Shader.Find("Unlit/Color");
            var m = new Material(shader) { name = "PlyPointCloud_DefaultMat", hideFlags = HideFlags.HideAndDontSave };
            // Let vertex color drive the final color (Unlit usually respects vertex color in Points topology)
            return m;
        }

        // ── PLY parsing ────────────────────────────────────────────────────

        enum PType { UChar, Char, UShort, Short, UInt, Int, Float, Double }
        struct PField { public string name; public PType type; public int offset; }

        static int TypeSize(PType t) => t switch
        {
            PType.UChar or PType.Char => 1,
            PType.UShort or PType.Short => 2,
            PType.UInt or PType.Int or PType.Float => 4,
            PType.Double => 8,
            _ => 0,
        };

        static PType ParseType(string s) => s switch
        {
            "uchar" or "uint8"  => PType.UChar,
            "char"  or "int8"   => PType.Char,
            "ushort" or "uint16" => PType.UShort,
            "short" or "int16"  => PType.Short,
            "uint"  or "uint32" => PType.UInt,
            "int"   or "int32"  => PType.Int,
            "float" or "float32" => PType.Float,
            "double" or "float64" => PType.Double,
            _ => throw new InvalidDataException($"Unsupported PLY property type: {s}")
        };

        void _LoadPly(string path, out Vector3[] positions, out Color32[] colors)
        {
            byte[] raw = File.ReadAllBytes(path);
            int headerEnd = _FindHeaderEnd(raw);
            if (headerEnd < 0) throw new InvalidDataException("PLY: 'end_header' not found");

            string headerText = Encoding.ASCII.GetString(raw, 0, headerEnd);
            _ParseHeader(headerText, out int vertexCount, out var fields, out int recordSize, out bool isBinaryLE);
            if (!isBinaryLE)
                throw new InvalidDataException("PLY: only binary_little_endian is supported");

            int offX = _RequireOffset(fields, "x");
            int offY = _RequireOffset(fields, "y");
            int offZ = _RequireOffset(fields, "z");
            PType typeX = _FieldType(fields, "x");

            int offR = _TryOffset(fields, "red", "r", "diffuse_red");
            int offG = _TryOffset(fields, "green", "g", "diffuse_green");
            int offB = _TryOffset(fields, "blue", "b", "diffuse_blue");
            int offInt = _TryOffset(fields, "scalar_Intensity", "intensity", "scalar_intensity");
            PType typeInt = offInt >= 0 ? _FieldType(fields, offInt) : PType.Float;

            bool hasRgb = offR >= 0 && offG >= 0 && offB >= 0;

            positions = new Vector3[vertexCount];
            colors = new Color32[vertexCount];

            long needed = (long)vertexCount * recordSize;
            if (headerEnd + needed > raw.Length)
                throw new InvalidDataException(
                    $"PLY: truncated — header says {vertexCount}×{recordSize}B but payload is {raw.Length - headerEnd}B");

            int cursor = headerEnd;
            for (int i = 0; i < vertexCount; i++)
            {
                int b = cursor + i * recordSize;
                float x = _ReadFloat(raw, b + offX, typeX);
                float y = _ReadFloat(raw, b + offY, typeX);
                float z = _ReadFloat(raw, b + offZ, typeX);
                // Unity is left-handed — flip X so the cloud isn't mirrored.
                positions[i] = new Vector3(-x, z, y);

                if (hasRgb)
                {
                    byte r = raw[b + offR];
                    byte g = raw[b + offG];
                    byte bl = raw[b + offB];
                    colors[i] = new Color32(r, g, bl, 255);
                }
                else if (offInt >= 0)
                {
                    float inten = _ReadFloat(raw, b + offInt, typeInt);
                    // Potree intensity is stored LAS-style (uint16 0..65535 scaled). Normalize heuristically.
                    float v = inten >= 256f ? (inten / 65535f) : (inten / 255f);
                    v = Mathf.Clamp01(v * intensityGain);
                    byte ch = (byte)(v * 255f);
                    colors[i] = new Color32(
                        (byte)(ch * intensityTint.r),
                        (byte)(ch * intensityTint.g),
                        (byte)(ch * intensityTint.b),
                        255);
                }
                else
                {
                    colors[i] = new Color32(255, 255, 255, 255);
                }
            }
        }

        static int _FindHeaderEnd(byte[] raw)
        {
            // Marker: "end_header\n"
            byte[] marker = Encoding.ASCII.GetBytes("end_header\n");
            for (int i = 0; i <= raw.Length - marker.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < marker.Length; j++)
                {
                    if (raw[i + j] != marker[j]) { match = false; break; }
                }
                if (match) return i + marker.Length;
            }
            // Try CRLF variant
            byte[] marker2 = Encoding.ASCII.GetBytes("end_header\r\n");
            for (int i = 0; i <= raw.Length - marker2.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < marker2.Length; j++)
                {
                    if (raw[i + j] != marker2[j]) { match = false; break; }
                }
                if (match) return i + marker2.Length;
            }
            return -1;
        }

        static void _ParseHeader(string header, out int vertexCount, out PField[] fields,
                                 out int recordSize, out bool isBinaryLE)
        {
            vertexCount = 0;
            var list = new System.Collections.Generic.List<PField>();
            int offset = 0;
            isBinaryLE = false;
            bool inVertexElement = false;
            foreach (var rawLine in header.Split('\n'))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                var toks = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (toks.Length == 0) continue;
                switch (toks[0])
                {
                    case "format":
                        if (toks.Length >= 2 && toks[1] == "binary_little_endian") isBinaryLE = true;
                        break;
                    case "element":
                        if (toks.Length >= 3)
                        {
                            inVertexElement = (toks[1] == "vertex");
                            if (inVertexElement) vertexCount = int.Parse(toks[2]);
                        }
                        break;
                    case "property":
                        if (!inVertexElement) break;
                        if (toks.Length < 3) break;
                        var t = ParseType(toks[1]);
                        list.Add(new PField { name = toks[toks.Length - 1], type = t, offset = offset });
                        offset += TypeSize(t);
                        break;
                }
            }
            fields = list.ToArray();
            recordSize = offset;
        }

        static int _RequireOffset(PField[] f, string name)
        {
            var o = _TryOffset(f, name);
            if (o < 0) throw new InvalidDataException($"PLY: missing required property '{name}'");
            return o;
        }

        static int _TryOffset(PField[] f, params string[] names)
        {
            foreach (var n in names)
                foreach (var fi in f)
                    if (fi.name == n) return fi.offset;
            return -1;
        }

        static PType _FieldType(PField[] f, string name)
        {
            foreach (var fi in f) if (fi.name == name) return fi.type;
            throw new InvalidDataException($"PLY: missing property '{name}'");
        }

        static PType _FieldType(PField[] f, int offset)
        {
            foreach (var fi in f) if (fi.offset == offset) return fi.type;
            return PType.Float;
        }

        static float _ReadFloat(byte[] raw, int at, PType t) => t switch
        {
            PType.Float  => BitConverter.ToSingle(raw, at),
            PType.Double => (float)BitConverter.ToDouble(raw, at),
            PType.Int    => BitConverter.ToInt32(raw, at),
            PType.UInt   => BitConverter.ToUInt32(raw, at),
            PType.Short  => BitConverter.ToInt16(raw, at),
            PType.UShort => BitConverter.ToUInt16(raw, at),
            PType.Char   => (sbyte)raw[at],
            PType.UChar  => raw[at],
            _ => 0f,
        };
    }
}
