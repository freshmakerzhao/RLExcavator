using UnityEngine;
using AGXUnity;

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
  ///   大臂常由两根液压缸驱动 → boom 用数组，可拖 1~2 个。
  /// </summary>
  public class ExcavatorController : MonoBehaviour
  {
    [Header("关节约束")]
    [Tooltip("大臂液压缸，可能有 1~2 根，必须同步驱动")]
    public Constraint[] boom;

    [Tooltip("小臂液压缸")]
    public Constraint stick;

    [Tooltip("铲斗液压缸")]
    public Constraint bucket;
    
    [Tooltip("回转（第一版挖土 demo 可留空，锁住底盘不回转）")]
    public Constraint swing;

    [Header("每个关节的最大目标速度（prismatic=m/s，hinge=rad/s）")]
    [Tooltip("先给保守值跑通，再按手感/训练效果调大")]
    public float boomMaxSpeed   = 0.5f;
    public float stickMaxSpeed  = 0.5f;
    public float bucketMaxSpeed = 0.6f;
    public float swingMaxSpeed  = 0.5f;

    [Header("速度变化限制")]
    [Tooltip("大臂目标速度每秒最大变化量(m/s^2)，越小越柔和")]
    public float boomAcceleration = 0.7f;
    [Tooltip("铲斗目标速度每秒最大变化量(m/s^2)，越小越柔和")]
    public float bucketAcceleration = 0.7f;

    [Header("初始位姿复位")]
    [Tooltip("开启后使用 Inspector 中填写的 home 位姿；关闭则使用 Play 后第一次捕获的位姿")]
    public bool useInspectorHomePose = false;
    [Tooltip("大臂液压缸 home 位移。长度应与 boom 数组一致；两根缸通常填同一组初始读数")]
    public float[] boomHomePose;
    [Tooltip("小臂液压缸 home 位移")]
    public float stickHomePose = 0f;
    [Tooltip("铲斗液压缸 home 位移")]
    public float bucketHomePose = 0f;
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

    [Header("当前关节读数（运行时参考）")]
    public float[] currentBoomPose;
    public float currentStickPose = 0f;
    public float currentBucketPose = 0f;
    public float currentSwingPose = 0f;

    private float m_boomSpeed = 0f;
    private float m_bucketSpeed = 0f;
    private Coroutine m_homeRoutine = null;

    public bool IsHoming { get; private set; } = false;

    // 记录每个关节的初始位置/角度，用于 episode 复位。
    // 注意：AGX 是物理引擎，关节不能"瞬移"复位，只能用 LockController 把它拉回初始位姿。
    private float[] m_boomHome;
    private float   m_stickHome;
    private float   m_bucketHome;
    private float   m_swingHome;
    private bool    m_homeCaptured = false;

    // ---------- 生命周期 ----------

    // 在第一次物理步之后再抓初始姿态（此时 AGX 关节已实例化）。
    // 用 FixedUpdate 里的一次性标记，比 Start 更安全（Start 时 AGX Native 可能还没就绪）。
    private void FixedUpdate()
    {
      if ( !m_homeCaptured )
        CaptureHomePose();

      UpdateCurrentPoseReadout();
    }

    // ---------- 对外：驱动 ----------

    /// <summary>
    /// 用归一化命令(-1..1)驱动各关节。这是 RL 动作进入物理世界的唯一入口。
    /// </summary>
    public void SetNormalizedSpeeds( float boomCmd, float stickCmd, float bucketCmd, float swingCmd = 0f )
    {
      if ( IsHoming )
        return;

      float dt = Time.fixedDeltaTime;
      m_boomSpeed = MoveTowardsSpeed( m_boomSpeed, Mathf.Clamp( boomCmd, -1f, 1f ) * boomMaxSpeed, boomAcceleration, dt );
      m_bucketSpeed = MoveTowardsSpeed( m_bucketSpeed, Mathf.Clamp( bucketCmd, -1f, 1f ) * bucketMaxSpeed, bucketAcceleration, dt );

      float stickSpeed = Mathf.Clamp( stickCmd, -1f, 1f ) * stickMaxSpeed;
      float swingSpeed = Mathf.Clamp( swingCmd, -1f, 1f ) * swingMaxSpeed;

      if ( boom != null )
        foreach ( var c in boom )
          DriveConstraint( c, m_boomSpeed );

      DriveConstraint( stick,  stickSpeed );
      DriveConstraint( bucket, m_bucketSpeed );
      DriveConstraint( swing,  swingSpeed );
    }

    /// <summary>所有关节停下（目标速度=0）。</summary>
    public void StopAll()
    {
      m_boomSpeed = 0f;
      m_bucketSpeed = 0f;

      if ( boom != null )
        foreach ( var c in boom ) DriveConstraint( c, 0f );
      DriveConstraint( stick,  0f );
      DriveConstraint( bucket, 0f );
      DriveConstraint( swing,  0f );
    }

    private static float MoveTowardsSpeed( float current, float target, float acceleration, float deltaTime )
    {
      return Mathf.MoveTowards( current, target, Mathf.Max( acceleration, 0.01f ) * Mathf.Max( deltaTime, 0.001f ) );
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
    public float BoomNormalizedPosition   => ( boom != null && boom.Length > 0 ) ? GetNormalizedPosition( boom[ 0 ] ) : 0f;
    public float StickNormalizedPosition  => GetNormalizedPosition( stick );
    public float BucketNormalizedPosition => GetNormalizedPosition( bucket );
    public float SwingNormalizedPosition  => GetNormalizedPosition( swing );

    // ---------- 对外：episode 复位 ----------

    /// <summary>抓取当前姿态作为"初始姿态(home)"。播放后第一帧自动调用一次。</summary>
    public void CaptureHomePose()
    {
      if ( boom != null ) {
        m_boomHome = new float[ boom.Length ];
        for ( int i = 0; i < boom.Length; i++ )
          m_boomHome[ i ] = boom[ i ] != null ? boom[ i ].GetCurrentAngle() : 0f;
      }
      m_stickHome  = stick  != null ? stick.GetCurrentAngle()  : 0f;
      m_bucketHome = bucket != null ? bucket.GetCurrentAngle() : 0f;
      m_swingHome  = swing  != null ? swing.GetCurrentAngle()  : 0f;
      m_homeCaptured = true;
    }

    private void UpdateCurrentPoseReadout()
    {
      if ( boom != null ) {
        if ( currentBoomPose == null || currentBoomPose.Length != boom.Length )
          currentBoomPose = new float[ boom.Length ];

        for ( int i = 0; i < boom.Length; i++ )
          currentBoomPose[ i ] = boom[ i ] != null ? boom[ i ].GetCurrentAngle() : 0f;
      }

      currentStickPose  = stick  != null ? stick.GetCurrentAngle()  : 0f;
      currentBucketPose = bucket != null ? bucket.GetCurrentAngle() : 0f;
      currentSwingPose  = swing  != null ? swing.GetCurrentAngle()  : 0f;
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

      stickHomePose = m_stickHome;
      bucketHomePose = m_bucketHome;
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

      LockConstraintAt( stick,  enable, GetStickHomePose() );
      LockConstraintAt( bucket, enable, GetBucketHomePose() );
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

    private System.Collections.IEnumerator HomePoseResetRoutine( int settleSteps )
    {
      IsHoming = true;
      m_boomSpeed = 0f;
      m_bucketSpeed = 0f;

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

      IsHoming = false;
      m_homeRoutine = null;
    }

    private float GetBoomHomePose( int index )
    {
      if ( useInspectorHomePose && boomHomePose != null && index < boomHomePose.Length )
        return boomHomePose[ index ];

      return m_boomHome != null && index < m_boomHome.Length ? m_boomHome[ index ] : 0f;
    }

    private float GetStickHomePose()
    {
      return useInspectorHomePose ? stickHomePose : m_stickHome;
    }

    private float GetBucketHomePose()
    {
      return useInspectorHomePose ? bucketHomePose : m_bucketHome;
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

      maxError = Mathf.Max( maxError, GetConstraintHomeError( stick, GetStickHomePose() ) );
      maxError = Mathf.Max( maxError, GetConstraintHomeError( bucket, GetBucketHomePose() ) );
      maxError = Mathf.Max( maxError, GetConstraintHomeError( swing, GetSwingHomePose() ) );

      return maxError;
    }

    private static float GetConstraintHomeError( Constraint c, float target )
    {
      return c != null ? Mathf.Abs( c.GetCurrentAngle() - target ) : 0f;
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
