using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace RLExcavator
{
  /// <summary>
  /// Records the velocity references sent during a demo excavation cycle.
  /// The exported CSV is intended for real-machine open-loop link tests.
  /// </summary>
  public class OpenLoopVelocityRecorder : MonoBehaviour
  {
    private const string kAuditPathEnvironmentVariable = "RL_EXCAVATOR_SIM_CONTROL_AUDIT_JSONL";
    private const string kSuitePathEnvironmentVariable = "RL_EXCAVATOR_TRAJECTORY_SUITE_JSON";

    [Header("References")]
    public ExcavationCycleTask cycleTask;
    public ExcavatorController controller;
    public ExcavatorTrajectoryAgent agent;
    public ExcavatorFrameRegistry frameRegistry;

    [Header("Recording")]
    [Tooltip("任务执行开始/结束时自动开始和导出记录。")]
    public bool recordOnCycleTask = true;

    [Tooltip("CSV 导出目录。相对路径位于 Unity 项目根目录下；绝对路径直接使用。")]
    public string outputDirectoryName = "open_loop_velocity_exports";

    [Tooltip("导出的 CSV 顶部写入 profile/action_order/正反向速度等元信息注释。")]
    public bool includeMetadataComments = true;

    [Tooltip("每次开始记录时清空上一轮样本。")]
    public bool clearSamplesOnStart = true;

    [Header("Strict RL sim control audit")]
    [Tooltip("开启后，在现有 CSV 之外追加记录策略决策边界 JSONL，用于 RL sim-real 对照实验。")]
    public bool enableStrictRlSimControlAudit = false;

    [Tooltip("冻结 trajectory suite JSON；也可由环境变量 RL_EXCAVATOR_TRAJECTORY_SUITE_JSON 指定。")]
    public string trajectorySuiteJsonPath = "";

    [Tooltip("严格控制 audit JSONL；也可由环境变量 RL_EXCAVATOR_SIM_CONTROL_AUDIT_JSONL 指定。")]
    public string controlAuditJsonlPath = "";

    [Header("Runtime")]
    [SerializeField]
    private bool m_isRecording = false;

    [SerializeField]
    private string m_lastExportPath = "";

    private readonly List<Sample> m_samples = new List<Sample>( 4096 );
    private double m_recordStartTime = 0.0;
    private int m_sampleIndex = 0;
    private ExcavatorTrajectoryAgent m_policyActionSource;
    private ExcavationCycleTask m_cycleTaskSource;
    private bool m_trackingEventsSubscribed = false;
    private bool m_cycleEventsSubscribed = false;
    private StrictRlSimControlAudit m_strictRlSimControlAudit;

    public bool IsRecording => m_isRecording;
    public string LastExportPath => m_lastExportPath;
    public int SampleCount => m_samples.Count;

    private struct Sample
    {
      public int index;
      public double timestamp;
      public float unityTime;
      public string phase;
      public string mode;
      public float boomPolicyActionRaw;
      public float stickPolicyActionRaw;
      public float bucketPolicyActionRaw;
      public float swingPolicyActionRaw;
      public float boomAction;
      public float stickAction;
      public float bucketAction;
      public float swingAction;
      public float boomVelocity;
      public float stickVelocity;
      public float bucketVelocity;
      public float swingVelocity;
      public float boomPosition;
      public float stickPosition;
      public float bucketPosition;
      public float swingPosition;
      public float boomNormalizedPosition;
      public float stickNormalizedPosition;
      public float bucketNormalizedPosition;
      public float swingNormalizedPosition;
      public Vector3 bucketTipWorld;
      public Vector3 bucketTipLocal;
    }

    private void Awake()
    {
      ResolveStrictRlSimControlAuditConfiguration();
      ResolveReferences();
      RefreshPolicyActionSubscription();
      RefreshCycleTaskSubscription();
    }

    private void OnEnable()
    {
      ResolveReferences();
      RefreshPolicyActionSubscription();
      RefreshCycleTaskSubscription();
    }

    private void OnDisable()
    {
      m_strictRlSimControlAudit?.Flush();
      DetachPolicyActionSubscription();
      DetachCycleTaskSubscription();
    }

    private void LateUpdate()
    {
      m_strictRlSimControlAudit?.Flush();
    }

    private void FixedUpdate()
    {
      if ( !m_isRecording )
        return;

      ResolveReferences();
      RefreshPolicyActionSubscription();
      RefreshCycleTaskSubscription();
      if ( controller == null ) {
        m_isRecording = false;
        Debug.LogWarning( "[OpenLoopVelocityRecorder] Recording stopped because the controller reference is missing.", this );
        return;
      }

      RecordSample();
    }

    [ContextMenu("开始记录")]
    public void StartRecording()
    {
      ResolveReferences();
      RefreshPolicyActionSubscription();
      RefreshCycleTaskSubscription();

      if ( controller == null ) {
        Debug.LogWarning( "[OpenLoopVelocityRecorder] Missing controller reference.", this );
        return;
      }

      if ( clearSamplesOnStart ) {
        m_samples.Clear();
        m_sampleIndex = 0;
      }

      m_recordStartTime = Time.timeAsDouble;
      m_isRecording = true;
      RecordSample();
      Debug.Log( "[OpenLoopVelocityRecorder] Recording started.", this );
    }

    [ContextMenu("停止并导出")]
    public void StopAndExport()
    {
      StopAndExport( true, "manual" );
    }

    public void StopAndExport( bool success, string reason )
    {
      if ( !m_isRecording && m_samples.Count == 0 )
        return;

      if ( m_isRecording && controller != null )
        RecordSample();

      m_isRecording = false;
      ExportCsv( success, reason );
    }

    private void ResolveReferences()
    {
      if ( cycleTask == null )
        cycleTask = GetComponent<ExcavationCycleTask>();
      if ( cycleTask == null )
        cycleTask = GetComponentInChildren<ExcavationCycleTask>( true );
      if ( controller == null && cycleTask != null )
        controller = cycleTask.controller;
      if ( agent == null && cycleTask != null )
        agent = cycleTask.agent;
      if ( controller == null )
        controller = GetComponentInChildren<ExcavatorController>( true );
      if ( agent == null )
        agent = GetComponentInChildren<ExcavatorTrajectoryAgent>( true );
      if ( frameRegistry == null && agent != null )
        frameRegistry = agent.frameRegistry;
      if ( frameRegistry == null && controller != null )
        frameRegistry = controller.GetComponent<ExcavatorFrameRegistry>();
    }

    private void RefreshPolicyActionSubscription()
    {
      ExcavatorTrajectoryAgent requestedSource = m_strictRlSimControlAudit != null ? agent : null;
      if ( m_policyActionSource == requestedSource )
        return;

      DetachPolicyActionSubscription();
      m_policyActionSource = requestedSource;
      if ( m_policyActionSource != null )
        m_policyActionSource.PolicyActionReceived += OnPolicyActionReceived;
    }

    private void DetachPolicyActionSubscription()
    {
      if ( m_policyActionSource == null )
        return;

      m_policyActionSource.PolicyActionReceived -= OnPolicyActionReceived;
      m_policyActionSource = null;
    }

    private void RefreshCycleTaskSubscription()
    {
      bool requestTrackingEvents = m_strictRlSimControlAudit != null;
      bool requestCycleEvents = recordOnCycleTask;
      ExcavationCycleTask requestedSource = requestTrackingEvents || requestCycleEvents ? cycleTask : null;
      if ( m_cycleTaskSource == requestedSource
        && m_trackingEventsSubscribed == requestTrackingEvents
        && m_cycleEventsSubscribed == requestCycleEvents )
        return;

      DetachCycleTaskSubscription();
      m_cycleTaskSource = requestedSource;
      if ( m_cycleTaskSource == null )
        return;

      if ( requestTrackingEvents ) {
        m_cycleTaskSource.TrackingSegmentStarted += OnTrackingSegmentStarted;
        m_cycleTaskSource.TrackingSegmentFinished += OnTrackingSegmentFinished;
        m_trackingEventsSubscribed = true;
      }
      if ( requestCycleEvents ) {
        m_cycleTaskSource.CycleStarted += OnCycleStarted;
        m_cycleTaskSource.CycleFinished += OnCycleFinished;
        m_cycleEventsSubscribed = true;
      }
    }

    private void DetachCycleTaskSubscription()
    {
      if ( m_cycleTaskSource == null )
        return;

      m_cycleTaskSource.TrackingSegmentStarted -= OnTrackingSegmentStarted;
      m_cycleTaskSource.TrackingSegmentFinished -= OnTrackingSegmentFinished;
      m_cycleTaskSource.CycleStarted -= OnCycleStarted;
      m_cycleTaskSource.CycleFinished -= OnCycleFinished;
      m_cycleTaskSource = null;
      m_trackingEventsSubscribed = false;
      m_cycleEventsSubscribed = false;
    }

    private void OnCycleStarted()
    {
      StartRecording();
    }

    private void OnCycleFinished( bool success, string reason )
    {
      StopAndExport( success, reason );
    }

    private void OnTrackingSegmentStarted( string traceRunId )
    {
      m_strictRlSimControlAudit?.StartTrackingSegment(
        traceRunId,
        Time.realtimeSinceStartupAsDouble );
    }

    private void OnTrackingSegmentFinished( string traceRunId, string result )
    {
      m_strictRlSimControlAudit?.FinishTrackingSegment(
        traceRunId,
        result,
        Time.realtimeSinceStartupAsDouble );
    }

    private void OnPolicyActionReceived( Vector4 action, int sequence, double monotonicSeconds )
    {
      if ( m_strictRlSimControlAudit == null || !m_strictRlSimControlAudit.IsEnabled )
        return;
      TryCaptureTrackingState( out StrictRlSimTrackingState tracking );
      m_strictRlSimControlAudit.RecordPolicySample( action, sequence, monotonicSeconds, tracking );
    }

    private void ResolveStrictRlSimControlAuditConfiguration()
    {
      ResolveRequestedAuditPaths( out string auditPath, out string suitePath );

      bool requested = enableStrictRlSimControlAudit
        || !string.IsNullOrWhiteSpace( auditPath )
        || !string.IsNullOrWhiteSpace( suitePath );
      if ( !requested ) {
        m_strictRlSimControlAudit = null;
        return;
      }

      string resolvedPath = ResolveStrictRlSimControlAuditPath( auditPath );
      string resolvedSuitePath = ResolveStrictRlSimControlAuditPath( suitePath );
      StrictRlSimControlAudit.TryCreate(
        resolvedPath,
        resolvedSuitePath,
        message => Debug.LogWarning( "[OpenLoopVelocityRecorder] " + message, this ),
        out m_strictRlSimControlAudit );
    }

    private void ResolveRequestedAuditPaths( out string auditPath, out string suitePath )
    {
      auditPath = Environment.GetEnvironmentVariable( kAuditPathEnvironmentVariable );
      suitePath = Environment.GetEnvironmentVariable( kSuitePathEnvironmentVariable );
      if ( string.IsNullOrWhiteSpace( auditPath ) )
        auditPath = controlAuditJsonlPath;
      if ( string.IsNullOrWhiteSpace( suitePath ) )
        suitePath = trajectorySuiteJsonPath;

      string[] args = Environment.GetCommandLineArgs();
      for ( int index = 0; index < args.Length - 1; index++ ) {
        if ( args[ index ] == "--rl-sim-control-audit-path" )
          auditPath = args[ index + 1 ];
        else if ( args[ index ] == "--rl-sim-trajectory-suite" )
          suitePath = args[ index + 1 ];
      }
    }

    private static string ResolveStrictRlSimControlAuditPath( string configuredPath )
    {
      if ( string.IsNullOrWhiteSpace( configuredPath ) )
        return "";

      return Path.IsPathRooted( configuredPath )
        ? configuredPath
        : Path.GetFullPath( Path.Combine( Application.dataPath, "..", configuredPath ) );
    }

    private bool TryCaptureTrackingState( out StrictRlSimTrackingState tracking )
    {
      ResolveReferences();
      Transform frame = frameRegistry != null ? frameRegistry.MachineRoot : null;
      if ( agent == null || agent.bucketTip == null || agent.trajectory == null || frame == null ) {
        tracking = default;
        return false;
      }

      Vector3 tipRos = UnityMachineLocalToRos( frame.InverseTransformPoint( agent.bucketTip.position ) );
      Vector3 waypointRos = UnityMachineLocalToRos(
        frame.InverseTransformPoint( agent.trajectory.CurrentTargetPosition ) );
      tracking = new StrictRlSimTrackingState(
        tipRos,
        waypointRos,
        agent.CurrentTargetIndex,
        Vector3.Distance( tipRos, waypointRos ),
        agent.CurrentPathProgress );
      return tracking.IsValid;
    }

    private static Vector3 UnityMachineLocalToRos( Vector3 unityLocal )
    {
      return new Vector3( unityLocal.z, -unityLocal.x, unityLocal.y );
    }

    private void RecordSample()
    {
      Transform bucketTip = agent != null ? agent.bucketTip : null;
      Transform localFrame = frameRegistry != null && frameRegistry.MachineRoot != null
        ? frameRegistry.MachineRoot
        : null;
      Vector3 tipWorld = bucketTip != null ? bucketTip.position : Vector3.zero;
      Vector3 tipLocal = localFrame != null && bucketTip != null
        ? localFrame.InverseTransformPoint( tipWorld )
        : Vector3.zero;
      Vector4 rawPolicyAction = agent != null ? agent.LastRawPolicyAction : Vector4.zero;

      m_samples.Add( new Sample {
        index = m_sampleIndex++,
        timestamp = Time.timeAsDouble - m_recordStartTime,
        unityTime = Time.time,
        phase = cycleTask != null ? cycleTask.state.ToString() : "",
        mode = agent != null ? agent.CurrentTaskMode.ToString() : "",
        boomPolicyActionRaw = rawPolicyAction.x,
        stickPolicyActionRaw = rawPolicyAction.y,
        bucketPolicyActionRaw = rawPolicyAction.z,
        swingPolicyActionRaw = rawPolicyAction.w,
        boomAction = controller.LastBoomCommand,
        stickAction = controller.LastStickCommand,
        bucketAction = controller.LastBucketCommand,
        swingAction = controller.LastSwingCommand,
        boomVelocity = controller.LastBoomTargetSpeed,
        stickVelocity = controller.LastStickTargetSpeed,
        bucketVelocity = controller.LastBucketTargetSpeed,
        swingVelocity = controller.LastSwingTargetSpeed,
        boomPosition = controller.BoomPosition,
        stickPosition = controller.StickPosition,
        bucketPosition = controller.BucketPosition,
        swingPosition = controller.SwingPosition,
        boomNormalizedPosition = controller.BoomNormalizedPosition,
        stickNormalizedPosition = controller.StickNormalizedPosition,
        bucketNormalizedPosition = controller.BucketNormalizedPosition,
        swingNormalizedPosition = controller.SwingNormalizedPosition,
        bucketTipWorld = tipWorld,
        bucketTipLocal = tipLocal
      } );
    }

    private void ExportCsv( bool success, string reason )
    {
      string directory = ResolveOutputDirectory();
      Directory.CreateDirectory( directory );

      MachineProfile profile = TryLoadProfile();
      string machineId = profile != null ? profile.MachineId : "unknown_machine";
      string timestamp = DateTime.Now.ToString( "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture );
      string filename = $"{machineId}_open_loop_{timestamp}_{( success ? "success" : "stopped" )}.csv";
      string path = Path.Combine( directory, filename );

      File.WriteAllText( path, BuildCsv( profile, success, reason ), Encoding.UTF8 );
      m_lastExportPath = path;
      Debug.Log( $"[OpenLoopVelocityRecorder] Exported {m_samples.Count} samples to {path}", this );
    }

    private string ResolveOutputDirectory()
    {
      if ( string.IsNullOrWhiteSpace( outputDirectoryName ) )
        return Path.GetFullPath( Path.Combine( Application.dataPath, ".." ) );

      return Path.IsPathRooted( outputDirectoryName )
        ? outputDirectoryName
        : Path.GetFullPath( Path.Combine( Application.dataPath, "..", outputDirectoryName ) );
    }

    private string BuildCsv( MachineProfile profile, bool success, string reason )
    {
      var builder = new StringBuilder( m_samples.Count * 256 );

      if ( includeMetadataComments ) {
        builder.AppendLine( "# rl_excavator_open_loop_velocity_export" );
        builder.AppendLine( "# success=" + success.ToString( CultureInfo.InvariantCulture ) );
        builder.AppendLine( "# reason=" + EscapeComment( reason ) );
        builder.AppendLine( "# unity_fixed_delta_time_s=" + Time.fixedDeltaTime.ToString( "G9", CultureInfo.InvariantCulture ) );
        builder.AppendLine( "# units: boom/stick/bucket velocity=m/s, swing velocity=rad/s, prismatic position=m, swing position=rad" );
        builder.AppendLine( "# command convention: *_policy_action_raw is the model output before deadzone; *_action_cmd is the effective command after deadzone; *_v_ref is controller target speed after sim-side acceleration limiting" );

        if ( profile != null ) {
          builder.AppendLine( "# profile_machine_id=" + profile.MachineId );
          builder.AppendLine( "# profile_schema_version=" + profile.SchemaVersion );
          builder.AppendLine( "# profile_action_order=" + string.Join( "|", profile.ActionOrder ) );
          AppendActuatorMetadata( builder, profile, "boom" );
          AppendActuatorMetadata( builder, profile, "stick" );
          AppendActuatorMetadata( builder, profile, "bucket" );
          AppendActuatorMetadata( builder, profile, "swing" );
        }
      }

      builder.AppendLine(
        "sample_index,timestamp_s,unity_time_s,phase,mode,"
        + "boom_action_cmd,stick_action_cmd,bucket_action_cmd,swing_action_cmd,"
        + "boom_v_ref_mps,stick_v_ref_mps,bucket_v_ref_mps,swing_v_ref_radps,"
        + "boom_q_m,stick_q_m,bucket_q_m,swing_q_rad,"
        + "boom_q_norm,stick_q_norm,bucket_q_norm,swing_q_norm,"
        + "bucket_tip_world_x,bucket_tip_world_y,bucket_tip_world_z,"
        + "bucket_tip_local_x,bucket_tip_local_y,bucket_tip_local_z,"
        + "boom_policy_action_raw,stick_policy_action_raw,bucket_policy_action_raw,swing_policy_action_raw" );

      for ( int i = 0; i < m_samples.Count; i++ )
        AppendSample( builder, m_samples[ i ] );

      return builder.ToString();
    }

    private static void AppendActuatorMetadata( StringBuilder builder, MachineProfile profile, string actuatorName )
    {
      MachineActuatorProfile actuator = profile.GetActuator( actuatorName );
      builder.AppendLine( "# actuator_" + actuatorName
        + "_max_abs_speed=" + actuator.MaxAbsSpeed.ToString( "G9", CultureInfo.InvariantCulture )
        + ",max_speed_positive=" + actuator.MaxSpeedPositive.ToString( "G9", CultureInfo.InvariantCulture )
        + ",max_speed_negative=" + actuator.MaxSpeedNegative.ToString( "G9", CultureInfo.InvariantCulture )
        + ",command_deadzone_positive_normalized=" + actuator.CommandDeadzonePositiveNormalized.ToString( "G9", CultureInfo.InvariantCulture )
        + ",command_deadzone_negative_normalized=" + actuator.CommandDeadzoneNegativeNormalized.ToString( "G9", CultureInfo.InvariantCulture )
        + ",sign=" + actuator.Sign.ToString( CultureInfo.InvariantCulture )
        + ",joint_type=" + actuator.JointType );
    }

    private static void AppendSample( StringBuilder builder, Sample sample )
    {
      Append( builder, sample.index );
      Append( builder, sample.timestamp );
      Append( builder, sample.unityTime );
      AppendText( builder, sample.phase );
      AppendText( builder, sample.mode );
      Append( builder, sample.boomAction );
      Append( builder, sample.stickAction );
      Append( builder, sample.bucketAction );
      Append( builder, sample.swingAction );
      Append( builder, sample.boomVelocity );
      Append( builder, sample.stickVelocity );
      Append( builder, sample.bucketVelocity );
      Append( builder, sample.swingVelocity );
      Append( builder, sample.boomPosition );
      Append( builder, sample.stickPosition );
      Append( builder, sample.bucketPosition );
      Append( builder, sample.swingPosition );
      Append( builder, sample.boomNormalizedPosition );
      Append( builder, sample.stickNormalizedPosition );
      Append( builder, sample.bucketNormalizedPosition );
      Append( builder, sample.swingNormalizedPosition );
      Append( builder, sample.bucketTipWorld.x );
      Append( builder, sample.bucketTipWorld.y );
      Append( builder, sample.bucketTipWorld.z );
      Append( builder, sample.bucketTipLocal.x );
      Append( builder, sample.bucketTipLocal.y );
      Append( builder, sample.bucketTipLocal.z );
      Append( builder, sample.boomPolicyActionRaw );
      Append( builder, sample.stickPolicyActionRaw );
      Append( builder, sample.bucketPolicyActionRaw );
      AppendLast( builder, sample.swingPolicyActionRaw );
    }

    private static void Append( StringBuilder builder, int value )
    {
      builder.Append( value.ToString( CultureInfo.InvariantCulture ) );
      builder.Append( ',' );
    }

    private static void Append( StringBuilder builder, double value )
    {
      builder.Append( value.ToString( "G17", CultureInfo.InvariantCulture ) );
      builder.Append( ',' );
    }

    private static void Append( StringBuilder builder, float value )
    {
      builder.Append( value.ToString( "G9", CultureInfo.InvariantCulture ) );
      builder.Append( ',' );
    }

    private static void AppendText( StringBuilder builder, string value )
    {
      builder.Append( EscapeCsv( value ) );
      builder.Append( ',' );
    }

    private static void AppendLast( StringBuilder builder, float value )
    {
      builder.Append( value.ToString( "G9", CultureInfo.InvariantCulture ) );
      builder.AppendLine();
    }

    private static string EscapeCsv( string value )
    {
      if ( string.IsNullOrEmpty( value ) )
        return "";

      if ( value.IndexOfAny( new[] { ',', '"', '\n', '\r' } ) < 0 )
        return value;

      return "\"" + value.Replace( "\"", "\"\"" ) + "\"";
    }

    private static string EscapeComment( string value )
    {
      return string.IsNullOrEmpty( value ) ? "" : value.Replace( "\n", " " ).Replace( "\r", " " );
    }

    private static MachineProfile TryLoadProfile()
    {
      try {
        return MachineProfileLoader.Load();
      }
      catch ( Exception exception ) {
        Debug.LogWarning( "[OpenLoopVelocityRecorder] Failed to load machine profile metadata: " + exception.Message );
        return null;
      }
    }

  }
}
