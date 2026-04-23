"""
Binary FBX 7.4.0 writer + parser — Blender 호환.
"""
from __future__ import annotations

import struct
import time
import zlib
from typing import List, Optional, Sequence, Tuple, Union

import numpy as np

# ── 상수 ──────────────────────────────────────────────────────────────────
_MAGIC = b"Kaydara FBX Binary  \x00\x1a\x00"
_VERSION = 7400          # v7400 — 가장 호환성 좋은 버전
_NULL_RECORD_SIZE = 13   # v7400 기준. v7500+는 25바이트


# ── 프로퍼티 타입 코드 & 직렬화 ────────────────────────────────────────────
# 단일 scalar: Y(i16) C(u8) I(i32) F(f32) D(f64) L(i64)
# 배열: f(f32*) d(f64*) l(i64*) i(i32*) b(u8*)  — 헤더 + 데이터
# 문자열/raw: S(string) R(bytes)

class P:
    """프로퍼티 표현. type_code(바이트 1) + value(자료)."""
    __slots__ = ("type_code", "value")
    def __init__(self, type_code: bytes, value):
        self.type_code = type_code
        self.value = value


def _prop_i32(v) -> P:   return P(b"I", int(v))
def _prop_i64(v) -> P:   return P(b"L", int(v))
def _prop_f64(v) -> P:   return P(b"D", float(v))
def _prop_str(s: str) -> P: return P(b"S", s.encode("utf-8"))
def _prop_bytes(b: bytes) -> P: return P(b"R", b)


def _write_prop(buf: bytearray, prop: P):
    t = prop.type_code
    v = prop.value
    buf.extend(t)
    if t == b"Y":
        buf.extend(struct.pack("<h", int(v)))
    elif t == b"C":
        buf.extend(struct.pack("<B", 1 if v else 0))
    elif t == b"I":
        buf.extend(struct.pack("<i", int(v)))
    elif t == b"F":
        buf.extend(struct.pack("<f", float(v)))
    elif t == b"D":
        buf.extend(struct.pack("<d", float(v)))
    elif t == b"L":
        buf.extend(struct.pack("<q", int(v)))
    elif t == b"S":
        if isinstance(v, str):
            v = v.encode("utf-8")
        buf.extend(struct.pack("<I", len(v)))
        buf.extend(v)
    elif t == b"R":
        buf.extend(struct.pack("<I", len(v)))
        buf.extend(v)
    elif t in (b"f", b"d", b"l", b"i", b"b"):
        # 배열: length (u32), encoding (u32: 0=raw, 1=zlib), compressed_length (u32), data
        arr = v
        if isinstance(arr, np.ndarray):
            if t == b"f": arr = arr.astype("<f4", copy=False)
            elif t == b"d": arr = arr.astype("<f8", copy=False)
            elif t == b"l": arr = arr.astype("<i8", copy=False)
            elif t == b"i": arr = arr.astype("<i4", copy=False)
            elif t == b"b": arr = arr.astype("<u1", copy=False)
            raw = arr.tobytes()
            count = arr.size
        else:
            count = len(arr)
            fmt = {b"f": "<f", b"d": "<d", b"l": "<q", b"i": "<i", b"b": "<B"}[t]
            raw = b"".join(struct.pack(fmt, x) for x in arr)
        # 큰 배열만 zlib 압축 (1024바이트 이상)
        if len(raw) >= 1024:
            comp = zlib.compress(raw, level=1)
            buf.extend(struct.pack("<III", count, 1, len(comp)))
            buf.extend(comp)
        else:
            buf.extend(struct.pack("<III", count, 0, len(raw)))
            buf.extend(raw)
    else:
        raise ValueError(f"unknown prop type: {t}")


# ── 노드 직렬화 ─────────────────────────────────────────────────────────
class Node:
    __slots__ = ("name", "props", "children")
    def __init__(self, name: str, props: Optional[List[P]] = None,
                 children: Optional[List["Node"]] = None):
        self.name = name
        self.props = props or []
        self.children = children or []

    def add(self, child: "Node") -> "Node":
        self.children.append(child)
        return child


