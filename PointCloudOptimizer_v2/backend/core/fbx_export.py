"""
FBX ASCII 7.4.0 writer (minimal) — Maya/Unity 호환.

지원:
  - 메쉬 지오메트리 (v, f, n)
  - per-vertex 색 (LayerElementColor)
  - UV 좌표 (LayerElementUV)
  - 다중 머티리얼 (face별 material id)
  - Lambert 머티리얼 (Kd 디퓨즈)

미지원 (향후):
  - 텍스처 파일 embed (map_Kd 경로만 문자열로 들어감)
  - 애니메이션·스켈레톤
"""
from __future__ import annotations

import datetime
import time
from typing import List, Optional, Sequence, Tuple

import numpy as np


# ── UID 관리 ─────────────────────────────────────────────────────────────
_UID_SEED = 1000


def _uid() -> int:
    global _UID_SEED
    _UID_SEED += 1
    return _UID_SEED


def _reset_uid():
    global _UID_SEED
    _UID_SEED = 1000


def _fmt_vec3(v) -> str:
    return f"{float(v[0]):.6f},{float(v[1]):.6f},{float(v[2]):.6f}"


def _a_list(values, per_line: int = 12) -> str:
    """FBX 배열: a: v1,v2,... (줄바꿈 처리)"""
    if hasattr(values, "tolist"):
        values = values.tolist()
    out = []
    for i, v in enumerate(values):
        out.append(str(v))
    # 긴 배열은 한 줄로도 OK — Maya/Unity 둘 다 파싱함
    return ",".join(out)


