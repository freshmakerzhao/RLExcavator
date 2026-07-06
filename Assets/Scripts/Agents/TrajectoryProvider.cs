using System.Collections.Generic;
using UnityEngine;

namespace RLExcavator
{
  /// <summary>
  /// 为 TrajectoryAgent 提供单点目标或多点路径，并同步维护路径点调试可视化。
  /// </summary>
  public class TrajectoryProvider : MonoBehaviour
  {
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

    [Header("Reachable workspace volume path")]
    [Tooltip("挖机语义坐标注册器。用于在同一训练环境内自动查找任务模式来源。")]
    public ExcavatorFrameRegistry frameRegistry;

    [Tooltip("提供当前 MoveToDig / CarryMaterial 模式的 Agent。为空时会在同一环境根节点下自动查找。")]
    public ExcavatorTrajectoryAgent taskModeSource;

    [Tooltip("MoveToDig 模式使用的 3D 可达空间。为空时会尝试按名字自动查找 MoveToDig。")]
    public ReachableWorkspaceVisualizer moveToDigReachableWorkspace;

    [Tooltip("CarryMaterial 模式使用的 3D 可达空间。为空时会尝试按名字自动查找 CarryMaterial。")]
    public ReachableWorkspaceVisualizer carryMaterialReachableWorkspace;

    [Tooltip("ReachableWorkspaceVolume 模式下起点和终点的最小空间距离。")]
    public float reachableWorkspaceMinEndpointDistance = 1.0f;

    [Tooltip("EasyTraining 时把第一个 waypoint 固定到当前铲尖附近。")]
    public bool useCurrentTipAsPathStart = false;

    [Tooltip("EasyTraining 起点参考。为空时尝试使用当前 Trajectory Agent 的 bucketTip。")]
    public Transform currentTipPathStart;

    [Tooltip("EasyTraining 时终点距离起点的最大空间距离，单位米；<=0 表示不限制。")]
    public float reachableWorkspaceMaxEndpointDistance = 0f;

    [Tooltip("EasyTraining 时在可达空间参数里的局部采样半径。")]
    [Range( 0.01f, 0.5f )]
    public float nearbyPathParameterRadius = 0.12f;

    [Tooltip("ReachableWorkspaceVolume 模式下路径中段的弯曲扰动强度。")]
    [Range( 0f, 0.5f )]
    public float reachableWorkspaceCurveStrength = 0.16f;

    [Tooltip("ReachableWorkspaceVolume 模式下是否允许路径中段带弧度。")]
    public bool reachableWorkspaceUseCurvedPaths = true;

    [Tooltip("ReachableWorkspaceVolume 模式下每个 waypoint 在方向参数上的随机扰动。")]
    [Range( 0f, 0.35f )]
    public float reachableWorkspaceDirectionJitter = 0.08f;

    [Tooltip("ReachableWorkspaceVolume 模式下每个 waypoint 在高度参数上的随机扰动。")]
    [Range( 0f, 0.35f )]
    public float reachableWorkspaceHeightJitter = 0.12f;

    [Tooltip("ReachableWorkspaceVolume 模式下每个 waypoint 在近远深度参数上的随机扰动。")]
    [Range( 0f, 0.35f )]
    public float reachableWorkspaceDepthJitter = 0.12f;

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
    private Vector3[] m_pendingLocalWaypoints = null;
    private bool m_pathVisualsVisible = true;
    private bool m_reachableWorkspaceWarningShown = false;

    private void Start()
    {
      ResolveFrameRegistryIfNeeded();

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

      if ( TryGenerateReachableWorkspaceVolumePath( count ) )
        return;

      // Keep the previous path untouched when the workspace is not configured yet.
    }

    private bool TryGenerateReachableWorkspaceVolumePath( int count )
    {
      ResolveReachableWorkspaceReferences();

      ReachableWorkspaceVisualizer workspace = ActiveReachableWorkspace();
      if ( workspace != null && TryGenerateReachableWorkspaceComponentPath( workspace, count ) )
        return true;

      if ( !m_reachableWorkspaceWarningShown ) {
        Debug.LogWarning( "[TrajectoryProvider] Missing valid MoveToDig/CarryMaterial ReachableWorkspaceVisualizer for random path generation." );
        m_reachableWorkspaceWarningShown = true;
      }

      return false;
    }

