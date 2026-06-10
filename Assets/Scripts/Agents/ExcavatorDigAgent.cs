using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using AGXUnity.Model;

namespace RLExcavator
{
  /// <summary>
  /// 挖掘机挖土强化学习智能体（连续控制）。
  ///
  /// 这是整套流程的"大脑接口"，对照你做过的 TurtleAgent 来理解差异：
  ///   - 动作：Turtle 是离散(前进/转向)，这里是 4 维连续(boom/stick/bucket/swing 目标速度)。
  ///   - 奖励：Turtle 是到达目标，这里是"铲斗里挖到的土质量"（密集塑形信号）。
  ///   - 环境：Turtle 是静态地面，这里是 AGX 可变形地形 + 铲斗(shovel)。
  ///
  /// 数据流：
  ///   CollectObservations() 把世界状态喂给网络
  ///        -> 网络输出 4 个连续动作
  ///        -> OnActionReceived() 把动作交给 ExcavatorController 驱动关节
  ///        -> AddReward() 根据铲斗装载量变化给奖励
  ///        -> 达标 -> 地板/平台变绿提示（当前验证阶段暂不重置环境）
  ///
  /// 重要：ML-Agents 的决策步会与物理固定步对齐，所以动作施加与奖励统计都放在
  /// OnActionReceived（每个决策步一次），不要用 Update 的 deltaTime 去积分物理量。
  /// </summary>
  public class ExcavatorDigAgent : Agent
  {
    [Header("引用（Inspector 拖入）")]
    [Tooltip("我们自己写的关节控制器")]
    public ExcavatorController controller;
    [Tooltip("AGX 可变形地形")]
    public DeformableTerrain terrain;
    [Tooltip("铲斗的 DeformableTerrainShovel 组件")]
    public DeformableTerrainShovel shovel;
    [Tooltip("铲斗尖参考点（用于观测铲斗位置 / 埋深），可用铲斗上的一个空物体")]
    public Transform bucketTip;
    [Tooltip("Episode reset 入口。留空则运行时自动查找 ResetTerrain 组件")]
    public global::ResetTerrain episodeResetter;

    [Header("成功提示")]
    [Tooltip("成功时触发的共享视觉提示。留空则运行时尝试在 Frame 上自动创建。")]
    public SuccessIndicator successIndicator;

    [Header("任务参数")]
    [Tooltip("判定成功的铲斗土质量(kg)。先用手动测试估个值再填")]
    public float targetMassKg = 200f;
    [Tooltip("达标后还需把铲斗尖相对本局起点抬高多少米才算成功")]
    public float liftHeight = 2.0f;
    [Tooltip("用于归一化质量观测的参考值(kg)，一般取 targetMass 的 1~2 倍")]
    public float massNormalizer = 400f;

    [Header("奖励权重（先跑通再调）")]
    public float wMassGain   = 3.0f;   // 每步装载量增量的奖励（核心信号）
    public float wLift       = 0.5f;   // 达标后抬升奖励
    public float wActionCost = 0.001f; // 抖动惩罚
    [Tooltip("斗内已有质量的持续小奖励，帮助 agent 知道保土是好事")]
    public float wMassHeld = 0.001f;
    [Tooltip("未达标前铲斗尖向下进入土层的塑形奖励")]
    public float wDigDepth = 0.0008f;
    [Tooltip("未达标前保持在初始挖掘区域附近的塑形奖励")]
    public float wStayNearDigArea = 0.0003f;
    [Tooltip("未达标前过早抬升铲斗尖的惩罚")]
    public float wEarlyLiftPenalty = 0.0008f;
    [Tooltip("用于归一化下探奖励的参考深度(m)")]
    public float digDepthNormalizer = 0.8f;
    [Tooltip("挖掘区域水平半径(m)，越小越强调在初始土堆附近挖")]
    public float digAreaRadius = 3.0f;

    [Header("Episode 复位")]
    [Tooltip("每局开始时用 ExcavatorController 的 LockController 复位挖掘机姿态")]
    public bool resetPoseOnEpisodeBegin = true;
    [Tooltip("姿态复位保持 LockController 的物理步数，负数表示使用 controller.homeSettleSteps")]
    public int poseResetSettleSteps = -1;
    [Tooltip("成功提示结束后自动执行和 R 键一致的 episode reset")]
    public bool resetEpisodeOnSuccess = true;

    [Header("调试")]
    [Tooltip("打印达标/成功日志，用于 Heuristic 验证阶段")]
    public bool debugEpisodeEvents = true;
    [Tooltip("Agent 启用时自动禁用键盘测试脚本，避免键盘 0 输入覆盖 trainer 动作")]
    public bool disableKeyboardTesterWhenAgentEnabled = true;
    [Tooltip("Behavior Name 为 ExcavatorDig 时自动禁用轨迹跟踪 Agent，避免两个 Agent 同时接管控制")]
    public bool disableTrajectoryAgentWhenEnabled = true;
    [Tooltip("打印前若干次 trainer 动作，用于确认 agent 是否收到非零动作")]
    public bool debugActions = false;
    public int debugActionPrints = 20;