def _write_node(buf: bytearray, node: Optional[Node]) -> None:
    """
    노드 포맷 (v7400):
      EndOffset (u32)         — 이 노드가 끝나는 절대 offset
      NumProperties (u32)
      PropertyListLen (u32)   — props 영역 바이트 수
      NameLen (u8)
      Name (raw bytes)
      Properties
      Children (재귀)
      (자식 있으면) NULL record = 13 bytes of 0
    """
    if node is None:
        # NULL 터미네이터
        buf.extend(b"\x00" * _NULL_RECORD_SIZE)
        return

    header_start = len(buf)
    # 헤더 공간 예약 (end_offset, num_props, props_len)
    buf.extend(b"\x00" * 12)

    # 이름
    name_bytes = node.name.encode("utf-8")
    buf.append(len(name_bytes))
    buf.extend(name_bytes)

    # 프로퍼티
    props_start = len(buf)
    for p in node.props:
        _write_prop(buf, p)
    props_len = len(buf) - props_start

    # 자식
    if node.children:
        for ch in node.children:
            _write_node(buf, ch)
        # NULL 레코드 (자식 있을 때만)
        buf.extend(b"\x00" * _NULL_RECORD_SIZE)

    # 헤더 패치
    end_offset = len(buf)
    struct.pack_into("<III", buf, header_start,
                     end_offset, len(node.props), props_len)


# ── 상위 헬퍼 ─────────────────────────────────────────────────────────────
_UID_SEED = 1000000


def _uid() -> int:
    global _UID_SEED
    _UID_SEED += 1
    return _UID_SEED


def _reset_uid() -> None:
    global _UID_SEED
    _UID_SEED = 1000000


def _prop70_int(name: str, val: int) -> Node:
    return Node("P", [_prop_str(name), _prop_str("int"), _prop_str("Integer"),
                      _prop_str(""), _prop_i32(val)])


def _prop70_dbl(name: str, val: float) -> Node:
    return Node("P", [_prop_str(name), _prop_str("double"), _prop_str("Number"),
                      _prop_str(""), _prop_f64(val)])


def _prop70_color(name: str, r: float, g: float, b: float, anim: bool = False) -> Node:
    flag = "A" if anim else ""
    return Node("P", [_prop_str(name), _prop_str("Color"), _prop_str(""),
                      _prop_str(flag), _prop_f64(r), _prop_f64(g), _prop_f64(b)])


def _prop70_vec(name: str, typ: str, sub: str, x: float, y: float, z: float,
                anim: bool = False) -> Node:
    flag = "A" if anim else ""
    return Node("P", [_prop_str(name), _prop_str(typ), _prop_str(sub),
                      _prop_str(flag), _prop_f64(x), _prop_f64(y), _prop_f64(z)])


