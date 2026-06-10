using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;

namespace RLExcavator
{
  /// <summary>
  /// 挖掘机铲斗轨迹跟踪智能体
  ///
  /// 【功能说明】
  /// 让挖掘机的铲斗尖（bucketTip）跟踪一系列路径点（waypoints），实现轨迹跟踪任务。
  /// 与 ExcavatorDigAgent（挖土任务）分开设计，这样轨迹跟踪的奖励可以独立调优，不影响挖土任务。
  ///
  /// 【数据流】
  ///   CollectObservations() 收集铲斗尖位置、目标位置、关节状态等观测
  ///        -> 网络输出 4 个连续动作（boom/stick/bucket/swing 目标速度）
  ///        -> OnActionReceived() 把动作交给 ExcavatorController 驱动关节
  ///        -> AddReward() 根据距离变化和到达目标给奖励
  ///        -> 到达路径终点 -> EndEpisode()
  ///
  /// 【两种运行模式】
  ///   1. controlOnlyDebugMode = true：纯调试模式，只测试控制链路
  ///      - 不计算奖励，不结束 episode
  ///      - Heuristic 模式下仍通过 ML-Agents 的动作链路控制
  ///      - 用于验证"按键->关节运动"这条链路是否通
  ///
  ///   2. controlOnlyDebugMode = false：完整 RL 模式
  ///      - 计算距离奖励、进度奖励、动作惩罚
  ///      - 到达目标后给奖励并结束 episode
  ///      - 用于训练或 Heuristic 验证完整环境
  ///
  /// 【关键设计】
  ///   - Heuristic 和策略推理共用 OnActionReceived 动作入口
  ///   - 自动查找场景中的组件引用（autoFindReferences）
  ///   - 与 ExcavatorDigAgent 互斥（disableDigAgentWhenEnabled）
  /// </summary>
  public class ExcavatorTrajectoryAgent : Agent
  {
    // ==================== 引用（Inspector 拖入或自动查找） ====================

    [Header("References")]
    [Tooltip("挖掘机关节控制器，负责把归一化动作转换为 AGX 关节目标速度")]
    public ExcavatorController controller;

    [Tooltip("铲斗尖的 Transform，用于计算到目标的距离")]
    public Transform bucketTip;

    [Tooltip("轨迹路径点提供器。第一版可以只放一个 TargetPoint；多点路径阶段再用 TrajectoryProvider")]
    public TrajectoryProvider trajectory;

    [Tooltip("可选：地形重置器。轨迹跟踪默认不需要重置地形，除非需要同步重置")]
    public global::ResetTerrain episodeResetter;

    [Tooltip("成功到达目标点时触发的共享视觉提示（地板变绿等）")]
    public SuccessIndicator successIndicator;

    // ==================== 任务参数 ====================

    [Header("Task")]
    [Tooltip("纯调试模式：只测试控制链路，不计算奖励，不结束 episode")]
    public bool controlOnlyDebugMode = false;

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

    // ==================== 奖励权重 ====================

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

    // ==================== Episode 复位参数 ====================

    [Header("Episode reset")]
    [Tooltip("是否在 episode 开始时重置挖掘机姿态到初始位置")]
    public bool resetPoseOnEpisodeBegin = true;

    [Tooltip("是否在 episode 开始时重置地形。轨迹跟踪默认关闭，除非需要同步重置")]
    public bool useEpisodeResetterOnEpisodeBegin = false;

    [Tooltip("姿态复位时保持 LockController 的物理步数，-1 表示使用 controller 默认值")]
    public int poseResetSettleSteps = -1;

    // ==================== 调试参数 ====================

    [Header("Debug")]
    [Tooltip("打印 episode 开始/结束等事件日志")]
    public bool debugEpisodeEvents = true;

    [Tooltip("Agent 启用时自动禁用键盘测试脚本，避免两者同时写关节")]
    public bool disableKeyboardTesterWhenAgentEnabled = true;

    [Tooltip("Agent 启用时自动禁用挖土 Agent，避免两个 Agent 同时接管控制")]
    public bool disableDigAgentWhenEnabled = true;

