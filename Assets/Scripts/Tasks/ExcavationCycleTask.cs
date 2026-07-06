using System.Collections;
using Unity.MLAgents;
using Unity.MLAgents.Policies;
using UnityEngine;

namespace RLExcavator
{
  /// <summary>
  /// Demo-level excavation cycle orchestration:
  /// current bucket tip -> dig point -> fixed dig action -> dump point -> fixed dump action.
  /// It reuses planner, trajectory agent and controller; it does not own UI.
  /// </summary>
  public class ExcavationCycleTask : MonoBehaviour
  {
    public enum CycleState
    {
      Idle,
      PlanningToDig,
      TrackingToDig,
      Digging,
      PlanningToDump,
      TrackingToDump,
      Dumping,
      ReturningToInit,
      Completed,
      Failed
    }

    [System.Serializable]
    public struct NormalizedJointDelta
    {
      [Tooltip("动臂归一化位置增量。")]
      public float boom;

      [Tooltip("斗杆归一化位置增量。")]
      public float stick;

      [Tooltip("铲斗归一化位置增量。")]
      public float bucket;

      [Tooltip("回转归一化位置增量。")]
      public float swing;

      public NormalizedJointDelta( float boom, float stick, float bucket, float swing )
      {
        this.boom = boom;
        this.stick = stick;
        this.bucket = bucket;
        this.swing = swing;
      }

      public Vector4 ToVector4()
      {
        return new Vector4( boom, stick, bucket, swing );
      }
    }

    [System.Serializable]
    public class JointServoTarget
    {
      [Tooltip("动作段名称。")]
      public string label = "目标姿态";

      [Tooltip("相对当前姿态的归一化关节增量，X=大臂，Y=小臂，Z=铲斗，W=回转。")]
      [UnityEngine.Serialization.FormerlySerializedAs( "targetNormalizedQpos" )]
      public NormalizedJointDelta deltaNormalizedQpos;

      public JointServoTarget()
      {
      }

      public JointServoTarget( string label, NormalizedJointDelta deltaNormalizedQpos )
      {
        this.label = label;
        this.deltaNormalizedQpos = deltaNormalizedQpos;
      }
    }

    [Header("References")]
    public ExcavatorTrajectoryAgent agent;
    public ExcavatorController controller;
    public SimpleTrajectoryPlanner planner;
    public TrajectoryProvider trajectory;
    public DecisionRequester decisionRequester;

    [Header("Fixed actions")]
    [Tooltip("挖掘动作序列。每段目标=该段开始时的当前姿态+归一化增量。")]
    public JointServoTarget[] digTargets = {
      new JointServoTarget( "下探", new NormalizedJointDelta( 0.50f, 0.00f, 0.00f, 0.00f ) ),
      new JointServoTarget( "收斗", new NormalizedJointDelta( 0.00f, 0.00f, -1.40f, 0.00f ) ),
      new JointServoTarget( "提斗", new NormalizedJointDelta( -0.50f, 0.20f, 0.00f, 0.00f ) )
    };

    [Tooltip("倾倒动作序列。每段目标=该段开始时的当前姿态+归一化增量。")]
    public JointServoTarget[] dumpTargets = {
      new JointServoTarget( "打开铲斗", new NormalizedJointDelta( 0.00f, 0.00f, 1.40f, 0.00f ) ),
      new JointServoTarget( "回收铲斗", new NormalizedJointDelta( 0.00f, 0.00f, -1.40f, 0.00f ) )
    };

    [Tooltip("位置误差到动作速度的比例增益。")]
    [Min( 0.01f )]
    public float fixedActionKp = 1.5f;

    [Tooltip("方向明确但动作过小时补的最小动作量。")]
    [Range( 0f, 1f )]
    public float fixedActionMinAction = 0.08f;

    [Tooltip("伺服动作输出上限。")]
    [Range( 0.01f, 1f )]
    public float fixedActionMaxAction = 1f;

    [Tooltip("每段目标姿态的归一化位置误差阈值。")]
    [Range( 0.001f, 0.2f )]
    public float fixedActionPositionTolerance = 0.03f;

    [Tooltip("每段目标姿态最多执行时间，超时后进入下一段。")]
    [Min( 0.1f )]
    public float fixedActionStepTimeoutSeconds = 3f;

    [Tooltip("每段目标姿态到达或超时后的停顿时间。")]
    [Min( 0f )]
    public float fixedActionHoldSeconds = 0.15f;

