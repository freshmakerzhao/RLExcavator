using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;

namespace RLExcavator
{
  /// <summary>
  /// Bucket-tip trajectory tracking task.
  /// This is intentionally separate from ExcavatorDigAgent so path-following rewards
  /// can be tuned without changing the excavation task.
  /// </summary>
  public class ExcavatorTrajectoryAgent : Agent
  {
    [Header("References")]
    public ExcavatorController controller;
    public Transform bucketTip;
    public TrajectoryProvider trajectory;
    [Tooltip("可选：复用 R 键/episode 的复位入口。")]
    public global::ResetTerrain episodeResetter;

    [Header("Task")]
    [Tooltip("先只测试控制链路：按键动作直接驱动挖掘机，不计算轨迹奖励/不结束 episode。确认能控制后再关闭。")]
    public bool controlOnlyDebugMode = true;
    [Tooltip("铲斗尖距离当前目标点小于该值时认为到达。")]
    public float targetThreshold = 0.25f;
    [Tooltip("距离归一化参考值，接近环境内最大合理误差即可。")]
    public float distanceNormalizer = 8.0f;
    [Tooltip("局部位置归一化参考值。")]
    public float positionNormalizer = 10.0f;

    [Header("Reward")]
    public float wDistance = 0.01f;
    public float wProgress = 0.2f;
    public float wActionCost = 0.001f;
    public float waypointBonus = 0.2f;
    public float pathCompleteBonus = 1.0f;

    [Header("Episode reset")]
    public bool resetPoseOnEpisodeBegin = true;
    [Tooltip("轨迹跟踪默认只复位挖掘机姿态；需要同步重置地形时再开启。")]
    public bool useEpisodeResetterOnEpisodeBegin = false;
    public int poseResetSettleSteps = -1;

    [Header("Debug")]
    public bool debugEpisodeEvents = true;
    public bool disableKeyboardTesterWhenAgentEnabled = true;
    public bool disableDigAgentWhenEnabled = true;
    [Tooltip("引用为空时在当前 scene 内自动查找，减少 Inspector 手动绑定错误。")]
    public bool autoFindReferences = true;
    [Tooltip("打印前若干次动作，用于确认 Heuristic/trainer 是否真的给了动作。")]
    public bool debugActions = false;
    public int debugActionPrints = 20;
    [Tooltip("调试专用：不依赖 ML-Agents Heuristic/DecisionRequester，直接在 Update 里读键盘驱动 controller。")]
    public bool directKeyboardFallback = true;

    private float m_previousDistance = 0f;
    private bool m_waitingForReset = false;
    private bool m_missingReferenceWarned = false;
    private int m_debugActionPrintCount = 0;
    [HideInInspector] public int CurrentEpisode = 0;
    public float CurrentScore => GetCumulativeReward();
    public float CurrentDistanceToTarget => CurrentDistanceToTargetInternal();

    public override void Initialize()
    {
      if ( autoFindReferences && controller == null )
        controller = FindInScene<ExcavatorController>();

      if ( autoFindReferences && trajectory == null )
        trajectory = FindInScene<TrajectoryProvider>();

      if ( autoFindReferences && bucketTip == null ) {
        var bucketTipObject = FindInSceneByName( "BucketTip" );
        if ( bucketTipObject != null )
          bucketTip = bucketTipObject.transform;
      }

      if ( autoFindReferences && episodeResetter == null )
        episodeResetter = FindInScene<global::ResetTerrain>();

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
      m_waitingForReset = true;

      if ( trajectory != null )
        trajectory.ResetPath();

      if ( controlOnlyDebugMode ) {
        m_waitingForReset = false;
        if ( debugEpisodeEvents )
          Debug.Log( $"[ExcavatorTrajectoryAgent] Control-only episode {CurrentEpisode} begin." );
        return;
      }

      if ( useEpisodeResetterOnEpisodeBegin && episodeResetter != null ) {
        episodeResetter.ResetEpisode( false );
        return;
      }

      if ( resetPoseOnEpisodeBegin && controller != null ) {
        controller.BeginHomePoseReset( poseResetSettleSteps );
        return;
      }

      BeginEpisodeMeasurements();
    }

    private void Update()
    {
      if ( !controlOnlyDebugMode || !directKeyboardFallback || controller == null )
        return;

      var command = ExcavatorKeyboardInput.ReadDefault();
      command.ApplyTo( controller );

      if ( debugActions && m_debugActionPrintCount < debugActionPrints ) {
        if ( command.HasInput ) {
          Debug.Log( $"[ExcavatorTrajectoryAgent] direct keyboard boom={command.boom:F3}, stick={command.stick:F3}, bucket={command.bucket:F3}, swing={command.swing:F3}" );
          m_debugActionPrintCount++;
        }
      }
    }

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
      // Total: 15 observations.
    }

    public override void OnActionReceived( ActionBuffers actions )
    {
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

      if ( controlOnlyDebugMode )
        return;

      if ( bucketTip == null || trajectory == null ) {
        if ( debugEpisodeEvents && !m_missingReferenceWarned ) {
          Debug.LogWarning( "[ExcavatorTrajectoryAgent] Missing bucketTip or trajectory reference. Ending episode." );
          m_missingReferenceWarned = true;
        }
        AddReward( -1f );
        EndEpisode();
        return;
      }

      if ( episodeResetter != null && episodeResetter.IsResetting )
        return;

      if ( controller != null && controller.IsHoming )
        return;

      if ( m_waitingForReset )
        BeginEpisodeMeasurements();

      float distance = CurrentDistanceToTargetInternal();
      float progressDelta = m_previousDistance - distance;
      m_previousDistance = distance;

      AddReward( -wDistance * distance / Mathf.Max( distanceNormalizer, 0.01f ) );
      AddReward( wProgress * progressDelta / Mathf.Max( distanceNormalizer, 0.01f ) );
      AddReward( -wActionCost * ( Mathf.Abs( boom ) + Mathf.Abs( stick ) + Mathf.Abs( bucket ) + Mathf.Abs( swing ) ) );
      AddReward( -1f / Mathf.Max( MaxStep, 1 ) );

      if ( distance <= targetThreshold ) {
        bool completed = trajectory == null || trajectory.AdvanceTarget();
        AddReward( waypointBonus );

        if ( completed ) {
          AddReward( pathCompleteBonus );
          if ( debugEpisodeEvents )
            Debug.Log( $"[ExcavatorTrajectoryAgent] Path complete. episode={CurrentEpisode}, score={GetCumulativeReward():F3}" );
          EndEpisode();
        }
        else {
          m_previousDistance = CurrentDistanceToTargetInternal();
          if ( debugEpisodeEvents )
            Debug.Log( $"[ExcavatorTrajectoryAgent] Waypoint reached. next={trajectory.CurrentIndex}, distance={m_previousDistance:F2}m" );
        }
      }
    }

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

    private void BeginEpisodeMeasurements()
    {
      m_waitingForReset = false;
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
