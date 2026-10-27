using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class ContinuousPathMissionCourseTests
  {
    [Test]
    public void PrefixUsesActualStartAndPreservesCornersBeforeInterpolatedEnd()
    {
      Vector3 actualStart = Vector3.left;
      var points = new[] { Vector3.zero, Vector3.right, Vector3.right + Vector3.up * 2f };
      Vector3[] original = (Vector3[])points.Clone();
      Type type = Type.GetType( "RLExcavator.ContinuousPathMissionCourse, Assembly-CSharp", false );
      Assert.That( type, Is.Not.Null, "The training-only mission course must exist." );
      MethodInfo build = type.GetMethod( "BuildPrefix", BindingFlags.Public | BindingFlags.Static );
      Assert.That( build, Is.Not.Null );

      var result = (Vector3[])build.Invoke( null,
        new object[] { actualStart, points, 0.75f, Vector3.zero } );

      // Adjusted route is 2 m right followed by 2 m up. The first 75% ends 1 m up.
      Assert.That( result.Length, Is.EqualTo( 3 ) );
      AssertVector( result[ 0 ], actualStart );
      AssertVector( result[ 1 ], Vector3.right );
      AssertVector( result[ 2 ], Vector3.right + Vector3.up );
      CollectionAssert.AreEqual( original, points, "Reference generation must not mutate the route." );
    }

    [Test]
    public void FullReferenceConvertsRosAxesWithoutMovingTheActualStart()
    {
      var root = new GameObject( "mission-reference-test" );
      UnityEngine.Random.State randomState = UnityEngine.Random.state;
      try {
        Component course = root.AddComponent( CourseType() );
        var route = new[] { new Vector3( 1f, 2f, 3f ), new Vector3( 4f, 5f, 6f ),
                            new Vector3( 7f, 8f, 9f ) };
        SetField( course, "routeRosM", route );
        SetField( course, "routeJitterM", 0f );
        SetField( course, "fullPathProbability", 1f );
        Vector3 actualStart = Vector3.one * 0.5f;

        var result = (Vector3[])CourseType().GetMethod( "CreateReference" )
          .Invoke( course, new object[] { actualStart } );

        Assert.That( result.Length, Is.EqualTo( 3 ) );
        AssertVector( result[ 0 ], actualStart );
        AssertVector( result[ 1 ], new Vector3( -5f, 6f, 4f ) );
        AssertVector( result[ 2 ], new Vector3( -8f, 9f, 7f ) );
        AssertVector( route[ 1 ], new Vector3( 4f, 5f, 6f ) );
      }
      finally {
        UnityEngine.Random.state = randomState;
        UnityEngine.Object.DestroyImmediate( root );
      }
    }

    [Test]
    public void FullPrefixOffsetsOnlyReferenceWaypointsAndKeepsRepeatedPointsWellDefined()
    {
      var points = new[] { Vector3.zero, Vector3.zero, Vector3.right, Vector3.right + Vector3.up };
      var result = (Vector3[])CourseType().GetMethod( "BuildPrefix" ).Invoke( null,
        new object[] { Vector3.zero, points, 1f, Vector3.forward } );

      Assert.That( result.Length, Is.EqualTo( 4 ) );
      AssertVector( result[ 0 ], Vector3.zero );
      AssertVector( result[ 1 ], Vector3.forward );
      AssertVector( result[ 2 ], Vector3.right + Vector3.forward );
      AssertVector( result[ 3 ], Vector3.right + Vector3.up + Vector3.forward );
      var stationary = (Vector3[])CourseType().GetMethod( "BuildPrefix" ).Invoke( null,
        new object[] { Vector3.one, new[] { Vector3.one, Vector3.one }, 0.5f, Vector3.zero } );
      Assert.That( stationary.Length, Is.EqualTo( 1 ) );
      AssertVector( stationary[ 0 ], Vector3.one );
    }

    [TestCase(0f)]
    [TestCase(0.02f)]
    public void PrepareUsesProfileLimitsAndTheSameSampleForBothBoomCylinders( float jitter )
    {
      var root = new GameObject( "mission-pose-test" );
      root.SetActive( false ); // No native physics is needed to configure the next home-lock reset.
      UnityEngine.Random.State randomState = UnityEngine.Random.state;
      try {
        Component course = root.AddComponent( CourseType() );
        Type controllerType = Type.GetType( "RLExcavator.ExcavatorController, Assembly-CSharp", true );
        Component controller = root.AddComponent( controllerType );
        Type constraintType = controllerType.GetField( "swing" ).FieldType;
        foreach ( string axis in new[] { "boom", "stick", "bucket" } ) {
          var axisObject = new GameObject( axis );
          axisObject.transform.SetParent( root.transform );
          Component constraint = axisObject.AddComponent( constraintType );
          Array constraints = Array.CreateInstance( constraintType, axis == "boom" ? 2 : 1 );
          for ( int i = 0; i < constraints.Length; ++i )
            constraints.SetValue( constraint, i );
          SetField( controller, axis, constraints );
        }
        var swingObject = new GameObject( "swing" );
        swingObject.transform.SetParent( root.transform );
        SetField( controller, "swing", swingObject.AddComponent( constraintType ) );
        SetField( course, "startJointPose", new Vector4( -9f, 9f, 0.015f, 0.4f ) );
        SetField( course, "jointJitterM", jitter );
        SetField( course, "swingJitterRad", 0f );
        UnityEngine.Random.InitState( 51 );

        CourseType().GetMethod( "PrepareEpisode" ).Invoke( course, new object[] { controller } );

        MachineProfile profile = MachineProfileLoader.Load();
        float[] boom = GetField<float[]>( controller, "boomHomePose" );
        Assert.That( boom[ 0 ], Is.EqualTo( profile.GetActuator( "boom" ).RangeMin ) );
        Assert.That( boom[ 1 ], Is.EqualTo( boom[ 0 ] ), "Both boom cylinders need the same random sample." );
        Assert.That( GetField<float[]>( controller, "stickHomePose" )[ 0 ],
          Is.EqualTo( profile.GetActuator( "stick" ).RangeMax ) );
        Assert.That( GetField<float[]>( controller, "bucketHomePose" )[ 0 ],
          Is.InRange( 0.015f - jitter, 0.015f + jitter ) );
        Assert.That( GetField<float>( controller, "swingHomePose" ), Is.EqualTo( 0.4f ) );
        Assert.That( GetField<bool>( controller, "useInspectorHomePose" ), Is.True );
      }
      finally {
        UnityEngine.Random.state = randomState;
        UnityEngine.Object.DestroyImmediate( root );
      }
    }

    [Test]
    public void PrepareReportsMissingReferencesWithoutEnablingInspectorHomePose()
    {
      var root = new GameObject( "mission-missing-reference-test" );
      root.SetActive( false );
      try {
        Component course = root.AddComponent( CourseType() );
        MethodInfo prepare = CourseType().GetMethod( "PrepareEpisode" );
        var missingController = Assert.Throws<TargetInvocationException>(
          () => prepare.Invoke( course, new object[] { null } ) );
        Assert.That( missingController.InnerException, Is.TypeOf<ArgumentNullException>() );
        Component controller = root.AddComponent(
          Type.GetType( "RLExcavator.ExcavatorController, Assembly-CSharp", true ) );
        var missingBoom = Assert.Throws<TargetInvocationException>(
          () => prepare.Invoke( course, new object[] { controller } ) );
        Assert.That( missingBoom.InnerException, Is.TypeOf<InvalidOperationException>() );
        StringAssert.Contains( "boom", missingBoom.InnerException.Message );
        Assert.That( GetField<bool>( controller, "useInspectorHomePose" ), Is.False );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( root );
      }
    }

    private static Type CourseType()
      => Type.GetType( "RLExcavator.ContinuousPathMissionCourse, Assembly-CSharp", true );

    private static void SetField( object target, string name, object value )
      => target.GetType().GetField( name ).SetValue( target, value );

    private static T GetField<T>( object target, string name )
      => (T)target.GetType().GetField( name ).GetValue( target );

    private static void AssertVector( Vector3 actual, Vector3 expected )
      => Assert.That( Vector3.Distance( actual, expected ), Is.LessThan( 1e-6f ) );
  }
}
