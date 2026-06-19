using UnityEngine;

namespace RLExcavator
{
  /// <summary>
  /// 最小轨迹规划器：根据起点和终点生成一条可由 TrajectoryProvider 跟踪的 waypoint 路径。
  /// 第一版只负责接口闭环；后续可在保持输出接口不变的前提下替换为 RRT*。
  /// </summary>
  public class SimpleTrajectoryPlanner : MonoBehaviour
  {
    public enum EditableControlPoint
    {
      Start,
      End
    }

    [Header("References")]
    [Tooltip("接收规划结果的轨迹提供器")]
    public TrajectoryProvider trajectory;

    [Tooltip("规划起点控制点。Demo 中建议绑定一个可见的空物体")]
    public Transform startPoint;

    [Tooltip("规划终点控制点。Demo 中建议绑定一个可见的空物体")]
    public Transform endPoint;

    [Header("Path shape")]
    [Tooltip("生成 waypoint 数量。当前策略训练主要使用 5 个点，demo 第一版建议保持 5")]
    public int waypointCount = 5;

    [Tooltip("起终点距离较远时，按最大段长自动增加 waypoint 数量")]
    public bool fitWaypointCountToDistance = false;

    [Tooltip("自动增加 waypoint 时允许的最大相邻 waypoint 距离")]
    public float maxSegmentLength = 2.0f;

    [Tooltip("自动增加 waypoint 时的最大 waypoint 数量")]
    public int maxWaypointCount = 12;

    [Tooltip("路径中段额外抬高量。0 表示直线插值")]
    public float arcHeight = 0.5f;

    [Tooltip("生成后是否立即写入 TrajectoryProvider；关闭后会排队到下一次 ResetPath 生效")]
    public bool applyImmediately = true;

    [Header("Interactive editing")]
    [Tooltip("当前 UI 正在编辑的规划控制点")]
    public EditableControlPoint editableControlPoint = EditableControlPoint.End;

    [Tooltip("UI 每次移动控制点的距离，单位为 Unity 世界坐标米")]
    public float endpointMoveStep = 0.5f;

    [Tooltip("控制点前后左右移动的参考坐标系。为空时使用当前 planner 物体坐标系")]
    public Transform endpointMoveFrame;

    public Color startPointColor = new Color( 0.25f, 0.85f, 0.35f, 1f );
    public Color endPointColor = new Color( 1f, 0.35f, 0.15f, 1f );
    public float controlPointVisualRadius = 0.22f;

    [Header("Debug")]
    public bool drawGizmos = true;
    public Color previewColor = new Color( 0.9f, 0.5f, 0.1f, 1f );
    public float previewRadius = 0.08f;

    private void Start()
    {
      EnsureControlPointVisuals();
    }

    [ContextMenu("Plan Configured Path")]
    public void PlanConfiguredPath()
    {
      TryPlanConfiguredPath();
    }

    public bool TryPlanConfiguredPath()
    {
      if ( !TryGetConfiguredEndpoints( out Vector3 start, out Vector3 goal, true ) )
        return false;

      Plan( start, goal );
      return true;
    }

    public void SelectStartPoint()
    {
      editableControlPoint = EditableControlPoint.Start;
    }

    public void SelectEndPoint()
    {
      editableControlPoint = EditableControlPoint.End;
    }

    public void MoveSelectedPointForward()
    {
      MoveSelectedPoint( Vector3.forward );
    }

    public void MoveSelectedPointBackward()
    {
      MoveSelectedPoint( Vector3.back );
    }

    public void MoveSelectedPointLeft()
    {
      MoveSelectedPoint( Vector3.left );
    }

    public void MoveSelectedPointRight()
    {
      MoveSelectedPoint( Vector3.right );
    }

    public void MoveSelectedPointUp()
    {
      MoveSelectedPoint( Vector3.up );
    }

    public void MoveSelectedPointDown()
    {
      MoveSelectedPoint( Vector3.down );
    }

    public void MoveSelectedPoint( Vector3 localDirection )
    {
      MoveControlPoint( SelectedControlPointTransform(), localDirection );
    }

