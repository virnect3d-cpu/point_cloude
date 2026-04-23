"""Unity .unitypackage 빌더 (Python 전용, Unity 불필요).

포맷:
  - gzip tar archive
  - 각 에셋: {GUID}/{asset, asset.meta, pathname}
  - GUID는 32 hex 소문자 (대시 없음)

출력 에셋:
  Assets/PointCloudOptimizer/Scene.ply                 (원본 포인트 클라우드)
  Assets/PointCloudOptimizer/Colliders.json            (콜라이더 JSON, TextAsset)
  Assets/PointCloudOptimizer/ColliderRebuilder.cs      (MonoBehaviour)
  Assets/PointCloudOptimizer/SceneColliders.prefab     (ColliderRebuilder + JSON ref)
  Assets/PointCloudOptimizer/README.md                 (안내)

ColliderRebuilder.cs가 Awake에서 JSON을 읽어 Box/Mesh Collider를 재구성.
— Unity JsonUtility 친화적 flat 스키마로 변환 후 저장.

참고 구현:
  - AgeOfLearning/upackage (Python 참고)
  - FatihBAKIR/UnityPacker (C# 포맷 참고)
"""
from __future__ import annotations

import io
import json
import tarfile
import time
import uuid
from typing import Any, Iterable


# ─────────────────────────────────────────────────────────────────────────────
# GUID / YAML 템플릿
# ─────────────────────────────────────────────────────────────────────────────
def _new_guid() -> str:
    """Unity GUID: 32-char lowercase hex."""
    return uuid.uuid4().hex


