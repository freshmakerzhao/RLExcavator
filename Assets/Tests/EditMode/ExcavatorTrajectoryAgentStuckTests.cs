using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class ExcavatorTrajectoryAgentStuckTests
  {
    [Test]
    public void StuckWindowAllowsTipMotionEvenWithoutTargetProgress()
    {
      var agentObject = new GameObject( "Agent" );
      try {
        Component agent = agentObject.AddComponent( FindType( "RLExcavator.ExcavatorTrajectoryAgent" ) );
        SetPublicField( agent, "stuckWindowSteps", 5 );
        SetPublicField( agent, "stuckMinTipMotion", 0.01f );
        SetPublicField( agent, "stuckMinActionMagnitude", 0.1f );

        InvokePrivate( agent, "ResetStuckWindow", Vector3.zero );

        bool stuck = false;
        for ( int i = 0; i < 5; i++ )
          stuck = InvokePrivate<bool>( agent, "IsMechanicallyStuck", new Vector3( 0f, 0f, ( i + 1 ) * 0.004f ), 0.5f );

        Assert.That( stuck, Is.False );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( agentObject );
      }
    }

    [Test]
    public void StuckWindowFailsWhenCommandedTipDoesNotMove()
    {
      var agentObject = new GameObject( "Agent" );
      try {
        Component agent = agentObject.AddComponent( FindType( "RLExcavator.ExcavatorTrajectoryAgent" ) );
        SetPublicField( agent, "stuckWindowSteps", 5 );
        SetPublicField( agent, "stuckMinTipMotion", 0.01f );
        SetPublicField( agent, "stuckMinActionMagnitude", 0.1f );

        InvokePrivate( agent, "ResetStuckWindow", Vector3.zero );

        bool stuck = false;
        for ( int i = 0; i < 5; i++ )
          stuck = InvokePrivate<bool>( agent, "IsMechanicallyStuck", Vector3.zero, 0.5f );

        Assert.That( stuck, Is.True );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( agentObject );
      }
    }

    [Test]
    public void StuckWindowIgnoresNoCommandMotion()
    {
      var agentObject = new GameObject( "Agent" );
      try {
        Component agent = agentObject.AddComponent( FindType( "RLExcavator.ExcavatorTrajectoryAgent" ) );
        SetPublicField( agent, "stuckWindowSteps", 5 );
        SetPublicField( agent, "stuckMinTipMotion", 0.01f );
        SetPublicField( agent, "stuckMinActionMagnitude", 0.1f );

        InvokePrivate( agent, "ResetStuckWindow", Vector3.zero );

        bool stuck = false;
        for ( int i = 0; i < 20; i++ )
          stuck = InvokePrivate<bool>( agent, "IsMechanicallyStuck", Vector3.zero, 0.0f );

        Assert.That( stuck, Is.False );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( agentObject );
      }
    }

    [Test]
    public void StuckFailureCanBeDisabledForDebugging()
    {
      var agentObject = new GameObject( "Agent" );
      try {
        Component agent = agentObject.AddComponent( FindType( "RLExcavator.ExcavatorTrajectoryAgent" ) );
        SetPublicField( agent, "failOnStuck", false );
        SetPublicField( agent, "stuckWindowSteps", 5 );
        SetPublicField( agent, "stuckMinTipMotion", 0.01f );
        SetPublicField( agent, "stuckMinActionMagnitude", 0.1f );

        InvokePrivate( agent, "ResetStuckWindow", Vector3.zero );

        bool stuck = false;
        for ( int i = 0; i < 20; i++ )
          stuck = InvokePrivate<bool>( agent, "IsMechanicallyStuck", Vector3.zero, 0.5f );

        Assert.That( stuck, Is.False );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( agentObject );
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

    private static void InvokePrivate( object target, string methodName, params object[] args )
    {
      InvokePrivate<object>( target, methodName, args );
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
