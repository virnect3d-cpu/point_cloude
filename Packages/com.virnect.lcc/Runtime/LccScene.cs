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
        // + manifest.name 이 실제 파일명과 다른 경우도 있어서 (예: "XGrids Splats" vs "ShinWon_1st_Cutter")
        // 이름 후보 × 경로 후보 조합을 순서대로 탐색.
        public string ResolveProxyMeshPlyAssetPath()
        {
            if (string.IsNullOrEmpty(rootPath)) return null;

            string parent      = Path.GetDirectoryName(rootPath) ?? "";
            string grandparent = Path.GetDirectoryName(parent)   ?? "";

            string[] nameCandidates =
            {
                manifest?.name,
                this.name,
                Path.GetFileName(rootPath),   // LCC 드롭 폴더 이름 (가장 안정적)
            };

            string[] dirCandidates =
            {
                rootPath,
                Path.Combine(rootPath, "mesh-files"),
                Path.Combine(parent, "mesh-files"),
                parent,
                Path.Combine(grandparent, "mesh-files"),
            };

            // 1) 이름 × 디렉토리 조합
            foreach (var name in nameCandidates)
            {
                if (string.IsNullOrEmpty(name)) continue;
                foreach (var dir in dirCandidates)
                {
                    string norm = Path.Combine(dir, name + ".ply").Replace('\\', '/');
                    string abs = Path.GetFullPath(norm);
                    if (File.Exists(abs)) return norm;
                }
            }

            // 2) 이름 매칭 실패 시 — rootPath 에 .ply 가 딱 하나면 그거 사용
            if (Directory.Exists(rootPath))
            {
                var plys = Directory.GetFiles(rootPath, "*.ply");
                if (plys.Length == 1) return plys[0].Replace('\\', '/');
            }

            return null;
        }
    }
}
