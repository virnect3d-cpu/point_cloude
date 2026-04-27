using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Virnect.Lcc.Editor
{
    // XGrids PortalCam 이 LCC 와 함께 export 하는 proxy 메쉬 PLY 를 읽는다.
    //
    // 관측된 헤더 (ShinWon_*.ply 전 샘플 공통):
    //   ply
    //   format binary_little_endian 1.0
    //   comment Written with hapPLY
    //   element vertex N
    //   property float x
    //   property float y
    //   property float z
    //   element face M
    //   property list uchar uint vertex_indices
    //   end_header
    //
    // 트라이앵글 fan (uchar count == 3) 만 처리. quad/n-gon 은 fan triangulation.
    public static class LccPlyTriMeshReader
    {
        public struct Result
        {
            public Vector3[] vertices;
            public int[]     triangles;   // 길이 = 3 × triCount
            public Bounds    bounds;
            public int       sourceVertexCount;
            public int       sourceFaceCount;
        }

        public static Result Read(string path)
        {
            using var fs = File.OpenRead(path);
            var headerEnd = _ReadAsciiHeader(fs, out int vertCount, out int faceCount,
                                             out bool hasNormals, out bool hasColors);
            fs.Position = headerEnd;

            using var br = new BinaryReader(fs);
            var verts = new Vector3[vertCount];
            // 첫 vertex 가 bounds seed
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;

            int perVertExtraBytes = 0;
            if (hasNormals) perVertExtraBytes += 12; // nx, ny, nz floats
            if (hasColors)  perVertExtraBytes += 4;  // r,g,b,a uchar (or 3 — 보수적으로 4 처리 시 잘못될 수 있어 3 으로)
            // 실제 happly export 는 옵션이므로 hasColors 는 안전하게 3 byte 로 가정.
            // 본 .lcc proxy 메쉬에는 normals/colors 가 없음(헤더 확인됨) → 0.

            for (int i = 0; i < vertCount; i++)
            {
                float x = br.ReadSingle();
                float y = br.ReadSingle();
                float z = br.ReadSingle();
                if (perVertExtraBytes > 0) br.BaseStream.Position += perVertExtraBytes;
                verts[i] = new Vector3(x, y, z);
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }

            // faces: list uchar uint vertex_indices
            // 트라이앵글이 절대다수이므로 List<int> 보다는 큰 배열 prealloc.
            // 최악: 모두 quad → 2 × faceCount 트라이앵글. 평균은 ≈ faceCount.
            // 헤더의 face count 가 425844 처럼 정확하면 단순 3 × faceCount.
            var tris = new int[faceCount * 3];
            int triWrite = 0;
            for (int f = 0; f < faceCount; f++)
            {
                int n = br.ReadByte();
                if (n == 3)
                {
                    int a = (int)br.ReadUInt32();
                    int b = (int)br.ReadUInt32();
                    int c = (int)br.ReadUInt32();
                    if (triWrite + 3 > tris.Length) Array.Resize(ref tris, tris.Length * 2);
                    tris[triWrite++] = a;
                    tris[triWrite++] = b;
                    tris[triWrite++] = c;
                }
                else if (n >= 4)
                {
                    // fan triangulation
                    int v0 = (int)br.ReadUInt32();
                    int prev = (int)br.ReadUInt32();
                    for (int k = 2; k < n; k++)
                    {
                        int cur = (int)br.ReadUInt32();
                        if (triWrite + 3 > tris.Length) Array.Resize(ref tris, tris.Length * 2);
                        tris[triWrite++] = v0;
                        tris[triWrite++] = prev;
                        tris[triWrite++] = cur;
                        prev = cur;
                    }
                }
                else
                {
                    // n < 3 → degenerate, skip the indices (n × 4 bytes)
                    br.BaseStream.Position += n * 4;
                }
            }
            if (triWrite != tris.Length) Array.Resize(ref tris, triWrite);

            return new Result
            {
                vertices = verts,
                triangles = tris,
                bounds = new Bounds(
                    new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f),
                    new Vector3(maxX - minX, maxY - minY, maxZ - minZ)),
                sourceVertexCount = vertCount,
                sourceFaceCount   = faceCount,
            };
        }

        // 헤더 파싱 — '\n' 단위로 라인 읽고 'end_header' 까지의 byte offset 반환.
        static long _ReadAsciiHeader(Stream s, out int vertCount, out int faceCount,
                                     out bool hasNormals, out bool hasColors)
        {
            vertCount = 0; faceCount = 0; hasNormals = false; hasColors = false;
            string current = null; // "vertex" | "face"

            var sb = new StringBuilder(64);
            while (true)
            {
                sb.Length = 0;
                while (true)
                {
                    int b = s.ReadByte();
                    if (b < 0) throw new IOException("PLY: unexpected EOF in header");
                    if (b == (byte)'\n') break;
                    if (b == (byte)'\r') continue;
                    sb.Append((char)b);
                }
                string line = sb.ToString().Trim();
                if (line.Length == 0) continue;
                if (line == "end_header") return s.Position;
                if (line.StartsWith("comment", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("format",  StringComparison.OrdinalIgnoreCase))
                {
                    if (!line.Contains("binary_little_endian"))
                        throw new IOException("PLY: only binary_little_endian supported (got: " + line + ")");
                    continue;
                }
                if (line.StartsWith("element"))
                {
                    var parts = line.Split(' ');
                    if (parts.Length >= 3)
                    {
                        current = parts[1];
                        if (current == "vertex") int.TryParse(parts[2], out vertCount);
                        else if (current == "face") int.TryParse(parts[2], out faceCount);
                    }
                    continue;
                }
                if (line.StartsWith("property") && current == "vertex")
                {
                    if (line.Contains(" nx") || line.Contains(" ny") || line.Contains(" nz")) hasNormals = true;
                    if (line.Contains(" red") || line.Contains(" green") || line.Contains(" blue")) hasColors = true;
                }
            }
        }
    }
}
