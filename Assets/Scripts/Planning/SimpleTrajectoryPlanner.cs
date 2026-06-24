using System.Collections.Generic;
using UnityEngine;

namespace RLExcavator
{
  /// <summary>
  /// 最小轨迹规划器：根据起点和终点生成一条可由 TrajectoryProvider 跟踪的 waypoint 路径。
  /// 第一版只负责接口闭环；后续可在保持输出接口不变的前提下替换为 RRT*。
  /// </summary>
  public class SimpleTrajectoryPlanner : MonoBehaviour
  {
    public enum PlannerAlgorithm
    {
      ArcInterpolation,
      RrtStar,
      CartesianRrtStar
    }

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
    [Tooltip("轨迹生成算法。ArcInterpolation 是原有稳定 baseline；RrtStar 使用可达空间采样生成路径。")]
    public PlannerAlgorithm plannerAlgorithm = PlannerAlgorithm.ArcInterpolation;

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

    [Header("Reachable workspace")]
    [Tooltip("当前激活的铲斗尖可达空间。保留该字段用于兼容旧场景；Demo 中会随 MoveToDig/CarryMaterial 自动切换。")]
    public ReachableWorkspaceVisualizer reachableWorkspace;

    [Tooltip("MoveToDig 模式使用的可达空间。为空时回退到当前激活的 reachableWorkspace。")]
    public ReachableWorkspaceVisualizer moveToDigReachableWorkspace;

    [Tooltip("CarryMaterial 模式使用的可达空间。为空时回退到当前激活的 reachableWorkspace。")]
    public ReachableWorkspaceVisualizer carryMaterialReachableWorkspace;

    [Tooltip("CartesianRrtStar 使用 ReachableWorkspaceVisualizer 的 3D 体积作为规划边界，而不是使用下方手动 Bounds。")]
    public bool useReachableWorkspaceVolumeForCartesianRrt = true;

    [Tooltip("规划前将起点/终点夹到可达空间最近点，避免给策略输入明显不可达目标。")]
    public bool clampEndpointsToWorkspace = true;

    [Tooltip("夹取后的起点/终点是否回写到可见控制点对象。关闭时只影响本次规划路径。")]
    public bool writeClampedEndpointsBack = true;

    [Tooltip("通过 UI 移动起点/终点后，是否自动夹到可达空间最近点。")]
    public bool clampMovedControlPointsToWorkspace = true;

    [Tooltip("非参数域轨迹写入 TrajectoryProvider 前，将 waypoint 投影回当前可达空间。CartesianRrtStar 使用 3D 体积，旧 RrtStar 使用参数域表面。")]
    public bool clampGeneratedWaypointsToWorkspace = true;

    [Tooltip("起点/终点被夹取超过该距离时打印 warning，单位米。")]
    public float endpointClampWarningDistance = 0.75f;

    [Tooltip("输出 waypoint 被投影超过该距离时打印 warning，单位米。")]
    public float generatedWaypointClampWarningDistance = 0.25f;

    [Header("RRT*")]
    [Tooltip("通用 3D RRT* 的世界坐标规划范围中心。CartesianRrtStar 会把范围外视为不可通过区域。")]
    public Vector3 cartesianPlanningBoundsCenter = new Vector3( 0f, 2.5f, 0f );

    [Tooltip("通用 3D RRT* 的世界坐标规划范围尺寸。CartesianRrtStar 会把范围外视为不可通过区域。")]
    public Vector3 cartesianPlanningBoundsSize = new Vector3( 12f, 5f, 12f );

    [Tooltip("RRT* 最大采样迭代次数。无障碍 demo 通常几百次足够；复杂障碍需要增大。")]
    public int rrtMaxIterations = 900;

    [Tooltip("RRT* 每次扩展的最大步长，建议接近策略训练时相邻 waypoint 间距。")]
    public float rrtStepSize = 1.1f;

    [Tooltip("采样时直接采到终点的概率。较大时更容易连向终点，较小时探索更充分。")]
    [Range( 0f, 0.6f )]
    public float rrtGoalBias = 0.15f;

    [Tooltip("新节点距离终点小于该值时尝试连接终点。")]
    public float rrtGoalTolerance = 0.9f;

    [Tooltip("RRT* 重连邻域半径。过小退化为 RRT，过大计算更慢。")]
    public float rrtRewireRadius = 2.2f;

    [Tooltip("是否在无碰撞直线路径存在时直接输出直线路径。关闭后始终尝试 RRT*。")]
    public bool preferDirectPathWhenClear = false;

    [Tooltip("RRT* 失败时回退到可达空间参数域插值轨迹，保证 demo 不会因为规划失败完全不可用。字段名保留 Arc 是为了兼容已有 Unity 序列化。")]
    public bool fallbackToArcOnRrtFailure = true;

    [Tooltip("路径平滑时尝试删除冗余节点的次数。")]
    public int rrtSmoothingPasses = 2;

    [Tooltip("在可达空间参数域检查一条边时的离散采样数。数值越大越不容易跨出可达面或穿过障碍，但规划更慢。")]
    public int rrtWorkspaceEdgeSamples = 8;

    [Tooltip("规划节点和路径段允许的最低世界高度，防止采到地面以下。")]
    public float rrtMinWorldY = -1000f;

    [Tooltip("简化碰撞检测球半径。障碍物层未配置时不做 Physics 碰撞检测。")]
    public float rrtCollisionRadius = 0.18f;

    [Tooltip("RRT* 碰撞检测使用的障碍物层。值为 Nothing 时跳过障碍检测。")]
    public LayerMask rrtObstacleMask = 0;

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

