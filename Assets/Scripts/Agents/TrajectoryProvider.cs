using System.Collections.Generic;
using UnityEngine;

namespace RLExcavator
{
  /// <summary>
  /// 为 TrajectoryAgent 提供单点目标或多点路径，并同步维护路径点调试可视化。
  /// </summary>
  public class TrajectoryProvider : MonoBehaviour
  {
    public enum RandomPathMode
    {
      Independent,
      Trend
    }

    [Header("Path")]
    [Tooltip("目标路径点。可以只放一个点，也可以放一条完整路径。")]
    public Transform[] waypoints;
    [Tooltip("waypoints 为空时使用的局部目标点。")]
    public Vector3 fallbackLocalTarget = new Vector3( 0f, 0f, 2f );

    [Header("Random path")]
    [Tooltip("每个 episode 重置时自动生成随机 waypoint 路径，用于 Spinelli-style waypoint-following 训练。")]
    public bool randomizePathOnReset = false;
    [Tooltip("随机路径点数量。论文 waypoint-following 训练使用 5 个点，部署时可跟踪更长路径。")]
    public int randomWaypointCount = 5;
    [Tooltip("随机路径生成模式。Trend 会围绕 TrajectoryGroup 中的基准路径加扰动；Independent 会生成更分散的随机点。")]
    public RandomPathMode randomPathMode = RandomPathMode.Trend;
    [Tooltip("Independent 模式下随机路径点的局部坐标最小值。")]
    public Vector3 randomLocalMin = new Vector3( -4f, 0.5f, -2f );
    [Tooltip("Independent 模式下随机路径点的局部坐标最大值。")]
    public Vector3 randomLocalMax = new Vector3( 4f, 4.5f, 5.5f );
    [Tooltip("Independent 模式下相邻 waypoint 的最小推进距离。")]
    public float randomMinSegmentLength = 1.0f;
    [Tooltip("Independent 模式下相邻随机 waypoint 的最大局部距离，避免生成折返太急的路径。")]
    public float randomMaxSegmentLength = 2.4f;
    [Tooltip("Trend 模式下基准路径点在水平面 X/Z 方向的随机扰动。")]
    public float trendLateralJitter = 0.8f;
    [Tooltip("Trend 模式下基准路径点在高度 Y 方向的随机扰动。")]
    public float trendVerticalJitter = 0.45f;

    [Header("Debug visual")]
    public bool drawGizmos = true;
    [Tooltip("路径连线颜色。")]
    public Color pathColor = new Color( 0.1f, 0.8f, 1f, 1f );
    [Tooltip("当前正在跟踪的路径点颜色。")]
    public Color currentTargetColor = new Color( 1f, 0.85f, 0.1f, 1f );
    [Tooltip("尚未到达的路径点颜色。")]
    public Color pendingTargetColor = new Color( 0.25f, 0.65f, 1f, 1f );
    [Tooltip("已经到达的路径点颜色。")]
    public Color completedTargetColor = new Color( 0.25f, 0.85f, 0.35f, 1f );
    public float gizmoRadius = 0.12f;
    [Tooltip("Play 时自动给 waypoint 添加 Game 视图可见的小球标记。")]
    public bool addRuntimeVisuals = true;
    public float runtimeVisualRadius = 0.18f;

    public int CurrentIndex { get; private set; } = 0;
    public bool PathCompleted { get; private set; } = false;

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
    private Vector3[] m_baseLocalWaypoints = null;

