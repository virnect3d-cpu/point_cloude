"""
GLB (Binary glTF 2.0) exporter — Blender/Unity/three.js 네이티브 지원.

ASCII FBX와 달리 Blender에서 바로 열림. 모던 표준, 파일 크기 작음.
지원:
  - 지오메트리 (per-primitive)
  - per-vertex 색상 (COLOR_0 attribute)
  - UV (TEXCOORD_0)
  - 노멀 (NORMAL)
  - 다중 material (cluster별 primitive 분리 → 자동 material split)
  - Lambert 비슷한 PBR (roughness=1, metallic=0)
"""
from __future__ import annotations

import io
from typing import List, Optional, Sequence, Tuple

import numpy as np


def _split_by_material(
    verts: np.ndarray,
    faces: np.ndarray,
    face_mat_ids: np.ndarray,
    vertex_colors: Optional[np.ndarray] = None,
    normals: Optional[np.ndarray] = None,
    uvs: Optional[np.ndarray] = None,
) -> List[dict]:
    """
    face별 material id에 따라 별개 primitive로 쪼갬.
    각 primitive는 자기만의 vertex 재인덱싱된 배열을 가짐.
    indices는 반드시 1D flat (F*3,) — glTF accessor count가 개별 index 수
    """
    F = np.asarray(faces, dtype=np.int64)
    ids = np.asarray(face_mat_ids, dtype=np.int64)
    unique_mats = np.unique(ids)
    prims = []
    for m in unique_mats:
        mask = ids == m
        mf = F[mask]                              # (Fi, 3)
        used = np.unique(mf.flatten())
        old2new = -np.ones(len(verts), dtype=np.int64)
        old2new[used] = np.arange(len(used))
        remapped = old2new[mf]                    # (Fi, 3)
        p = {
            "verts": verts[used].astype(np.float32),
            "indices": remapped.flatten().astype(np.uint32),   # ★ 1D flatten
            "mat_id": int(m),
        }
        if normals is not None and len(normals) == len(verts):
            p["normals"] = normals[used].astype(np.float32)
        if uvs is not None and len(uvs) == len(verts):
            p["uvs"] = uvs[used].astype(np.float32)
        if vertex_colors is not None and len(vertex_colors) == len(verts):
            p["colors"] = vertex_colors[used].astype(np.float32)
        prims.append(p)
    return prims


def export_glb(
    verts: np.ndarray,
    faces: np.ndarray,                                # (F,3) tri
    *,
    normals: Optional[np.ndarray] = None,
    uvs: Optional[np.ndarray] = None,
    vertex_colors: Optional[np.ndarray] = None,       # (V,3) float 0~1
    face_mat_ids: Optional[np.ndarray] = None,        # (F,) int
    materials: Optional[Sequence[Tuple[str, Tuple[float, float, float]]]] = None,
) -> bytes:
    """
    반환: GLB bytes. Blender/Unity에서 직접 import 가능.

    material_count > 1 + face_mat_ids 주어지면 클러스터별 primitive로 쪼개서
    Blender에서 각각 별개 material 슬롯으로 나타남.
    """
    import pygltflib
    from pygltflib import (
        GLTF2, Scene, Node, Mesh, Primitive, Attributes,
        Accessor, BufferView, Buffer, Material, PbrMetallicRoughness,
        Asset,
    )

    if not materials:
        materials = [("lambert1", (0.8, 0.8, 0.8))]

    # 1. primitive 분할 (material별)
    if face_mat_ids is not None and len(face_mat_ids) == len(faces) and len(materials) > 1:
        prims = _split_by_material(verts, faces, face_mat_ids,
                                    vertex_colors=vertex_colors,
                                    normals=normals, uvs=uvs)
    else:
        # 단일 material
        p = {
            "verts": verts.astype(np.float32),
            "indices": faces.astype(np.uint32).flatten() if faces.ndim > 1 else faces.astype(np.uint32),
            "mat_id": 0,
        }
        # indices 2D → 1D
        if faces.ndim > 1:
            p["indices"] = faces.astype(np.uint32).flatten()
        if normals is not None:
            p["normals"] = normals.astype(np.float32)
        if uvs is not None:
            p["uvs"] = uvs.astype(np.float32)
        if vertex_colors is not None:
            p["colors"] = vertex_colors.astype(np.float32)
        prims = [p]

    # 2. 바이너리 버퍼 빌드 + 액세서/뷰 메타데이터 수집
    bin_bytes = bytearray()
    accessors: list = []
    buffer_views: list = []

    def _add_buffer_view(data: bytes, target: int = None) -> int:
        # 4바이트 정렬
        while len(bin_bytes) % 4 != 0:
            bin_bytes.append(0)
        offset = len(bin_bytes)
        bin_bytes.extend(data)
        bv = BufferView(buffer=0, byteOffset=offset, byteLength=len(data))
        if target is not None:
            bv.target = target
        buffer_views.append(bv)
        return len(buffer_views) - 1

    def _add_accessor(bv_idx: int, component_type: int, count: int, type_str: str,
                       min_vals=None, max_vals=None) -> int:
        acc = Accessor(
            bufferView=bv_idx, byteOffset=0,
            componentType=component_type, count=count, type=type_str,
        )
        if min_vals is not None: acc.min = min_vals
        if max_vals is not None: acc.max = max_vals
        accessors.append(acc)
        return len(accessors) - 1

    # GL 상수
    GL_UNSIGNED_INT = 5125
    GL_FLOAT = 5126
    GL_ELEMENT_ARRAY_BUFFER = 34963
    GL_ARRAY_BUFFER = 34962

    primitives_out = []
    for p in prims:
        V = np.ascontiguousarray(p["verts"], dtype=np.float32)
        I = np.ascontiguousarray(p["indices"], dtype=np.uint32)

        # POSITION
        pos_bv = _add_buffer_view(V.tobytes(), target=GL_ARRAY_BUFFER)
        pos_acc = _add_accessor(
            pos_bv, GL_FLOAT, len(V), "VEC3",
            min_vals=V.min(axis=0).tolist(), max_vals=V.max(axis=0).tolist(),
        )

        # INDICES
        idx_bv = _add_buffer_view(I.tobytes(), target=GL_ELEMENT_ARRAY_BUFFER)
        idx_acc = _add_accessor(idx_bv, GL_UNSIGNED_INT, len(I), "SCALAR")

        attrs = Attributes(POSITION=pos_acc)

        if "normals" in p:
            N = np.ascontiguousarray(p["normals"], dtype=np.float32)
            n_bv = _add_buffer_view(N.tobytes(), target=GL_ARRAY_BUFFER)
            n_acc = _add_accessor(n_bv, GL_FLOAT, len(N), "VEC3")
            attrs.NORMAL = n_acc

        if "uvs" in p:
            U = np.ascontiguousarray(p["uvs"], dtype=np.float32)
            u_bv = _add_buffer_view(U.tobytes(), target=GL_ARRAY_BUFFER)
            u_acc = _add_accessor(u_bv, GL_FLOAT, len(U), "VEC2")
            attrs.TEXCOORD_0 = u_acc

        if "colors" in p:
            C = np.ascontiguousarray(p["colors"], dtype=np.float32)
            c_bv = _add_buffer_view(C.tobytes(), target=GL_ARRAY_BUFFER)
            c_acc = _add_accessor(c_bv, GL_FLOAT, len(C), "VEC3")
            attrs.COLOR_0 = c_acc

        prim = Primitive(attributes=attrs, indices=idx_acc, material=int(p["mat_id"]))
        primitives_out.append(prim)

    # Materials
    gl_materials = []
    for (name, col) in materials:
        r, g, b = float(col[0]), float(col[1]), float(col[2])
        pbr = PbrMetallicRoughness(
            baseColorFactor=[r, g, b, 1.0],
            metallicFactor=0.0,
            roughnessFactor=0.9,
        )
        gl_materials.append(Material(name=name, pbrMetallicRoughness=pbr, doubleSided=True))

    # 조립
    gltf = GLTF2(
        asset=Asset(version="2.0", generator="PointCloud Optimizer"),
        buffers=[Buffer(byteLength=len(bin_bytes))],
        bufferViews=buffer_views,
        accessors=accessors,
        materials=gl_materials,
        meshes=[Mesh(primitives=primitives_out, name="PointCloudMesh")],
        nodes=[Node(mesh=0, name="PointCloudMesh")],
        scenes=[Scene(nodes=[0])],
        scene=0,
    )
    gltf.set_binary_blob(bytes(bin_bytes))
    # pygltflib GLB 직렬화
    return b"".join(gltf.save_to_bytes())


