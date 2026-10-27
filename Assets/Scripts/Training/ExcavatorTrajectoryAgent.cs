using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine.Serialization;

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
    public enum TrajectoryTaskMode
    {
      MoveToDig = 0,
      CarryMaterial = 1
    }

    private const int kLockedObservationSize = 38;
    private const int kLockedMaxEpisodeSteps = 1500;
    private const int kDefaultOutOfBoundsFailureSteps = 30;
    private const int kDefaultStuckWindowSteps = 200;
    private const float kDefaultStuckMinTipMotion = 0.01f;
    private const float kDefaultStuckMinActionMagnitude = 0.1f;
    private const float kFailurePenalty = 5.0f;
    private const float kProgressRewardWeight = 5.0f;
    private const float kTrackingRewardWeight = 0.3f;
    private const float kTubeRewardWeight = 0.5f;
    private const float kPitchRewardWeight = 0.2f;
    private const float kWaypointReward = 1.0f;
    private const float kCompleteReward = 10.0f;
    private const float kSmoothRewardWeight = 0.05f;
    private const float kEnergyRewardWeight = 0.01f;
    private const float kTimePenalty = 0.001f;
    private const float kDeadzoneNonzeroActionPenaltyPerAxis = 0.003f;

    // Inspector 引用

    [Header("Deadzone Reward Shaping (Plan A)")]
    [Tooltip("A1 bump 惩罚权重（每轴峰值罚）。当前部署导向训练使用 0.03。")]
    [Min(0f)]
    [SerializeField] private float deadzoneBumpWeight = 0.03f;
    [Tooltip("A1 逃逸余量 m：bump 视 |a|>=d*(1+m) 为已清出死区，使控制器边界 |a|=d 仍被惩罚（消除边界缝隙）并留标定余量。默认 0.1。")]
    [Min(0f)]
    [SerializeField] private float deadzoneEscapeMargin = 0.1f;
    [Tooltip("A2 能耗是否按方向死区置零后的等效动作计。训练场景开启，通用 prefab 默认关闭。")]
    [SerializeField] private bool deadzoneEnergyUseEffective = false;

    [Header("Training Domain Randomization (A4)")]
    [Tooltip("仅训练场景启用：每个 episode 独立扰动四轴方向性命令死区。推理场景必须关闭。")]
    [SerializeField] private bool randomizeCommandDeadzonesOnEpisodeBegin = false;
    [Tooltip("死区相对 machine profile 标定值的最小倍率。")]
    [Min(0f)]
    [SerializeField] private float commandDeadzoneScaleMin = 0.8f;
    [Tooltip("死区相对 machine profile 标定值的最大倍率。")]
    [Min(0f)]
    [SerializeField] private float commandDeadzoneScaleMax = 1.2f;

    [Header("Carry terminal swing stabilization")]
    [Tooltip("只对 CarryMaterial 最终 waypoint 启用回转制动奖励与稳定到达门。默认关闭，候选训练配置显式开启。")]
    [SerializeField] private bool terminalSwingStabilizationEnabled = false;
    [Tooltip("最终 waypoint 的真实位置阈值；启用后不叠加仅供显示的目标球半径。")]
    [Min(0f)]
    [SerializeField] private float terminalSwingReachThresholdM = 0.25f;
    [Tooltip("进入该距离后开始惩罚未制动的回转速度与同向回转命令。")]
    [Min(0f)]
    [SerializeField] private float terminalSwingApproachDistanceM = 0.50f;
    [Tooltip("终点附近归一化回转速度平方惩罚权重。")]
    [Min(0f)]
    [SerializeField] private float terminalSwingVelocityWeight = 0.20f;
    [Tooltip("已有回转速度方向上继续加速的额外惩罚权重。")]
    [Min(0f)]
    [SerializeField] private float terminalSwingDriveWeight = 0.10f;
    [Tooltip("最终到达允许的最大归一化回转速度。")]
    [Min(0f)]
    [SerializeField] private float terminalSwingMaxAbsVelocityNormalized = 0.15f;
    [Tooltip("位置和回转速度连续满足多少个决策步才完成 CarryMaterial。")]
    [Min(1)]
    [SerializeField] private int terminalSwingSettledDecisionSteps = 3;

    [Header("References")]
    [Tooltip("挖掘机关节控制器，负责把归一化动作转换为 AGX 关节目标速度")]
    public ExcavatorController controller;

    [Tooltip("挖机语义坐标注册器。观测、轨迹局部误差、可达区域统一相对 MachineRoot。")]
    public ExcavatorFrameRegistry frameRegistry;

    [Tooltip("铲斗尖的 Transform，用于计算到目标的距离")]
    public Transform bucketTip;

    [Tooltip("用于计算铲斗姿态的 Transform。为空时自动查找 Traj_Bucket/Bucket，找不到则退回 bucketTip。")]
    public Transform bucketAttitudeReference;

    [Tooltip("铲斗 pitch 的参考坐标系。应绑定到随 swing 一起旋转的上车/驾驶室/回转平台节点，避免 swing 污染 pitch。为空时退回当前 Agent 根坐标系。")]
    public Transform bucketPitchFrame;

    [Tooltip("轨迹路径点提供器。可以是单点目标，也可以是多点路径")]
    public TrajectoryProvider trajectory;

    [Tooltip("成功到达目标点时触发的共享视觉提示（地板变绿等）")]
    public SuccessIndicator successIndicator;

    [Tooltip("当前训练环境根节点。多环境并行时用于把自动查找限制在本环境内；为空则使用当前对象的最顶层父节点。")]
    public Transform environmentRoot;

    [Tooltip("当前激活可达空间。为空时按任务模式从 TrajectoryProvider 查找。")]
    public ReachableWorkspaceVisualizer reachableWorkspace;

    [Tooltip("MoveToDig 模式使用的可达空间。")]
    public ReachableWorkspaceVisualizer moveToDigReachableWorkspace;

    [Tooltip("CarryMaterial 模式使用的可达空间。")]
    public ReachableWorkspaceVisualizer carryMaterialReachableWorkspace;

    // 任务参数

    [Header("Task")]
    [Tooltip("铲斗尖距离当前目标点小于该值时认为到达（单位：米）")]
    public float targetThreshold = 0.03f;

    [Tooltip("目标点有可视化小球时，把小球半径也计入触达阈值")]
    public bool includeTargetVisualRadiusInThreshold = true;

    [Tooltip("到达单点/路径终点后结束 episode。训练单点跟踪时保持开启")]
    public bool endEpisodeOnTargetReached = true;

    [Tooltip("距离归一化参考值，用于把距离缩放到合理范围")]
    public float distanceNormalizer = 0.5f;

    [Tooltip("局部位置归一化参考值")]
    public float positionNormalizer = 0.5f;

    [Header("Spinelli-style waypoint tracking")]
    [Tooltip("前视 waypoint 数量。默认保持 3；如修改，必须同步 Behavior Parameters 的观测维度。")]
    public int waypointLookahead = 3; // waypointLookahead数量影响了观测维度,规则是waypointLookahead * 3,因为每个waypoint都有x,y,z三个坐标
    [Tooltip("路径 tube 半径。铲斗尖应尽量保持在相邻 waypoint 连线形成的 tube 内。")]
    public float tubeRadius = 0.04f;

    [Header("Bucket attitude conditioning")]
    [Tooltip("当前轨迹任务模式。MoveToDig 要求铲斗接近入铲姿态；CarryMaterial 要求铲斗接近运输姿态。")]
    public TrajectoryTaskMode taskMode = TrajectoryTaskMode.MoveToDig;

    [Tooltip("每个 episode 开始时随机采样任务模式。关闭后使用 Inspector 中的固定 taskMode。")]
    public bool randomizeTaskModeOnEpisodeBegin = true;

    [Tooltip("用于定义铲斗 pitch 的铲斗局部参考轴。需要与真机 FK 中使用的参考轴保持一致。")]
    public Vector3 bucketPitchAxisLocal = Vector3.forward;

    [Tooltip("MoveToDig 模式下的目标铲斗 pitch，单位：度。需要按当前模型坐标系标定。")]
    public float moveToDigTargetBucketPitchDeg = -90f;

    [Tooltip("CarryMaterial 模式下的目标铲斗 pitch，单位：度。通常为水平或略微后仰。")]
    public float carryMaterialTargetBucketPitchDeg = 0f;

    [Tooltip("铲斗 pitch 归一化角度，单位：度。观测和统计使用。")]
    public float bucketPitchNormalizerDeg = 180f;

    [Header("Locked scale profile")]
    [Tooltip("启动时从 shared/machine_profile.jsonc 读取观测归一化常数。")]
    public bool loadObservationNormalizersFromMachineProfile = true;

    [Tooltip("打印一次 38 维观测协议自检日志。")]
    public bool logObservationProfileOnInitialize = false;

    [Tooltip("锁定最大回合步数。手册 §4 规定为 1500。")]
    public int maxEpisodeSteps = kLockedMaxEpisodeSteps;

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

    [Tooltip("铲斗姿态误差惩罚权重。误差越大惩罚越大，用于条件化不同任务姿态。")]
    public float wBucketAttitude = 0.1f;

    [Tooltip("铲斗姿态误差减少时的奖励权重，帮助策略主动把铲斗转向目标姿态。")]
    public float wBucketAttitudeProgress = 0.2f;

    [Tooltip("铲斗姿态角速度惩罚权重，防止姿态抖动。")]
    public float wBucketAngularVelocity = 0.0005f;

    [Tooltip("到达 waypoint 和完成路径的奖励是否按铲斗姿态误差缩放，避免只追点不管姿态。")]
    public bool scaleWaypointBonusByBucketAttitude = true;

    [Tooltip("姿态误差小于该角度时，waypoint/path bonus 基本不衰减。")]
    public float waypointBonusPitchToleranceDeg = 30f;

    [Tooltip("姿态误差很大时仍保留的 waypoint/path bonus 比例，避免训练初期完全无正反馈。")]
    [Range( 0f, 1f )]
    public float minWaypointBonusScaleWhenPitchBad = 0.2f;

    [Tooltip("到达 waypoint 但姿态误差过大时的额外惩罚。")]
    public float badPitchWaypointPenalty = 0.25f;

    // Episode 复位

    [Header("Episode reset")]
    [Tooltip("是否在 episode 开始时重置挖掘机姿态到初始位置")]
    public bool resetPoseOnEpisodeBegin = true;

    [Tooltip("姿态复位时保持 LockController 的物理步数，-1 表示使用 controller 默认值")]
    public int poseResetSettleSteps = -1;

    [Header("Termination")]
    [Tooltip("单个 waypoint 最多允许消耗的决策步数。超过后结束 episode，避免长期卡点刷无效样本；0 表示关闭。")]
    public int maxStepsPerWaypoint = 1200;

    [Tooltip("单个 waypoint 超时后的额外惩罚。")]
    public float waypointTimeoutPenalty = 1.0f;

    [Tooltip("是否启用可达区域越界失败终止。调试时可关闭以观察轨迹和动作。")]
    public bool failOnOutOfBounds = true;

    [Tooltip("连续越界多少个决策步后结束 episode。")]
    public int outOfBoundsFailureSteps = kDefaultOutOfBoundsFailureSteps;

    [Tooltip("距离可达区域最近点超过 distanceNormalizer 的多少倍时算越界。")]
    public float outOfBoundsDistanceMultiplier = 1.5f;

    [Tooltip("是否启用卡死失败终止。调试时可关闭以确认是否由 stuck 触发 reset。")]
    public bool failOnStuck = true;

    [Tooltip("卡死检测窗口长度，单位为决策步。")]
    public int stuckWindowSteps = kDefaultStuckWindowSteps;

    [FormerlySerializedAs( "stuckMinProgressDelta" )]
    [Tooltip("卡死检测窗口内，铲尖相对 MachineRoot 的最小移动距离，单位米。")]
    public float stuckMinTipMotion = kDefaultStuckMinTipMotion;

    [Tooltip("卡死检测需要的最小平均动作幅值。低于该值认为没有尝试移动，不判定 stuck。")]
    public float stuckMinActionMagnitude = kDefaultStuckMinActionMagnitude;

    // 调试选项

    [Header("Debug")]
    [Tooltip("打印 episode 开始/结束等事件日志")]
    public bool debugEpisodeEvents = true;

    [Tooltip("引用为空时只在 environmentRoot 内自动查找。多环境并行时建议显式绑定引用。")]
    public bool autoFindReferences = true;

    [Tooltip("打印前若干次动作，用于确认 Heuristic 或策略是否真的给了动作")]
    public bool debugActions = false;

    [Tooltip("最多打印的动作次数")]
    public int debugActionPrints = 20;

    [Tooltip("向 TensorBoard 记录轨迹跟踪评价指标")]
    public bool recordTrainingStats = true;

    [Tooltip("打印前若干步锁定奖励分项，便于验收量级。")]
    public bool debugRewardTerms = false;

    [Tooltip("最多打印的奖励分项次数。")]
    public int debugRewardPrints = 20;

    // 运行时状态

    /// <summary>上一步铲斗尖到目标的距离，用于计算进度奖励</summary>
    private float m_previousDistance = 0f;

    private float m_previousProgress = 0f;

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
    private bool m_episodeOutOfBounds = false;
    private bool m_episodeStuck = false;
    private int m_outOfBoundsSteps = 0;
    private int m_debugRewardPrintCount = 0;
    private string m_episodeFailureReason = "";
    private string m_lastEpisodeEndReason = "";

    private int m_episodeStepSamples = 0;
    private float m_episodeActionMagnitudeSum = 0f;
    private float m_episodePositiveProgressSum = 0f;
    private float m_episodeSignedProgressSum = 0f;
    private float m_currentWaypointBestDistance = 0f;

    private Vector4 m_previousAction = Vector4.zero;
    private Vector4 m_previousJointPose = Vector4.zero;
    private Vector4 m_jointVelocity = Vector4.zero;
    private Vector3 m_previousTipLocal = Vector3.zero;
    private Vector3 m_tipVelocityLocal = Vector3.zero;
    private Vector3 m_episodeStartTipPosition = Vector3.zero;
    private float m_previousBucketPitchDeg = 0f;
    private float m_previousBucketPitchErrorAbsDeg = 0f;
    private float m_bucketPitchVelocityNormalized = 0f;
    private float m_episodeBucketPitchErrorSum = 0f;
    private float m_episodeBucketPitchProgressSum = 0f;
    private float m_episodeWaypointBonusScaleSum = 0f;
    private int m_episodeWaypointBonusScaleSamples = 0;
    private Vector4 m_episodeDeadzoneNonzeroActionCounts = Vector4.zero;
    private float m_episodeDeadzoneBumpPenaltySum = 0f;
    private float m_episodeTerminalSwingPenaltySum = 0f;
    private int m_terminalSwingStableCount = 0;
    private int m_episodeTerminalSwingWaitSteps = 0;
    private Vector4 m_episodeCommandDeadzoneScales = Vector4.one;
    private bool m_hasPreviousMotionState = false;
    private Vector3 m_stuckWindowStartTipBase = Vector3.zero;
    private int m_stuckWindowCount = 0;
    private float m_stuckWindowMaxActionMagnitude = 0f;
    private float m_previousMotionTime = 0f;
    private int m_completedPathSerial = 0;
    private int m_policyActionSequence = 0;
    private bool m_policyDecisionPending = false;
    private MachineProfile m_machineProfile = null;
    private ContinuousPathMissionCourse m_missionCourse;
    private ContinuousPathBenchmark m_benchmark;
    private int m_episodeStartStep;
    private int EffectiveEpisodeStep => UsesContinuousPath && m_continuousPath.resetClockAfterHoming
      ? ( m_waitingForPoseReset ? 0 : Mathf.Max( StepCount - m_episodeStartStep, 0 ) ) : StepCount;
    private ContinuousPathTracking m_continuousPath;
    private bool UsesContinuousPath => m_continuousPath != null && m_continuousPath.isActiveAndEnabled;

    // UI / 调试只读属性

    /// <summary>当前 episode 编号</summary>
    [HideInInspector] public int CurrentEpisode = 0;

    /// <summary>当前累积奖励（ML-Agents 内部维护）</summary>
    public float CurrentScore => GetCumulativeReward();
    public float EpisodeRewardDeadzoneScale { get; private set; } = 1f;
    public float EpisodeRewardPitchScale { get; private set; } = 1f;

    /// <summary>当前铲斗尖到目标的距离</summary>
    public float CurrentDistanceToTarget => CurrentDistanceToTargetInternal();

    /// <summary>当前到达目标的阈值（含可视化小球半径）</summary>
    public float CurrentTargetThreshold => CurrentReachThreshold();

    /// <summary>当前正在跟踪的路径点索引</summary>
    public int CurrentTargetIndex => trajectory != null ? trajectory.CurrentIndex : 0;

    /// <summary>路径点总数</summary>
    public int CurrentTargetCount => trajectory != null ? trajectory.TargetCount : HasTarget() ? 1 : 0;

    /// <summary>当前路径完成进度，范围 0 到 1</summary>
    public float CurrentPathProgress => UsesContinuousPath ? m_continuousPath.Progress : trajectory != null ? trajectory.Progress : 0f;

    /// <summary>每次完整路径成功完成时递增。用于 demo 状态机跨 episode reset 稳定检测完成事件。</summary>
    public int CompletedPathSerial => m_completedPathSerial;

    public TrajectoryTaskMode CurrentTaskMode => taskMode;
    public float CurrentBucketPitchDeg => CurrentBucketPitchDegrees();
    public float CurrentTargetBucketPitchDeg => TargetBucketPitchDegrees();
    public float CurrentBucketPitchErrorDeg => BucketPitchErrorDegrees();
    public string CurrentEpisodeFailureReason => m_lastEpisodeEndReason;
    public int CurrentWaypointSteps => m_stepsOnCurrentWaypoint;
    public int CurrentOutOfBoundsSteps => m_outOfBoundsSteps;
    public int CurrentStuckWindowSamples => m_stuckWindowCount;
    public Vector4 LastRawPolicyAction { get; private set; } = Vector4.zero;
    public Vector4 CurrentEpisodeCommandDeadzoneScales => m_episodeCommandDeadzoneScales;
    public event System.Action<Vector4, int, double> PolicyActionReceived;

    /// <summary>
    /// 标记会被方向性死区吞掉的非零策略动作。零动作和越过死区的动作不标记。
    /// </summary>
    public static Vector4 DirectionalDeadzoneNonzeroMask( Vector4 rawAction, MachineProfile profile )
    {
      return DirectionalDeadzoneNonzeroMaskWithScales( rawAction, profile, Vector4.one );
    }

    public static Vector4 DirectionalDeadzoneNonzeroMaskWithScales(
      Vector4 rawAction,
      MachineProfile profile,
      Vector4 deadzoneScales )
    {
      if ( profile == null )
        throw new System.ArgumentNullException( nameof( profile ) );

      return new Vector4(
        IsNonzeroInsideDirectionalDeadzone( rawAction.x, profile.GetActuator( "boom" ), deadzoneScales.x ) ? 1f : 0f,
        IsNonzeroInsideDirectionalDeadzone( rawAction.y, profile.GetActuator( "stick" ), deadzoneScales.y ) ? 1f : 0f,
        IsNonzeroInsideDirectionalDeadzone( rawAction.z, profile.GetActuator( "bucket" ), deadzoneScales.z ) ? 1f : 0f,
        IsNonzeroInsideDirectionalDeadzone( rawAction.w, profile.GetActuator( "swing" ), deadzoneScales.w ) ? 1f : 0f );
    }

    /// <summary>
    /// 旧平坦死区惩罚，仅保留用于基线回归；当前训练奖励使用 DeadzoneBumpPenalty。
    /// </summary>
    public static float DeadzoneNonzeroActionPenalty( Vector4 rawAction, MachineProfile profile )
    {
      Vector4 mask = DirectionalDeadzoneNonzeroMask( rawAction, profile );
      return -kDeadzoneNonzeroActionPenaltyPerAxis * ( mask.x + mask.y + mask.z + mask.w );
    }

    /// <summary>
    /// 将可复现的 [0,1] 样本映射为非负、有限的死区倍率。
    /// 运行时传入 Unity Random.value，测试可直接覆盖边界和异常输入。
    /// </summary>
    public static float DeadzoneScaleFromUnitSample( float minimum, float maximum, float unitSample )
    {
      if ( float.IsNaN( minimum ) || float.IsInfinity( minimum ) || minimum < 0f
        || float.IsNaN( maximum ) || float.IsInfinity( maximum ) || maximum < 0f )
        return 1f;

      float lower = Mathf.Min( minimum, maximum );
      float upper = Mathf.Max( minimum, maximum );
      float safeSample = float.IsNaN( unitSample ) || float.IsInfinity( unitSample )
        ? 0.5f
        : Mathf.Clamp01( unitSample );
      return Mathf.Lerp( lower, upper, safeSample );
    }

    public static Vector4 DeadzoneScalesFromUnitSamples(
      float minimum,
      float maximum,
      Vector4 unitSamples )
    {
      return new Vector4(
        DeadzoneScaleFromUnitSample( minimum, maximum, unitSamples.x ),
        DeadzoneScaleFromUnitSample( minimum, maximum, unitSamples.y ),
        DeadzoneScaleFromUnitSample( minimum, maximum, unitSamples.z ),
        DeadzoneScaleFromUnitSample( minimum, maximum, unitSamples.w ) );
    }

    /// <summary>
    /// A1 平滑死区 bump 惩罚：逃逸阈值 D=d*(1+margin)，用 4t(1-t)(t=|a|/D)计分，
    /// 峰值在 t=0.5，a=0 与 |a|>=D 处为 0。控制器边界 |a|=d 仍受惩罚。
    /// </summary>
    public static float DeadzoneBumpPenalty(
      Vector4 rawAction,
      MachineProfile profile,
      float weight,
      float escapeMargin )
    {
      return DeadzoneBumpPenaltyWithScales(
        rawAction,
        profile,
        weight,
        escapeMargin,
        Vector4.one );
    }

    public static float DeadzoneBumpPenaltyWithScales(
      Vector4 rawAction,
      MachineProfile profile,
      float weight,
      float escapeMargin,
      Vector4 deadzoneScales )
    {
      if ( profile == null )
        throw new System.ArgumentNullException( nameof( profile ) );

      float safeWeight = float.IsNaN( weight ) || float.IsInfinity( weight )
        ? 0f
        : Mathf.Max( weight, 0f );
      float sum =
        DirectionalDeadzoneBumpWithScale( rawAction.x, profile.GetActuator( "boom" ), escapeMargin, deadzoneScales.x )
        + DirectionalDeadzoneBumpWithScale( rawAction.y, profile.GetActuator( "stick" ), escapeMargin, deadzoneScales.y )
        + DirectionalDeadzoneBumpWithScale( rawAction.z, profile.GetActuator( "bucket" ), escapeMargin, deadzoneScales.z )
        + DirectionalDeadzoneBumpWithScale( rawAction.w, profile.GetActuator( "swing" ), escapeMargin, deadzoneScales.w );
      return -safeWeight * sum;
    }

    /// <summary>
    /// 单轴死区 bump 值 4t(1-t)，范围 [0,1]。零动作、清出逃逸余量、无死区、NaN/Inf 均返回 0。
    /// </summary>
    public static float DirectionalDeadzoneBump(
      float rawAction,
      MachineActuatorProfile actuator,
      float escapeMargin )
    {
      return DirectionalDeadzoneBumpWithScale( rawAction, actuator, escapeMargin, 1f );
    }

    public static float DirectionalDeadzoneBumpWithScale(
      float rawAction,
      MachineActuatorProfile actuator,
      float escapeMargin,
      float deadzoneScale )
    {
      if ( float.IsNaN( rawAction ) || float.IsInfinity( rawAction ) )
        return 0f;

      float command = Mathf.Clamp( rawAction, -1f, 1f );
      if ( command == 0f )
        return 0f;

      float deadzone = command < 0f
        ? actuator.CommandDeadzoneNegativeNormalized
        : actuator.CommandDeadzonePositiveNormalized;
      deadzone *= SafeDeadzoneScale( deadzoneScale );
      if ( deadzone <= 0f )
        return 0f;

      float safeEscapeMargin = float.IsNaN( escapeMargin ) || float.IsInfinity( escapeMargin )
        ? 0f
        : Mathf.Max( escapeMargin, 0f );
      float escapeThreshold = Mathf.Min( deadzone * ( 1f + safeEscapeMargin ), 1f );
      float magnitude = Mathf.Abs( command );
      if ( magnitude >= escapeThreshold )
        return 0f;

      float t = magnitude / escapeThreshold;
      return 4f * t * ( 1f - t );
    }

    /// <summary>
    /// A2 返回经方向死区置零后的等效动作（与控制器一致），用于按实际执行计能耗。
    /// </summary>
    public static Vector4 DirectionalDeadzoneEffectiveAction( Vector4 rawAction, MachineProfile profile )
    {
      return DirectionalDeadzoneEffectiveActionWithScales( rawAction, profile, Vector4.one );
    }

    public static Vector4 DirectionalDeadzoneEffectiveActionWithScales(
      Vector4 rawAction,
      MachineProfile profile,
      Vector4 deadzoneScales )
    {
      if ( profile == null )
        throw new System.ArgumentNullException( nameof( profile ) );

      return new Vector4(
        DeadzoneEffectiveAxis( rawAction.x, profile.GetActuator( "boom" ), deadzoneScales.x ),
        DeadzoneEffectiveAxis( rawAction.y, profile.GetActuator( "stick" ), deadzoneScales.y ),
        DeadzoneEffectiveAxis( rawAction.z, profile.GetActuator( "bucket" ), deadzoneScales.z ),
        DeadzoneEffectiveAxis( rawAction.w, profile.GetActuator( "swing" ), deadzoneScales.w ) );
    }

    /// <summary>
    /// A2 关闭时使用 raw 动作计能耗；开启时改用经方向死区置零的等效动作。
    /// </summary>
    public static Vector4 DeadzoneEnergyAction(
      Vector4 rawAction,
      MachineProfile profile,
      bool useEffectiveAction )
    {
      return DeadzoneEnergyActionWithScales(
        rawAction,
        profile,
        useEffectiveAction,
        Vector4.one );
    }

    public static Vector4 DeadzoneEnergyActionWithScales(
      Vector4 rawAction,
      MachineProfile profile,
      bool useEffectiveAction,
      Vector4 deadzoneScales )
    {
      return useEffectiveAction
        ? DirectionalDeadzoneEffectiveActionWithScales( rawAction, profile, deadzoneScales )
        : rawAction;
    }

    private static float DeadzoneEffectiveAxis(
      float rawAction,
      MachineActuatorProfile actuator,
      float deadzoneScale )
    {
      if ( float.IsNaN( rawAction ) || float.IsInfinity( rawAction ) )
        return 0f;

      float command = Mathf.Clamp( rawAction, -1f, 1f );
      if ( command == 0f )
        return 0f;

      float deadzone = command < 0f
        ? actuator.CommandDeadzoneNegativeNormalized
        : actuator.CommandDeadzonePositiveNormalized;
      deadzone *= SafeDeadzoneScale( deadzoneScale );
      return Mathf.Abs( command ) <= deadzone ? 0f : command;
    }

    private static float SafeDeadzoneScale( float scale )
    {
      return float.IsNaN( scale ) || float.IsInfinity( scale )
        ? 1f
        : Mathf.Max( scale, 0f );
    }

    public override void Initialize()
    {
      CurrentEpisode = 0;
      maxEpisodeSteps = kLockedMaxEpisodeSteps;
      MaxStep = kLockedMaxEpisodeSteps;
      m_continuousPath = GetComponent<ContinuousPathTracking>();
      m_missionCourse = GetComponent<ContinuousPathMissionCourse>();
      m_benchmark = GetComponent<ContinuousPathBenchmark>();

      if ( environmentRoot == null )
        environmentRoot = transform.root;

      if ( autoFindReferences )
        ResolveMissingReferences();

      LoadMachineProfileAndApplyObservationConstants();
      // Agent.Initialize may run before this sibling component's OnEnable.
      // Read its configured state here; isActiveAndEnabled is lifecycle-dependent.
      if ( m_continuousPath != null && m_continuousPath.enabled && m_continuousPath.resetClockAfterHoming )
        MaxStep = 0; // profile加载后关闭自动计时；新课程显式限制1500个有效执行步。
      ValidateReferences();
      if ( UsesContinuousPath )
        Debug.Log( "[ExcavatorTrajectoryAgent] continuous_path_v1: independent 47D observation contract; "
          + "machine_profile supplies mechanical scales only. Legacy 38D models are not compatible.", this );
    }

    public override void OnEpisodeBegin()
    {
      RecordEpisodeStatsIfNeeded( false );

      CurrentEpisode++;
      m_missingReferenceWarned = false;
      m_debugActionPrintCount = 0;
      m_debugRewardPrintCount = 0;
      m_waitingForPoseReset = true;
      m_episodeActive = false;
      m_episodeStatsRecorded = false;
      m_episodeReachedTargets = 0;
      m_episodeCompletedPath = false;
      m_episodeMinDistance = float.PositiveInfinity;
      m_stepsOnCurrentWaypoint = 0;
      m_episodeTimedOut = false;
      m_episodeOutOfBounds = false;
      m_episodeStuck = false;
      m_outOfBoundsSteps = 0;
      m_episodeFailureReason = "";
      m_episodeStepSamples = 0;
      m_episodeActionMagnitudeSum = 0f;
      m_episodePositiveProgressSum = 0f;
      m_episodeSignedProgressSum = 0f;
      m_currentWaypointBestDistance = 0f;
      m_episodeBucketPitchErrorSum = 0f;
      m_episodeBucketPitchProgressSum = 0f;
      m_episodeWaypointBonusScaleSum = 0f;
      m_episodeWaypointBonusScaleSamples = 0;
      m_episodeDeadzoneNonzeroActionCounts = Vector4.zero;
      m_episodeDeadzoneBumpPenaltySum = 0f;
      m_episodeTerminalSwingPenaltySum = 0f;
      m_terminalSwingStableCount = 0;
      m_episodeTerminalSwingWaitSteps = 0;

      // Reward-only ablations are fixed for the episode; task and observation contracts stay unchanged.
      EpisodeRewardDeadzoneScale = ReadRewardScale( "reward_deadzone_scale" );
      EpisodeRewardPitchScale = ReadRewardScale( "reward_pitch_scale" );
      LoadTerminalSwingStabilizationParameters();

      m_episodeCommandDeadzoneScales = randomizeCommandDeadzonesOnEpisodeBegin
        ? DeadzoneScalesFromUnitSamples(
          commandDeadzoneScaleMin,
          commandDeadzoneScaleMax,
          new Vector4( Random.value, Random.value, Random.value, Random.value ) )
        : Vector4.one;
      if ( controller != null && m_machineProfile != null )
        controller.ApplyMachineProfileCommandDeadzoneScales( m_machineProfile, m_episodeCommandDeadzoneScales );

      if ( randomizeTaskModeOnEpisodeBegin )
        taskMode = Random.value < 0.5f ? TrajectoryTaskMode.MoveToDig : TrajectoryTaskMode.CarryMaterial;

      if ( m_benchmark != null )
        m_benchmark.Prepare( this );

      // 候选连续路径从复位后的真实铲尖生成；旧场景保留原初始化时序。
      if ( UsesContinuousPath )
        m_continuousPath.InvalidatePath();
      else if ( trajectory != null )
        trajectory.ResetPath();

      if ( debugEpisodeEvents )
        Debug.Log( $"[ExcavatorTrajectoryAgent] Episode {CurrentEpisode} begin." );

      if ( UsesContinuousPath && m_missionCourse != null && m_missionCourse.enabled )
        m_missionCourse.PrepareEpisode( controller );

      if ( resetPoseOnEpisodeBegin && controller != null ) {
        controller.BeginHomePoseReset( poseResetSettleSteps );
        return;
      }

      BeginEpisodeMeasurements();
    }

    /// <summary>
    /// 默认保持 38 维；只有挂载 ContinuousPathTracking 的独立候选场景输出 47 维。
    /// </summary>
    public override void CollectObservations( VectorSensor sensor )
    {
      // 连续路径必须先从复位后的铲尖生成，再产生首个有效策略观测。
      if ( UsesContinuousPath && m_waitingForPoseReset && ( controller == null || !controller.IsHoming ) )
        BeginEpisodeMeasurements();
      // DecisionRequester may re-apply the stored action between decisions.  Only the
      // action following a fresh observation request is a new policy decision.
      m_policyDecisionPending = true;
      Vector3 tipBase = CurrentTipBasePosition();
      UpdateMotionEstimates( tipBase );

      AddActuatorPositionVelocityObservation( sensor, "boom", CurrentBoomPosition(), m_jointVelocity.x );
      AddActuatorPositionVelocityObservation( sensor, "stick", CurrentStickPosition(), m_jointVelocity.y );
      AddActuatorPositionVelocityObservation( sensor, "bucket", CurrentBucketPosition(), m_jointVelocity.z );

      float swingAngle = CurrentSwingPosition();
      sensor.AddObservation( Mathf.Sin( swingAngle ) );
      sensor.AddObservation( Mathf.Cos( swingAngle ) );
      sensor.AddObservation( m_jointVelocity.w );

      AddVectorObservation( sensor, tipBase / Mathf.Max( positionNormalizer, 0.01f ) );
      AddVectorObservation( sensor, m_tipVelocityLocal );
      if ( UsesContinuousPath )
        m_continuousPath.AddPathObservations( sensor, tipBase, distanceNormalizer );
      else {
        AddLookaheadWaypointObservations( sensor, tipBase );
        sensor.AddObservation( trajectory != null ? trajectory.Progress : 0f );
        sensor.AddObservation( ClampedTubeCrossTrackRatio() );
        sensor.AddObservation( IsFinalWaypoint() ? 1f : 0f );
      }
      sensor.AddObservation( taskMode == TrajectoryTaskMode.MoveToDig ? 1f : 0f );
      sensor.AddObservation( taskMode == TrajectoryTaskMode.CarryMaterial ? 1f : 0f );
      sensor.AddObservation( Mathf.Clamp01( (float)EffectiveEpisodeStep / Mathf.Max( maxEpisodeSteps, 1 ) ) );

      sensor.AddObservation( m_previousAction.x );
      sensor.AddObservation( m_previousAction.y );
      sensor.AddObservation( m_previousAction.z );
      sensor.AddObservation( m_previousAction.w );

      AddBucketAttitudeObservations( sensor );
    }

    /// <summary>
    /// 4 维连续动作：boom、stick、bucket、swing 的归一化目标速度。
    /// </summary>
    public override void OnActionReceived( ActionBuffers actions )
    {
      if ( controller != null && controller.IsHoming )
        return;

      if ( m_waitingForPoseReset && UsesContinuousPath ) {
        // Homing 结束可能落在两个决策之间；等待新观测，不执行 homing 期间存下的动作。
        if ( controller != null )
          controller.SetNormalizedSpeeds( 0f, 0f, 0f, 0f );
        RequestDecision();
        return;
      }
      if ( m_waitingForPoseReset )
        BeginEpisodeMeasurements();

      var a = actions.ContinuousActions;
      float boom = a.Length > 0 ? a[ 0 ] : 0f;
      float stick = a.Length > 1 ? a[ 1 ] : 0f;
      float bucket = a.Length > 2 ? a[ 2 ] : 0f;
      float swing = a.Length > 3 ? a[ 3 ] : 0f;
      Vector4 action = new Vector4( boom, stick, bucket, swing );
      LastRawPolicyAction = action;
      bool isNewPolicyDecision = m_policyDecisionPending;

      if ( debugActions && m_debugActionPrintCount < debugActionPrints ) {
        Debug.Log( $"[ExcavatorTrajectoryAgent] action boom={boom:F3}, stick={stick:F3}, bucket={bucket:F3}, swing={swing:F3}" );
        m_debugActionPrintCount++;
      }

      if ( controller != null )
        controller.SetNormalizedSpeeds( boom, stick, bucket, swing );

      if ( isNewPolicyDecision ) {
        m_policyDecisionPending = false;
        PublishPolicyAction( action );
      }

      if ( bucketTip == null || trajectory == null ) {
        if ( debugEpisodeEvents && !m_missingReferenceWarned ) {
          Debug.LogWarning( "[ExcavatorTrajectoryAgent] Missing bucketTip or trajectory reference. Ending episode." );
          m_missingReferenceWarned = true;
        }
        EndEpisodeAsFailure( "missing_reference" );
        return;
      }

      if ( UsesContinuousPath ) {
        EvaluateContinuousPathAction( action, isNewPolicyDecision );
        return;
      }

      float distance = CurrentDistanceToTargetInternal();
      m_episodeMinDistance = Mathf.Min( m_episodeMinDistance, distance );
      m_currentWaypointBestDistance = Mathf.Min( m_currentWaypointBestDistance, distance );
      m_previousDistance = distance;
      float distanceScale = Mathf.Max( distanceNormalizer, 0.01f );
      float normalizedDistance = distance / distanceScale;
      // A2 开启时按本 episode 随机死区置零后的等效动作计能耗。
      Vector4 energyAction = m_machineProfile != null
        ? DeadzoneEnergyActionWithScales(
          action,
          m_machineProfile,
          deadzoneEnergyUseEffective,
          m_episodeCommandDeadzoneScales )
        : action;
      float actionMeanSquare = energyAction.sqrMagnitude / 4f;
      float actionMeanAbs = ( Mathf.Abs( boom ) + Mathf.Abs( stick ) + Mathf.Abs( bucket ) + Mathf.Abs( swing ) ) / 4f;
      float actionDeltaMeanSquare = ( action - m_previousAction ).sqrMagnitude / 4f;
      float tubeCrossTrackRatio = RawTubeCrossTrackRatio();
      float tubeViolation = Mathf.Max( 0f, Mathf.Abs( tubeCrossTrackRatio ) - 1f );
      float bucketPitchErrorAbsDeg = Mathf.Abs( BucketPitchErrorDegrees() );
      float bucketPitchError = bucketPitchErrorAbsDeg / Mathf.Max( bucketPitchNormalizerDeg, 1f );
      bool completedThisStep = false;

      m_stepsOnCurrentWaypoint++;
      m_episodeStepSamples++;
      m_episodeActionMagnitudeSum += Mathf.Abs( boom ) + Mathf.Abs( stick ) + Mathf.Abs( bucket ) + Mathf.Abs( swing );
      m_episodeBucketPitchErrorSum += bucketPitchError;

      bool terminalSwingGateActive = terminalSwingStabilizationEnabled
        && taskMode == TrajectoryTaskMode.CarryMaterial
        && IsFinalWaypoint();
      float reachThreshold = terminalSwingGateActive
        ? Mathf.Max( terminalSwingReachThresholdM, 0f )
        : CurrentReachThreshold();
      bool positionReached = distance <= reachThreshold;
      bool terminalSwingSettled = !terminalSwingGateActive
        || TrajectoryTerminalStabilization.IsSettled(
          distance,
          reachThreshold,
          m_jointVelocity.w,
          terminalSwingMaxAbsVelocityNormalized );
      m_terminalSwingStableCount = terminalSwingGateActive && isNewPolicyDecision
        ? TrajectoryTerminalStabilization.NextStableCount(
          m_terminalSwingStableCount,
          terminalSwingSettled )
        : terminalSwingGateActive ? m_terminalSwingStableCount : 0;
      bool waypointReady = positionReached && ( !terminalSwingGateActive
        || m_terminalSwingStableCount >= Mathf.Max( terminalSwingSettledDecisionSteps, 1 ) );
      if ( terminalSwingGateActive && isNewPolicyDecision && positionReached && !waypointReady )
        m_episodeTerminalSwingWaitSteps++;

      if ( waypointReady && !m_episodeCompletedPath ) {
        bool completed = trajectory == null || trajectory.AdvanceTarget();
        m_episodeReachedTargets++;
        m_stepsOnCurrentWaypoint = 0;
        m_terminalSwingStableCount = 0;
        AddReward( kWaypointReward );

        if ( completed ) {
          m_episodeCompletedPath = true;
          completedThisStep = true;
          m_completedPathSerial++;
          AddReward( kCompleteReward );
          if ( debugEpisodeEvents )
            Debug.Log( $"[ExcavatorTrajectoryAgent] Path complete. episode={CurrentEpisode}, score={GetCumulativeReward():F3}" );

          if ( successIndicator != null )
            successIndicator.Show();
        }
        else {
          m_previousDistance = CurrentDistanceToTargetInternal();
          m_currentWaypointBestDistance = m_previousDistance;
          ResetStuckWindow( CurrentTipBasePosition() );
          if ( debugEpisodeEvents )
            Debug.Log( $"[ExcavatorTrajectoryAgent] Waypoint reached. next={trajectory.CurrentIndex}, distance={m_previousDistance:F2}m" );
        }
      }

      float progress = trajectory != null ? trajectory.Progress : 0f;
      float progressDelta = progress - m_previousProgress;
      m_previousProgress = progress;
      m_episodePositiveProgressSum += Mathf.Max( progressDelta, 0f );
      m_episodeSignedProgressSum += progressDelta;

      float progressReward = kProgressRewardWeight * progressDelta;
      float trackingPenalty = -kTrackingRewardWeight * normalizedDistance;
      float tubePenalty = -kTubeRewardWeight * tubeViolation;
      float pitchPenalty = -kPitchRewardWeight * bucketPitchError * EpisodeRewardPitchScale;
      float smoothPenalty = -kSmoothRewardWeight * actionDeltaMeanSquare;
      float energyPenalty = -kEnergyRewardWeight * actionMeanSquare;
      Vector4 deadzoneNonzeroMask = m_machineProfile != null
        ? DirectionalDeadzoneNonzeroMaskWithScales(
          action,
          m_machineProfile,
          m_episodeCommandDeadzoneScales )
        : Vector4.zero;
      // A1 用 bump 惩罚取代平坦惩罚；死区内非零 mask 仍单独用于 KPI 指标(下方统计)。
      float deadzonePenalty = m_machineProfile != null
        ? EpisodeRewardDeadzoneScale * DeadzoneBumpPenaltyWithScales(
          action,
          m_machineProfile,
          deadzoneBumpWeight,
          deadzoneEscapeMargin,
          m_episodeCommandDeadzoneScales )
        : 0f;
      float terminalSwingPenalty = terminalSwingGateActive
        ? TrajectoryTerminalStabilization.CalculatePenalty(
          distance,
          terminalSwingApproachDistanceM,
          m_jointVelocity.w,
          swing,
          terminalSwingVelocityWeight,
          terminalSwingDriveWeight )
        : 0f;
      m_episodeDeadzoneNonzeroActionCounts += deadzoneNonzeroMask;
      m_episodeDeadzoneBumpPenaltySum += deadzonePenalty;
      m_episodeTerminalSwingPenaltySum += terminalSwingPenalty;

      AddReward( progressReward );
      AddReward( trackingPenalty );
      AddReward( tubePenalty );
      AddReward( pitchPenalty );
      AddReward( smoothPenalty );
      AddReward( energyPenalty );
      AddReward( deadzonePenalty );
      AddReward( terminalSwingPenalty );
      AddReward( -kTimePenalty );

      DebugRewardTerms(
        progressReward,
        trackingPenalty,
        tubePenalty,
        pitchPenalty,
        smoothPenalty,
        energyPenalty,
        deadzonePenalty,
        distance,
        progress,
        tubeCrossTrackRatio,
        bucketPitchErrorAbsDeg );

      if ( completedThisStep ) {
        m_previousAction = action;
        m_previousBucketPitchErrorAbsDeg = bucketPitchErrorAbsDeg;
        EndEpisodeWithStats( true );
        return;
      }

      if ( StepCount + 1 >= maxEpisodeSteps ) {
        m_episodeTimedOut = true;
        EndEpisodeAsFailure( "timeout" );
        return;
      }

      if ( maxStepsPerWaypoint > 0 && m_stepsOnCurrentWaypoint >= maxStepsPerWaypoint ) {
        m_episodeTimedOut = true;
        AddReward( -Mathf.Max( waypointTimeoutPenalty, 0f ) );
        EndEpisodeAsFailure( "waypoint_timeout" );
        return;
      }

      if ( failOnOutOfBounds && IsOutOfReachableWorkspace() ) {
        m_outOfBoundsSteps++;
        if ( m_outOfBoundsSteps > Mathf.Max( outOfBoundsFailureSteps, 0 ) ) {
          m_episodeOutOfBounds = true;
          EndEpisodeAsFailure( "out_of_bounds" );
          return;
        }
      }
      else {
        m_outOfBoundsSteps = 0;
      }

      if ( failOnStuck && IsMechanicallyStuck( CurrentTipBasePosition(), actionMeanAbs ) ) {
        m_episodeStuck = true;
        EndEpisodeAsFailure( "stuck" );
        return;
      }

      m_previousAction = action;
      m_previousBucketPitchErrorAbsDeg = bucketPitchErrorAbsDeg;
    }

    private void EvaluateContinuousPathAction( Vector4 action, bool isNewPolicyDecision )
    {
      Vector3 tip = CurrentTipBasePosition();
      AddReward( m_continuousPath.EvaluateStep(
        tip, BucketPitchErrorDegrees(), action, Time.fixedDeltaTime, isNewPolicyDecision ) );
      if ( m_benchmark != null )
        m_benchmark.RecordSample( this, m_continuousPath, tip );
      m_previousAction = action;
      m_episodeMinDistance = Mathf.Min( m_episodeMinDistance, m_continuousPath.EndpointDistanceM );

      if ( m_continuousPath.Completed ) {
        m_episodeCompletedPath = true;
        m_completedPathSerial++;
        if ( successIndicator != null )
          successIndicator.Show();
        EndEpisodeWithStats( true );
        return;
      }

      if ( EffectiveEpisodeStep + 1 >= maxEpisodeSteps ) {
        m_episodeTimedOut = true;
        EndEpisodeAsFailure( "timeout" );
        return;
      }

      // 复用已有机械活动范围及卡死处理；连续路径没有 waypoint timeout。
      m_outOfBoundsSteps = failOnOutOfBounds && IsOutOfReachableWorkspace() ? m_outOfBoundsSteps + 1 : 0;
      if ( m_outOfBoundsSteps > Mathf.Max( outOfBoundsFailureSteps, 0 ) ) {
        m_episodeOutOfBounds = true;
        EndEpisodeAsFailure( "out_of_bounds" );
        return;
      }
      float meanAction = ( Mathf.Abs( action.x ) + Mathf.Abs( action.y ) + Mathf.Abs( action.z ) + Mathf.Abs( action.w ) ) / 4f;
      if ( failOnStuck && IsMechanicallyStuck( tip, meanAction ) ) {
        m_episodeStuck = true;
        EndEpisodeAsFailure( "stuck" );
      }
    }

    private void PublishPolicyAction( Vector4 rawAction )
    {
      Vector4 commanded = controller == null
        ? new Vector4(
          Mathf.Clamp( rawAction.x, -1f, 1f ),
          Mathf.Clamp( rawAction.y, -1f, 1f ),
          Mathf.Clamp( rawAction.z, -1f, 1f ),
          Mathf.Clamp( rawAction.w, -1f, 1f ) )
        : new Vector4(
          controller.LastBoomCommand,
          controller.LastStickCommand,
          controller.LastBucketCommand,
          controller.LastSwingCommand );
      int sequence = m_policyActionSequence++;
      try {
        PolicyActionReceived?.Invoke( commanded, sequence, Time.realtimeSinceStartupAsDouble );
      }
      catch ( System.Exception exception ) {
        Debug.LogWarning( "[ExcavatorTrajectoryAgent] Policy action observer failed: " + exception.Message, this );
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
      m_episodeStartStep = StepCount;
      if ( UsesContinuousPath && trajectory != null && bucketTip != null ) {
        trajectory.ResetPath();
        Transform root = MachineRootFrame();
        Vector3[] path;
        if ( m_missionCourse != null && m_missionCourse.enabled ) {
          path = m_missionCourse.CreateReference( CurrentTipBasePosition() );
          var missionWorldPoints = new Vector3[ path.Length ];
          for ( int i = 0; i < path.Length; i++ )
            missionWorldPoints[ i ] = root.TransformPoint( path[ i ] );
          trajectory.SetPlannedPath( missionWorldPoints );
        }
        else if ( m_continuousPath.generateTrainingReference && trajectory.randomizePathOnReset ) {
          if ( m_continuousPath.preserveTrainingPathShape ) {
            var sampled = new Vector3[ trajectory.TargetCount ];
            for ( int i = 0; i < sampled.Length; i++ )
              sampled[ i ] = root.InverseTransformPoint( trajectory.GetWaypointPosition( i ) );
            path = ContinuousPathTracking.AlignTrainingReferenceStart( sampled, CurrentTipBasePosition() );
          }
          else {
            Vector3 endpoint = root.InverseTransformPoint( trajectory.GetWaypointPosition( trajectory.TargetCount - 1 ) );
            float bend = trajectory.reachableWorkspaceUseCurvedPaths ? trajectory.reachableWorkspaceCurveStrength : 0f;
            path = ContinuousPathTracking.CreateTrainingReferencePath(
              CurrentTipBasePosition(), endpoint, 5, Random.onUnitSphere, bend );
          }
          var worldPoints = new Vector3[ path.Length ];
          for ( int i = 0; i < path.Length; i++ )
            worldPoints[ i ] = root.TransformPoint( path[ i ] );
          trajectory.SetPlannedPath( worldPoints ); // 显示和诊断使用与训练相同的参考。
        }
        else {
          path = new Vector3[ trajectory.TargetCount + 1 ];
          path[ 0 ] = CurrentTipBasePosition();
          for ( int i = 0; i < trajectory.TargetCount; i++ )
            path[ i + 1 ] = root.InverseTransformPoint( trajectory.GetWaypointPosition( i ) );
        }
        bool smoothReference = m_continuousPath.smoothTrainingReferenceProbability > 0f &&
          Random.value < m_continuousPath.smoothTrainingReferenceProbability;
        if ( smoothReference )
          path = SmoothReferencePath.Build( path, m_continuousPath.smoothCornerCutDistanceM,
            m_continuousPath.smoothSampleSpacingM );
        m_continuousPath.ResetPath( path );
        if ( m_continuousPath.smoothTrainingReferenceProbability > 0f ) {
          Academy.Instance.StatsRecorder.Add( "ContinuousPath/TrainingSmoothedReferenceRate", smoothReference ? 1f : 0f );
          Academy.Instance.StatsRecorder.Add( "ContinuousPath/TrainingReferencePoints", path.Length );
        }
        if ( m_benchmark != null )
          m_benchmark.RecordStart( this, path );
      }
      m_previousDistance = CurrentDistanceToTargetInternal();
      m_previousProgress = trajectory != null ? trajectory.Progress : 0f;
      m_episodeMinDistance = bucketTip != null && trajectory != null ? m_previousDistance : 0f;
      m_currentWaypointBestDistance = m_previousDistance;
      m_episodeActive = true;
      m_episodeStartTipPosition = bucketTip != null ? bucketTip.position : transform.position;
      ResetMotionHistory();
      ResetStuckWindow( CurrentTipBasePosition() );

      if ( debugEpisodeEvents )
        Debug.Log( $"[ExcavatorTrajectoryAgent] Episode {CurrentEpisode} begin. distance={m_previousDistance:F2}m" );
    }

    private void EndEpisodeWithStats( bool completed )
    {
      if ( completed )
        m_lastEpisodeEndReason = "success";

      RecordEpisodeStatsIfNeeded( completed );
      if ( m_benchmark != null )
        m_benchmark.RecordEnd( completed, completed ? "success" : m_episodeFailureReason );
      EndEpisode();
    }

    private void RecordEpisodeStatsIfNeeded( bool completed )
    {
      if ( !recordTrainingStats || !m_episodeActive || m_episodeStatsRecorded )
        return;

      m_episodeStatsRecorded = true;

      if ( UsesContinuousPath ) {
        m_continuousPath.RecordEpisodeStats( completed || m_episodeCompletedPath, taskMode.ToString() );
        var continuousStats = Academy.Instance.StatsRecorder;
        bool missionCourse = m_missionCourse != null && m_missionCourse.enabled;
        string course = missionCourse ? "Mission" : "Workspace";
        string courseTask = $"ContinuousPath/{course}/{taskMode}";
        float finished = completed || m_episodeCompletedPath ? 1f : 0f;
        continuousStats.Add( $"{courseTask}/SuccessRate", finished, StatAggregationMethod.Average );
        continuousStats.Add( $"{courseTask}/TerminalErrorM", m_continuousPath.EndpointDistanceM, StatAggregationMethod.Average );
        continuousStats.Add( $"{courseTask}/Progress", m_continuousPath.Progress, StatAggregationMethod.Average );
        continuousStats.Add( $"{courseTask}/GeometricMeanCrossTrackM", m_continuousPath.GeometricMeanCrossTrackDistanceM, StatAggregationMethod.Average );
        continuousStats.Add( $"{courseTask}/GeometricMaxCrossTrackM", m_continuousPath.GeometricMaxCrossTrackDistanceM, StatAggregationMethod.Average );
        if ( missionCourse && m_missionCourse.LastPrefixFraction >= 0.999f )
          continuousStats.Add( $"{courseTask}/FullPathSuccessRate", finished, StatAggregationMethod.Average );
        continuousStats.Add( "ContinuousPath/TimeoutRate", m_episodeTimedOut ? 1f : 0f, StatAggregationMethod.Average );
        continuousStats.Add( "ContinuousPath/OutOfBoundsRate", m_episodeOutOfBounds ? 1f : 0f, StatAggregationMethod.Average );
        continuousStats.Add( "ContinuousPath/StuckRate", m_episodeStuck ? 1f : 0f, StatAggregationMethod.Average );
        return;
      }

      float finalDistance = CurrentDistanceToTargetInternal();
      float minDistance = float.IsPositiveInfinity( m_episodeMinDistance ) ? finalDistance : m_episodeMinDistance;
      float pathProgress = trajectory != null ? trajectory.Progress : 0f;
      float targetCount = Mathf.Max( CurrentTargetCount, 1 );

      float success = completed || m_episodeCompletedPath ? 1f : 0f;
      float waypointReachRate = m_episodeReachedTargets / targetCount;
      float timeout = m_episodeTimedOut ? 1f : 0f;
      float sampleCount = Mathf.Max( m_episodeStepSamples, 1 );
      float meanActionMagnitude = m_episodeActionMagnitudeSum / sampleCount;
      float meanPositiveProgress = m_episodePositiveProgressSum / sampleCount;
      float meanBucketPitchErrorDeg = m_episodeBucketPitchErrorSum / sampleCount * Mathf.Max( bucketPitchNormalizerDeg, 1f );
      float meanWaypointBonusScale = m_episodeWaypointBonusScaleSamples > 0 ? m_episodeWaypointBonusScaleSum / m_episodeWaypointBonusScaleSamples : 0f;
      Vector4 deadzoneNonzeroRate = m_episodeDeadzoneNonzeroActionCounts / sampleCount;

      var stats = Academy.Instance.StatsRecorder;
      stats.Add( "Trajectory/SuccessRate", success, StatAggregationMethod.Average );
      stats.Add( "Trajectory/WaypointsReached", m_episodeReachedTargets, StatAggregationMethod.Average );
      stats.Add( "Trajectory/WaypointReachRate", waypointReachRate, StatAggregationMethod.Average );
      stats.Add( "Trajectory/WaypointTimeoutRate", timeout, StatAggregationMethod.Average );
      stats.Add( "Trajectory/OutOfBoundsRate", m_episodeOutOfBounds ? 1f : 0f, StatAggregationMethod.Average );
      stats.Add( "Trajectory/StuckRate", m_episodeStuck ? 1f : 0f, StatAggregationMethod.Average );
      stats.Add( "Trajectory/PathProgress", pathProgress, StatAggregationMethod.Average );
      stats.Add( "Trajectory/FinalDistance", finalDistance, StatAggregationMethod.Average );
      stats.Add( "Trajectory/MinDistance", minDistance, StatAggregationMethod.Average );

      stats.Add( "Trajectory/MeanActionMagnitude", meanActionMagnitude, StatAggregationMethod.Average );
      stats.Add( "Trajectory/MeanPositiveProgress", meanPositiveProgress, StatAggregationMethod.Average );
      stats.Add( "Trajectory/SignedProgress", m_episodeSignedProgressSum, StatAggregationMethod.Average );
      stats.Add( "Trajectory/TaskMode", taskMode == TrajectoryTaskMode.CarryMaterial ? 1f : 0f, StatAggregationMethod.Average );
      stats.Add( "Trajectory/BucketPitchErrorDeg", meanBucketPitchErrorDeg, StatAggregationMethod.Average );
      stats.Add( "Trajectory/BucketPitchProgress", m_episodeBucketPitchProgressSum, StatAggregationMethod.Average );
      stats.Add( "Trajectory/WaypointBonusScale", meanWaypointBonusScale, StatAggregationMethod.Average );
      stats.Add( "Trajectory/RewardDeadzoneScale", EpisodeRewardDeadzoneScale, StatAggregationMethod.Average );
      stats.Add( "Trajectory/RewardPitchScale", EpisodeRewardPitchScale, StatAggregationMethod.Average );
      stats.Add( "Trajectory/DeadzoneNonzeroAxesPerStep", deadzoneNonzeroRate.x + deadzoneNonzeroRate.y + deadzoneNonzeroRate.z + deadzoneNonzeroRate.w, StatAggregationMethod.Average );
      stats.Add( "Trajectory/DeadzoneBumpPenaltyPerStep", m_episodeDeadzoneBumpPenaltySum / sampleCount, StatAggregationMethod.Average );
      stats.Add( "Trajectory/DeadzoneNonzeroRateBoom", deadzoneNonzeroRate.x, StatAggregationMethod.Average );
      stats.Add( "Trajectory/DeadzoneNonzeroRateStick", deadzoneNonzeroRate.y, StatAggregationMethod.Average );
      stats.Add( "Trajectory/DeadzoneNonzeroRateBucket", deadzoneNonzeroRate.z, StatAggregationMethod.Average );
      stats.Add( "Trajectory/DeadzoneNonzeroRateSwing", deadzoneNonzeroRate.w, StatAggregationMethod.Average );
      stats.Add( "Trajectory/DeadzoneScaleBoom", m_episodeCommandDeadzoneScales.x, StatAggregationMethod.Average );
      stats.Add( "Trajectory/DeadzoneScaleStick", m_episodeCommandDeadzoneScales.y, StatAggregationMethod.Average );
      stats.Add( "Trajectory/DeadzoneScaleBucket", m_episodeCommandDeadzoneScales.z, StatAggregationMethod.Average );
      stats.Add( "Trajectory/DeadzoneScaleSwing", m_episodeCommandDeadzoneScales.w, StatAggregationMethod.Average );
      stats.Add( "Trajectory/TerminalSwingPenaltyPerStep", m_episodeTerminalSwingPenaltySum / sampleCount, StatAggregationMethod.Average );
      stats.Add( "Trajectory/TerminalSwingWaitSteps", m_episodeTerminalSwingWaitSteps, StatAggregationMethod.Average );

      string taskPrefix = taskMode == TrajectoryTaskMode.CarryMaterial ? "Trajectory/CarryMaterial" : "Trajectory/MoveToDig";
      stats.Add( $"{taskPrefix}/SuccessRate", success, StatAggregationMethod.Average );
      stats.Add( $"{taskPrefix}/WaypointReachRate", waypointReachRate, StatAggregationMethod.Average );
      stats.Add( $"{taskPrefix}/WaypointTimeoutRate", timeout, StatAggregationMethod.Average );
      stats.Add( $"{taskPrefix}/PathProgress", pathProgress, StatAggregationMethod.Average );
      stats.Add( $"{taskPrefix}/BucketPitchErrorDeg", meanBucketPitchErrorDeg, StatAggregationMethod.Average );
      stats.Add( $"{taskPrefix}/BucketPitchProgress", m_episodeBucketPitchProgressSum, StatAggregationMethod.Average );
      stats.Add( $"{taskPrefix}/WaypointBonusScale", meanWaypointBonusScale, StatAggregationMethod.Average );
      stats.Add( $"{taskPrefix}/FinalDistance", finalDistance, StatAggregationMethod.Average );
    }

    private float CurrentDistanceToTargetInternal()
    {
      if ( UsesContinuousPath && m_continuousPath.Ready )
        return m_continuousPath.EndpointDistanceM;
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
      if ( UsesContinuousPath )
        return m_continuousPath.endpointToleranceM;
      float threshold = Mathf.Max( targetThreshold, 0f );
      if ( !includeTargetVisualRadiusInThreshold || trajectory == null )
        return threshold;

      Transform target = trajectory.CurrentTargetTransform;
      if ( target == null )
        return threshold;

      var visual = target.GetComponent<TargetPointVisual>();
      return visual != null ? threshold + Mathf.Max( visual.radius, 0f ) : threshold;
    }

    private void LoadTerminalSwingStabilizationParameters()
    {
      var parameters = Academy.Instance.EnvironmentParameters;
      terminalSwingStabilizationEnabled = parameters.GetWithDefault(
        "terminal_swing_stabilization_enabled",
        terminalSwingStabilizationEnabled ? 1f : 0f ) >= 0.5f;
      terminalSwingReachThresholdM = parameters.GetWithDefault(
        "terminal_swing_reach_threshold_m",
        terminalSwingReachThresholdM );
      terminalSwingApproachDistanceM = parameters.GetWithDefault(
        "terminal_swing_approach_distance_m",
        terminalSwingApproachDistanceM );
      terminalSwingVelocityWeight = parameters.GetWithDefault(
        "terminal_swing_velocity_weight",
        terminalSwingVelocityWeight );
      terminalSwingDriveWeight = parameters.GetWithDefault(
        "terminal_swing_drive_weight",
        terminalSwingDriveWeight );
      terminalSwingMaxAbsVelocityNormalized = parameters.GetWithDefault(
        "terminal_swing_max_abs_velocity_normalized",
        terminalSwingMaxAbsVelocityNormalized );
      terminalSwingSettledDecisionSteps = Mathf.RoundToInt( parameters.GetWithDefault(
        "terminal_swing_settled_decision_steps",
        terminalSwingSettledDecisionSteps ) );
    }

    private static float ReadRewardScale( string name )
    {
      float value = Academy.Instance.EnvironmentParameters.GetWithDefault( name, 1f );
      if ( float.IsNaN( value ) || float.IsInfinity( value ) || value < 0f || value > 1f )
        throw new System.ArgumentOutOfRangeException( name, value, "Reward scale must be finite and in [0, 1]." );
      return value;
    }

    private void AddLookaheadWaypointObservations( VectorSensor sensor, Vector3 tipBase )
    {
      int lookahead = Mathf.Clamp( waypointLookahead, 1, 3 );
      int baseIndex = trajectory != null ? trajectory.CurrentIndex : 0;

      for ( int i = 0; i < lookahead; i++ ) {
        Vector3 waypointBase = Vector3.zero;
        if ( trajectory != null )
          waypointBase = MachineRootFrame().InverseTransformPoint( trajectory.GetWaypointPosition( baseIndex + i ) );

        AddVectorObservation( sensor, ( waypointBase - tipBase ) / Mathf.Max( distanceNormalizer, 0.01f ) );
      }

      for ( int i = lookahead; i < 3; i++ )
        AddVectorObservation( sensor, Vector3.zero );
    }

    private void AddBucketAttitudeObservations( VectorSensor sensor )
    {
      float normalizer = Mathf.Max( bucketPitchNormalizerDeg, 1f );
      sensor.AddObservation( CurrentBucketPitchDegrees() / normalizer );
      sensor.AddObservation( TargetBucketPitchDegrees() / normalizer );
      sensor.AddObservation( BucketPitchErrorDegrees() / normalizer );
      sensor.AddObservation( m_bucketPitchVelocityNormalized );
    }

    private bool IsFinalWaypoint()
    {
      return trajectory == null || trajectory.CurrentIndex >= trajectory.TargetCount - 1;
    }

    private float ClampedTubeCrossTrackRatio()
    {
      return Mathf.Clamp( RawTubeCrossTrackRatio(), -1f, 1f );
    }

    private float RawTubeCrossTrackRatio()
    {
      return TubeCrossTrackDistance() / Mathf.Max( tubeRadius, 0.01f );
    }

    private float TubeCrossTrackDistance()
    {
      if ( bucketTip == null || trajectory == null || trajectory.TargetCount <= 1 )
        return 0f;

      Vector3 point = bucketTip.position;
      int current = trajectory.CurrentIndex;
      Vector3 from = current > 0 ? trajectory.GetWaypointPosition( current - 1 ) : m_episodeStartTipPosition;
      Vector3 to = trajectory.GetWaypointPosition( current );
      Vector3 segment = to - from;
      float lengthSq = segment.sqrMagnitude;
      if ( lengthSq < 1e-6f )
        return Vector3.Distance( point, to );

      float t = Mathf.Clamp01( Vector3.Dot( point - from, segment ) / lengthSq );
      Vector3 closest = from + segment * t;
      return Vector3.Distance( point, closest );
    }

    private void UpdateMotionEstimates( Vector3 tipBase )
    {
      Vector4 jointPose = new Vector4(
        CurrentBoomPosition(),
        CurrentStickPosition(),
        CurrentBucketPosition(),
        CurrentSwingPosition()
      );

      float bucketPitchDeg = CurrentBucketPitchDegrees();

      if ( m_hasPreviousMotionState ) {
        float dt = Mathf.Max( Time.time - m_previousMotionTime, Time.fixedDeltaTime, 0.001f );
        Vector4 rawJointVelocity = ( jointPose - m_previousJointPose ) / dt;
        m_jointVelocity = new Vector4(
          NormalizedActuatorVelocity( "boom", rawJointVelocity.x ),
          NormalizedActuatorVelocity( "stick", rawJointVelocity.y ),
          NormalizedActuatorVelocity( "bucket", rawJointVelocity.z ),
          NormalizedActuatorVelocity( "swing", rawJointVelocity.w ) );
        m_tipVelocityLocal = ( tipBase - m_previousTipLocal ) / dt / Mathf.Max( TipVelocityScale(), 0.001f );
        m_bucketPitchVelocityNormalized =
          Mathf.Clamp( Mathf.DeltaAngle( m_previousBucketPitchDeg, bucketPitchDeg ) / dt / 180f, -1f, 1f );
      }
      else {
        m_jointVelocity = Vector4.zero;
        m_tipVelocityLocal = Vector3.zero;
        m_bucketPitchVelocityNormalized = 0f;
        m_hasPreviousMotionState = true;
      }

      m_previousJointPose = jointPose;
      m_previousTipLocal = tipBase;
      m_previousBucketPitchDeg = bucketPitchDeg;
      m_previousMotionTime = Time.time;
    }

    private void ResetMotionHistory()
    {
      m_previousAction = Vector4.zero;
      LastRawPolicyAction = Vector4.zero;
      m_previousJointPose = new Vector4(
        CurrentBoomPosition(),
        CurrentStickPosition(),
        CurrentBucketPosition(),
        CurrentSwingPosition()
      );
      m_previousTipLocal = CurrentTipBasePosition();
      m_previousBucketPitchDeg = CurrentBucketPitchDegrees();
      m_previousBucketPitchErrorAbsDeg = Mathf.Abs( BucketPitchErrorDegrees() );
      m_bucketPitchVelocityNormalized = 0f;
      m_jointVelocity = Vector4.zero;
      m_tipVelocityLocal = Vector3.zero;
      m_hasPreviousMotionState = true;
      m_previousMotionTime = Time.time;
    }

    private void LoadMachineProfileAndApplyObservationConstants()
    {
      try {
        m_machineProfile = MachineProfileLoader.Load();
        if ( !loadObservationNormalizersFromMachineProfile )
          return;

        MachineObservationSchema schema = m_machineProfile.ObservationSchema;

        if ( schema.TotalDim != kLockedObservationSize )
          Debug.LogError( $"[ExcavatorTrajectoryAgent] machine_profile observation total_dim={schema.TotalDim}, expected {kLockedObservationSize}.", this );
        if ( schema.ActionDim != 4 )
          Debug.LogError( $"[ExcavatorTrajectoryAgent] machine_profile action_dim={schema.ActionDim}, expected 4.", this );

        MachineObservationNormalizers normalizers = schema.Normalizers;
        positionNormalizer = normalizers.PositionNormalizer;
        distanceNormalizer = normalizers.DistanceNormalizer;
        tubeRadius = normalizers.TubeRadius;
        targetThreshold = normalizers.TargetThreshold;
        bucketPitchNormalizerDeg = normalizers.PitchNormDeg;
        waypointLookahead = schema.WaypointLookahead;

        MachineTaskProfile taskProfile = m_machineProfile.TaskProfile;
        moveToDigTargetBucketPitchDeg = taskProfile.MoveToDigBucketPitchTargetDeg;
        carryMaterialTargetBucketPitchDeg = taskProfile.CarryMaterialBucketPitchTargetDeg;
        maxEpisodeSteps = kLockedMaxEpisodeSteps;
        MaxStep = kLockedMaxEpisodeSteps;

        if ( logObservationProfileOnInitialize ) {
          Debug.Log(
            "[ExcavatorTrajectoryAgent] observation profile "
            + $"id={schema.Id}, total_dim={schema.TotalDim}, action_dim={schema.ActionDim}, "
            + $"position_normalizer={positionNormalizer:F3}, tip_velocity_scale={TipVelocityScale():F3}, "
            + $"distance_normalizer={distanceNormalizer:F3}, tube_radius={tubeRadius:F3}, "
            + $"target_threshold={targetThreshold:F3}, pitch_norm_deg={bucketPitchNormalizerDeg:F1}, "
            + $"move_to_dig_pitch={moveToDigTargetBucketPitchDeg:F1}, carry_material_pitch={carryMaterialTargetBucketPitchDeg:F1}",
            this );
        }
      }
      catch ( System.Exception exception ) {
        Debug.LogError( "[ExcavatorTrajectoryAgent] Failed to load observation constants from machine_profile.jsonc: " + exception.Message, this );
      }
    }

    private MachineActuatorProfile ActuatorProfile( string actuatorName )
    {
      if ( m_machineProfile == null )
        return null;

      try {
        return m_machineProfile.GetActuator( actuatorName );
      }
      catch ( System.Exception ) {
        return null;
      }
    }

    private float TipVelocityScale()
    {
      return m_machineProfile != null
        ? m_machineProfile.ObservationSchema.Normalizers.TipVelocityScale
        : 0.05f;
    }

    private void AddActuatorPositionVelocityObservation( VectorSensor sensor, string actuatorName, float rawPosition, float normalizedVelocity )
    {
      sensor.AddObservation( NormalizeActuatorPosition( actuatorName, rawPosition ) );
      sensor.AddObservation( Mathf.Clamp( normalizedVelocity, -1f, 1f ) );
    }

    private float NormalizeActuatorPosition( string actuatorName, float rawPosition )
    {
      MachineActuatorProfile actuator = ActuatorProfile( actuatorName );
      if ( actuator != null && actuator.HasFiniteRange ) {
        float span = actuator.RangeMax - actuator.RangeMin;
        if ( Mathf.Abs( span ) > 1e-6f ) {
          float normalized = ( rawPosition - actuator.RangeMin ) / span * 2f - 1f;
          return Mathf.Clamp( normalized * Mathf.Sign( actuator.Sign == 0 ? 1 : actuator.Sign ), -1f, 1f );
        }
      }

      if ( controller == null )
        return 0f;

      if ( actuatorName == "boom" )
        return controller.BoomNormalizedPosition;
      if ( actuatorName == "stick" )
        return controller.StickNormalizedPosition;
      if ( actuatorName == "bucket" )
        return controller.BucketNormalizedPosition;
      if ( actuatorName == "swing" )
        return controller.SwingNormalizedPosition;

      return 0f;
    }

    private float NormalizedActuatorVelocity( string actuatorName, float rawVelocity )
    {
      MachineActuatorProfile actuator = ActuatorProfile( actuatorName );
      int sign = actuator != null && actuator.Sign != 0 ? actuator.Sign : 1;
      float signedVelocity = rawVelocity * sign;
      float maxSpeed = actuator != null ? actuator.MaxSpeedForDirection( signedVelocity ) : FallbackMaxSpeed( actuatorName, signedVelocity );

      if ( maxSpeed <= 1e-6f )
        return 0f;

      return Mathf.Clamp( signedVelocity / maxSpeed, -1f, 1f );
    }

    private float FallbackMaxSpeed( string actuatorName, float signedVelocity )
    {
      if ( controller == null )
        return 0f;

      return controller.MaxSpeedForDirection( actuatorName, signedVelocity );
    }

    private Vector3 CurrentTipBasePosition()
    {
      return bucketTip != null ? MachineRootFrame().InverseTransformPoint( bucketTip.position ) : Vector3.zero;
    }

    private float CurrentBoomPosition()
    {
      return controller != null ? controller.BoomPosition : 0f;
    }

    private float CurrentStickPosition()
    {
      return controller != null ? controller.StickPosition : 0f;
    }

    private float CurrentBucketPosition()
    {
      return controller != null ? controller.BucketPosition : 0f;
    }

    private float CurrentSwingPosition()
    {
      return controller != null ? controller.SwingPosition : 0f;
    }

    private ReachableWorkspaceVisualizer ActiveReachableWorkspace()
    {
      if ( taskMode == TrajectoryTaskMode.CarryMaterial )
        return carryMaterialReachableWorkspace != null
          ? carryMaterialReachableWorkspace
          : trajectory != null && trajectory.carryMaterialReachableWorkspace != null
            ? trajectory.carryMaterialReachableWorkspace
            : reachableWorkspace;

      return moveToDigReachableWorkspace != null
        ? moveToDigReachableWorkspace
        : trajectory != null && trajectory.moveToDigReachableWorkspace != null
          ? trajectory.moveToDigReachableWorkspace
          : reachableWorkspace;
    }

    private bool IsOutOfReachableWorkspace()
    {
      ReachableWorkspaceVisualizer workspace = ActiveReachableWorkspace();
      if ( workspace == null || bucketTip == null )
        return false;

      Vector3 closest = workspace.ClosestWorldPointInVolume( bucketTip.position );
      float distanceToWorkspace = Vector3.Distance( bucketTip.position, closest );
      return distanceToWorkspace > Mathf.Max( outOfBoundsDistanceMultiplier, 0f ) * Mathf.Max( distanceNormalizer, 0.01f );
    }

    private void ResetStuckWindow( Vector3 tipBasePosition )
    {
      m_stuckWindowStartTipBase = tipBasePosition;
      m_stuckWindowCount = 0;
      m_stuckWindowMaxActionMagnitude = 0f;
    }

    private bool IsMechanicallyStuck( Vector3 tipBasePosition, float actionMagnitude )
    {
      if ( !failOnStuck )
        return false;

      int requiredSamples = Mathf.Max( stuckWindowSteps, 1 );
      m_stuckWindowCount++;
      m_stuckWindowMaxActionMagnitude = Mathf.Max( m_stuckWindowMaxActionMagnitude, Mathf.Abs( actionMagnitude ) );

      if ( m_stuckWindowCount < requiredSamples )
        return false;

      float tipMotion = Vector3.Distance( tipBasePosition, m_stuckWindowStartTipBase );
      bool hasCommand = m_stuckWindowMaxActionMagnitude >= Mathf.Max( stuckMinActionMagnitude, 0f );
      bool hasAlmostNoMotion = tipMotion < Mathf.Max( stuckMinTipMotion, 0f );
      ResetStuckWindow( tipBasePosition );

      return hasCommand && hasAlmostNoMotion;
    }

    private void EndEpisodeAsFailure( string reason )
    {
      m_episodeFailureReason = reason;
      m_lastEpisodeEndReason = reason;
      AddReward( -kFailurePenalty );

      if ( debugEpisodeEvents )
        Debug.Log( $"[ExcavatorTrajectoryAgent] Episode failed: {m_episodeFailureReason}. episode={CurrentEpisode}, score={GetCumulativeReward():F3}", this );

      EndEpisodeWithStats( false );
    }

    private void DebugRewardTerms(
      float progressReward,
      float trackingPenalty,
      float tubePenalty,
      float pitchPenalty,
      float smoothPenalty,
      float energyPenalty,
      float deadzonePenalty,
      float distance,
      float progress,
      float tubeCrossTrackRatio,
      float bucketPitchErrorAbsDeg )
    {
      if ( !debugRewardTerms || m_debugRewardPrintCount >= debugRewardPrints )
        return;

      Debug.Log(
        "[ExcavatorTrajectoryAgent] reward "
        + $"progress={progressReward:F4}, tracking={trackingPenalty:F4}, tube={tubePenalty:F4}, "
        + $"pitch={pitchPenalty:F4}, smooth={smoothPenalty:F4}, energy={energyPenalty:F4}, deadzone={deadzonePenalty:F4}, time={-kTimePenalty:F4}, "
        + $"distance={distance:F3}, pathProgress={progress:F3}, tubeRatio={tubeCrossTrackRatio:F3}, pitchErrDeg={bucketPitchErrorAbsDeg:F1}",
        this );
      m_debugRewardPrintCount++;
    }

    private static bool IsNonzeroInsideDirectionalDeadzone(
      float rawAction,
      MachineActuatorProfile actuator,
      float deadzoneScale )
    {
      if ( float.IsNaN( rawAction ) || float.IsInfinity( rawAction ) )
        return false;

      float command = Mathf.Clamp( rawAction, -1f, 1f );
      if ( command == 0f )
        return false;

      float deadzone = command < 0f
        ? actuator.CommandDeadzoneNegativeNormalized
        : actuator.CommandDeadzonePositiveNormalized;
      deadzone *= SafeDeadzoneScale( deadzoneScale );
      return Mathf.Abs( command ) <= deadzone;
    }

    private float TargetBucketPitchDegrees()
    {
      return taskMode == TrajectoryTaskMode.CarryMaterial ? carryMaterialTargetBucketPitchDeg : moveToDigTargetBucketPitchDeg;
    }

    private float BucketPitchErrorDegrees()
    {
      return Mathf.DeltaAngle( TargetBucketPitchDegrees(), CurrentBucketPitchDegrees() );
    }

    private float CurrentBucketPitchDegrees()
    {
      Transform reference = bucketAttitudeReference != null ? bucketAttitudeReference : bucketTip;
      if ( reference == null )
        return 0f;

      Transform frame = bucketPitchFrame != null ? bucketPitchFrame : MachineRootFrame();
      Vector3 localAxis = bucketPitchAxisLocal.sqrMagnitude > 1e-6f ? bucketPitchAxisLocal.normalized : Vector3.forward;
      Vector3 bucketAxis = reference.TransformDirection( localAxis );
      Vector3 pitchAxis = frame.right;
      Vector3 forwardInPitchPlane = Vector3.ProjectOnPlane( frame.forward, pitchAxis );
      Vector3 bucketInPitchPlane = Vector3.ProjectOnPlane( bucketAxis, pitchAxis );

      if ( forwardInPitchPlane.sqrMagnitude < 1e-6f || bucketInPitchPlane.sqrMagnitude < 1e-6f )
        return 0f;

      return Vector3.SignedAngle( forwardInPitchPlane.normalized, bucketInPitchPlane.normalized, pitchAxis );
    }

    // 环境内查找工具。优先在 Inspector 显式拖引用，自动查找只作为兜底。
    private void ResolveMissingReferences()
    {
      if ( frameRegistry == null )
        frameRegistry = FindInEnvironment<ExcavatorFrameRegistry>() ?? GetComponent<ExcavatorFrameRegistry>();

      if ( frameRegistry != null )
        frameRegistry.ResolveMissingReferences();

      if ( controller == null )
        controller = frameRegistry != null
          ? frameRegistry.GetComponent<ExcavatorController>() ?? FindInEnvironment<ExcavatorController>()
          : FindInEnvironment<ExcavatorController>();

      if ( trajectory == null )
        trajectory = FindInEnvironment<TrajectoryProvider>();

      if ( moveToDigReachableWorkspace == null && trajectory != null )
        moveToDigReachableWorkspace = trajectory.moveToDigReachableWorkspace;

      if ( carryMaterialReachableWorkspace == null && trajectory != null )
        carryMaterialReachableWorkspace = trajectory.carryMaterialReachableWorkspace;

      if ( reachableWorkspace == null )
        reachableWorkspace = moveToDigReachableWorkspace != null ? moveToDigReachableWorkspace : carryMaterialReachableWorkspace;

      if ( bucketTip == null ) {
        if ( frameRegistry != null && frameRegistry.BucketTip != null )
          bucketTip = frameRegistry.BucketTip;
        else {
          var bucketTipObject = FindInEnvironmentByName( "Traj_BucketTip" ) ?? FindInEnvironmentByName( "BucketTip" );
          if ( bucketTipObject != null )
            bucketTip = bucketTipObject.transform;
        }
      }

      if ( bucketAttitudeReference == null ) {
        var bucketObject = FindInEnvironmentByName( "Traj_Bucket" ) ?? FindInEnvironmentByName( "Bucket" );
        bucketAttitudeReference = bucketObject != null ? bucketObject.transform : bucketTip;
      }

      if ( bucketPitchFrame == null ) {
        if ( frameRegistry != null && frameRegistry.BucketPitchFrame != null )
          bucketPitchFrame = frameRegistry.BucketPitchFrame;
        else {
          var frameObject =
            FindInEnvironmentByName( "Traj_Cabin" ) ??
            FindInEnvironmentByName( "Cabin" ) ??
            FindInEnvironmentByName( "UpperCarriage" ) ??
            FindInEnvironmentByName( "Chassisbody" ) ??
            FindInEnvironmentByName( "excavator_CAT365" );
          bucketPitchFrame = frameObject != null ? frameObject.transform : MachineRootFrame();
        }
      }

      if ( successIndicator == null )
        successIndicator = FindOrCreateSuccessIndicator();
    }

    private void ValidateReferences()
    {
      if ( controller == null || bucketTip == null || trajectory == null || bucketAttitudeReference == null || bucketPitchFrame == null ) {
        Debug.LogWarning(
          $"[ExcavatorTrajectoryAgent] Missing references on {name}. " +
          "For multi-environment training, bind frameRegistry, controller, bucketTip, bucketAttitudeReference, bucketPitchFrame, trajectory, and successIndicator inside each environment prefab." );
      }
    }

    private Transform MachineRootFrame()
    {
      if ( frameRegistry == null && autoFindReferences )
        frameRegistry = FindInEnvironment<ExcavatorFrameRegistry>() ?? GetComponent<ExcavatorFrameRegistry>();

      if ( frameRegistry != null && frameRegistry.MachineRoot != null )
        return frameRegistry.MachineRoot;

      return transform;
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
