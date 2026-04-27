using UnityEngine;

// 빈 컴포넌트. [SelectionBase] attribute 로 인해 자식 (_ArasP / __LccCollider) 클릭 시
// Scene View 가 자동으로 이 컴포넌트가 붙은 부모 GameObject 를 선택. E (Rotate) gizmo 로
// 부모를 회전하면 splat + collider 가 함께 움직임.
[SelectionBase]
[AddComponentMenu("Virnect/LCC Selection Base")]
public sealed class LccSelectionBase : MonoBehaviour { }
