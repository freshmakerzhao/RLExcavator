using UnityEngine;

namespace RLExcavator
{
  /// <summary>
  /// Provides a simple waypoint path for bucket-tip trajectory tracking.
  /// Use one waypoint for point reaching, or multiple waypoints for path following.
  /// </summary>
  public class TrajectoryProvider : MonoBehaviour
  {
    [Header("Path")]
    [Tooltip("目标路径点。第一版可以只放一个 TargetPoint。")]
    public Transform[] waypoints;
    [Tooltip("waypoints 为空时使用的局部目标点。")]
    public Vector3 fallbackLocalTarget = new Vector3( 0f, 0f, 2f );

    [Header("Debug visual")]
    public bool drawGizmos = true;
    public Color pathColor = new Color( 0.1f, 0.8f, 1f, 1f );
    public Color currentTargetColor = new Color( 1f, 0.85f, 0.1f, 1f );
    public float gizmoRadius = 0.12f;
    [Tooltip("Play 时自动给 waypoint 添加 Game 视图可见的小球标记。")]
    public bool addRuntimeVisuals = true;
    public float runtimeVisualRadius = 0.18f;

    public int CurrentIndex { get; private set; } = 0;

    /// <summary>当前目标点的 Transform（用于获取 TargetPointVisual 组件）</summary>
    public Transform CurrentTargetTransform
    {
      get
      {
        if ( waypoints != null && waypoints.Length > 0 ) {
          int index = Mathf.Clamp( CurrentIndex, 0, waypoints.Length - 1 );
          return waypoints[ index ];
        }
        return m_fallbackVisualTarget;
      }
    }

    private Transform m_fallbackVisualTarget = null;

    private void Start()
    {
      if ( addRuntimeVisuals )
        EnsureRuntimeVisuals();
    }

    public int TargetCount => waypoints != null && waypoints.Length > 0 ? waypoints.Length : 1;

    public Vector3 CurrentTargetPosition
    {
      get
      {
        if ( waypoints != null && waypoints.Length > 0 ) {
          int index = Mathf.Clamp( CurrentIndex, 0, waypoints.Length - 1 );
          if ( waypoints[ index ] != null )
            return waypoints[ index ].position;
        }

        return transform.TransformPoint( fallbackLocalTarget );
      }
    }

    public float Progress
    {
      get
      {
        int count = TargetCount;
        if ( count <= 1 )
          return 0f;

        return Mathf.Clamp01( (float)CurrentIndex / ( count - 1 ) );
      }
    }

    public void ResetPath()
    {
      CurrentIndex = 0;
    }

    /// <summary>
    /// Advances to the next waypoint.
    /// Returns true when the path has been completed.
    /// </summary>
    public bool AdvanceTarget()
    {
      if ( CurrentIndex >= TargetCount - 1 )
        return true;

      CurrentIndex++;
      return false;
    }

    private Vector3 GetTargetPositionForGizmo( int index )
    {
      if ( waypoints != null && index >= 0 && index < waypoints.Length && waypoints[ index ] != null )
        return waypoints[ index ].position;

      return transform.TransformPoint( fallbackLocalTarget );
    }

    private void EnsureRuntimeVisuals()
    {
      if ( waypoints != null && waypoints.Length > 0 ) {
        foreach ( var waypoint in waypoints )
          AddVisualTo( waypoint );
        return;
      }

      if ( m_fallbackVisualTarget == null ) {
        var targetObject = new GameObject( "FallbackTargetPoint" );
        targetObject.transform.SetParent( transform, false );
        targetObject.transform.localPosition = fallbackLocalTarget;
        m_fallbackVisualTarget = targetObject.transform;
      }

      AddVisualTo( m_fallbackVisualTarget );
    }

    private void AddVisualTo( Transform target )
    {
      if ( target == null )
        return;

      var visual = target.GetComponent<TargetPointVisual>();
      if ( visual == null )
        visual = target.gameObject.AddComponent<TargetPointVisual>();

      visual.color = currentTargetColor;
      visual.radius = runtimeVisualRadius;
    }

    private void OnDrawGizmos()
    {
      if ( !drawGizmos )
        return;

      int count = TargetCount;
      Gizmos.color = pathColor;
      for ( int i = 0; i < count; i++ ) {
        Vector3 p = GetTargetPositionForGizmo( i );
        Gizmos.DrawWireSphere( p, gizmoRadius );

        if ( i + 1 < count )
          Gizmos.DrawLine( p, GetTargetPositionForGizmo( i + 1 ) );
      }

      Gizmos.color = currentTargetColor;
      Gizmos.DrawSphere( CurrentTargetPosition, gizmoRadius * 1.25f );
    }
  }
}
