using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace RLExcavator
{
  /// <summary>
  /// Lets the demo operator set the active planning control point from the Game view.
  /// The selected point is owned by SimpleTrajectoryPlanner; this script only translates
  /// mouse input into world-space dig/dump point updates.
  /// </summary>
  public class DemoPointPicker : MonoBehaviour
  {
    [Header("References")]
    public SimpleTrajectoryPlanner planner;
    public Camera pickingCamera;

    [Header("Picking")]
    [Tooltip("开启后，鼠标点击 UI 按钮时不会修改挖掘点/倾倒点。")]
    public bool ignoreClicksOverUi = true;

    [Tooltip("优先通过 Physics Raycast 点击平台、地面或可达区域辅助面。")]
    public bool usePhysicsRaycast = true;

    [Tooltip("Raycast 可命中的层。建议给演示地面/规划平面单独设置 Layer 后只选该 Layer。")]
    public LayerMask pickableSurfaceMask = Physics.DefaultRaycastLayers;

    [Tooltip("Raycast 最大距离。")]
    public float maxRayDistance = 200f;

    [Tooltip("没有命中 Collider 时，使用当前编辑点高度处的水平平面作为 fallback。")]
    public bool useSelectedPointPlaneFallback = true;

    [Header("Interaction")]
    [Tooltip("左键按下时设置当前编辑点。")]
    public bool clickToSetPoint = true;

    [Tooltip("左键按住拖拽时持续移动当前编辑点。")]
    public bool dragToMovePoint = true;

    [Tooltip("滚轮调整当前编辑点高度，单位为米。")]
    public float scrollHeightStep = 0.2f;

    [Tooltip("设置点后自动重新生成轨迹。演示时可以开启；调试 RRT* 参数时建议关闭。")]
    public bool autoPlanAfterPointChanged = false;

    public string LastPickSummary { get; private set; } = "尚未点选";

    private bool m_dragging;

    private void Awake()
    {
      ResolveReferences();
    }

    private void Update()
    {
      ResolveReferences();

      if ( planner == null || pickingCamera == null )
        return;

      Mouse mouse = Mouse.current;
      if ( mouse == null )
        return;

      if ( mouse.leftButton.wasReleasedThisFrame )
        m_dragging = false;

      bool pointerOverUi = ignoreClicksOverUi && IsPointerOverUi();
      if ( clickToSetPoint && mouse.leftButton.wasPressedThisFrame && !pointerOverUi ) {
        m_dragging = true;
        TrySetPointFromMouse( mouse );
      }
      else if ( dragToMovePoint && m_dragging && mouse.leftButton.isPressed && !pointerOverUi ) {
        TrySetPointFromMouse( mouse );
      }

      Vector2 scroll = mouse.scroll.ReadValue();
      if ( Mathf.Abs( scroll.y ) > 0.01f && !pointerOverUi )
        MoveSelectedPointHeight( Mathf.Sign( scroll.y ) * Mathf.Max( scrollHeightStep, 0.01f ) );
    }

    private void ResolveReferences()
    {
      if ( planner == null )
        planner = GetComponent<SimpleTrajectoryPlanner>();

      if ( planner == null )
        planner = GetComponentInChildren<SimpleTrajectoryPlanner>( true );

      if ( pickingCamera == null )
        pickingCamera = Camera.main;
    }

    private bool TrySetPointFromMouse( Mouse mouse )
    {
      if ( !TryGetMouseWorldPoint( mouse.position.ReadValue(), out Vector3 worldPoint ) )
        return false;

      if ( !planner.SetSelectedPointWorldPosition( worldPoint ) )
        return false;

      Vector3 displayedPoint = planner.TryGetSelectedPointWorldPosition( out Vector3 selectedPoint )
        ? selectedPoint
        : worldPoint;
      LastPickSummary = $"{planner.EditablePointName} 已更新：{FormatVector( displayedPoint )}";

      if ( autoPlanAfterPointChanged )
        planner.TryPlanConfiguredPath();

      return true;
    }

    private bool TryGetMouseWorldPoint( Vector2 screenPosition, out Vector3 worldPoint )
    {
      Ray ray = pickingCamera.ScreenPointToRay( screenPosition );
      if ( usePhysicsRaycast && Physics.Raycast(
        ray,
        out RaycastHit hit,
        Mathf.Max( maxRayDistance, 0.1f ),
        pickableSurfaceMask,
        QueryTriggerInteraction.Ignore ) ) {
        worldPoint = hit.point;
        return true;
      }

      if ( useSelectedPointPlaneFallback && planner.TryGetSelectedPointWorldPosition( out Vector3 selectedPoint ) ) {
        var plane = new Plane( Vector3.up, selectedPoint );
        if ( plane.Raycast( ray, out float enter ) ) {
          worldPoint = ray.GetPoint( enter );
          return true;
        }
      }

      worldPoint = Vector3.zero;
      LastPickSummary = "点选失败：没有命中可用平面";
      return false;
    }

    private void MoveSelectedPointHeight( float deltaY )
    {
      if ( !planner.TryGetSelectedPointWorldPosition( out Vector3 worldPoint ) )
        return;

      worldPoint.y += deltaY;
      if ( planner.SetSelectedPointWorldPosition( worldPoint ) ) {
        Vector3 displayedPoint = planner.TryGetSelectedPointWorldPosition( out Vector3 selectedPoint )
          ? selectedPoint
          : worldPoint;
        LastPickSummary = $"{planner.EditablePointName} 高度调整：{FormatVector( displayedPoint )}";
        if ( autoPlanAfterPointChanged )
          planner.TryPlanConfiguredPath();
      }
    }

    private static bool IsPointerOverUi()
    {
      return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
    }

    private static string FormatVector( Vector3 value )
    {
      return $"({value.x:0.00}, {value.y:0.00}, {value.z:0.00})";
    }
  }
}