    private bool TryGenerateReachableWorkspaceComponentPath( ReachableWorkspaceVisualizer workspace, int count )
    {
      if ( workspace == null )
        return false;

      Vector3 startParameters = StartVolumeParameters( workspace );
      Vector3 endParameters = RandomReachableWorkspaceVolumeParameters();
      float minDistance = Mathf.Max( reachableWorkspaceMinEndpointDistance, 0f );
      float maxDistance = Mathf.Max( reachableWorkspaceMaxEndpointDistance, 0f );
      Vector3 start = workspace.SampleWorldVolume( startParameters.x, startParameters.y, startParameters.z );
      Vector3 end = workspace.SampleWorldVolume( endParameters.x, endParameters.y, endParameters.z );

      if ( useCurrentTipAsPathStart && currentTipPathStart != null && maxDistance > 0f ) {
        endParameters = NearbyEndVolumeParameters( workspace, startParameters, start, minDistance, maxDistance );
      }
      else {
        for ( int i = 0; i < 24; i++ ) {
          endParameters = RandomReachableWorkspaceVolumeParameters();
          end = workspace.SampleWorldVolume( endParameters.x, endParameters.y, endParameters.z );

          if ( ( end - start ).magnitude >= minDistance )
            break;
        }
      }

      float curveStrength = reachableWorkspaceUseCurvedPaths ? Mathf.Clamp01( reachableWorkspaceCurveStrength ) : 0f;
      float curvePhase = Random.value < 0.5f ? -1f : 1f;

      for ( int i = 0; i < count; i++ ) {
        float t = count <= 1 ? 1f : (float)i / ( count - 1 );
        float endpointScale = i == 0 || i == count - 1 ? 0.35f : 1f;
        float arc = Mathf.Sin( Mathf.PI * t );

        float direction =
          Mathf.Lerp( startParameters.x, endParameters.x, t ) +
          curvePhase * arc * curveStrength +
          Random.Range( -reachableWorkspaceDirectionJitter, reachableWorkspaceDirectionJitter ) * endpointScale;

        float height =
          Mathf.Lerp( startParameters.y, endParameters.y, t ) +
          Random.Range( -reachableWorkspaceHeightJitter, reachableWorkspaceHeightJitter ) * endpointScale;

        float depth =
          Mathf.Lerp( startParameters.z, endParameters.z, t ) -
          arc * curveStrength * 0.25f +
          Random.Range( -reachableWorkspaceDepthJitter, reachableWorkspaceDepthJitter ) * endpointScale;

        Vector3 worldPoint = SampleContainedReachableWorkspacePoint( workspace, direction, height, depth );
        if ( waypoints[ i ] != null )
          waypoints[ i ].localPosition = transform.InverseTransformPoint( worldPoint );
      }

      return true;
    }

    private Vector3 StartVolumeParameters( ReachableWorkspaceVisualizer workspace )
    {
      ResolveCurrentTipPathStartIfNeeded();

      if ( useCurrentTipAsPathStart && currentTipPathStart != null )
        return ClampVolumeParameters( workspace.ClosestWorldVolumeParameters( currentTipPathStart.position ) );

      return RandomReachableWorkspaceVolumeParameters();
    }

    private Vector3 NearbyEndVolumeParameters(
      ReachableWorkspaceVisualizer workspace,
      Vector3 startParameters,
      Vector3 startWorld,
      float minDistance,
      float maxDistance )
    {
      Vector3 bestParameters = startParameters;
      float bestScore = float.PositiveInfinity;
      float targetDistance = Mathf.Lerp( minDistance, maxDistance, 0.65f );
      float radius = Mathf.Clamp( nearbyPathParameterRadius, 0.01f, 0.5f );

      for ( int i = 0; i < 48; i++ ) {
        Vector3 offset = Random.insideUnitSphere * radius;
        Vector3 candidateParameters = ClampVolumeParameters( startParameters + offset );
        Vector3 candidateWorld = workspace.SampleWorldVolume( candidateParameters );
        float distance = Vector3.Distance( startWorld, candidateWorld );
        bool insideDistanceBand = distance >= minDistance && distance <= maxDistance;
        float score = Mathf.Abs( distance - targetDistance ) + ( insideDistanceBand ? 0f : 10f );

        if ( score < bestScore ) {
          bestScore = score;
          bestParameters = candidateParameters;
        }

        if ( insideDistanceBand )
          return candidateParameters;
      }

      return bestParameters;
    }

