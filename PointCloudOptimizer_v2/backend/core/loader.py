"""
Point cloud file loader.
Supports: PLY (ASCII + binary, optional nx/ny/nz), XYZ / PTS / CSV, PCD (ASCII), OBJ (vertex-only), LAS (optional)
"""
from __future__ import annotations

import struct
from typing import Any, Dict, Optional, Tuple

import numpy as np


def _ply_normal_indices(props: list[str]) -> Tuple[Optional[int], Optional[int], Optional[int]]:
    for a, b, c in (("nx", "ny", "nz"), ("normal_x", "normal_y", "normal_z")):
        if a in props and b in props and c in props:
            return props.index(a), props.index(b), props.index(c)
    return None, None, None


def _ply_color_indices(props: list[str]) -> Tuple[Optional[int], Optional[int], Optional[int], bool]:
    """R/G/B 인덱스 + 8bit 정수형 여부 반환 (정수면 /255 정규화 필요)."""
    for a, b, c in (("red", "green", "blue"),
                    ("r", "g", "b"),
                    ("diffuse_red", "diffuse_green", "diffuse_blue")):
        if a in props and b in props and c in props:
            return props.index(a), props.index(b), props.index(c), True  # 대부분 uchar
    return None, None, None, False


# ── PLY ──────────────────────────────────────────────────────────────────────
def _parse_ply_full(data: bytes) -> Tuple[np.ndarray, Optional[np.ndarray], Optional[np.ndarray]]:
    """PLY → (pts, normals or None, colors or None). colors는 0~1 float32 (N,3)."""
    raw = memoryview(data)
    text = data[:8192].decode("latin-1")
    lines = text.split("\n")

    n_verts = 0
    props: list[str] = []
    prop_types: list[str] = []
    in_vertex = False
    binary_little = False
    binary_big = False
    header_end = 0

    for line in lines:
        l = line.strip()
        if l == "end_header":
            header_end = data.index(b"end_header") + len(b"end_header") + 1
            break
        if l.startswith("element vertex"):
            n_verts = int(l.split()[-1])
            in_vertex = True
        elif l.startswith("element") and not l.startswith("element vertex"):
            in_vertex = False
        elif l.startswith("property") and in_vertex:
            parts = l.split()
            prop_types.append(parts[1])
            props.append(parts[2])
        elif l == "format binary_little_endian 1.0":
            binary_little = True
        elif l == "format binary_big_endian 1.0":
            binary_big = True

    if n_verts == 0:
        raise ValueError("PLY: vertex 수가 0입니다")

    type_map = {"float": "f", "float32": "f", "double": "d", "float64": "d",
                "int": "i", "int32": "i", "uint": "I", "uint32": "I",
                "short": "h", "int16": "h", "ushort": "H", "uint16": "H",
                "char": "b", "int8": "b", "uchar": "B", "uint8": "B"}
    size_map = {"f": 4, "d": 8, "i": 4, "I": 4, "h": 2, "H": 2, "b": 1, "B": 1}

    x_idx = props.index("x") if "x" in props else None
    y_idx = props.index("y") if "y" in props else None
    z_idx = props.index("z") if "z" in props else None
    if x_idx is None or y_idx is None or z_idx is None:
        raise ValueError("PLY: x/y/z 프로퍼티를 찾을 수 없습니다")

    nx_i, ny_i, nz_i = _ply_normal_indices(props)
    has_n = nx_i is not None and ny_i is not None and nz_i is not None
    r_idx, g_idx, b_idx, _col_int = _ply_color_indices(props)
    has_c = r_idx is not None and g_idx is not None and b_idx is not None
    # 색상이 정수(uchar/uint8/uint16)인지 float인지 타입으로 판단
    color_is_int = False
    if has_c:
        ct = prop_types[r_idx]
        color_is_int = ct in ("uchar", "uint8", "char", "int8", "ushort", "uint16")

    if binary_little or binary_big:
        endian = "<" if binary_little else ">"
        fmt_chars = [type_map.get(t, "f") for t in prop_types]
        row_size = sum(size_map[c] for c in fmt_chars)
        row_fmt = endian + "".join(fmt_chars)
        body = bytes(raw[header_end: header_end + n_verts * row_size])
        rows = list(struct.iter_unpack(row_fmt, body))
        pts = np.array([[r[x_idx], r[y_idx], r[z_idx]] for r in rows], dtype=np.float32)
        if has_n:
            nrm = np.array([[r[nx_i], r[ny_i], r[nz_i]] for r in rows], dtype=np.float32)
        else:
            nrm = None
        if has_c:
            col = np.array([[r[r_idx], r[g_idx], r[b_idx]] for r in rows], dtype=np.float32)
            if color_is_int:
                col /= 255.0
            col = np.clip(col, 0.0, 1.0)
        else:
            col = None
    else:
        body = data[header_end:].decode("latin-1")
        pts_list = []
        nrm_list = []
        col_list = []
        need = 3
        if has_n: need = max(need, max(nx_i, ny_i, nz_i) + 1)
        if has_c: need = max(need, max(r_idx, g_idx, b_idx) + 1)
        for line in body.split("\n"):
            parts = line.split()
            if len(parts) < need:
                continue
            try:
                pts_list.append([float(parts[x_idx]), float(parts[y_idx]), float(parts[z_idx])])
                if has_n:
                    nrm_list.append([float(parts[nx_i]), float(parts[ny_i]), float(parts[nz_i])])
                if has_c:
                    col_list.append([float(parts[r_idx]), float(parts[g_idx]), float(parts[b_idx])])
            except (ValueError, IndexError):
                continue
            if len(pts_list) >= n_verts:
                break
        pts = np.array(pts_list, dtype=np.float32)
        nrm = np.array(nrm_list, dtype=np.float32) if has_n and len(nrm_list) == len(pts_list) else None
        if has_c and len(col_list) == len(pts_list):
            col = np.array(col_list, dtype=np.float32)
            if color_is_int:
                col /= 255.0
            col = np.clip(col, 0.0, 1.0)
        else:
            col = None

    return pts, nrm, col