def _build_geometry(
    verts: np.ndarray,
    faces: np.ndarray,
    uvs: Optional[np.ndarray] = None,
    normals: Optional[np.ndarray] = None,
    vertex_colors: Optional[np.ndarray] = None,
    face_mat_ids: Optional[np.ndarray] = None,
    material_count: int = 1,
) -> Tuple[str, int]:
    """Geometry 노드 블록을 만들고 (text, uid) 반환."""
    geo_uid = _uid()

    V = np.asarray(verts, dtype=np.float64)
    F = np.asarray(faces, dtype=np.int64)
    nV, nF = len(V), len(F)

    # Vertices: flat list
    v_flat = V.reshape(-1).tolist()

    # PolygonVertexIndex: 각 polygon의 마지막 index는 ~x (bitwise NOT) = -(x+1)
    poly_idx = []
    for f in F:
        for k, vi in enumerate(f):
            if k == len(f) - 1:
                poly_idx.append(-int(vi) - 1)  # end-of-polygon marker
            else:
                poly_idx.append(int(vi))

    lines = []
    lines.append(f'\tGeometry: {geo_uid}, "Geometry::", "Mesh" {{')
    lines.append(f'\t\tVertices: *{nV*3} {{')
    lines.append(f'\t\t\ta: {_a_list(v_flat)}')
    lines.append('\t\t}')
    lines.append(f'\t\tPolygonVertexIndex: *{len(poly_idx)} {{')
    lines.append(f'\t\t\ta: {_a_list(poly_idx)}')
    lines.append('\t\t}')
    lines.append('\t\tGeometryVersion: 124')

    # Normals (LayerElementNormal) — per-vertex (ByVertice)
    if normals is not None and len(normals) == nV:
        n_flat = np.asarray(normals, dtype=np.float64).reshape(-1).tolist()
        lines.append('\t\tLayerElementNormal: 0 {')
        lines.append('\t\t\tVersion: 101')
        lines.append('\t\t\tName: ""')
        lines.append('\t\t\tMappingInformationType: "ByVertice"')
        lines.append('\t\t\tReferenceInformationType: "Direct"')
        lines.append(f'\t\t\tNormals: *{nV*3} {{')
        lines.append(f'\t\t\t\ta: {_a_list(n_flat)}')
        lines.append('\t\t\t}')
        lines.append('\t\t}')

    # Vertex colors (LayerElementColor) — per-vertex RGBA
    if vertex_colors is not None and len(vertex_colors) == nV:
        col = np.asarray(vertex_colors, dtype=np.float64)
        if col.shape[1] == 3:
            alpha = np.ones((nV, 1))
            col = np.hstack([col, alpha])
        c_flat = col.reshape(-1).tolist()
        lines.append('\t\tLayerElementColor: 0 {')
        lines.append('\t\t\tVersion: 101')
        lines.append('\t\t\tName: "colorSet1"')
        lines.append('\t\t\tMappingInformationType: "ByVertice"')
        lines.append('\t\t\tReferenceInformationType: "Direct"')
        lines.append(f'\t\t\tColors: *{nV*4} {{')
        lines.append(f'\t\t\t\ta: {_a_list(c_flat)}')
        lines.append('\t\t\t}')
        lines.append('\t\t}')

    # UVs
    if uvs is not None and len(uvs) == nV:
        uv_flat = np.asarray(uvs, dtype=np.float64).reshape(-1).tolist()
        # UVIndex: per polygon-vertex → 전체 poly_idx 순서대로 vertex 인덱스
        uv_idx = []
        for f in F:
            for vi in f:
                uv_idx.append(int(vi))
        lines.append('\t\tLayerElementUV: 0 {')
        lines.append('\t\t\tVersion: 101')
        lines.append('\t\t\tName: "map1"')
        lines.append('\t\t\tMappingInformationType: "ByPolygonVertex"')
        lines.append('\t\t\tReferenceInformationType: "IndexToDirect"')
        lines.append(f'\t\t\tUV: *{nV*2} {{')
        lines.append(f'\t\t\t\ta: {_a_list(uv_flat)}')
        lines.append('\t\t\t}')
        lines.append(f'\t\t\tUVIndex: *{len(uv_idx)} {{')
        lines.append(f'\t\t\t\ta: {_a_list(uv_idx)}')
        lines.append('\t\t\t}')
        lines.append('\t\t}')

    # Materials (face mat id 있으면 per-face, 없으면 AllSame)
    if face_mat_ids is not None and len(face_mat_ids) == nF and material_count > 1:
        lines.append('\t\tLayerElementMaterial: 0 {')
        lines.append('\t\t\tVersion: 101')
        lines.append('\t\t\tName: ""')
        lines.append('\t\t\tMappingInformationType: "ByPolygon"')
        lines.append('\t\t\tReferenceInformationType: "IndexToDirect"')
        lines.append(f'\t\t\tMaterials: *{nF} {{')
        lines.append(f'\t\t\t\ta: {_a_list(face_mat_ids.tolist() if hasattr(face_mat_ids,"tolist") else list(face_mat_ids))}')
        lines.append('\t\t\t}')
        lines.append('\t\t}')
    else:
        lines.append('\t\tLayerElementMaterial: 0 {')
        lines.append('\t\t\tVersion: 101')
        lines.append('\t\t\tName: ""')
        lines.append('\t\t\tMappingInformationType: "AllSame"')
        lines.append('\t\t\tReferenceInformationType: "IndexToDirect"')
        lines.append('\t\t\tMaterials: *1 { a: 0 }')
        lines.append('\t\t}')

    # Layer (어떤 element가 활성화되어있는지)
    lines.append('\t\tLayer: 0 {')
    lines.append('\t\t\tVersion: 100')
    if normals is not None and len(normals) == nV:
        lines.append('\t\t\tLayerElement:  {')
        lines.append('\t\t\t\tType: "LayerElementNormal"')
        lines.append('\t\t\t\tTypedIndex: 0')
        lines.append('\t\t\t}')
    if vertex_colors is not None and len(vertex_colors) == nV:
        lines.append('\t\t\tLayerElement:  {')
        lines.append('\t\t\t\tType: "LayerElementColor"')
        lines.append('\t\t\t\tTypedIndex: 0')
        lines.append('\t\t\t}')
    if uvs is not None and len(uvs) == nV:
        lines.append('\t\t\tLayerElement:  {')
        lines.append('\t\t\t\tType: "LayerElementUV"')
        lines.append('\t\t\t\tTypedIndex: 0')
        lines.append('\t\t\t}')
    lines.append('\t\t\tLayerElement:  {')
    lines.append('\t\t\t\tType: "LayerElementMaterial"')
    lines.append('\t\t\t\tTypedIndex: 0')
    lines.append('\t\t\t}')
    lines.append('\t\t}')
    lines.append('\t}')
    return "\n".join(lines), geo_uid


