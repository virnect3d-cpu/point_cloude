using System;
using Unity.Mathematics;
using UnityEngine;

namespace Virnect.Lcc
{
    // Gaussian Splat 레코드 디코더 (XGrids LCC).
    //
    // 매니페스트 attributes 에서 6개 속성이 관측됨:
    //   position (float3), normal (float3 — sample 파일은 0 이라 미사용 가능),
    //   color (float3), scale (float3), rotation (float4 quat), opacity (float),
    //   sh (Spherical Harmonics coefficients — 차수 미상)
    //
    // encoding "COMPRESS" 는 양자화 + 가변길이 코드로 추정.
    // 실제 포맷은 docs/lcc-format.md 에 역분석 진척을 누적 기록.
    //
    // v2 첫 구현 타겟 = "좌표만 뽑아 포인트로 다운그레이드" ((b)안).
    // SH/opacity/스케일은 일단 무시, position + color 만 추출.
    public static class LccSplatDecoder
    {
        public struct Point
        {
            public float3 position;   // 월드 좌표로 변환된 값 (manifest.offset + manifest.scale 적용)
            public Color32 color;     // 0~255
        }

        // 지정 LOD 의 청크 바이트를 점 배열로 변환.
        // 현재는 스텁 — 바이트 레이아웃 확정 후 구현.
        public static Point[] DecodeLod(byte[] chunk, LccManifest manifest, int lodLevel)
        {
            throw new NotImplementedException(
                "LccSplatDecoder.DecodeLod — XGrids data.bin 레이아웃 역분석 대기. " +
                "임시 대안: mesh-files/*.ply (XGrids 가 함께 내보내는 proxy) 를 사용.");
        }
    }
}
