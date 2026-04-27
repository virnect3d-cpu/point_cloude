# LCC 정합(Alignment) 이슈 정리

`Splat_ShinWon_*` (LCC 임포트로 들어온 GaussianSplat 객체) 들의 위치/스케일/회전을 잡으면서 부딪힌 문제들과, 왜 지금은 **수동 보정이 답**이고 자동화가 까다로운지 정리.

## 1. 현재 박힌 값 (Scene5_LccRotate)

5개 Splat 모두 `position = (0, 0, 0)`, `localScale = (-1, 1, 1)`, `LccInteractiveRotator.enabled = false`.
회전(quaternion)은 객체별로 다르며 시각적으로 맞춰진 값으로 고정 — 다음 표는 baseline 기록용.

| GameObject | localRotation (xyzw) | EulerHint |
|---|---|---|
| `Splat_ShinWon_1st_Cutter` | (0.124, -0.701, 0.691, 0.130) | (89.07, -138.91, 20.45) |
| `Splat_ShinWon_Facility_01` | (0.711, -0.028, 0.027, 0.702) | (90.78, 0, 4.46) |
| `Splat_ShinWon_Facility_02` | (-0.707, 0.0036, 0.0048, 0.707) | (-90.10, 180, -179.23) |
| `Splat_ShinWon_Facility_03` | (-0.712, -0.00053, -0.00126, 0.702) | (-89.23, -184.53, 184.38) |
| `Splat_ShinWon_Facility_Middle` | (0.707, 0.0125, -0.0125, 0.707) | (90, 0, -2.03) |

> 회전 값은 LCC 원본 좌표계 + 시각 보정의 합으로 결정된 거라 **객체마다 다름**. 단일 회전식으로 일반화 불가.

## 2. 발견한 이슈 (root cause 별)

### 2-1. 좌우 반전 (X-flip)
- **현상**: LCC 임포트 직후 splat이 좌/우가 거울처럼 뒤집힌 상태로 보임.
- **임시 보정**: `splatGO.transform.localScale = (-1, 1, 1)`
- **위치**: `LccDropAutoImporter._AutoSpawnInActiveScene()` — `ForceHorizontalFlipOnImport` (default `true`).
- **파급**: `lossyScale.x = -1`이 자식(`__LccCollider`)에도 전파 → MeshCollider winding 뒤집힘.

### 2-2. LccInteractiveRotator의 우발 회전
- **현상**: Edit 모드에서 Scene View에 좌클릭+드래그 한 번 → 5개 객체 회전이 같이 돌아감 → 작업 도중 모르게 박힘.
- **원인**: `Assets/LccInteractiveRotator.cs`의 `Update()`가 `editorOnlyMode=true`일 때 Edit 모드에서만 동작. 씬에 박힌 컴포넌트 인스턴스의 `requireSelection = false`라 셀렉션 무시하고 모든 인스턴스가 동시 회전.
- **임시 대응**: 5개 모두 `enabled = false`로 끔.
- **재발 방지**: 새 LCC import 시도 컴포넌트 default가 `requireSelection=true`이지만, 이미 씬에 박힌 인스턴스는 별개이므로 **씬 단위로 점검 필요**.

### 2-3. position 변동 호소 ("1개 움직이면 다 변경됨")
- **실제 원인**: 위 2-2 — Rotator가 "회전"을 시키지만, 회전 후 splat 콘텐츠의 world 위치가 달라지므로 시각상 "위치가 움직인 것"처럼 보임.
- **현재 처방**: `position = (0,0,0)` + Rotator off 조합으로 셋팅 고정.

### 2-4. lossyScale 음수 + MeshCollider
- **잠재 이슈**: `__LccCollider`의 MeshCollider가 음수 스케일에서 winding/노멀 반전 → 충돌 한쪽 면 통과/실패 가능.
- **검증 필요**: 플레이 모드에서 V-Bot을 5개 모두에 충돌시켜 확인.
- **대안**: 콜라이더는 플립 미적용 + 시각만 플립 (현재는 부모 통째로 플립).

## 3. 왜 지금은 수동이 답인가

| 항목 | 자동화 못 하는 이유 |
|---|---|
| 좌우 플립 | LCC 변환 파이프라인 자체의 좌표계 버그라 객체별로 일관됨 → `ForceHorizontalFlipOnImport`로 일부 자동화. 단, 변환 쪽이 고쳐지면 **거꾸로** 박힐 수 있어 영구 자동화 불가. |
| 회전 | LCC 캡처 단계의 카메라/디바이스 자세에 따라 원본 좌표계가 매번 달라짐. 객체별 1회성 시각 매칭 필요. |
| 위치 | 씬의 의미적 배치 (어디에 둘지) 는 디자이너 결정. 자동 산출 불가. |
| 스케일 | 보통 1, 단 X-flip 보정으로만 -1 사용. |

## 4. 왜 수동이 어려운가

1. **5개 동일 작업 반복** — 멀티 셀렉트 일괄 transform 도구 부재. 한 객체씩 Inspector에서 클릭.
2. **LccInteractiveRotator의 함정** — 작업 중 무심코 Scene View 클릭하면 회전이 바뀜. 셀렉션 무관 동작.
3. **Quaternion 직접 편집 부담** — Scene YAML은 텍스트지만 quaternion 값 4개를 손으로 박는 건 위험. EulerHint 도 표시값이라 연결이 약함.
4. **Undo 스택 단절** — MCP/스크립트 경유 transform 변경은 Unity Undo 스택에 안 잡힘. 실수 복구 어려움.
5. **콜라이더 정합 분리** — Splat 시각(Aras-P)과 collider(`__LccCollider`)가 다른 자식이라 부모 transform 변경 시 양쪽 동기화 필요. 부모 스케일 음수 시 콜라이더 winding 따로 신경 써야 함.
6. **GaussianSplat 내부 좌표** — `_ArasP.localRotation = Inverse(parent.rotation)` 패턴으로 부모 회전을 상쇄하는 구조. 부모/자식 둘 다 봐야 시각 결과 예측 가능.
7. **Scene View 좌표계 시각화 부재** — 회전 박힌 후 "원본 회전이 뭐였는지" 알 길 없음 (git blame해야 됨).

## 5. 향후 개선 방향 (자동화 가능한 부분만)

- **LCC 변환기 측 X-flip 옵션 노출** — Unity 후처리 의존 제거. 변환기 옵션이 권위.
- **`LccTransformBaseline` 컴포넌트** — 임포트 시 초기 회전을 `[SerializeField] Quaternion _baseline` 으로 박아두고, "Reset to baseline" 메뉴 제공. 우발 회전 복구 1클릭.
- **씬 단위 일괄 도구** — `Tools/LCC/Reset Splat Transforms` 메뉴: 선택된 Splat root들에 `position=0, scale.x=±1` 표준 적용.
- **`LccInteractiveRotator` 인스턴스 강제 안전화** — 씬 로드 시 모든 인스턴스의 `requireSelection = true` 강제 + 또는 컴포넌트 자체 제거 후 별도 디버그 윈도우로 이동.
- **MeshCollider 정합 검증 테스트** — PlayMode 자동 테스트로 5개 객체에 ray 충돌 검증.
- **LCC import 메타** — per-asset `LccImportSettings` ScriptableObject로 객체별 회전/플립 옵션 박기 (ProxyMesh.asset과 같은 위치).

## 6. 관련 커밋

- `89dcde1` — Scene5 5개 Splat 좌우플립 + position 0 + Rotator off
- `28db855` — `LccDropForge.ForceHorizontalFlipOnImport` 옵션 추가
