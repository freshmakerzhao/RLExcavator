using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;
using AGXUnity.Model;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace RLExcavator
{
  /// <summary>
  /// 挖掘机挖土强化学习智能体（连续控制）。
  ///
  /// 这是整套流程的"大脑接口"，对照你做过的 TurtleAgent 来理解差异：
  ///   - 动作：Turtle 是离散(前进/转向)，这里是 3 维连续(boom/stick/bucket 目标速度)。
  ///   - 奖励：Turtle 是到达目标，这里是"铲斗里挖到的土质量"（密集塑形信号）。
  ///   - 环境：Turtle 是静态地面，这里是 AGX 可变形地形 + 铲斗(shovel)。
  ///
  /// 数据流：
  ///   CollectObservations() 把世界状态喂给网络
  ///        -> 网络输出 3 个连续动作
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

    [Header("成功提示")]
    [Tooltip("达标后短暂变绿的地板/平台 Renderer。建议拖入 Frame 或平台网格的 Renderer")]
    public Renderer successIndicatorRenderer;
    public Color successColor = new Color( 0.1f, 0.85f, 0.25f, 1f );
    [Tooltip("成功后地板保持绿色的秒数")]
    public float successIndicatorSeconds = 1.0f;

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

    [Header("调试")]
    [Tooltip("打印达标/成功日志，用于 Heuristic 验证阶段")]
    public bool debugEpisodeEvents = true;

    // ---- 运行时状态 ----
    private float m_prevMass = 0f;      // 上一步铲斗内土质量，用于算增量
    private bool  m_reachedTarget = false;
    private bool  m_successSignaled = false;
    private float m_episodeStartTipY = 0f;
    private Coroutine m_successIndicatorRoutine = null;
    [HideInInspector] public int CurrentEpisode = 0;

    // 取铲斗内当前土质量(kg)。来自 AGX：terrain.getDynamicMass(shovel) 返回被铲斗"托住"的动态土质量。
    private float MassInBucket()
    {
      if ( terrain == null || shovel == null || terrain.Native == null || shovel.Native == null )
        return 0f;
      return (float)terrain.Native.getDynamicMass( shovel.Native );
    }

    public override void Initialize()
    {
      CurrentEpisode = 0;

      if ( successIndicatorRenderer == null ) {
        var frame = GameObject.Find( "Frame" );
        if ( frame != null )
          successIndicatorRenderer = frame.GetComponentInChildren<Renderer>();
      }
    }

    public override void OnEpisodeBegin()
    {
      CurrentEpisode++;
      m_reachedTarget = false;
      m_successSignaled = false;
      m_episodeStartTipY = bucketTip != null ? bucketTip.position.y : 0f;

      m_prevMass = MassInBucket();

      if ( debugEpisodeEvents )
        Debug.Log( $"[ExcavatorDigAgent] Episode {CurrentEpisode} begin. tipY={m_episodeStartTipY:F2}, mass={m_prevMass:F1}kg" );
    }

    // 观测：全部尽量归一化到 [-1,1]
    public override void CollectObservations( VectorSensor sensor )
    {
      // 3× 关节位置（已在 controller 内归一化）
      sensor.AddObservation( controller != null ? controller.BoomNormalizedPosition   : 0f );
      sensor.AddObservation( controller != null ? controller.StickNormalizedPosition  : 0f );
      sensor.AddObservation( controller != null ? controller.BucketNormalizedPosition : 0f );

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
      // 合计 8 维（先精简，确认有效后再加速度等）
    }

    public override void OnActionReceived( ActionBuffers actions )
    {
      var a = actions.ContinuousActions;
      float boom   = a.Length > 0 ? a[ 0 ] : 0f;
      float stick  = a.Length > 1 ? a[ 1 ] : 0f;
      float bucket = a.Length > 2 ? a[ 2 ] : 0f;

      // 1) 动作 -> 关节
      if ( controller != null )
        controller.SetNormalizedSpeeds( boom, stick, bucket );

      // 2) 奖励：装载量增量（核心）
      float mass = MassInBucket();
      float dMass = mass - m_prevMass;
      m_prevMass = mass;
      AddReward( wMassGain * dMass / Mathf.Max( massNormalizer, 1f ) );

      // 3) 时间惩罚：鼓励尽快完成（沿用 Turtle 思路）
      AddReward( -1f / Mathf.Max( MaxStep, 1 ) );

      // 4) 抖动惩罚：避免乱抖
      AddReward( -wActionCost * ( Mathf.Abs( boom ) + Mathf.Abs( stick ) + Mathf.Abs( bucket ) ) );

      // 5) 是否装够了土
      if ( !m_reachedTarget && mass >= targetMassKg ) {
        m_reachedTarget = true;
        if ( debugEpisodeEvents )
          Debug.Log( $"[ExcavatorDigAgent] Target mass reached: {mass:F1}/{targetMassKg:F1}kg, tipLift={( bucketTip != null ? bucketTip.position.y - m_episodeStartTipY : 0f ):F2}m" );
      }

      // 6) 达标后：抬升奖励 + 成功判定
      if ( m_reachedTarget && bucketTip != null ) {
        float tipLift = bucketTip.position.y - m_episodeStartTipY;
        AddReward( wLift * Mathf.Max( 0f, tipLift ) / Mathf.Max( liftHeight, 0.01f ) / Mathf.Max( MaxStep, 1 ) );

        if ( !m_successSignaled && tipLift >= liftHeight ) {
          m_successSignaled = true;
          if ( debugEpisodeEvents )
            Debug.Log( $"[ExcavatorDigAgent] Success: mass={mass:F1}kg, tipLift={tipLift:F2}/{liftHeight:F2}m." );
          AddReward( 1.0f );        // 成功大奖励
          ShowSuccessIndicator();
        }
      }
    }

    private void ShowSuccessIndicator()
    {
      if ( successIndicatorRenderer == null )
        return;

      if ( m_successIndicatorRoutine != null )
        StopCoroutine( m_successIndicatorRoutine );

      m_successIndicatorRoutine = StartCoroutine( SuccessIndicatorRoutine() );
    }

    private System.Collections.IEnumerator SuccessIndicatorRoutine()
    {
      var block = new MaterialPropertyBlock();
      successIndicatorRenderer.GetPropertyBlock( block );
      block.SetColor( "_BaseColor", successColor );
      block.SetColor( "_Color", successColor );
      successIndicatorRenderer.SetPropertyBlock( block );

      yield return new WaitForSeconds( Mathf.Max( successIndicatorSeconds, 0.01f ) );

      successIndicatorRenderer.SetPropertyBlock( null );
      m_successIndicatorRoutine = null;
    }

    // 手动控制：与键盘测试一致，先用它确认动作/观测/奖励闭环正确再训练。
    public override void Heuristic( in ActionBuffers actionsOut )
    {
      var c = actionsOut.ContinuousActions;
      c[ 0 ] = KeyAxis( KeyCode.K, KeyCode.I );
      c[ 1 ] = KeyAxis( KeyCode.G, KeyCode.T );
      c[ 2 ] = KeyAxis( KeyCode.J, KeyCode.L );
    }

    private static float KeyAxis( KeyCode pos, KeyCode neg )
    {
#if ENABLE_INPUT_SYSTEM
      var kb = Keyboard.current;
      if ( kb == null )
        return 0f;
      float v = 0f;
      if ( kb[ ToKey( pos ) ].isPressed ) v += 1f;
      if ( kb[ ToKey( neg ) ].isPressed ) v -= 1f;
      return v;
#else
      float v = 0f;
      if ( Input.GetKey( pos ) ) v += 1f;
      if ( Input.GetKey( neg ) ) v -= 1f;
      return v;
#endif
    }

#if ENABLE_INPUT_SYSTEM
    private static Key ToKey( KeyCode code )
    {
      switch ( code ) {
        case KeyCode.I: return Key.I;
        case KeyCode.K: return Key.K;
        case KeyCode.T: return Key.T;
        case KeyCode.G: return Key.G;
        case KeyCode.J: return Key.J;
        case KeyCode.L: return Key.L;
        default:        return Key.None;
      }
    }
#endif
  }
}
