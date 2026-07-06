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
    [Header("References")]
    public ExcavationCycleTask cycleTask;
    public ExcavatorController controller;
    public ExcavatorTrajectoryAgent agent;
    public ExcavatorFrameRegistry frameRegistry;

    [Header("Recording")]
    [Tooltip("任务执行开始/结束时自动开始和导出记录。")]
    public bool recordOnCycleTask = true;

    [Tooltip("CSV 导出目录。相对路径位于 Unity persistentDataPath 下；绝对路径直接使用。")]
    public string outputDirectoryName = "open_loop_velocity_exports";

    [Tooltip("导出的 CSV 顶部写入 profile/action_order/正反向速度等元信息注释。")]
    public bool includeMetadataComments = true;

    [Tooltip("每次开始记录时清空上一轮样本。")]
    public bool clearSamplesOnStart = true;

    [Header("Runtime")]
    [SerializeField]
    private bool m_isRecording = false;

    [SerializeField]
    private string m_lastExportPath = "";

    private readonly List<Sample> m_samples = new List<Sample>( 4096 );
    private double m_recordStartTime = 0.0;
    private int m_sampleIndex = 0;

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
      ResolveReferences();
    }

    private void OnEnable()
    {
      ResolveReferences();
      Subscribe();
    }

    private void OnDisable()
    {
      Unsubscribe();
    }

    private void FixedUpdate()
    {
      if ( !m_isRecording )
        return;

      RecordSample();
    }

    [ContextMenu("开始记录")]
    public void StartRecording()
    {
      ResolveReferences();

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
      if ( controller == null && cycleTask != null )
        controller = cycleTask.controller;
      if ( agent == null && cycleTask != null )
        agent = cycleTask.agent;
      if ( frameRegistry == null && agent != null )
        frameRegistry = agent.frameRegistry;
      if ( frameRegistry == null && controller != null )
        frameRegistry = controller.GetComponent<ExcavatorFrameRegistry>();
    }

    private void Subscribe()
    {
      if ( !recordOnCycleTask || cycleTask == null )
        return;

      cycleTask.CycleStarted -= OnCycleStarted;
      cycleTask.CycleFinished -= OnCycleFinished;
      cycleTask.CycleStarted += OnCycleStarted;
      cycleTask.CycleFinished += OnCycleFinished;
    }

    private void Unsubscribe()
    {
      if ( cycleTask == null )
        return;

      cycleTask.CycleStarted -= OnCycleStarted;
      cycleTask.CycleFinished -= OnCycleFinished;
    }

    private void OnCycleStarted()
    {
      StartRecording();
    }

    private void OnCycleFinished( bool success, string reason )
    {
      StopAndExport( success, reason );
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

      m_samples.Add( new Sample {
        index = m_sampleIndex++,
        timestamp = Time.timeAsDouble - m_recordStartTime,
        unityTime = Time.time,
        phase = cycleTask != null ? cycleTask.state.ToString() : "",
        mode = agent != null ? agent.CurrentTaskMode.ToString() : "",
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
        return Application.persistentDataPath;

      return Path.IsPathRooted( outputDirectoryName )
        ? outputDirectoryName
        : Path.Combine( Application.persistentDataPath, outputDirectoryName );
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
        builder.AppendLine( "# command convention: *_action_cmd in [-1,1], *_v_ref is controller target speed after sim-side acceleration limiting" );

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
        + "bucket_tip_local_x,bucket_tip_local_y,bucket_tip_local_z" );

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
        + ",sign=" + actuator.Sign.ToString( CultureInfo.InvariantCulture )
        + ",deploy_sign=" + actuator.DeploySign.ToString( CultureInfo.InvariantCulture )
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
      AppendLast( builder, sample.bucketTipLocal.z );
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
