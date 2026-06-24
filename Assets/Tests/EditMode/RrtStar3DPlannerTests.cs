using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class RrtStar3DPlannerTests
  {
    [Test]
    public void PlanWithoutObstaclesReturnsDirectCollisionFreePath()
    {
      var settings = new RrtStar3DSettings {
        preferDirectPathWhenClear = true,
        collisionRadius = 0.1f,
        seed = 1
      };
      var planner = new RrtStar3DPlanner( settings );

      RrtStar3DResult result = planner.Plan(
        new Bounds( Vector3.zero, new Vector3( 20f, 20f, 20f ) ),
        new Vector3( -8f, 0f, 0f ),
        new Vector3( 8f, 0f, 0f ) );

      Assert.That( result.success, Is.True, result.summary );
      Assert.That( result.smoothedPath, Has.Length.EqualTo( 2 ) );
      Assert.That( result.smoothedPath[ 0 ], Is.EqualTo( new Vector3( -8f, 0f, 0f ) ) );
      Assert.That( result.smoothedPath[ 1 ], Is.EqualTo( new Vector3( 8f, 0f, 0f ) ) );
      Assert.That( planner.IsResultPathCollisionFree( result.smoothedPath ), Is.True );
    }

    [Test]
    public void PlanAroundSphereReturnsCollisionFreePath()
    {
      var settings = new RrtStar3DSettings {
        maxIterations = 3500,
        stepSize = 1.6f,
        goalBias = 0.18f,
        goalTolerance = 1.7f,
        rewireRadius = 3.5f,
        collisionRadius = 0.25f,
        edgeCheckResolution = 12,
        smoothingPasses = 4,
        seed = 11
      };
      var collision = new SphereCollisionChecker( Vector3.zero, 2.5f );
      var planner = new RrtStar3DPlanner( settings, collision );

      RrtStar3DResult result = planner.Plan(
        new Bounds( Vector3.zero, new Vector3( 20f, 16f, 20f ) ),
        new Vector3( -8f, 0f, 0f ),
        new Vector3( 8f, 0f, 0f ) );

      Assert.That( result.success, Is.True, result.summary );
      Assert.That( result.smoothedPath.Length, Is.GreaterThanOrEqualTo( 3 ) );
      Assert.That( planner.IsResultPathCollisionFree( result.rawPath ), Is.True );
      Assert.That( planner.IsResultPathCollisionFree( result.smoothedPath ), Is.True );
    }

    private sealed class SphereCollisionChecker : IRrtStar3DCollisionChecker
    {
      private readonly Vector3 m_center;
      private readonly float m_radius;

      public SphereCollisionChecker( Vector3 center, float radius )
      {
        m_center = center;
        m_radius = radius;
      }

      public bool IsPointValid( Vector3 point, float radius )
      {
        return Vector3.Distance( point, m_center ) > m_radius + radius;
      }

      public bool IsSegmentValid( Vector3 from, Vector3 to, float radius )
      {
        int steps = 24;
        for ( int i = 0; i <= steps; i++ ) {
          Vector3 point = Vector3.Lerp( from, to, (float)i / steps );
          if ( !IsPointValid( point, radius ) )
            return false;
        }

        return true;
      }
    }
  }
}
