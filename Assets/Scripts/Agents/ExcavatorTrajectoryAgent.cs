using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

namespace RLExcavator
{
  /// <summary>
  /// 挖掘机铲斗轨迹跟踪智能体。
  ///
  /// 该 Agent 只做轨迹跟踪：根据 bucketTip 与当前路径点的相对位置输出
  /// boom / stick / bucket / swing 四个连续动作。它不负责挖土奖励，也不依赖地形重置。
  ///
  /// 【关键设计】
  ///   - Heuristic 和策略推理共用 OnActionReceived 动作入口
  ///   - Episode 只通过 ExcavatorController 复位挖掘机姿态
  ///   - 只控制当前环境中的 Trajectory 挖掘机；与其它 Agent 的互斥由 prefab/scene 配置保证
  /// </summary>
  public class ExcavatorTrajectoryAgent : Agent
  {
    // Inspector 引用

    [Header("References")]
    [Tooltip("挖掘机关节控制器，负责把归一化动作转换为 AGX 关节目标速度")]
    public ExcavatorController controller;

    [Tooltip("铲斗尖的 Transform，用于计算到目标的距离")]
    public Transform bucketTip;

    [Tooltip("轨迹路径点提供器。可以是单点目标，也可以是多点路径")]
    public TrajectoryProvider trajectory;

    [Tooltip("成功到达目标点时触发的共享视觉提示（地板变绿等）")]
    public SuccessIndicator successIndicator;

    [Tooltip("当前训练环境根节点。多环境并行时用于把自动查找限制在本环境内；为空则使用当前对象的最顶层父节点。")]
    public Transform environmentRoot;

    // 任务参数

    [Header("Task")]
    [Tooltip("铲斗尖距离当前目标点小于该值时认为到达（单位：米）")]
    public float targetThreshold = 0.25f;

    [Tooltip("目标点有可视化小球时，把小球半径也计入触达阈值")]
    public bool includeTargetVisualRadiusInThreshold = true;

    [Tooltip("到达单点/路径终点后结束 episode。训练单点跟踪时保持开启")]
    public bool endEpisodeOnTargetReached = true;

    [Tooltip("距离归一化参考值，用于把距离缩放到合理范围")]
    public float distanceNormalizer = 8.0f;

    [Tooltip("局部位置归一化参考值")]
    public float positionNormalizer = 10.0f;

    [Header("Spinelli-style waypoint tracking")]
    [Tooltip("前视 waypoint 数量。默认保持 3；如修改，必须同步 Behavior Parameters 的观测维度。")]
    public int waypointLookahead = 3; // waypointLookahead数量影响了观测维度,规则是waypointLookahead * 3,因为每个waypoint都有x,y,z三个坐标
    [Tooltip("路径 tube 半径。铲斗尖应尽量保持在相邻 waypoint 连线形成的 tube 内。")]
    public float tubeRadius = 1.0f;

    // 奖励权重

    [Header("Reward")]
    [Tooltip("距离惩罚权重：距离目标越远，惩罚越大（鼓励靠近）")]
    public float wDistance = 0.005f;

    [Tooltip("进度奖励权重：每步距离减少量，单位为米（鼓励持续靠近目标）")]
    public float wProgress = 1.0f;

    [Tooltip("动作惩罚权重：避免关节乱抖（鼓励平滑动作）")]
    public float wActionCost = 0.0002f;

    [Tooltip("到达单个路径点的奖励")]
    public float waypointBonus = 1.0f;

    [Tooltip("到达路径终点的奖励（成功完成整条轨迹）")]
    public float pathCompleteBonus = 5.0f;

    [Tooltip("粗目标势能奖励权重：只有距离 waypoint 变近时才给正奖励，避免停在目标附近刷分")]
    public float wTargetCoarse = 0.2f;

    [Tooltip("细目标势能奖励权重：只有精确接近 waypoint 的过程才给正奖励，避免静止刷分")]
    public float wTargetFine = 0.3f;

    [Tooltip("tube 内推进奖励权重：只有在 tube 内并沿当前 waypoint 继续推进时才给正奖励")]
    public float wTube = 0.2f;

    [Tooltip("tube 外惩罚权重：偏离规划路径时惩罚")]
    public float wTubeViolation = 0.05f;

