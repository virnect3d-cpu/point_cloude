using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Virnect.Lcc.Editor
{
    // Unity Editor 에 .lcc 파일을 끌어다 놓으면 자동 임포트.
    // 한 개의 .lcc 는 "디렉토리 단위" 로 간주되므로, 실제로는
    //   <name>.lcc (manifest JSON) 만 Unity Assets 안에 있어도
    //   같은 폴더의 data.bin / index.bin / attrs.lcp 를 함께 읽음.
    //
    // 스캐폴드 단계: LccManifest 파싱까지만 하고 LccScene ScriptableObject 생성.
    // 스플랫 디코드는 LccSplatDecoder 완성 후 연결.
    [ScriptedImporter(version: 1, ext: "lcc")]
    public sealed class LccScriptedImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx)
        {
            var absPath  = Path.GetFullPath(ctx.assetPath);
            var rootDir  = Path.GetDirectoryName(absPath);
            if (string.IsNullOrEmpty(rootDir))
            {
                ctx.LogImportError($"LCC: 경로 판별 실패 ({ctx.assetPath})");
                return;
            }

            LccManifest manifest;
            try
            {
                manifest = LccManifest.Parse(File.ReadAllText(absPath));
            }
            catch (System.Exception e)
            {
                ctx.LogImportError($"LCC: 매니페스트 JSON 파싱 실패 — {e.Message}");
                return;
            }

            // attrs.lcp 는 옵션 (없어도 임포트는 계속)
            LccSceneAttrs attrs = null;
            var attrsPath = Path.Combine(rootDir, "attrs.lcp");
            if (File.Exists(attrsPath))
            {
                try { attrs = LccSceneAttrs.Parse(File.ReadAllText(attrsPath)); }
                catch (System.Exception e) { ctx.LogImportWarning($"LCC: attrs.lcp 무시 — {e.Message}"); }
            }

            var scene = ScriptableObject.CreateInstance<LccScene>();
            scene.rootPath = rootDir;
            scene.manifest = manifest;
            scene.attrs    = attrs;
            scene.name     = manifest.name ?? Path.GetFileNameWithoutExtension(absPath);

            ctx.AddObjectToAsset("scene", scene);
            ctx.SetMainObject(scene);

            // TODO: LccSplatDecoder 완성 후 데이터 디코드 → Mesh/Point buffers 생성 → AddObjectToAsset
        }
    }
}
