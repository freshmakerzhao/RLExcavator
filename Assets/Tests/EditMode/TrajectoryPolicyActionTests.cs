using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class TrajectoryPolicyActionTests
  {
    [Test]
    public void TrajectoryAgentPublishesRawPolicyActionBeforeDeadzoneFiltering()
    {
      Type agentType = FindType( "RLExcavator.ExcavatorTrajectoryAgent" );
      var gameObject = new GameObject( "raw-policy-action-test" );

      try {
        Component agent = gameObject.AddComponent( agentType );
        object actions = CreateContinuousActionBuffer( 0.03f, -0.04f, 0.05f, -0.06f );

        try {
          agentType.GetMethod( "OnActionReceived", BindingFlags.Instance | BindingFlags.Public )
            .Invoke( agent, new object[] { actions } );
        }
        catch ( TargetInvocationException exception ) when ( exception.InnerException is NullReferenceException ) {
          // This isolated test intentionally omits a full ML-Agents Academy/trajectory setup.
          // Raw action capture must happen before the environment-dependent reward path.
        }

        PropertyInfo property = agentType.GetProperty( "LastRawPolicyAction", BindingFlags.Instance | BindingFlags.Public );
        Assert.That( property, Is.Not.Null, "Trajectory agent must expose the raw policy action for inference diagnostics." );
        Assert.That( (Vector4)property.GetValue( agent ), Is.EqualTo( new Vector4( 0.03f, -0.04f, 0.05f, -0.06f ) ) );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( gameObject );
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

    private static object CreateContinuousActionBuffer( params float[] values )
    {
      Type actionBuffersType = FindType( "Unity.MLAgents.Actuators.ActionBuffers" );

      return Activator.CreateInstance( actionBuffersType, new object[] { values, Array.Empty<int>() } );
    }
  }
}