# 역호환: 기존 호출부 유지
def _parse_ply_points_normals(data: bytes) -> Tuple[np.ndarray, Optional[np.ndarray]]:
    pts, nrm, _ = _parse_ply_full(data)
    return pts, nrm


def _parse_ply(data: bytes) -> np.ndarray:
    pts, _, _ = _parse_ply_full(data)
    return pts


# ── XYZ / PTS / CSV ──────────────────────────────────────────────────────────
def _parse_xyz(data: bytes) -> np.ndarray:
    text = data.decode("utf-8", errors="replace")
    pts_list = []
    for line in text.split("\n"):
        line = line.strip()
        if not line or line.startswith("#") or line.startswith("//"):
            continue
        parts = line.replace(",", " ").split()
        if len(parts) < 3:
            continue
        try:
            pts_list.append([float(parts[0]), float(parts[1]), float(parts[2])])
        except ValueError:
            continue
    if not pts_list:
        raise ValueError("XYZ: 유효한 포인트를 찾을 수 없습니다")
    return np.array(pts_list, dtype=np.float32)


# ── OBJ (vertex only) ────────────────────────────────────────────────────────
def _parse_obj_points(data: bytes) -> np.ndarray:
    text = data.decode("utf-8", errors="replace")
    pts_list = []
    for line in text.split("\n"):
        if not line.startswith("v "):
            continue
        parts = line.split()
        if len(parts) >= 4:
            try:
                pts_list.append([float(parts[1]), float(parts[2]), float(parts[3])])
            except ValueError:
                continue
    if not pts_list:
        raise ValueError("OBJ: vertex를 찾을 수 없습니다")
    return np.array(pts_list, dtype=np.float32)


# ── PCD (ASCII only) ─────────────────────────────────────────────────────────
def _parse_pcd(data: bytes) -> np.ndarray:
    text = data.decode("utf-8", errors="replace")
    lines = text.split("\n")
    x_col = y_col = z_col = 0
    data_start = 0
    fields: list[str] = []

    for i, line in enumerate(lines):
        l = line.strip().upper()
        if l.startswith("FIELDS"):
            fields = line.strip().split()[1:]
            if "X" in [f.upper() for f in fields]:
                fi = [f.upper() for f in fields]
                x_col = fi.index("X")
                y_col = fi.index("Y")
                z_col = fi.index("Z")
        elif l.startswith("DATA"):
            data_start = i + 1
            break

    pts_list = []
    for line in lines[data_start:]:
        parts = line.split()
        if len(parts) <= max(x_col, y_col, z_col):
            continue
        try:
            pts_list.append([float(parts[x_col]), float(parts[y_col]), float(parts[z_col])])
        except ValueError:
            continue

    if not pts_list:
        raise ValueError("PCD: 포인트를 찾을 수 없습니다")
    return np.array(pts_list, dtype=np.float32)