    [Tooltip("固定动作期间，目标速度为 0 的关节自动锁在当前位置。RL 追踪和手动控制不使用该模式。")]
    public bool lockZeroSpeedJointsDuringFixedAction = true;

    [Header("Tracking")]
    [Tooltip("每段轨迹追踪的最长等待时间。超时会停止当前任务。")]
    [Min( 1f )]
    public float trackingTimeoutSeconds = 20f;

    [Tooltip("开启后必须有 InferenceOnly 模型才允许任务追踪。")]
    public bool requireInferenceOnlyBehavior = true;

    [Tooltip("停止追踪时禁用 DecisionRequester。")]
    public bool disableDecisionRequesterWhenStopped = true;

    [Tooltip("停止追踪时禁用 Agent。")]
    public bool disableAgentWhenStopped = true;

    [Tooltip("完整任务执行期间是否显示 waypoint 轨迹点。关闭只隐藏可视化，不影响规划和跟踪。")]
    public bool showTrajectoryPathDuringExecution = false;

    [Header("Return to init joint pose")]
    [Tooltip("完整任务倾倒结束后，用关节位置伺服回到初始关节姿态。")]
    public bool returnToInitJointPoseAfterCycle = false;

    [Tooltip("初始关节姿态，归一化顺序为 boom、stick、bucket、swing。")]
    public NormalizedJointDelta initNormalizedJointPose;

    [Tooltip("回到初始关节姿态的最长时间。")]
    [Min( 0.1f )]
    public float returnToInitJointPoseTimeoutSeconds = 8f;

    [Header("Runtime")]
    public CycleState state = CycleState.Idle;

    public string AutoTaskStatus { get; private set; } = "空闲";
    public string FixedActionStatus { get; private set; } = "空闲";
    public bool LastRunSucceeded { get; private set; } = false;
    public bool IsCycleRunning => m_cycleRoutine != null;
    public bool IsFixedActionRunning => m_fixedActionRoutine != null;

    public event System.Action CycleStarted;
    public event System.Action<bool, string> CycleFinished;
    public event System.Action FixedActionCompleted;

    private Coroutine m_cycleRoutine;
    private Coroutine m_fixedActionRoutine;
    private bool m_previousResetPoseOnEpisodeBegin = true;
    private bool m_hasResetPoseOverride = false;

    [ContextMenu("执行完整挖掘任务")]
    public void ExecuteDigTransportDump()
    {
      if ( m_cycleRoutine != null ) {
        Debug.LogWarning( "[ExcavationCycleTask] Task is already running." );
        return;
      }

      state = CycleState.PlanningToDig;
      LastRunSucceeded = false;
      AutoTaskStatus = "准备";
      CycleStarted?.Invoke();
      m_cycleRoutine = StartCoroutine( RunCycle() );
    }

    [ContextMenu("停止任务")]
    public void StopTask()
    {
      StopTask( true );
    }

    public void StopTask( bool stopMotion )
    {
      bool wasRunning = m_cycleRoutine != null;
      if ( m_cycleRoutine != null ) {
        StopCoroutine( m_cycleRoutine );
        m_cycleRoutine = null;
      }

      AutoTaskStatus = "空闲";
      if ( stopMotion ) {
        StopFixedAction( false );
        StopTrackingControls();
        if ( controller != null ) {
          controller.SetCommandInputEnabled( false );
          controller.StopAll();
        }
      }

      RestoreRuntimeOverrides();
      state = CycleState.Idle;
      if ( wasRunning )
        CycleFinished?.Invoke( false, "stopped" );
    }

    [ContextMenu("挖掘测试")]
    public void StartDigTest()
    {
      StartFixedAction( "挖掘测试", digTargets, true );
    }

    [ContextMenu("倾倒测试")]
    public void StartDumpTest()
    {
      StartFixedAction( "倾倒测试", dumpTargets, true );
    }

    public void StartFixedAction( string actionLabel, JointServoTarget[] targets, bool notifyWhenCompleted )
    {
      if ( controller == null ) {
        Debug.LogWarning( "[ExcavationCycleTask] Missing controller reference." );
        return;
      }

      if ( targets == null || targets.Length == 0 ) {
        Debug.LogWarning( $"[ExcavationCycleTask] {actionLabel} target sequence is empty." );
        return;
      }

      StopFixedAction( false );
      StopTrackingControls();
      controller.SetCommandInputEnabled( true );
      m_fixedActionRoutine = StartCoroutine( RunJointServoSequence( actionLabel, targets, notifyWhenCompleted ) );
    }