    private void MoveControlPoint( Transform point, Vector3 localDirection )
    {
      if ( point == null ) {
        Debug.LogWarning( "[SimpleTrajectoryPlanner] Missing editable control point reference." );
        return;
      }

      if ( localDirection.sqrMagnitude < 0.0001f )
        return;

      Transform moveFrame = endpointMoveFrame != null ? endpointMoveFrame : transform;
      Vector3 worldDirection = moveFrame.TransformDirection( localDirection.normalized );
      point.position += worldDirection * Mathf.Max( endpointMoveStep, 0.01f );
    }

    public void Plan( Vector3 startWorld, Vector3 goalWorld )
    {
      Vector3[] path = GeneratePath( startWorld, goalWorld );
      if ( trajectory == null ) {
        Debug.LogWarning( "[SimpleTrajectoryPlanner] Missing TrajectoryProvider reference." );
        return;
      }

      if ( applyImmediately )
        trajectory.SetPlannedPath( path );
      else
        trajectory.QueuePlannedPath( path );
    }

    public Vector3[] GeneratePath( Vector3 startWorld, Vector3 goalWorld )
    {
      int count = PlannedWaypointCount( startWorld, goalWorld );
      Vector3[] path = new Vector3[ count ];

      for ( int i = 0; i < count; i++ ) {
        float t = count <= 1 ? 1f : (float)i / ( count - 1 );
        Vector3 point = Vector3.Lerp( startWorld, goalWorld, t );
        point.y += Mathf.Sin( Mathf.PI * t ) * Mathf.Max( arcHeight, 0f );
        path[ i ] = point;
      }

      return path;
    }

    private int PlannedWaypointCount( Vector3 startWorld, Vector3 goalWorld )
    {
      int count = Mathf.Max( waypointCount, 2 );
      if ( !fitWaypointCountToDistance )
        return count;

      float distance = Vector3.Distance( startWorld, goalWorld );
      float segmentLength = Mathf.Max( maxSegmentLength, 0.1f );
      int distanceBasedCount = Mathf.CeilToInt( distance / segmentLength ) + 1;
      return Mathf.Clamp( Mathf.Max( count, distanceBasedCount ), 2, Mathf.Max( maxWaypointCount, 2 ) );
    }

    public bool TryGetStartPoint( out Vector3 start )
    {
      if ( startPoint == null ) {
        start = Vector3.zero;
        return false;
      }

      start = startPoint.position;
      return true;
    }

    public bool TryGetEndPoint( out Vector3 goal )
    {
      if ( endPoint == null ) {
        goal = Vector3.zero;
        return false;
      }

      goal = endPoint.position;
      return true;
    }

    private bool TryGetConfiguredEndpoints( out Vector3 start, out Vector3 goal, bool warnIfMissing )
    {
      if ( !TryGetStartPoint( out start ) ) {
        if ( warnIfMissing )
          Debug.LogWarning( "[SimpleTrajectoryPlanner] Missing startPoint reference." );
        goal = Vector3.zero;
        return false;
      }

      if ( !TryGetEndPoint( out goal ) ) {
        if ( warnIfMissing )
          Debug.LogWarning( "[SimpleTrajectoryPlanner] Missing endPoint reference." );
        return false;
      }

      return true;
    }

    public string EditablePointName => editableControlPoint == EditableControlPoint.Start ? "Start" : "End";

    private Transform SelectedControlPointTransform()
    {
      return editableControlPoint == EditableControlPoint.Start ? startPoint : endPoint;
    }

    private void EnsureControlPointVisuals()
    {
      EnsureControlPointVisual( startPoint, startPointColor );
      EnsureControlPointVisual( endPoint, endPointColor );
    }

    private void EnsureControlPointVisual( Transform point, Color color )
    {
      if ( point == null )
        return;

      var visual = point.GetComponent<TargetPointVisual>();
      if ( visual == null )
        visual = point.gameObject.AddComponent<TargetPointVisual>();

      visual.SetStyle( color, controlPointVisualRadius );
      visual.SetVisible( true );
    }

    private void OnDrawGizmosSelected()
    {
      if ( !drawGizmos || !TryGetConfiguredEndpoints( out Vector3 start, out Vector3 goal, false ) )
        return;

      Vector3[] path = GeneratePath( start, goal );
      Gizmos.color = previewColor;
      for ( int i = 0; i < path.Length; i++ ) {
        Gizmos.DrawWireSphere( path[ i ], previewRadius );
        if ( i + 1 < path.Length )
          Gizmos.DrawLine( path[ i ], path[ i + 1 ] );
      }
    }
  }
}
