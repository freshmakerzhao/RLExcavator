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
        SetFloatField( controller, "boomMaxSpeed", 1f );
        SetFloatField( controller, "stickMaxSpeed", 1f );
        SetFloatField( controller, "bucketMaxSpeed", 1f );
        SetFloatField( controller, "swingMaxSpeed", 1f );

        MethodInfo applyMethod = controllerType.GetMethod( "ApplyMachineProfileSpeeds", BindingFlags.Instance | BindingFlags.Public );
        Assert.That( applyMethod, Is.Not.Null, "ExcavatorController must expose ApplyMachineProfileSpeeds(MachineProfile)." );

        applyMethod.Invoke( controller, new object[] { profile } );

        Assert.That( GetFloatField( controller, "boomMaxSpeed" ), Is.EqualTo( 0.06f ).Within( 1e-6f ) );
        Assert.That( GetFloatField( controller, "stickMaxSpeed" ), Is.EqualTo( 0.06f ).Within( 1e-6f ) );
        Assert.That( GetFloatField( controller, "bucketMaxSpeed" ), Is.EqualTo( 0.06f ).Within( 1e-6f ) );
        Assert.That( GetFloatField( controller, "swingMaxSpeed" ), Is.EqualTo( 0.6f ).Within( 1e-6f ) );
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

    private static string SharedProfilePath()
    {
      return Path.GetFullPath( Path.Combine( Application.dataPath, "../../shared/machine_profile.json" ) );
    }
  }
}
