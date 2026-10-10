using UnityEngine;
using AGXUnity;
using AGXRigidBody = AGXUnity.RigidBody;
using System.Globalization;

namespace RLExcavator
{
  /// <summary>
  /// 挖掘机关节控制器
  ///
  ///   RL动作(-1..1) --> 目标速度(m/s 或 rad/s) --> AGX 的 TargetSpeedController --> 液压缸/铰链运动
  ///
  /// 设计原则：
  ///   1) 不依赖任何外部 Control 系统；所有关节引用都在 Inspector 里手动拖入。
  ///   2) 关节类型无关：boom/stick/bucket 在 AGX 模型里是 Prismatic(液压缸)，swing 是 Hinge(铰链)，
  ///      但 AGXUnity 的 Constraint API 对两者通用，所以下面代码不区分类型。
  ///   3) 只暴露 RL 真正需要的能力：驱动(SetNormalizedSpeeds)、读状态(GetNormalizedPosition)、
  ///      复位姿态(HomeLock)。
  ///
  /// 如何在 Hierarchy 找到这些 Constraint：
  ///   Play 后展开挖掘机根节点，AGXUnity 会把每个关节实例化成带 Constraint 组件的 GameObject，
  ///   名字通常含 boom / stick / bucket / cabin(回转) 等。把它们拖到下面对应字段。
  ///   boom / stick / bucket 都用数组表示，可拖 1~2 根需要同步驱动的液压缸。
  /// </summary>
  public class ExcavatorController : MonoBehaviour
  {
    [Header("关节约束")]
    [Tooltip("大臂液压缸")]
    public Constraint[] boom;

    [Tooltip("小臂液压缸")]
    public Constraint[] stick;

    [Tooltip("铲斗液压缸")]
    public Constraint[] bucket;
    
    [Tooltip("回转")]
    public Constraint swing;

    [Header("每个关节的最大目标速度（prismatic=m/s，hinge=rad/s）")]
    [Tooltip("正向 action 使用的速度上限；运行时由 profile 覆盖。")]
    public float boomMaxSpeedPositive = 0f;
    [Tooltip("反向 action 使用的速度上限；运行时由 profile 覆盖。")]
    public float boomMaxSpeedNegative = 0f;
    [Tooltip("正向 action 使用的速度上限；运行时由 profile 覆盖。")]
    public float stickMaxSpeedPositive = 0f;
    [Tooltip("反向 action 使用的速度上限；运行时由 profile 覆盖。")]
    public float stickMaxSpeedNegative = 0f;
    [Tooltip("正向 action 使用的速度上限；运行时由 profile 覆盖。")]
    public float bucketMaxSpeedPositive = 0f;
    [Tooltip("反向 action 使用的速度上限；运行时由 profile 覆盖。")]
    public float bucketMaxSpeedNegative = 0f;
    [Tooltip("正向 action 使用的速度上限；运行时由 profile 覆盖。")]
    public float swingMaxSpeedPositive = 0f;
    [Tooltip("反向 action 使用的速度上限；运行时由 profile 覆盖。")]
    public float swingMaxSpeedNegative = 0f;

    [Header("机型 Profile")]
    [Tooltip("启动时从 shared/machine_profile.jsonc 读取四个执行器正/反向速度上限，作为速度上限的唯一权威来源。")]
    public bool loadMaxSpeedsFromMachineProfile = true;
    [Tooltip("启动应用 profile 速度后打印一行自检日志。")]
    public bool logMachineProfileSpeedLoad = true;
    [Tooltip("启动时从 shared/machine_profile.jsonc 读取执行器 range，并写入 AGX RangeController。")]
    public bool loadRangesFromMachineProfile = true;
    [Tooltip("启动应用 profile range 后打印一行自检日志。")]
    public bool logMachineProfileRangeLoad = true;
    [Tooltip("启动应用 profile 双向归一化命令死区后打印一行自检日志。")]
    public bool logMachineProfileCommandDeadzoneLoad = true;

    [Header("速度变化限制")]
    [Tooltip("大臂目标速度每秒最大变化量(m/s^2)，越小越柔和")]
    public float boomAcceleration = 0.7f;
    [Tooltip("铲斗目标速度每秒最大变化量(m/s^2)，越小越柔和")]
    public float bucketAcceleration = 0.7f;

    [Header("命令输入闸门")]
    [Tooltip("关闭后忽略 SetNormalizedSpeeds 写入，并保持所有关节目标速度为 0。训练时保持开启；交互 demo 可由 TaskManager 控制。")]
    public bool commandInputEnabled = true;

