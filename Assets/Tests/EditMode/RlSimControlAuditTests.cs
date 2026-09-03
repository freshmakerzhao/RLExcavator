using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class RlSimControlAuditTests
  {
    private static Vector4 s_lastAction;
    private static int s_lastSequence;
    private static double s_lastMonotonicSeconds;
    private static int s_decisionCount;
    private static string s_startedTraceRunId;
    private static string s_finishedTraceRunId;
    private static string s_finishedResult;

    [Test]
    public void TrajectoryAgentPublishesClampedCommandAtPolicyDecisionBoundary()
    {
      Type agentType = FindType( "RLExcavator.ExcavatorTrajectoryAgent" );
      var gameObject = new GameObject( "rl-sim-audit-policy-decision" );

      try {
        Component agent = gameObject.AddComponent( agentType );
        EventInfo decisionEvent = agentType.GetEvent( "PolicyActionReceived" );
        Assert.That( decisionEvent, Is.Not.Null );
        decisionEvent.AddEventHandler( agent, PolicyDecisionHandler( decisionEvent ) );
        ResetDecisionCapture();

        InvokePolicyAction( agentType, agent, 1.2f, -1.3f, 0.25f, -0.5f );
        Assert.That( s_lastAction, Is.EqualTo( new Vector4( 1f, -1f, 0.25f, -0.5f ) ) );
        InvokePolicyAction( agentType, agent, 0.1f, 0.2f, 0.3f, 0.4f );

        Assert.That( s_decisionCount, Is.EqualTo( 2 ) );
        Assert.That( s_lastAction, Is.EqualTo( new Vector4( 0.1f, 0.2f, 0.3f, 0.4f ) ) );
        Assert.That( s_lastSequence, Is.EqualTo( 1 ) );
        Assert.That( s_lastMonotonicSeconds, Is.GreaterThanOrEqualTo( 0.0 ) );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( gameObject );
      }
    }

    [Test]
    public void CycleTaskPublishesDistinctTraceIdentityForEachTrackingSegment()
    {
      Type cycleType = FindType( "RLExcavator.ExcavationCycleTask" );
      var gameObject = new GameObject( "rl-sim-audit-tracking-segment" );

      try {
        Component cycle = gameObject.AddComponent( cycleType );
        EventInfo started = cycleType.GetEvent( "TrackingSegmentStarted" );
        EventInfo finished = cycleType.GetEvent( "TrackingSegmentFinished" );
        Assert.That( started, Is.Not.Null );
        Assert.That( finished, Is.Not.Null );
        started.AddEventHandler( cycle, StringHandler( started, nameof( CaptureSegmentStart ) ) );
        finished.AddEventHandler( cycle, StringPairHandler( finished, nameof( CaptureSegmentFinish ) ) );
        s_startedTraceRunId = "";
        s_finishedTraceRunId = "";
        s_finishedResult = "";

        InvokePrivateVoid( cycleType, cycle, "BeginTrackingSegment" );
        string firstTraceRunId = s_startedTraceRunId;
        Assert.That( firstTraceRunId, Is.Not.Empty );
        cycleType.GetMethod( "StopTrackingControls" ).Invoke( cycle, new object[] { "interrupted" } );
        Assert.That( s_finishedTraceRunId, Is.EqualTo( firstTraceRunId ) );
        Assert.That( s_finishedResult, Is.EqualTo( "interrupted" ) );

        InvokePrivateVoid( cycleType, cycle, "BeginTrackingSegment" );
        Assert.That( s_startedTraceRunId, Is.Not.EqualTo( firstTraceRunId ) );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( gameObject );
      }
    }

    [Test]
    public void RecorderWritesOneStrictAuditRecordPerPolicyDecision()
    {
      using ( AuditFixture fixture = CreateAuditFixture( "valid", ValidSuite( "suite-001", "[0,1]" ) ) ) {
        EventInfo started = fixture.CycleType.GetEvent( "TrackingSegmentStarted" );
        s_startedTraceRunId = "";
        started.AddEventHandler( fixture.Cycle, StringHandler( started, nameof( CaptureSegmentStart ) ) );
        ActivateRecorder( fixture );

        InvokePrivateVoid( fixture.CycleType, fixture.Cycle, "BeginTrackingSegment" );
        InvokePolicyAction( fixture.AgentType, fixture.Agent, 0.2f, -0.3f, 0.4f, -0.5f );
        InvokePolicyAction( fixture.AgentType, fixture.Agent, 0.6f, -0.7f, 0.8f, -0.9f );
        fixture.CycleType.GetMethod( "StopTrackingControls" ).Invoke( fixture.Cycle, new object[] { "interrupted" } );
        FlushRecorder( fixture );

        string[] records = File.ReadAllLines( fixture.AuditPath );
        Assert.That( records, Has.Length.EqualTo( 3 ) );
        AssertRecordIdentity( records[ 0 ], fixture, s_startedTraceRunId, 0, 0 );
        Assert.That( records[ 1 ], Does.Contain( "\"sample_id\":1" ) );
        Assert.That( records[ 1 ], Does.Contain( "\"policy_action_seq\":1" ) );
        Assert.That( records[ 2 ], Does.Contain( "\"record_type\":\"terminal\"" ) );
        Assert.That( records[ 2 ], Does.Contain( "\"result\":\"interrupted\"" ) );
        Assert.That( records[ 2 ], Does.Contain( "\"runtime_monotonic_s\":" ) );
        Assert.That( records[ 2 ], Does.Contain( "\"elapsed_s\":" ) );
        Assert.That( records[ 2 ], Does.Not.Contain( "\"sample_id\"" ) );
        Assert.That( records[ 2 ], Does.Not.Contain( "\"policy_action_seq\"" ) );
      }
    }

    [Test]
    public void AuditIoFailureDoesNotInterruptLaterPolicyDecisions()
    {
      using ( AuditFixture fixture = CreateAuditFixture( "io-failure", ValidSuite( "suite-001", "[0,1]" ) ) ) {
        Directory.CreateDirectory( fixture.AuditPath );
        ActivateRecorder( fixture );
        EventInfo decisionEvent = fixture.AgentType.GetEvent( "PolicyActionReceived" );
        decisionEvent.AddEventHandler( fixture.Agent, PolicyDecisionHandler( decisionEvent ) );
        ResetDecisionCapture();

        InvokePrivateVoid( fixture.CycleType, fixture.Cycle, "BeginTrackingSegment" );
        InvokePolicyAction( fixture.AgentType, fixture.Agent, 0.1f, 0.2f, 0.3f, 0.4f );
        Assert.DoesNotThrow( () => FlushRecorder( fixture ) );
        InvokePolicyAction( fixture.AgentType, fixture.Agent, 0.2f, 0.3f, 0.4f, 0.5f );

        Assert.That( s_decisionCount, Is.EqualTo( 2 ) );
      }
    }

    [Test]
    public void RecorderRejectsTrajectorySuiteWithAdditionalTopLevelField()
    {
      const string suite = "{\"suite_id\":\"suite-001\",\"sample_period_s\":0.1,\"sample_ids\":[0],\"geometry\":{}}";
      using ( AuditFixture fixture = CreateAuditFixture( "strict-suite", suite ) ) {
        ActivateRecorder( fixture );
        InvokePrivateVoid( fixture.CycleType, fixture.Cycle, "BeginTrackingSegment" );
        InvokePolicyAction( fixture.AgentType, fixture.Agent, 0.1f, 0.2f, 0.3f, 0.4f );
        Assert.That( File.Exists( fixture.AuditPath ), Is.False );
      }
    }

    [Test]
    public void TrajectorySuiteSnapshotKeepsHashAndSamplesFromSameRead()
    {
      Type auditType = FindType( "RLExcavator.StrictRlSimControlAudit" );
      string directory = TemporaryDirectory( "suite-snapshot" );
      string suitePath = Path.Combine( directory, "suite.json" );
      string firstPayload = ValidSuite( "suite-a", "[0,1]" );

      try {
        Directory.CreateDirectory( directory );
        File.WriteAllText( suitePath, firstPayload );
        MethodInfo loader = auditType.GetMethod(
          "LoadStrictRlSimTrajectorySuiteSnapshot",
          BindingFlags.Static | BindingFlags.NonPublic );
        Assert.That( loader, Is.Not.Null );
        object snapshot = loader.Invoke( null, new object[] { suitePath } );
        File.WriteAllText( suitePath, ValidSuite( "suite-b", "[0,1]" ) );

        Assert.That( ReadField<string>( snapshot, "sha256" ), Is.EqualTo( Sha256Text( firstPayload ) ) );
        Assert.That( ReadField<double>( snapshot, "sample_period_s" ), Is.EqualTo( 0.1 ).Within( 1e-12 ) );
        Assert.That( ReadField<int[]>( snapshot, "sample_ids" ), Is.EqualTo( new[] { 0, 1 } ) );
      }
      finally {
        DeleteDirectory( directory );
      }
    }

    [Test]
    public void RecorderMarksAuditFailedWhenSuiteSampleIdsAreExhausted()
    {
      string suite = ValidSuite( "suite-001", "[0]" );
      using ( AuditFixture fixture = CreateAuditFixture( "suite-exhaustion", suite ) ) {
        ActivateRecorder( fixture );
        InvokePrivateVoid( fixture.CycleType, fixture.Cycle, "BeginTrackingSegment" );
        InvokePolicyAction( fixture.AgentType, fixture.Agent, 0.1f, 0.2f, 0.3f, 0.4f );
        InvokePolicyAction( fixture.AgentType, fixture.Agent, 0.2f, 0.3f, 0.4f, 0.5f );
        FlushRecorder( fixture );

        string[] records = File.ReadAllLines( fixture.AuditPath );
        Assert.That( records, Has.Length.EqualTo( 2 ) );
        Assert.That( records[ 1 ], Does.Contain( "\"status\":\"error\"" ) );
        Assert.That( records[ 1 ], Does.Contain( "trajectory_suite_sample_ids_exhausted" ) );
      }
    }

    [TestCase( "completed" )]
    [TestCase( "timeout" )]
    [TestCase( "interrupted" )]
    public void RecorderAcceptsNonEmptyContiguousSuitePrefixAtTerminal( string result )
    {
      string suite = ValidSuite( "suite-001", "[0,1]" );
      using ( AuditFixture fixture = CreateAuditFixture( "short-segment-" + result, suite ) ) {
        ActivateRecorder( fixture );
        InvokePrivateVoid( fixture.CycleType, fixture.Cycle, "BeginTrackingSegment" );
        InvokePolicyAction( fixture.AgentType, fixture.Agent, 0.1f, 0.2f, 0.3f, 0.4f );
        fixture.CycleType.GetMethod( "StopTrackingControls" ).Invoke( fixture.Cycle, new object[] { result } );
        FlushRecorder( fixture );

        string[] records = File.ReadAllLines( fixture.AuditPath );
        Assert.That( records, Has.Length.EqualTo( 2 ) );
        Assert.That( records[ 1 ], Does.Contain( "\"record_type\":\"terminal\"" ) );
        Assert.That( records[ 1 ], Does.Contain( "\"status\":\"terminal\"" ) );
        Assert.That( records[ 1 ], Does.Contain( "\"result\":\"" + result + "\"" ) );
        Assert.That( records[ 1 ], Does.Contain( "\"consumed_sample_count\":1" ) );
        Assert.That( records[ 1 ], Does.Contain( "\"suite_sample_count\":2" ) );
        Assert.That( records[ 1 ], Does.Not.Contain( "\"decision_count\"" ) );
        Assert.That( records[ 1 ], Does.Not.Contain( "trajectory_suite_decision_count_mismatch" ) );
      }
    }

    [Test]
    public void RecorderMarksZeroSampleTrackingSegmentAsAuditError()
    {
      string suite = ValidSuite( "suite-001", "[0,1]" );
      using ( AuditFixture fixture = CreateAuditFixture( "zero-sample-segment", suite ) ) {
        ActivateRecorder( fixture );
        InvokePrivateVoid( fixture.CycleType, fixture.Cycle, "BeginTrackingSegment" );
        fixture.CycleType.GetMethod( "StopTrackingControls" ).Invoke( fixture.Cycle, new object[] { "interrupted" } );
        FlushRecorder( fixture );

        string[] records = File.ReadAllLines( fixture.AuditPath );
        Assert.That( records, Has.Length.EqualTo( 2 ) );
        Assert.That( records[ 0 ], Does.Contain( "\"record_type\":\"audit_error\"" ) );
        Assert.That( records[ 0 ], Does.Contain( "tracking_segment_zero_policy_samples" ) );
        Assert.That( records[ 1 ], Does.Contain( "\"record_type\":\"terminal\"" ) );
        Assert.That( records[ 1 ], Does.Contain( "\"status\":\"error\"" ) );
      }
    }

    [TestCase( "[0,2]" )]
    [TestCase( "[1,2]" )]
    [TestCase( "[0,0]" )]
    public void RecorderRejectsSuiteUnlessSampleIdsAreExactlyZeroBasedContiguous( string sampleIds )
    {
      string suite = ValidSuite( "suite-001", sampleIds );
      using ( AuditFixture fixture = CreateAuditFixture( "non-contiguous-suite-" + sampleIds.GetHashCode(), suite ) ) {
        ActivateRecorder( fixture );
        InvokePrivateVoid( fixture.CycleType, fixture.Cycle, "BeginTrackingSegment" );
        InvokePolicyAction( fixture.AgentType, fixture.Agent, 0.1f, 0.2f, 0.3f, 0.4f );
        FlushRecorder( fixture );
        Assert.That( File.Exists( fixture.AuditPath ), Is.False );
      }
    }

    [TestCase( "0.2" )]
    [TestCase( "0.100001" )]
    public void RecorderRejectsSuiteUnlessSamplePeriodIsExactlyPointOne( string samplePeriod )
    {
      string suite = "{\"suite_id\":\"suite-001\",\"sample_period_s\":"
        + samplePeriod + ",\"sample_ids\":[0,1]}";
      using ( AuditFixture fixture = CreateAuditFixture( "invalid-period-" + samplePeriod, suite ) ) {
        ActivateRecorder( fixture );
        InvokePrivateVoid( fixture.CycleType, fixture.Cycle, "BeginTrackingSegment" );
        InvokePolicyAction( fixture.AgentType, fixture.Agent, 0.1f, 0.2f, 0.3f, 0.4f );
        FlushRecorder( fixture );
        Assert.That( File.Exists( fixture.AuditPath ), Is.False );
      }
    }

    private static void AssertRecordIdentity(
      string record,
      AuditFixture fixture,
      string traceRunId,
      int sampleId,
      int sequence )
    {
      Assert.That( record, Does.Contain( "\"schema_version\":\"rl_excavator_sim_control_audit.v1\"" ) );
      Assert.That( record, Does.Contain( "\"trajectory_suite_sha256\":\"" + Sha256( fixture.SuitePath ) + "\"" ) );
      Assert.That( record, Does.Contain( "\"trace_run_id\":\"" + traceRunId + "\"" ) );
      Assert.That( record, Does.Contain( "\"sample_id\":" + sampleId ) );
      Assert.That( record, Does.Contain( "\"policy_action_seq\":" + sequence ) );
      Assert.That( record, Does.Contain( "\"record_type\":\"policy_sample\"" ) );
      Assert.That( record, Does.Contain( "\"result\":\"active\"" ) );
      Assert.That( record, Does.Contain( "\"bucket_tip_ros_m\":[1.0,2.0,3.0]" ) );
      Assert.That( record, Does.Contain( "\"reference_waypoint_ros_m\":[4.0,5.0,6.0]" ) );
      Assert.That( record, Does.Contain( "\"waypoint_index\":0" ) );
      Assert.That( record, Does.Contain( "\"waypoint_distance_m\":5.196" ) );
      Assert.That( record, Does.Contain( "\"episode_progress\":0.0" ) );
    }

    private static string ValidSuite( string suiteId, string sampleIds )
    {
      return "{\"suite_id\":\"" + suiteId
        + "\",\"sample_period_s\":0.1,\"sample_ids\":" + sampleIds + "}";
    }

    private static AuditFixture CreateAuditFixture( string label, string suitePayload )
    {
      var fixture = new AuditFixture( TemporaryDirectory( label ) );
      Directory.CreateDirectory( fixture.Directory );
      File.WriteAllText( fixture.SuitePath, suitePayload );
      fixture.GameObject = new GameObject( "rl-sim-control-audit-" + label );
      fixture.Agent = fixture.GameObject.AddComponent( fixture.AgentType );
      fixture.Cycle = fixture.GameObject.AddComponent( fixture.CycleType );
      fixture.Recorder = fixture.GameObject.AddComponent( fixture.RecorderType );
      fixture.ConfigureTrackingGeometry();
      ConfigureAuditRecorder( fixture );
      return fixture;
    }

    private static void ConfigureAuditRecorder( AuditFixture fixture )
    {
      SetPublicField( fixture.RecorderType, fixture.Recorder, "cycleTask", fixture.Cycle );
      SetPublicField( fixture.RecorderType, fixture.Recorder, "agent", fixture.Agent );
      SetPublicField( fixture.RecorderType, fixture.Recorder, "recordOnCycleTask", false );
      SetPublicField( fixture.RecorderType, fixture.Recorder, "trajectorySuiteJsonPath", fixture.SuitePath );
      SetPublicField( fixture.RecorderType, fixture.Recorder, "controlAuditJsonlPath", fixture.AuditPath );
    }

    private static void ActivateRecorder( AuditFixture fixture )
    {
      InvokePrivateVoid( fixture.RecorderType, fixture.Recorder, "ResolveStrictRlSimControlAuditConfiguration" );
      InvokePrivateVoid( fixture.RecorderType, fixture.Recorder, "RefreshPolicyActionSubscription" );
      InvokePrivateVoid( fixture.RecorderType, fixture.Recorder, "RefreshCycleTaskSubscription" );
    }

    private static void FlushRecorder( AuditFixture fixture )
    {
      InvokePrivateVoid( fixture.RecorderType, fixture.Recorder, "LateUpdate" );
    }

    private static void CapturePolicyDecision( Vector4 action, int sequence, double monotonicSeconds )
    {
      s_lastAction = action;
      s_lastSequence = sequence;
      s_lastMonotonicSeconds = monotonicSeconds;
      s_decisionCount++;
    }

    private static void ResetDecisionCapture()
    {
      s_lastAction = Vector4.zero;
      s_lastSequence = -1;
      s_lastMonotonicSeconds = -1.0;
      s_decisionCount = 0;
    }

    private static void CaptureSegmentStart( string traceRunId )
    {
      s_startedTraceRunId = traceRunId;
    }

    private static void CaptureSegmentFinish( string traceRunId, string result )
    {
      s_finishedTraceRunId = traceRunId;
      s_finishedResult = result;
    }

    private static Delegate PolicyDecisionHandler( EventInfo eventInfo )
    {
      MethodInfo method = typeof( RlSimControlAuditTests ).GetMethod(
        nameof( CapturePolicyDecision ),
        BindingFlags.Static | BindingFlags.NonPublic );
      return Delegate.CreateDelegate( eventInfo.EventHandlerType, method );
    }

    private static Delegate StringHandler( EventInfo eventInfo, string methodName )
    {
      MethodInfo method = typeof( RlSimControlAuditTests ).GetMethod(
        methodName,
        BindingFlags.Static | BindingFlags.NonPublic );
      return Delegate.CreateDelegate( eventInfo.EventHandlerType, method );
    }

    private static Delegate StringPairHandler( EventInfo eventInfo, string methodName )
    {
      MethodInfo method = typeof( RlSimControlAuditTests ).GetMethod(
        methodName,
        BindingFlags.Static | BindingFlags.NonPublic );
      return Delegate.CreateDelegate( eventInfo.EventHandlerType, method );
    }

    private static void InvokePrivateVoid( Type type, object target, string methodName )
    {
      MethodInfo method = type.GetMethod( methodName, BindingFlags.Instance | BindingFlags.NonPublic );
      Assert.That( method, Is.Not.Null );
      method.Invoke( target, null );
    }

    private static void SetPublicField( Type type, object target, string fieldName, object value )
    {
      FieldInfo field = type.GetField( fieldName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( field, Is.Not.Null );
      field.SetValue( target, value );
    }

    private static void InvokePolicyAction( Type agentType, object agent, params float[] values )
    {
      object actions = CreateContinuousActionBuffer( values );
      try {
        agentType.GetMethod( "OnActionReceived", BindingFlags.Instance | BindingFlags.Public )
          .Invoke( agent, new[] { actions } );
      }
      catch ( TargetInvocationException exception ) when ( exception.InnerException is NullReferenceException ) {
        // The isolated decision-boundary test intentionally omits the reward graph.
      }
    }

    private static object CreateContinuousActionBuffer( params float[] values )
    {
      Type actionBuffersType = FindType( "Unity.MLAgents.Actuators.ActionBuffers" );
      return Activator.CreateInstance( actionBuffersType, new object[] { values, Array.Empty<int>() } );
    }

    private static Type FindType( string fullName )
    {
      foreach ( Assembly assembly in AppDomain.CurrentDomain.GetAssemblies() ) {
        Type type = assembly.GetType( fullName );
        if ( type != null )
          return type;
      }

      Assert.Fail( "Type not found: " + fullName );
      return null;
    }

    private static T ReadField<T>( object target, string fieldName )
    {
      FieldInfo field = target.GetType().GetField( fieldName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( field, Is.Not.Null );
      return (T)field.GetValue( target );
    }

    private static string Sha256( string path )
    {
      return Sha256Bytes( File.ReadAllBytes( path ) );
    }

    private static string Sha256Text( string value )
    {
      return Sha256Bytes( Encoding.UTF8.GetBytes( value ) );
    }

    private static string Sha256Bytes( byte[] bytes )
    {
      using ( SHA256 sha = SHA256.Create() )
        return BitConverter.ToString( sha.ComputeHash( bytes ) ).Replace( "-", "" ).ToLowerInvariant();
    }

    private static string TemporaryDirectory( string label )
    {
      return Path.Combine( Application.temporaryCachePath, label + "-" + Guid.NewGuid().ToString( "N" ) );
    }

    private static void DeleteDirectory( string directory )
    {
      if ( Directory.Exists( directory ) )
        Directory.Delete( directory, true );
    }

    private sealed class AuditFixture : IDisposable
    {
      public readonly string Directory;
      public readonly string SuitePath;
      public readonly string AuditPath;
      public readonly Type AgentType = FindType( "RLExcavator.ExcavatorTrajectoryAgent" );
      public readonly Type CycleType = FindType( "RLExcavator.ExcavationCycleTask" );
      public readonly Type RecorderType = FindType( "RLExcavator.OpenLoopVelocityRecorder" );
      public readonly Type TrajectoryType = FindType( "RLExcavator.TrajectoryProvider" );
      public readonly Type FrameRegistryType = FindType( "RLExcavator.ExcavatorFrameRegistry" );
      public GameObject GameObject;
      public Component Agent;
      public Component Cycle;
      public Component Recorder;
      public Component Trajectory;
      public Component FrameRegistry;

      public AuditFixture( string directory )
      {
        Directory = directory;
        SuitePath = Path.Combine( directory, "suite.json" );
        AuditPath = Path.Combine( directory, "audit.jsonl" );
      }

      public void ConfigureTrackingGeometry()
      {
        Trajectory = GameObject.AddComponent( TrajectoryType );
        FrameRegistry = GameObject.AddComponent( FrameRegistryType );
        Transform tip = ChildAt( "BucketTip", new Vector3( -2f, 3f, 1f ) );
        Transform waypoint = ChildAt( "Waypoint", new Vector3( -5f, 6f, 4f ) );
        SetPublicField( TrajectoryType, Trajectory, "waypoints", new[] { waypoint } );
        SetPublicField( FrameRegistryType, FrameRegistry, "machineRoot", GameObject.transform );
        SetPublicField( FrameRegistryType, FrameRegistry, "bucketTip", tip );
        SetPublicField( AgentType, Agent, "bucketTip", tip );
        SetPublicField( AgentType, Agent, "trajectory", Trajectory );
        SetPublicField( AgentType, Agent, "frameRegistry", FrameRegistry );
      }

      private Transform ChildAt( string name, Vector3 localPosition )
      {
        var child = new GameObject( name );
        child.transform.SetParent( GameObject.transform, false );
        child.transform.localPosition = localPosition;
        return child.transform;
      }

      public void Dispose()
      {
        if ( GameObject != null )
          UnityEngine.Object.DestroyImmediate( GameObject );
        DeleteDirectory( Directory );
      }
    }
  }
}