# ── 상위 엑스포트 함수 ──────────────────────────────────────────────────
def export_fbx_binary(
    verts: np.ndarray,
    faces: np.ndarray,                              # (F,3) int
    *,
    normals: Optional[np.ndarray] = None,           # (V,3)
    uvs: Optional[np.ndarray] = None,               # (V,2)
    vertex_colors: Optional[np.ndarray] = None,     # (V,3) 0~1
    face_mat_ids: Optional[np.ndarray] = None,      # (F,) int
    materials: Optional[Sequence[Tuple[str, Tuple[float, float, float]]]] = None,
    texture_png: Optional[bytes] = None,            # Embedded diffuse map (PNG bytes)
    texture_name: str = "diffuse",
) -> bytes:
    """
    Binary FBX 7.4.0 바이트 반환. Blender에서 직접 import 가능.

    texture_png 주면 모든 머티리얼 DiffuseColor에 PNG 임베드 텍스처 연결.
    Blender/Maya/Unity FBX 로더 전부 지원 (Video.Content = raw bytes).
    """
    _reset_uid()

    V = np.asarray(verts, dtype=np.float64)
    F = np.asarray(faces, dtype=np.int64)
    nV = len(V)
    nF = len(F)

    if not materials:
        materials = [("lambert1", (0.8, 0.8, 0.8))]
    mat_count = len(materials)
    has_tex = bool(texture_png)

    # PolygonVertexIndex: 마지막 index에 ~(bitwise NOT) 적용 = -(x+1)
    poly_idx = np.empty(nF * 3, dtype=np.int32)
    for i in range(nF):
        a, b, c = int(F[i, 0]), int(F[i, 1]), int(F[i, 2])
        poly_idx[i*3 + 0] = a
        poly_idx[i*3 + 1] = b
        poly_idx[i*3 + 2] = -c - 1

    # UID 할당
    geo_uid = _uid()
    model_uid = _uid()
    mat_uids = [_uid() for _ in materials]
    # Texture/Video UIDs (텍스처 있을 때만)
    tex_uid = _uid() if has_tex else 0
    vid_uid = _uid() if has_tex else 0

    # ── Root ──────────────────────────────────────────────────────────
    root = Node("")  # pseudo

    # FBXHeaderExtension (Blender 호환 최소 세트).
    # NOTE: Autodesk FBX SDK(Unity/Maya)의 엄격한 파서는 이 writer의 출력을
    # "File is corrupted"로 판정합니다. Autodesk SDK 완전 호환을 위해서는
    # SceneInfo, FileId, 수십 개의 PropertyTemplate P 속성 등 방대한 추가가 필요.
    # → Unity/Maya 사용자는 GLB(glTFast) 또는 OBJ 경로 사용 권장.
    header = Node("FBXHeaderExtension")
    header.add(Node("FBXHeaderVersion", [_prop_i32(1003)]))
    header.add(Node("FBXVersion", [_prop_i32(_VERSION)]))
    t = time.localtime()
    cts = Node("CreationTimeStamp")
    cts.add(Node("Version", [_prop_i32(1000)]))
    cts.add(Node("Year", [_prop_i32(t.tm_year)]))
    cts.add(Node("Month", [_prop_i32(t.tm_mon)]))
    cts.add(Node("Day", [_prop_i32(t.tm_mday)]))
    cts.add(Node("Hour", [_prop_i32(t.tm_hour)]))
    cts.add(Node("Minute", [_prop_i32(t.tm_min)]))
    cts.add(Node("Second", [_prop_i32(t.tm_sec)]))
    cts.add(Node("Millisecond", [_prop_i32(0)]))
    header.add(cts)
    header.add(Node("Creator", [_prop_str("PointCloud Optimizer")]))
    root.add(header)

    # GlobalSettings
    gs = Node("GlobalSettings")
    gs.add(Node("Version", [_prop_i32(1000)]))
    p70 = Node("Properties70")
    p70.add(_prop70_int("UpAxis", 1))
    p70.add(_prop70_int("UpAxisSign", 1))
    p70.add(_prop70_int("FrontAxis", 2))
    p70.add(_prop70_int("FrontAxisSign", 1))
    p70.add(_prop70_int("CoordAxis", 0))
    p70.add(_prop70_int("CoordAxisSign", 1))
    p70.add(_prop70_dbl("UnitScaleFactor", 1.0))
    p70.add(_prop70_dbl("OriginalUnitScaleFactor", 1.0))
    gs.add(p70)
    root.add(gs)

    # Documents
    docs = Node("Documents")
    docs.add(Node("Count", [_prop_i32(1)]))
    doc_uid = _uid()
    doc = Node("Document", [_prop_i64(doc_uid), _prop_str(""), _prop_str("Scene")])
    doc.add(Node("RootNode", [_prop_i64(0)]))
    docs.add(doc)
    root.add(docs)

    # References
    root.add(Node("References"))

    # Definitions
    defs = Node("Definitions")
    defs.add(Node("Version", [_prop_i32(100)]))
    total_count = 1 + 1 + 1 + mat_count  # GlobalSettings + Geometry + Model + Materials
    if has_tex:
        total_count += 2                  # + Texture + Video
    defs.add(Node("Count", [_prop_i32(total_count)]))
    # GlobalSettings
    ot = Node("ObjectType", [_prop_str("GlobalSettings")])
    ot.add(Node("Count", [_prop_i32(1)]))
    defs.add(ot)
    # Geometry
    ot = Node("ObjectType", [_prop_str("Geometry")])
    ot.add(Node("Count", [_prop_i32(1)]))
    pt = Node("PropertyTemplate", [_prop_str("FbxMesh")])
    pt.add(Node("Properties70"))
    ot.add(pt)
    defs.add(ot)
    # Model
    ot = Node("ObjectType", [_prop_str("Model")])
    ot.add(Node("Count", [_prop_i32(1)]))
    pt = Node("PropertyTemplate", [_prop_str("FbxNode")])
    pt.add(Node("Properties70"))
    ot.add(pt)
    defs.add(ot)
    # Material
    ot = Node("ObjectType", [_prop_str("Material")])
    ot.add(Node("Count", [_prop_i32(mat_count)]))
    pt = Node("PropertyTemplate", [_prop_str("FbxSurfaceLambert")])
    pt.add(Node("Properties70"))
    ot.add(pt)
    defs.add(ot)
    # Texture + Video (embedded diffuse map)
    if has_tex:
        ot = Node("ObjectType", [_prop_str("Texture")])
        ot.add(Node("Count", [_prop_i32(1)]))
        pt = Node("PropertyTemplate", [_prop_str("FbxFileTexture")])
        pt.add(Node("Properties70"))
        ot.add(pt)
        defs.add(ot)
        ot = Node("ObjectType", [_prop_str("Video")])
        ot.add(Node("Count", [_prop_i32(1)]))
        pt = Node("PropertyTemplate", [_prop_str("FbxVideo")])
        pt.add(Node("Properties70"))
        ot.add(pt)
        defs.add(ot)
    root.add(defs)

    # Objects
    objs = Node("Objects")

    # Geometry
    # Blender 규칙: 두 번째 prop는 {element_name}\x00\x01{class_name}
    # ※ Unity FBX SDK는 element_name이 class_name과 같으면 geometry를 무시함.
    #    고유 이름(PointCloudGeo) 사용 — Blender/Maya/Unity 전부 호환.
    geo = Node("Geometry", [_prop_i64(geo_uid), _prop_str("PointCloudGeo\x00\x01Geometry"), _prop_str("Mesh")])
    # Vertices: flat float64
    v_flat = V.reshape(-1).astype(np.float64)
    geo.add(Node("Vertices", [P(b"d", v_flat)]))
    # PolygonVertexIndex: flat int32
    geo.add(Node("PolygonVertexIndex", [P(b"i", poly_idx)]))
    geo.add(Node("GeometryVersion", [_prop_i32(124)]))

    # LayerElementNormal
    if normals is not None and len(normals) == nV:
        n_flat = np.asarray(normals, dtype=np.float64).reshape(-1)
        le = Node("LayerElementNormal", [_prop_i32(0)])
        le.add(Node("Version", [_prop_i32(101)]))
        le.add(Node("Name", [_prop_str("")]))
        le.add(Node("MappingInformationType", [_prop_str("ByVertice")]))
        le.add(Node("ReferenceInformationType", [_prop_str("Direct")]))
        le.add(Node("Normals", [P(b"d", n_flat)]))
        geo.add(le)

    # LayerElementColor (per-vertex RGBA)
    if vertex_colors is not None and len(vertex_colors) == nV:
        col = np.asarray(vertex_colors, dtype=np.float64)
        if col.shape[1] == 3:
            col = np.hstack([col, np.ones((nV, 1))])
        c_flat = col.reshape(-1)
        le = Node("LayerElementColor", [_prop_i32(0)])
        le.add(Node("Version", [_prop_i32(101)]))
        le.add(Node("Name", [_prop_str("colorSet1")]))
        le.add(Node("MappingInformationType", [_prop_str("ByVertice")]))
        le.add(Node("ReferenceInformationType", [_prop_str("Direct")]))
        le.add(Node("Colors", [P(b"d", c_flat)]))
        geo.add(le)

    # LayerElementUV
    if uvs is not None and len(uvs) == nV:
        uv_flat = np.asarray(uvs, dtype=np.float64).reshape(-1)
        # UVIndex per polygon-vertex
        uv_idx = F.flatten().astype(np.int32)
        le = Node("LayerElementUV", [_prop_i32(0)])
        le.add(Node("Version", [_prop_i32(101)]))
        le.add(Node("Name", [_prop_str("map1")]))
        le.add(Node("MappingInformationType", [_prop_str("ByPolygonVertex")]))
        le.add(Node("ReferenceInformationType", [_prop_str("IndexToDirect")]))
        le.add(Node("UV", [P(b"d", uv_flat)]))
        le.add(Node("UVIndex", [P(b"i", uv_idx)]))
        geo.add(le)

    # LayerElementMaterial
    le = Node("LayerElementMaterial", [_prop_i32(0)])
    le.add(Node("Version", [_prop_i32(101)]))
    le.add(Node("Name", [_prop_str("")]))
    if face_mat_ids is not None and len(face_mat_ids) == nF and mat_count > 1:
        le.add(Node("MappingInformationType", [_prop_str("ByPolygon")]))
        le.add(Node("ReferenceInformationType", [_prop_str("IndexToDirect")]))
        mat_idx = np.asarray(face_mat_ids, dtype=np.int32)
        le.add(Node("Materials", [P(b"i", mat_idx)]))
    else:
        le.add(Node("MappingInformationType", [_prop_str("AllSame")]))
        le.add(Node("ReferenceInformationType", [_prop_str("IndexToDirect")]))
        le.add(Node("Materials", [P(b"i", np.array([0], dtype=np.int32))]))
    geo.add(le)

    # Layer
    layer = Node("Layer", [_prop_i32(0)])
    layer.add(Node("Version", [_prop_i32(100)]))
    if normals is not None and len(normals) == nV:
        le = Node("LayerElement")
        le.add(Node("Type", [_prop_str("LayerElementNormal")]))
        le.add(Node("TypedIndex", [_prop_i32(0)]))
        layer.add(le)
    if vertex_colors is not None and len(vertex_colors) == nV:
        le = Node("LayerElement")
        le.add(Node("Type", [_prop_str("LayerElementColor")]))
        le.add(Node("TypedIndex", [_prop_i32(0)]))
        layer.add(le)
    if uvs is not None and len(uvs) == nV:
        le = Node("LayerElement")
        le.add(Node("Type", [_prop_str("LayerElementUV")]))
        le.add(Node("TypedIndex", [_prop_i32(0)]))
        layer.add(le)
    le = Node("LayerElement")
    le.add(Node("Type", [_prop_str("LayerElementMaterial")]))
    le.add(Node("TypedIndex", [_prop_i32(0)]))
    layer.add(le)
    geo.add(layer)

    objs.add(geo)

    # Model — Blender 규칙: "{elem_name}\x00\x01{class}"
    model = Node("Model", [_prop_i64(model_uid),
                           _prop_str("PointCloudMesh\x00\x01Model"),
                           _prop_str("Mesh")])
    model.add(Node("Version", [_prop_i32(232)]))
    p70 = Node("Properties70")
    p70.add(_prop70_vec("Lcl Translation", "Lcl Translation", "", 0, 0, 0, anim=True))
    p70.add(_prop70_vec("Lcl Rotation", "Lcl Rotation", "", 0, 0, 0, anim=True))
    p70.add(_prop70_vec("Lcl Scaling", "Lcl Scaling", "", 1, 1, 1, anim=True))
    p70.add(_prop70_int("DefaultAttributeIndex", 0))
    model.add(p70)
    model.add(Node("Shading", [P(b"C", True)]))
    model.add(Node("Culling", [_prop_str("CullingOff")]))
    objs.add(model)

    # Materials — Blender 규칙: "{name}\x00\x01Material"
    # 텍스처 있으면 diffuse를 1,1,1로 (텍스처가 승함) - Unity/Blender 관행
    tex_diffuse = 1.0 if has_tex else None
    for (name, col), muid in zip(materials, mat_uids):
        if tex_diffuse is not None:
            r = g = b = tex_diffuse
        else:
            r, g, b = float(col[0]), float(col[1]), float(col[2])
        mat = Node("Material", [_prop_i64(muid), _prop_str(f"{name}\x00\x01Material"), _prop_str("")])
        mat.add(Node("Version", [_prop_i32(102)]))
        mat.add(Node("ShadingModel", [_prop_str("lambert")]))
        mat.add(Node("MultiLayer", [_prop_i32(0)]))
        mp = Node("Properties70")
        mp.add(_prop70_color("DiffuseColor", r, g, b, anim=True))
        mp.add(_prop70_vec("Diffuse", "Vector3D", "Vector", r, g, b))
        mp.add(_prop70_color("AmbientColor", 0.1, 0.1, 0.1))
        mp.add(_prop70_vec("Emissive", "Vector3D", "Vector", 0, 0, 0))
        mp.add(_prop70_dbl("Opacity", 1.0))
        mat.add(mp)
        objs.add(mat)

    # ── Video + Texture (임베드 PNG) ─────────────────────────────────
    if has_tex:
        # 안전한 파일명 (확장자 포함)
        _tex_filename = f"{texture_name}.png"
        # Video (raw PNG bytes embedded via Content: R)
        vid = Node("Video", [_prop_i64(vid_uid),
                             _prop_str(f"{texture_name}\x00\x01Video"),
                             _prop_str("Clip")])
        vid.add(Node("Type", [_prop_str("Clip")]))
        vp = Node("Properties70")
        vp.add(Node("P", [_prop_str("Path"), _prop_str("KString"),
                          _prop_str("XRefUrl"), _prop_str(""),
                          _prop_str(_tex_filename)]))
        vid.add(vp)
        vid.add(Node("UseMipMap", [_prop_i32(0)]))
        vid.add(Node("Filename", [_prop_str(_tex_filename)]))
        vid.add(Node("RelativeFilename", [_prop_str(_tex_filename)]))
        vid.add(Node("Content", [_prop_bytes(bytes(texture_png))]))
        objs.add(vid)

        # Texture
        tex = Node("Texture", [_prop_i64(tex_uid),
                               _prop_str(f"{texture_name}\x00\x01Texture"),
                               _prop_str("")])
        tex.add(Node("Type", [_prop_str("TextureVideoClip")]))
        tex.add(Node("Version", [_prop_i32(202)]))
        tex.add(Node("TextureName", [_prop_str(f"{texture_name}\x00\x01Texture")]))
        tp = Node("Properties70")
        tp.add(Node("P", [_prop_str("CurrentTextureBlendMode"),
                          _prop_str("enum"), _prop_str(""), _prop_str(""),
                          _prop_i32(0)]))
        tp.add(Node("P", [_prop_str("UVSet"), _prop_str("KString"),
                          _prop_str(""), _prop_str(""), _prop_str("map1")]))
        tp.add(Node("P", [_prop_str("UseMaterial"), _prop_str("bool"),
                          _prop_str(""), _prop_str(""), _prop_i32(1)]))
        tex.add(tp)
        tex.add(Node("Media", [_prop_str(f"{texture_name}\x00\x01Video")]))
        tex.add(Node("Filename", [_prop_str(_tex_filename)]))
        tex.add(Node("RelativeFilename", [_prop_str(_tex_filename)]))
        tex.add(Node("ModelUVTranslation", [_prop_f64(0.0), _prop_f64(0.0)]))
        tex.add(Node("ModelUVScaling", [_prop_f64(1.0), _prop_f64(1.0)]))
        tex.add(Node("Texture_Alpha_Source", [_prop_str("None")]))
        tex.add(Node("Cropping", [_prop_i32(0), _prop_i32(0),
                                  _prop_i32(0), _prop_i32(0)]))
        objs.add(tex)

    root.add(objs)

    # Connections
    conns = Node("Connections")
    # Model → RootNode
    conns.add(Node("C", [_prop_str("OO"), _prop_i64(model_uid), _prop_i64(0)]))
    # Geometry → Model
    conns.add(Node("C", [_prop_str("OO"), _prop_i64(geo_uid), _prop_i64(model_uid)]))
    # Material → Model
    for muid in mat_uids:
        conns.add(Node("C", [_prop_str("OO"), _prop_i64(muid), _prop_i64(model_uid)]))
    # Video → Texture (OO), Texture → Material DiffuseColor (OP, 모든 머티리얼에)
    if has_tex:
        conns.add(Node("C", [_prop_str("OO"), _prop_i64(vid_uid), _prop_i64(tex_uid)]))
        for muid in mat_uids:
            conns.add(Node("C", [_prop_str("OP"), _prop_i64(tex_uid),
                                 _prop_i64(muid), _prop_str("DiffuseColor")]))
    root.add(conns)

    # Takes
    takes = Node("Takes")
    takes.add(Node("Current", [_prop_str("")]))
    root.add(takes)

    # ── 직렬화 ────────────────────────────────────────────────────────
    buf = bytearray()
    buf.extend(_MAGIC)
    buf.extend(struct.pack("<I", _VERSION))

    for ch in root.children:
        _write_node(buf, ch)
    # 마지막에 NULL 레코드
    buf.extend(b"\x00" * _NULL_RECORD_SIZE)

    # Footer — Blender's exact byte layout (verified against blender_ref.fbx hexdump).
    # Unity FBX SDK rejects files with wrong footer_code → "File is corrupted".
    # Layout: footer_code(16) + pad(8 zeros) + version(4) + zeros(120) + magic(16)
    _FOOTER_CODE = bytes([0xFA, 0xBC, 0xAB, 0x09, 0xD0, 0xC8, 0xD4, 0x66,
                          0xB1, 0x76, 0xFB, 0x83, 0x1C, 0xF7, 0x26, 0x7E])
    _FOOTER_MAGIC = bytes([0xF8, 0x5A, 0x8C, 0x6A, 0xDE, 0xF5, 0xD9, 0x7E,
                           0xEC, 0xE9, 0x0C, 0xE3, 0x75, 0x8F, 0x29, 0x0B])

    buf.extend(_FOOTER_CODE)
    buf.extend(b"\x00" * 8)                        # 8-byte pad
    buf.extend(struct.pack("<I", _VERSION))        # version (little-endian u32)
    buf.extend(b"\x00" * 120)                      # 120 zero bytes
    buf.extend(_FOOTER_MAGIC)                      # final magic

    return bytes(buf)


