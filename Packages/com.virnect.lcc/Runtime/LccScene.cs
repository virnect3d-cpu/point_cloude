using System.IO;
using UnityEngine;

namespace Virnect.Lcc
{
    // 한 개의 LCC "씬" — 디렉토리 단위로 묶인 파일 세트를 보유.
    //   <root>/
    //     <name>.lcc        ← manifest (JSON)
    //     attrs.lcp         ← 씬 transform/spawn/poses
    //     data.bin          ← LOD 스플랫 페이로드
    //     index.bin         ← 청크 인덱스
    //     environment.bin   ← 환경/스카이박스 등
    //     collision.lci     ← 충돌 프록시
    //     assets/poses.json
    //     thumb.jpg
    //     (선택) mesh-files/<name>.ply  ← XGrids 가 함께 내보낸 proxy 메쉬
    [CreateAssetMenu(fileName = "NewLccScene", menuName = "Virnect/LCC Scene", order = 50)]
    public sealed class LccScene : ScriptableObject
    {
        public string rootPath;
        public LccManifest    manifest;
        public LccSceneAttrs  attrs;

        public string LccPath         => manifest == null ? null : Path.Combine(rootPath, manifest.name + ".lcc");
        public string DataBinPath     => Path.Combine(rootPath, "data.bin");
        public string IndexBinPath    => Path.Combine(rootPath, "index.bin");
        public string CollisionPath   => Path.Combine(rootPath, "collision.lci");
        public string ProxyMeshPlyPath(string sceneName) =>
            Path.Combine(Path.GetDirectoryName(rootPath) ?? "", "mesh-files", sceneName + ".ply");
    }
}