    public void StopFixedAction( bool notifyWhenCompleted )
    {
      if ( m_fixedActionRoutine != null ) {
        StopCoroutine( m_fixedActionRoutine );
        m_fixedActionRoutine = null;
      }

      FixedActionStatus = "空闲";
      if ( controller != null ) {
        controller.SetZeroSpeedPositionHoldEnabled( false );
        controller.StopAll();
      }

      if ( notifyWhenCompleted )
        FixedActionCompleted?.Invoke();
    }

    public void StopTrackingControls()
    {
      if ( controller != null )
        controller.SetCommandInputEnabled( false );

      if ( disableDecisionRequesterWhenStopped )
        SetDecisionRequesterEnabled( false );

      if ( disableAgentWhenStopped )
        SetAgentEnabled( false );
    }

    private IEnumerator RunCycle()
    {
      if ( !ValidateReferences() ) {
        FinishTask( false, "引用缺失" );
        yield break;
      }

      Vector3 digPoint = planner.startPoint.position;
      Vector3 dumpPoint = planner.endPoint.position;

      BeginRuntimeOverrides();
      StopFixedAction( false );

      if ( !PlanRuntimeSegmentFromBucketTip( ExcavatorTrajectoryAgent.TrajectoryTaskMode.MoveToDig, digPoint, "赴挖掘点" ) ) {
        FinishTask( false, "赴挖掘点规划失败" );
        yield break;
      }

      int moveToDigCompletionSerial = agent.CompletedPathSerial;
      if ( !StartTracking() ) {
        FinishTask( false, "赴挖掘点追踪启动失败" );
        yield break;
      }

      state = CycleState.TrackingToDig;
      yield return WaitForTrackedPathComplete( "赴挖掘点", moveToDigCompletionSerial );
      if ( !IsCycleRunning )
        yield break;

      StopTrackingControls();
      if ( !HasTrackedPathCompletedSince( moveToDigCompletionSerial ) ) {
        FinishTask( false, "赴挖掘点追踪超时" );
        yield break;
      }

      state = CycleState.Digging;
      yield return RunAutoFixedAction( "挖掘动作", digTargets );
      if ( !IsCycleRunning )
        yield break;

      if ( !PlanRuntimeSegmentFromBucketTip( ExcavatorTrajectoryAgent.TrajectoryTaskMode.CarryMaterial, dumpPoint, "前往倾倒点" ) ) {
        FinishTask( false, "倾倒点规划失败" );
        yield break;
      }

      int carryMaterialCompletionSerial = agent.CompletedPathSerial;
      if ( !StartTracking() ) {
        FinishTask( false, "倾倒点追踪启动失败" );
        yield break;
      }

      state = CycleState.TrackingToDump;
      yield return WaitForTrackedPathComplete( "前往倾倒点", carryMaterialCompletionSerial );
      if ( !IsCycleRunning )
        yield break;

      StopTrackingControls();
      if ( !HasTrackedPathCompletedSince( carryMaterialCompletionSerial ) ) {
        FinishTask( false, "倾倒点追踪超时" );
        yield break;
      }

      state = CycleState.Dumping;
      yield return RunAutoFixedAction( "倾倒动作", dumpTargets );
      if ( !IsCycleRunning )
        yield break;

      if ( returnToInitJointPoseAfterCycle ) {
        state = CycleState.ReturningToInit;
        yield return RunAutoJointServoToNormalizedPose(
          "回初始姿态",
          initNormalizedJointPose.ToVector4(),
          returnToInitJointPoseTimeoutSeconds );
        if ( !IsCycleRunning )
          yield break;
      }

      FinishTask( true, "完成" );
    }

