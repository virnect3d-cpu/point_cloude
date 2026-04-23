using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Virnect.Lcc
{
    // Colored point cloud PLY 로더.
    // XGrids_Splats_LODn.ply 같은 "vertex only + rgb(a) uchar" PLY 를 읽어
    // (positions, colors) 배열로 반환. Mesh 생성은 안 함 (colorizer 입력용).
    //
    // 지원 헤더 예:
    //   format binary_little_endian 1.0
    //   element vertex N
    //   property float x
    //   property float y
    //   property float z
    //   property uchar red
    //   property uchar green
    //   property uchar blue
    //   (optional) property uchar alpha
    //   end_header
    //
    // property 순서/타입은 헤더를 그대로 파싱해서 offset 계산 → 임의 순서 OK.
    public static class LccColoredPointCloudPlyLoader
    {
        public struct Cloud
        {
            public Vector3[] positions;
            public Color32[] colors;
        }

        enum PType { UChar, Char, UShort, Short, UInt, Int, Float, Double }

        struct PField { public string name; public PType type; public int offset; }

        public static Cloud Load(string absolutePath)
        {
            if (!File.Exists(absolutePath))
                throw new FileNotFoundException($"PLY not found: {absolutePath}");

            byte[] raw = File.ReadAllBytes(absolutePath);
            int headerEnd = _FindHeaderEnd(raw);
            if (headerEnd < 0) throw new InvalidDataException("PLY: 'end_header' marker not found");

            string headerText = Encoding.ASCII.GetString(raw, 0, headerEnd);
            _ParseHeader(headerText, out int vertexCount, out var fields, out int recordSize);

            int offX = _RequireOffset(fields, "x");
            int offY = _RequireOffset(fields, "y");
            int offZ = _RequireOffset(fields, "z");
            int offR = _TryOffset(fields, "red",   "r", "diffuse_red",   "f_dc_0");
            int offG = _TryOffset(fields, "green", "g", "diffuse_green", "f_dc_1");
            int offB = _TryOffset(fields, "blue",  "b", "diffuse_blue",  "f_dc_2");
            bool fdc = _HasField(fields, "f_dc_0");

            if (offR < 0 || offG < 0 || offB < 0)
                throw new InvalidDataException(
                    "PLY: 색상 속성 (red/green/blue 또는 f_dc_0/1/2) 없음. position-only PLY 는 LccMeshPlyLoader 사용");

            var positions = new Vector3[vertexCount];
            var colors    = new Color32[vertexCount];

            int cursor = headerEnd;
            for (int i = 0; i < vertexCount; i++)
            {
                int baseOff = cursor + i * recordSize;
                float x = BitConverter.ToSingle(raw, baseOff + offX);
                float y = BitConverter.ToSingle(raw, baseOff + offY);
                float z = BitConverter.ToSingle(raw, baseOff + offZ);
                positions[i] = new Vector3(x, y, z);

                if (fdc)
                {
                    // SH DC 계수 → RGB (SH_C0 상수 포함)
                    //   rgb = clamp(0.5 + 0.28209479177 * f_dc, 0..1)
                    const float SH_C0 = 0.28209479177387814f;
                    float r = 0.5f + SH_C0 * BitConverter.ToSingle(raw, baseOff + offR);
                    float g = 0.5f + SH_C0 * BitConverter.ToSingle(raw, baseOff + offG);
                    float b = 0.5f + SH_C0 * BitConverter.ToSingle(raw, baseOff + offB);
                    colors[i] = new Color32(
                        (byte)Mathf.Clamp(r * 255f, 0f, 255f),
                        (byte)Mathf.Clamp(g * 255f, 0f, 255f),
                        (byte)Mathf.Clamp(b * 255f, 0f, 255f),
                        255);
                }
                else
                {
                    colors[i] = new Color32(raw[baseOff + offR], raw[baseOff + offG], raw[baseOff + offB], 255);
                }
            }

            return new Cloud { positions = positions, colors = colors };
        }

        // ──────── Header parsing ────────

        static readonly byte[] EndHeaderMarker = Encoding.ASCII.GetBytes("end_header");

        static int _FindHeaderEnd(byte[] raw)
        {
            int limit = Math.Min(raw.Length - EndHeaderMarker.Length, 64 * 1024);
            for (int i = 0; i < limit; i++)
            {
                bool match = true;
                for (int j = 0; j < EndHeaderMarker.Length; j++)
                    if (raw[i + j] != EndHeaderMarker[j]) { match = false; break; }
                if (!match) continue;

                int after = i + EndHeaderMarker.Length;
                if (after < raw.Length && raw[after] == '\r') after++;
                if (after < raw.Length && raw[after] == '\n') after++;
                return after;
            }
            return -1;
        }

        static void _ParseHeader(string header, out int vertexCount, out List<PField> fields, out int recordSize)
        {
            if (!header.Contains("binary_little_endian"))
                throw new InvalidDataException("PLY: only binary_little_endian supported");

            vertexCount = 0;
            fields = new List<PField>();
            recordSize = 0;
            bool inVertexBlock = false;

            foreach (var rawLine in header.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("element vertex "))
                {
                    int.TryParse(line.Substring("element vertex ".Length).Trim(), out vertexCount);
                    inVertexBlock = true;
                }
                else if (line.StartsWith("element "))
                {
                    inVertexBlock = false;
                }
                else if (inVertexBlock && line.StartsWith("property "))
                {
                    // property <type> <name>  (list properties 는 이 블록에 없어야 함)
                    var parts = line.Split(' ');
                    if (parts.Length < 3)
                        throw new InvalidDataException($"PLY: invalid property line '{line}'");
                    if (parts[1] == "list")
                        throw new InvalidDataException("PLY: vertex element 에 list property 는 지원 안 함");
                    var t = _TypeOf(parts[1]);
                    fields.Add(new PField { name = parts[2], type = t, offset = recordSize });
                    recordSize += _SizeOf(t);
                }
            }
            if (vertexCount <= 0 || fields.Count == 0)
                throw new InvalidDataException($"PLY: bad vertex block (count={vertexCount}, fields={fields.Count})");
        }

        static PType _TypeOf(string s) => s switch
        {
            "uchar"  or "uint8"  => PType.UChar,
            "char"   or "int8"   => PType.Char,
            "ushort" or "uint16" => PType.UShort,
            "short"  or "int16"  => PType.Short,
            "uint"   or "uint32" => PType.UInt,
            "int"    or "int32"  => PType.Int,
            "float"  or "float32"=> PType.Float,
            "double" or "float64"=> PType.Double,
            _ => throw new InvalidDataException($"PLY: unknown property type '{s}'"),
        };

        static int _SizeOf(PType t) => t switch
        {
            PType.UChar or PType.Char => 1,
            PType.UShort or PType.Short => 2,
            PType.UInt or PType.Int or PType.Float => 4,
            PType.Double => 8,
            _ => throw new InvalidOperationException(),
        };

        static int _RequireOffset(List<PField> fs, string name)
        {
            foreach (var f in fs) if (f.name == name) return f.offset;
            throw new InvalidDataException($"PLY: required property '{name}' missing");
        }

        static int _TryOffset(List<PField> fs, params string[] names)
        {
            foreach (var n in names)
                foreach (var f in fs) if (f.name == n) return f.offset;
            return -1;
        }

        static bool _HasField(List<PField> fs, string name)
        {
            foreach (var f in fs) if (f.name == name) return true;
            return false;
        }
    }
}