    [Header("初始位姿复位")]
    [Tooltip("episode 复位时恢复挖掘机根节点的初始位置和旋转")]
    public bool resetRootPoseOnHomeReset = true;
    [Tooltip("episode 复位时清零子物体中所有 Unity/AGX 刚体速度")]
    public bool clearBodyVelocitiesOnHomeReset = true;
    [Tooltip("开启后使用 Inspector 中填写的 home 位姿；关闭则使用 Play 后第一次捕获的位姿")]
    public bool useInspectorHomePose = false;
    [Tooltip("大臂液压缸 home 位移。长度应与 boom 数组一致；两根缸通常填同一组初始读数")]
    public float[] boomHomePose;
    [Tooltip("小臂液压缸 home 位移。长度应与 stick 数组一致")]
    public float[] stickHomePose;
    [Tooltip("铲斗液压缸 home 位移。长度应与 bucket 数组一致")]
    public float[] bucketHomePose;
    [Tooltip("回转 home 角度(rad)")]
    public float swingHomePose = 0f;
    [Tooltip("执行复位时保持 LockController 的物理步数")]
    public int homeSettleSteps = 60;
    [Tooltip("复位最多等待的物理步数，防止异常姿态导致训练永久卡住")]
    public int maxHomeSettleSteps = 300;
    [Tooltip("判断已回到 home 的关节误差阈值(prismatic=m, hinge=rad)")]
    public float homePoseTolerance = 0.01f;
    [Tooltip("复位超时但未到达 home 时打印警告")]
    public bool warnOnHomeTimeout = true;

    private float m_boomSpeed = 0f;
    private float m_bucketSpeed = 0f;
    private Coroutine m_homeRoutine = null;
    private Vector3 m_initialRootPosition = Vector3.zero;
    private Quaternion m_initialRootRotation = Quaternion.identity;
    private bool m_rootPoseCaptured = false;
    private bool m_zeroSpeedPositionHoldEnabled = false;
    private bool m_boomZeroSpeedHoldEnabled = false;
    private bool m_stickZeroSpeedHoldEnabled = false;
    private bool m_bucketZeroSpeedHoldEnabled = false;
    private bool m_swingZeroSpeedHoldEnabled = false;
    private float m_boomCommandDeadzonePositiveNormalized = 0f;
    private float m_boomCommandDeadzoneNegativeNormalized = 0f;
    private float m_stickCommandDeadzonePositiveNormalized = 0f;
    private float m_stickCommandDeadzoneNegativeNormalized = 0f;
    private float m_bucketCommandDeadzonePositiveNormalized = 0f;
    private float m_bucketCommandDeadzoneNegativeNormalized = 0f;
    private float m_swingCommandDeadzonePositiveNormalized = 0f;
    private float m_swingCommandDeadzoneNegativeNormalized = 0f;

    public bool IsHoming { get; private set; } = false;

    // 记录每个关节的初始位置/角度，用于 episode 复位。
    // 注意：AGX 是物理引擎，关节不能"瞬移"复位，只能用 LockController 把它拉回初始位姿。
    private float[] m_boomHome;
    private float[] m_stickHome;
    private float[] m_bucketHome;
    private float   m_swingHome;
    private bool    m_homeCaptured = false;

    public bool CommandInputEnabled => commandInputEnabled;
    public float LastBoomCommand { get; private set; } = 0f;
    public float LastStickCommand { get; private set; } = 0f;
    public float LastBucketCommand { get; private set; } = 0f;
    public float LastSwingCommand { get; private set; } = 0f;
    public float LastBoomTargetSpeed { get; private set; } = 0f;
    public float LastStickTargetSpeed { get; private set; } = 0f;
    public float LastBucketTargetSpeed { get; private set; } = 0f;
    public float LastSwingTargetSpeed { get; private set; } = 0f;

    // ---------- 生命周期 ----------

    private void Awake()
    {
      try {
        MachineProfile profile = MachineProfileLoader.Load();

        ApplyMachineProfileCommandDeadzones( profile );
        if ( loadMaxSpeedsFromMachineProfile )
          ApplyMachineProfileSpeeds( profile );
        if ( loadRangesFromMachineProfile )
          ApplyMachineProfileRanges( profile );

        if ( loadMaxSpeedsFromMachineProfile && logMachineProfileSpeedLoad )
          Debug.Log( FormatMachineProfileSpeedLog(), this );
        if ( loadRangesFromMachineProfile && logMachineProfileRangeLoad )
          Debug.Log( FormatMachineProfileRangeLog( profile ), this );
        if ( logMachineProfileCommandDeadzoneLoad )
          Debug.Log( FormatMachineProfileCommandDeadzoneLog(), this );
      }
      catch ( System.Exception exception ) {
        Debug.LogError( "[ExcavatorController] Failed to apply machine profile: " + exception.Message, this );
      }
    }

    // 在第一次物理步之后再抓初始姿态（此时 AGX 关节已实例化）。
    // 用 FixedUpdate 里的一次性标记，比 Start 更安全（Start 时 AGX Native 可能还没就绪）。
    private void FixedUpdate()
    {
      if ( !m_homeCaptured )
        CaptureHomePose();

    }

    public void ApplyMachineProfileSpeeds( MachineProfile profile )
    {
      if ( profile == null )
        throw new System.ArgumentNullException( nameof( profile ) );

      ApplyMachineProfileSpeed( profile.GetActuator( "boom" ), out boomMaxSpeedPositive, out boomMaxSpeedNegative );
      ApplyMachineProfileSpeed( profile.GetActuator( "stick" ), out stickMaxSpeedPositive, out stickMaxSpeedNegative );
      ApplyMachineProfileSpeed( profile.GetActuator( "bucket" ), out bucketMaxSpeedPositive, out bucketMaxSpeedNegative );
      ApplyMachineProfileSpeed( profile.GetActuator( "swing" ), out swingMaxSpeedPositive, out swingMaxSpeedNegative );
    }