    [Tooltip("引用为空时在当前 scene 内自动查找，减少 Inspector 手动绑定错误")]
    public bool autoFindReferences = true;

    [Tooltip("打印前若干次动作，用于确认 Heuristic/trainer 是否真的给了动作")]
    public bool debugActions = false;

    [Tooltip("最多打印的动作次数")]
    public int debugActionPrints = 20;

    // ==================== 运行时状态（私有） ====================

    /// <summary>上一步铲斗尖到目标的距离，用于计算进度奖励</summary>
    private float m_previousDistance = 0f;

    /// <summary>是否正在等待 episode 复位完成</summary>
    private bool m_waitingForPoseReset = false;

    /// <summary>是否已经打印过缺失引用的警告（避免重复打印）</summary>
    private bool m_missingReferenceWarned = false;

    /// <summary>已打印的动作调试次数计数</summary>
    private int m_debugActionPrintCount = 0;

    // ==================== 公开属性（供 UI 或其他脚本读取） ====================

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

    // ==================== 生命周期方法 ====================

    /// <summary>
    /// 初始化：自动查找场景中的组件引用，禁用冲突的脚本
    /// 只在 Agent 第一次启用时调用一次
    /// </summary>
    public override void Initialize()
    {
      CurrentEpisode = 0;

      // 自动查找 TrajectoryProvider
      if ( autoFindReferences && trajectory == null )
        trajectory = FindInScene<TrajectoryProvider>();

      // 自动查找名为 "BucketTip" 的物体作为铲斗尖
      if ( autoFindReferences && bucketTip == null ) {
        var bucketTipObject = FindInSceneByName( "BucketTip" );
        if ( bucketTipObject != null )
          bucketTip = bucketTipObject.transform;
      }

      // 自动查找地形重置器
      if ( autoFindReferences && episodeResetter == null )
        episodeResetter = FindInScene<global::ResetTerrain>();

      // 如果重置器组件未激活，清空引用（避免后续判断出错）
      if ( episodeResetter != null && !IsComponentActive( episodeResetter ) )
        episodeResetter = null;

      // 自动查找或创建成功提示器
      if ( autoFindReferences && successIndicator == null )
        successIndicator = FindOrCreateSuccessIndicator();

      // 禁用键盘测试脚本，避免与 Agent 同时写关节
      if ( disableKeyboardTesterWhenAgentEnabled ) {
        var keyboardTester = GetComponent<ExcavatorKeyboardTester>();
        if ( keyboardTester != null )
          keyboardTester.enabled = false;
      }

      // 禁用挖土 Agent，避免两个 Agent 同时接管控制
      if ( disableDigAgentWhenEnabled && IsBehaviorName( "ExcavatorTrajectory" ) ) {
        var digAgent = GetComponent<ExcavatorDigAgent>();
        if ( digAgent != null )
          digAgent.enabled = false;
      }
    }

    /// <summary>
    /// Episode 开始时调用（每次训练回合开始）
    ///
    /// 【逻辑】
    ///   1. 重置路径点索引
    ///   2. 按配置重置地形或挖掘机姿态
    ///   3. 复位完成后开始记录距离测量
    /// </summary>
    public override void OnEpisodeBegin()
    {
      CurrentEpisode++;
      m_missingReferenceWarned = false;
      m_debugActionPrintCount = 0;
      m_waitingForPoseReset = true;

      // 重置路径点索引到起点
      if ( trajectory != null )
        trajectory.ResetPath();

      if ( debugEpisodeEvents )
        Debug.Log( $"[ExcavatorTrajectoryAgent] {( controlOnlyDebugMode ? "Control-only" : "Episode" )} {CurrentEpisode} begin." );

      if ( useEpisodeResetterOnEpisodeBegin && episodeResetter != null ) {
        episodeResetter.ResetEpisode( false );
        return;
      }

      // 姿态重置
      if ( resetPoseOnEpisodeBegin && controller != null) {
        controller.BeginHomePoseReset( poseResetSettleSteps );
        return;
      }

      // 开始测量（记录初始距离等）
      BeginEpisodeMeasurements();
    }

    // ==================== ML-Agents 核心方法 ====================