    public bool LastPlanningSucceeded { get; private set; }
    public int LastPlanningIterations { get; private set; }
    public int LastRawPathPointCount { get; private set; }
    public string LastPlanningSummary { get; private set; } = "尚未规划";

    private void Start()
    {
      EnsureActiveReachableWorkspaceFallback();
      EnsureControlPointVisuals();
    }

    public ReachableWorkspaceVisualizer ReachableWorkspaceForMode( ExcavatorTrajectoryAgent.TrajectoryTaskMode mode )
    {
      switch ( mode ) {
        case ExcavatorTrajectoryAgent.TrajectoryTaskMode.CarryMaterial:
          return carryMaterialReachableWorkspace != null ? carryMaterialReachableWorkspace : reachableWorkspace;
        case ExcavatorTrajectoryAgent.TrajectoryTaskMode.MoveToDig:
        default:
          return moveToDigReachableWorkspace != null ? moveToDigReachableWorkspace : reachableWorkspace;
      }
    }

    public void SetActiveReachableWorkspaceForMode( ExcavatorTrajectoryAgent.TrajectoryTaskMode mode )
    {
      SetActiveReachableWorkspace( ReachableWorkspaceForMode( mode ) );
    }

    public void SetActiveReachableWorkspace( ReachableWorkspaceVisualizer workspace )
    {
      if ( workspace != null )
        reachableWorkspace = workspace;
    }

