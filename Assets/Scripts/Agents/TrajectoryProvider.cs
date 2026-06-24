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
      CalibratedFan,
      ReachableWorkspaceVolume
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

    [Header("Reachable workspace volume path")]
    [Tooltip("提供当前 MoveToDig / CarryMaterial 模式的 Agent。为空时会在同一环境根节点下自动查找。")]
    public ExcavatorTrajectoryAgent taskModeSource;

    [Tooltip("MoveToDig 模式使用的 3D 可达空间。为空时会尝试按名字自动查找 MoveToDig。")]
    public ReachableWorkspaceVisualizer moveToDigReachableWorkspace;

    [Tooltip("CarryMaterial 模式使用的 3D 可达空间。为空时会尝试按名字自动查找 CarryMaterial。")]
    public ReachableWorkspaceVisualizer carryMaterialReachableWorkspace;

    [Tooltip("没有 ReachableWorkspaceVisualizer 组件时，体积锚点所属参考系。为空时回退 calibratedFanFrame / TrajectoryGroup 父物体。")]
    public Transform reachableWorkspaceFrame;

    [Tooltip("MoveToDig 后备 3D 可达空间锚点，顺序为 5 个方向 x 4 点。只有场景中未绑定 ReachableWorkspaceVisualizer 时使用。")]
    public Vector3[] moveToDigVolumeLocalPoints = ReachableWorkspaceVisualizer.Env01VolumeAnchors();

    [Tooltip("CarryMaterial 后备 3D 可达空间锚点，顺序为 5 个方向 x 4 点。只有场景中未绑定 ReachableWorkspaceVisualizer 时使用。")]
    public Vector3[] carryMaterialVolumeLocalPoints = ReachableWorkspaceVisualizer.Env01VolumeAnchors();

    [Tooltip("ReachableWorkspaceVolume 模式下起点和终点的最小空间距离。")]
    public float reachableWorkspaceMinEndpointDistance = 1.0f;

    [Tooltip("ReachableWorkspaceVolume 模式下路径中段的弯曲扰动强度。")]
    [Range( 0f, 0.5f )]
    public float reachableWorkspaceCurveStrength = 0.16f;

    [Tooltip("ReachableWorkspaceVolume 模式下是否允许路径中段带弧度。")]
    public bool reachableWorkspaceUseCurvedPaths = true;

    [Tooltip("ReachableWorkspaceVolume 后备锚点采样时，起点和终点在方向参数上的最小跨度。")]
    [Range( 0f, 1f )]
    public float reachableWorkspaceMinDirectionSpan = 0.30f;

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
    private bool m_reachableWorkspaceWarningShown = false;

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

      if ( randomPathMode == RandomPathMode.ReachableWorkspaceVolume && TryGenerateReachableWorkspaceVolumePath( count ) )
        return;

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

    private bool TryGenerateReachableWorkspaceVolumePath( int count )
    {
      EnsureReachableWorkspaceVolumeAnchorArrays();
      ResolveReachableWorkspaceReferences();

      ReachableWorkspaceVisualizer workspace = ActiveReachableWorkspace();
      if ( workspace != null && TryGenerateReachableWorkspaceComponentPath( workspace, count ) )
        return true;

      Vector3[] volumeAnchors = ActiveReachableWorkspaceVolumeAnchors();
      if ( IsValidVolumeAnchorArray( volumeAnchors ) ) {
        GenerateReachableWorkspaceAnchorPath( volumeAnchors, count );
        return true;
      }

      if ( !m_reachableWorkspaceWarningShown ) {
        Debug.LogWarning( "[TrajectoryProvider] ReachableWorkspaceVolume mode has no valid Move/Carry volume. Falling back to CalibratedFan." );
        m_reachableWorkspaceWarningShown = true;
      }

      GenerateCalibratedFanPath( count );
      return true;
    }

    private bool TryGenerateReachableWorkspaceComponentPath( ReachableWorkspaceVisualizer workspace, int count )
    {
      if ( workspace == null || !workspace.TryGetWorldVolumeBounds( out Bounds bounds ) )
        return false;

      if ( !TryRandomWorldPointInWorkspace( workspace, bounds, out Vector3 start ) )
        return false;

      Vector3 end = start;
      float minDistance = Mathf.Max( reachableWorkspaceMinEndpointDistance, 0f );
      for ( int i = 0; i < 24; i++ ) {
        if ( !TryRandomWorldPointInWorkspace( workspace, bounds, out end ) )
          return false;

        if ( ( end - start ).magnitude >= minDistance )
          break;
      }

      Vector3 axis = end - start;
      Vector3 lateral = Vector3.Cross( axis.sqrMagnitude > 0.0001f ? axis.normalized : Vector3.forward, Vector3.up );
      if ( lateral.sqrMagnitude < 0.0001f )
        lateral = Vector3.right;
      lateral.Normalize();

      float curveStrength = reachableWorkspaceUseCurvedPaths ? Mathf.Clamp01( reachableWorkspaceCurveStrength ) : 0f;
      float jitterScale = Mathf.Max( bounds.extents.magnitude, 0.1f ) * curveStrength;

      for ( int i = 0; i < count; i++ ) {
        float t = count <= 1 ? 1f : (float)i / ( count - 1 );
        float arc = Mathf.Sin( Mathf.PI * t );
        Vector3 worldPoint = Vector3.Lerp( start, end, t );

        if ( curveStrength > 0f ) {
          float signedOffset = Random.Range( -jitterScale, jitterScale ) * arc;
          float verticalOffset = Random.Range( -jitterScale, jitterScale ) * 0.35f * arc;
          worldPoint += lateral * signedOffset + Vector3.up * verticalOffset;
        }

        worldPoint = workspace.ClosestWorldPointInVolume( worldPoint );
        if ( waypoints[ i ] != null )
          waypoints[ i ].localPosition = transform.InverseTransformPoint( worldPoint );
      }

      return true;
    }

    private bool TryRandomWorldPointInWorkspace( ReachableWorkspaceVisualizer workspace, Bounds bounds, out Vector3 point )
    {
      for ( int i = 0; i < 96; i++ ) {
        Vector3 candidate = new Vector3(
          Random.Range( bounds.min.x, bounds.max.x ),
          Random.Range( bounds.min.y, bounds.max.y ),
          Random.Range( bounds.min.z, bounds.max.z )
        );

        if ( workspace.ContainsWorldPointInVolume( candidate ) ) {
          point = candidate;
          return true;
        }
      }

      Vector3 fallback = new Vector3(
        Random.Range( bounds.min.x, bounds.max.x ),
        Random.Range( bounds.min.y, bounds.max.y ),
        Random.Range( bounds.min.z, bounds.max.z )
      );
      point = workspace.ClosestWorldPointInVolume( fallback );
      return true;
    }

    private void GenerateReachableWorkspaceAnchorPath( Vector3[] volumeAnchors, int count )
    {
      float startDirection = Random.value;
      float endDirection = Random.value;
      float minSpan = Mathf.Clamp01( reachableWorkspaceMinDirectionSpan );

      for ( int i = 0; i < 16 && Mathf.Abs( endDirection - startDirection ) < minSpan; i++ )
        endDirection = Random.value;

      if ( Mathf.Abs( endDirection - startDirection ) < minSpan )
        endDirection = startDirection < 0.5f ? Mathf.Min( 1f, startDirection + minSpan ) : Mathf.Max( 0f, startDirection - minSpan );

      float startHeight = Random.Range( 0.1f, 0.9f );
      float endHeight = Random.Range( 0.1f, 0.9f );
      float startDepth = Random.Range( 0.1f, 0.9f );
      float endDepth = Random.Range( 0.1f, 0.9f );
      float curvePhase = Random.value < 0.5f ? -1f : 1f;
      float curveStrength = reachableWorkspaceUseCurvedPaths ? reachableWorkspaceCurveStrength : 0f;

      for ( int i = 0; i < count; i++ ) {
        float t = count <= 1 ? 1f : (float)i / ( count - 1 );
        float endpointScale = i == 0 || i == count - 1 ? 0.35f : 1f;
        float arc = Mathf.Sin( Mathf.PI * t );

        float direction =
          Mathf.Lerp( startDirection, endDirection, t ) +
          curvePhase * arc * curveStrength +
          Random.Range( -calibratedFanDirectionJitter, calibratedFanDirectionJitter ) * endpointScale;

        float height =
          Mathf.Lerp( startHeight, endHeight, t ) +
          Random.Range( -calibratedFanRangeJitter, calibratedFanRangeJitter ) * endpointScale;

        float depth =
          Mathf.Lerp( startDepth, endDepth, t ) -
          arc * curveStrength * 0.25f +
          Random.Range( -calibratedFanRangeJitter, calibratedFanRangeJitter ) * endpointScale;

        Vector3 localPoint = SampleReachableWorkspaceVolume(
          volumeAnchors,
          Mathf.Clamp01( direction ),
          Mathf.Clamp01( height ),
          Mathf.Clamp01( depth ) );

        if ( waypoints[ i ] != null )
          waypoints[ i ].localPosition = ReachableWorkspaceLocalToWaypointLocal( localPoint );
      }
    }

    private ReachableWorkspaceVisualizer ActiveReachableWorkspace()
    {
      ExcavatorTrajectoryAgent.TrajectoryTaskMode mode = ActiveTaskMode();
      if ( mode == ExcavatorTrajectoryAgent.TrajectoryTaskMode.CarryMaterial )
        return carryMaterialReachableWorkspace != null ? carryMaterialReachableWorkspace : moveToDigReachableWorkspace;

      return moveToDigReachableWorkspace != null ? moveToDigReachableWorkspace : carryMaterialReachableWorkspace;
    }

    private Vector3[] ActiveReachableWorkspaceVolumeAnchors()
    {
      ExcavatorTrajectoryAgent.TrajectoryTaskMode mode = ActiveTaskMode();
      if ( mode == ExcavatorTrajectoryAgent.TrajectoryTaskMode.CarryMaterial )
        return IsValidVolumeAnchorArray( carryMaterialVolumeLocalPoints ) ? carryMaterialVolumeLocalPoints : moveToDigVolumeLocalPoints;

      return IsValidVolumeAnchorArray( moveToDigVolumeLocalPoints ) ? moveToDigVolumeLocalPoints : carryMaterialVolumeLocalPoints;
    }

    private ExcavatorTrajectoryAgent.TrajectoryTaskMode ActiveTaskMode()
    {
      return taskModeSource != null ? taskModeSource.CurrentTaskMode : ExcavatorTrajectoryAgent.TrajectoryTaskMode.MoveToDig;
    }

    private void ResolveReachableWorkspaceReferences()
    {
      if ( taskModeSource == null ) {
        Transform root = transform.root;
        if ( root != null )
          taskModeSource = root.GetComponentInChildren<ExcavatorTrajectoryAgent>( true );
      }

      if ( moveToDigReachableWorkspace != null && carryMaterialReachableWorkspace != null )
        return;

      ReachableWorkspaceVisualizer[] workspaces = UnityEngine.Object.FindObjectsByType<ReachableWorkspaceVisualizer>(
        FindObjectsInactive.Include,
        FindObjectsSortMode.None );

      foreach ( ReachableWorkspaceVisualizer workspace in workspaces ) {
        if ( workspace == null )
          continue;

        string workspaceName = workspace.name.ToLowerInvariant();
        if ( moveToDigReachableWorkspace == null && workspaceName.Contains( "movetodig" ) )
          moveToDigReachableWorkspace = workspace;
        else if ( carryMaterialReachableWorkspace == null && workspaceName.Contains( "carrymaterial" ) )
          carryMaterialReachableWorkspace = workspace;
      }
    }

    private Vector3 ReachableWorkspaceLocalToWaypointLocal( Vector3 workspaceLocalPoint )
    {
      Transform frame = reachableWorkspaceFrame != null
        ? reachableWorkspaceFrame
        : calibratedFanFrame != null
          ? calibratedFanFrame
          : transform.parent;

      if ( frame == null || frame == transform )
        return workspaceLocalPoint;

      Vector3 worldPoint = frame.TransformPoint( workspaceLocalPoint );
      return transform.InverseTransformPoint( worldPoint );
    }

    private static Vector3 SampleReachableWorkspaceVolume( Vector3[] anchors, float direction01, float height01, float depth01 )
    {
      const int directionCount = ReachableWorkspaceVisualizer.DirectionCount;

      float directionScaled = Mathf.Clamp01( direction01 ) * ( directionCount - 1 );
      int directionIndex = Mathf.Clamp( Mathf.FloorToInt( directionScaled ), 0, directionCount - 2 );
      float directionT = directionScaled - directionIndex;

      int p0Direction = Mathf.Max( directionIndex - 1, 0 );
      int p1Direction = directionIndex;
      int p2Direction = directionIndex + 1;
      int p3Direction = Mathf.Min( directionIndex + 2, directionCount - 1 );

      return CatmullRom(
        SampleReachableWorkspaceSection( anchors, p0Direction, height01, depth01 ),
        SampleReachableWorkspaceSection( anchors, p1Direction, height01, depth01 ),
        SampleReachableWorkspaceSection( anchors, p2Direction, height01, depth01 ),
        SampleReachableWorkspaceSection( anchors, p3Direction, height01, depth01 ),
        directionT );
    }

    private static Vector3 SampleReachableWorkspaceSection( Vector3[] points, int direction, float height, float depth )
    {
      const int cornerCount = ReachableWorkspaceVisualizer.VolumeCornerCount;
      int start = direction * cornerCount;
      Vector3 highFar = points[ start ];
      Vector3 lowFar = points[ start + 1 ];
      Vector3 highNear = points[ start + 2 ];
      Vector3 lowNear = points[ start + 3 ];
      Vector3 high = Vector3.Lerp( highNear, highFar, Mathf.Clamp01( depth ) );
      Vector3 low = Vector3.Lerp( lowNear, lowFar, Mathf.Clamp01( depth ) );
      return Vector3.Lerp( high, low, Mathf.Clamp01( height ) );
    }

    private static Vector3 CatmullRom( Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t )
    {
      float clampedT = Mathf.Clamp01( t );
      float t2 = clampedT * clampedT;
      float t3 = t2 * clampedT;
      return 0.5f * (
        2f * p1 +
        ( -p0 + p2 ) * clampedT +
        ( 2f * p0 - 5f * p1 + 4f * p2 - p3 ) * t2 +
        ( -p0 + 3f * p1 - 3f * p2 + p3 ) * t3 );
    }

    private static bool IsValidVolumeAnchorArray( Vector3[] anchors )
    {
      return anchors != null && anchors.Length == ReachableWorkspaceVisualizer.VolumeAnchorCount;
    }

    private void EnsureReachableWorkspaceVolumeAnchorArrays()
    {
      if ( !IsValidVolumeAnchorArray( moveToDigVolumeLocalPoints ) )
        moveToDigVolumeLocalPoints = ReachableWorkspaceVisualizer.Env01VolumeAnchors();

      if ( !IsValidVolumeAnchorArray( carryMaterialVolumeLocalPoints ) )
        carryMaterialVolumeLocalPoints = ReachableWorkspaceVisualizer.Env01VolumeAnchors();
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