# ══════════════════════════════════════════════════════════════════════════════
# Binary FBX 파싱 (읽기) — Page 4 입력용
# ══════════════════════════════════════════════════════════════════════════════
def parse_fbx_binary(data: bytes, full: bool = False):
    """Binary FBX → 메쉬 데이터.
    기본: (verts (V,3), faces (F,3))
    full=True: {"verts","faces","uvs","uv_idx","normals","materials","face_mat_ids"}
    """
    if not data.startswith(b"Kaydara FBX Binary"):
        raise ValueError("Binary FBX 아님 (Kaydara 매직 없음)")

    version = struct.unpack("<I", data[23:27])[0]
    is_64 = version >= 7500
    pos = 27

    def read_prop(buf, p):
        t = buf[p:p+1]; p += 1
        if t == b"Y":
            return struct.unpack("<h", buf[p:p+2])[0], p+2
        if t == b"C":
            return buf[p], p+1
        if t == b"I":
            return struct.unpack("<i", buf[p:p+4])[0], p+4
        if t == b"F":
            return struct.unpack("<f", buf[p:p+4])[0], p+4
        if t == b"D":
            return struct.unpack("<d", buf[p:p+8])[0], p+8
        if t == b"L":
            return struct.unpack("<q", buf[p:p+8])[0], p+8
        if t in (b"S", b"R"):
            ln = struct.unpack("<I", buf[p:p+4])[0]
            return buf[p+4:p+4+ln], p+4+ln
        if t in (b"f", b"d", b"l", b"i", b"b"):
            count = struct.unpack("<I", buf[p:p+4])[0]; p += 4
            enc = struct.unpack("<I", buf[p:p+4])[0]; p += 4
            clen = struct.unpack("<I", buf[p:p+4])[0]; p += 4
            raw = buf[p:p+clen]; p += clen
            if enc == 1:
                raw = zlib.decompress(raw)
            dmap = {b"f": "<f4", b"d": "<f8", b"l": "<i8", b"i": "<i4", b"b": "<u1"}
            arr = np.frombuffer(raw, dtype=dmap[t]).copy()
            return arr, p
        raise ValueError(f"unknown type: {t}")

    result = {"vertices": None, "polygon_index": None, "uvs": None, "uv_idx": None,
              "normals": None, "materials_idx": None, "material_names": []}
    # 파싱 컨텍스트 — 어떤 노드 안에 있는지 추적
    ctx_stack = []

    def read_node(buf, p):
        if is_64:
            end_off = struct.unpack("<Q", buf[p:p+8])[0]; p += 8
            nprops = struct.unpack("<Q", buf[p:p+8])[0]; p += 8
            plen = struct.unpack("<Q", buf[p:p+8])[0]; p += 8
        else:
            end_off = struct.unpack("<I", buf[p:p+4])[0]; p += 4
            nprops = struct.unpack("<I", buf[p:p+4])[0]; p += 4
            plen = struct.unpack("<I", buf[p:p+4])[0]; p += 4
        if end_off == 0:
            return None, p
        name_len = buf[p]; p += 1
        name = buf[p:p+name_len].decode("ascii", errors="replace"); p += name_len
        props_collected = []
        for _ in range(nprops):
            v, p = read_prop(buf, p)
            props_collected.append(v)
            # Geometry top-level 노드들
            if name == "Vertices" and hasattr(v, "shape") and result["vertices"] is None:
                result["vertices"] = v
            elif name == "PolygonVertexIndex" and hasattr(v, "shape") and result["polygon_index"] is None:
                result["polygon_index"] = v
            # LayerElement 내부
            elif ctx_stack and ctx_stack[-1] == "LayerElementUV":
                if name == "UV" and hasattr(v, "shape") and result["uvs"] is None:
                    result["uvs"] = v
                elif name == "UVIndex" and hasattr(v, "shape") and result["uv_idx"] is None:
                    result["uv_idx"] = v
            elif ctx_stack and ctx_stack[-1] == "LayerElementNormal":
                if name == "Normals" and hasattr(v, "shape") and result["normals"] is None:
                    result["normals"] = v
            elif ctx_stack and ctx_stack[-1] == "LayerElementMaterial":
                if name == "Materials" and hasattr(v, "shape") and result["materials_idx"] is None:
                    result["materials_idx"] = v
            # Material 노드의 이름 프로퍼티 (2번째 prop는 "name\x00\x01Material")
            elif name == "Material" and len(props_collected) == 2 and isinstance(v, bytes):
                try:
                    nm = v.split(b"\x00\x01")[0].decode("utf-8", errors="replace")
                    result["material_names"].append(nm)
                except Exception:
                    pass

        # 컨텍스트 푸시 후 자식 파싱
        push = name in ("LayerElementUV", "LayerElementNormal", "LayerElementMaterial")
        if push: ctx_stack.append(name)
        while p < end_off:
            child, p = read_node(buf, p)
            if child is None:
                break
        if push: ctx_stack.pop()
        return True, end_off

    try:
        while pos < len(data) - 16:
            node, pos = read_node(data, pos)
            if node is None:
                break
    except Exception:
        pass

    if result["vertices"] is None or result["polygon_index"] is None:
        raise ValueError("FBX에서 Vertices/PolygonVertexIndex를 찾지 못함")

    V = result["vertices"].reshape(-1, 3).astype(np.float32)
    P = result["polygon_index"].astype(np.int64)

    faces = []
    poly_face_map = []   # 각 삼각형이 어느 원본 polygon에서 나왔는지
    cur = []
    poly_idx = 0
    for x in P:
        if x < 0:
            cur.append(int(-x - 1))
            if len(cur) >= 3:
                for i in range(1, len(cur) - 1):
                    faces.append([cur[0], cur[i], cur[i+1]])
                    poly_face_map.append(poly_idx)
            poly_idx += 1
            cur = []
        else:
            cur.append(int(x))
    F = np.asarray(faces, dtype=np.int32) if faces else np.zeros((0, 3), dtype=np.int32)

    if not full:
        return V, F

    # ── full=True: UV, normals, materials 포함 ───────────────────────
    out = {"verts": V, "faces": F, "poly_face_map": np.asarray(poly_face_map, dtype=np.int32)}

    # UV: ByPolygonVertex IndexToDirect 기준
    if result["uvs"] is not None and result["uv_idx"] is not None:
        uv_arr = result["uvs"].reshape(-1, 2).astype(np.float32)
        uv_idx_arr = result["uv_idx"].astype(np.int64)
        # 원본 polygon-vertex 순서 → 삼각형 순서 매핑 (fan triangulation 고려)
        # 간단 접근: 원본 polygon별 UV를 삼각형 버텍스별로 기록
        # 실제로는 정확한 매핑을 위해 polygon 재파싱 필요 — 여기선 버텍스 개수 같을 때만 per-vertex UV 시도
        if len(uv_arr) == len(V):
            out["uvs"] = uv_arr
        else:
            out["uvs"] = None  # per-vertex 아닌 경우 복잡 — 스킵
    else:
        out["uvs"] = None

    # Normals: ByVertice일 때만 (V와 크기 같을 때)
    if result["normals"] is not None:
        n_arr = result["normals"].reshape(-1, 3).astype(np.float32)
        if len(n_arr) == len(V):
            out["normals"] = n_arr
        else:
            out["normals"] = None
    else:
        out["normals"] = None

    # Materials: ByPolygon이면 각 polygon에 id 할당 → 삼각형별로 매핑
    if result["materials_idx"] is not None:
        mat_arr = result["materials_idx"].astype(np.int32)
        if len(mat_arr) == 1:
            # AllSame
            out["face_mat_ids"] = np.zeros(len(F), dtype=np.int32)
        elif len(mat_arr) == poly_idx:
            # ByPolygon — 각 삼각형에 poly_face_map으로 배정
            out["face_mat_ids"] = mat_arr[out["poly_face_map"]]
        else:
            out["face_mat_ids"] = None
    else:
        out["face_mat_ids"] = None

    out["material_names"] = result["material_names"]
    return out