def _build_model(name: str = "Mesh") -> Tuple[str, int]:
    model_uid = _uid()
    lines = []
    lines.append(f'\tModel: {model_uid}, "Model::{name}", "Mesh" {{')
    lines.append('\t\tVersion: 232')
    lines.append('\t\tProperties70:  {')
    lines.append('\t\t\tP: "RotationActive", "bool", "", "",1')
    lines.append('\t\t\tP: "InheritType", "enum", "", "",1')
    lines.append('\t\t\tP: "ScalingMax", "Vector3D", "Vector", "",0,0,0')
    lines.append('\t\t\tP: "DefaultAttributeIndex", "int", "Integer", "",0')
    lines.append('\t\t\tP: "Lcl Translation", "Lcl Translation", "", "A",0,0,0')
    lines.append('\t\t\tP: "Lcl Rotation", "Lcl Rotation", "", "A",0,0,0')
    lines.append('\t\t\tP: "Lcl Scaling", "Lcl Scaling", "", "A",1,1,1')
    lines.append('\t\t}')
    lines.append('\t\tShading: T')
    lines.append('\t\tCulling: "CullingOff"')
    lines.append('\t}')
    return "\n".join(lines), model_uid


def _build_material(name: str, color: Tuple[float, float, float]) -> Tuple[str, int]:
    mat_uid = _uid()
    r, g, b = float(color[0]), float(color[1]), float(color[2])
    lines = []
    lines.append(f'\tMaterial: {mat_uid}, "Material::{name}", "" {{')
    lines.append('\t\tVersion: 102')
    lines.append('\t\tShadingModel: "lambert"')
    lines.append('\t\tMultiLayer: 0')
    lines.append('\t\tProperties70:  {')
    lines.append('\t\t\tP: "ShadingModel", "KString", "", "", "lambert"')
    lines.append(f'\t\t\tP: "DiffuseColor", "Color", "", "A",{r},{g},{b}')
    lines.append(f'\t\t\tP: "Diffuse", "Vector3D", "Vector", "",{r},{g},{b}')
    lines.append('\t\t\tP: "AmbientColor", "Color", "", "A",0.1,0.1,0.1')
    lines.append('\t\t\tP: "Emissive", "Vector3D", "Vector", "",0,0,0')
    lines.append('\t\t\tP: "Opacity", "double", "Number", "",1')
    lines.append('\t\t}')
    lines.append('\t}')
    return "\n".join(lines), mat_uid


