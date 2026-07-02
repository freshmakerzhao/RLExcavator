using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class UITargetTrackingTests
  {
    [Test]
    public void FallbackHudStaysEnabledWithoutInformationText()
    {
      var hudObject = new GameObject( "TrainingHud" );
      try {
        Type hudType = FindType( "UITargetTracking" );
        Component hud = hudObject.AddComponent( hudType );

        InvokeNonPublic( hud, "Awake" );

        Assert.That( ( (Behaviour)hud ).enabled, Is.True );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( hudObject );
      }
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

    private static void InvokeNonPublic( object target, string methodName )
    {
      MethodInfo method = target.GetType().GetMethod( methodName, BindingFlags.Instance | BindingFlags.NonPublic );
      Assert.That( method, Is.Not.Null, "Missing method: " + methodName );
      method.Invoke( target, null );
    }
  }
}