    // ---- 运行时状态 ----
    private float m_prevMass = 0f;      // 上一步铲斗内土质量，用于算增量
    private bool  m_reachedTarget = false;
    private bool  m_successSignaled = false;
    private float m_episodeStartTipY = 0f;
    private Vector3 m_episodeStartTipPosition = Vector3.zero;
    private bool m_waitingForPoseReset = false;
    private int m_debugActionPrintCount = 0;
    [HideInInspector] public int CurrentEpisode = 0;
    public float CurrentScore => GetCumulativeReward();
    public float CurrentMassInBucket => MassInBucket();

    // 取铲斗内当前土质量(kg)。来自 AGX：terrain.getDynamicMass(shovel) 返回被铲斗"托住"的动态土质量。
    private float MassInBucket()
    {
      if ( terrain == null || shovel == null || terrain.Native == null || shovel.Native == null )
        return 0f;
      return Mathf.Max( 0f, (float)terrain.Native.getDynamicMass( shovel.Native ) );
    }

    public override void Initialize()
    {
      CurrentEpisode = 0;

      if ( successIndicator == null )
        successIndicator = FindOrCreateSuccessIndicator();

      if ( episodeResetter == null )
        episodeResetter = FindInScene<global::ResetTerrain>();

      if ( disableKeyboardTesterWhenAgentEnabled ) {
        var keyboardTester = GetComponent<ExcavatorKeyboardTester>();
        if ( keyboardTester != null )
          keyboardTester.enabled = false;
      }

      if ( disableTrajectoryAgentWhenEnabled && IsBehaviorName( "ExcavatorDig" ) ) {
        var trajectoryAgent = GetComponent<ExcavatorTrajectoryAgent>();
        if ( trajectoryAgent != null )
          trajectoryAgent.enabled = false;
      }
    }

    private bool IsBehaviorName( string behaviorName )
    {
      var behavior = GetComponent<BehaviorParameters>();
      return behavior != null && behavior.BehaviorName == behaviorName;
    }

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

    public override void OnEpisodeBegin()
    {
      CurrentEpisode++;
      m_reachedTarget = false;
      m_successSignaled = false;
      m_waitingForPoseReset = true;

      if ( episodeResetter != null ) {
        episodeResetter.ResetEpisode( false );
        return;
      }

      if ( resetPoseOnEpisodeBegin && controller != null ) {
        controller.BeginHomePoseReset( poseResetSettleSteps );
        return;
      }

      BeginEpisodeMeasurements();
    }

    private void BeginEpisodeMeasurements()
    {
      m_waitingForPoseReset = false;
      m_episodeStartTipPosition = bucketTip != null ? bucketTip.position : Vector3.zero;
      m_episodeStartTipY = m_episodeStartTipPosition.y;
      m_prevMass = MassInBucket();

      if ( debugEpisodeEvents )
        Debug.Log( $"[ExcavatorDigAgent] Episode {CurrentEpisode} begin. tipY={m_episodeStartTipY:F2}, mass={m_prevMass:F1}kg" );
    }

    public void RestartEpisodeMeasurements()
    {
      CurrentEpisode++;
      m_reachedTarget = false;
      m_successSignaled = false;
      BeginEpisodeMeasurements();
    }

    // 观测：全部尽量归一化到 [-1,1]
    public override void CollectObservations( VectorSensor sensor )
    {
      // 4× 关节位置（已在 controller 内归一化）
      sensor.AddObservation( controller != null ? controller.BoomNormalizedPosition   : 0f );
      sensor.AddObservation( controller != null ? controller.StickNormalizedPosition  : 0f );
      sensor.AddObservation( controller != null ? controller.BucketNormalizedPosition : 0f );
      sensor.AddObservation( controller != null ? controller.SwingNormalizedPosition  : 0f );

      // 1× 铲斗内土质量（归一化）
      float mass = MassInBucket();
      sensor.AddObservation( Mathf.Clamp01( mass / Mathf.Max( massNormalizer, 1f ) ) );

      // 3× 铲斗尖相对地形的位置（粗略归一化）。这里用世界坐标的简化表示；
      //    更严谨可换成相对"目标挖掘点"的向量，第一版先用绝对高度+水平位置。
      if ( bucketTip != null ) {
        Vector3 p = bucketTip.position;
        sensor.AddObservation( p.x / 10f );
        sensor.AddObservation( p.y / 10f );   // 高度，用于学习"抬起"
        sensor.AddObservation( p.z / 10f );
      }
      else {
        sensor.AddObservation( 0f );
        sensor.AddObservation( 0f );
        sensor.AddObservation( 0f );
      }

      // 1× episode 进度
      sensor.AddObservation( MaxStep > 0 ? (float)StepCount / MaxStep : 0f );
      // 合计 9 维（先精简，确认有效后再加速度等）
    }

