using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class ExcavatorTrajectoryAgentReachThresholdTests
  {
    [Test]
    public void ReachThresholdIncludesCurrentWaypointVisualRadiusWhenEnabled()
    {
      var agentObject = new GameObject( "Agent" );
      var trajectoryObject = new GameObject( "Trajectory" );
      var waypointObject = new GameObject( "Waypoint_0" );

      try {
        Component agent = agentObject.AddComponent( FindType( "RLExcavator.ExcavatorTrajectoryAgent" ) );
        Component trajectory = trajectoryObject.AddComponent( FindType( "RLExcavator.TrajectoryProvider" ) );
        Component visual = waypointObject.AddComponent( FindType( "RLExcavator.TargetPointVisual" ) );

        SetPublicField( visual, "radius", 0.18f );
        SetPublicField( trajectory, "waypoints", new[] { waypointObject.transform } );
        SetPublicField( agent, "trajectory", trajectory );
        SetPublicField( agent, "targetThreshold", 0.03f );
        SetPublicField( agent, "includeTargetVisualRadiusInThreshold", true );

        float threshold = InvokePrivate<float>( agent, "CurrentReachThreshold" );

        Assert.That( threshold, Is.EqualTo( 0.21f ).Within( 0.0001f ) );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( agentObject );
        UnityEngine.Object.DestroyImmediate( trajectoryObject );
        UnityEngine.Object.DestroyImmediate( waypointObject );
      }
    }

    [Test]
    public void ReachThresholdUsesTargetThresholdOnlyWhenVisualRadiusDisabled()
    {
      var agentObject = new GameObject( "Agent" );
      var trajectoryObject = new GameObject( "Trajectory" );
      var waypointObject = new GameObject( "Waypoint_0" );

      try {
        Component agent = agentObject.AddComponent( FindType( "RLExcavator.ExcavatorTrajectoryAgent" ) );
        Component trajectory = trajectoryObject.AddComponent( FindType( "RLExcavator.TrajectoryProvider" ) );
        Component visual = waypointObject.AddComponent( FindType( "RLExcavator.TargetPointVisual" ) );

        SetPublicField( visual, "radius", 0.18f );
        SetPublicField( trajectory, "waypoints", new[] { waypointObject.transform } );
        SetPublicField( agent, "trajectory", trajectory );
        SetPublicField( agent, "targetThreshold", 0.03f );
        SetPublicField( agent, "includeTargetVisualRadiusInThreshold", false );

        float threshold = InvokePrivate<float>( agent, "CurrentReachThreshold" );

        Assert.That( threshold, Is.EqualTo( 0.03f ).Within( 0.0001f ) );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( agentObject );
        UnityEngine.Object.DestroyImmediate( trajectoryObject );
        UnityEngine.Object.DestroyImmediate( waypointObject );
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

    private static T InvokePrivate<T>( object target, string methodName, params object[] args )
    {
      MethodInfo method = target.GetType().GetMethod( methodName, BindingFlags.Instance | BindingFlags.NonPublic );
      Assert.That( method, Is.Not.Null, "Missing method: " + methodName );
      object result = method.Invoke( target, args );
      return result is T typed ? typed : default;
    }

    private static void SetPublicField( object target, string fieldName, object value )
    {
      FieldInfo field = target.GetType().GetField( fieldName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( field, Is.Not.Null, "Missing public field: " + fieldName );
      field.SetValue( target, value );
    }
  }
}
