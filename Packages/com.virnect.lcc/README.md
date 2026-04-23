# com.virnect.lcc — Virnect LCC Importer (Unity)

XGrids PortalCam `.lcc` 파일을 Unity 에서 바로 쓰기 위한 UPM 패키지.

## 설치

```
Window → Package Manager → + → Add package from git URL
```

```
https://github.com/virnect3d-cpu/point_cloude.git?path=/Packages/com.virnect.lcc#v2-main
```

## 사용 흐름 (예정)

1. Unity Project 창에 `.lcc` 파일 드래그 → `LccScriptedImporter` 가 자동으로 읽어 `LccScene` 에셋 생성
2. 씬에 `LccPointCloudRenderer` 컴포넌트 추가 → LOD 단계 선택
3. 여러 스캔본을 합치려면 `Virnect → LCC Importer` 창에서 `LccScene` 여러 개 등록 → "월드로 인스턴스화"
4. (파이썬 백엔드 연동) 로드된 포인트 클라우드를 PLY 로 내보내 기존 PointCloud Optimizer 파이프라인(메쉬 변환·비교)에 투입

## 현재 상태

**스캐폴드** — 실제 스플랫 디코드·렌더 동작은 구현 대기. `docs/pipeline.md`·`docs/lcc-format.md` 참조.

## 테스트 데이터

`C:/Users/jeongsomin/Desktop/LCC/LCC/lcc-result/ShinWon_1st_Cutter.lcc`
(9.97M splats · 5 LOD · 326 MB)