    [Tooltip("动作变化惩罚权重：惩罚相邻 action 的突变")]
    public float wActionDelta = 0.0005f;

    [Tooltip("终点稳定奖励权重：到达最后 waypoint 后鼓励动作变小、减少过冲")]
    public float wFinalStabilization = 0.05f;

    [Tooltip("单个 waypoint 最多允许消耗的决策步数。超过后结束 episode，避免长期卡点刷无效样本；0 表示关闭。")]
    public int maxStepsPerWaypoint = 1200;

    [Tooltip("单个 waypoint 超时后的惩罚。")]
    public float waypointTimeoutPenalty = 1.0f;

    // Episode 复位

    [Header("Episode reset")]
    [Tooltip("是否在 episode 开始时重置挖掘机姿态到初始位置")]
    public bool resetPoseOnEpisodeBegin = true;

    [Tooltip("姿态复位时保持 LockController 的物理步数，-1 表示使用 controller 默认值")]
    public int poseResetSettleSteps = -1;

    // 调试选项

    [Header("Debug")]
    [Tooltip("打印 episode 开始/结束等事件日志")]
    public bool debugEpisodeEvents = true;

    [Tooltip("Agent 启用时自动禁用键盘测试脚本，避免两者同时写关节")]
    public bool disableKeyboardTesterWhenAgentEnabled = true;

    [Tooltip("引用为空时只在 environmentRoot 内自动查找。多环境并行时建议显式绑定引用。")]
    public bool autoFindReferences = true;

    [Tooltip("打印前若干次动作，用于确认 Heuristic 或策略是否真的给了动作")]
    public bool debugActions = false;

    [Tooltip("最多打印的动作次数")]
    public int debugActionPrints = 20;

    [Tooltip("向 TensorBoard 记录轨迹跟踪评价指标")]
    public bool recordTrainingStats = true;

    // 运行时状态

    /// <summary>上一步铲斗尖到目标的距离，用于计算进度奖励</summary>
    private float m_previousDistance = 0f;

    /// <summary>是否正在等待 episode 复位完成</summary>
    private bool m_waitingForPoseReset = false;

    /// <summary>是否已经打印过缺失引用的警告（避免重复打印）</summary>
    private bool m_missingReferenceWarned = false;

    /// <summary>已打印的动作调试次数计数</summary>
    private int m_debugActionPrintCount = 0;

    /// <summary>当前 episode 是否已经进入有效统计阶段</summary>
    private bool m_episodeActive = false;

    /// <summary>当前 episode 的统计是否已经写出，避免手动 EndEpisode 后重复记录</summary>
    private bool m_episodeStatsRecorded = true;

    /// <summary>当前 episode 已到达的路径点数量</summary>
    private int m_episodeReachedTargets = 0;

    /// <summary>当前 episode 是否已经完成整条路径</summary>
    private bool m_episodeCompletedPath = false;

    /// <summary>当前 episode 内距离目标最近的一次距离</summary>
    private float m_episodeMinDistance = 0f;

    /// <summary>当前 waypoint 已消耗的决策步数，用于检测卡点。</summary>
    private int m_stepsOnCurrentWaypoint = 0;

    /// <summary>当前 episode 是否因为 waypoint 超时结束。</summary>
    private bool m_episodeTimedOut = false;

    private int m_episodeStepSamples = 0;
    private float m_episodeActionMagnitudeSum = 0f;
    private float m_episodePositiveProgressSum = 0f;
    private float m_episodeSignedProgressSum = 0f;

    private Vector4 m_previousAction = Vector4.zero;
    private Vector4 m_previousJointPose = Vector4.zero;
    private Vector4 m_jointVelocity = Vector4.zero;
    private Vector3 m_previousTipLocal = Vector3.zero;
    private Vector3 m_tipVelocityLocal = Vector3.zero;
    private Vector3 m_episodeStartTipPosition = Vector3.zero;
    private bool m_hasPreviousMotionState = false;

    // UI / 调试只读属性

    /// <summary>当前 episode 编号</summary>
    [HideInInspector] public int CurrentEpisode = 0;

    /// <summary>当前累积奖励（ML-Agents 内部维护）</summary>
    public float CurrentScore => GetCumulativeReward();