    private void EnsureActiveReachableWorkspaceFallback()
    {
      if ( reachableWorkspace == null ) {
        if ( moveToDigReachableWorkspace != null )
          reachableWorkspace = moveToDigReachableWorkspace;
        else if ( carryMaterialReachableWorkspace != null )
          reachableWorkspace = carryMaterialReachableWorkspace;
      }

      if ( moveToDigReachableWorkspace == null )
        moveToDigReachableWorkspace = reachableWorkspace;
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

    public bool TryGetSelectedPointWorldPosition( out Vector3 worldPosition )
    {
      Transform point = SelectedControlPointTransform();
      if ( point == null ) {
        worldPosition = Vector3.zero;
        return false;
      }

      worldPosition = point.position;
      return true;
    }

    public bool SetSelectedPointWorldPosition( Vector3 worldPosition )
    {
      return SetControlPointWorldPosition( SelectedControlPointTransform(), worldPosition );
    }

    public bool SetControlPointWorldPosition( Transform point, Vector3 worldPosition )
    {
      if ( point == null ) {
        Debug.LogWarning( "[SimpleTrajectoryPlanner] Missing editable control point reference." );
        return false;
      }

      point.position = worldPosition;
      ClampControlPointIfNeeded( point );
      return true;
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
      ClampControlPointIfNeeded( point );
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
      Vector3 start = startWorld;
      Vector3 goal = goalWorld;
      if ( plannerAlgorithm == PlannerAlgorithm.CartesianRrtStar )
        ClampEndpointsToCartesianVolumeIfNeeded( ref start, ref goal );
      else
        ClampEndpointsIfNeeded( ref start, ref goal );

      if ( plannerAlgorithm == PlannerAlgorithm.CartesianRrtStar ) {
        if ( TryGenerateCartesianRrtStarPath( start, goal, out Vector3[] cartesianPath ) )
          return cartesianPath;

        if ( !fallbackToArcOnRrtFailure )
          return new[] { start, goal };
      }

      if ( plannerAlgorithm == PlannerAlgorithm.RrtStar ) {
        if ( TryGenerateRrtStarPath( start, goal, out Vector3[] rrtPath ) )
          return rrtPath;

        if ( !fallbackToArcOnRrtFailure ) {
          return GenerateWorkspaceParameterPath( start, goal );
        }
      }

      LastPlanningSucceeded = true;
      LastPlanningIterations = 0;
      LastRawPathPointCount = 0;
      if ( plannerAlgorithm == PlannerAlgorithm.CartesianRrtStar ) {
        LastPlanningSummary = "Cartesian 3D RRT* 失败，已回退到圆弧插值";
      }
      else if ( plannerAlgorithm == PlannerAlgorithm.RrtStar && reachableWorkspace != null ) {
        LastPlanningSummary = "RRT* 失败，已回退到可达空间参数域插值";
        return GenerateWorkspaceParameterPath( start, goal );
      }

      LastPlanningSummary = "圆弧插值轨迹";

      Vector3[] arcPath = ClampGeneratedPathIfNeeded( GenerateArcPath( start, goal ) );
      LastPlanningSummary = AppendPathClampSummary( LastPlanningSummary );
      return arcPath;
    }

    private Vector3[] GenerateArcPath( Vector3 startWorld, Vector3 goalWorld )
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

    private Vector3[] GenerateWorkspaceParameterPath( Vector3 startWorld, Vector3 goalWorld )
    {
      if ( reachableWorkspace == null )
        return new[] { startWorld, goalWorld };

      Vector2 startParameters = reachableWorkspace.ClosestWorldParameters( startWorld );
      Vector2 goalParameters = reachableWorkspace.ClosestWorldParameters( goalWorld );
      List<Vector2> parameterPath = ResampleParameterPathByDistance(
        new List<Vector2> { startParameters, goalParameters },
        PlannedWaypointCount( startWorld, goalWorld ) );
      return ParametersToWorldPath( parameterPath );
    }

    private bool TryGenerateCartesianRrtStarPath( Vector3 startWorld, Vector3 goalWorld, out Vector3[] path )
    {
      path = null;
      LastPlanningSucceeded = false;
      LastPlanningIterations = 0;
      LastRawPathPointCount = 0;

      if ( useReachableWorkspaceVolumeForCartesianRrt && reachableWorkspace != null )
        return TryGenerateReachableWorkspaceCartesianRrtStarPath( startWorld, goalWorld, out path );

      Bounds bounds = CartesianPlanningBounds();
      var settings = CartesianRrtSettings();
      var collisionChecker = new BoundedCartesianRrtCollisionChecker(
        bounds,
        rrtObstacleMask,
        Mathf.Max( rrtWorkspaceEdgeSamples, 1 ),
        rrtMinWorldY,
        null );
      var planner = new RrtStar3DPlanner( settings, collisionChecker );
      RrtStar3DResult result = planner.Plan( bounds, startWorld, goalWorld );

      LastPlanningSucceeded = result.success;
      LastPlanningIterations = result.iterations;
      LastRawPathPointCount = result.rawPath != null ? result.rawPath.Length : 0;
      LastPlanningSummary = $"Cartesian 3D {result.summary}";

      if ( !result.success )
        return false;

      Vector3[] sourcePath = result.rawPath != null && result.rawPath.Length >= 2
        ? result.rawPath
        : result.smoothedPath;
      if ( sourcePath == null || sourcePath.Length < 2 ) {
        LastPlanningSummary = $"{LastPlanningSummary} No usable raw path for output.";
        return false;
      }

      path = ResampleWorldPathByDistance( sourcePath, PlannedWaypointCountForPath( sourcePath, startWorld, goalWorld ) );
      LastPlanningSummary = $"{LastPlanningSummary} Cartesian output uses raw path. Output points: {path.Length}, length: {WorldPathLength( sourcePath ):0.00} m.";
      return true;
    }

    private bool TryGenerateReachableWorkspaceCartesianRrtStarPath( Vector3 startWorld, Vector3 goalWorld, out Vector3[] path )
    {
      path = null;

      if ( !reachableWorkspace.TryGetLocalVolumeBounds( out Bounds localBounds ) ) {
        LastPlanningSummary = "Cartesian 3D 缺少 ReachableWorkspace local volume bounds";
        return false;
      }

      Vector3 startLocal = reachableWorkspace.ClosestLocalPointInVolume( reachableWorkspace.WorldPointToLocal( startWorld ) );
      Vector3 goalLocal = reachableWorkspace.ClosestLocalPointInVolume( reachableWorkspace.WorldPointToLocal( goalWorld ) );
      var settings = CartesianRrtSettings();
      var collisionChecker = new ReachableWorkspaceLocalRrtCollisionChecker(
        reachableWorkspace,
        localBounds,
        rrtObstacleMask,
        Mathf.Max( rrtWorkspaceEdgeSamples, 1 ),
        rrtMinWorldY );
      var planner = new RrtStar3DPlanner( settings, collisionChecker );
      RrtStar3DResult result = planner.Plan( localBounds, startLocal, goalLocal );

      LastPlanningSucceeded = result.success;
      LastPlanningIterations = result.iterations;
      LastRawPathPointCount = result.rawPath != null ? result.rawPath.Length : 0;
      LastPlanningSummary = $"Cartesian 3D workspace-local {result.summary}";

      if ( !result.success )
        return false;

      Vector3[] sourceLocalPath = result.rawPath != null && result.rawPath.Length >= 2
        ? result.rawPath
        : result.smoothedPath;
      if ( sourceLocalPath == null || sourceLocalPath.Length < 2 ) {
        LastPlanningSummary = $"{LastPlanningSummary} No usable local raw path for output.";
        return false;
      }

      Vector3[] sourceWorldPath = new Vector3[ sourceLocalPath.Length ];
      for ( int i = 0; i < sourceLocalPath.Length; i++ )
        sourceWorldPath[ i ] = reachableWorkspace.LocalPointToWorld( sourceLocalPath[ i ] );

      path = ResampleWorldPathByDistance( sourceWorldPath, PlannedWaypointCountForPath( sourceWorldPath, startWorld, goalWorld ) );
      LastPlanningSummary = $"{LastPlanningSummary} Output points: {path.Length}, length: {WorldPathLength( sourceWorldPath ):0.00} m.";
      return true;
    }

    private RrtStar3DSettings CartesianRrtSettings()
    {
      return new RrtStar3DSettings {
        maxIterations = rrtMaxIterations,
        stepSize = rrtStepSize,
        goalBias = rrtGoalBias,
        goalTolerance = rrtGoalTolerance,
        rewireRadius = rrtRewireRadius,
        collisionRadius = rrtCollisionRadius,
        edgeCheckResolution = rrtWorkspaceEdgeSamples,
        smoothingPasses = 0,
        seed = 11,
        preferDirectPathWhenClear = preferDirectPathWhenClear
      };
    }

    private Bounds CartesianPlanningBounds()
    {
      if ( useReachableWorkspaceVolumeForCartesianRrt && reachableWorkspace != null && reachableWorkspace.TryGetWorldVolumeBounds( out Bounds workspaceBounds ) )
        return workspaceBounds;

      Vector3 size = new Vector3(
        Mathf.Max( cartesianPlanningBoundsSize.x, 0.1f ),
        Mathf.Max( cartesianPlanningBoundsSize.y, 0.1f ),
        Mathf.Max( cartesianPlanningBoundsSize.z, 0.1f ) );
      return new Bounds( cartesianPlanningBoundsCenter, size );
    }

    private bool TryGenerateRrtStarPath( Vector3 startWorld, Vector3 goalWorld, out Vector3[] path )
    {
      path = null;
      LastPlanningSucceeded = false;
      LastPlanningIterations = 0;
      LastRawPathPointCount = 0;

      if ( reachableWorkspace == null ) {
        LastPlanningSummary = "RRT* 缺少 ReachableWorkspace 引用";
        Debug.LogWarning( "[SimpleTrajectoryPlanner] RRT* requires a ReachableWorkspaceVisualizer reference." );
        return false;
      }

      Vector2 startParameters = reachableWorkspace.ClosestWorldParameters( startWorld );
      Vector2 goalParameters = reachableWorkspace.ClosestWorldParameters( goalWorld );
      Vector3 clampedStart = reachableWorkspace.SampleWorld( startParameters );
      Vector3 clampedGoal = reachableWorkspace.SampleWorld( goalParameters );

      if ( !IsWorkspaceStateValid( startParameters ) || !IsWorkspaceStateValid( goalParameters ) ) {
        LastPlanningSummary = "RRT* 起点或终点高度/碰撞检查失败";
        return false;
      }

      if ( preferDirectPathWhenClear && IsWorkspaceEdgeValid( startParameters, goalParameters ) ) {
        List<Vector2> parameterPath = ResampleParameterPathByDistance(
          new List<Vector2> { startParameters, goalParameters },
          PlannedWaypointCount( clampedStart, clampedGoal ) );
        path = ParametersToWorldPath( parameterPath );
        LastPlanningSucceeded = true;
        LastPlanningSummary = "RRT* 参数域直连成功";
        LastRawPathPointCount = 2;
        return true;
      }

      var nodes = new List<RrtNode> {
        new RrtNode( startParameters, clampedStart, -1, 0f )
      };

      int bestGoalParent = -1;
      float bestGoalCost = float.PositiveInfinity;
      int maxIterations = Mathf.Max( rrtMaxIterations, 1 );
      float stepSize = Mathf.Max( rrtStepSize, 0.05f );
      float goalTolerance = Mathf.Max( rrtGoalTolerance, 0.05f );
      float rewireRadius = Mathf.Max( rrtRewireRadius, stepSize );

      for ( int iteration = 0; iteration < maxIterations; iteration++ ) {
        LastPlanningIterations = iteration + 1;
        Vector2 sampleParameters = Random.value < rrtGoalBias
          ? goalParameters
          : new Vector2( Random.value, Random.value );
        Vector3 sampleWorld = reachableWorkspace.SampleWorld( sampleParameters );
        int nearestIndex = NearestNodeIndex( nodes, sampleWorld );
        Vector2 nearestParameters = nodes[ nearestIndex ].parameters;
        Vector3 nearestWorld = nodes[ nearestIndex ].position;
        Vector2 candidateParameters = SteerParameters( nearestParameters, sampleParameters, stepSize );
        Vector3 candidateWorld = reachableWorkspace.SampleWorld( candidateParameters );

        if ( Vector3.Distance( nearestWorld, candidateWorld ) < 0.03f )
          continue;

        if ( !IsWorkspaceStateValid( candidateParameters ) || !IsWorkspaceEdgeValid( nearestParameters, candidateParameters ) )
          continue;

        int parentIndex = nearestIndex;
        float bestCost = nodes[ nearestIndex ].cost + Vector3.Distance( nearestWorld, candidateWorld );

        for ( int i = 0; i < nodes.Count; i++ ) {
          float distance = Vector3.Distance( nodes[ i ].position, candidateWorld );
          if ( distance > rewireRadius )
            continue;

          float candidateCost = nodes[ i ].cost + distance;
          if ( candidateCost >= bestCost || !IsWorkspaceEdgeValid( nodes[ i ].parameters, candidateParameters ) )
            continue;

          parentIndex = i;
          bestCost = candidateCost;
        }

        int newIndex = nodes.Count;
        nodes.Add( new RrtNode( candidateParameters, candidateWorld, parentIndex, bestCost ) );

        for ( int i = 1; i < nodes.Count - 1; i++ ) {
          float distance = Vector3.Distance( nodes[ i ].position, candidateWorld );
          if ( distance > rewireRadius )
            continue;

          float rewiredCost = bestCost + distance;
          if ( rewiredCost >= nodes[ i ].cost || !IsWorkspaceEdgeValid( candidateParameters, nodes[ i ].parameters ) )
            continue;

          nodes[ i ] = new RrtNode( nodes[ i ].parameters, nodes[ i ].position, newIndex, rewiredCost );
        }

        float distanceToGoal = Vector3.Distance( candidateWorld, clampedGoal );
        if ( distanceToGoal > goalTolerance || !IsWorkspaceEdgeValid( candidateParameters, goalParameters ) )
          continue;

        float goalCost = bestCost + distanceToGoal;
        if ( goalCost < bestGoalCost ) {
          bestGoalParent = newIndex;
          bestGoalCost = goalCost;
        }
      }

      if ( bestGoalParent < 0 ) {
        LastPlanningSummary = $"RRT* 未连接终点，节点数 {nodes.Count}";
        return false;
      }

      List<Vector2> rawParameterPath = BuildRawRrtParameterPath( nodes, bestGoalParent, goalParameters );
      LastRawPathPointCount = rawParameterPath.Count;

      List<Vector2> smoothed = SmoothParameterPath( rawParameterPath, Mathf.Max( rrtSmoothingPasses, 0 ) );
      List<Vector2> resampled = ResampleParameterPathByDistance( smoothed, PlannedWaypointCount( clampedStart, clampedGoal ) );
      path = ParametersToWorldPath( resampled );
      LastPlanningSucceeded = true;
      LastPlanningSummary = $"RRT* 参数域成功：迭代 {LastPlanningIterations}，原始点 {LastRawPathPointCount}，输出点 {path.Length}";
      return true;
    }

    private bool IsWorkspaceStateValid( Vector2 parameters )
    {
      if ( reachableWorkspace == null )
        return false;

      if ( parameters.x < 0f || parameters.x > 1f || parameters.y < 0f || parameters.y > 1f )
        return false;

      return IsPointValid( reachableWorkspace.SampleWorld( parameters ) );
    }

    private bool IsWorkspaceEdgeValid( Vector2 fromParameters, Vector2 toParameters )
    {
      if ( reachableWorkspace == null )
        return false;

      int steps = Mathf.Max( rrtWorkspaceEdgeSamples, 1 );
      Vector3 previous = reachableWorkspace.SampleWorld( fromParameters );
      if ( !IsPointValid( previous ) )
        return false;

      for ( int i = 1; i <= steps; i++ ) {
        float t = (float)i / steps;
        Vector2 parameters = Vector2.Lerp( fromParameters, toParameters, t );
        Vector3 current = reachableWorkspace.SampleWorld( parameters );
        if ( !IsPointValid( current ) || !IsSegmentValid( previous, current ) )
          return false;

        previous = current;
      }

      return true;
    }

    private Vector2 SteerParameters( Vector2 fromParameters, Vector2 toParameters, float maxWorldDistance )
    {
      if ( reachableWorkspace == null )
        return Clamp01( toParameters );

      Vector2 delta = toParameters - fromParameters;
      if ( delta.sqrMagnitude < 0.000001f )
        return Clamp01( toParameters );

      Vector3 fromWorld = reachableWorkspace.SampleWorld( fromParameters );
      Vector3 toWorld = reachableWorkspace.SampleWorld( toParameters );
      if ( Vector3.Distance( fromWorld, toWorld ) <= maxWorldDistance )
        return Clamp01( toParameters );

      float low = 0f;
      float high = 1f;
      for ( int i = 0; i < 10; i++ ) {
        float mid = ( low + high ) * 0.5f;
        Vector2 midParameters = Vector2.Lerp( fromParameters, toParameters, mid );
        Vector3 midWorld = reachableWorkspace.SampleWorld( midParameters );
        if ( Vector3.Distance( fromWorld, midWorld ) <= maxWorldDistance )
          low = mid;
        else
          high = mid;
      }

      return Clamp01( Vector2.Lerp( fromParameters, toParameters, low ) );
    }

    private static Vector2 Clamp01( Vector2 value )
    {
      return new Vector2( Mathf.Clamp01( value.x ), Mathf.Clamp01( value.y ) );
    }

    private List<Vector2> BuildRawRrtParameterPath( List<RrtNode> nodes, int goalParentIndex, Vector2 goalParameters )
    {
      var reversed = new List<Vector2> { goalParameters };
      int index = goalParentIndex;
      while ( index >= 0 ) {
        reversed.Add( nodes[ index ].parameters );
        index = nodes[ index ].parentIndex;
      }

      reversed.Reverse();
      return reversed;
    }

    private List<Vector2> SmoothParameterPath( List<Vector2> rawPath, int passes )
    {
      if ( rawPath == null || rawPath.Count <= 2 || passes <= 0 )
        return rawPath;

      var path = new List<Vector2>( rawPath );
      for ( int pass = 0; pass < passes; pass++ ) {
        int index = 0;
        while ( index < path.Count - 2 ) {
          if ( IsWorkspaceEdgeValid( path[ index ], path[ index + 2 ] ) )
            path.RemoveAt( index + 1 );
          else
            index++;
        }
      }

      return path;
    }

    private List<Vector2> ResampleParameterPathByDistance( List<Vector2> source, int count )
    {
      int outputCount = Mathf.Max( count, 2 );
      var result = new List<Vector2>( outputCount );
      if ( source == null || source.Count == 0 ) {
        for ( int i = 0; i < outputCount; i++ )
          result.Add( Vector2.zero );
        return result;
      }

      if ( source.Count == 1 ) {
        for ( int i = 0; i < outputCount; i++ )
          result.Add( Clamp01( source[ 0 ] ) );
        return result;
      }

      float totalLength = 0f;
      for ( int i = 1; i < source.Count; i++ )
        totalLength += ParameterSegmentWorldDistance( source[ i - 1 ], source[ i ] );

      if ( totalLength < 0.0001f ) {
        for ( int i = 0; i < outputCount; i++ )
          result.Add( Clamp01( source[ 0 ] ) );
        return result;
      }

      result.Add( Clamp01( source[ 0 ] ) );

      int segmentIndex = 1;
      float distanceBeforeSegment = 0f;
      float currentSegmentLength = ParameterSegmentWorldDistance( source[ 0 ], source[ 1 ] );

      for ( int i = 1; i < outputCount - 1; i++ ) {
        float targetDistance = totalLength * i / ( outputCount - 1 );
        while ( segmentIndex < source.Count - 1 && distanceBeforeSegment + currentSegmentLength < targetDistance ) {
          distanceBeforeSegment += currentSegmentLength;
          segmentIndex++;
          currentSegmentLength = ParameterSegmentWorldDistance( source[ segmentIndex - 1 ], source[ segmentIndex ] );
        }

        float t = currentSegmentLength < 0.0001f ? 0f : ( targetDistance - distanceBeforeSegment ) / currentSegmentLength;
        result.Add( Clamp01( Vector2.Lerp( source[ segmentIndex - 1 ], source[ segmentIndex ], Mathf.Clamp01( t ) ) ) );
      }

      result.Add( Clamp01( source[ source.Count - 1 ] ) );
      return result;
    }

    private static Vector3[] ResampleWorldPathByDistance( Vector3[] source, int count )
    {
      int outputCount = Mathf.Max( count, 2 );
      if ( source == null || source.Length == 0 )
        return new Vector3[ outputCount ];

      if ( source.Length == 1 ) {
        Vector3[] single = new Vector3[ outputCount ];
        for ( int i = 0; i < single.Length; i++ )
          single[ i ] = source[ 0 ];
        return single;
      }

      float totalLength = 0f;
      for ( int i = 1; i < source.Length; i++ )
        totalLength += Vector3.Distance( source[ i - 1 ], source[ i ] );

      if ( totalLength < 0.0001f ) {
        Vector3[] collapsed = new Vector3[ outputCount ];
        for ( int i = 0; i < collapsed.Length; i++ )
          collapsed[ i ] = source[ 0 ];
        return collapsed;
      }

      Vector3[] result = new Vector3[ outputCount ];
      result[ 0 ] = source[ 0 ];
      int segmentIndex = 1;
      float distanceBeforeSegment = 0f;
      float currentSegmentLength = Vector3.Distance( source[ 0 ], source[ 1 ] );

      for ( int i = 1; i < outputCount - 1; i++ ) {
        float targetDistance = totalLength * i / ( outputCount - 1 );
        while ( segmentIndex < source.Length - 1 && distanceBeforeSegment + currentSegmentLength < targetDistance ) {
          distanceBeforeSegment += currentSegmentLength;
          segmentIndex++;
          currentSegmentLength = Vector3.Distance( source[ segmentIndex - 1 ], source[ segmentIndex ] );
        }

        float t = currentSegmentLength < 0.0001f ? 0f : ( targetDistance - distanceBeforeSegment ) / currentSegmentLength;
        result[ i ] = Vector3.Lerp( source[ segmentIndex - 1 ], source[ segmentIndex ], Mathf.Clamp01( t ) );
      }

      result[ outputCount - 1 ] = source[ source.Length - 1 ];
      return result;
    }

    private float ParameterSegmentWorldDistance( Vector2 fromParameters, Vector2 toParameters )
    {
      if ( reachableWorkspace == null )
        return Vector2.Distance( fromParameters, toParameters );

      return Vector3.Distance(
        reachableWorkspace.SampleWorld( fromParameters ),
        reachableWorkspace.SampleWorld( toParameters ) );
    }

    private Vector3[] ParametersToWorldPath( List<Vector2> parameterPath )
    {
      if ( parameterPath == null || parameterPath.Count == 0 )
        return new Vector3[ 0 ];

      Vector3[] path = new Vector3[ parameterPath.Count ];
      for ( int i = 0; i < parameterPath.Count; i++ )
        path[ i ] = reachableWorkspace.SampleWorld( parameterPath[ i ] );

      return path;
    }

    private int m_lastGeneratedClampCount;
    private float m_lastGeneratedClampMaxDistance;

    private Vector3[] ClampGeneratedPathIfNeeded( Vector3[] path )
    {
      m_lastGeneratedClampCount = 0;
      m_lastGeneratedClampMaxDistance = 0f;

      if ( !clampGeneratedWaypointsToWorkspace || reachableWorkspace == null || path == null )
        return path;

      Vector3[] clampedPath = new Vector3[ path.Length ];
      for ( int i = 0; i < path.Length; i++ ) {
        Vector3 clamped = ShouldClampGeneratedPathToVolume()
          ? reachableWorkspace.ClosestWorldPointInVolume( path[ i ] )
          : reachableWorkspace.ClosestWorldPoint( path[ i ] );
        float distance = Vector3.Distance( path[ i ], clamped );
        if ( distance > 0.0001f ) {
          m_lastGeneratedClampCount++;
          m_lastGeneratedClampMaxDistance = Mathf.Max( m_lastGeneratedClampMaxDistance, distance );
        }

        clampedPath[ i ] = clamped;
      }

      if ( m_lastGeneratedClampMaxDistance > Mathf.Max( generatedWaypointClampWarningDistance, 0f ) ) {
        Debug.LogWarning(
          $"[SimpleTrajectoryPlanner] Projected {m_lastGeneratedClampCount}/{path.Length} generated waypoint(s) back to the reachable workspace. Max move: {m_lastGeneratedClampMaxDistance:0.00} m." );
      }

      return clampedPath;
    }

    private string AppendPathClampSummary( string summary )
    {
      if ( m_lastGeneratedClampCount <= 0 )
        return summary;

      return $"{summary}，投影输出点 {m_lastGeneratedClampCount} 个，最大 {m_lastGeneratedClampMaxDistance:0.00} m";
    }

    private bool ShouldClampGeneratedPathToVolume()
    {
      return plannerAlgorithm == PlannerAlgorithm.CartesianRrtStar && useReachableWorkspaceVolumeForCartesianRrt;
    }

    private void ClampEndpointsIfNeeded( ref Vector3 start, ref Vector3 goal )
    {
      if ( !clampEndpointsToWorkspace || reachableWorkspace == null )
        return;

      Vector3 clampedStart = reachableWorkspace.ClosestWorldPoint( start );
      Vector3 clampedGoal = reachableWorkspace.ClosestWorldPoint( goal );
      WarnIfEndpointMoved( "start", start, clampedStart );
      WarnIfEndpointMoved( "goal", goal, clampedGoal );
      start = clampedStart;
      goal = clampedGoal;

      if ( writeClampedEndpointsBack ) {
        if ( startPoint != null )
          startPoint.position = start;
        if ( endPoint != null )
          endPoint.position = goal;
      }
    }

    private void ClampEndpointsToCartesianVolumeIfNeeded( ref Vector3 start, ref Vector3 goal )
    {
      if ( !clampEndpointsToWorkspace || !useReachableWorkspaceVolumeForCartesianRrt || reachableWorkspace == null )
        return;

      Vector3 clampedStart = reachableWorkspace.ClosestWorldPointInVolume( start );
      Vector3 clampedGoal = reachableWorkspace.ClosestWorldPointInVolume( goal );
      WarnIfEndpointMoved( "start", start, clampedStart );
      WarnIfEndpointMoved( "goal", goal, clampedGoal );
      start = clampedStart;
      goal = clampedGoal;

      if ( writeClampedEndpointsBack ) {
        if ( startPoint != null )
          startPoint.position = start;
        if ( endPoint != null )
          endPoint.position = goal;
      }
    }

    private void ClampControlPointIfNeeded( Transform point )
    {
      if ( !clampMovedControlPointsToWorkspace || reachableWorkspace == null || point == null )
        return;

      if ( plannerAlgorithm == PlannerAlgorithm.CartesianRrtStar && useReachableWorkspaceVolumeForCartesianRrt )
        point.position = reachableWorkspace.ClosestWorldPointInVolume( point.position );
      else
        point.position = reachableWorkspace.ClosestWorldPoint( point.position );
    }

    private void WarnIfEndpointMoved( string endpointName, Vector3 original, Vector3 clamped )
    {
      float moved = Vector3.Distance( original, clamped );
      if ( moved <= Mathf.Max( endpointClampWarningDistance, 0f ) )
        return;

      Debug.LogWarning( $"[SimpleTrajectoryPlanner] Clamped {endpointName} endpoint by {moved:0.00} m to the reachable workspace." );
    }

    private bool IsPointValid( Vector3 point )
    {
      if ( point.y < rrtMinWorldY )
        return false;

      if ( (int)rrtObstacleMask == 0 || rrtCollisionRadius <= 0f )
        return true;

      return !Physics.CheckSphere( point, rrtCollisionRadius, rrtObstacleMask, QueryTriggerInteraction.Ignore );
    }

    private bool IsSegmentValid( Vector3 from, Vector3 to )
    {
      if ( !IsPointValid( from ) || !IsPointValid( to ) )
        return false;

      float sqrDistance = ( to - from ).sqrMagnitude;
      if ( sqrDistance < 0.0001f )
        return true;

      if ( from.y < rrtMinWorldY || to.y < rrtMinWorldY )
        return false;

      if ( (int)rrtObstacleMask == 0 || rrtCollisionRadius <= 0f )
        return true;

      int steps = Mathf.Max( rrtWorkspaceEdgeSamples, 1 );
      for ( int i = 1; i < steps; i++ ) {
        Vector3 point = Vector3.Lerp( from, to, (float)i / steps );
        if ( !IsPointValid( point ) )
          return false;
      }

      return true;
    }

    private static int NearestNodeIndex( List<RrtNode> nodes, Vector3 sample )
    {
      int bestIndex = 0;
      float bestDistance = ( nodes[ 0 ].position - sample ).sqrMagnitude;
      for ( int i = 1; i < nodes.Count; i++ ) {
        float distance = ( nodes[ i ].position - sample ).sqrMagnitude;
        if ( distance >= bestDistance )
          continue;

        bestDistance = distance;
        bestIndex = i;
      }

      return bestIndex;
    }

    private int PlannedWaypointCount( Vector3 startWorld, Vector3 goalWorld )
    {
      int count = Mathf.Max( waypointCount, 2 );
      if ( !fitWaypointCountToDistance )
        return count;

      float distance = Vector3.Distance( startWorld, goalWorld );
      return PlannedWaypointCountForDistance( distance, count );
    }

    private int PlannedWaypointCountForPath( Vector3[] path, Vector3 startWorld, Vector3 goalWorld )
    {
      int count = Mathf.Max( waypointCount, 2 );
      if ( !fitWaypointCountToDistance )
        return count;

      float distance = path != null && path.Length >= 2 ? WorldPathLength( path ) : Vector3.Distance( startWorld, goalWorld );
      return PlannedWaypointCountForDistance( distance, count );
    }

    private int PlannedWaypointCountForDistance( float distance, int minimumCount )
    {
      float segmentLength = Mathf.Max( maxSegmentLength, 0.1f );
      int distanceBasedCount = Mathf.CeilToInt( distance / segmentLength ) + 1;
      return Mathf.Clamp( Mathf.Max( minimumCount, distanceBasedCount ), 2, Mathf.Max( maxWaypointCount, 2 ) );
    }

    private static float WorldPathLength( Vector3[] path )
    {
      if ( path == null || path.Length < 2 )
        return 0f;

      float length = 0f;
      for ( int i = 1; i < path.Length; i++ )
        length += Vector3.Distance( path[ i - 1 ], path[ i ] );

      return length;
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

    public string EditablePointName => editableControlPoint == EditableControlPoint.Start ? "起点" : "终点";

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

    private class RrtNode
    {
      public readonly Vector2 parameters;
      public readonly Vector3 position;
      public readonly int parentIndex;
      public readonly float cost;

      public RrtNode( Vector2 parameters, Vector3 position, int parentIndex, float cost )
      {
        this.parameters = parameters;
        this.position = position;
        this.parentIndex = parentIndex;
        this.cost = cost;
      }
    }

    private class BoundedCartesianRrtCollisionChecker : IRrtStar3DCollisionChecker
    {
      private readonly Bounds m_bounds;
      private readonly LayerMask m_obstacleMask;
      private readonly int m_edgeCheckResolution;
      private readonly float m_minWorldY;
      private readonly ReachableWorkspaceVisualizer m_workspaceVolume;

      public BoundedCartesianRrtCollisionChecker( Bounds bounds, LayerMask obstacleMask, int edgeCheckResolution, float minWorldY, ReachableWorkspaceVisualizer workspaceVolume )
      {
        m_bounds = bounds;
        m_obstacleMask = obstacleMask;
        m_edgeCheckResolution = Mathf.Max( edgeCheckResolution, 1 );
        m_minWorldY = minWorldY;
        m_workspaceVolume = workspaceVolume;
      }

      public bool IsPointValid( Vector3 point, float radius )
      {
        if ( point.y < m_minWorldY || !m_bounds.Contains( point ) )
          return false;

        if ( m_workspaceVolume != null && !m_workspaceVolume.ContainsWorldPointInVolume( point ) )
          return false;

        if ( (int)m_obstacleMask == 0 || radius <= 0f )
          return true;

        return !Physics.CheckSphere( point, radius, m_obstacleMask, QueryTriggerInteraction.Ignore );
      }

      public bool IsSegmentValid( Vector3 from, Vector3 to, float radius )
      {
        if ( !IsPointValid( from, radius ) || !IsPointValid( to, radius ) )
          return false;

        if ( ( to - from ).sqrMagnitude < 0.0001f )
          return true;

        for ( int i = 1; i < m_edgeCheckResolution; i++ ) {
          Vector3 point = Vector3.Lerp( from, to, (float)i / m_edgeCheckResolution );
          if ( !IsPointValid( point, radius ) )
            return false;
        }

        return true;
      }
    }

    private class ReachableWorkspaceLocalRrtCollisionChecker : IRrtStar3DCollisionChecker
    {
      private const float BoundaryTolerance = 0.015f;

      private readonly ReachableWorkspaceVisualizer m_workspace;
      private readonly Bounds m_localBounds;
      private readonly LayerMask m_obstacleMask;
      private readonly int m_edgeCheckResolution;
      private readonly float m_minWorldY;

      public ReachableWorkspaceLocalRrtCollisionChecker(
        ReachableWorkspaceVisualizer workspace,
        Bounds localBounds,
        LayerMask obstacleMask,
        int edgeCheckResolution,
        float minWorldY )
      {
        m_workspace = workspace;
        m_localBounds = localBounds;
        m_obstacleMask = obstacleMask;
        m_edgeCheckResolution = Mathf.Max( edgeCheckResolution, 1 );
        m_minWorldY = minWorldY;
      }

      public bool IsPointValid( Vector3 localPoint, float radius )
      {
        if ( m_workspace == null || !m_localBounds.Contains( localPoint ) )
          return false;

        if ( !m_workspace.ContainsLocalPointInVolume( localPoint ) ) {
          Vector3 closest = m_workspace.ClosestLocalPointInVolume( localPoint );
          if ( Vector3.Distance( closest, localPoint ) > BoundaryTolerance )
            return false;
        }

        Vector3 worldPoint = m_workspace.LocalPointToWorld( localPoint );
        if ( worldPoint.y < m_minWorldY )
          return false;

        if ( (int)m_obstacleMask == 0 || radius <= 0f )
          return true;

        return !Physics.CheckSphere( worldPoint, radius, m_obstacleMask, QueryTriggerInteraction.Ignore );
      }

      public bool IsSegmentValid( Vector3 fromLocal, Vector3 toLocal, float radius )
      {
        if ( !IsPointValid( fromLocal, radius ) || !IsPointValid( toLocal, radius ) )
          return false;

        if ( ( toLocal - fromLocal ).sqrMagnitude < 0.0001f )
          return true;

        for ( int i = 1; i < m_edgeCheckResolution; i++ ) {
          Vector3 localPoint = Vector3.Lerp( fromLocal, toLocal, (float)i / m_edgeCheckResolution );
          if ( !IsPointValid( localPoint, radius ) )
            return false;
        }

        return true;
      }
    }

    private void OnDrawGizmosSelected()
    {
      if ( drawGizmos && plannerAlgorithm == PlannerAlgorithm.CartesianRrtStar ) {
        Bounds bounds = CartesianPlanningBounds();
        Gizmos.color = new Color( previewColor.r, previewColor.g, previewColor.b, 0.45f );
        Gizmos.DrawWireCube( bounds.center, bounds.size );
      }

      if ( !drawGizmos || !TryGetConfiguredEndpoints( out Vector3 start, out Vector3 goal, false ) )
        return;

      Vector3[] path = GenerateArcPath( start, goal );
      Gizmos.color = previewColor;
      for ( int i = 0; i < path.Length; i++ ) {
        Gizmos.DrawWireSphere( path[ i ], previewRadius );
        if ( i + 1 < path.Length )
          Gizmos.DrawLine( path[ i ], path[ i + 1 ] );
      }
    }
  }
}
