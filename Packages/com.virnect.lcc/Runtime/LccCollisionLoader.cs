using System;
using System.IO;
using UnityEngine;

namespace Virnect.Lcc
{
    // collision.lci 파서.
    //
    // 관측된 magic = 'c','o','l','l' (0x636F6C6C) + 바로 이어 uint32 LE.
    // 그 뒤 0x200 바이트 블록으로 Float32 헤더 (추정 AABB) 가 보임.
    // 정확한 레이아웃은 docs/lcc-format.md.
    //
    // v2 첫 구현은 "attrs.lcp 의 simpleMesh.path 가 실제로 PLY 인 경우" 를 우선 처리.
    // ShinWon 샘플의 경우 collision.lci 가 PLY-호환이 아니라 XGrids 고유 컨테이너.
    public static class LccCollisionLoader
    {
        public static readonly byte[] Magic = { (byte)'c', (byte)'o', (byte)'l', (byte)'l' };

        public static bool HasValidMagic(string path)
        {
            if (!File.Exists(path)) return false;
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return fs.Read(head) == 4
                && head[0] == Magic[0] && head[1] == Magic[1]
                && head[2] == Magic[2] && head[3] == Magic[3];
        }

        public static Mesh LoadAsMesh(string path)
        {
            throw new NotImplementedException(
                "collision.lci 레이아웃 역분석 대기 — 현재는 mesh-files/*.ply 사용 권장.");
        }
    }
}