    private bool ValidateReferences()
    {
      if ( planner == null ) {
        Debug.LogWarning( "[ExcavationCycleTask] Missing planner reference." );
        return false;
      }

      if ( planner.startPoint == null || planner.endPoint == null ) {
        Debug.LogWarning( "[ExcavationCycleTask] Missing dig or dump point reference." );
        return false;
      }

      if ( ActiveBucketTip() == null ) {
        Debug.LogWarning( "[ExcavationCycleTask] Missing bucket tip reference." );
        return false;
      }

      if ( agent == null ) {
        Debug.LogWarning( "[ExcavationCycleTask] Missing trajectory agent reference." );
        return false;
      }

      if ( trajectory == null ) {
        Debug.LogWarning( "[ExcavationCycleTask] Missing trajectory reference." );
        return false;
      }

      if ( controller == null ) {
        Debug.LogWarning( "[ExcavationCycleTask] Missing controller reference." );
        return false;
      }

      if ( digTargets == null || digTargets.Length == 0 ) {
        Debug.LogWarning( "[ExcavationCycleTask] Dig action sequence is empty." );
        return false;
      }

      if ( dumpTargets == null || dumpTargets.Length == 0 ) {
        Debug.LogWarning( "[ExcavationCycleTask] Dump action sequence is empty." );
        return false;
      }

      return true;
    }

    private bool PlanRuntimeSegmentFromBucketTip(
      ExcavatorTrajectoryAgent.TrajectoryTaskMode mode,
      Vector3 targetWorld,
      string phaseLabel )
    {
      Transform bucketTip = ActiveBucketTip();
      if ( bucketTip == null || planner == null )
        return false;

      SetTaskMode( mode );
      AutoTaskStatus = $"规划：{phaseLabel}";
      state = mode == ExcavatorTrajectoryAgent.TrajectoryTaskMode.MoveToDig ? CycleState.PlanningToDig : CycleState.PlanningToDump;
      planner.PlanRuntimePath( bucketTip.position, targetWorld );
      if ( trajectory != null )
        trajectory.SetPathVisualsVisible( showTrajectoryPathDuringExecution );
      return true;
    }

    private bool StartTracking()
    {
      if ( agent == null )
        return false;

      if ( !CanUseInferenceControlMode() )
        return false;

      SetInferenceControlMode();

      if ( !CanRunDemoControl() )
        return false;

      if ( controller == null )
        return false;

      SetAgentEnabled( true );
      controller.SetCommandInputEnabled( true );
      SetDecisionRequesterEnabled( true );
      return true;
    }

    private IEnumerator WaitForTrackedPathComplete( string phaseLabel, int completionSerialBeforeSegment )
    {
      float startedAt = Time.time;
      float timeout = Mathf.Max( trackingTimeoutSeconds, 1f );

      while ( !HasTrackedPathCompletedSince( completionSerialBeforeSegment ) ) {
        AutoTaskStatus = $"{phaseLabel}：追踪中 {Time.time - startedAt:0.0}s";

        if ( Time.time - startedAt >= timeout )
          yield break;

        yield return null;
      }
    }

    private bool HasTrackedPathCompletedSince( int completionSerialBeforeSegment )
    {
      if ( agent != null && agent.CompletedPathSerial > completionSerialBeforeSegment )
        return true;

      return trajectory != null && trajectory.PathCompleted;
    }

    private IEnumerator RunAutoFixedAction( string actionLabel, JointServoTarget[] targets )
    {
      if ( controller == null )
        yield break;

      SetDecisionRequesterEnabled( false );
      SetAgentEnabled( false );
      controller.SetCommandInputEnabled( true );
      AutoTaskStatus = actionLabel;
      m_fixedActionRoutine = StartCoroutine( RunJointServoSequence( actionLabel, targets, false ) );
      yield return m_fixedActionRoutine;
      if ( controller != null )
        controller.SetCommandInputEnabled( false );
    }

    public void StartReturnToNormalizedJointPose( string actionLabel, Vector4 targetNormalizedQpos, bool notifyWhenCompleted )
    {
      if ( controller == null ) {
        Debug.LogWarning( "[ExcavationCycleTask] Missing controller reference." );
        return;
      }

      StopFixedAction( false );
      StopTrackingControls();
      controller.SetCommandInputEnabled( true );
      m_fixedActionRoutine = StartCoroutine(
        RunJointServoToNormalizedPose(
          string.IsNullOrEmpty( actionLabel ) ? "回初始姿态" : actionLabel,
          targetNormalizedQpos,
          returnToInitJointPoseTimeoutSeconds,
          notifyWhenCompleted ) );
    }

    private IEnumerator RunAutoJointServoToNormalizedPose( string actionLabel, Vector4 targetNormalizedQpos, float timeoutSeconds )
    {
      if ( controller == null )
        yield break;

      SetDecisionRequesterEnabled( false );
      SetAgentEnabled( false );
      controller.SetCommandInputEnabled( true );
      AutoTaskStatus = actionLabel;
      m_fixedActionRoutine = StartCoroutine( RunJointServoToNormalizedPose( actionLabel, targetNormalizedQpos, timeoutSeconds, false ) );
      yield return m_fixedActionRoutine;
      if ( controller != null )
        controller.SetCommandInputEnabled( false );
    }

