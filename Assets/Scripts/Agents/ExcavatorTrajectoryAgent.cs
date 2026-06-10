using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
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
  ///   - 与 ExcavatorDigAgent 互斥，避免两个 Agent 同时写控制器
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

    // 奖励权重

    [Header("Reward")]
    [Tooltip("距离惩罚权重：距离目标越远，惩罚越大（鼓励靠近）")]
    public float wDistance = 0.01f;

    [Tooltip("进度奖励权重：每步距离减少量（鼓励持续靠近目标）")]
    public float wProgress = 0.2f;

    [Tooltip("动作惩罚权重：避免关节乱抖（鼓励平滑动作）")]
    public float wActionCost = 0.001f;

    [Tooltip("到达单个路径点的奖励")]
    public float waypointBonus = 0.2f;

    [Tooltip("到达路径终点的奖励（成功完成整条轨迹）")]
    public float pathCompleteBonus = 1.0f;

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

    [Tooltip("Agent 启用时自动禁用挖土 Agent，避免两个 Agent 同时接管控制")]
    public bool disableDigAgentWhenEnabled = true;

    [Tooltip("引用为空时在当前 scene 内自动查找，减少 Inspector 手动绑定错误")]
    public bool autoFindReferences = true;

    [Tooltip("打印前若干次动作，用于确认 Heuristic 或策略是否真的给了动作")]
    public bool debugActions = false;

    [Tooltip("最多打印的动作次数")]
    public int debugActionPrints = 20;

    // 运行时状态

    /// <summary>上一步铲斗尖到目标的距离，用于计算进度奖励</summary>
    private float m_previousDistance = 0f;

    /// <summary>是否正在等待 episode 复位完成</summary>
    private bool m_waitingForPoseReset = false;

    /// <summary>是否已经打印过缺失引用的警告（避免重复打印）</summary>
    private bool m_missingReferenceWarned = false;

    /// <summary>已打印的动作调试次数计数</summary>
    private int m_debugActionPrintCount = 0;

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

    public override void Initialize()
    {
      CurrentEpisode = 0;

      if ( autoFindReferences && trajectory == null )
        trajectory = FindInScene<TrajectoryProvider>();

      if ( autoFindReferences && bucketTip == null ) {
        var bucketTipObject = FindInSceneByName( "BucketTip" );
        if ( bucketTipObject != null )
          bucketTip = bucketTipObject.transform;
      }

      if ( autoFindReferences && successIndicator == null )
        successIndicator = FindOrCreateSuccessIndicator();

      if ( disableKeyboardTesterWhenAgentEnabled ) {
        var keyboardTester = GetComponent<ExcavatorKeyboardTester>();
        if ( keyboardTester != null )
          keyboardTester.enabled = false;
      }

      if ( disableDigAgentWhenEnabled && IsBehaviorName( "ExcavatorTrajectory" ) ) {
        var digAgent = GetComponent<ExcavatorDigAgent>();
        if ( digAgent != null )
          digAgent.enabled = false;
      }
    }

    public override void OnEpisodeBegin()
    {
      CurrentEpisode++;
      m_missingReferenceWarned = false;
      m_debugActionPrintCount = 0;
      m_waitingForPoseReset = true;

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
    /// 15 维观测：
    /// bucketTip 局部位置(3)、目标局部位置(3)、误差向量(3)、
    /// 关节位置(4)、路径进度(1)、episode 进度(1)。
    /// </summary>
    public override void CollectObservations( VectorSensor sensor )
    {
      Vector3 tipLocal = Vector3.zero;
      Vector3 targetLocal = Vector3.zero;
      Vector3 errorLocal = Vector3.zero;

      if ( bucketTip != null )
        tipLocal = transform.InverseTransformPoint( bucketTip.position );

      if ( trajectory != null )
        targetLocal = transform.InverseTransformPoint( trajectory.CurrentTargetPosition );

      errorLocal = targetLocal - tipLocal;

      AddVectorObservation( sensor, tipLocal / Mathf.Max( positionNormalizer, 0.01f ) );
      AddVectorObservation( sensor, targetLocal / Mathf.Max( positionNormalizer, 0.01f ) );
      AddVectorObservation( sensor, errorLocal / Mathf.Max( distanceNormalizer, 0.01f ) );

      sensor.AddObservation( controller != null ? controller.BoomNormalizedPosition   : 0f );
      sensor.AddObservation( controller != null ? controller.StickNormalizedPosition  : 0f );
      sensor.AddObservation( controller != null ? controller.BucketNormalizedPosition : 0f );
      sensor.AddObservation( controller != null ? controller.SwingNormalizedPosition  : 0f );

      sensor.AddObservation( trajectory != null ? trajectory.Progress : 0f );
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
        EndEpisode();
        return;
      }

      float distance = CurrentDistanceToTargetInternal();
      float progressDelta = m_previousDistance - distance;
      m_previousDistance = distance;

      AddReward( -wDistance * distance / Mathf.Max( distanceNormalizer, 0.01f ) );
      AddReward( wProgress * progressDelta / Mathf.Max( distanceNormalizer, 0.01f ) );
      AddReward( -wActionCost * ( Mathf.Abs( boom ) + Mathf.Abs( stick ) + Mathf.Abs( bucket ) + Mathf.Abs( swing ) ) );
      AddReward( -1f / Mathf.Max( MaxStep, 1 ) );

      if ( distance <= CurrentReachThreshold() ) {
        bool completed = trajectory == null || trajectory.AdvanceTarget();
        AddReward( waypointBonus );

        if ( completed ) {
          AddReward( pathCompleteBonus );
          if ( debugEpisodeEvents )
            Debug.Log( $"[ExcavatorTrajectoryAgent] Path complete. episode={CurrentEpisode}, score={GetCumulativeReward():F3}" );

          if ( successIndicator != null )
            successIndicator.Show();

          if ( endEpisodeOnTargetReached )
            EndEpisode();
        }
        else {
          m_previousDistance = CurrentDistanceToTargetInternal();
          if ( debugEpisodeEvents )
            Debug.Log( $"[ExcavatorTrajectoryAgent] Waypoint reached. next={trajectory.CurrentIndex}, distance={m_previousDistance:F2}m" );
        }
      }
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

      if ( debugEpisodeEvents )
        Debug.Log( $"[ExcavatorTrajectoryAgent] Episode {CurrentEpisode} begin. distance={m_previousDistance:F2}m" );
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

    // Scene 查找工具。优先在 Inspector 显式拖引用，自动查找只作为兜底。
    private T FindInScene<T>() where T : Component
    {
      var roots = gameObject.scene.GetRootGameObjects();
      foreach ( var root in roots ) {
        var component = root.GetComponentInChildren<T>( true );
        if ( component != null )
          return component;
      }
      return null;
    }

    private bool IsBehaviorName( string behaviorName )
    {
      var behavior = GetComponent<BehaviorParameters>();
      return behavior != null && behavior.BehaviorName == behaviorName;
    }

    private GameObject FindInSceneByName( string objectName )
    {
      var roots = gameObject.scene.GetRootGameObjects();
      foreach ( var root in roots ) {
        var match = FindChildByName( root.transform, objectName );
        if ( match != null )
          return match.gameObject;
      }
      return null;
    }

    /// <summary>
    /// 自动查找成功提示器。轨迹环境最好显式绑定，避免多环境时拿到错误对象。
    /// </summary>
    private SuccessIndicator FindOrCreateSuccessIndicator()
    {
      var existing = FindInScene<SuccessIndicator>();
      if ( existing != null )
        return existing;

      var frame = FindInSceneByName( "Frame" );
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
