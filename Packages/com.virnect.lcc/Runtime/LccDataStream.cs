using System;
using System.IO;
using UnityEngine;

namespace Virnect.Lcc
{
    // data.bin 청크 스트림 — LOD 5단계의 스플랫 페이지를 순차 읽기.
    //
    // 바이트 레이아웃은 아직 역분석 중 (docs/lcc-format.md 참조).
    // 확인된 바:
    //   - 첫 12 바이트 ≈ Float32 LE 3개 (position 같은 형태의 실수)
    //   - 총 용량 ≈ totalSplats * (레코드 크기) + 헤더
    //   - splats[i] = LOD i 의 스플랫 개수 (manifest)
    //
    // 여기서는 인터페이스만 확보하고 실제 디코드는 LccSplatDecoder 가 담당.
    public sealed class LccDataStream : IDisposable
    {
        public readonly LccManifest Manifest;
        readonly Stream _stream;
        readonly BinaryReader _r;

        public LccDataStream(string dataBinPath, LccManifest manifest)
        {
            if (!File.Exists(dataBinPath)) throw new FileNotFoundException(dataBinPath);
            Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            _stream = File.OpenRead(dataBinPath);
            _r = new BinaryReader(_stream);
        }

        // LOD 레벨 i 의 청크 바이트 범위를 index.bin 에서 계산해 반환.
        // TODO: index.bin 레이아웃 역분석 필요.
        public (long offset, long length) GetLodRange(int lodLevel)
        {
            throw new NotImplementedException(
                "LCC data.bin/index.bin 레이아웃은 아직 역분석 중입니다. " +
                "docs/lcc-format.md 참조.");
        }

        public byte[] ReadRange(long offset, long length)
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[length];
            int read = _stream.Read(buf, 0, (int)length);
            if (read != length) throw new IOException($"short read: {read}/{length}");
            return buf;
        }

        public void Dispose()
        {
            _r?.Dispose();
            _stream?.Dispose();
        }
    }
}