    private IEnumerator RunJointServoToNormalizedPose( string actionLabel, Vector4 targetNormalizedQpos, float timeoutSeconds, bool notifyWhenCompleted )
    {
      Vector4 targetQpos = ClampNormalizedJointPose( targetNormalizedQpos );
      float startedAt = Time.time;
      float timeout = Mathf.Max( timeoutSeconds, 0.1f );

      while ( Time.time - startedAt < timeout ) {
        if ( controller == null )
          yield break;

        Vector4 error = targetQpos - CurrentNormalizedJointPose();
        float maxError = MaxAbs( error );
        FixedActionStatus = $"{actionLabel}：误差 {maxError:0.000}";

        if ( maxError <= fixedActionPositionTolerance )
          break;

        Vector4 action = ServoActionFromError( error );
        controller.SetNormalizedSpeeds( action.x, action.y, action.z, action.w );
        yield return new WaitForFixedUpdate();
      }

      if ( controller != null )
        controller.StopAll();

      FixedActionStatus = $"{actionLabel}完成";
      m_fixedActionRoutine = null;
      if ( notifyWhenCompleted )
        FixedActionCompleted?.Invoke();
    }

    private IEnumerator RunJointServoSequence( string actionLabel, JointServoTarget[] targets, bool notifyWhenCompleted )
    {
      if ( lockZeroSpeedJointsDuringFixedAction && controller != null )
        controller.SetZeroSpeedPositionHoldEnabled( true );

      try {
        for ( int i = 0; i < targets.Length; i++ ) {
          JointServoTarget target = targets[ i ];
          Vector4 startQpos = CurrentNormalizedJointPose();
          Vector4 deltaQpos = target.deltaNormalizedQpos.ToVector4();
          Vector4 targetQpos = ClampNormalizedJointPose( startQpos + deltaQpos );
          float startedAt = Time.time;
          string label = string.IsNullOrEmpty( target.label ) ? $"第 {i + 1} 段" : target.label;

          while ( Time.time - startedAt < fixedActionStepTimeoutSeconds ) {
            if ( controller == null )
              yield break;

            Vector4 error = targetQpos - CurrentNormalizedJointPose();
            if ( lockZeroSpeedJointsDuringFixedAction )
              ClearUnusedAxisErrors( ref error, deltaQpos );
            float maxError = MaxAbs( error );
            FixedActionStatus = $"{actionLabel}：{label} 误差 {maxError:0.000}";

            if ( maxError <= fixedActionPositionTolerance )
              break;

            Vector4 action = ServoActionFromError( error );
            controller.SetNormalizedSpeeds( action.x, action.y, action.z, action.w );
            yield return new WaitForFixedUpdate();
          }

          if ( controller != null )
            controller.StopAll();

          if ( fixedActionHoldSeconds > 0f )
            yield return new WaitForSeconds( fixedActionHoldSeconds );
        }
      }
      finally {
        if ( controller != null )
          controller.SetZeroSpeedPositionHoldEnabled( false );
      }

      if ( controller != null )
        controller.StopAll();

      FixedActionStatus = $"{actionLabel}完成";
      m_fixedActionRoutine = null;
      if ( notifyWhenCompleted )
        FixedActionCompleted?.Invoke();
    }

    private void FinishTask( bool success, string status )
    {
      StopFixedAction( false );
      StopTrackingControls();
      if ( controller != null ) {
        controller.SetCommandInputEnabled( false );
        controller.StopAll();
      }

      AutoTaskStatus = success ? "完成" : $"失败：{status}";
      LastRunSucceeded = success;
      m_cycleRoutine = null;
      state = success ? CycleState.Completed : CycleState.Failed;
      RestoreRuntimeOverrides();
      CycleFinished?.Invoke( success, status );
    }

    private void BeginRuntimeOverrides()
    {
      if ( agent == null || m_hasResetPoseOverride )
        return;

      m_previousResetPoseOnEpisodeBegin = agent.resetPoseOnEpisodeBegin;
      agent.resetPoseOnEpisodeBegin = false;
      m_hasResetPoseOverride = true;
    }

