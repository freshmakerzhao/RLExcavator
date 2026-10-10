using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class MachineProfileLoaderTests
  {
    [TestCase( "// \"machine_id\": \"comment-only\"\n" )]
    [TestCase( "/* \"machine_id\": \"comment-only\" */\n" )]
    public void LoadFromJsonIgnoresCommentedOutKeys( string comment )
    {
      string json = File.ReadAllText( SharedProfilePath() );
      MachineProfile expected = MachineProfileLoader.LoadFromJson( json );

      MachineProfile actual = MachineProfileLoader.LoadFromJson( comment + json );

      Assert.That( actual.MachineId, Is.EqualTo( expected.MachineId ) );
      Assert.That( actual.ActionOrder, Is.EqualTo( expected.ActionOrder ) );
    }

    [Test]
    public void LoadFromJsonIgnoresInlineCommentsAroundActuatorValues()
    {
      string json = File.ReadAllText( SharedProfilePath() );
      MachineProfile expected = MachineProfileLoader.LoadFromJson( json );
      string commented = json.Replace( "\"max_speed_positive\":",
        "/* \"max_speed_positive\": 999, } ] */ \"max_speed_positive\": // speed in SI units\n" );

      MachineProfile actual = MachineProfileLoader.LoadFromJson( commented + "\n// end of profile" );

      foreach ( string name in expected.ActionOrder )
        Assert.That( actual.GetActuator( name ).MaxSpeedPositive,
          Is.EqualTo( expected.GetActuator( name ).MaxSpeedPositive ) );
    }

    [Test]
    public void LoadFromJsonPreservesCommentMarkersAndEscapesInsideStrings()
    {
      string json = File.ReadAllText( SharedProfilePath() );
      MachineProfile expected = MachineProfileLoader.LoadFromJson( json );
      string encodedId = "https://lab/*profile*/设备/\\\"quoted\\\"/\\\\fixture";
      string modified = json.Replace( "\"" + expected.MachineId + "\"", "\"" + encodedId + "\"" );

      MachineProfile actual = MachineProfileLoader.LoadFromJson( modified );

      Assert.That( actual.MachineId, Is.EqualTo( "https://lab/*profile*/设备/\"quoted\"/\\fixture" ) );
    }

    [TestCase( "\n/* unterminated" )]
    [TestCase( "\n{}" )]
    public void LoadFromJsonRejectsMalformedTrailingContent( string suffix )
    {
      string json = File.ReadAllText( SharedProfilePath() );

      Assert.Throws<FormatException>( () => MachineProfileLoader.LoadFromJson( json + suffix ) );
    }

    [Test]
    public void DefaultProfilePathUsesJsoncExtension()
    {
      Assert.That( Path.GetFileName( MachineProfileLoader.DefaultProfilePath() ),
        Is.EqualTo( "machine_profile.jsonc" ) );
    }

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

      AssertActuator( profile.GetActuator( "boom" ), 0, "prismatic", 0.0351f, 0.0185f, true, -0.080f, 0.095f );
      AssertActuator( profile.GetActuator( "stick" ), 1, "prismatic", 0.0444f, 0.0357f, true, -0.090f, 0.060f );
      AssertActuator( profile.GetActuator( "bucket" ), 2, "prismatic", 0.0342f, 0.0419f, true, -0.045f, 0.085f );

      MachineActuatorProfile swing = profile.GetActuator( "swing" );
      Assert.That( swing.ActionIndex, Is.EqualTo( 3 ) );
      Assert.That( swing.JointType, Is.EqualTo( "revolute_continuous" ) );
      Assert.That( swing.MaxSpeedPositive, Is.EqualTo( 0.6f ).Within( 1e-6f ) );
      Assert.That( swing.MaxSpeedNegative, Is.EqualTo( 0.6f ).Within( 1e-6f ) );
      Assert.That( swing.MaxAbsSpeed, Is.EqualTo( 0.6f ).Within( 1e-6f ) );
      Assert.That( swing.HasFiniteRange, Is.False );
      Assert.That( swing.RangeEnabledRequired, Is.False );
      Assert.That( swing.Sign, Is.EqualTo( 1 ) );
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
    }

    private static string SharedProfilePath()
    {
      return Path.GetFullPath( Path.Combine( Application.dataPath, "../../shared/machine_profile.jsonc" ) );
    }
  }
}