    /// <summary>当前铲斗尖到目标的距离</summary>
    public float CurrentDistanceToTarget => CurrentDistanceToTargetInternal();

    /// <summary>当前到达目标的阈值（含可视化小球半径）</summary>
    public float CurrentTargetThreshold => CurrentReachThreshold();

    /// <summary>当前正在跟踪的路径点索引</summary>
    public int CurrentTargetIndex => trajectory != null ? trajectory.CurrentIndex : 0;

    /// <summary>路径点总数</summary>
    public int CurrentTargetCount => trajectory != null ? trajectory.TargetCount : HasTarget() ? 1 : 0;

    /// <summary>当前路径完成进度，范围 0 到 1</summary>
    public float CurrentPathProgress => trajectory != null ? trajectory.Progress : 0f;

    public override void Initialize()
    {
      CurrentEpisode = 0;

      if ( environmentRoot == null )
        environmentRoot = transform.root;

      if ( autoFindReferences )
        ResolveMissingReferences();

      ValidateReferences();

      if ( disableKeyboardTesterWhenAgentEnabled ) {
        var keyboardTester = GetComponent<ExcavatorKeyboardTester>();
        if ( keyboardTester != null )
          keyboardTester.enabled = false;
      }
    }

    public override void OnEpisodeBegin()
    {
      RecordEpisodeStatsIfNeeded( false );

      CurrentEpisode++;
      m_missingReferenceWarned = false;
      m_debugActionPrintCount = 0;
      m_waitingForPoseReset = true;
      m_episodeActive = false;
      m_episodeStatsRecorded = false;
      m_episodeReachedTargets = 0;
      m_episodeCompletedPath = false;
      m_episodeMinDistance = float.PositiveInfinity;
      m_stepsOnCurrentWaypoint = 0;
      m_episodeTimedOut = false;
      m_episodeStepSamples = 0;
      m_episodeActionMagnitudeSum = 0f;
      m_episodePositiveProgressSum = 0f;
      m_episodeSignedProgressSum = 0f;

      if ( trajectory != null )
        trajectory.ResetPath();

      if ( debugEpisodeEvents )
        Debug.Log( $"[ExcavatorTrajectoryAgent] Episode {CurrentEpisode} begin." );

      if ( resetPoseOnEpisodeBegin && controller != null ) {
        controller.BeginHomePoseReset( poseResetSettleSteps );
        return;
      }

      BeginEpisodeMeasurements();
    }

    /// <summary>
    /// 31 维观测：
    /// bucketTip 局部位置(3)、bucketTip 局部速度(3)、关节位置(4)、关节速度(4)、
    /// 上一动作(4)、未来 3 个 waypoint 误差(9)、路径进度(1)、是否最后目标(1)、
    /// tube signed distance(1)、episode 进度(1)。
    /// </summary>
    public override void CollectObservations( VectorSensor sensor )
    {
      Vector3 tipLocal = Vector3.zero;

      if ( bucketTip != null )
        tipLocal = transform.InverseTransformPoint( bucketTip.position );

      UpdateMotionEstimates( tipLocal );

      AddVectorObservation( sensor, tipLocal / Mathf.Max( positionNormalizer, 0.01f ) );
      AddVectorObservation( sensor, m_tipVelocityLocal );

      sensor.AddObservation( controller != null ? controller.BoomNormalizedPosition   : 0f );
      sensor.AddObservation( controller != null ? controller.StickNormalizedPosition  : 0f );
      sensor.AddObservation( controller != null ? controller.BucketNormalizedPosition : 0f );
      sensor.AddObservation( controller != null ? controller.SwingNormalizedPosition  : 0f );

      sensor.AddObservation( m_jointVelocity.x );
      sensor.AddObservation( m_jointVelocity.y );
      sensor.AddObservation( m_jointVelocity.z );
      sensor.AddObservation( m_jointVelocity.w );

      sensor.AddObservation( m_previousAction.x );
      sensor.AddObservation( m_previousAction.y );
      sensor.AddObservation( m_previousAction.z );
      sensor.AddObservation( m_previousAction.w );

      AddLookaheadWaypointObservations( sensor, tipLocal );

      sensor.AddObservation( trajectory != null ? trajectory.Progress : 0f );
      sensor.AddObservation( IsFinalWaypoint() ? 1f : 0f );
      sensor.AddObservation( NormalizedTubeDistance() );
      sensor.AddObservation( MaxStep > 0 ? (float)StepCount / MaxStep : 0f );
    }