    /// <summary>
    /// 收集观测数据（每步调用一次，喂给神经网络）
    ///
    /// 【观测空间】共 15 维：
    ///   - 3× 铲斗尖局部位置（归一化）
    ///   - 3× 目标点局部位置（归一化）
    ///   - 3× 误差向量（目标 - 铲斗尖，归一化）
    ///   - 4× 关节位置（boom/stick/bucket/swing，归一化到 [-1,1]）
    ///   - 1× 路径进度（0~1）
    ///   - 1× episode 进度（step/maxStep）
    ///
    /// 【为什么用局部坐标】
    ///   局部坐标是相对于挖掘机本体的，这样无论挖掘机在场景哪个位置，
    ///   观测值的范围都是固定的，网络更容易学习。
    /// </summary>
    public override void CollectObservations( VectorSensor sensor )
    {
      Vector3 tipLocal = Vector3.zero;
      Vector3 targetLocal = Vector3.zero;
      Vector3 errorLocal = Vector3.zero;

      // 铲斗尖的局部坐标（相对于挖掘机本体）
      if ( bucketTip != null )
        tipLocal = transform.InverseTransformPoint( bucketTip.position );

      // 目标点的局部坐标
      if ( trajectory != null )
        targetLocal = transform.InverseTransformPoint( trajectory.CurrentTargetPosition );

      // 误差向量：目标位置 - 铲斗尖位置（铲斗需要移动的方向和距离）
      errorLocal = targetLocal - tipLocal;

      // 添加位置观测（归一化到合理范围）
      AddVectorObservation( sensor, tipLocal / Mathf.Max( positionNormalizer, 0.01f ) );
      AddVectorObservation( sensor, targetLocal / Mathf.Max( positionNormalizer, 0.01f ) );
      AddVectorObservation( sensor, errorLocal / Mathf.Max( distanceNormalizer, 0.01f ) );

      // 关节位置（已在 controller 内归一化到 [-1,1]）
      sensor.AddObservation( controller != null ? controller.BoomNormalizedPosition   : 0f );
      sensor.AddObservation( controller != null ? controller.StickNormalizedPosition  : 0f );
      sensor.AddObservation( controller != null ? controller.BucketNormalizedPosition : 0f );
      sensor.AddObservation( controller != null ? controller.SwingNormalizedPosition  : 0f );

      // 路径进度（0~1，用于让 agent 知道自己完成了多少路径）
      sensor.AddObservation( trajectory != null ? trajectory.Progress : 0f );

      // episode 进度（当前步数 / 最大步数，用于让 agent 有时间紧迫感）
      sensor.AddObservation( MaxStep > 0 ? (float)StepCount / MaxStep : 0f );
      // 总共 15 维观测
    }

