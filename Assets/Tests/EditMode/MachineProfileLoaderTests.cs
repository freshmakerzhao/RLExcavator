using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class MachineProfileLoaderTests
  {
    [Test]
    public void LoadSharedProfileReturnsLockedActionOrderAndDimensions()
    {
      MachineProfile profile = MachineProfileLoader.LoadFromPath( SharedProfilePath() );

      Assert.That( profile.ActionOrder, Is.EqualTo( new[] { "boom", "stick", "bucket", "swing" } ) );
      Assert.That( profile.ReachableWorkspaceFile, Is.EqualTo( "reachable_workspaces/scale_excavator_workspace.json" ) );
      Assert.That( profile.ObservationSchema.Id, Is.EqualTo( "scale_excavator_v2_38d" ) );
      Assert.That( profile.ObservationSchema.TotalDim, Is.EqualTo( 38 ) );
      Assert.That( profile.ObservationSchema.ActionDim, Is.EqualTo( 4 ) );
    }

    [Test]
    public void LoadSharedProfileReturnsActuatorSpeedsRangesAndSigns()
    {
      MachineProfile profile = MachineProfileLoader.LoadFromPath( SharedProfilePath() );

      AssertActuator( profile.GetActuator( "boom" ), 0, "prismatic", 0.0351f, 0.0185f, true, -0.095f, 0.080f );
      AssertActuator( profile.GetActuator( "stick" ), 1, "prismatic", 0.0444f, 0.0357f, true, -0.060f, 0.090f );
      AssertActuator( profile.GetActuator( "bucket" ), 2, "prismatic", 0.0342f, 0.0419f, true, -0.085f, 0.045f );

      MachineActuatorProfile swing = profile.GetActuator( "swing" );
      Assert.That( swing.ActionIndex, Is.EqualTo( 3 ) );
      Assert.That( swing.JointType, Is.EqualTo( "revolute_continuous" ) );
      Assert.That( swing.MaxSpeedPositive, Is.EqualTo( 0.6f ).Within( 1e-6f ) );
      Assert.That( swing.MaxSpeedNegative, Is.EqualTo( 0.6f ).Within( 1e-6f ) );
      Assert.That( swing.MaxAbsSpeed, Is.EqualTo( 0.6f ).Within( 1e-6f ) );
      Assert.That( swing.HasFiniteRange, Is.False );
      Assert.That( swing.RangeEnabledRequired, Is.False );
      Assert.That( swing.Sign, Is.EqualTo( 1 ) );
      Assert.That( swing.DeploySign, Is.EqualTo( 1 ) );
    }

    [Test]
    public void LoadSharedProfileReturnsObservationNormalizers()
    {
      MachineProfile profile = MachineProfileLoader.LoadFromPath( SharedProfilePath() );
      MachineObservationNormalizers normalizers = profile.ObservationSchema.Normalizers;

      Assert.That( normalizers.PositionNormalizer, Is.EqualTo( 1.13f ).Within( 1e-6f ) );
      Assert.That( normalizers.TipVelocityScale, Is.EqualTo( 0.05f ).Within( 1e-6f ) );
      Assert.That( normalizers.DistanceNormalizer, Is.EqualTo( 1.13f ).Within( 1e-6f ) );
      Assert.That( normalizers.TubeRadius, Is.EqualTo( 0.04f ).Within( 1e-6f ) );
      Assert.That( normalizers.TargetThreshold, Is.EqualTo( 0.03f ).Within( 1e-6f ) );
      Assert.That( normalizers.PitchNormDeg, Is.EqualTo( 180.0f ).Within( 1e-6f ) );
    }

    [Test]
    public void LoadSharedProfileReturnsTaskPitchTargets()
    {
      MachineProfile profile = MachineProfileLoader.LoadFromPath( SharedProfilePath() );

      Assert.That( profile.TaskProfile.MoveToDigBucketPitchTargetDeg, Is.EqualTo( 70.0f ).Within( 1e-6f ) );
      Assert.That( profile.TaskProfile.CarryMaterialBucketPitchTargetDeg, Is.EqualTo( 180.0f ).Within( 1e-6f ) );
    }

    private static void AssertActuator(
      MachineActuatorProfile actuator,
      int actionIndex,
      string jointType,
      float maxSpeedPositive,
      float maxSpeedNegative,
      bool rangeEnabledRequired,
      float rangeMin,
      float rangeMax )
    {
      Assert.That( actuator.ActionIndex, Is.EqualTo( actionIndex ) );
      Assert.That( actuator.JointType, Is.EqualTo( jointType ) );
      Assert.That( actuator.MaxSpeedPositive, Is.EqualTo( maxSpeedPositive ).Within( 1e-6f ) );
      Assert.That( actuator.MaxSpeedNegative, Is.EqualTo( maxSpeedNegative ).Within( 1e-6f ) );
      Assert.That( actuator.MaxAbsSpeed, Is.EqualTo( Mathf.Max( maxSpeedPositive, maxSpeedNegative ) ).Within( 1e-6f ) );
      Assert.That( actuator.HasFiniteRange, Is.True );
      Assert.That( actuator.RangeMin, Is.EqualTo( rangeMin ).Within( 1e-6f ) );
      Assert.That( actuator.RangeMax, Is.EqualTo( rangeMax ).Within( 1e-6f ) );
      Assert.That( actuator.RangeEnabledRequired, Is.EqualTo( rangeEnabledRequired ) );
      Assert.That( actuator.Sign, Is.EqualTo( 1 ) );
      Assert.That( actuator.DeploySign, Is.EqualTo( 1 ) );
    }

    private static string SharedProfilePath()
    {
      return Path.GetFullPath( Path.Combine( Application.dataPath, "../../shared/machine_profile.json" ) );
    }
  }
}
