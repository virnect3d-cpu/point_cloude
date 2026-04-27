using System;
using System.IO;
using UnityEngine;

namespace Virnect.Lcc
{
    // collision.lci 파서.
    //
    // 관측된 헤더 (docs/lcc-format.md §5):
    //   [0x00..0x04)  magic 'coll'
    //   [0x04..0x08)  uint32 LE  version (관측: 2)
    //   [0x08..0x0C)  uint32 LE  ? (관측: 288 = 0x120) — 헤더 크기 추정
    //   [0x0C..0x18)  float32×3  AABB min  (x, y, z)
    //   [0x18..0x24)  float32×3  AABB max  (x, y, z)
    //   [0x24..0x30)  float32×2 + uint32   cellSize.x/y + cellCount(?)
    //   [0x30..0x200) 이후  cell payload — 셀 구조 미확정 (v2.1 작업)
    //
    // v2.0 에서는 헤더 메타만 파싱해 AABB / cellSize 노출.
    // 완전 mesh 추출은 mesh-files/*.ply 또는 백엔드 /api/mesh-collider 사용 권장.
    public static class LccCollisionLoader
    {
        public static readonly byte[] Magic = { (byte)'c', (byte)'o', (byte)'l', (byte)'l' };

        [Serializable]
        public struct Header
        {
            public int     version;        // 관측: 2
            public int     headerWord;     // 관측: 288 — header size 추정
            public Vector3 aabbMin;
            public Vector3 aabbMax;
            public Vector2 cellSize;       // X, Y 셀 길이
            public int     cellCount;      // ? — 0x24+8 의 uint32

            public Bounds  Bounds => new Bounds((aabbMin + aabbMax) * 0.5f, aabbMax - aabbMin);
        }

        public static bool HasValidMagic(string path)
        {
            if (!File.Exists(path)) return false;
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return fs.Read(head) == 4
                && head[0] == Magic[0] && head[1] == Magic[1]
                && head[2] == Magic[2] && head[3] == Magic[3];
        }

        /// <summary>collision.lci 헤더(48 B) 파싱.
        /// 셀 페이로드는 아직 미해독 — 헤더 메타만 노출.</summary>
        public static bool TryReadHeader(string path, out Header header)
        {
            header = default;
            if (!File.Exists(path)) return false;
            using var fs = File.OpenRead(path);
            Span<byte> buf = stackalloc byte[0x30];
            if (fs.Read(buf) != buf.Length) return false;
            // magic
            if (buf[0]!=Magic[0]||buf[1]!=Magic[1]||buf[2]!=Magic[2]||buf[3]!=Magic[3]) return false;
            header.version    = BitConverter.ToInt32(buf.Slice(0x04, 4));
            header.headerWord = BitConverter.ToInt32(buf.Slice(0x08, 4));
            header.aabbMin = new Vector3(
                BitConverter.ToSingle(buf.Slice(0x0C, 4)),
                BitConverter.ToSingle(buf.Slice(0x10, 4)),
                BitConverter.ToSingle(buf.Slice(0x14, 4)));
            header.aabbMax = new Vector3(
                BitConverter.ToSingle(buf.Slice(0x18, 4)),
                BitConverter.ToSingle(buf.Slice(0x1C, 4)),
                BitConverter.ToSingle(buf.Slice(0x20, 4)));
            header.cellSize = new Vector2(
                BitConverter.ToSingle(buf.Slice(0x24, 4)),
                BitConverter.ToSingle(buf.Slice(0x28, 4)));
            header.cellCount = BitConverter.ToInt32(buf.Slice(0x2C, 4));
            return true;
        }

        public static Mesh LoadAsMesh(string path)
        {
            throw new NotImplementedException(
                "collision.lci 셀 페이로드 역분석 대기 — TryReadHeader 로 AABB/cellSize 만 사용 가능. " +
                "전체 mesh 가 필요하면 mesh-files/*.ply 또는 /api/mesh-collider 백엔드 사용.");
        }
    }
}