    private void Start()
    {
      CaptureBaseWaypointsIfNeeded( waypoints != null ? waypoints.Length : 0 );

      if ( addRuntimeVisuals )
        EnsureRuntimeVisuals();

      RefreshWaypointVisuals();
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

    public Vector3 GetWaypointPosition( int index )
    {
      if ( waypoints != null && waypoints.Length > 0 ) {
        int clamped = Mathf.Clamp( index, 0, waypoints.Length - 1 );
        if ( waypoints[ clamped ] != null )
          return waypoints[ clamped ].position;
      }

      return transform.TransformPoint( fallbackLocalTarget );
    }

    public Transform GetWaypointTransform( int index )
    {
      return GetTargetTransform( index );
    }

    public float Progress
    {
      get
      {
        int count = TargetCount;
        if ( PathCompleted )
          return 1f;

        if ( count <= 1 )
          return 0f;

        return Mathf.Clamp01( (float)CurrentIndex / ( count - 1 ) );
      }
    }

    public void ResetPath()
    {
      if ( randomizePathOnReset )
        GenerateRandomPath();

      CurrentIndex = 0;
      PathCompleted = false;
      RefreshWaypointVisuals();
    }

    /// <summary>前进到下一个路径点；返回 true 表示整条路径已经完成。</summary>
    public bool AdvanceTarget()
    {
      if ( CurrentIndex >= TargetCount - 1 ) {
        PathCompleted = true;
        RefreshWaypointVisuals();
        return true;
      }

      CurrentIndex++;
      RefreshWaypointVisuals();
      return false;
    }

    private Vector3 GetTargetPositionForGizmo( int index )
    {
      return GetWaypointPosition( index );
    }

    private void GenerateRandomPath()
    {
      int count = Mathf.Max( randomWaypointCount, 1 );
      EnsureWaypointArray( count );

      if ( randomPathMode == RandomPathMode.Trend ) {
        GenerateTrendPath( count );
        return;
      }

      Vector3 previous = RandomPathPoint();
      for ( int i = 0; i < count; i++ ) {
        Vector3 candidate = i == 0 ? previous : RandomPathPointNear( previous );
        previous = candidate;

        if ( waypoints[ i ] != null )
          waypoints[ i ].localPosition = candidate;
      }
    }

    private void GenerateTrendPath( int count )
    {
      CaptureBaseWaypointsIfNeeded( count );

      for ( int i = 0; i < count; i++ ) {
        Vector3 basePoint = i < m_baseLocalWaypoints.Length ? m_baseLocalWaypoints[ i ] : fallbackLocalTarget;
        float endpointScale = i == 0 || i == count - 1 ? 0.5f : 1f;
        Vector3 jitter = new Vector3(
          Random.Range( -trendLateralJitter, trendLateralJitter ) * endpointScale,
          Random.Range( -trendVerticalJitter, trendVerticalJitter ) * endpointScale,
          Random.Range( -trendLateralJitter, trendLateralJitter ) * endpointScale
        );

        if ( waypoints[ i ] != null )
          waypoints[ i ].localPosition = basePoint + jitter;
      }
    }

    private void CaptureBaseWaypointsIfNeeded( int count )
    {
      if ( count <= 0 )
        return;

      if ( m_baseLocalWaypoints != null && m_baseLocalWaypoints.Length == count )
        return;

      m_baseLocalWaypoints = new Vector3[ count ];
      for ( int i = 0; i < count; i++ ) {
        Transform waypoint = waypoints != null && i < waypoints.Length ? waypoints[ i ] : null;
        m_baseLocalWaypoints[ i ] = waypoint != null ? waypoint.localPosition : fallbackLocalTarget;
      }
    }

    private void EnsureWaypointArray( int count )
    {
      if ( waypoints == null )
        waypoints = new Transform[ 0 ];

      var used = new HashSet<Transform>();

      if ( waypoints.Length == count ) {
        for ( int i = 0; i < waypoints.Length; i++ )
          if ( !IsValidOwnedWaypoint( waypoints[ i ], used ) )
            waypoints[ i ] = CreateWaypointObject( i );
        return;
      }

      var next = new Transform[ count ];
      for ( int i = 0; i < count; i++ ) {
        if ( i < waypoints.Length && IsValidOwnedWaypoint( waypoints[ i ], used ) )
          next[ i ] = waypoints[ i ];
        else
          next[ i ] = CreateWaypointObject( i );
      }
      waypoints = next;
    }

    private bool IsValidOwnedWaypoint( Transform waypoint, HashSet<Transform> used )
    {
      if ( waypoint == null )
        return false;

      if ( !waypoint.IsChildOf( transform ) )
        return false;

      return used.Add( waypoint );
    }

    private Transform CreateWaypointObject( int index )
    {
      var targetObject = new GameObject( $"TargetPoint_{index}" );
      targetObject.transform.SetParent( transform, false );
      AddVisualTo( targetObject.transform );
      return targetObject.transform;
    }

    private Vector3 RandomPathPoint()
    {
      return new Vector3(
        Random.Range( randomLocalMin.x, randomLocalMax.x ),
        Random.Range( randomLocalMin.y, randomLocalMax.y ),
        Random.Range( randomLocalMin.z, randomLocalMax.z )
      );
    }

    private Vector3 RandomPathPointNear( Vector3 previous )
    {
      Vector3 candidate = RandomPathPoint();
      float minLength = Mathf.Max( randomMinSegmentLength, 0f );
      Vector3 delta = candidate - previous;

      for ( int i = 0; i < 8 && delta.magnitude < minLength; i++ ) {
        candidate = RandomPathPoint();
        delta = candidate - previous;
      }

      float maxLength = Mathf.Max( randomMaxSegmentLength, 0.1f );
      if ( delta.magnitude > maxLength )
        candidate = previous + delta.normalized * maxLength;

      return ClampPathPoint( candidate );
    }

    private Vector3 ClampPathPoint( Vector3 value )
    {
      return new Vector3(
        Mathf.Clamp( value.x, randomLocalMin.x, randomLocalMax.x ),
        Mathf.Clamp( value.y, randomLocalMin.y, randomLocalMax.y ),
        Mathf.Clamp( value.z, randomLocalMin.z, randomLocalMax.z )
      );
    }

    private void EnsureRuntimeVisuals()
    {
      if ( waypoints != null && waypoints.Length > 0 ) {
        foreach ( var waypoint in waypoints )
          AddVisualTo( waypoint );
      }
      else if ( m_fallbackVisualTarget == null ) {
        var targetObject = new GameObject( "FallbackTargetPoint" );
        targetObject.transform.SetParent( transform, false );
        targetObject.transform.localPosition = fallbackLocalTarget;
        m_fallbackVisualTarget = targetObject.transform;
        AddVisualTo( m_fallbackVisualTarget );
      }

      RefreshWaypointVisuals();
    }

    private void AddVisualTo( Transform target )
    {
      if ( target == null )
        return;

      var visual = target.GetComponent<TargetPointVisual>();
      if ( visual == null )
        visual = target.gameObject.AddComponent<TargetPointVisual>();

      visual.SetStyle( pendingTargetColor, runtimeVisualRadius );
    }

    private void RefreshWaypointVisuals()
    {
      int count = TargetCount;
      for ( int i = 0; i < count; i++ ) {
        Transform target = GetTargetTransform( i );
        if ( target == null )
          continue;

        var visual = target.GetComponent<TargetPointVisual>();
        if ( visual == null && addRuntimeVisuals )
          visual = target.gameObject.AddComponent<TargetPointVisual>();

        if ( visual == null )
          continue;

        Color color = TargetColorForIndex( i );
        float radius = i == CurrentIndex && !PathCompleted ? runtimeVisualRadius * 1.25f : runtimeVisualRadius;
        visual.SetStyle( color, radius );
      }
    }

    private Color TargetColorForIndex( int index )
    {
      if ( PathCompleted || index < CurrentIndex )
        return completedTargetColor;

      if ( index == CurrentIndex )
        return currentTargetColor;

      return pendingTargetColor;
    }

    private Transform GetTargetTransform( int index )
    {
      if ( waypoints != null && waypoints.Length > 0 && index >= 0 && index < waypoints.Length )
        return waypoints[ index ];

      return m_fallbackVisualTarget;
    }

    private void OnDrawGizmos()
    {
      if ( !drawGizmos )
        return;

      int count = TargetCount;
      for ( int i = 0; i < count; i++ ) {
        Vector3 p = GetTargetPositionForGizmo( i );
        Gizmos.color = TargetColorForIndex( i );
        Gizmos.DrawWireSphere( p, gizmoRadius );

        if ( i + 1 < count ) {
          Gizmos.color = pathColor;
          Gizmos.DrawLine( p, GetTargetPositionForGizmo( i + 1 ) );
        }
      }

      Gizmos.color = currentTargetColor;
      Gizmos.DrawSphere( CurrentTargetPosition, gizmoRadius * 1.25f );
    }
  }
}
