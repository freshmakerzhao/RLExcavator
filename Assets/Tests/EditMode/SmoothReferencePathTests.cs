using System;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class SmoothReferencePathTests
  {
    [Test]
    public void RoundingPreservesEndpointsWithoutRequiringTheOriginalCorner()
    {
      var route = new[] { Vector3.zero, Vector3.right, Vector3.right + Vector3.up };
      var original = (Vector3[])route.Clone();

      Vector3[] result = Build( route, 0.2f, 0.01f );

      AssertVector( result[ 0 ], route[ 0 ] );
      AssertVector( result[ result.Length - 1 ], route[ 2 ] );
      float nearestCorner = float.PositiveInfinity;
      foreach ( Vector3 point in result )
        nearestCorner = Mathf.Min( nearestCorner, Vector3.Distance( point, route[ 1 ] ) );
      Assert.That( nearestCorner, Is.GreaterThan( 0.05f ), "Round the bend instead of retaining a waypoint stop." );
      CollectionAssert.AreEqual( original, route );
    }

    [Test]
    public void ZeroCutReturnsAnIndependentCopyOfTheOriginalPoints()
    {
      var route = new[] { Vector3.zero, Vector3.right, Vector3.right, Vector3.up };
      Vector3[] result = Build( route, 0f, 0.01f );

      CollectionAssert.AreEqual( route, result );
      Assert.That( result, Is.Not.SameAs( route ) );
    }

    [Test]
    public void StraightReferenceKeepsItsGeometryAndWorksWithTheExistingArcLengthPath()
    {
      Vector3[] result = Build( new[] { Vector3.zero, Vector3.right, Vector3.right * 2f }, 0.2f, 0.01f );
      foreach ( Vector3 point in result ) {
        Assert.That( point.y, Is.Zero );
        Assert.That( point.z, Is.Zero );
        Assert.That( point.x, Is.InRange( 0f, 2f ) );
      }
      Type pathType = Type.GetType( "RLExcavator.ContinuousReferencePath, Assembly-CSharp", true );
      object path = Activator.CreateInstance( pathType, new object[] { result } );
      Assert.That( (float)pathType.GetProperty( "Length" ).GetValue( path ), Is.EqualTo( 2f ).Within( 1e-5f ) );
      AssertVector( (Vector3)pathType.GetMethod( "PointAt" ).Invoke( path, new object[] { 1.25f } ),
                    Vector3.right * 1.25f );
      AssertSampleSpacing( result, 0.01f );
    }

    [Test]
    public void ShortAdjacentCornersStayFiniteInsideTheirLocalBounds()
    {
      var route = new[] { Vector3.zero, Vector3.right * 0.02f,
        new Vector3( 0.02f, 0.01f, 0f ), new Vector3( 0.03f, 0.01f, 0f ) };
      Vector3[] result = Build( route, 10f, 0.001f );

      AssertVector( result[ 0 ], route[ 0 ] );
      AssertVector( result[ result.Length - 1 ], route[ 3 ] );
      foreach ( Vector3 point in result ) {
        Assert.That( point.x, Is.InRange( -1e-7f, 0.0300001f ) );
        Assert.That( point.y, Is.InRange( -1e-7f, 0.0100001f ) );
        Assert.That( point.z, Is.Zero );
      }
      AssertSampleSpacing( result, 0.001f );
    }

    [Test]
    public void SampledCurveJoinsTheStraightSegmentsWithNearlyTheSameTangent()
    {
      Vector3[] result = Build( new[] { Vector3.zero, Vector3.right, Vector3.right + Vector3.up },
                                0.2f, 0.005f );
      int entry = Array.FindIndex( result, point => Vector3.Distance( point, Vector3.right * 0.8f ) < 1e-6f );
      int exit = Array.FindIndex( result, point => Vector3.Distance( point, Vector3.right + Vector3.up * 0.2f ) < 1e-6f );

      Assert.That( entry, Is.GreaterThan( 0 ) );
      Assert.That( exit, Is.LessThan( result.Length - 1 ) );
      Assert.That( Vector3.Angle( result[ entry ] - result[ entry - 1 ], result[ entry + 1 ] - result[ entry ] ),
        Is.LessThan( 1f ) );
      Assert.That( Vector3.Angle( result[ exit ] - result[ exit - 1 ], result[ exit + 1 ] - result[ exit ] ),
        Is.LessThan( 1f ) );
      for ( int i = 1; i < result.Length - 1; ++i )
        Assert.That( Vector3.Angle( result[ i ] - result[ i - 1 ], result[ i + 1 ] - result[ i ] ), Is.LessThan( 3f ) );
    }

    [Test]
    public void RepeatedPointsAndStationaryRoutesRemainWellDefined()
    {
      Vector3[] result = Build( new[] { Vector3.zero, Vector3.zero, Vector3.right }, 0.2f, 0.01f );
      AssertVector( result[ 0 ], Vector3.zero );
      AssertVector( result[ result.Length - 1 ], Vector3.right );
      AssertSampleSpacing( result, 0.01f );
      CollectionAssert.AreEqual( new[] { Vector3.up }, Build( new[] { Vector3.up, Vector3.up }, 0.2f, 0.01f ) );
    }

    [Test]
    public void ReversalKeepsTheOriginalTurnRatherThanInventingADetour()
    {
      Vector3[] result = Build( new[] { Vector3.zero, Vector3.right, Vector3.zero }, 0.2f, 0.01f );
      Assert.That( Array.Exists( result, point => Vector3.Distance( point, Vector3.right ) < 1e-6f ), Is.True );
      foreach ( Vector3 point in result ) {
        Assert.That( point.x, Is.InRange( 0f, 1f ) );
        Assert.That( point.y, Is.Zero );
        Assert.That( point.z, Is.Zero );
      }
    }

    private static Vector3[] Build( Vector3[] points, float cutDistance, float sampleSpacing )
    {
      Type type = Type.GetType( "RLExcavator.SmoothReferencePath, Assembly-CSharp", false );
      Assert.That( type, Is.Not.Null, "The reference curve builder must exist." );
      var build = type.GetMethod( "Build", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static );
      Assert.That( build, Is.Not.Null );
      return (Vector3[])build.Invoke( null, new object[] { points, cutDistance, sampleSpacing } );
    }

    private static void AssertVector( Vector3 actual, Vector3 expected )
      => Assert.That( Vector3.Distance( actual, expected ), Is.LessThan( 1e-6f ) );

    private static void AssertSampleSpacing( Vector3[] points, float spacing )
    {
      for ( int i = 1; i < points.Length; ++i )
        Assert.That( Vector3.Distance( points[ i - 1 ], points[ i ] ), Is.InRange( 1e-8f, spacing + 1e-6f ) );
    }
  }
}