    private void RestoreRuntimeOverrides()
    {
      if ( agent == null || !m_hasResetPoseOverride )
        return;

      agent.resetPoseOnEpisodeBegin = m_previousResetPoseOnEpisodeBegin;
      m_hasResetPoseOverride = false;
    }

    private void SetTaskMode( ExcavatorTrajectoryAgent.TrajectoryTaskMode mode )
    {
      if ( agent != null ) {
        agent.taskMode = mode;
        agent.randomizeTaskModeOnEpisodeBegin = false;
      }

      if ( planner != null )
        planner.SetActiveReachableWorkspaceForMode( mode );
    }

    private void SetInferenceControlMode()
    {
      SetBehaviorType( BehaviorType.InferenceOnly );
    }

    private bool CanRunDemoControl()
    {
      if ( !requireInferenceOnlyBehavior )
        return true;

      if ( agent == null )
        return false;

      var behavior = agent.GetComponent<BehaviorParameters>();
      if ( behavior == null )
        return true;

      return behavior.BehaviorType == BehaviorType.InferenceOnly;
    }

    private bool CanUseInferenceControlMode()
    {
      if ( agent == null )
        return false;

      var behavior = agent.GetComponent<BehaviorParameters>();
      if ( behavior == null || behavior.Model != null )
        return true;

      Debug.LogWarning( "[ExcavationCycleTask] Cannot start inference tracking without an assigned model." );
      return false;
    }

    private void SetBehaviorType( BehaviorType behaviorType )
    {
      if ( agent == null )
        return;

      var behavior = agent.GetComponent<BehaviorParameters>();
      if ( behavior != null )
        behavior.BehaviorType = behaviorType;
    }

    private void SetDecisionRequesterEnabled( bool enabled )
    {
      if ( decisionRequester != null )
        decisionRequester.enabled = enabled;
    }

    private void SetAgentEnabled( bool enabled )
    {
      if ( agent != null )
        agent.enabled = enabled;
    }

    private Transform ActiveBucketTip()
    {
      return agent != null ? agent.bucketTip : null;
    }

    private Vector4 CurrentNormalizedJointPose()
    {
      if ( controller == null )
        return Vector4.zero;

      return new Vector4(
        controller.BoomNormalizedPosition,
        controller.StickNormalizedPosition,
        controller.BucketNormalizedPosition,
        controller.SwingNormalizedPosition );
    }

    private Vector4 ServoActionFromError( Vector4 error )
    {
      return new Vector4(
        ServoAxisAction( error.x ),
        ServoAxisAction( error.y ),
        ServoAxisAction( error.z ),
        ServoAxisAction( error.w ) );
    }

    private float ServoAxisAction( float error )
    {
      if ( Mathf.Abs( error ) <= fixedActionPositionTolerance )
        return 0f;

      float maxAction = Mathf.Clamp01( fixedActionMaxAction );
      float minAction = Mathf.Clamp( fixedActionMinAction, 0f, maxAction );
      float rawAction = Mathf.Max( fixedActionKp, 0.01f ) * error;
      float magnitude = Mathf.Clamp( Mathf.Abs( rawAction ), minAction, maxAction );
      return Mathf.Sign( error ) * magnitude;
    }

    private static void ClearUnusedAxisErrors( ref Vector4 error, Vector4 deltaQpos )
    {
      if ( Mathf.Abs( deltaQpos.x ) <= 0.0001f )
        error.x = 0f;
      if ( Mathf.Abs( deltaQpos.y ) <= 0.0001f )
        error.y = 0f;
      if ( Mathf.Abs( deltaQpos.z ) <= 0.0001f )
        error.z = 0f;
      if ( Mathf.Abs( deltaQpos.w ) <= 0.0001f )
        error.w = 0f;
    }

    private static Vector4 ClampNormalizedJointPose( Vector4 target )
    {
      return new Vector4(
        Mathf.Clamp( target.x, -1f, 1f ),
        Mathf.Clamp( target.y, -1f, 1f ),
        Mathf.Clamp( target.z, -1f, 1f ),
        Mathf.Clamp( target.w, -1f, 1f ) );
    }

    private static float MaxAbs( Vector4 value )
    {
      return Mathf.Max(
        Mathf.Abs( value.x ),
        Mathf.Abs( value.y ),
        Mathf.Abs( value.z ),
        Mathf.Abs( value.w ) );
    }
  }
}
