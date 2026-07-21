using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class MachineProfileCommandDeadzoneTests
  {
    [Test]
    public void SharedProfileLoadsMeasuredDirectionalCommandDeadzones()
    {
      MachineProfile profile = MachineProfileLoader.LoadFromPath( SharedProfilePath() );

      AssertDeadzone( profile.GetActuator( "boom" ), 0.05698005698f, 0.108108108108f );
      AssertDeadzone( profile.GetActuator( "stick" ), 0.045045045045f, 0.089635854342f );
      AssertDeadzone( profile.GetActuator( "bucket" ), 0.058479532164f, 0.047732696897f );
      AssertDeadzone( profile.GetActuator( "swing" ), 0.090175344686f, 0.119264165553f );
    }

    [Test]
    public void ControllerZerosCommandsAtOrInsideDirectionalDeadzone()
    {
      MachineProfile profile = MachineProfileLoader.LoadFromPath( SharedProfilePath() );
      var gameObject = new GameObject( "deadzone-controller-test" );

      try {
        Type controllerType = FindType( "RLExcavator.ExcavatorController" );
        Component controller = gameObject.AddComponent( controllerType );
        Invoke( controllerType, controller, "ApplyMachineProfileSpeeds", profile );
        Invoke( controllerType, controller, "ApplyMachineProfileCommandDeadzones", profile );

        Invoke(
          controllerType,
          controller,
          "SetNormalizedSpeeds",
          profile.GetActuator( "boom" ).CommandDeadzonePositiveNormalized,
          -profile.GetActuator( "stick" ).CommandDeadzoneNegativeNormalized,
          profile.GetActuator( "bucket" ).CommandDeadzonePositiveNormalized,
          -profile.GetActuator( "swing" ).CommandDeadzoneNegativeNormalized );

        Assert.That( GetFloatProperty( controllerType, controller, "LastBoomCommand" ), Is.Zero );
        Assert.That( GetFloatProperty( controllerType, controller, "LastStickCommand" ), Is.Zero );
        Assert.That( GetFloatProperty( controllerType, controller, "LastBucketCommand" ), Is.Zero );
        Assert.That( GetFloatProperty( controllerType, controller, "LastSwingCommand" ), Is.Zero );

        controllerType.GetMethod( "SetNormalizedSpeeds", BindingFlags.Instance | BindingFlags.Public ).Invoke(
          controller,
          new object[] {
            profile.GetActuator( "boom" ).CommandDeadzonePositiveNormalized + 0.01f,
            -(profile.GetActuator( "stick" ).CommandDeadzoneNegativeNormalized + 0.01f),
            profile.GetActuator( "bucket" ).CommandDeadzonePositiveNormalized + 0.01f,
            -(profile.GetActuator( "swing" ).CommandDeadzoneNegativeNormalized + 0.01f)
          } );

        Assert.That( GetFloatProperty( controllerType, controller, "LastBoomCommand" ),
          Is.EqualTo( profile.GetActuator( "boom" ).CommandDeadzonePositiveNormalized + 0.01f ).Within( 1e-6f ) );
        Assert.That( GetFloatProperty( controllerType, controller, "LastStickCommand" ),
          Is.EqualTo( -(profile.GetActuator( "stick" ).CommandDeadzoneNegativeNormalized + 0.01f) ).Within( 1e-6f ) );
        Assert.That( GetFloatProperty( controllerType, controller, "LastBucketCommand" ),
          Is.EqualTo( profile.GetActuator( "bucket" ).CommandDeadzonePositiveNormalized + 0.01f ).Within( 1e-6f ) );
        Assert.That( GetFloatProperty( controllerType, controller, "LastSwingCommand" ),
          Is.EqualTo( -(profile.GetActuator( "swing" ).CommandDeadzoneNegativeNormalized + 0.01f) ).Within( 1e-6f ) );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( gameObject );
      }
    }

    [Test]
    public void TrajectoryRewardMarksOnlyNonzeroActionsInsideDirectionalDeadzones()
    {
      MachineProfile profile = MachineProfileLoader.LoadFromPath( SharedProfilePath() );
      Type agentType = FindType( "RLExcavator.ExcavatorTrajectoryAgent" );
      MethodInfo maskMethod = agentType.GetMethod(
        "DirectionalDeadzoneNonzeroMask",
        BindingFlags.Static | BindingFlags.Public );

      Assert.That( maskMethod, Is.Not.Null, "Trajectory reward must expose its directional deadzone classification." );
      Vector4 mask = (Vector4)maskMethod.Invoke(
        null,
        new object[] {
          new Vector4(
            profile.GetActuator( "boom" ).CommandDeadzonePositiveNormalized,
            -profile.GetActuator( "stick" ).CommandDeadzoneNegativeNormalized,
            profile.GetActuator( "bucket" ).CommandDeadzonePositiveNormalized + 0.01f,
            0f ),
          profile
        } );

      Assert.That( mask, Is.EqualTo( new Vector4( 1f, 1f, 0f, 0f ) ) );
    }

    [Test]
    public void TrajectoryRewardPenalizesEachDeadzoneNonzeroAxisByThreeMillipoints()
    {
      MachineProfile profile = MachineProfileLoader.LoadFromPath( SharedProfilePath() );
      Type agentType = FindType( "RLExcavator.ExcavatorTrajectoryAgent" );
      MethodInfo penaltyMethod = agentType.GetMethod(
        "DeadzoneNonzeroActionPenalty",
        BindingFlags.Static | BindingFlags.Public );

      Assert.That( penaltyMethod, Is.Not.Null, "Trajectory reward must expose the deadzone nonzero penalty." );
      float penalty = (float)penaltyMethod.Invoke(
        null,
        new object[] {
          new Vector4(
            profile.GetActuator( "boom" ).CommandDeadzonePositiveNormalized,
            -profile.GetActuator( "stick" ).CommandDeadzoneNegativeNormalized,
            0f,
            0f ),
          profile
        } );

      Assert.That( penalty, Is.EqualTo( -0.006f ).Within( 1e-7f ) );
    }

    private static void AssertDeadzone(
      MachineActuatorProfile actuator,
      float expectedPositive,
      float expectedNegative )
    {
      Assert.That( actuator.CommandDeadzonePositiveNormalized, Is.EqualTo( expectedPositive ).Within( 1e-6f ) );
      Assert.That( actuator.CommandDeadzoneNegativeNormalized, Is.EqualTo( expectedNegative ).Within( 1e-6f ) );
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

    private static void Invoke( Type type, object target, string methodName, params object[] arguments )
    {
      MethodInfo method = type.GetMethod( methodName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( method, Is.Not.Null, "Missing public method: " + methodName );
      method.Invoke( target, arguments );
    }

    private static float GetFloatProperty( Type type, object target, string propertyName )
    {
      PropertyInfo property = type.GetProperty( propertyName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( property, Is.Not.Null, "Missing public property: " + propertyName );
      return (float)property.GetValue( target );
    }

    private static string SharedProfilePath()
    {
      return Path.GetFullPath( Path.Combine( Application.dataPath, "../../shared/machine_profile.json" ) );
    }
  }
}
