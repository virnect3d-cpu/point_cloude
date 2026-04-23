using System;
using System.IO;
using Unity.Mathematics;
using UnityEngine;

namespace Virnect.Lcc
{
    // XGrids PortalCam LCC Gaussian Splat decoder.
    //
    // Layout verified by tools_probe_databin.py on ShinWon_1st_Cutter
    // (file size 318,902,656 B / 9,965,708 splats = exactly 32.0 B/splat):
    //
    //   offset 0..12  : float32 LE × 3  → position (world coords, inside scene bbox)
    //   offset 12..16 : RGBA8            → color (verified 0..255 range all channels)
    //   offset 16..32 : TODO             → scale/rotation/opacity/SH (not needed for
    //                                       point-cloud downgrade; ignored in v2)
    //
    // LOD slicing: cumulative splats[] × 32B gives the byte range for each LOD.
    public static class LccSplatDecoder
    {
        public const int RecordSize = 32;

        public struct Point
        {
            public float3  position;
            public Color32 color;       // RGBA8
        }

        // Read one LOD level as points. Does NOT hold onto the file handle.
        public static Point[] DecodeLod(LccScene scene, int lodLevel)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));
            var manifest = scene.manifest ?? throw new InvalidOperationException("scene.manifest is null");
            var splats = manifest.splats;
            if (splats == null || lodLevel < 0 || lodLevel >= splats.Length)
                throw new ArgumentOutOfRangeException(nameof(lodLevel),
                    $"Must be in 0..{(splats?.Length ?? 0) - 1}");

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
                var buf = new byte[Math.Min(byteLen, 16 * 1024 * 1024)];  // 16MB chunks
                int outIdx = 0;
                long remaining = byteLen;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(remaining, buf.Length);
                    // Align to record boundary
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
                        points[outIdx++] = new Point
                        {
                            position = new float3(x, y, z),
                            color    = new Color32(r, g, b, a),
                        };
                    }
                    remaining -= read;
                }
                if (outIdx != count)
                    throw new IOException($"short decode: expected {count} got {outIdx}");
            }
            return points;
        }

        // Byte range of a LOD inside data.bin (useful for memory-mapped/streaming access).
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