    private void ResolveCurrentTipPathStartIfNeeded()
    {
      if ( currentTipPathStart != null )
        return;

      if ( taskModeSource != null && taskModeSource.bucketTip != null )
        currentTipPathStart = taskModeSource.bucketTip;
    }

    private static Vector3 RandomReachableWorkspaceVolumeParameters()
    {
      return new Vector3(
        Random.Range( 0.02f, 0.98f ),
        Random.Range( 0.1f, 0.9f ),
        Random.Range( 0.1f, 0.9f ) );
    }

    private static Vector3 ClampVolumeParameters( Vector3 parameters )
    {
      return new Vector3(
        Mathf.Clamp( parameters.x, 0.02f, 0.98f ),
        Mathf.Clamp( parameters.y, 0.08f, 0.92f ),
        Mathf.Clamp( parameters.z, 0.08f, 0.92f ) );
    }

    private static Vector3 SampleContainedReachableWorkspacePoint(
      ReachableWorkspaceVisualizer workspace,
      float direction,
      float height,
      float depth )
    {
      float clampedDirection = Mathf.Clamp( direction, 0.02f, 0.98f );
      float clampedHeight = Mathf.Clamp( height, 0.08f, 0.92f );
      float clampedDepth = Mathf.Clamp( depth, 0.08f, 0.92f );

      Vector3 point = workspace.SampleWorldVolume( clampedDirection, clampedHeight, clampedDepth );
      if ( workspace.ContainsWorldPointInVolume( point ) )
        return point;

      for ( int i = 1; i <= 4; i++ ) {
        float centerPull = i * 0.18f;
        point = workspace.SampleWorldVolume(
          Mathf.Lerp( clampedDirection, 0.5f, centerPull ),
          Mathf.Lerp( clampedHeight, 0.5f, centerPull ),
          Mathf.Lerp( clampedDepth, 0.5f, centerPull ) );
        if ( workspace.ContainsWorldPointInVolume( point ) )
          return point;
      }

      return workspace.SampleWorldVolume( 0.5f, 0.5f, 0.5f );
    }

    private ReachableWorkspaceVisualizer ActiveReachableWorkspace()
    {
      ExcavatorTrajectoryAgent.TrajectoryTaskMode mode = ActiveTaskMode();
      return mode == ExcavatorTrajectoryAgent.TrajectoryTaskMode.CarryMaterial
        ? carryMaterialReachableWorkspace
        : moveToDigReachableWorkspace;
    }

    private ExcavatorTrajectoryAgent.TrajectoryTaskMode ActiveTaskMode()
    {
      return taskModeSource != null ? taskModeSource.CurrentTaskMode : ExcavatorTrajectoryAgent.TrajectoryTaskMode.MoveToDig;
    }

    private void ResolveReachableWorkspaceReferences()
    {
      Transform searchRoot = ReachableWorkspaceSearchRoot();
      ResolveFrameRegistryIfNeeded();

      if ( taskModeSource == null ) {
        if ( searchRoot != null )
          taskModeSource = searchRoot.GetComponentInChildren<ExcavatorTrajectoryAgent>( true );
      }

      if ( moveToDigReachableWorkspace != null && carryMaterialReachableWorkspace != null )
        return;

      ReachableWorkspaceVisualizer[] workspaces = searchRoot != null
        ? searchRoot.GetComponentsInChildren<ReachableWorkspaceVisualizer>( true )
        : UnityEngine.Object.FindObjectsByType<ReachableWorkspaceVisualizer>(
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

    private Transform ReachableWorkspaceSearchRoot()
    {
      if ( taskModeSource != null && taskModeSource.environmentRoot != null )
        return taskModeSource.environmentRoot;

      if ( transform.parent != null )
        return transform.parent;

      return transform.root;
    }

    private void ResolveFrameRegistryIfNeeded()
    {
      if ( frameRegistry != null )
        return;

      if ( taskModeSource != null && taskModeSource.frameRegistry != null ) {
        frameRegistry = taskModeSource.frameRegistry;
        return;
      }

      frameRegistry = ExcavatorFrameRegistry.FindNearest( this );
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

      if ( addRuntimeVisuals )
        EnsureRuntimeVisuals();
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
