using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AGXUnity;
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
    }

    [Serializable]
    private sealed class Reply
    {
      public string kind;
      public int epoch;
      public int sequence;
      public bool accepted;
      public string reason;
      public int state_sequence;
      public double sim_time_s;
      public bool state_valid;
      public ActuatorState actuator_state;
      public BucketTipPose bucket_tip_pose;
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
    [Tooltip("Physical tip point on the bucket.")]
    public Transform bucketTip;
    [Tooltip("Swinging upper body frame used to measure bucket pitch.")]
    public Transform bucketPitchFrame;

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
    private int m_epoch;
    private int m_lastSequence;
    private int m_stateSequence;
    private double m_simTime;
    private double m_expiry;
    private readonly float[] m_target = new float[4];
    private bool m_hasTarget;
    private bool m_manualTarget;

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
      int generation = Interlocked.CompareExchange( ref m_disconnectGeneration, 0, 0 );
      if ( generation != m_seenDisconnectGeneration ) {
        m_seenDisconnectGeneration = generation;
        ClearTarget();
      }
      if ( m_active != null )
        return;

      Pending next = null;
      lock ( m_inboxLock ) {
        if ( m_inbox.Count > 0 )
          next = m_inbox.Dequeue();
      }
      if ( next != null )
        Handle( next );
    }

    private void FixedUpdate()
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
        m_resetting = false;
        m_actuators.StopAll();
        Reply result = ReadState();
        result.kind = "reset_result";
        result.reason = result.state_valid ? "reset" : result.reason;
        result.accepted = result.state_valid;
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
        ClearTarget();
        m_actuators.StopAll();
        m_epoch++;
        m_lastSequence = 0;
        m_stateSequence = 0;
        m_simTime = 0;
        m_active = pending;
        m_resetting = true;
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
      if ( double.IsNaN( request.issued_sim_time_s ) || double.IsNaN( request.expires_sim_time_s ) ||
           double.IsInfinity( request.issued_sim_time_s ) || double.IsInfinity( request.expires_sim_time_s ) ||
           request.issued_sim_time_s > m_simTime || request.expires_sim_time_s <= m_simTime ||
           request.expires_sim_time_s <= request.issued_sim_time_s ) return "expired_or_future_command";
      return null;
    }

    private Reply ReadState()
    {
      var result = new Reply { kind = "state", epoch = m_epoch, accepted = true,
        reason = "stepped", state_sequence = m_stateSequence, sim_time_s = m_simTime,
        state_valid = HasActuators() && HasTipPose() };
      if ( !result.state_valid ) return result;
      try {
        result.actuator_state = new ActuatorState {
          boom = ReadAxis( m_actuators.boom[0], false ),
          stick = ReadAxis( m_actuators.stick[0], false ),
          bucket = ReadAxis( m_actuators.bucket[0], false ),
          swing = ReadAxis( m_actuators.swing, true )
        };
        result.bucket_tip_pose = ReadBucketTipPose();
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
      m_simulation.DoStep();
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
              if ( line.Length > 4096 ) { writer.WriteLine( "{\"accepted\":false,\"reason\":\"line_too_long\"}" ); continue; }
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
