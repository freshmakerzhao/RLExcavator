using System;
using System.Reflection;
using NUnit.Framework;

namespace RLExcavator.Tests
{
  public class TrajectoryTerminalStabilizationTests
  {
    [Test]
    public void PenaltyIsInactiveOutsideApproachZone()
    {
      Assert.That(
        Penalty(
          distance: 0.41f,
          approachDistance: 0.40f,
          normalizedSwingVelocity: 1f,
          swingAction: 1f,
          velocityWeight: 0.20f,
          driveWeight: 0.10f ),
        Is.Zero );
    }

    [Test]
    public void PenaltyGrowsAsAHighSpeedArrivalApproachesTarget()
    {
      float farPenalty = Penalty( 0.30f, 0.40f, 1f, 1f, 0.20f, 0.10f );
      float nearPenalty = Penalty( 0.10f, 0.40f, 1f, 1f, 0.20f, 0.10f );

      Assert.That( farPenalty, Is.LessThan( 0f ) );
      Assert.That( nearPenalty, Is.LessThan( farPenalty ) );
    }

    [Test]
    public void BrakingActionCostsLessThanDrivingWithExistingSwingMotion()
    {
      float keepDriving = Penalty( 0.10f, 0.40f, 0.8f, 0.8f, 0.20f, 0.10f );
      float braking = Penalty( 0.10f, 0.40f, 0.8f, -0.8f, 0.20f, 0.10f );

      Assert.That( braking, Is.LessThan( 0f ) );
      Assert.That( braking, Is.GreaterThan( keepDriving ) );
    }

    [TestCase( 0.20f, 0.20f, 0.15f, true )]
    [TestCase( 0.22f, 0.21f, 0.15f, false )]
    [TestCase( 0.20f, 0.20f, 0.151f, false )]
    [TestCase( 0.201f, 0.20f, 0.00f, false )]
    public void SettledEnvelopeRequiresBothPositionAndLowSwingSpeed(
      float distance,
      float reachThreshold,
      float normalizedSwingVelocity,
      bool expected )
    {
      Type type = FindType( "RLExcavator.TrajectoryTerminalStabilization" );
      MethodInfo method = type.GetMethod( "IsSettled", BindingFlags.Static | BindingFlags.Public );

      Assert.That( method, Is.Not.Null );
      bool result = (bool)method.Invoke(
        null,
        new object[] { distance, reachThreshold, normalizedSwingVelocity, 0.15f } );
      Assert.That( result, Is.EqualTo( expected ) );
    }

    [Test]
    public void StableCountResetsWhenTheSettlingEnvelopeIsLost()
    {
      Type type = FindType( "RLExcavator.TrajectoryTerminalStabilization" );
      MethodInfo method = type.GetMethod( "NextStableCount", BindingFlags.Static | BindingFlags.Public );

      Assert.That( method, Is.Not.Null );
      Assert.That( method.Invoke( null, new object[] { 2, true } ), Is.EqualTo( 3 ) );
      Assert.That( method.Invoke( null, new object[] { 2, false } ), Is.EqualTo( 0 ) );
    }

    private static float Penalty(
      float distance,
      float approachDistance,
      float normalizedSwingVelocity,
      float swingAction,
      float velocityWeight,
      float driveWeight )
    {
      Type type = FindType( "RLExcavator.TrajectoryTerminalStabilization" );
      MethodInfo method = type.GetMethod( "CalculatePenalty", BindingFlags.Static | BindingFlags.Public );
      Assert.That( method, Is.Not.Null );
      return (float)method.Invoke(
        null,
        new object[] {
          distance,
          approachDistance,
          normalizedSwingVelocity,
          swingAction,
          velocityWeight,
          driveWeight
        } );
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
