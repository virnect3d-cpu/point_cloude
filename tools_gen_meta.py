"""Generate .meta files for the com.virnect.lcc UPM package.
UPM git packages need .meta for every asset — Unity won't auto-create in PackageCache.
GUIDs are deterministic (md5 of relative path) so cached PackageCache stays stable.
"""
import hashlib
import os
import sys

ROOT = os.path.join(os.path.dirname(__file__), "Packages", "com.virnect.lcc")
ROOT = os.path.abspath(ROOT)


def guid_for(path: str) -> str:
    rel = os.path.relpath(path, ROOT).replace(os.sep, "/")
    return hashlib.md5(("virnect.lcc/" + rel).encode()).hexdigest()


def write_meta(path: str, body: str) -> bool:
    meta = path + ".meta"
    if os.path.exists(meta):
        return False
    with open(meta, "w", encoding="utf-8", newline="\n") as f:
        f.write(body)
    return True


def cs_meta(p):
    return (
        "fileFormatVersion: 2\n"
        f"guid: {guid_for(p)}\n"
        "MonoImporter:\n"
        "  externalObjects: {}\n"
        "  serializedVersion: 2\n"
        "  defaultReferences: []\n"
        "  executionOrder: 0\n"
        "  icon: {instanceID: 0}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    )


def asmdef_meta(p):
    return (
        "fileFormatVersion: 2\n"
        f"guid: {guid_for(p)}\n"
        "AssemblyDefinitionImporter:\n"
        "  externalObjects: {}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    )


def folder_meta(p):
    return (
        "fileFormatVersion: 2\n"
        f"guid: {guid_for(p)}\n"
        "folderAsset: yes\n"
        "DefaultImporter:\n"
        "  externalObjects: {}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    )


def shader_meta(p):
    return (
        "fileFormatVersion: 2\n"
        f"guid: {guid_for(p)}\n"
        "ShaderImporter:\n"
        "  externalObjects: {}\n"
        "  defaultTextures: []\n"
        "  nonModifiableTextures: []\n"
        "  preprocessorOverride: 0\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    )


def text_meta(p):
    return (
        "fileFormatVersion: 2\n"
        f"guid: {guid_for(p)}\n"
        "TextScriptImporter:\n"
        "  externalObjects: {}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    )


def main():
    created = []
    for dirpath, dirnames, filenames in os.walk(ROOT):
        # Skip tilde-suffixed directories (Samples~) — Unity excludes them from import
        dirnames[:] = [d for d in dirnames if not d.endswith("~")]
        if os.path.abspath(dirpath) != ROOT:
            if write_meta(dirpath, folder_meta(dirpath)):
                created.append(dirpath + ".meta")
        for f in filenames:
            if f.endswith(".meta"):
                continue
            p = os.path.join(dirpath, f)
            ext = f.rsplit(".", 1)[-1].lower() if "." in f else ""
            if ext == "cs":
                body = cs_meta(p)
            elif ext == "asmdef":
                body = asmdef_meta(p)
            elif ext == "shader":
                body = shader_meta(p)
            elif ext in ("json", "md", "txt", "asmref"):
                body = text_meta(p)
            else:
                body = text_meta(p)
            if write_meta(p, body):
                created.append(p + ".meta")

    print(f"created {len(created)} meta files", file=sys.stderr)
    for c in created:
        print(" ", os.path.relpath(c, ROOT))


if __name__ == "__main__":
    main()
