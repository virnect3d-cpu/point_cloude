#!/usr/bin/env python
"""
PointCloud Optimizer — 데스크톱 앱 창 (외부 브라우저 없음)
pywebview(Edge WebView2)로 http://127.0.0.1:PORT 를 창에 표시합니다.

Usage:
  python run_desktop.py [--port 8000] [--host 127.0.0.1] [--maximized]
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).parent.resolve()
sys.path.insert(0, str(ROOT))


# ─────────────────────────────────────────────────────────────────────────────
# 의존성 체크
# ─────────────────────────────────────────────────────────────────────────────
def check_deps() -> None:
    missing: list[tuple[str, str]] = [
        ("fastapi",    "fastapi"),
        ("uvicorn",    "uvicorn"),
        ("numpy",      "numpy"),
        ("scipy",      "scipy"),
        ("skimage",    "scikit-image"),
        ("trimesh",    "trimesh"),
        ("open3d",     "open3d"),
        ("multipart",  "python-multipart"),
        ("webview",    "pywebview"),
    ]
    bad: list[str] = []
    for mod, pip_name in missing:
        try:
            __import__(mod)
        except ImportError:
            bad.append(pip_name)
    if bad:
        print("=" * 60)
        print("필요 패키지가 없습니다. 아래 한 줄로 설치 후 다시 실행하세요.\n")
        print(f"  pip install {' '.join(bad)}")
        print("=" * 60)
        sys.exit(1)


# ─────────────────────────────────────────────────────────────────────────────
# 서버 대기
# ─────────────────────────────────────────────────────────────────────────────
def wait_for_server(base: str, timeout: float = 30.0) -> bool:
    url = base.rstrip("/") + "/api/health"
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=0.8) as r:
                if r.status == 200:
                    return True
        except (urllib.error.URLError, TimeoutError, OSError):
            time.sleep(0.08)
    return False


# ─────────────────────────────────────────────────────────────────────────────
# PyWebView JS API  —  Python 함수를 window.pywebview.api.xxx() 로 노출
# ─────────────────────────────────────────────────────────────────────────────
class JsApi:
    """JavaScript → Python 네이티브 브리지.
    모든 메서드는 pywebview에 의해 JS 스레드에서 호출됩니다.
    반환값은 JSON 직렬화 가능한 타입이어야 합니다.
    """

    # ── 화면 정보 ──────────────────────────────────────────────────────────
    def get_screen_info(self) -> dict:
        """주 모니터의 물리 해상도를 반환합니다."""
        try:
            import webview
            screens = webview.screens
            if screens:
                s = screens[0]
                return {"width": s.width, "height": s.height, "ok": True}
        except Exception:
            pass
        return {"width": 1920, "height": 1080, "ok": False}

    # ── 창 조작 ────────────────────────────────────────────────────────────
    def maximize(self) -> None:
        try:
            import webview
            webview.windows[0].maximize()
        except Exception:
            pass

    def restore(self) -> None:
        try:
            import webview
            webview.windows[0].restore()
        except Exception:
            pass

    # ── 파일 저장 다이얼로그 ───────────────────────────────────────────────
    def save_file_dialog(self, default_name: str = "output.obj",
                         content: str = "") -> dict:
        """네이티브 저장 다이얼로그를 열고 파일을 씁니다.
        content 가 비어있으면 경로만 반환합니다.
        """
        try:
            import webview
            win = webview.windows[0]
            ext = Path(default_name).suffix.lstrip(".").upper() or "OBJ"
            result = win.create_file_dialog(
                webview.SAVE_DIALOG,
                directory=str(Path.home() / "Downloads"),
                save_filename=default_name,
                file_types=(f"{ext} 파일 (*.{ext.lower()})", "모든 파일 (*.*)")
            )
            if not result:
                return {"ok": False, "reason": "cancelled"}
            path = result[0] if isinstance(result, (list, tuple)) else result
            if content:
                Path(path).write_text(content, encoding="utf-8")
            return {"ok": True, "path": str(path)}
        except Exception as e:
            return {"ok": False, "reason": str(e)}

    def write_text_file(self, path: str, content: str) -> dict:
        """지정 경로에 텍스트 파일 직접 쓰기 (다이얼로그 없음).
        OBJ 저장 후 같은 폴더에 MTL 부수 파일 쓰는 용도.
        """
        try:
            p = Path(path)
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text(content or "", encoding="utf-8")
            return {"ok": True, "path": str(p)}
        except Exception as e:
            return {"ok": False, "reason": str(e)}

    def write_bytes_file(self, path: str, b64: str) -> dict:
        """지정 경로에 Base64 바이너리 직접 쓰기 (다이얼로그 없음).
        폴더 선택 후 PLY·이미지 등 바이너리 파일 일괄 저장용.
        """
        import base64
        try:
            p = Path(path)
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_bytes(base64.b64decode(b64 or ""))
            return {"ok": True, "path": str(p)}
        except Exception as e:
            return {"ok": False, "reason": str(e)}

    def save_bytes_dialog(self, default_name: str, b64: str) -> dict:
        """Base64 인코딩된 바이너리를 네이티브 다이얼로그로 저장합니다."""
        import base64
        try:
            import webview
            win = webview.windows[0]
            ext = Path(default_name).suffix.lstrip(".").upper() or "BIN"
            result = win.create_file_dialog(
                webview.SAVE_DIALOG,
                directory=str(Path.home() / "Downloads"),
                save_filename=default_name,
                file_types=(f"{ext} 파일 (*.{ext.lower()})", "모든 파일 (*.*)")
            )
            if not result:
                return {"ok": False, "reason": "cancelled"}
            path = result[0] if isinstance(result, (list, tuple)) else result
            Path(path).write_bytes(base64.b64decode(b64))
            return {"ok": True, "path": str(path)}
        except Exception as e:
            return {"ok": False, "reason": str(e)}

    # ── 폴더 선택 (출력 폴더 지정) ─────────────────────────────────────────
    def pick_directory(self) -> dict:
        """네이티브 폴더 선택 다이얼로그를 엽니다."""
        try:
            import webview
            win = webview.windows[0]
            result = win.create_file_dialog(
                webview.FOLDER_DIALOG,
                directory=str(Path.home())
            )
            if not result:
                return {"ok": False, "reason": "cancelled"}
            path = result[0] if isinstance(result, (list, tuple)) else result
            return {"ok": True, "path": str(path), "name": Path(path).name}
        except Exception as e:
            return {"ok": False, "reason": str(e)}

    # ── 폴더를 탐색기에서 열기 ─────────────────────────────────────────────
    def reveal_in_explorer(self, path: str) -> dict:
        """파일 또는 폴더를 Windows 탐색기에서 엽니다."""
        try:
            p = Path(path)
            target = str(p) if p.is_dir() else str(p.parent)
            if sys.platform == "win32":
                if p.is_file():
                    subprocess.Popen(["explorer", "/select,", str(p)])
                else:
                    subprocess.Popen(["explorer", target])
            elif sys.platform == "darwin":
                subprocess.Popen(["open", "-R", str(p)])
            else:
                subprocess.Popen(["xdg-open", target])
            return {"ok": True}
        except Exception as e:
            return {"ok": False, "reason": str(e)}

    # ── 파일을 직접 읽어서 서버에 업로드 (대용량 파일용) ─────────────────
    def upload_file_dialog(self, upload_url: str) -> dict:
        """네이티브 파일 선택 후 Python에서 직접 multipart POST 전송합니다.
        JS fetch 대신 Python urllib을 사용하므로 메모리 제한 없음.
        """
        import urllib.request, io, mimetypes, uuid
        try:
            import webview
            win = webview.windows[0]
            exts = (
                "포인트 클라우드 (*.ply;*.xyz;*.pts;*.pcd;*.las;*.laz;*.obj;*.csv;*.txt;*.ptx;*.splat;*.ksplat)",
                "모든 파일 (*.*)"
            )
            result = win.create_file_dialog(
                webview.OPEN_DIALOG,
                directory=str(Path.home() / "Downloads"),
                file_types=exts
            )
            if not result:
                return {"ok": False, "reason": "cancelled"}
            fpath = result[0] if isinstance(result, (list, tuple)) else result
            fpath = Path(fpath)
            if not fpath.exists():
                return {"ok": False, "reason": "파일이 존재하지 않습니다"}

            # multipart/form-data 직접 구성
            boundary = uuid.uuid4().hex
            fname = fpath.name
            data = fpath.read_bytes()
            mime = mimetypes.guess_type(fname)[0] or "application/octet-stream"

            body = (
                f"--{boundary}\r\n"
                f'Content-Disposition: form-data; name="file"; filename="{fname}"\r\n'
                f"Content-Type: {mime}\r\n\r\n"
            ).encode() + data + f"\r\n--{boundary}--\r\n".encode()

            req = urllib.request.Request(
                upload_url,
                data=body,
                headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
                method="POST"
            )
            # 대용량 파일 업로드 타임아웃 1시간 (16GB 업로드 대비)
            with urllib.request.urlopen(req, timeout=3600) as r:
                resp = json.loads(r.read().decode())
            return {"ok": True, "response": resp, "filename": fname,
                    "size": len(data), "path": str(fpath)}
        except Exception as e:
            return {"ok": False, "reason": str(e)}


# ─────────────────────────────────────────────────────────────────────────────
# 창 크기 자동 결정
# ─────────────────────────────────────────────────────────────────────────────
def _calc_window_size(maximized: bool) -> tuple[int, int, bool]:
    """(width, height, start_maximized) 반환.
    maximized=True 이면 최대화 플래그를 켭니다.
    아니면 스크린 90% 크기로 시작합니다.
    """
    if maximized:
        return 1440, 900, True   # maximized=True 라 실제 w/h는 무시됨

    # screeninfo 또는 tkinter로 화면 크기 측정
    sw, sh = 1920, 1080
    try:
        import tkinter as tk
        r = tk.Tk(); r.withdraw()
        sw, sh = r.winfo_screenwidth(), r.winfo_screenheight()
        r.destroy()
    except Exception:
        pass

    w = max(1280, int(sw * 0.88))
    h = max(800,  int(sh * 0.88))
    return w, h, False


# ─────────────────────────────────────────────────────────────────────────────
# 진입점
# ─────────────────────────────────────────────────────────────────────────────
def main() -> None:
    parser = argparse.ArgumentParser(description="PointCloud Optimizer (desktop window)")
    parser.add_argument("--host",      default="127.0.0.1")
    parser.add_argument("--port",      type=int, default=8000)
    parser.add_argument("--maximized", action="store_true",
                        help="앱을 최대화 상태로 시작합니다")
    parser.add_argument("--windowed",  action="store_true",
                        help="최대화 없이 스크린 비례 크기로 시작합니다")
    args = parser.parse_args()

    check_deps()

    import uvicorn
    import webview

    # uvicorn — 대용량 요청 허용 (16GB 업로드 허용)
    cfg = uvicorn.Config(
        "backend.app:app",
        host=args.host,
        port=args.port,
        log_level="warning",
        timeout_keep_alive=3600,     # 대용량 파일 업로드 중 연결 유지 (1시간)
        h11_max_incomplete_event_size=16 * 1024 * 1024 * 1024,  # h11 헤더 버퍼 16GB
    )
    server = uvicorn.Server(cfg)
    th = threading.Thread(target=server.run, daemon=True)
    th.start()

    base = f"http://{args.host}:{args.port}"
    if not wait_for_server(base, timeout=30.0):
        print("로컬 서버가 뜨지 않았습니다. 포트가 사용 중이면 --port 로 바꿔 보세요.")
        sys.exit(1)

    # 창 크기 결정 — --windowed 가 없으면 기본 최대화
    use_max = not args.windowed
    w, h, start_max = _calc_window_size(use_max)

    api = JsApi()
    window = webview.create_window(
        "PointCloud Optimizer",
        f"{base}/",
        js_api=api,
        width=w,
        height=h,
        min_size=(900, 640),
        maximized=start_max,
        easy_drag=False,        # 드래그로 창 이동 비활성화 (3D 뷰어 충돌 방지)
    )
    # 정상 모드 — DevTools 자동 오픈 없음
    webview.start(debug=False, private_mode=True)


if __name__ == "__main__":
    main()
