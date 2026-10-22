using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AGXUnity;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace RLExcavator
{
  /// <summary>Local, one-client simulation probe. All AGX calls stay on Unity's main thread.</summary>
  public sealed class ExcavatorSimBridge : MonoBehaviour
  {
    [Serializable]
    private sealed class Request
    {
      public string kind;
      public int epoch;
      public int sequence;
      public double issued_sim_time_s;
      public double expires_sim_time_s;
      public string[] axis_order;
      public float[] values;
      public int steps;
      public Vector3[] points_ros_m;
    }

    [Serializable]
    private class Reply
    {
      public string kind;
      public int epoch;
      public int sequence;
      public bool accepted;
      public string reason;
      public int state_sequence;
      public double sim_time_s;
      public string machine_frame_id;
      public bool state_valid;
      public ActuatorState actuator_state;
      public BucketTipPose bucket_tip_pose;
      public CameraImage front_rgb;
      public RawJointAngles raw_joint_angles_rad;
      public ExcavatorWallContacts.State wall_contact;
      public ExcavatorMaterialTelemetry.State material_state;
    }

    // JsonUtility 会为 null 的内联对象输出默认字段，单独的回复类型保证普通状态不携带空地图。
    [Serializable]
    private sealed class TerrainReply : Reply
    {
      public ExcavatorTerrainTelemetry.State terrain_state;
    }

    [Serializable]
    private sealed class LidarReply : Reply
    {
      public ExcavatorLidarTelemetry.ScanSet lidar_state;
    }

    [Serializable]
    private sealed class LidarConfigurationReply : Reply
    {
      public LidarConfiguration lidar_configuration;
    }

    [Serializable]
    private sealed class LidarConfiguration
    {
      public bool enabled;
      public int sensor_count;
    }

    [Serializable]
    private sealed class RawJointAngles
    {
      public float boom;
      public float arm;
      public float bucket;
    }

    [Serializable]
    private sealed class CameraImage
    {
      public string encoding = "png";
      public int width = ExcavatorCameraCapture.Width;
      public int height = ExcavatorCameraCapture.Height;
      public string data_base64;
    }

    [Serializable]
    private sealed class AxisState
    {
      public float position_m;
      public float velocity_mps;
      public float position_rad;
      public float velocity_rad_s;
    }

    [Serializable]
    private sealed class ActuatorState
    {
      public AxisState boom;
      public AxisState stick;
      public AxisState bucket;
      public AxisState swing;
    }

    [Serializable]
    private sealed class BucketTipPose
    {
      public Vector3 position_ros_m;
      public float pitch_rad;
    }

    private sealed class Pending
    {
      public string json;
      public TaskCompletionSource<string> reply;
    }

    [Tooltip("Loopback-only TCP port for newline-delimited JSON commands.")]
    public int port = 18790;
    [Tooltip("Physical steps allowed in a single step request.")]
    public int maxStepsPerRequest = 1000;
    [Tooltip("Fixed machine frame used for Follow waypoints.")]
    public Transform machineRoot;
    [Tooltip("Only show the current RL reference path in the overview/Scene view; excluded from policy cameras.")]
    public bool showReferencePath = true;
    [Tooltip("随坐标原点迁移而更新；Python 对照共享配置，防止新旧原点混用。")]
    public string machineFrameId = "legacy_machine_root_v1";
    [Tooltip("Physical tip point on the bucket.")]
    public Transform bucketTip;
    [Tooltip("Swinging upper body frame used to measure bucket pitch.")]
    public Transform bucketPitchFrame;
    [Tooltip("Independent front RGB sensor; not the operator's overview camera.")]
    public Camera frontCamera;
    [Tooltip("Soil restored after the machine returns home; optional in soil-free scenes.")]
    public ExcavatorRuntimeSoil soil;
    [Tooltip("Read-only contact telemetry for the excavator and four soil-container walls.")]
    public ExcavatorWallContacts wallContacts;
    [Tooltip("阶段边界读取的斗内及车厢物料统计；不在每个物理步遍历颗粒。")]
    public ExcavatorMaterialTelemetry materialTelemetry;
    [Tooltip("按需读取活动地形；不进入 ACT 图像采样或每个物理步。")]
    public ExcavatorTerrainTelemetry terrainTelemetry;
    [Tooltip("AGX 雷达扫描窗口；只在显式 read_lidar 请求中发送点云。")]
    public ExcavatorLidarTelemetry[] lidarSensors = System.Array.Empty<ExcavatorLidarTelemetry>();
    [Tooltip("Raw boom hinge angle for calibration; not an ACT sensor angle.")]
    public Constraint boomJoint;
    [Tooltip("Raw arm hinge angle for calibration; not an ACT sensor angle.")]
    public Constraint armJoint;
    [Tooltip("Raw bucket hinge angle for calibration; not an ACT sensor angle.")]
    public Constraint bucketJoint;

    private readonly Queue<Pending> m_inbox = new Queue<Pending>();
    private readonly object m_inboxLock = new object();
    private TcpListener m_listener;
    private Thread m_worker;
    private volatile bool m_running;
    private int m_disconnectGeneration;
    private int m_seenDisconnectGeneration;
    private Simulation m_simulation;
    private Simulation.AutoSteppingModes m_previousSteppingMode;
    private ExcavatorRuntimeActuators m_actuators;
    private Pending m_active;
    private int m_stepsRemaining;
    private bool m_resetting;
    private bool m_soilResetStarted;
    private int m_epoch;
    private int m_lastSequence;
    private int m_stateSequence;
    private double m_simTime;
    private double m_expiry;
    private readonly float[] m_target = new float[4];
    private bool m_hasTarget;
    private bool m_manualTarget;
    private ExcavatorReferencePath m_referencePath;

    private void Start()
    {
      m_actuators = GetComponentInChildren<ExcavatorRuntimeActuators>( true );
      m_simulation = Simulation.Instance;
      if ( m_actuators == null || m_simulation == null || port < 1 || port > 65535 ) {
        Debug.LogError( "[ExcavatorSimBridge] Missing actuator driver, simulation, or valid port.", this );
        enabled = false;
        return;
      }

      m_previousSteppingMode = m_simulation.AutoSteppingMode;
      m_referencePath = gameObject.AddComponent<ExcavatorReferencePath>();
      m_simulation.AutoSteppingMode = Simulation.AutoSteppingModes.Disabled;
      m_simulation.TimeStep = Time.fixedDeltaTime;
      m_actuators.StopAll();
      try {
        m_listener = new TcpListener( IPAddress.Loopback, port );
        m_listener.Start();
        m_running = true;
        m_worker = new Thread( Serve ) { IsBackground = true, Name = "ExcavatorSimBridge" };
        m_worker.Start();
        Debug.Log( "[ExcavatorSimBridge] Listening on 127.0.0.1:" + port, this );
      }
      catch ( System.Exception error ) {
        Debug.LogError( "[ExcavatorSimBridge] Could not listen: " + error.Message, this );
        enabled = false;
      }
    }

    private void Update()
    {
      if (m_referencePath != null) m_referencePath.visible = showReferencePath;
      int generation = Interlocked.CompareExchange( ref m_disconnectGeneration, 0, 0 );
      if ( generation != m_seenDisconnectGeneration ) {
        m_seenDisconnectGeneration = generation;
        ClearTarget();
      }
      if ( m_active == null ) {
        Pending next = null;
        lock ( m_inboxLock ) {
          if ( m_inbox.Count > 0 )
            next = m_inbox.Dequeue();
        }
        if ( next != null )
          Handle( next );
      }

      // Explicit step requests already specify simulation time. Do not also wait
      // for Unity's wall-clock FixedUpdate cadence. Yield between physics steps
      // after 50 ms so a long request still lets the editor and rendering run.
      long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency / 20;
      while ( m_active != null && !m_resetting ) {
        AdvancePhysics();
        if ( System.Diagnostics.Stopwatch.GetTimestamp() >= deadline )
          break;
      }
    }

    private void FixedUpdate()
    {
      // Homing counts WaitForFixedUpdate yields; keep its existing cadence.
      if ( m_resetting )
        AdvancePhysics();
    }

    private void AdvancePhysics()
    {
      if ( m_simulation == null || m_active == null )
        return;

      if ( m_resetting ) {
        if ( m_actuators.IsHoming ) {
          if ( !TryDoStep() ) {
            m_actuators.CancelHomePoseReset();
            m_resetting = false;
            Complete( new Reply { kind = "reset_result", epoch = m_epoch,
              accepted = false, reason = "physics_not_stepping" } );
          }
          return;
        }
        if ( soil != null ) {
          if ( !m_soilResetStarted ) {
            m_soilResetStarted = true;
            soil.BeginReset();
          }
          if ( soil.IsResetting ) return;
          if ( soil.ResetError != null ) {
            m_resetting = false;
            Complete( new Reply { kind = "reset_result", epoch = m_epoch,
              accepted = false, reason = "soil_reset_failed: " + soil.ResetError } );
            return;
          }
        }
        m_resetting = false;
        m_actuators.StopAll();
        Reply result = ReadState();
        result.kind = "reset_result";
        result.reason = result.state_valid ? "reset" : result.reason;
        result.accepted = result.state_valid;
        if ( result.accepted && wallContacts != null ) {
          wallContacts.BeginEpisode();
          result.wall_contact = wallContacts.ReadState();
        }
        Complete( result );
        return;
      }

      if ( m_stepsRemaining <= 0 )
        return;
      if ( m_hasTarget && m_simTime + m_simulation.TimeStep > m_expiry )
        ClearTarget();
      if ( m_hasTarget && !ApplyTarget( m_manualTarget, m_target ) )
        ClearTarget();
      if ( !TryDoStep() ) {
        ClearTarget();
        m_stepsRemaining = 0;
        Complete( new Reply { kind = "state", epoch = m_epoch,
          accepted = false, reason = "physics_not_stepping", state_valid = false } );
        return;
      }
      m_stateSequence++;
      m_simTime = m_stateSequence * (double)m_simulation.TimeStep;
      foreach (var lidar in lidarSensors ?? System.Array.Empty<ExcavatorLidarTelemetry>())
        if (lidar != null) lidar.CaptureStep(m_epoch, m_stateSequence, m_simTime, m_simulation.TimeStep);
      m_stepsRemaining--;
      if ( m_stepsRemaining == 0 )
        Complete( ReadState() );
    }

    private void Handle( Pending pending )
    {
      Request request;
      try { request = JsonUtility.FromJson<Request>( pending.json ); }
      catch ( System.Exception ) { Reject( pending, null, "invalid_json" ); return; }
      if ( request == null ) { Reject( pending, null, "invalid_json" ); return; }

      if ( request.kind == "reset" ) {
        if ( wallContacts != null ) wallContacts.BeginReset();
        ClearTarget();
        m_actuators.StopAll();
        m_epoch++;
        foreach (var lidar in lidarSensors ?? System.Array.Empty<ExcavatorLidarTelemetry>())
          if (lidar != null) lidar.BeginEpoch(m_epoch);
        m_lastSequence = 0;
        m_stateSequence = 0;
        m_simTime = 0;
        m_active = pending;
        m_resetting = true;
        m_soilResetStarted = false;
        try { m_actuators.BeginHomePoseReset(); }
        catch ( System.Exception error ) {
          m_resetting = false;
          m_active = null;
          Reject( pending, request, "reset_failed: " + error.Message );
        }
        return;
      }

      if ( request.kind == "velocity_reference" || request.kind == "manual_action" ) {
        bool manual = request.kind == "manual_action";
        string reason = ValidateCommand( request );
        if ( reason != null ) { Reject( pending, request, reason ); return; }
        if ( manual && Array.Exists( request.values, value => value < -1f || value > 1f ) ) {
          Reject( pending, request, "manual_action_out_of_range" );
          return;
        }
        if ( !ApplyTarget( manual, request.values ) ) {
          Reject( pending, request, "controller_rejected" );
          return;
        }
        Array.Copy( request.values, m_target, 4 );
        m_manualTarget = manual;
        m_expiry = request.expires_sim_time_s;
        m_lastSequence = request.sequence;
        m_hasTarget = true;
        Complete( pending, new Reply { kind = manual ? "manual_result" : "velocity_result", epoch = m_epoch,
          sequence = request.sequence, accepted = true, reason = "accepted" } );
        return;
      }

      if ( request.kind == "read_effect" ) {
        if ( request.epoch != m_epoch || m_epoch == 0 ) { Reject( pending, request, "wrong_epoch" ); return; }
        // 与当前位姿使用同一状态序号，不推进物理，也不采集 RGB。
        Reply effect = ReadState();
        if ( !effect.state_valid ) { Reject( pending, request, "effect_state_invalid" ); return; }
        effect.kind = "effect";
        effect.reason = "effect_read";
        effect.material_state = materialTelemetry != null ? materialTelemetry.ReadState() :
          new ExcavatorMaterialTelemetry.State { available = false, reason = "material_reader_missing" };
        Complete( pending, effect );
        return;
      }

      if (request.kind == "reference_path") {
        if (request.epoch != m_epoch || m_epoch == 0) { Reject(pending, request, "wrong_epoch"); return; }
        try { m_referencePath.SetPath(request.points_ros_m, machineRoot, Camera.main); }
        catch (ArgumentException error) { Reject(pending, request, error.Message); return; }
        // Display-only: no physics step, state read, actuator target, or motion sequence change.
        Complete(pending, new Reply { kind = "reference_path_result", epoch = m_epoch,
          accepted = true, reason = "reference_path_displayed" });
        return;
      }

      if (request.kind == "configure_lidar") {
        if (request.epoch != m_epoch || m_epoch == 0) { Reject(pending, request, "wrong_epoch"); return; }
        if (!TryReadLidarEnabled(pending.json, out bool enabled)) {
          Reject(pending, request, "invalid_lidar_enabled"); return;
        }
        // 启停与当前本体状态同帧，不采集 RGB、不推进物理、不消耗动作序号。
        var configuration = PopulateState(new LidarConfigurationReply());
        if (!configuration.state_valid) { Reject(pending, request, "lidar_state_invalid"); return; }
        try { ExcavatorLidarTelemetry.ConfigureScanSet(lidarSensors, enabled); }
        catch (InvalidOperationException error) { Reject(pending, request, error.Message); return; }
        configuration.kind = "lidar_configuration";
        configuration.reason = "lidar_configured";
        configuration.lidar_configuration = new LidarConfiguration { enabled = enabled, sensor_count = lidarSensors.Length };
        Complete(pending, configuration);
        return;
      }

      if (request.kind == "read_lidar") {
        if (request.epoch != m_epoch || m_epoch == 0) { Reject(pending, request, "wrong_epoch"); return; }
        var scan = PopulateState(new LidarReply());
        if (!scan.state_valid) { Reject(pending, request, "lidar_state_invalid"); return; }
        scan.kind = "lidar";
        scan.reason = "lidar_read";
        scan.lidar_state = ExcavatorLidarTelemetry.ReadScanSet(lidarSensors, m_epoch);
        Complete(pending, scan);
        return;
      }

      if ( request.kind == "read_terrain" ) {
        if ( request.epoch != m_epoch || m_epoch == 0 ) { Reject( pending, request, "wrong_epoch" ); return; }
        var terrain = PopulateState( new TerrainReply() );
        if ( !terrain.state_valid ) { Reject( pending, request, "terrain_state_invalid" ); return; }
        terrain.kind = "terrain";
        terrain.reason = "terrain_read";
        terrain.terrain_state = terrainTelemetry != null ? terrainTelemetry.ReadState() :
          new ExcavatorTerrainTelemetry.State { reason = "terrain_reader_missing" };
        Complete( pending, terrain );
        return;
      }

      if ( request.kind == "observe" ) {
        if ( request.epoch != m_epoch || m_epoch == 0 ) { Reject( pending, request, "wrong_epoch" ); return; }
        try {
          Reply observation = ReadState();
          if ( !observation.state_valid ) throw new InvalidOperationException( observation.reason );
          observation.front_rgb = new CameraImage {
            data_base64 = Convert.ToBase64String( ExcavatorCameraCapture.CapturePng( frontCamera ) )
          };
          observation.kind = "observation";
          observation.reason = "observed";
          Complete( pending, observation );
        }
        catch ( System.Exception error ) { Reject( pending, request, "observation_failed: " + error.Message ); }
        return;
      }

      if ( request.kind == "step" ) {
        if ( request.epoch != m_epoch || m_epoch == 0 ) { Reject( pending, request, "wrong_epoch" ); return; }
        if ( request.steps < 1 || request.steps > maxStepsPerRequest ) {
          Reject( pending, request, "invalid_step_count" ); return;
        }
        m_active = pending;
        m_stepsRemaining = request.steps;
        return;
      }

      Reject( pending, request, "unsupported_kind" );
    }

    private static bool TryReadLidarEnabled(string json, out bool enabled)
    {
      enabled = false;
      try {
        // JsonUtility 会把缺失、数字或字符串宽松转换；配置开关必须是显式 JSON bool。
        var data = JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        var value = data["enabled"];
        if (value == null || value.Type != JTokenType.Boolean) return false;
        enabled = (bool)value;
        return true;
      }
      catch (JsonException) { return false; }
    }

    private bool ApplyTarget( bool manual, float[] values )
    {
      return manual
        ? m_actuators.TrySetManualAction( values[0], values[1], values[2], values[3] )
        : m_actuators.TrySetPhysicalSpeeds( values[0], values[1], values[2], values[3] );
    }

    private string ValidateCommand( Request request )
    {
      if ( m_epoch == 0 || request.epoch != m_epoch ) return "wrong_epoch";
      if ( request.sequence <= m_lastSequence ) return "out_of_order";
      if ( request.axis_order == null || request.axis_order.Length != 4 ||
          request.axis_order[0] != "boom" || request.axis_order[1] != "stick" ||
          request.axis_order[2] != "bucket" || request.axis_order[3] != "swing" ) return "wrong_axis_order";
      if ( request.values == null || request.values.Length != 4 ) return "wrong_value_count";
      foreach ( float value in request.values )
        if ( float.IsNaN( value ) || float.IsInfinity( value ) ) return "non_finite_value";
      // JSON double round trips can round an echoed clock slightly upward (1 ns tolerance).
      if ( double.IsNaN( request.issued_sim_time_s ) || double.IsNaN( request.expires_sim_time_s ) ||
           double.IsInfinity( request.issued_sim_time_s ) || double.IsInfinity( request.expires_sim_time_s ) ||
           request.issued_sim_time_s > m_simTime + 1e-9 || request.expires_sim_time_s <= m_simTime ||
           request.expires_sim_time_s <= request.issued_sim_time_s ) return "expired_or_future_command";
      return null;
    }

    private Reply ReadState()
    {
      return PopulateState( new Reply() );
    }

    private T PopulateState<T>( T result ) where T : Reply
    {
      result.kind = "state";
      result.epoch = m_epoch;
      result.accepted = true;
      result.reason = "stepped";
      result.state_sequence = m_stateSequence;
      result.sim_time_s = m_simTime;
      result.machine_frame_id = machineFrameId;
      result.wall_contact = wallContacts != null ? wallContacts.ReadState() : new ExcavatorWallContacts.State();
      result.state_valid = HasActuators() && HasTipPose() &&
        boomJoint != null && armJoint != null && bucketJoint != null;
      if ( !result.state_valid ) return result;
      try {
        result.actuator_state = new ActuatorState {
          boom = ReadAxis( m_actuators.boom[0], false ),
          stick = ReadAxis( m_actuators.stick[0], false ),
          bucket = ReadAxis( m_actuators.bucket[0], false ),
          swing = ReadAxis( m_actuators.swing, true )
        };
        result.bucket_tip_pose = ReadBucketTipPose();
        result.raw_joint_angles_rad = new RawJointAngles {
          boom = ReadAxis( boomJoint, true ).position_rad,
          arm = ReadAxis( armJoint, true ).position_rad,
          bucket = ReadAxis( bucketJoint, true ).position_rad
        };
      }
      catch ( System.Exception error ) {
        result.state_valid = false;
        result.reason = "state_read_failed: " + error.Message;
      }
      return result;
    }

    private bool TryDoStep()
    {
      double before = m_simulation.Native.getTimeStamp();
      if ( wallContacts != null ) wallContacts.BeginStep();
      try { m_simulation.DoStep(); }
      finally { if ( wallContacts != null ) wallContacts.EndStep(); }
      return m_simulation.Native.getTimeStamp() > before;
    }

    private static AxisState ReadAxis( Constraint constraint, bool rotational )
    {
      float position = constraint.GetCurrentAngle();
      float speed = constraint.GetCurrentSpeed();
      if ( float.IsNaN( position ) || float.IsInfinity( position ) ||
           float.IsNaN( speed ) || float.IsInfinity( speed ) )
        throw new InvalidOperationException( "Non-finite actuator state" );
      return rotational
        ? new AxisState { position_rad = position, velocity_rad_s = speed }
        : new AxisState { position_m = position, velocity_mps = speed };
    }

    private bool HasActuators()
    {
      return m_actuators != null && m_actuators.HasActuators;
    }

    private bool HasTipPose()
    {
      return machineRoot != null && bucketTip != null && bucketPitchFrame != null;
    }

    private BucketTipPose ReadBucketTipPose()
    {
      Vector3 local = machineRoot.InverseTransformPoint( bucketTip.position );
      Vector3 ros = new Vector3( local.z, -local.x, local.y );
      Vector3 pitchAxis = bucketPitchFrame.right;
      Vector3 forward = Vector3.ProjectOnPlane( bucketPitchFrame.forward, pitchAxis );
      Vector3 bucketAxis = Vector3.ProjectOnPlane( bucketTip.forward, pitchAxis );
      if ( forward.sqrMagnitude < 1e-8f || bucketAxis.sqrMagnitude < 1e-8f )
        throw new InvalidOperationException( "Bucket pitch axis is undefined" );
      float pitch = Vector3.SignedAngle( forward, bucketAxis, pitchAxis ) * Mathf.Deg2Rad;
      if ( !float.IsFinite( ros.x ) || !float.IsFinite( ros.y ) || !float.IsFinite( ros.z ) ||
           !float.IsFinite( pitch ) )
        throw new InvalidOperationException( "Non-finite bucket tip pose" );
      return new BucketTipPose { position_ros_m = ros, pitch_rad = pitch };
    }

    private void Reject( Pending pending, Request request, string reason )
    {
      ClearTarget();
      m_actuators.StopAll();
      Complete( pending, new Reply { kind = "rejected", epoch = m_epoch,
        sequence = request == null ? 0 : request.sequence, accepted = false, reason = reason } );
    }

    private void ClearTarget()
    {
      if (m_referencePath != null) m_referencePath.Clear();
      m_hasTarget = false;
      m_manualTarget = false;
      Array.Clear( m_target, 0, m_target.Length );
      if ( m_actuators != null ) m_actuators.StopAll();
    }

    private void Complete( Reply reply )
    {
      Pending pending = m_active;
      m_active = null;
      Complete( pending, reply );
    }

    private static void Complete( Pending pending, Reply reply )
    {
      pending.reply.TrySetResult( JsonUtility.ToJson( reply ) );
    }

    private void Serve()
    {
      while ( m_running ) {
        try {
          using ( TcpClient client = m_listener.AcceptTcpClient() )
          using ( NetworkStream stream = client.GetStream() )
          using ( var reader = new StreamReader( stream, Encoding.UTF8 ) )
          using ( var writer = new StreamWriter( stream, new UTF8Encoding( false ) ) { AutoFlush = true } ) {
            string line;
            while ( m_running && (line = reader.ReadLine()) != null ) {
              // Dense reference curves contain hundreds of XYZ samples; commands remain small.
              if ( line.Length > 65536 ) { writer.WriteLine( "{\"accepted\":false,\"reason\":\"line_too_long\"}" ); continue; }
              var pending = new Pending { json = line,
                reply = new TaskCompletionSource<string>( TaskCreationOptions.RunContinuationsAsynchronously ) };
              lock ( m_inboxLock ) m_inbox.Enqueue( pending );
              writer.WriteLine( pending.reply.Task.GetAwaiter().GetResult() );
            }
          }
        }
        catch ( System.Exception ) {
          if ( !m_running ) break;
        }
        finally {
          Interlocked.Increment( ref m_disconnectGeneration );
        }
      }
    }

    private void OnDisable()
    {
      if (m_referencePath != null) m_referencePath.Clear();
      m_running = false;
      m_listener?.Stop();
      if ( m_actuators != null ) m_actuators.StopAll();
      if ( m_simulation != null ) m_simulation.AutoSteppingMode = m_previousSteppingMode;
      m_active?.reply.TrySetResult( "{\"accepted\":false,\"reason\":\"bridge_stopped\"}" );
      lock ( m_inboxLock )
        while ( m_inbox.Count > 0 )
          m_inbox.Dequeue().reply.TrySetResult( "{\"accepted\":false,\"reason\":\"bridge_stopped\"}" );
    }
  }
}