    public void ApplyMachineProfileRanges( MachineProfile profile )
    {
      if ( profile == null )
        throw new System.ArgumentNullException( nameof( profile ) );

      ApplyMachineProfileRangeToConstraints( profile.GetActuator( "boom" ), boom );
      ApplyMachineProfileRangeToConstraints( profile.GetActuator( "stick" ), stick );
      ApplyMachineProfileRangeToConstraints( profile.GetActuator( "bucket" ), bucket );
      ApplyMachineProfileRangeToConstraints( profile.GetActuator( "swing" ), swing );
    }

    public void ApplyMachineProfileCommandDeadzones( MachineProfile profile )
    {
      ApplyMachineProfileCommandDeadzoneScales( profile, Vector4.one );
    }

    /// <summary>
    /// 以 machine profile 为中心值应用每轴死区倍率。仅供训练域随机化使用；
    /// 倍率为 1 时与真实机标定完全一致。
    /// </summary>
    public void ApplyMachineProfileCommandDeadzoneScales( MachineProfile profile, Vector4 scales )
    {
      if ( profile == null )
        throw new System.ArgumentNullException( nameof( profile ) );

      ApplyMachineProfileCommandDeadzone(
        profile.GetActuator( "boom" ),
        scales.x,
        out m_boomCommandDeadzonePositiveNormalized,
        out m_boomCommandDeadzoneNegativeNormalized );
      ApplyMachineProfileCommandDeadzone(
        profile.GetActuator( "stick" ),
        scales.y,
        out m_stickCommandDeadzonePositiveNormalized,
        out m_stickCommandDeadzoneNegativeNormalized );
      ApplyMachineProfileCommandDeadzone(
        profile.GetActuator( "bucket" ),
        scales.z,
        out m_bucketCommandDeadzonePositiveNormalized,
        out m_bucketCommandDeadzoneNegativeNormalized );
      ApplyMachineProfileCommandDeadzone(
        profile.GetActuator( "swing" ),
        scales.w,
        out m_swingCommandDeadzonePositiveNormalized,
        out m_swingCommandDeadzoneNegativeNormalized );
    }

    private string FormatMachineProfileSpeedLog()
    {
      return "[ExcavatorController] signed speed limits from machine_profile: "
        + "boom=" + FormatSignedSpeed( boomMaxSpeedPositive, boomMaxSpeedNegative )
        + ", stick=" + FormatSignedSpeed( stickMaxSpeedPositive, stickMaxSpeedNegative )
        + ", bucket=" + FormatSignedSpeed( bucketMaxSpeedPositive, bucketMaxSpeedNegative )
        + ", swing=" + FormatSignedSpeed( swingMaxSpeedPositive, swingMaxSpeedNegative );
    }

    private static void ApplyMachineProfileSpeed(
      MachineActuatorProfile actuator,
      out float maxSpeedPositive,
      out float maxSpeedNegative )
    {
      maxSpeedPositive = actuator.MaxSpeedPositive;
      maxSpeedNegative = actuator.MaxSpeedNegative;
    }

    private static void ApplyMachineProfileCommandDeadzone(
      MachineActuatorProfile actuator,
      float scale,
      out float positiveNormalized,
      out float negativeNormalized )
    {
      float safeScale = float.IsNaN( scale ) || float.IsInfinity( scale )
        ? 1f
        : Mathf.Max( scale, 0f );
      positiveNormalized = Mathf.Clamp01( actuator.CommandDeadzonePositiveNormalized * safeScale );
      negativeNormalized = Mathf.Clamp01( actuator.CommandDeadzoneNegativeNormalized * safeScale );
    }

    private static string FormatSignedSpeed( float positive, float negative )
    {
      return "+" + positive.ToString( "0.###", CultureInfo.InvariantCulture )
        + "/-" + negative.ToString( "0.###", CultureInfo.InvariantCulture );
    }

    private string FormatMachineProfileCommandDeadzoneLog()
    {
      return "[ExcavatorController] normalized command deadzones from machine_profile: "
        + "boom=" + FormatSignedDeadzone( m_boomCommandDeadzonePositiveNormalized, m_boomCommandDeadzoneNegativeNormalized )
        + ", stick=" + FormatSignedDeadzone( m_stickCommandDeadzonePositiveNormalized, m_stickCommandDeadzoneNegativeNormalized )
        + ", bucket=" + FormatSignedDeadzone( m_bucketCommandDeadzonePositiveNormalized, m_bucketCommandDeadzoneNegativeNormalized )
        + ", swing=" + FormatSignedDeadzone( m_swingCommandDeadzonePositiveNormalized, m_swingCommandDeadzoneNegativeNormalized );
    }

    private static string FormatSignedDeadzone( float positive, float negative )
    {
      return "+" + positive.ToString( "0.###", CultureInfo.InvariantCulture )
        + "/-" + negative.ToString( "0.###", CultureInfo.InvariantCulture );
    }

    private string FormatMachineProfileRangeLog( MachineProfile profile )
    {
      return "[ExcavatorController] range from machine_profile: "
        + FormatActuatorRange( profile.GetActuator( "boom" ) )
        + ", " + FormatActuatorRange( profile.GetActuator( "stick" ) )
        + ", " + FormatActuatorRange( profile.GetActuator( "bucket" ) )
        + ", " + FormatActuatorRange( profile.GetActuator( "swing" ) );
    }

