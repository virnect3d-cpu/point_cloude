# LCC 포맷 역분석 노트 (XGrids PortalCam)

작성: 2026-04-23 · 샘플: `ShinWon_1st_Cutter`

## 0. 파일 구성 (한 씬 단위 디렉토리)

```
<root>/
  <name>.lcc         1,763 B   ← JSON manifest (UTF-8)
  attrs.lcp            580 B   ← scene attrs JSON
  data.bin       318,902,656 B ← 9,965,708 스플랫 × 32 B/레코드 ✅ 확정
  index.bin            840 B   ← 청크 인덱스 (아직 역분석 중)
  environment.bin   91,552 B   ← 환경/배경
  collision.lci  6,429,040 B   ← 충돌 프록시 (magic "coll")
  assets/poses.json           ← 카메라 경로
  thumb.jpg
  log.txt, report.json
```
옵션으로 `mesh-files/<name>.ply` (XGrids 가 함께 내보내는 proxy 메쉬) 동봉.

## 1. `<name>.lcc` — JSON manifest ✅ 확정

(이전 버전과 동일. 생략)

## 2. `attrs.lcp` — scene attrs JSON ✅ 확정

(이전 버전과 동일. 생략)

## 3. `data.bin` — 32B 스플랫 레코드 페이로드

### 3.1 상위 구조 ✅ 확정
- **레코드 크기 = 32 B 고정**. 수학 검증: 318,902,656 B / 9,965,708 splats = **정확히 32.0**.
- LOD 경계는 `manifest.splats[]` 누적합 × 32 B.
  ```
  LOD 0: splats=5,149,480  bytes=[         0 .. 164,783,360)
  LOD 1: splats=2,569,400  bytes=[ 164,783,360 .. 247,004,160)
  LOD 2: splats=1,284,217  bytes=[ 247,004,160 .. 288,099,104)
  LOD 3: splats=  641,905  bytes=[ 288,099,104 .. 308,640,064)
  LOD 4: splats=  320,706  bytes=[ 308,640,064 .. 318,902,656)
  ```

### 3.2 레코드 내부 32B 레이아웃

```
offset  bytes  type              field       status
─────── ───── ───────────────── ─────────── ─────────────
[0..12)  12   float32 LE × 3    position    ✅ 확정 (2000 샘플 전부 scene bbox 내부)
[12..16)  4   u8 × 4            RGBA        ✅ 강한 증거 (4채널 모두 0..255 분포, α 실제 변동)
[16..32) 16   ?                 scale/rot/SH 🔍 미확정 (f16 가설 기각)
```

### 3.3 tail 16B 추가 역분석 TODO
- f16×8 가설: 값이 0 근처 몰리고 |quat|≠1, NaN 3% → **기각**.
- 다음 시도: 
  1. 바이트별 u8 히스토그램 (RGBA8 스타일 인지)
  2. u16×8 가설 (양자화 범위가 palette 인지)
  3. 첫 16B 가 공통 + 마지막 16B 가 per-splat 가 아닐 가능성 (블록 단위 codebook)
  4. XGrids SDK 문서/샘플 확보 시 대조

### 3.4 v2 범위에서 활용 가능한 데이터 ✅
**position + color 만으로 포인트 클라우드 MVP 완성 가능** (사용자 요구: 기존 기능 다운그레이드).
- Python: `tools_lcc_to_ply.py` — LOD 지정해 PLY 바이너리로 추출 (320 K splats → 4.9 MB, 1.2 s)
- C#: `LccSplatDecoder.DecodeLod(scene, lod)` → `Point[] { position, color }`

## 4. `index.bin` — 840 B (추정)

```
0x00  00 00 00 00  D1 13 09 00  00 00 00 00  00 00 00 00
0x10  20 7A 22 01  18 73 04 00  00 65 D2 09  00 00 00 00
```
- `manifest.indexDataSize = 84` × 10 = 840. LOD 당 2 개의 84 B 엔트리라는 가설.
- `data.bin` 의 LOD 경계가 단순 `splats[]` 누적합으로 맞아떨어지므로 index.bin 은 **공간 인덱스** (셀 → splat 범위) 일 가능성 큼.

## 5. `collision.lci` — XGrids 충돌 컨테이너

```
0x00  63 6F 6C 6C         ← magic "coll"
0x04  02 00 00 00         ← version? = 2
0x08  20 01 00 00         ← = 288
0x0C  CD 74 F7 C1  49 BE 16 C2  CA 95 E3 C0   ← (-30.93, -37.69, -7.11)  AABB min
0x18  55 91 21 42  7E 0C 8F 42  18 70 84 41   ← ( 40.39,  71.52, 16.55)  AABB max
0x24  00 00 F0 41  00 00 F0 41  06 00 00 00   ← (30.0, 30.0, 6?)   cellLengthX/Y 일치
...
```
**권장**: v2 1차에서는 `mesh-files/*.ply` 사용 → Unity MeshCollider. `.lci` 완전 파서는 v2.1.

## 6. 다음 작업

- [x] `data.bin` 32B 레코드 가설 검증 → 확정
- [x] position + color PLY 추출기 (Python + C#)
- [ ] tail 16B 역분석 (scale/rot/opacity/SH)
- [ ] `index.bin` 84B×10 가설 검증
- [ ] `collision.lci` 셀 구조
- [ ] XGrids 공식 문서/SDK 입수 시 전체 대조