    /// <summary>
    /// 接收动作并执行（每步调用一次）
    ///
    /// 【动作空间】4 维连续动作：
    ///   - a[0]：boom（大臂）目标速度 [-1,1]
    ///   - a[1]：stick（小臂）目标速度 [-1,1]
    ///   - a[2]：bucket（铲斗）目标速度 [-1,1]
    ///   - a[3]：swing（回转）目标速度 [-1,1]
    ///
    /// 【奖励计算】
    ///   - 距离惩罚：距离目标越远，惩罚越大
    ///   - 进度奖励：每步距离减少量（鼓励持续靠近）
    ///   - 动作惩罚：避免关节乱抖
    ///   - 时间惩罚：鼓励尽快完成
    ///   - 路径点奖励：到达路径点时给奖励
    ///   - 完成奖励：到达路径终点时给大奖励
    /// </summary>
    public override void OnActionReceived( ActionBuffers actions )
    {
      if ( episodeResetter != null && episodeResetter.IsResetting )
        return;

      if ( controller != null && controller.IsHoming )
        return;

      if ( m_waitingForPoseReset )
        BeginEpisodeMeasurements();

      // 从动作缓冲区读取 4 个连续动作
      var a = actions.ContinuousActions;
      float boom = a.Length > 0 ? a[ 0 ] : 0f;    // 大臂速度
      float stick = a.Length > 1 ? a[ 1 ] : 0f;   // 小臂速度
      float bucket = a.Length > 2 ? a[ 2 ] : 0f;  // 铲斗速度
      float swing = a.Length > 3 ? a[ 3 ] : 0f;   // 回转速度

      // 调试日志：打印接收到的动作
      if ( debugActions && m_debugActionPrintCount < debugActionPrints ) {
        Debug.Log( $"[ExcavatorTrajectoryAgent] action boom={boom:F3}, stick={stick:F3}, bucket={bucket:F3}, swing={swing:F3}" );
        m_debugActionPrintCount++;
      }

      // 把动作交给控制器，驱动关节运动
      if ( controller != null )
        controller.SetNormalizedSpeeds( boom, stick, bucket, swing );

      // controlOnlyDebugMode：只测试控制链路，不计算奖励
      if ( controlOnlyDebugMode )
        return;

      // 检查必要引用是否存在
      if ( bucketTip == null || trajectory == null ) {
        if ( debugEpisodeEvents && !m_missingReferenceWarned ) {
          Debug.LogWarning( "[ExcavatorTrajectoryAgent] Missing bucketTip or trajectory reference. Ending episode." );
          m_missingReferenceWarned = true;
        }
        AddReward( -1f );  // 缺失引用，给惩罚
        EndEpisode();
        return;
      }

      // ============ 奖励计算 ============

      // 计算当前距离和进度
      float distance = CurrentDistanceToTargetInternal();
      float progressDelta = m_previousDistance - distance;  // 正值 = 靠近目标
      m_previousDistance = distance;

      // 1. 距离惩罚：距离目标越远，惩罚越大（鼓励靠近）
      AddReward( -wDistance * distance / Mathf.Max( distanceNormalizer, 0.01f ) );

      // 2. 进度奖励：每步距离减少量（鼓励持续靠近目标）
      AddReward( wProgress * progressDelta / Mathf.Max( distanceNormalizer, 0.01f ) );

      // 3. 动作惩罚：避免关节乱抖（动作幅度越大，惩罚越大）
      AddReward( -wActionCost * ( Mathf.Abs( boom ) + Mathf.Abs( stick ) + Mathf.Abs( bucket ) + Mathf.Abs( swing ) ) );

      // 4. 时间惩罚：每步都给小惩罚，鼓励尽快完成
      AddReward( -1f / Mathf.Max( MaxStep, 1 ) );

      // ============ 目标到达判断 ============

      // 如果距离小于阈值，认为到达当前路径点
      if ( distance <= targetThreshold ) {
        // 尝试前进到下一个路径点
        bool completed = trajectory == null || trajectory.AdvanceTarget();

        // 5. 到达路径点奖励
        AddReward( waypointBonus );

        if ( completed ) {
          // 到达路径终点：给大奖励，结束 episode
          AddReward( pathCompleteBonus );
          if ( debugEpisodeEvents )
            Debug.Log( $"[ExcavatorTrajectoryAgent] Path complete. episode={CurrentEpisode}, score={GetCumulativeReward():F3}" );

          // 显示成功提示（地板变绿等）
          if ( successIndicator != null )
            successIndicator.Show();

          // 结束 episode
          if ( endEpisodeOnTargetReached )
            EndEpisode();
        }
        else {
          // 到达中间路径点：更新距离，继续跟踪下一个点
          m_previousDistance = CurrentDistanceToTargetInternal();
          if ( debugEpisodeEvents )
            Debug.Log( $"[ExcavatorTrajectoryAgent] Waypoint reached. next={trajectory.CurrentIndex}, distance={m_previousDistance:F2}m" );
        }
      }
    }