    /// <summary>
    /// 4 维连续动作：boom、stick、bucket、swing 的归一化目标速度。
    /// </summary>
    public override void OnActionReceived( ActionBuffers actions )
    {
      if ( controller != null && controller.IsHoming )
        return;

      if ( m_waitingForPoseReset )
        BeginEpisodeMeasurements();

      var a = actions.ContinuousActions;
      float boom = a.Length > 0 ? a[ 0 ] : 0f;
      float stick = a.Length > 1 ? a[ 1 ] : 0f;
      float bucket = a.Length > 2 ? a[ 2 ] : 0f;
      float swing = a.Length > 3 ? a[ 3 ] : 0f;
      Vector4 action = new Vector4( boom, stick, bucket, swing );

      if ( debugActions && m_debugActionPrintCount < debugActionPrints ) {
        Debug.Log( $"[ExcavatorTrajectoryAgent] action boom={boom:F3}, stick={stick:F3}, bucket={bucket:F3}, swing={swing:F3}" );
        m_debugActionPrintCount++;
      }

      if ( controller != null )
        controller.SetNormalizedSpeeds( boom, stick, bucket, swing );

      if ( bucketTip == null || trajectory == null ) {
        if ( debugEpisodeEvents && !m_missingReferenceWarned ) {
          Debug.LogWarning( "[ExcavatorTrajectoryAgent] Missing bucketTip or trajectory reference. Ending episode." );
          m_missingReferenceWarned = true;
        }
        AddReward( -1f );
        EndEpisodeWithStats( false );
        return;
      }

      float distance = CurrentDistanceToTargetInternal();
      m_episodeMinDistance = Mathf.Min( m_episodeMinDistance, distance );
      float previousDistance = m_previousDistance;
      float progressDelta = previousDistance - distance;
      m_previousDistance = distance;
      float distanceScale = Mathf.Max( distanceNormalizer, 0.01f );
      float normalizedDistance = distance / distanceScale;
      float previousNormalizedDistance = previousDistance / distanceScale;
      float actionMagnitude = Mathf.Abs( boom ) + Mathf.Abs( stick ) + Mathf.Abs( bucket ) + Mathf.Abs( swing );
      float actionDelta = ( action - m_previousAction ).sqrMagnitude;
      float tubeDistance = NormalizedTubeDistance();
      float clippedProgressDelta = Mathf.Clamp( progressDelta, -0.25f, 0.25f );
      float positiveProgressDelta = Mathf.Max( clippedProgressDelta, 0f );
      float coarsePotentialDelta = Mathf.Exp( -normalizedDistance ) - Mathf.Exp( -previousNormalizedDistance );
      float finePotentialDelta =
        Mathf.Exp( -normalizedDistance * normalizedDistance * 16f ) -
        Mathf.Exp( -previousNormalizedDistance * previousNormalizedDistance * 16f );
      m_stepsOnCurrentWaypoint++;
      m_episodeStepSamples++;
      m_episodeActionMagnitudeSum += actionMagnitude;
      m_episodePositiveProgressSum += positiveProgressDelta;
      m_episodeSignedProgressSum += clippedProgressDelta;

      AddReward( -wDistance * normalizedDistance );
      AddReward( wProgress * clippedProgressDelta );
      AddReward( wTargetCoarse * coarsePotentialDelta );
      AddReward( wTargetFine * finePotentialDelta );
      AddReward( tubeDistance >= 0f ? wTube * positiveProgressDelta * Mathf.Clamp01( tubeDistance ) : wTubeViolation * tubeDistance );
      AddReward( -wActionCost * action.sqrMagnitude );
      AddReward( -wActionDelta * actionDelta );
      AddReward( -1f / Mathf.Max( MaxStep, 1 ) );

      if ( distance <= CurrentReachThreshold() && !m_episodeCompletedPath ) {
        bool completed = trajectory == null || trajectory.AdvanceTarget();
        m_episodeReachedTargets++;
        m_stepsOnCurrentWaypoint = 0;
        AddReward( waypointBonus );

        if ( completed ) {
          m_episodeCompletedPath = true;
          AddReward( pathCompleteBonus );
          if ( debugEpisodeEvents )
            Debug.Log( $"[ExcavatorTrajectoryAgent] Path complete. episode={CurrentEpisode}, score={GetCumulativeReward():F3}" );

          if ( successIndicator != null )
            successIndicator.Show();

          AddReward( -wFinalStabilization * actionMagnitude );

          if ( endEpisodeOnTargetReached )
            EndEpisodeWithStats( true );
        }
        else {
          m_previousDistance = CurrentDistanceToTargetInternal();
          if ( debugEpisodeEvents )
            Debug.Log( $"[ExcavatorTrajectoryAgent] Waypoint reached. next={trajectory.CurrentIndex}, distance={m_previousDistance:F2}m" );
        }
      }
      else if ( maxStepsPerWaypoint > 0 && m_stepsOnCurrentWaypoint >= maxStepsPerWaypoint ) {
        m_episodeTimedOut = true;
        AddReward( -Mathf.Max( waypointTimeoutPenalty, 0f ) );
        if ( debugEpisodeEvents )
          Debug.Log( $"[ExcavatorTrajectoryAgent] Waypoint timeout. episode={CurrentEpisode}, target={CurrentTargetIndex}, distance={distance:F2}m" );

        EndEpisodeWithStats( false );
        return;
      }

      m_previousAction = action;
    }