# ── LAS / LAZ ─────────────────────────────────────────────────────────────────
def _parse_las(data: bytes) -> np.ndarray:
    try:
        import laspy
    except ImportError:
        raise ValueError(
            "LAS/LAZ 파일 지원을 위해 laspy를 설치하세요:\n"
            "  pip install \"laspy[lazrs,laszip]>=2.4.0\"\n"
            "(LAZ 압축 포맷은 lazrs 또는 laszip 백엔드가 필수입니다.)"
        )

    import io as _io
    is_laz = data[:4] == b"LASF" and (data[24:26] in (b"\x01\x02", b"\x01\x03", b"\x01\x04") or True) \
             and len(data) > 0 and (
                 # LAZ는 VLR에 "laszip encoded" record가 포함됨
                 b"laszip encoded" in data[:16384]
             )
    try:
        las = laspy.read(_io.BytesIO(data))
    except Exception as e:
        msg = str(e)
        if is_laz or "laz" in msg.lower() or "compress" in msg.lower():
            raise ValueError(
                "LAZ 압축 해제 백엔드(lazrs/laszip)가 없습니다.\n"
                "해결 방법:\n"
                "  pip install \"laspy[lazrs]>=2.4.0\"   (권장, Rust 기반)\n"
                "  또는  pip install \"laspy[laszip]\"   (LASzip.dll 필요)\n"
                f"\n원본 오류: {msg}"
            )
        raise ValueError(f"LAS 파싱 오류: {msg}")

    pts = np.column_stack([las.x, las.y, las.z]).astype(np.float32)
    if pts.size == 0:
        raise ValueError("LAS/LAZ 파일에 포인트가 없습니다")
    return pts


def _validate_magic(data: bytes, ext: str) -> None:
    """파일 내용의 magic bytes로 확장자 위장 방지.

    e.g. evil.exe → evil.ply 로 rename 공격 차단.
    PLY: "ply\n" header required (ASCII).
    PCD: "# .PCD" 또는 유사 문자열.
    OBJ: ASCII이므로 첫 바이트가 printable이어야.
    """
    if len(data) < 4:
        raise ValueError("파일이 너무 작거나 비어있습니다")
    head4 = data[:4]
    if ext == "ply":
        # PLY는 반드시 "ply\n" 또는 "ply\r\n"로 시작
        if not (head4.startswith(b"ply\n") or head4.startswith(b"ply\r")):
            raise ValueError("PLY 파일이 아닙니다 (magic 'ply' 헤더 누락)")
    elif ext == "pcd":
        # PCD는 주석 '#' 또는 'VERSION'으로 시작
        head8 = data[:8]
        if not (head8.startswith(b"# ") or head8.startswith(b"VERSION")):
            raise ValueError("PCD 파일이 아닙니다")
    # xyz/pts/csv/txt/obj는 ASCII 텍스트라 magic 엄격 체크 생략 (앞 4바이트 non-printable이면 거절)
    elif ext in ("xyz", "pts", "csv", "txt", "obj"):
        # 최소한 ASCII/UTF-8로 디코딩 가능해야
        try:
            head_preview = data[:256].decode("utf-8", errors="strict")
        except UnicodeDecodeError:
            raise ValueError(f".{ext} 파일이 텍스트 형식이 아닙니다 (binary 감지)")
        # 헤더가 숫자/문자/공백/구분자 이외의 non-printable이 많으면 거절
        non_printable = sum(1 for c in head_preview if ord(c) < 32 and c not in "\r\n\t")
        if non_printable > len(head_preview) * 0.1:
            raise ValueError(f".{ext} 파일 내용이 비정상적입니다")


def load_full(filename: str, data: bytes) -> Dict[str, Any]:
    """pts + 선택적 normals + 선택적 colors (PLY에 red/green/blue 있을 때만).

    파일 확장자와 실제 content가 일치하는지 magic bytes로 먼저 검증.
    """
    ext = filename.rsplit(".", 1)[-1].lower() if "." in filename else ""
    # Magic byte 검증 — 확장자 위장 방지
    _validate_magic(data, ext)
    if ext == "ply":
        pts, nrm, col = _parse_ply_full(data)
        return {"pts": pts, "normals": nrm, "colors": col}
    parsers = {
        "xyz": _parse_xyz,
        "pts": _parse_xyz,
        "csv": _parse_xyz,
        "txt": _parse_xyz,
        "obj": _parse_obj_points,
        "pcd": _parse_pcd,
        "las": _parse_las,
        "laz": _parse_las,
    }
    parser = parsers.get(ext)
    if parser is None:
        try:
            return {"pts": _parse_xyz(data), "normals": None, "colors": None}
        except Exception:
            raise ValueError(f"지원하지 않는 파일 형식: .{ext}")
    return {"pts": parser(data), "normals": None, "colors": None}


def load(filename: str, data: bytes) -> np.ndarray:
    return load_full(filename, data)["pts"]
