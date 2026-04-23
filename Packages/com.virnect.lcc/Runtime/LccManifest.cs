using System;
using Unity.Mathematics;
using UnityEngine;

namespace Virnect.Lcc
{
    // .lcc JSON 매니페스트 ─ XGrids PortalCam 포맷.
    // data.bin / index.bin 의 레이아웃을 해석하기 위한 메타데이터.
    // 실관측 필드 (sample: ShinWon_1st_Cutter.lcc):
    //   version "5.0", source "lcc", dataType "PortalCam",
    //   totalSplats 9965708, totalLevel 5, splats [5.1M, 2.6M, 1.3M, 641K, 320K],
    //   cellLengthX/Y 30, offset/shift [3], scale [3], epsg 0,
    //   encoding "COMPRESS", fileType "Portable",
    //   boundingBox {min[3], max[3]},
    //   attributes [{name:"position", min[3], max[3]}, normal, color, scale, rotation, opacity, sh]
    [Serializable]
    public sealed class LccManifest
    {
        public string version;
        public string guid;
        public string name;
        public string description;
        public string source;      // "lcc"
        public string dataType;    // "PortalCam"
        public int    totalSplats;
        public int    totalLevel;
        public int    cellLengthX;
        public int    cellLengthY;
        public int    indexDataSize;
        public float[] offset;     // length 3
        public int    epsg;
        public float[] shift;      // length 3
        public float[] scale;      // length 3
        public int[]  splats;      // per-LOD splat count
        public LccBBox boundingBox;
        public string encoding;    // "COMPRESS"
        public string fileType;    // "Portable"
        public LccAttr[] attributes;

        public static LccManifest Parse(string json) => JsonUtility.FromJson<LccManifest>(json);

        public double3 OffsetD => offset == null || offset.Length < 3
            ? double3.zero
            : new double3(offset[0], offset[1], offset[2]);

        public float3 ScaleF => scale == null || scale.Length < 3
            ? new float3(1,1,1)
            : new float3(scale[0], scale[1], scale[2]);
    }

    [Serializable] public sealed class LccBBox { public float[] min; public float[] max; }
    [Serializable] public sealed class LccAttr { public string name; public float[] min; public float[] max; }
}