    // Heuristic Only 下使用与 DigAgent 相同的键盘映射。
    public override void Heuristic( in ActionBuffers actionsOut )
    {
      ExcavatorKeyboardInput.WriteDefaultTo( actionsOut.ContinuousActions );

      if ( debugActions && m_debugActionPrintCount < debugActionPrints ) {
        var command = ExcavatorKeyboardInput.ReadDefault();
        if ( command.HasInput ) {
          Debug.Log( $"[ExcavatorTrajectoryAgent] heuristic boom={command.boom:F3}, stick={command.stick:F3}, bucket={command.bucket:F3}, swing={command.swing:F3}" );
          m_debugActionPrintCount++;
        }
      }
    }

    // 辅助方法

    /// <summary>
    /// 姿态复位完成后调用，记录第一步距离作为进度奖励基准。
    /// </summary>
    private void BeginEpisodeMeasurements()
    {
      m_waitingForPoseReset = false;
      m_previousDistance = CurrentDistanceToTargetInternal();
      m_episodeMinDistance = bucketTip != null && trajectory != null ? m_previousDistance : 0f;
      m_episodeActive = true;
      m_episodeStartTipPosition = bucketTip != null ? bucketTip.position : transform.position;
      ResetMotionHistory();

      if ( debugEpisodeEvents )
        Debug.Log( $"[ExcavatorTrajectoryAgent] Episode {CurrentEpisode} begin. distance={m_previousDistance:F2}m" );
    }

    private void EndEpisodeWithStats( bool completed )
    {
      RecordEpisodeStatsIfNeeded( completed );
      EndEpisode();
    }

    private void RecordEpisodeStatsIfNeeded( bool completed )
    {
      if ( !recordTrainingStats || !m_episodeActive || m_episodeStatsRecorded )
        return;

      m_episodeStatsRecorded = true;

      float finalDistance = CurrentDistanceToTargetInternal();
      float minDistance = float.IsPositiveInfinity( m_episodeMinDistance ) ? finalDistance : m_episodeMinDistance;
      float pathProgress = trajectory != null ? trajectory.Progress : 0f;
      float targetCount = Mathf.Max( CurrentTargetCount, 1 );

      var stats = Academy.Instance.StatsRecorder;
      stats.Add( "Trajectory/SuccessRate", completed || m_episodeCompletedPath ? 1f : 0f, StatAggregationMethod.Average );
      stats.Add( "Trajectory/WaypointsReached", m_episodeReachedTargets, StatAggregationMethod.Average );
      stats.Add( "Trajectory/WaypointReachRate", m_episodeReachedTargets / targetCount, StatAggregationMethod.Average );
      stats.Add( "Trajectory/WaypointTimeoutRate", m_episodeTimedOut ? 1f : 0f, StatAggregationMethod.Average );
      stats.Add( "Trajectory/PathProgress", pathProgress, StatAggregationMethod.Average );
      stats.Add( "Trajectory/FinalDistance", finalDistance, StatAggregationMethod.Average );
      stats.Add( "Trajectory/MinDistance", minDistance, StatAggregationMethod.Average );

      float sampleCount = Mathf.Max( m_episodeStepSamples, 1 );
      stats.Add( "Trajectory/MeanActionMagnitude", m_episodeActionMagnitudeSum / sampleCount, StatAggregationMethod.Average );
      stats.Add( "Trajectory/MeanPositiveProgress", m_episodePositiveProgressSum / sampleCount, StatAggregationMethod.Average );
      stats.Add( "Trajectory/SignedProgress", m_episodeSignedProgressSum, StatAggregationMethod.Average );
    }