    private static string FormatActuatorRange( MachineActuatorProfile actuator )
    {
      if ( actuator.HasFiniteRange )
        return actuator.Name + "=[" + FormatFloat( actuator.RangeMin ) + "," + FormatFloat( actuator.RangeMax ) + "]";

      return actuator.Name + "=continuous";
    }

    private static string FormatFloat( float value )
    {
      return value.ToString( "0.###", CultureInfo.InvariantCulture );
    }

    private void ApplyMachineProfileRangeToConstraints( MachineActuatorProfile actuator, Constraint[] constraints )
    {
      if ( constraints == null || constraints.Length == 0 ) {
        if ( actuator.RangeEnabledRequired )
          Debug.LogWarning( "[ExcavatorController] No constraints bound for range-required actuator: " + actuator.Name, this );
        return;
      }

      foreach ( var constraint in constraints )
        ApplyMachineProfileRangeToConstraint( actuator, constraint );
    }

    private void ApplyMachineProfileRangeToConstraints( MachineActuatorProfile actuator, Constraint constraint )
    {
      ApplyMachineProfileRangeToConstraint( actuator, constraint );
    }

    private void ApplyMachineProfileRangeToConstraint( MachineActuatorProfile actuator, Constraint constraint )
    {
      if ( constraint == null ) {
        if ( actuator.RangeEnabledRequired )
          Debug.LogWarning( "[ExcavatorController] Null constraint bound for range-required actuator: " + actuator.Name, this );
        return;
      }

      if ( !actuator.HasFiniteRange ) {
        var continuousRange = constraint.GetController<RangeController>();
        if ( continuousRange != null )
          continuousRange.Enable = false;

        if ( actuator.RangeEnabledRequired )
          Debug.LogWarning( "[ExcavatorController] Actuator requires range but profile has no finite range: " + actuator.Name, this );
        return;
      }

      var range = constraint.GetController<RangeController>();
      if ( range == null ) {
        Debug.LogWarning( "[ExcavatorController] Constraint has no RangeController for actuator "
          + actuator.Name + ": " + constraint.name, this );
        return;
      }

      range.Range = new RangeReal( actuator.RangeMin, actuator.RangeMax );
      range.Enable = actuator.RangeEnabledRequired;
    }

    // ---------- 对外：驱动 ----------

    /// <summary>
    /// 用归一化命令(-1..1)驱动各关节。这是 RL 动作进入物理世界的唯一入口。
    /// </summary>
    public void SetNormalizedSpeeds( float boomCmd, float stickCmd, float bucketCmd, float swingCmd = 0f )
    {
      float boomInput = Mathf.Clamp( boomCmd, -1f, 1f );
      float stickInput = Mathf.Clamp( stickCmd, -1f, 1f );
      float bucketInput = Mathf.Clamp( bucketCmd, -1f, 1f );
      float swingInput = Mathf.Clamp( swingCmd, -1f, 1f );

      boomInput = ApplyNormalizedCommandDeadzone(
        boomInput,
        m_boomCommandDeadzonePositiveNormalized,
        m_boomCommandDeadzoneNegativeNormalized );
      stickInput = ApplyNormalizedCommandDeadzone(
        stickInput,
        m_stickCommandDeadzonePositiveNormalized,
        m_stickCommandDeadzoneNegativeNormalized );
      bucketInput = ApplyNormalizedCommandDeadzone(
        bucketInput,
        m_bucketCommandDeadzonePositiveNormalized,
        m_bucketCommandDeadzoneNegativeNormalized );
      swingInput = ApplyNormalizedCommandDeadzone(
        swingInput,
        m_swingCommandDeadzonePositiveNormalized,
        m_swingCommandDeadzoneNegativeNormalized );

      if ( !commandInputEnabled || IsHoming )
        return;

      LastBoomCommand = boomInput;
      LastStickCommand = stickInput;
      LastBucketCommand = bucketInput;
      LastSwingCommand = swingInput;

      bool holdBoom = m_zeroSpeedPositionHoldEnabled && IsZeroCommand( boomInput );
      bool holdStick = m_zeroSpeedPositionHoldEnabled && IsZeroCommand( stickInput );
      bool holdBucket = m_zeroSpeedPositionHoldEnabled && IsZeroCommand( bucketInput );
      bool holdSwing = m_zeroSpeedPositionHoldEnabled && IsZeroCommand( swingInput );

      SetConstraintSetPositionHold( boom, ref m_boomZeroSpeedHoldEnabled, holdBoom );
      SetConstraintSetPositionHold( stick, ref m_stickZeroSpeedHoldEnabled, holdStick );
      SetConstraintSetPositionHold( bucket, ref m_bucketZeroSpeedHoldEnabled, holdBucket );
      SetConstraintPositionHold( swing, ref m_swingZeroSpeedHoldEnabled, holdSwing );

      float dt = Time.fixedDeltaTime;
      m_boomSpeed = holdBoom ? 0f : MoveTowardsSpeed( m_boomSpeed, CommandToTargetSpeed( boomInput, boomMaxSpeedPositive, boomMaxSpeedNegative ), boomAcceleration, dt );
      m_bucketSpeed = holdBucket ? 0f : MoveTowardsSpeed( m_bucketSpeed, CommandToTargetSpeed( bucketInput, bucketMaxSpeedPositive, bucketMaxSpeedNegative ), bucketAcceleration, dt );

      float stickSpeed = CommandToTargetSpeed( stickInput, stickMaxSpeedPositive, stickMaxSpeedNegative );
      float swingSpeed = CommandToTargetSpeed( swingInput, swingMaxSpeedPositive, swingMaxSpeedNegative );

      LastBoomTargetSpeed = m_boomSpeed;
      LastStickTargetSpeed = stickSpeed;
      LastBucketTargetSpeed = m_bucketSpeed;
      LastSwingTargetSpeed = swingSpeed;

      if ( boom != null && !holdBoom )
        foreach ( var c in boom )
          DriveConstraint( c, m_boomSpeed );

      if ( !holdStick )
        DriveConstraintSet( stick, stickSpeed );
      if ( !holdBucket )
        DriveConstraintSet( bucket, m_bucketSpeed );
      if ( !holdSwing )
        DriveConstraint( swing,  swingSpeed );
    }