    public override void OnActionReceived( ActionBuffers actions )
    {
      if ( episodeResetter != null && episodeResetter.IsResetting )
        return;

      if ( controller != null && controller.IsHoming )
        return;

      if ( m_waitingForPoseReset )
        BeginEpisodeMeasurements();

      var a = actions.ContinuousActions;
      float boom   = a.Length > 0 ? a[ 0 ] : 0f;
      float stick  = a.Length > 1 ? a[ 1 ] : 0f;
      float bucket = a.Length > 2 ? a[ 2 ] : 0f;
      float swing  = a.Length > 3 ? a[ 3 ] : 0f;

      if ( debugActions && m_debugActionPrintCount < debugActionPrints ) {
        Debug.Log( $"[ExcavatorDigAgent] action boom={boom:F3}, stick={stick:F3}, bucket={bucket:F3}, swing={swing:F3}" );
        m_debugActionPrintCount++;
      }

      // 1) 动作 -> 关节
      if ( controller != null )
        controller.SetNormalizedSpeeds( boom, stick, bucket, swing );

      // 2) 奖励：装载量增量（核心）
      float mass = MassInBucket();
      float dMass = mass - m_prevMass;
      m_prevMass = mass;
      AddReward( wMassGain * dMass / Mathf.Max( massNormalizer, 1f ) );

      float mass01 = Mathf.Clamp01( mass / Mathf.Max( massNormalizer, 1f ) );
      AddReward( wMassHeld * mass01 );

      // 3) 时间惩罚：鼓励尽快完成（沿用 Turtle 思路）
      AddReward( -1f / Mathf.Max( MaxStep, 1 ) );

      // 4) 抖动惩罚：避免乱抖
      AddReward( -wActionCost * ( Mathf.Abs( boom ) + Mathf.Abs( stick ) + Mathf.Abs( bucket ) + Mathf.Abs( swing ) ) );

      if ( !m_reachedTarget && bucketTip != null ) {
        Vector3 tip = bucketTip.position;
        float lowerDepth = Mathf.Max( 0f, m_episodeStartTipY - tip.y );
        float depth01 = Mathf.Clamp01( lowerDepth / Mathf.Max( digDepthNormalizer, 0.01f ) );
        AddReward( wDigDepth * depth01 );

        Vector2 startXZ = new Vector2( m_episodeStartTipPosition.x, m_episodeStartTipPosition.z );
        Vector2 tipXZ = new Vector2( tip.x, tip.z );
        float distance01 = Mathf.Clamp01( Vector2.Distance( startXZ, tipXZ ) / Mathf.Max( digAreaRadius, 0.01f ) );
        AddReward( wStayNearDigArea * ( 1f - distance01 ) );

        float earlyLift = Mathf.Max( 0f, tip.y - m_episodeStartTipY );
        AddReward( -wEarlyLiftPenalty * Mathf.Clamp01( earlyLift / Mathf.Max( liftHeight, 0.01f ) ) );
      }

      // 5) 是否装够了土
      if ( !m_reachedTarget && mass >= targetMassKg ) {
        m_reachedTarget = true;
        if ( debugEpisodeEvents )
          Debug.Log( $"[ExcavatorDigAgent] Target mass reached: {mass:F1}/{targetMassKg:F1}kg, tipLift={( bucketTip != null ? bucketTip.position.y - m_episodeStartTipY : 0f ):F2}m" );
      }

      // 6) 达标后：抬升奖励 + 成功判定
      if ( m_reachedTarget && bucketTip != null ) {
        float tipLift = bucketTip.position.y - m_episodeStartTipY;
        bool hasTargetMass = mass >= targetMassKg;

        if ( hasTargetMass )
          AddReward( wLift * Mathf.Max( 0f, tipLift ) / Mathf.Max( liftHeight, 0.01f ) / Mathf.Max( MaxStep, 1 ) );

        if ( !m_successSignaled && hasTargetMass && tipLift >= liftHeight ) {
          m_successSignaled = true;
          if ( debugEpisodeEvents )
            Debug.Log( $"[ExcavatorDigAgent] Success: mass={mass:F1}kg, tipLift={tipLift:F2}/{liftHeight:F2}m." );
          AddReward( 1.0f );        // 成功大奖励
          ShowSuccessIndicator();
          if ( resetEpisodeOnSuccess )
            EndEpisode();
        }
      }
    }

    private void ShowSuccessIndicator()
    {
      if ( successIndicator != null )
        successIndicator.Show();
    }

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

    // 手动控制：与键盘测试一致，先用它确认动作/观测/奖励闭环正确再训练。
    public override void Heuristic( in ActionBuffers actionsOut )
    {
      ExcavatorKeyboardInput.WriteDefaultTo( actionsOut.ContinuousActions );
    }
  }
}
