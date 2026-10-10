using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class ExcavatorControllerProfileSpeedTests
  {
    [Test]
    public void ControllerAppliesMaxSpeedsFromSharedProfile()
    {
      MachineProfile profile = MachineProfileLoader.LoadFromPath( SharedProfilePath() );
      Type controllerType = FindType( "RLExcavator.ExcavatorController" );

      var gameObject = new GameObject( "ExcavatorControllerProfileSpeedTest" );
      try {
        Component controller = gameObject.AddComponent( controllerType );
        SetFloatField( controller, "boomMaxSpeedPositive", 1f );
        SetFloatField( controller, "boomMaxSpeedNegative", 1f );
        SetFloatField( controller, "stickMaxSpeedPositive", 1f );
        SetFloatField( controller, "stickMaxSpeedNegative", 1f );
        SetFloatField( controller, "bucketMaxSpeedPositive", 1f );
        SetFloatField( controller, "bucketMaxSpeedNegative", 1f );

        MethodInfo applyMethod = controllerType.GetMethod( "ApplyMachineProfileSpeeds", BindingFlags.Instance | BindingFlags.Public );
        Assert.That( applyMethod, Is.Not.Null, "ExcavatorController must expose ApplyMachineProfileSpeeds(MachineProfile)." );

        applyMethod.Invoke( controller, new object[] { profile } );

        Assert.That( GetFloatField( controller, "boomMaxSpeedPositive" ), Is.EqualTo( 0.0351f ).Within( 1e-6f ) );
        Assert.That( GetFloatField( controller, "boomMaxSpeedNegative" ), Is.EqualTo( 0.0185f ).Within( 1e-6f ) );
        Assert.That( GetFloatField( controller, "stickMaxSpeedPositive" ), Is.EqualTo( 0.0444f ).Within( 1e-6f ) );
        Assert.That( GetFloatField( controller, "stickMaxSpeedNegative" ), Is.EqualTo( 0.0357f ).Within( 1e-6f ) );
        Assert.That( GetFloatField( controller, "bucketMaxSpeedPositive" ), Is.EqualTo( 0.0342f ).Within( 1e-6f ) );
        Assert.That( GetFloatField( controller, "bucketMaxSpeedNegative" ), Is.EqualTo( 0.0419f ).Within( 1e-6f ) );
        Assert.That( InvokeFloatMethod( controller, "MaxAbsSpeed", "boom" ), Is.EqualTo( 0.0351f ).Within( 1e-6f ) );
        Assert.That( InvokeFloatMethod( controller, "MaxAbsSpeed", "stick" ), Is.EqualTo( 0.0444f ).Within( 1e-6f ) );
        Assert.That( InvokeFloatMethod( controller, "MaxAbsSpeed", "bucket" ), Is.EqualTo( 0.0419f ).Within( 1e-6f ) );
        Assert.That( InvokeFloatMethod( controller, "MaxAbsSpeed", "swing" ), Is.EqualTo( 0.6f ).Within( 1e-6f ) );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( gameObject );
      }
    }

    [Test]
    public void ControllerExposesRangeApplicationFromSharedProfile()
    {
      Type controllerType = FindType( "RLExcavator.ExcavatorController" );

      MethodInfo applyMethod = controllerType.GetMethod( "ApplyMachineProfileRanges", BindingFlags.Instance | BindingFlags.Public );

      Assert.That( applyMethod, Is.Not.Null, "ExcavatorController must expose ApplyMachineProfileRanges(MachineProfile)." );
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

    private static void SetFloatField( object target, string fieldName, float value )
    {
      FieldInfo field = target.GetType().GetField( fieldName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( field, Is.Not.Null, "Missing public field: " + fieldName );
      field.SetValue( target, value );
    }

    private static float GetFloatField( object target, string fieldName )
    {
      FieldInfo field = target.GetType().GetField( fieldName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( field, Is.Not.Null, "Missing public field: " + fieldName );
      return (float)field.GetValue( target );
    }

    private static float InvokeFloatMethod( object target, string methodName, string argument )
    {
      MethodInfo method = target.GetType().GetMethod( methodName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( method, Is.Not.Null, "Missing public method: " + methodName );
      return (float)method.Invoke( target, new object[] { argument } );
    }

    private static string SharedProfilePath()
    {
      return Path.GetFullPath( Path.Combine( Application.dataPath, "../../shared/machine_profile.jsonc" ) );
    }
  }
}