    public float MaxAbsSpeed( string actuatorName )
    {
      if ( actuatorName == "boom" )
        return Mathf.Max( boomMaxSpeedPositive, boomMaxSpeedNegative );
      if ( actuatorName == "stick" )
        return Mathf.Max( stickMaxSpeedPositive, stickMaxSpeedNegative );
      if ( actuatorName == "bucket" )
        return Mathf.Max( bucketMaxSpeedPositive, bucketMaxSpeedNegative );
      if ( actuatorName == "swing" )
        return Mathf.Max( swingMaxSpeedPositive, swingMaxSpeedNegative );

      return 0f;
    }

    public float MaxSpeedForDirection( string actuatorName, float signedCommandOrVelocity )
    {
      if ( actuatorName == "boom" )
        return signedCommandOrVelocity < 0f ? boomMaxSpeedNegative : boomMaxSpeedPositive;
      if ( actuatorName == "stick" )
        return signedCommandOrVelocity < 0f ? stickMaxSpeedNegative : stickMaxSpeedPositive;
      if ( actuatorName == "bucket" )
        return signedCommandOrVelocity < 0f ? bucketMaxSpeedNegative : bucketMaxSpeedPositive;
      if ( actuatorName == "swing" )
        return signedCommandOrVelocity < 0f ? swingMaxSpeedNegative : swingMaxSpeedPositive;

      return 0f;
    }

    private static float CommandToTargetSpeed( float normalizedCommand, float positiveMaxSpeed, float negativeMaxSpeed )
    {
      float selectedMaxSpeed = normalizedCommand < 0f ? negativeMaxSpeed : positiveMaxSpeed;

      return normalizedCommand * Mathf.Max( 0f, selectedMaxSpeed );
    }

    private static float ApplyNormalizedCommandDeadzone(
      float normalizedCommand,
      float positiveDeadzone,
      float negativeDeadzone )
    {
      float deadzone = normalizedCommand < 0f ? negativeDeadzone : positiveDeadzone;
      return Mathf.Abs( normalizedCommand ) <= deadzone ? 0f : normalizedCommand;
    }

    /// <summary>所有关节停下（目标速度=0）。</summary>
    public void StopAll()
    {
      m_boomSpeed = 0f;
      m_bucketSpeed = 0f;
      LastBoomCommand = 0f;
      LastStickCommand = 0f;
      LastBucketCommand = 0f;
      LastSwingCommand = 0f;
      LastBoomTargetSpeed = 0f;
      LastStickTargetSpeed = 0f;
      LastBucketTargetSpeed = 0f;
      LastSwingTargetSpeed = 0f;

      if ( m_zeroSpeedPositionHoldEnabled ) {
        SetConstraintSetPositionHold( boom, ref m_boomZeroSpeedHoldEnabled, true );
        SetConstraintSetPositionHold( stick, ref m_stickZeroSpeedHoldEnabled, true );
        SetConstraintSetPositionHold( bucket, ref m_bucketZeroSpeedHoldEnabled, true );
        SetConstraintPositionHold( swing, ref m_swingZeroSpeedHoldEnabled, true );
        return;
      }

      if ( boom != null )
        foreach ( var c in boom ) DriveConstraint( c, 0f );
      DriveConstraintSet( stick, 0f );
      DriveConstraintSet( bucket, 0f );
      DriveConstraint( swing,  0f );
    }

    /// <summary>固定动作伺服模式下，把零速关节锁在当前位置；RL/手动控制默认关闭。</summary>
    public void SetZeroSpeedPositionHoldEnabled( bool enabled )
    {
      if ( m_zeroSpeedPositionHoldEnabled == enabled )
        return;

      m_zeroSpeedPositionHoldEnabled = enabled;
      if ( !enabled )
        ClearZeroSpeedPositionHolds();
    }

    public void SetCommandInputEnabled( bool enabled )
    {
      commandInputEnabled = enabled;
      if ( !commandInputEnabled )
        StopAll();
    }

    public void EnableCommandInput()
    {
      SetCommandInputEnabled( true );
    }

    public void DisableCommandInput()
    {
      SetCommandInputEnabled( false );
    }

