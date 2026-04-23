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

        // Legacy 경로 (sibling mesh-files/<name>.ply). 신규 코드는 ResolveProxyMeshPly 권장.
        public string ProxyMeshPlyPath(string sceneName) =>
            Path.Combine(Path.GetDirectoryName(rootPath) ?? "", "mesh-files", sceneName + ".ply");

        // LCC 드롭 구조가 제각각이라 (사용자가 lcc-result 만 복사하기도, 통째로 복사하기도)
        // 후보 경로들을 순서대로 탐색. 프로젝트 상대 경로(Assets/...) 반환.
        public string ResolveProxyMeshPlyAssetPath()
        {
            if (string.IsNullOrEmpty(rootPath)) return null;
            string name = manifest?.name ?? this.name;
            if (string.IsNullOrEmpty(name)) return null;

            string parent      = Path.GetDirectoryName(rootPath) ?? "";
            string grandparent = Path.GetDirectoryName(parent)   ?? "";

            string[] candidates =
            {
                Path.Combine(rootPath, $"{name}.ply"),                // <root>/<name>.ply
                Path.Combine(rootPath, "mesh-files", $"{name}.ply"),  // <root>/mesh-files/<name>.ply
                Path.Combine(parent, "mesh-files", $"{name}.ply"),    // <parent>/mesh-files/<name>.ply (legacy)
                Path.Combine(parent, $"{name}.ply"),                  // <parent>/<name>.ply
                Path.Combine(grandparent, "mesh-files", $"{name}.ply"),
            };

            foreach (var rel in candidates)
            {
                string norm = rel.Replace('\\', '/');
                string abs = Path.GetFullPath(norm);
                if (File.Exists(abs)) return norm;
            }
            return null;
        }
    }
}
