using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class ContinuousPathTrackingTests
  {
    [Test]
    public void GeometricCornerCostDoesNotConfuseProjectionLagWithContourError()
    {
      var useGeometry = m_tracking.GetType().GetField( "useGeometricContourCost" );
      Assert.That( useGeometry, Is.Not.Null, "Refinement needs a separate geometric training cost." );
      Assert.That( useGeometry.GetValue( m_tracking ), Is.False, "Existing scenes retain their original reward." );
      useGeometry.SetValue( m_tracking, true );
      m_tracking.GetType().GetField( "cornerContourGain" ).SetValue( m_tracking, 2f );
      m_tracking.GetType().GetField( "contourCostPerSecond" ).SetValue( m_tracking, 1f );
      Reset( Vector3.zero, Vector3.right, Vector3.right + Vector3.up );
      Step( Vector3.right * 0.92f );
      Step( new Vector3( 0.96f, 0.08f, 0f ) );
      Assert.That( Property<float>( "GeometricCrossTrackDistanceM" ), Is.EqualTo( 0.04f ).Within( 1e-5f ) );
      Assert.That( Property<float>( "CrossTrackDistanceM" ), Is.GreaterThan( 0.045f ),
        "Ordered progress remains bounded; changing the cost must not hide its arc lag." );
      Assert.That( Property<float>( "LastContourPenalty" ), Is.EqualTo( -0.04f ).Within( 1e-5f ),
        "True distance 4cm, corner weight 1.6 and elapsed 0.1s determine the cost." );
      Assert.That( Property<bool>( "Completed" ), Is.False );
    }

    private GameObject m_object;
    private Component m_tracking;

    [SetUp]
    public void SetUp()
    {
      Type type = null;
      foreach ( Assembly assembly in AppDomain.CurrentDomain.GetAssemblies() )
        type = type ?? assembly.GetType( "RLExcavator.ContinuousPathTracking" );
      Assert.That( type, Is.Not.Null, "The isolated continuous-path training component must exist." );
      m_object = new GameObject( "ContinuousPathTrackingTest" );
      m_tracking = m_object.AddComponent( type );
      Reset( Vector3.zero, Vector3.right, Vector3.right * 2f );
    }

    [TearDown]
    public void TearDown()
    {
      if ( m_object != null )
        UnityEngine.Object.DestroyImmediate( m_object );
    }

    [Test]
    public void AligningWorkspaceReferenceKeepsItsArcAndUsesThePhysicalStart()
    {
      var method = m_tracking.GetType().GetMethod( "AlignTrainingReferenceStart" );
      Assert.That( method, Is.Not.Null, "Long references must retain the workspace arc rather than a Cartesian chord." );
      var original = new[] { Vector3.forward, new Vector3( 0.7071f, 0f, 0.7071f ), Vector3.right };
      Vector3 actualStart = Vector3.forward + Vector3.up * 0.05f;
      var aligned = (Vector3[])method.Invoke( null, new object[] { original, actualStart } );
      Assert.That( aligned[ 0 ], Is.EqualTo( actualStart ) );
      Assert.That( aligned[ 2 ], Is.EqualTo( original[ 2 ] ) );
      Assert.That( new Vector2( aligned[ 1 ].x, aligned[ 1 ].z ).magnitude,
        Is.GreaterThan( 0.99f ), "The intermediate arc must not collapse toward the chord's 0.707 m radius." );
      Assert.That( aligned[ 1 ].y, Is.EqualTo( 0.025f ).Within( 1e-6f ) );
      Assert.That( original[ 0 ], Is.EqualTo( Vector3.forward ), "Do not mutate the sampled path." );
    }

    [Test]
    public void PassingAnInteriorVertexDoesNotCompleteOrRequireStopping()
    {
      for ( int i = 1; i <= 12; i++ )
        Step( Vector3.right * ( i * 0.1f ) );
      Assert.That( Property<bool>( "Completed" ), Is.False );
      Assert.That( Property<float>( "Progress" ), Is.GreaterThan( 0.5f ) );
    }

    [Test]
    public void EndpointRequiresLowPhysicalSpeedAndCorrectPitch()
    {
      for ( int i = 1; i <= 20; i++ )
        Step( Vector3.right * ( i * 0.1f ), pitchError: 45f );
      Assert.That( Property<bool>( "Completed" ), Is.False );
      for ( int i = 0; i < 4; i++ )
        Step( Vector3.right * 2f, pitchError: 45f );
      Assert.That( Property<bool>( "Completed" ), Is.False, "Position alone must not complete a skill handoff." );
      for ( int i = 0; i < 4; i++ )
        Step( Vector3.right * 2f );
      Assert.That( Property<bool>( "Completed" ), Is.True );
    }

    [Test]
    public void StationaryTipCannotAccumulatePathProgressReward()
    {
      Step( Vector3.right * 0.1f );
      Assert.That( Property<float>( "LastProgressReward" ), Is.GreaterThan( 0f ) );
      Step( Vector3.right * 0.1f );
      Assert.That( Property<float>( "LastProgressReward" ), Is.Zero.Within( 1e-6f ) );
    }

    [Test]
    public void ActionChangePenaltyIsAppliedOnlyToFreshPolicyDecisions()
    {
      Step( Vector3.zero, action: Vector4.one );
      float changed = Property<float>( "LastSmoothPenalty" );
      Assert.That( changed, Is.LessThan( 0f ) );
      Step( Vector3.zero, action: Vector4.one, newDecision: false );
      Assert.That( Property<float>( "LastSmoothPenalty" ), Is.Zero );
      Step( Vector3.zero, action: Vector4.zero );
      Assert.That( Property<float>( "LastSmoothPenalty" ), Is.EqualTo( changed ).Within( 1e-6f ) );
    }

    [Test]
    public void ContinuousCostsIntegrateByElapsedTime()
    {
      float oneStep = Step( Vector3.zero, pitchError: 20f, dt: 0.1f );
      Reset( Vector3.zero, Vector3.right, Vector3.right * 2f );
      float fiveSteps = 0f;
      for ( int i = 0; i < 5; i++ )
        fiveSteps += Step( Vector3.zero, pitchError: 20f, dt: 0.02f );
      Assert.That( fiveSteps, Is.EqualTo( oneStep ).Within( 1e-6f ) );
    }

    [Test]
    public void PitchImprovementIsSignedAndCannotBeFarmedByOscillation()
    {
      Assert.That( m_tracking.GetType().GetProperty( "LastPitchProgressReward" ), Is.Not.Null );
      Step( Vector3.zero, pitchError: 60f );
      Assert.That( Property<float>( "LastPitchProgressReward" ), Is.Zero, "First sample only establishes the baseline." );
      Step( Vector3.zero, pitchError: 30f );
      float improvement = Property<float>( "LastPitchProgressReward" );
      Assert.That( improvement, Is.GreaterThan( 0f ) );
      Step( Vector3.zero, pitchError: 60f );
      Assert.That( Property<float>( "LastPitchProgressReward" ), Is.EqualTo( -improvement ).Within( 1e-6f ) );
      Step( Vector3.zero, pitchError: -60f );
      Assert.That( Property<float>( "LastPitchProgressReward" ), Is.Zero, "Equal absolute error is not improvement." );
      Assert.That( Property<bool>( "Completed" ), Is.False, "Better pitch alone cannot finish a path." );
    }

    [Test]
    public void PitchImprovementDoesNotDependOnStepDurationOrLeakAcrossEpisodes()
    {
      Assert.That( m_tracking.GetType().GetProperty( "LastPitchProgressReward" ), Is.Not.Null );
      Step( Vector3.zero, pitchError: 60f );
      Step( Vector3.zero, pitchError: 20f, dt: 0.1f );
      float whole = Property<float>( "LastPitchProgressReward" );
      Reset( Vector3.zero, Vector3.right );
      Step( Vector3.zero, pitchError: 60f );
      Assert.That( Property<float>( "LastPitchProgressReward" ), Is.Zero );
      float split = 0f;
      for ( int i = 1; i <= 5; i++ ) {
        Step( Vector3.zero, pitchError: 60f - 8f * i, dt: 0.02f );
        split += Property<float>( "LastPitchProgressReward" );
      }
      Assert.That( split, Is.EqualTo( whole ).Within( 1e-6f ) );
    }

    [TestCase( false )]
    [TestCase( true )]
    public void PathObservationHasTwentyOneValuesIncludingDuringPoseReset( bool invalidated )
    {
      if ( invalidated )
        m_tracking.GetType().GetMethod( "InvalidatePath" ).Invoke( m_tracking, null );
      Type sensorType = FindType( "Unity.MLAgents.Sensors.VectorSensor" );
      Type observationType = FindType( "Unity.MLAgents.Sensors.ObservationType" );
      object sensor = Activator.CreateInstance( sensorType,
        new object[] { 21, "continuous-path-test", Enum.ToObject( observationType, 0 ) } );
      m_tracking.GetType().GetMethod( "AddPathObservations" ).Invoke(
        m_tracking, new object[] { sensor, Vector3.zero, 1f } );
      var values = (ICollection)sensorType.GetMethod( "GetObservations", BindingFlags.Instance | BindingFlags.NonPublic )
        .Invoke( sensor, null );
      Assert.That( values.Count, Is.EqualTo( 21 ) );
      if ( invalidated )
        foreach ( float value in values )
          Assert.That( value, Is.Zero );
    }

    [Test]
    public void TrainingReferenceStartsAtActualTipAndNeverTurnsBackAlongItsChord()
    {
      MethodInfo method = m_tracking.GetType().GetMethod( "CreateTrainingReferencePath", BindingFlags.Static | BindingFlags.Public );
      Assert.That( method, Is.Not.Null, "The random training reference must be generated from the actual reset tip." );
      Assert.That( m_tracking.GetType().GetField( "generateTrainingReference" ).GetValue( m_tracking ), Is.False,
        "Ordinary path tracking must not rewrite supplied paths by default." );
      Vector3 start = new Vector3( 0.15f, 0.45f, 0.75f );
      Vector3 end = new Vector3( 0.25f, 0.50f, 0.65f );
      var points = (Vector3[])method.Invoke( null,
        new object[] { start, end, 5, new Vector3( 2f, 3f, 4f ), 0.1f } );

      Assert.That( points.Length, Is.EqualTo( 5 ) );
      Assert.That( Vector3.Distance( points[ 0 ], start ), Is.LessThan( 1e-6f ) );
      Assert.That( Vector3.Distance( points[ points.Length - 1 ], end ), Is.LessThan( 1e-6f ) );
      Vector3 chord = end - start;
      for ( int i = 1; i < points.Length; i++ ) {
        Vector3 segment = points[ i ] - points[ i - 1 ];
        Assert.That( Vector3.Dot( segment, chord ), Is.GreaterThan( 0f ), "A short curriculum path must not begin with a reversal." );
        if ( i > 1 )
          Assert.That( Vector3.Angle( points[ i - 1 ] - points[ i - 2 ], segment ), Is.LessThan( 15f ) );
      }
    }

    private void Reset( params Vector3[] points )
    {
      m_tracking.GetType().GetMethod( "ResetPath" ).Invoke( m_tracking, new object[] { points } );
    }

    private float Step( Vector3 tip, float pitchError = 0f, Vector4 action = default,
                        float dt = 0.1f, bool newDecision = true )
    {
      return (float)m_tracking.GetType().GetMethod( "EvaluateStep" ).Invoke(
        m_tracking, new object[] { tip, pitchError, action, dt, newDecision } );
    }

    private T Property<T>( string name )
    {
      return (T)m_tracking.GetType().GetProperty( name ).GetValue( m_tracking );
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
  }
}