    /// <summary>清理 episode 之间容易残留的速度和控制器状态。</summary>
    public void PrepareForEpisodeReset()
    {
      StopAll();
      ClearBodyVelocities();
      EnsureAllLocksDisabled();
    }

    private static float MoveTowardsSpeed( float current, float target, float acceleration, float deltaTime )
    {
      return Mathf.MoveTowards( current, target, Mathf.Max( acceleration, 0.01f ) * Mathf.Max( deltaTime, 0.001f ) );
    }

    private static void DriveConstraintSet( Constraint[] constraints, float speed )
    {
      if ( constraints == null )
        return;

      foreach ( var c in constraints )
        DriveConstraint( c, speed );
    }

    // 把"目标速度"写进 AGX 的 TargetSpeedController。
    // TargetSpeedController 是一个"马达"：它会持续施加力/力矩，使关节达到设定速度。
    private static void DriveConstraint( Constraint c, float speed )
    {
      if ( c == null )
        return;

      var motor = c.GetController<TargetSpeedController>();
      if ( motor == null )
        return;

      // 驱动时必须确保：马达启用、不要在零速时自锁、解除位置锁(LockController)。
      var locker = c.GetController<LockController>();
      if ( locker != null )
        locker.Enable = false;

      motor.Enable = true;
      motor.LockAtZeroSpeed = false;
      motor.Speed = speed;
    }

    // ---------- 对外：读状态（给 RL 当观测） ----------

    /// <summary>
    /// 读关节当前位置/角度，并用其行程范围(RangeController)归一化到 [-1, 1]。
    /// 这样不同关节的观测量纲一致，RL 网络更好学。
    /// </summary>
    public float GetNormalizedPosition( Constraint c )
    {
      if ( c == null )
        return 0f;

      float current = c.GetCurrentAngle(); // prismatic 返回位移(m)，hinge 返回角度(rad)

      var range = c.GetController<RangeController>();
      if ( range == null )
        return current; // 没有行程限制时退化为原始值（量纲不统一，建议给关节配 Range）

      var r = range.Range;             // RangeReal: Min / Max（默认是 ±Infinity）
      float min = r.Min;
      float max = r.Max;

      // 关键防护：若关节没设有限行程(默认 ±Infinity)，归一化会得到 NaN/Inf，
      // 会直接毁掉 RL 训练。这种情况退化为返回原始位移值。
      if ( !float.IsFinite( min ) || !float.IsFinite( max ) || max - min < 1e-6f )
        return current;

      // 映射 [min,max] -> [-1,1]
      return Mathf.Clamp( ( current - min ) / ( max - min ) * 2f - 1f, -1f, 1f );
    }

    /// <summary>大臂取第一根缸的归一化位置作为代表。</summary>
    public float BoomNormalizedPosition   => GetRepresentativeNormalizedPosition( boom );
    public float StickNormalizedPosition  => GetRepresentativeNormalizedPosition( stick );
    public float BucketNormalizedPosition => GetRepresentativeNormalizedPosition( bucket );
    public float SwingNormalizedPosition  => GetNormalizedPosition( swing );

    /// <summary>执行器原始位移/角度读数。prismatic=m, hinge=rad。</summary>
    public float BoomPosition   => GetRepresentativePosition( boom );
    public float StickPosition  => GetRepresentativePosition( stick );
    public float BucketPosition => GetRepresentativePosition( bucket );
    public float SwingPosition  => swing != null ? swing.GetCurrentAngle() : 0f;

    // ---------- 对外：episode 复位 ----------

    /// <summary>抓取当前姿态作为"初始姿态(home)"。播放后第一帧自动调用一次。</summary>
    public void CaptureHomePose()
    {
      CaptureRootPose();

      if ( boom != null ) {
        m_boomHome = new float[ boom.Length ];
        for ( int i = 0; i < boom.Length; i++ )
          m_boomHome[ i ] = boom[ i ] != null ? boom[ i ].GetCurrentAngle() : 0f;
      }
      m_stickHome = CaptureConstraintHomePoseArray( stick );
      m_bucketHome = CaptureConstraintHomePoseArray( bucket );
      m_swingHome  = swing  != null ? swing.GetCurrentAngle()  : 0f;
      m_homeCaptured = true;
    }

    /// <summary>把当前姿态复制到 Inspector home 字段，方便人工调好姿态后作为固定 reset 目标。</summary>
    public void CaptureCurrentPoseAsInspectorHome()
    {
      CaptureHomePose();

      if ( m_boomHome != null ) {
        boomHomePose = new float[ m_boomHome.Length ];
        for ( int i = 0; i < m_boomHome.Length; i++ )
          boomHomePose[ i ] = m_boomHome[ i ];
      }

      stickHomePose = CopyArray( m_stickHome );
      bucketHomePose = CopyArray( m_bucketHome );
      swingHomePose = m_swingHome;
      useInspectorHomePose = true;
    }

    /// <summary>
    /// 开/关"回家锁"。开启时用 LockController 把每个关节拉回初始位姿。
    /// AGX 不能瞬移复位，所以 episode 重置时的标准做法是：
    ///   先 SetHomeLock(true) 让关节被拉回 home，等几个物理步稳定后 SetHomeLock(false) 再开始这一局。
    /// </summary>
    public void SetHomeLock( bool enable )
    {
      if ( !m_homeCaptured )
        CaptureHomePose();

      if ( boom != null )
        for ( int i = 0; i < boom.Length; i++ )
          LockConstraintAt( boom[ i ], enable, GetBoomHomePose( i ) );

      LockConstraintSetAt( stick, enable, GetStickHomePose );
      LockConstraintSetAt( bucket, enable, GetBucketHomePose );
      LockConstraintAt( swing,  enable, GetSwingHomePose() );
    }

