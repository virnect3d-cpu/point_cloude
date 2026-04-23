# LCC 포맷 역분석 노트 (XGrids PortalCam)

작성: 2026-04-23 · 샘플: `ShinWon_1st_Cutter`

## 0. 파일 구성 (한 씬 단위 디렉토리)

```
<root>/
  <name>.lcc         1,763 B   ← JSON manifest (UTF-8)
  attrs.lcp            580 B   ← scene attrs JSON
  data.bin       318,902,656 B ← 5단계 LOD 스플랫 데이터 (COMPRESS)
  index.bin            840 B   ← 청크 인덱스
  environment.bin   91,552 B   ← 환경/배경
  collision.lci  6,429,040 B   ← 충돌 프록시 (magic "coll")
  assets/poses.json           ← 카메라 경로
  thumb.jpg
  log.txt, report.json
```
옵션으로 `mesh-files/<name>.ply` (XGrids 가 함께 내보내는 proxy 메쉬) 동봉.

## 1. `<name>.lcc` — JSON manifest (확정)

```json
{
  "version": "5.0",
  "source": "lcc",
  "dataType": "PortalCam",
  "totalSplats": 9965708,
  "totalLevel": 5,
  "cellLengthX": 30, "cellLengthY": 30,
  "indexDataSize": 84,
  "offset":  [0, 0, 0],
  "epsg": 0,
  "shift":   [0, 0, 0],
  "scale":   [1, 1, 1],
  "splats":  [5149480, 2569400, 1284217, 641905, 320706],
  "boundingBox": { "min": [-30.93, -37.69, -7.11], "max": [40.39, 71.52, 16.55] },
  "encoding": "COMPRESS",
  "fileType": "Portable",
  "attributes": [
    { "name":"position", "min":[-639.46, -610.04, -319.75], "max":[675.92, 658.11, 461.34] },
    { "name":"normal",   "min":[0,0,0], "max":[0,0,0] },
    { "name":"color",    "min":[0,0,0], "max":[1,1,1] },
    { "name":"scale",    ... },
    { "name":"rotation", ... },
    { "name":"opacity",  ... },
    { "name":"sh",       ... }
  ]
}
```

position 의 bbox(±600) 가 실제 씬 bbox(±70) 보다 훨씬 큰 것은 **양자화 범위** 를 의미하는 것으로 보임 — `COMPRESS` 모드에서 position 은 `[min, max]` 구간을 정수로 양자화해 저장.

## 2. `attrs.lcp` — scene attrs JSON (확정)

```json
{
  "version":"1.0.1",
  "name":"ShinWon_1st_Cutter",
  "guid":"6ph7x-18992750642218307",
  "spawnPoint": { "position":[0,0,1.6], "rotation":[0.707,0,0,0.707] },
  "transform":  { "position":[0,0,0], "rotation":[0,0,0,1], "scale":[1,1,1] },
  "poses":      { "path":"assets/poses.json" },
  "collider":   { "simpleMesh": { "type":"ply", "path":"collision.lci" } },
  ...
}
```
> `collider.simpleMesh.type` 이 "ply" 로 찍혀 있지만 실제 `collision.lci` 는 PLY 가 아니라 XGrids 자체 컨테이너(아래). 라벨과 실제가 불일치.

## 3. `data.bin` — 318 MB 스플랫 페이로드 (역분석 중)

첫 112 바이트 덤프:
```
offs=0x00  72 F8 57 BF  9E AC 04 C1  F6 F7 94 BF  3D 37 3C D4  → Float32 LE ≈ (-0.843, -8.292, -1.163, *)
offs=0x10  08 00 D5 00  D1 03 F7 71  B9 E6 00 00  00 00 00 00  → 16 B 꼬리 (tag? flags?)
offs=0x20  0F AE A0 C0  74 48 18 C1  E2 1D 65 BD  9D A9 AA 3F  → Float32 ≈ (-5.022, -9.518, -0.056, 1.333)
offs=0x30  6B 00 30 00  11 01 FD A5  7A CF 00 00  00 00 00 00
offs=0x40  36 85 F7 C0  95 4C 20 C1  CF 86 47 BD  00 00 00 1D  → Float32 ≈ (-7.735, -10.019, -0.049, *)
```

관찰:
- **32바이트 반복 패턴**: `float3 position` (12B) + `uint32 색상 또는 플래그` (4B) + `16B 꼬리` (스케일/회전/SH 양자화?)
- 각 레코드의 float3 가 전부 manifest.attributes.position 범위([-640, 676]) 에 떨어짐 → 이것이 양자화 **전** 또는 **부분복원** 값일 수 있음
- 32 B × 9,965,708 ≈ 319 MB → 총용량과 거의 일치. **현재 최유력 가설: 32 B/splat 고정 레코드 × 전체 스플랫 수, LOD 마다 splats[i] 개만큼 연속**

남은 역분석 과제:
1. 32 B 꼬리의 바이트 해석 (rotation quaternion 양자화 + opacity + SH 0차/1차)
2. LOD 간 경계를 `splats[]` 누적합으로 그냥 자르는지 vs `index.bin` 에 위치가 있는지 확인
3. `COMPRESS` 가 단순 고정길이인지 VLC 인지 — 총용량 일치로 보아 **고정길이 양자화** 쪽 가능성 큼

## 4. `index.bin` — 840 B (추정)

```
offs=0x00  00 00 00 00  D1 13 09 00  00 00 00 00  00 00 00 00
offs=0x10  20 7A 22 01  18 73 04 00  00 65 D2 09  00 00 00 00
```
- 840 B = 5 LOD 만 있다면 LOD 당 168 B 블록?
- `indexDataSize: 84` (manifest 값) × 10 = 840. → **LOD 당 2 개의 84 B 엔트리** 라는 가설 (예: 시작/끝 offset·byte·splat count).

## 5. `collision.lci` — XGrids 충돌 컨테이너

```
offs=0x00  63 6F 6C 6C        ← magic "coll"
offs=0x04  02 00 00 00        ← version? = 2
offs=0x08  20 01 00 00        ← 288?
offs=0x0C  CD 74 F7 C1  49 BE 16 C2  CA 95 E3 C0  ← Float32×3 (AABB min?) ≈ (-30.93, -37.69, -7.11)
offs=0x18  55 91 21 42  7E 0C 8F 42  18 70 84 41  ← Float32×3 (AABB max?) ≈ (40.39, 71.52, 16.55)
offs=0x24  00 00 F0 41  00 00 F0 41  06 00 00 00  ← (30.0, 30.0, 6?)  — cellLength X·Y 와 일치
offs=0x30  ...
```
→ `boundingBox` 와 `cellLengthX/Y` 가 들어간 헤더임이 거의 확실. 이후는 셀 단위 충돌 프리미티브 블록.

**권장**: v2 1차 출시에서는 `collision.lci` 대신 **`mesh-files/*.ply`** 를 써서 Unity MeshCollider 구성. `.lci` 완전 파서는 v2.1 이후.

## 6. 다음 작업

- [ ] `data.bin` 32B 레코드 가설 검증 — 처음 1000개 레코드를 CSV 로 덤프해 manifest bbox 안에 떨어지는지 확인
- [ ] `index.bin` 84B 엔트리 10개를 uint32 LE 로 파싱해 합이 파일 크기와 일치하는지
- [ ] `collision.lci` 헤더 32 B 이후 루프 구조 확인 (셀 수 × 셀 데이터)
- [ ] `assets/poses.json` 스키마 문서화
