#!/usr/bin/env python
"""
PointCloud Optimizer — HTTP 서버만 (개발/브라우저 테스트용)
Usage:  python run.py [--port 8000] [--host 0.0.0.0]

앱 창으로 쓰려면:  python run_desktop.py  (외부 브라우저 없음)
"""
import argparse
import sys
import os
import webbrowser
import threading
import time
from pathlib import Path

ROOT = Path(__file__).parent
sys.path.insert(0, str(ROOT))


def check_deps():
    missing = []
    for pkg, name in [("fastapi", "fastapi"), ("uvicorn", "uvicorn"),
                      ("numpy", "numpy"), ("scipy", "scipy"),
                      ("skimage", "scikit-image"), ("trimesh", "trimesh"),
                      ("open3d", "open3d"),
                      ("multipart", "python-multipart")]:
        try:
            __import__(pkg)
        except ImportError:
            missing.append(name)
    if missing:
        print("=" * 60)
        print("다음 패키지가 없습니다. 설치 후 다시 실행하세요:\n")
        print(f"  pip install {' '.join(missing)}")
        print("=" * 60)
        sys.exit(1)


def open_browser(url: str, delay: float = 1.5):
    time.sleep(delay)
    webbrowser.open(url)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="PointCloud Optimizer Server")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8000)
    parser.add_argument("--no-browser", action="store_true")
    parser.add_argument("--reload", action="store_true", help="Dev hot-reload")
    args = parser.parse_args()

    check_deps()

    url = f"http://{args.host}:{args.port}"
    print(f"\n🚀  PointCloud Optimizer  →  {url}\n")

    if not args.no_browser:
        threading.Thread(target=open_browser, args=(url,), daemon=True).start()

    import uvicorn
    uvicorn.run(
        "backend.app:app",
        host=args.host,
        port=args.port,
        reload=args.reload,
    )