    /// <summary>
    /// 开始一次物理复位：用 LockController 拉回 home，等待 settleSteps 个 FixedUpdate 后自动解锁。
    /// </summary>
    public void BeginHomePoseReset( int settleSteps = -1 )
    {
      if ( m_homeRoutine != null )
        StopCoroutine( m_homeRoutine );

      m_homeRoutine = StartCoroutine( HomePoseResetRoutine( settleSteps ) );
    }

    /// <summary>取消正在进行的 home 复位，用于人工接管时立即解除锁定。</summary>
    public void CancelHomePoseReset()
    {
      if ( m_homeRoutine != null ) {
        StopCoroutine( m_homeRoutine );
        m_homeRoutine = null;
      }

      EnsureAllLocksDisabled();
      StopAll();
      IsHoming = false;
    }

    private System.Collections.IEnumerator HomePoseResetRoutine( int settleSteps )
    {
      IsHoming = true;
      PrepareForEpisodeReset();

      if ( resetRootPoseOnHomeReset )
        RestoreRootPose();

      if ( clearBodyVelocitiesOnHomeReset )
        ClearBodyVelocities();

      SetHomeLock( true );

      int minSteps = Mathf.Max( settleSteps >= 0 ? settleSteps : homeSettleSteps, 1 );
      int maxSteps = Mathf.Max( maxHomeSettleSteps, minSteps );
      int steps = 0;

      while ( steps < minSteps || ( steps < maxSteps && !IsAtHomePose() ) ) {
        steps++;
        yield return new WaitForFixedUpdate();
      }

      if ( warnOnHomeTimeout && !IsAtHomePose() )
        Debug.LogWarning( $"[ExcavatorController] Home reset timeout after {steps} fixed steps. maxError={GetHomePoseMaxError():F4}" );

      SetHomeLock( false );
      StopAll();
      ClearBodyVelocities();
      EnsureAllLocksDisabled();

      IsHoming = false;
      m_homeRoutine = null;
    }

    private void CaptureRootPose()
    {
      if ( m_rootPoseCaptured )
        return;

      m_initialRootPosition = transform.position;
      m_initialRootRotation = transform.rotation;
      m_rootPoseCaptured = true;
    }

    private void RestoreRootPose()
    {
      if ( !m_rootPoseCaptured )
        CaptureRootPose();

      transform.SetPositionAndRotation( m_initialRootPosition, m_initialRootRotation );

      var agxBodies = GetComponentsInChildren<AGXRigidBody>( true );
      foreach ( var body in agxBodies ) {
        if ( body == null )
          continue;

        body.SyncNativeTransform();
      }

      Physics.SyncTransforms();
    }

    private void ClearBodyVelocities()
    {
      if ( !clearBodyVelocitiesOnHomeReset )
        return;

      var unityBodies = GetComponentsInChildren<UnityEngine.Rigidbody>( true );
      foreach ( var body in unityBodies ) {
        if ( body == null )
          continue;

        body.linearVelocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
      }

      var agxBodies = GetComponentsInChildren<AGXRigidBody>( true );
      foreach ( var body in agxBodies ) {
        if ( body == null )
          continue;

        body.LinearVelocity = Vector3.zero;
        body.AngularVelocity = Vector3.zero;
      }
    }

    private void EnsureAllLocksDisabled()
    {
      m_zeroSpeedPositionHoldEnabled = false;
      m_boomZeroSpeedHoldEnabled = false;
      m_stickZeroSpeedHoldEnabled = false;
      m_bucketZeroSpeedHoldEnabled = false;
      m_swingZeroSpeedHoldEnabled = false;

      if ( boom != null )
        foreach ( var c in boom )
          DisableLockController( c );

      DisableLockControllerSet( stick );
      DisableLockControllerSet( bucket );
      DisableLockController( swing );
    }

    private void ClearZeroSpeedPositionHolds()
    {
      SetConstraintSetPositionHold( boom, ref m_boomZeroSpeedHoldEnabled, false );
      SetConstraintSetPositionHold( stick, ref m_stickZeroSpeedHoldEnabled, false );
      SetConstraintSetPositionHold( bucket, ref m_bucketZeroSpeedHoldEnabled, false );
      SetConstraintPositionHold( swing, ref m_swingZeroSpeedHoldEnabled, false );
    }

    private static bool IsZeroCommand( float command )
    {
      return Mathf.Abs( command ) <= 0.0001f;
    }

    private static void SetConstraintSetPositionHold( Constraint[] constraints, ref bool holdEnabled, bool enabled )
    {
      if ( holdEnabled == enabled )
        return;

      holdEnabled = enabled;
      if ( constraints == null )
        return;

      foreach ( var constraint in constraints )
        SetConstraintPositionHold( constraint, enabled );
    }

