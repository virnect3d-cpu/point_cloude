using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace Virnect.Lcc
{
    // XGrids LCC 드롭의 mesh-files/<name>.ply (proxy 메쉬) 로더.
    // format binary_little_endian 1.0
    //   element vertex N   property float x/y/z
    //   element face   M   property list uchar uint vertex_indices
    public static class LccMeshPlyLoader
    {
        public static Mesh Load(string absolutePath)
        {
            if (!File.Exists(absolutePath))
                throw new FileNotFoundException($"PLY not found: {absolutePath}");

            byte[] raw = File.ReadAllBytes(absolutePath);
            int headerEnd = _FindHeaderEnd(raw);
            if (headerEnd < 0) throw new InvalidDataException("PLY: 'end_header' marker not found");

            string headerText = Encoding.ASCII.GetString(raw, 0, headerEnd);
            _ParseHeader(headerText, out int vertexCount, out int faceCount);

            var mesh = new Mesh { name = Path.GetFileNameWithoutExtension(absolutePath) };
            if (vertexCount > 65535) mesh.indexFormat = IndexFormat.UInt32;

            var verts = new Vector3[vertexCount];
            int cursor = headerEnd;
            for (int i = 0; i < vertexCount; i++)
            {
                float x = BitConverter.ToSingle(raw, cursor);     cursor += 4;
                float y = BitConverter.ToSingle(raw, cursor);     cursor += 4;
                float z = BitConverter.ToSingle(raw, cursor);     cursor += 4;
                verts[i] = new Vector3(x, y, z);
            }

            var tris = new int[faceCount * 3];
            int ti = 0;
            for (int f = 0; f < faceCount; f++)
            {
                byte n = raw[cursor++];
                if (n != 3)
                    throw new InvalidDataException($"PLY: only triangular faces supported (face {f} has {n} verts)");
                tris[ti++] = BitConverter.ToInt32(raw, cursor); cursor += 4;
                tris[ti++] = BitConverter.ToInt32(raw, cursor); cursor += 4;
                tris[ti++] = BitConverter.ToInt32(raw, cursor); cursor += 4;
            }

            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }

        static readonly byte[] EndHeaderMarker = Encoding.ASCII.GetBytes("end_header");

        static int _FindHeaderEnd(byte[] raw)
        {
            int limit = Math.Min(raw.Length - EndHeaderMarker.Length, 8192);
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

        static void _ParseHeader(string header, out int vertexCount, out int faceCount)
        {
            vertexCount = 0;
            faceCount = 0;
            if (!header.Contains("binary_little_endian"))
                throw new InvalidDataException("PLY: only binary_little_endian is supported");

            foreach (var rawLine in header.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("element vertex "))
                    int.TryParse(line.Substring("element vertex ".Length).Trim(), out vertexCount);
                else if (line.StartsWith("element face "))
                    int.TryParse(line.Substring("element face ".Length).Trim(), out faceCount);
            }
            if (vertexCount <= 0 || faceCount <= 0)
                throw new InvalidDataException($"PLY: bad counts (v={vertexCount}, f={faceCount})");
        }
    }
}