    /// <summary>
    /// Heuristic 模式：用键盘输入代替神经网络输出
    ///
    /// 【作用】
    ///   当 Behavior Parameters 设置为 "Heuristic Only" 时，
    ///   ML-Agents 不调用神经网络，而是调用这个方法获取动作。
    ///   这样可以用键盘手动控制挖掘机，验证环境是否正确。
    ///
    /// 【键位】
    ///   - boom（大臂）：I（正向）/ K（反向）
    ///   - stick（小臂）：G（正向）/ T（反向）
    ///   - bucket（铲斗）：J（正向）/ L（反向）
    ///   - swing（回转）：H（正向）/ F（反向）
    /// </summary>
    public override void Heuristic( in ActionBuffers actionsOut )
    {
      // 把键盘输入写入动作缓冲区
      ExcavatorKeyboardInput.WriteDefaultTo( actionsOut.ContinuousActions );

      // 调试日志：打印键盘输入
      if ( debugActions && m_debugActionPrintCount < debugActionPrints ) {
        var command = ExcavatorKeyboardInput.ReadDefault();
        if ( command.HasInput ) {
          Debug.Log( $"[ExcavatorTrajectoryAgent] heuristic boom={command.boom:F3}, stick={command.stick:F3}, bucket={command.bucket:F3}, swing={command.swing:F3}" );
          m_debugActionPrintCount++;
        }
      }
    }

    // ==================== 私有辅助方法 ====================

    /// <summary>
    /// 开始 episode 测量（记录初始距离等）
    ///
    /// 【调用时机】
    ///   - 重置完成后调用
    ///   - 记录铲斗尖到目标的初始距离，用于后续计算进度奖励
    /// </summary>
    private void BeginEpisodeMeasurements()
    {
      m_waitingForPoseReset = false;
      m_previousDistance = CurrentDistanceToTargetInternal();

      if ( debugEpisodeEvents )
        Debug.Log( $"[ExcavatorTrajectoryAgent] Episode {CurrentEpisode} begin. distance={m_previousDistance:F2}m" );
    }

    /// <summary>
    /// 计算铲斗尖到当前目标的距离（内部使用）
    /// </summary>
    /// <returns>距离（米），如果引用缺失返回 0</returns>
    private float CurrentDistanceToTargetInternal()
    {
      if ( bucketTip == null || trajectory == null )
        return 0f;

      return Vector3.Distance( bucketTip.position, trajectory.CurrentTargetPosition );
    }

    /// <summary>
    /// 是否有目标点（targetPoint 或 trajectory 至少有一个）
    /// </summary>
    private bool HasTarget()
    {
      return trajectory != null;
    }

    /// <summary>
    /// 计算到达目标的阈值（含可视化小球半径）
    ///
    /// 【作用】
    ///   如果目标点有 TargetPointVisual 组件（可视化小球），
    ///   把小球半径也加入阈值，让视觉接触更接近成功条件。
    /// </summary>
    private float CurrentReachThreshold()
    {
      float threshold = Mathf.Max( targetThreshold, 0f );

      // 如果目标点有可视化小球，把半径加入阈值
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

    // ==================== 工具方法 ====================

    /// <summary>
    /// 在场景中查找指定类型的组件（包括未激活的物体）
    /// </summary>
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

    /// <summary>
    /// 检查 BehaviorParameters 的 BehaviorName 是否匹配
    /// </summary>
    private bool IsBehaviorName( string behaviorName )
    {
      var behavior = GetComponent<BehaviorParameters>();
      return behavior != null && behavior.BehaviorName == behaviorName;
    }

    /// <summary>
    /// 检查组件是否激活（gameObject 是否在层级中激活）
    /// </summary>
    private static bool IsComponentActive( Component component )
    {
      return component != null && component.gameObject.activeInHierarchy;
    }

    /// <summary>
    /// 在场景中按名字查找物体（递归查找子物体）
    /// </summary>
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
    /// 查找或创建 SuccessIndicator（成功提示器）
    ///
    /// 【逻辑】
    ///   1. 先在场景中查找已有的 SuccessIndicator
    ///   2. 如果没有，找到名为 "Frame" 的物体，在其上创建
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

    /// <summary>
    /// 递归查找子物体（按名字）
    /// </summary>
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

    /// <summary>
    /// 把 Vector3 拆成 3 个 float 添加到观测中
    /// </summary>
    private static void AddVectorObservation( VectorSensor sensor, Vector3 value )
    {
      sensor.AddObservation( value.x );
      sensor.AddObservation( value.y );
      sensor.AddObservation( value.z );
    }
  }
}