    private static void SetConstraintPositionHold( Constraint constraint, ref bool holdEnabled, bool enabled )
    {
      if ( holdEnabled == enabled )
        return;

      holdEnabled = enabled;
      SetConstraintPositionHold( constraint, enabled );
    }

    private static void SetConstraintPositionHold( Constraint constraint, bool enabled )
    {
      if ( constraint == null )
        return;

      LockConstraintAt( constraint, enabled, constraint.GetCurrentAngle() );
    }

    private static void DisableLockControllerSet( Constraint[] constraints )
    {
      if ( constraints == null )
        return;

      foreach ( var c in constraints )
        DisableLockController( c );
    }

    private static void DisableLockController( Constraint c )
    {
      if ( c == null )
        return;

      var locker = c.GetController<LockController>();
      if ( locker != null )
        locker.Enable = false;
    }

    private float GetBoomHomePose( int index )
    {
      if ( useInspectorHomePose && boomHomePose != null && index < boomHomePose.Length )
        return boomHomePose[ index ];

      return m_boomHome != null && index < m_boomHome.Length ? m_boomHome[ index ] : 0f;
    }

    private float GetStickHomePose( int index )
    {
      if ( useInspectorHomePose && stickHomePose != null && index < stickHomePose.Length )
        return stickHomePose[ index ];

      return m_stickHome != null && index < m_stickHome.Length ? m_stickHome[ index ] : 0f;
    }

    private float GetBucketHomePose( int index )
    {
      if ( useInspectorHomePose && bucketHomePose != null && index < bucketHomePose.Length )
        return bucketHomePose[ index ];

      return m_bucketHome != null && index < m_bucketHome.Length ? m_bucketHome[ index ] : 0f;
    }

    private float GetSwingHomePose()
    {
      return useInspectorHomePose ? swingHomePose : m_swingHome;
    }

    private bool IsAtHomePose()
    {
      return GetHomePoseMaxError() <= Mathf.Max( homePoseTolerance, 0.0001f );
    }

    private float GetHomePoseMaxError()
    {
      float maxError = 0f;

      if ( boom != null )
        for ( int i = 0; i < boom.Length; i++ )
          maxError = Mathf.Max( maxError, GetConstraintHomeError( boom[ i ], GetBoomHomePose( i ) ) );

      maxError = Mathf.Max( maxError, GetConstraintSetHomeError( stick, GetStickHomePose ) );
      maxError = Mathf.Max( maxError, GetConstraintSetHomeError( bucket, GetBucketHomePose ) );
      maxError = Mathf.Max( maxError, GetConstraintHomeError( swing, GetSwingHomePose() ) );

      return maxError;
    }

    private static float GetConstraintHomeError( Constraint c, float target )
    {
      return c != null ? Mathf.Abs( c.GetCurrentAngle() - target ) : 0f;
    }

    private static float GetConstraintSetHomeError( Constraint[] constraints, System.Func<int, float> targetAt )
    {
      if ( constraints == null )
        return 0f;

      float maxError = 0f;
      for ( int i = 0; i < constraints.Length; i++ )
        maxError = Mathf.Max( maxError, GetConstraintHomeError( constraints[ i ], targetAt( i ) ) );

      return maxError;
    }

    private static void LockConstraintSetAt( Constraint[] constraints, bool enable, System.Func<int, float> targetAt )
    {
      if ( constraints == null )
        return;

      for ( int i = 0; i < constraints.Length; i++ )
        LockConstraintAt( constraints[ i ], enable, targetAt( i ) );
    }

    private static float[] CaptureConstraintHomePoseArray( Constraint[] constraints )
    {
      if ( constraints == null )
        return null;

      var result = new float[ constraints.Length ];
      for ( int i = 0; i < constraints.Length; i++ )
        result[ i ] = constraints[ i ] != null ? constraints[ i ].GetCurrentAngle() : 0f;

      return result;
    }

    private static float[] CopyArray( float[] values )
    {
      if ( values == null )
        return null;

      var result = new float[ values.Length ];
      for ( int i = 0; i < values.Length; i++ )
        result[ i ] = values[ i ];

      return result;
    }

    private float GetRepresentativeNormalizedPosition( Constraint[] constraints )
    {
      var representative = FindRepresentativeConstraint( constraints );
      return GetNormalizedPosition( representative );
    }

    private static float GetRepresentativePosition( Constraint[] constraints )
    {
      var representative = FindRepresentativeConstraint( constraints );
      return representative != null ? representative.GetCurrentAngle() : 0f;
    }

    private static Constraint FindRepresentativeConstraint( Constraint[] constraints )
    {
      if ( constraints == null )
        return null;

      foreach ( var c in constraints )
        if ( c != null )
          return c;

      return null;
    }

    // 用 LockController 把关节锁在指定位置（同时关掉马达，避免两者打架）。
    private static void LockConstraintAt( Constraint c, bool enable, float position )
    {
      if ( c == null )
        return;

      var motor = c.GetController<TargetSpeedController>();
      var locker = c.GetController<LockController>();

      if ( enable ) {
        if ( motor  != null ) { motor.Speed = 0f; motor.Enable = false; }
        if ( locker != null ) { locker.Position = position; locker.Enable = true; }
      }
      else {
        if ( locker != null ) locker.Enable = false;
        if ( motor  != null ) { motor.Enable = true; motor.Speed = 0f; }
      }
    }
  }
}
