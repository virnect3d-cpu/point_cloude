using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Virnect.Lcc
{
    // 여러 LCC 씬을 합쳐 하나의 월드로 구성.
    // 전제(사용자 확정): 모든 씬이 동일 EPSG 좌표계 → 단순 오프셋 누적으로 정합 가능.
    //
    // 동작:
    //   1. 각 scene.manifest.offset + attrs.transform.position 을 월드 위치로 삼음
    //   2. 회전/스케일은 attrs.transform 을 따른다
    //   3. 겹치는 영역의 ICP 는 v2 범위 밖 (선택 옵션)
    public static class LccSceneMerger
    {
        public struct PlacedScene
        {
            public LccScene scene;
            public double3  worldOffset;   // EPSG 원점 기준 오프셋 (double 정밀도 유지)
            public quaternion rotation;
            public float3   scale;
        }

        public static List<PlacedScene> Plan(IEnumerable<LccScene> scenes)
        {
            var list = new List<PlacedScene>();
            foreach (var s in scenes)
            {
                if (s == null || s.manifest == null) continue;
                var ofs = s.manifest.OffsetD;
                if (s.attrs?.transform?.position != null && s.attrs.transform.position.Length >= 3)
                    ofs += new double3(
                        s.attrs.transform.position[0],
                        s.attrs.transform.position[1],
                        s.attrs.transform.position[2]);

                var rot = quaternion.identity;
                if (s.attrs?.transform?.rotation != null && s.attrs.transform.rotation.Length >= 4)
                    rot = new quaternion(
                        s.attrs.transform.rotation[0],
                        s.attrs.transform.rotation[1],
                        s.attrs.transform.rotation[2],
                        s.attrs.transform.rotation[3]);

                var scl = new float3(1, 1, 1);
                if (s.attrs?.transform?.scale != null && s.attrs.transform.scale.Length >= 3)
                    scl = new float3(
                        s.attrs.transform.scale[0],
                        s.attrs.transform.scale[1],
                        s.attrs.transform.scale[2]);

                list.Add(new PlacedScene { scene = s, worldOffset = ofs, rotation = rot, scale = scl });
            }
            return list;
        }
    }
}