def export_fbx_ascii(
    verts: np.ndarray,
    faces: np.ndarray,                   # (F, 3) or (F, 4) — quad도 OK
    *,
    uvs: Optional[np.ndarray] = None,
    normals: Optional[np.ndarray] = None,
    vertex_colors: Optional[np.ndarray] = None,   # (V, 3) float 0~1
    face_mat_ids: Optional[np.ndarray] = None,    # (F,) int
    materials: Optional[Sequence[Tuple[str, Tuple[float, float, float]]]] = None,
    # quad/tri 혼합 지원 — faces 리스트가 두 종류면 quads + tris로 받기
    quads: Optional[Sequence[Sequence[int]]] = None,
    tris:  Optional[Sequence[Sequence[int]]] = None,
    quads_mat: Optional[Sequence[int]] = None,
    tris_mat:  Optional[Sequence[int]] = None,
) -> str:
    """
    ASCII FBX 7.4.0 텍스트 반환. Maya/Unity 에서 직접 import 가능.

    quads+tris 모드면 faces는 무시됨.
    material_count = len(materials). materials 없으면 기본 1개 생성.
    """
    _reset_uid()

    # faces 구성
    if quads is not None or tris is not None:
        # 혼합 모드: 폴리곤 리스트 구성 (각 polygon은 가변길이)
        all_faces: List[List[int]] = []
        all_mat: List[int] = []
        if quads:
            for i, q in enumerate(quads):
                all_faces.append(list(q))
                all_mat.append(int(quads_mat[i]) if quads_mat else 0)
        if tris:
            for i, t in enumerate(tris):
                all_faces.append(list(t))
                all_mat.append(int(tris_mat[i]) if tris_mat else 0)
        # Geometry 직접 구성 (가변 length polygon)
        F_array = all_faces
        face_mat = np.asarray(all_mat, dtype=np.int32) if all_mat else None
    else:
        F_array = np.asarray(faces, dtype=np.int64).tolist()
        face_mat = face_mat_ids

    # 재료 기본값
    if not materials:
        materials = [("lambert1", (0.8, 0.8, 0.8))]
    mat_count = len(materials)

    # 지오메트리 문자열
    # _build_geometry 는 np.ndarray faces 받도록 작성됐으니 List도 처리 가능하게 얼른 수정
    def _build_geo_with_list():
        geo_uid = _uid()
        V = np.asarray(verts, dtype=np.float64)
        nV = len(V)
        v_flat = V.reshape(-1).tolist()

        poly_idx = []
        for f in F_array:
            L = len(f)
            for k, vi in enumerate(f):
                if k == L - 1:
                    poly_idx.append(-int(vi) - 1)
                else:
                    poly_idx.append(int(vi))

        lines = []
        lines.append(f'\tGeometry: {geo_uid}, "Geometry::", "Mesh" {{')
        lines.append(f'\t\tVertices: *{nV*3} {{')
        lines.append(f'\t\t\ta: {_a_list(v_flat)}')
        lines.append('\t\t}')
        lines.append(f'\t\tPolygonVertexIndex: *{len(poly_idx)} {{')
        lines.append(f'\t\t\ta: {_a_list(poly_idx)}')
        lines.append('\t\t}')
        lines.append('\t\tGeometryVersion: 124')

        if normals is not None and len(normals) == nV:
            n_flat = np.asarray(normals, dtype=np.float64).reshape(-1).tolist()
            lines += [
                '\t\tLayerElementNormal: 0 {',
                '\t\t\tVersion: 101',
                '\t\t\tName: ""',
                '\t\t\tMappingInformationType: "ByVertice"',
                '\t\t\tReferenceInformationType: "Direct"',
                f'\t\t\tNormals: *{nV*3} {{', f'\t\t\t\ta: {_a_list(n_flat)}', '\t\t\t}',
                '\t\t}',
            ]

        if vertex_colors is not None and len(vertex_colors) == nV:
            col = np.asarray(vertex_colors, dtype=np.float64)
            if col.shape[1] == 3:
                col = np.hstack([col, np.ones((nV, 1))])
            c_flat = col.reshape(-1).tolist()
            lines += [
                '\t\tLayerElementColor: 0 {',
                '\t\t\tVersion: 101',
                '\t\t\tName: "colorSet1"',
                '\t\t\tMappingInformationType: "ByVertice"',
                '\t\t\tReferenceInformationType: "Direct"',
                f'\t\t\tColors: *{nV*4} {{', f'\t\t\t\ta: {_a_list(c_flat)}', '\t\t\t}',
                '\t\t}',
            ]

        if uvs is not None and len(uvs) == nV:
            uv_flat = np.asarray(uvs, dtype=np.float64).reshape(-1).tolist()
            uv_idx = []
            for f in F_array:
                for vi in f:
                    uv_idx.append(int(vi))
            lines += [
                '\t\tLayerElementUV: 0 {',
                '\t\t\tVersion: 101',
                '\t\t\tName: "map1"',
                '\t\t\tMappingInformationType: "ByPolygonVertex"',
                '\t\t\tReferenceInformationType: "IndexToDirect"',
                f'\t\t\tUV: *{nV*2} {{', f'\t\t\t\ta: {_a_list(uv_flat)}', '\t\t\t}',
                f'\t\t\tUVIndex: *{len(uv_idx)} {{', f'\t\t\t\ta: {_a_list(uv_idx)}', '\t\t\t}',
                '\t\t}',
            ]

        if face_mat is not None and len(face_mat) == len(F_array) and mat_count > 1:
            lines += [
                '\t\tLayerElementMaterial: 0 {',
                '\t\t\tVersion: 101',
                '\t\t\tName: ""',
                '\t\t\tMappingInformationType: "ByPolygon"',
                '\t\t\tReferenceInformationType: "IndexToDirect"',
                f'\t\t\tMaterials: *{len(face_mat)} {{',
                f'\t\t\t\ta: {_a_list(face_mat.tolist() if hasattr(face_mat,"tolist") else list(face_mat))}',
                '\t\t\t}',
                '\t\t}',
            ]
        else:
            lines += [
                '\t\tLayerElementMaterial: 0 {',
                '\t\t\tVersion: 101',
                '\t\t\tName: ""',
                '\t\t\tMappingInformationType: "AllSame"',
                '\t\t\tReferenceInformationType: "IndexToDirect"',
                '\t\t\tMaterials: *1 { a: 0 }',
                '\t\t}',
            ]

        lines.append('\t\tLayer: 0 {')
        lines.append('\t\t\tVersion: 100')
        if normals is not None and len(normals) == nV:
            lines += ['\t\t\tLayerElement:  {', '\t\t\t\tType: "LayerElementNormal"', '\t\t\t\tTypedIndex: 0', '\t\t\t}']
        if vertex_colors is not None and len(vertex_colors) == nV:
            lines += ['\t\t\tLayerElement:  {', '\t\t\t\tType: "LayerElementColor"', '\t\t\t\tTypedIndex: 0', '\t\t\t}']
        if uvs is not None and len(uvs) == nV:
            lines += ['\t\t\tLayerElement:  {', '\t\t\t\tType: "LayerElementUV"', '\t\t\t\tTypedIndex: 0', '\t\t\t}']
        lines += ['\t\t\tLayerElement:  {', '\t\t\t\tType: "LayerElementMaterial"', '\t\t\t\tTypedIndex: 0', '\t\t\t}']
        lines.append('\t\t}')
        lines.append('\t}')
        return "\n".join(lines), geo_uid

    geo_text, geo_uid = _build_geo_with_list()
    model_text, model_uid = _build_model("PointCloudMesh")

    # Materials
    mat_blocks = []
    mat_uids = []
    for name, col in materials:
        txt, uid = _build_material(name, col)
        mat_blocks.append(txt)
        mat_uids.append(uid)

    # Header
    dt = datetime.datetime.now()
    header = [
        '; FBX 7.4.0 project file',
        '; Generated by PointCloud Optimizer',
        ';',
        '',
        'FBXHeaderExtension:  {',
        '\tFBXHeaderVersion: 1003',
        '\tFBXVersion: 7400',
        f'\tCreationTimeStamp:  {{ Version: 1000 Year: {dt.year} Month: {dt.month} Day: {dt.day} Hour: {dt.hour} Minute: {dt.minute} Second: {dt.second} Millisecond: 0 }}',
        '\tCreator: "PointCloud Optimizer"',
        '}',
        'GlobalSettings:  {',
        '\tVersion: 1000',
        '\tProperties70:  {',
        '\t\tP: "UpAxisSign", "int", "Integer", "",1',
        '\t\tP: "UpAxis", "int", "Integer", "",1',
        '\t\tP: "FrontAxis", "int", "Integer", "",2',
        '\t\tP: "CoordAxis", "int", "Integer", "",0',
        '\t\tP: "UnitScaleFactor", "double", "Number", "",1',
        '\t\tP: "OriginalUnitScaleFactor", "double", "Number", "",1',
        '\t}',
        '}',
    ]

    # Definitions
    definitions = [
        'Definitions:  {',
        '\tVersion: 100',
        f'\tCount: {2 + mat_count}',
        '\tObjectType: "GlobalSettings" { Count: 1 }',
        '\tObjectType: "Geometry" {',
        '\t\tCount: 1',
        '\t\tPropertyTemplate: "FbxMesh" {  }',
        '\t}',
        '\tObjectType: "Model" {',
        '\t\tCount: 1',
        '\t\tPropertyTemplate: "FbxNode" {  }',
        '\t}',
        '\tObjectType: "Material" {',
        f'\t\tCount: {mat_count}',
        '\t\tPropertyTemplate: "FbxSurfaceLambert" {  }',
        '\t}',
        '}',
    ]

    # Objects
    objects = ['Objects:  {']
    objects.append(geo_text)
    objects.append(model_text)
    objects.extend(mat_blocks)
    objects.append('}')

    # Connections
    connections = ['Connections:  {']
    # Model → root scene
    connections.append(f'\t;Model::PointCloudMesh, Model::RootNode')
    connections.append(f'\tC: "OO",{model_uid},0')
    # Geometry → Model
    connections.append(f'\t;Geometry::, Model::PointCloudMesh')
    connections.append(f'\tC: "OO",{geo_uid},{model_uid}')
    # Materials → Model
    for mu, (name, _) in zip(mat_uids, materials):
        connections.append(f'\t;Material::{name}, Model::PointCloudMesh')
        connections.append(f'\tC: "OO",{mu},{model_uid}')
    connections.append('}')

    # Takes (빈)
    takes = ['Takes:  {', '\tCurrent: ""', '}']

    return "\n".join(header + definitions + objects + connections + takes) + "\n"
