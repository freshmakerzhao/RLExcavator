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
      Trend,
      CalibratedFan
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

    [Header("Calibrated fan path")]
    [Tooltip("CalibratedFan 标定点所属参考系。为空时使用当前 TrajectoryGroup 的父物体，通常就是 TrajectoryEnvironment。")]
    public Transform calibratedFanFrame;

    [Tooltip("CalibratedFan 模式下使用的铲斗尖可达空间标定点。默认是 env01 的 5方向 x 3距离层标定。")]
    public Vector3[] calibratedFanLocalPoints = ReachableWorkspaceVisualizer.Env01CalibrationAnchors();

    [Tooltip("CalibratedFan 模式下起点和终点在左右方向上的最小跨度，0 表示不限制，1 表示必须从最左到最右。")]
    [Range( 0f, 1f )]
    public float calibratedFanMinDirectionSpan = 0.35f;

    [Tooltip("CalibratedFan 模式下每个 waypoint 在扇形方向参数上的随机扰动。")]
    [Range( 0f, 0.35f )]
    public float calibratedFanDirectionJitter = 0.08f;

    [Tooltip("CalibratedFan 模式下每个 waypoint 在近远距离参数上的随机扰动。")]
    [Range( 0f, 0.35f )]
    public float calibratedFanRangeJitter = 0.12f;

    [Tooltip("CalibratedFan 模式下高度额外扰动，单位米。")]
    public float calibratedFanVerticalJitter = 0.15f;

    [Tooltip("CalibratedFan 模式下是否允许路径中段带弧度，增加轨迹形状多样性。")]
    public bool calibratedFanUseCurvedPaths = true;

    [Tooltip("CalibratedFan 模式下路径弧度强度。数值越大，路径越不接近直线插值。")]
    [Range( 0f, 0.5f )]
    public float calibratedFanCurveStrength = 0.18f;

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
    private Vector3[] m_pendingLocalWaypoints = null;
    private bool m_pathVisualsVisible = true;

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
      if ( m_pendingLocalWaypoints != null ) {
        ApplyLocalPath( m_pendingLocalWaypoints );
        m_pendingLocalWaypoints = null;
      }
      else if ( randomizePathOnReset )
        GenerateRandomPath();

      CurrentIndex = 0;
      PathCompleted = false;
      RefreshWaypointVisuals();
    }

    public void SetPathVisualsVisible( bool visible )
    {
      m_pathVisualsVisible = visible;
      RefreshWaypointVisuals();
    }

    /// <summary>
    /// 立即使用 planner 输出的世界坐标路径。适合推理/demo 中当前段马上开始跟踪。
    /// </summary>
    public void SetPlannedPath( Vector3[] worldPoints )
    {
      Vector3[] localPoints = WorldToLocalPath( worldPoints );
      if ( localPoints == null )
        return;

      ApplyLocalPath( localPoints );
      CurrentIndex = 0;
      PathCompleted = false;
      m_pathVisualsVisible = true;
      RefreshWaypointVisuals();
    }

    /// <summary>
    /// 暂存 planner 输出的世界坐标路径，在下一次 ResetPath() 时生效。
    /// 适合“本段结束 -> EndEpisode -> 下一段开始”这样的状态机时序。
    /// </summary>
    public void QueuePlannedPath( Vector3[] worldPoints )
    {
      m_pendingLocalWaypoints = WorldToLocalPath( worldPoints );
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

      if ( randomPathMode == RandomPathMode.CalibratedFan ) {
        GenerateCalibratedFanPath( count );
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

    private void GenerateCalibratedFanPath( int count )
    {
      EnsureCalibratedFanAnchorArray();

      float startDirection = Random.value;
      float endDirection = Random.value;
      float minSpan = Mathf.Clamp01( calibratedFanMinDirectionSpan );

      for ( int i = 0; i < 16 && Mathf.Abs( endDirection - startDirection ) < minSpan; i++ )
        endDirection = Random.value;

      if ( Mathf.Abs( endDirection - startDirection ) < minSpan )
        endDirection = startDirection < 0.5f ? Mathf.Min( 1f, startDirection + minSpan ) : Mathf.Max( 0f, startDirection - minSpan );

      float startRange = Random.Range( 0.05f, 0.95f );
      float endRange = Random.Range( 0.05f, 0.95f );
      float curvePhase = Random.value < 0.5f ? -1f : 1f;
      float curveStrength = calibratedFanUseCurvedPaths ? calibratedFanCurveStrength : 0f;

      for ( int i = 0; i < count; i++ ) {
        float t = count <= 1 ? 1f : (float)i / ( count - 1 );
        float endpointScale = i == 0 || i == count - 1 ? 0.35f : 1f;
        float arc = Mathf.Sin( Mathf.PI * t );

        float direction =
          Mathf.Lerp( startDirection, endDirection, t ) +
          curvePhase * arc * curveStrength +
          Random.Range( -calibratedFanDirectionJitter, calibratedFanDirectionJitter ) * endpointScale;

        float range =
          Mathf.Lerp( startRange, endRange, t ) -
          arc * curveStrength * 0.35f +
          Random.Range( -calibratedFanRangeJitter, calibratedFanRangeJitter ) * endpointScale;

        Vector3 candidate = SampleCalibratedFan( Mathf.Clamp01( direction ), Mathf.Clamp01( range ) );
        candidate.y += Random.Range( -calibratedFanVerticalJitter, calibratedFanVerticalJitter ) * endpointScale;

        if ( waypoints[ i ] != null )
          waypoints[ i ].localPosition = CalibratedFanLocalToWaypointLocal( candidate );
      }
    }

    private Vector3 CalibratedFanLocalToWaypointLocal( Vector3 fanLocalPoint )
    {
      Transform frame = calibratedFanFrame != null ? calibratedFanFrame : transform.parent;
      if ( frame == null || frame == transform )
        return fanLocalPoint;

      Vector3 worldPoint = frame.TransformPoint( fanLocalPoint );
      return transform.InverseTransformPoint( worldPoint );
    }

    private Vector3 SampleCalibratedFan( float direction01, float range01 )
    {
      EnsureCalibratedFanAnchorArray();

      const int directionCount = 5;
      const int radialCount = 3;

      float directionScaled = Mathf.Clamp01( direction01 ) * ( directionCount - 1 );
      float rangeScaled = Mathf.Clamp01( range01 ) * ( radialCount - 1 );
      int directionIndex = Mathf.Clamp( Mathf.FloorToInt( directionScaled ), 0, directionCount - 2 );
      int rangeIndex = Mathf.Clamp( Mathf.FloorToInt( rangeScaled ), 0, radialCount - 2 );
      float directionT = directionScaled - directionIndex;
      float rangeT = rangeScaled - rangeIndex;

      Vector3 nearLeft = calibratedFanLocalPoints[ CalibratedFanIndex( directionIndex, rangeIndex ) ];
      Vector3 farLeft = calibratedFanLocalPoints[ CalibratedFanIndex( directionIndex, rangeIndex + 1 ) ];
      Vector3 nearRight = calibratedFanLocalPoints[ CalibratedFanIndex( directionIndex + 1, rangeIndex ) ];
      Vector3 farRight = calibratedFanLocalPoints[ CalibratedFanIndex( directionIndex + 1, rangeIndex + 1 ) ];

      Vector3 left = Vector3.Lerp( nearLeft, farLeft, rangeT );
      Vector3 right = Vector3.Lerp( nearRight, farRight, rangeT );
      return Vector3.Lerp( left, right, directionT );
    }

    private void EnsureCalibratedFanAnchorArray()
    {
      if ( calibratedFanLocalPoints != null && calibratedFanLocalPoints.Length == 15 )
        return;

      calibratedFanLocalPoints = ReachableWorkspaceVisualizer.Env01CalibrationAnchors();
    }

    private static int CalibratedFanIndex( int direction, int radial )
    {
      const int radialCount = 3;
      return direction * radialCount + radial;
    }

    private Vector3[] WorldToLocalPath( Vector3[] worldPoints )
    {
      if ( worldPoints == null || worldPoints.Length == 0 ) {
        Debug.LogWarning( "[TrajectoryProvider] Ignored empty planned path." );
        return null;
      }

      Vector3[] localPoints = new Vector3[ worldPoints.Length ];
      for ( int i = 0; i < worldPoints.Length; i++ )
        localPoints[ i ] = transform.InverseTransformPoint( worldPoints[ i ] );

      return localPoints;
    }

    private void ApplyLocalPath( Vector3[] localPoints )
    {
      if ( localPoints == null || localPoints.Length == 0 )
        return;

      EnsureWaypointArray( localPoints.Length );

      for ( int i = 0; i < localPoints.Length; i++ )
        if ( waypoints[ i ] != null )
          waypoints[ i ].localPosition = localPoints[ i ];

      m_baseLocalWaypoints = new Vector3[ localPoints.Length ];
      for ( int i = 0; i < localPoints.Length; i++ )
        m_baseLocalWaypoints[ i ] = localPoints[ i ];

      if ( addRuntimeVisuals )
        EnsureRuntimeVisuals();
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
        visual.SetVisible( m_pathVisualsVisible );
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