    private float CurrentDistanceToTargetInternal()
    {
      if ( bucketTip == null || trajectory == null )
        return 0f;

      return Vector3.Distance( bucketTip.position, trajectory.CurrentTargetPosition );
    }

    private bool HasTarget()
    {
      return trajectory != null;
    }

    private float CurrentReachThreshold()
    {
      float threshold = Mathf.Max( targetThreshold, 0f );

      if ( includeTargetVisualRadiusInThreshold && trajectory != null ) {
        var currentTarget = trajectory.CurrentTargetTransform;
        if ( currentTarget != null ) {
          var visual = currentTarget.GetComponent<TargetPointVisual>();
          if ( visual != null )
            threshold += Mathf.Max( visual.radius, 0f );
        }
      }

      return threshold;
    }

    private void AddLookaheadWaypointObservations( VectorSensor sensor, Vector3 tipLocal )
    {
      int lookahead = Mathf.Clamp( waypointLookahead, 1, 3 );
      int baseIndex = trajectory != null ? trajectory.CurrentIndex : 0;

      for ( int i = 0; i < lookahead; i++ ) {
        Vector3 waypointLocal = Vector3.zero;
        if ( trajectory != null )
          waypointLocal = transform.InverseTransformPoint( trajectory.GetWaypointPosition( baseIndex + i ) );

        AddVectorObservation( sensor, ( waypointLocal - tipLocal ) / Mathf.Max( distanceNormalizer, 0.01f ) );
      }

      for ( int i = lookahead; i < 3; i++ )
        AddVectorObservation( sensor, Vector3.zero );
    }

    private bool IsFinalWaypoint()
    {
      return trajectory == null || trajectory.CurrentIndex >= trajectory.TargetCount - 1;
    }

    private float NormalizedTubeDistance()
    {
      if ( bucketTip == null || trajectory == null || trajectory.TargetCount <= 1 )
        return 0f;

      Vector3 tip = bucketTip.position;
      int current = trajectory.CurrentIndex;
      Vector3 from = current > 0 ? trajectory.GetWaypointPosition( current - 1 ) : m_episodeStartTipPosition;
      Vector3 to = trajectory.GetWaypointPosition( current );
      float signedDistance = TubeSignedDistance( tip, from, to, Mathf.Max( tubeRadius, 0.01f ) );
      return signedDistance / Mathf.Max( tubeRadius, 0.01f );
    }

    private static float TubeSignedDistance( Vector3 point, Vector3 from, Vector3 to, float radius )
    {
      Vector3 segment = to - from;
      float lengthSq = segment.sqrMagnitude;
      if ( lengthSq < 1e-6f )
        return radius - Vector3.Distance( point, to );

      float t = Mathf.Clamp01( Vector3.Dot( point - from, segment ) / lengthSq );
      Vector3 closest = from + segment * t;
      return radius - Vector3.Distance( point, closest );
    }