def _meta_default_importer(guid: str) -> str:
    """.ply 등 Unity 미지원 확장자용 — 바이너리 raw 에셋."""
    return (
        "fileFormatVersion: 2\n"
        f"guid: {guid}\n"
        "DefaultImporter:\n"
        "  externalObjects: {}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    )


def _meta_text_script(guid: str) -> str:
    """.json / .txt → TextAsset. mainObjectFileID: 4900000 ."""
    return (
        "fileFormatVersion: 2\n"
        f"guid: {guid}\n"
        "TextScriptImporter:\n"
        "  externalObjects: {}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    )


def _meta_mono_script(guid: str) -> str:
    """.cs → MonoScript. mainObjectFileID: 11500000 ."""
    return (
        "fileFormatVersion: 2\n"
        f"guid: {guid}\n"
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


def _meta_prefab(guid: str) -> str:
    """.prefab → PrefabImporter."""
    return (
        "fileFormatVersion: 2\n"
        f"guid: {guid}\n"
        "PrefabImporter:\n"
        "  externalObjects: {}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    )


# ─────────────────────────────────────────────────────────────────────────────
# 콜라이더 JSON → JsonUtility 친화 flat 스키마로 변환
# ─────────────────────────────────────────────────────────────────────────────
def _flatten_collider_json(raw_payload: dict) -> dict:
    """기존 JSON 스키마 → Unity JsonUtility 호환 flat 스키마.

    JsonUtility는 다형 타입/잭드 배열 미지원 → 단일 Item 타입으로 통합.
    - box:  center/size 사용, verticesFlat/trianglesFlat 빈 배열
    - mesh: verticesFlat/trianglesFlat 사용, center/size 0
    """
    out_colliders = []
    for c in raw_payload.get("colliders", []):
        ctype = c.get("type", "box")
        item = {
            "type": ctype,
            "name": c.get("name") or f"{ctype.capitalize()}Collider",
            "center": {"x": 0.0, "y": 0.0, "z": 0.0},
            "size":   {"x": 0.0, "y": 0.0, "z": 0.0},
            "verticesFlat":  [],
            "trianglesFlat": [],
        }
        if ctype == "box":
            cc = c.get("center") or {}
            cs = c.get("size") or {}
            item["center"] = {
                "x": float(cc.get("x", 0)), "y": float(cc.get("y", 0)), "z": float(cc.get("z", 0)),
            }
            item["size"] = {
                "x": float(cs.get("x", 0)), "y": float(cs.get("y", 0)), "z": float(cs.get("z", 0)),
            }
        else:  # mesh / convex_part / convex_mesh
            # vertices: [{x,y,z},...] or [[x,y,z],...]
            vs = c.get("vertices") or []
            flat_v = []
            for v in vs:
                if isinstance(v, dict):
                    flat_v.extend([float(v.get("x", 0)), float(v.get("y", 0)), float(v.get("z", 0))])
                else:
                    flat_v.extend([float(v[0]), float(v[1]), float(v[2])])
            item["verticesFlat"] = flat_v
            # triangles: [[a,b,c],...]
            tris = c.get("triangles") or []
            flat_t = []
            for t in tris:
                flat_t.extend([int(t[0]), int(t[1]), int(t[2])])
            item["trianglesFlat"] = flat_t
        out_colliders.append(item)

    return {
        "version": "unitypkg-1.0",
        "generated": raw_payload.get("generated", ""),
        "pointCount": int(raw_payload.get("pointCount", 0)),
        "plyFile": raw_payload.get("plyFile", ""),
        "colliders": out_colliders,
    }


# ─────────────────────────────────────────────────────────────────────────────
# C# 스크립트 (ColliderRebuilder.cs)
# ─────────────────────────────────────────────────────────────────────────────
_CS_SCRIPT = r"""// Auto-generated by PointCloudOptimizer
// Rebuilds BoxCollider / MeshCollider from JSON on Awake.
using UnityEngine;
using System.Collections.Generic;

namespace PointCloudOptimizer {

[System.Serializable]
public class ColliderV3 { public float x, y, z; }

[System.Serializable]
public class ColliderItem {
    public string type;               // "box" | "mesh" | "convex_mesh" | "convex_part"
    public string name;
    public ColliderV3 center;
    public ColliderV3 size;
    public float[] verticesFlat;      // mesh: xyz xyz xyz ...
    public int[]   trianglesFlat;     // mesh: abc abc abc ...
}

[System.Serializable]
public class ColliderContainer {
    public string version;
    public string generated;
    public int pointCount;
    public string plyFile;
    public ColliderItem[] colliders;
}

[AddComponentMenu("PointCloudOptimizer/Collider Rebuilder")]
[ExecuteAlways]
public class ColliderRebuilder : MonoBehaviour {
    [Tooltip("Colliders.json (TextAsset)")]
    public TextAsset colliderJson;
    [Tooltip("원본 포인트 클라우드 (참고용; 유니티는 .ply 기본 임포트 안 함 → keijiro/Pcx 권장)")]
    public Object pointCloudPly;
    [Tooltip("Awake 시 자식에 콜라이더가 없으면 자동 생성")]
    public bool autoRebuildOnAwake = true;
    [Tooltip("MeshCollider 볼록체 모드 (동적 리지드바디 용)")]
    public bool meshCollidersConvex = false;

    void Awake() {
        if (autoRebuildOnAwake && transform.childCount == 0) Rebuild();
    }

    [ContextMenu("Rebuild Colliders")]
    public void Rebuild() {
        if (colliderJson == null) {
            Debug.LogWarning("ColliderRebuilder: colliderJson이 비어있습니다.");
            return;
        }
        ColliderContainer data = null;
        try { data = JsonUtility.FromJson<ColliderContainer>(colliderJson.text); }
        catch (System.Exception e) { Debug.LogError("Collider JSON 파싱 실패: " + e); return; }
        if (data == null || data.colliders == null) return;

        // 기존 자식 제거 (in-editor 안전)
        var toKill = new List<GameObject>();
        foreach (Transform t in transform) toKill.Add(t.gameObject);
        foreach (var g in toKill) {
#if UNITY_EDITOR
            if (!Application.isPlaying) DestroyImmediate(g); else Destroy(g);
#else
            Destroy(g);
#endif
        }

        foreach (var c in data.colliders) {
            var go = new GameObject(string.IsNullOrEmpty(c.name) ? (c.type + "Collider") : c.name);
            go.transform.SetParent(transform, false);

            if (c.type == "box") {
                if (c.center != null) go.transform.localPosition = new Vector3(c.center.x, c.center.y, c.center.z);
                var bc = go.AddComponent<BoxCollider>();
                if (c.size != null) bc.size = new Vector3(c.size.x, c.size.y, c.size.z);
            } else {
                // mesh / convex_mesh / convex_part
                if (c.verticesFlat == null || c.trianglesFlat == null
                    || c.verticesFlat.Length < 9 || c.trianglesFlat.Length < 3) {
                    Debug.LogWarning("MeshCollider 데이터 부족: " + c.name);
                    continue;
                }
                var verts = new Vector3[c.verticesFlat.Length / 3];
                for (int i = 0; i < verts.Length; i++) {
                    verts[i] = new Vector3(c.verticesFlat[i*3], c.verticesFlat[i*3+1], c.verticesFlat[i*3+2]);
                }
                var mesh = new Mesh();
                if (verts.Length > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.vertices  = verts;
                mesh.triangles = c.trianglesFlat;
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                mesh.name = c.name;

                var mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = mesh;
                mc.convex = meshCollidersConvex || c.type == "convex_part" || c.type == "convex_mesh";
            }
        }
        Debug.Log("ColliderRebuilder: " + data.colliders.Length + "개 콜라이더 생성 완료");
    }
}

} // namespace
"""


# ─────────────────────────────────────────────────────────────────────────────
# Prefab YAML
# ─────────────────────────────────────────────────────────────────────────────
def _make_prefab_yaml(script_guid: str, json_guid: str, ply_guid: str) -> str:
    """SceneColliders.prefab — Root GameObject + Transform + ColliderRebuilder."""
    return (
        "%YAML 1.1\n"
        "%TAG !u! tag:unity3d.com,2011:\n"
        "--- !u!1 &1\n"
        "GameObject:\n"
        "  m_ObjectHideFlags: 0\n"
        "  m_CorrespondingSourceObject: {fileID: 0}\n"
        "  m_PrefabInstance: {fileID: 0}\n"
        "  m_PrefabAsset: {fileID: 0}\n"
        "  serializedVersion: 6\n"
        "  m_Component:\n"
        "  - component: {fileID: 2}\n"
        "  - component: {fileID: 3}\n"
        "  m_Layer: 0\n"
        "  m_Name: SceneColliders\n"
        "  m_TagString: Untagged\n"
        "  m_Icon: {fileID: 0}\n"
        "  m_NavMeshLayer: 0\n"
        "  m_StaticEditorFlags: 0\n"
        "  m_IsActive: 1\n"
        "--- !u!4 &2\n"
        "Transform:\n"
        "  m_ObjectHideFlags: 0\n"
        "  m_CorrespondingSourceObject: {fileID: 0}\n"
        "  m_PrefabInstance: {fileID: 0}\n"
        "  m_PrefabAsset: {fileID: 0}\n"
        "  m_GameObject: {fileID: 1}\n"
        "  serializedVersion: 2\n"
        "  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}\n"
        "  m_LocalPosition: {x: 0, y: 0, z: 0}\n"
        "  m_LocalScale: {x: 1, y: 1, z: 1}\n"
        "  m_ConstrainProportionsScale: 0\n"
        "  m_Children: []\n"
        "  m_Father: {fileID: 0}\n"
        "  m_LocalEulerAnglesHint: {x: 0, y: 0, z: 0}\n"
        "--- !u!114 &3\n"
        "MonoBehaviour:\n"
        "  m_ObjectHideFlags: 0\n"
        "  m_CorrespondingSourceObject: {fileID: 0}\n"
        "  m_PrefabInstance: {fileID: 0}\n"
        "  m_PrefabAsset: {fileID: 0}\n"
        "  m_GameObject: {fileID: 1}\n"
        "  m_Enabled: 1\n"
        "  m_EditorHideFlags: 0\n"
        f"  m_Script: {{fileID: 11500000, guid: {script_guid}, type: 3}}\n"
        "  m_Name: \n"
        "  m_EditorClassIdentifier: \n"
        f"  colliderJson: {{fileID: 4900000, guid: {json_guid}, type: 3}}\n"
        f"  pointCloudPly: {{fileID: 4900000, guid: {ply_guid}, type: 3}}\n"
        "  autoRebuildOnAwake: 1\n"
        "  meshCollidersConvex: 0\n"
    )


_README = """# PointCloudOptimizer 콜라이더 패키지

**자동 생성** · 이 폴더를 Unity 프로젝트의 `Assets/` 아래에 임포트하면 바로 사용 가능.

## 포함 파일
- `Scene.ply` — 원본 포인트 클라우드 (표시용; [keijiro/Pcx](https://github.com/keijiro/Pcx) 권장)
- `Colliders.json` — 콜라이더 데이터 (TextAsset)
- `ColliderRebuilder.cs` — JSON → Collider 재구성 스크립트
- `SceneColliders.prefab` — ColliderRebuilder가 장착된 프리팹
- `README.md` — 이 문서

## 사용법
1. 씬에 `SceneColliders.prefab` 드롭
2. Play 또는 우클릭 → "Rebuild Colliders"로 재구성
3. Box / Mesh 콜라이더가 자식 GameObject로 생성됨

## 커스터마이즈
- `meshCollidersConvex` — 동적 리지드바디용 볼록 메쉬 콜라이더
- `autoRebuildOnAwake` — 앱 실행 시 자동 재구성
"""


# ─────────────────────────────────────────────────────────────────────────────
# tar.gz 패커
# ─────────────────────────────────────────────────────────────────────────────
def _add_asset(tf: tarfile.TarFile, guid: str, pathname: str,
               asset_bytes: bytes, meta_text: str, mtime: float) -> None:
    """단일 에셋 추가: {guid}/{asset, asset.meta, pathname}."""
    def _add(name: str, data: bytes) -> None:
        info = tarfile.TarInfo(name=f"{guid}/{name}")
        info.size = len(data)
        info.mtime = int(mtime)
        info.mode = 0o644
        tf.addfile(info, io.BytesIO(data))

    if asset_bytes:
        _add("asset", asset_bytes)
    _add("asset.meta", meta_text.encode("utf-8"))
    _add("pathname", pathname.encode("utf-8"))


def build_unity_package(
    *,
    ply_name: str,
    ply_bytes: bytes,
    colliders_json_text: str,
    asset_folder: str = "Assets/PointCloudOptimizer",
) -> bytes:
    """콜라이더 JSON(+ 원본 PLY) → .unitypackage 바이트.

    Args:
      ply_name: 저장될 PLY 파일명 (예: "Goat_skull.ply")
      ply_bytes: 원본 PLY 파일 바이트
      colliders_json_text: 기존 JSON 포맷 문자열 (exportColliders 결과)
      asset_folder: Unity 프로젝트 내 저장 경로

    Returns:
      gzip tar archive bytes (.unitypackage)
    """
    raw_payload = json.loads(colliders_json_text)
    flat = _flatten_collider_json(raw_payload)
    flat_json_bytes = json.dumps(flat, ensure_ascii=False, indent=2).encode("utf-8")

    # GUID 생성
    guid_ply    = _new_guid()
    guid_json   = _new_guid()
    guid_script = _new_guid()
    guid_prefab = _new_guid()
    guid_readme = _new_guid()

    prefab_text = _make_prefab_yaml(
        script_guid=guid_script,
        json_guid=guid_json,
        ply_guid=guid_ply,
    )

    mtime = time.time()
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w:gz", format=tarfile.USTAR_FORMAT) as tf:
        folder = asset_folder.rstrip("/")
        # 1) PLY
        _add_asset(
            tf, guid_ply,
            f"{folder}/{ply_name}",
            ply_bytes,
            _meta_default_importer(guid_ply),
            mtime,
        )
        # 2) Colliders.json
        _add_asset(
            tf, guid_json,
            f"{folder}/Colliders.json",
            flat_json_bytes,
            _meta_text_script(guid_json),
            mtime,
        )
        # 3) ColliderRebuilder.cs
        _add_asset(
            tf, guid_script,
            f"{folder}/ColliderRebuilder.cs",
            _CS_SCRIPT.encode("utf-8"),
            _meta_mono_script(guid_script),
            mtime,
        )
        # 4) SceneColliders.prefab
        _add_asset(
            tf, guid_prefab,
            f"{folder}/SceneColliders.prefab",
            prefab_text.encode("utf-8"),
            _meta_prefab(guid_prefab),
            mtime,
        )
        # 5) README.md
        _add_asset(
            tf, guid_readme,
            f"{folder}/README.md",
            _README.encode("utf-8"),
            _meta_default_importer(guid_readme),
            mtime,
        )

    return buf.getvalue()
