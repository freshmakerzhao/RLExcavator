using System;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class ContinuousReferencePathTests
  {
    [Test]
    public void CornerImportanceFollowsTurnAngleAndFadesWithoutChangingThePath()
    {
      object bent = Path( Vector3.zero, Vector3.right, Vector3.right + Vector3.up );
      var importance = bent.GetType().GetMethod( "CornerImportance" );
      Assert.That( importance, Is.Not.Null, "Training should identify bends without adding waypoint stops." );
      Assert.That( (float)importance.Invoke( bent, new object[] { 1f, 0.2f } ), Is.EqualTo( 0.5f ).Within( 1e-6f ) );
      Assert.That( (float)importance.Invoke( bent, new object[] { 0.9f, 0.2f } ), Is.EqualTo( 0.25f ).Within( 1e-6f ) );
      Assert.That( (float)importance.Invoke( bent, new object[] { 0.5f, 0.2f } ), Is.Zero );
      object straight = Path( Vector3.zero, Vector3.right, Vector3.right * 2f );
      Assert.That( (float)importance.Invoke( straight, new object[] { 1f, 0.2f } ), Is.Zero );
      AssertVector( Sample( bent, "PointAt", 1f ), Vector3.right );
    }

    [Test]
    public void ArcLengthSamplingPreservesTheOriginalCorner()
    {
      object path = Path( Vector3.zero, Vector3.right, Vector3.right + Vector3.up );
      Assert.That( Property<float>( path, "Length" ), Is.EqualTo( 2f ).Within( 1e-6f ) );
      AssertVector( Sample( path, "PointAt", 0.5f ), new Vector3( 0.5f, 0f, 0f ) );
      AssertVector( Sample( path, "PointAt", 1.5f ), new Vector3( 1f, 0.5f, 0f ) );
      AssertVector( Sample( path, "TangentAt", 1.5f ), Vector3.up );
    }

    [Test]
    public void SamplingClampsAtEndpointsAndDoesNotRetainTheInputArray()
    {
      var points = new[] { Vector3.zero, Vector3.right, Vector3.right + Vector3.up };
      object path = Path( points );
      points[ 1 ] = Vector3.one * 100f;

      AssertVector( Sample( path, "PointAt", -1f ), Vector3.zero );
      AssertVector( Sample( path, "PointAt", 10f ), Vector3.right + Vector3.up );
      AssertVector( Sample( path, "TangentAt", -1f ), Vector3.right );
      AssertVector( Sample( path, "TangentAt", 10f ), Vector3.up );
      Assert.That( Property<float>( path, "Length" ), Is.EqualTo( 2f ).Within( 1e-6f ) );
    }

    [Test]
    public void ProjectionPassesACornerWithoutReachingTheVertex()
    {
      object path = Path( Vector3.zero, Vector3.right, Vector3.right + Vector3.up );
      object projection = Project( path, new Vector3( 0.92f, 0.15f, 0f ), 0.9f, 0.4f );

      Assert.That( Property<float>( projection, "ArcLength" ), Is.EqualTo( 1.15f ).Within( 1e-6f ) );
      Assert.That( Property<float>( projection, "Distance" ), Is.EqualTo( 0.08f ).Within( 1e-6f ) );
      AssertVector( Property<Vector3>( projection, "Tangent" ), Vector3.up );
    }

    [Test]
    public void ProjectionCannotJumpToADistantCrossingSegment()
    {
      object path = Path( Vector3.left, Vector3.right, Vector3.right + Vector3.up,
                          Vector3.left + Vector3.down );
      object projection = Project( path, new Vector3( 0.05f, 0.05f, 0f ), 0.9f, 0.3f );

      // 后面的对角线经过该位置，但只允许搜索当前弧长窗口。
      Assert.That( Property<float>( projection, "ArcLength" ), Is.EqualTo( 1.05f ).Within( 1e-6f ) );
      Assert.That( Property<float>( projection, "Distance" ), Is.EqualTo( 0.05f ).Within( 1e-6f ) );
      AssertVector( Property<Vector3>( projection, "Point" ), new Vector3( 0.05f, 0f, 0f ) );
    }

    [Test]
    public void ProjectionAllowsBackwardMotionAndRespectsTheTravelBound()
    {
      object path = Path( Vector3.zero, Vector3.right );
      object backward = Project( path, Vector3.right * 0.5f, 0.8f, 0.4f );
      object limited = Project( path, Vector3.right * 0.9f, 0.2f, 0.1f );

      Assert.That( Property<float>( backward, "ArcLength" ), Is.EqualTo( 0.5f ).Within( 1e-6f ) );
      Assert.That( Property<float>( limited, "ArcLength" ), Is.EqualTo( 0.3f ).Within( 1e-6f ) );
    }

    [Test]
    public void ZeroTravelBoundCannotAdvanceEvenWhenTheTipIsFurtherAhead()
    {
      object path = Path( Vector3.zero, Vector3.right );
      object projection = Project( path, Vector3.right, 0.2f, 0f );

      Assert.That( Property<float>( projection, "ArcLength" ), Is.EqualTo( 0.2f ).Within( 1e-6f ) );
    }

    [Test]
    public void RepeatedPointsAndAStationaryPathRemainWellDefined()
    {
      object path = Path( Vector3.zero, Vector3.zero, Vector3.right );
      Assert.That( Property<float>( path, "Length" ), Is.EqualTo( 1f ) );

      object stationary = Path( Vector3.up, Vector3.up );
      object projection = Project( stationary, Vector3.up + Vector3.right, 0f, 0.2f );
      Assert.That( Property<float>( stationary, "Length" ), Is.Zero );
      AssertVector( Sample( stationary, "PointAt", 10f ), Vector3.up );
      AssertVector( Sample( stationary, "TangentAt", 0f ), Vector3.zero );
      Assert.That( Property<float>( projection, "Distance" ), Is.EqualTo( 1f ) );
    }

    private static object Path( params Vector3[] points )
    {
      Type type = Type.GetType( "RLExcavator.ContinuousReferencePath, Assembly-CSharp", true );
      return Activator.CreateInstance( type, new object[] { points } );
    }

    private static T Property<T>( object value, string name )
      => (T)value.GetType().GetProperty( name ).GetValue( value );

    private static Vector3 Sample( object path, string method, float arcLength )
      => (Vector3)path.GetType().GetMethod( method ).Invoke( path, new object[] { arcLength } );

    private static object Project( object path, Vector3 position, float previousArc, float maxAdvance )
      => path.GetType().GetMethod( "Project" ).Invoke( path, new object[] { position, previousArc, maxAdvance } );

    private static void AssertVector( Vector3 actual, Vector3 expected )
      => Assert.That( Vector3.Distance( actual, expected ), Is.LessThan( 1e-6f ) );
  }
}