    private void UpdateMotionEstimates( Vector3 tipLocal )
    {
      Vector4 jointPose = new Vector4(
        controller != null ? controller.BoomNormalizedPosition   : 0f,
        controller != null ? controller.StickNormalizedPosition  : 0f,
        controller != null ? controller.BucketNormalizedPosition : 0f,
        controller != null ? controller.SwingNormalizedPosition  : 0f
      );

      if ( m_hasPreviousMotionState ) {
        float dt = Mathf.Max( Time.fixedDeltaTime, 0.001f );
        m_jointVelocity = ( jointPose - m_previousJointPose ) / dt;
        m_tipVelocityLocal = ( tipLocal - m_previousTipLocal ) / dt / Mathf.Max( positionNormalizer, 0.01f );
      }
      else {
        m_jointVelocity = Vector4.zero;
        m_tipVelocityLocal = Vector3.zero;
        m_hasPreviousMotionState = true;
      }

      m_previousJointPose = jointPose;
      m_previousTipLocal = tipLocal;
    }

    private void ResetMotionHistory()
    {
      m_previousAction = Vector4.zero;
      m_previousJointPose = new Vector4(
        controller != null ? controller.BoomNormalizedPosition   : 0f,
        controller != null ? controller.StickNormalizedPosition  : 0f,
        controller != null ? controller.BucketNormalizedPosition : 0f,
        controller != null ? controller.SwingNormalizedPosition  : 0f
      );
      m_previousTipLocal = bucketTip != null ? transform.InverseTransformPoint( bucketTip.position ) : Vector3.zero;
      m_jointVelocity = Vector4.zero;
      m_tipVelocityLocal = Vector3.zero;
      m_hasPreviousMotionState = true;
    }

    // 环境内查找工具。优先在 Inspector 显式拖引用，自动查找只作为兜底。
    private void ResolveMissingReferences()
    {
      if ( controller == null )
        controller = FindInEnvironment<ExcavatorController>();

      if ( trajectory == null )
        trajectory = FindInEnvironment<TrajectoryProvider>();

      if ( bucketTip == null ) {
        var bucketTipObject = FindInEnvironmentByName( "Traj_BucketTip" ) ?? FindInEnvironmentByName( "BucketTip" );
        if ( bucketTipObject != null )
          bucketTip = bucketTipObject.transform;
      }

      if ( successIndicator == null )
        successIndicator = FindOrCreateSuccessIndicator();
    }

    private void ValidateReferences()
    {
      if ( controller == null || bucketTip == null || trajectory == null ) {
        Debug.LogWarning(
          $"[ExcavatorTrajectoryAgent] Missing references on {name}. " +
          "For multi-environment training, bind controller, bucketTip, trajectory, and successIndicator inside each environment prefab." );
      }
    }

    // 在环境中查找指定类型的组件。
    private T FindInEnvironment<T>() where T : Component
    {
      // 这里需要在运行开始之前,在unity上为每一个environment的agent绑定对应的environmentRoot
      Transform root = environmentRoot != null ? environmentRoot : transform.root;
      return root != null ? root.GetComponentInChildren<T>( true ) : null;
    }

    private GameObject FindInEnvironmentByName( string objectName )
    {
      Transform root = environmentRoot != null ? environmentRoot : transform.root;
      var match = root != null ? FindChildByName( root, objectName ) : null;
      return match != null ? match.gameObject : null;
    }

    /// <summary>
    /// 自动查找成功提示器。查找范围限定在当前环境根节点内，避免多环境时拿到别的环境对象。
    /// </summary>
    private SuccessIndicator FindOrCreateSuccessIndicator()
    {
      var existing = FindInEnvironment<SuccessIndicator>();
      if ( existing != null )
        return existing;

      var frame = FindInEnvironmentByName( "TrajBox" ) ?? FindInEnvironmentByName( "Frame" );
      if ( frame == null )
        return null;

      var indicator = frame.GetComponent<SuccessIndicator>();
      if ( indicator == null )
        indicator = frame.AddComponent<SuccessIndicator>();

      if ( indicator.targetRenderer == null )
        indicator.targetRenderer = frame.GetComponentInChildren<Renderer>();

      return indicator;
    }

    private static Transform FindChildByName( Transform parent, string objectName )
    {
      if ( parent.name == objectName )
        return parent;

      foreach ( Transform child in parent ) {
        var match = FindChildByName( child, objectName );
        if ( match != null )
          return match;
      }
      return null;
    }

    private static void AddVectorObservation( VectorSensor sensor, Vector3 value )
    {
      sensor.AddObservation( value.x );
      sensor.AddObservation( value.y );
      sensor.AddObservation( value.z );
    }
  }
}