# ══════════════════════════════════════════════════════════════════════════════
# GLB 파싱 (읽기) — Page 4 입력용
# ══════════════════════════════════════════════════════════════════════════════
def parse_glb(data: bytes):
    """GLB → (verts (V,3), faces (F,3)). 첫 primitive 기준.
    quad 없음 (glTF는 tri만), n-gon 없음.
    """
    import tempfile, os
    import pygltflib
    # pygltflib 은 파일 경로 또는 bytes 스트림 받음
    with tempfile.NamedTemporaryFile(suffix=".glb", delete=False) as tf:
        tf.write(data)
        tpath = tf.name
    try:
        g = pygltflib.GLTF2().load_binary(tpath)
    finally:
        try: os.unlink(tpath)
        except Exception: pass

    if not g.meshes:
        raise ValueError("GLB에 메쉬 없음")
    mesh = g.meshes[0]
    if not mesh.primitives:
        raise ValueError("GLB 메쉬에 primitive 없음")

    # 모든 primitive 모아서 합치기
    blob = g.binary_blob()
    all_V = []
    all_F = []
    v_offset = 0

    def _acc_bytes(acc_idx):
        acc = g.accessors[acc_idx]
        bv = g.bufferViews[acc.bufferView]
        start = (bv.byteOffset or 0) + (acc.byteOffset or 0)
        comp = acc.componentType
        # SCALAR=1, VEC2=2, VEC3=3, VEC4=4
        n = {"SCALAR":1,"VEC2":2,"VEC3":3,"VEC4":4}.get(acc.type, 1)
        dmap = {5120:"i1", 5121:"u1", 5122:"i2", 5123:"u2",
                5125:"u4", 5126:"f4"}
        dtype = np.dtype("<" + dmap[comp])
        cnt = acc.count * n
        sz = dtype.itemsize * cnt
        raw = blob[start:start+sz]
        return np.frombuffer(raw, dtype=dtype).copy().reshape(acc.count, n) if n > 1 else np.frombuffer(raw, dtype=dtype).copy()

    for prim in mesh.primitives:
        if prim.attributes is None or prim.attributes.POSITION is None:
            continue
        V = _acc_bytes(prim.attributes.POSITION).astype(np.float32)
        if prim.indices is not None:
            I = _acc_bytes(prim.indices).astype(np.int64)
            F = I.reshape(-1, 3)
        else:
            # non-indexed: 연속 3개씩
            F = np.arange(len(V)).reshape(-1, 3)
        all_V.append(V)
        all_F.append(F + v_offset)
        v_offset += len(V)

    V = np.vstack(all_V).astype(np.float32)
    F = np.vstack(all_F).astype(np.int32)
    return V, F
