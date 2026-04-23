using System;
using System.IO;
using Unity.Mathematics;
using UnityEngine;

namespace Virnect.Lcc
{
    // XGrids PortalCam LCC Gaussian Splat decoder.
    //
    // 32-byte fixed record, verified by tools_probe_databin.py +
    // tools_probe_tail16.py + tools_probe_tail_all_lods.py on
    // ShinWon_1st_Cutter (9,965,708 splats × 32 B = 318,902,656 B exact):
    //
    //   [ 0..12) float32 LE × 3   → position (world coords)
    //   [12..16) u8     × 4       → RGBA8 color
    //   [16..22) u16    × 3       → scale (unquantized to manifest.attrs.scale [min..max])
    //   [22..24) u16              → opacity (unquantized to manifest.attrs.opacity [min..max])
    //   [24..26) u16              → unknown, tight distribution ~0.88 (SH DC? reserved?)
    //   [26..32) 6 B              → always zeros (reserved; confirmed across all 5 LODs)
    //
    // Attribute ranges come from manifest.attributes (e.g. scale max = [6.82, 5.71, 3.49]).
    //
    // LOD slicing: cumulative splats[] × 32B.
    public static class LccSplatDecoder
    {
        public const int RecordSize = 32;

        public struct Point
        {
            public float3  position;
            public Color32 color;
            public float3  scale;
            public float   opacity;
        }

        public static Point[] DecodeLod(LccScene scene, int lodLevel)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));
            var manifest = scene.manifest ?? throw new InvalidOperationException("scene.manifest is null");
            var splats = manifest.splats;
            if (splats == null || lodLevel < 0 || lodLevel >= splats.Length)
                throw new ArgumentOutOfRangeException(nameof(lodLevel),
                    $"Must be in 0..{(splats?.Length ?? 0) - 1}");

            // Attribute ranges for unquantization
            float sMin0 = 0, sMin1 = 0, sMin2 = 0, sMax0 = 1, sMax1 = 1, sMax2 = 1;
            float oMin = 0, oMax = 1;
            if (manifest.attributes != null)
            {
                foreach (var a in manifest.attributes)
                {
                    if (a == null) continue;
                    if (a.name == "scale" && a.min?.Length >= 3 && a.max?.Length >= 3)
                    {
                        sMin0 = a.min[0]; sMin1 = a.min[1]; sMin2 = a.min[2];
                        sMax0 = a.max[0]; sMax1 = a.max[1]; sMax2 = a.max[2];
                    }
                    else if (a.name == "opacity" && a.min?.Length >= 1 && a.max?.Length >= 1)
                    {
                        oMin = a.min[0]; oMax = a.max[0];
                    }
                }
            }
            float sRng0 = sMax0 - sMin0;
            float sRng1 = sMax1 - sMin1;
            float sRng2 = sMax2 - sMin2;
            float oRng = oMax - oMin;

            int count = splats[lodLevel];
            long byteStart = 0;
            for (int i = 0; i < lodLevel; i++) byteStart += (long)splats[i] * RecordSize;
            long byteLen = (long)count * RecordSize;

            var dataPath = scene.DataBinPath;
            if (!File.Exists(dataPath))
                throw new FileNotFoundException($"data.bin not found: {dataPath}");

            var points = new Point[count];
            using (var fs = File.OpenRead(dataPath))
            {
                fs.Seek(byteStart, SeekOrigin.Begin);
                var buf = new byte[Math.Min(byteLen, 16 * 1024 * 1024)];
                int outIdx = 0;
                long remaining = byteLen;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(remaining, buf.Length);
                    toRead -= toRead % RecordSize;
                    if (toRead == 0) break;
                    int read = fs.Read(buf, 0, toRead);
                    if (read <= 0) break;
                    int nrec = read / RecordSize;
                    for (int i = 0; i < nrec; i++)
                    {
                        int off = i * RecordSize;
                        float x = BitConverter.ToSingle(buf, off + 0);
                        float y = BitConverter.ToSingle(buf, off + 4);
                        float z = BitConverter.ToSingle(buf, off + 8);
                        byte r = buf[off + 12];
                        byte g = buf[off + 13];
                        byte b = buf[off + 14];
                        byte a = buf[off + 15];
                        ushort qsx = BitConverter.ToUInt16(buf, off + 16);
                        ushort qsy = BitConverter.ToUInt16(buf, off + 18);
                        ushort qsz = BitConverter.ToUInt16(buf, off + 20);
                        ushort qop = BitConverter.ToUInt16(buf, off + 22);
                        points[outIdx++] = new Point
                        {
                            position = new float3(x, y, z),
                            color    = new Color32(r, g, b, a),
                            scale    = new float3(
                                sMin0 + (qsx / 65535f) * sRng0,
                                sMin1 + (qsy / 65535f) * sRng1,
                                sMin2 + (qsz / 65535f) * sRng2),
                            opacity  = oMin + (qop / 65535f) * oRng,
                        };
                    }
                    remaining -= read;
                }
                if (outIdx != count)
                    throw new IOException($"short decode: expected {count} got {outIdx}");
            }
            return points;
        }

        public static (long byteStart, long byteLen, int count) GetLodRange(LccManifest m, int lod)
        {
            if (m?.splats == null || lod < 0 || lod >= m.splats.Length)
                throw new ArgumentOutOfRangeException(nameof(lod));
            long s = 0;
            for (int i = 0; i < lod; i++) s += (long)m.splats[i] * RecordSize;
            return (s, (long)m.splats[lod] * RecordSize, m.splats[lod]);
        }
    }
}
