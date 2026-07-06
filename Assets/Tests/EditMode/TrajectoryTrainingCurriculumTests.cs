using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class TrajectoryTrainingCurriculumTests
  {
    [Test]
    public void ApplyEasyTrainingMakesNearbyShortStraightTask()
    {
      TestStage(
        stageValue: 1,
        expectedWaypointCount: 2,
        expectedCurvedPaths: false,
        expectedCurveStrength: 0f,
        expectedJitter: 0.03f,
        expectedUseCurrentTipAsPathStart: true,
        expectedMinEndpointDistance: 0.06f,
        expectedMaxEndpointDistance: 0.20f,
        expectedScalePitchBonus: false );
    }

    [Test]
    public void ApplyFullTrainingRestoresDefaultTrajectoryDifficulty()
    {
      TestStage(
        stageValue: 2,
        expectedWaypointCount: 5,
        expectedCurvedPaths: true,
        expectedCurveStrength: 0.16f,
        expectedJitter: 0.12f,
        expectedUseCurrentTipAsPathStart: false,
        expectedMinEndpointDistance: 0.1f,
        expectedMaxEndpointDistance: 0f,
        expectedScalePitchBonus: true );
    }

    private static void TestStage(
      int stageValue,
      int expectedWaypointCount,
      bool expectedCurvedPaths,
      float expectedCurveStrength,
      float expectedJitter,
      bool expectedUseCurrentTipAsPathStart,
      float expectedMinEndpointDistance,
      float expectedMaxEndpointDistance,
      bool expectedScalePitchBonus )
    {
      var root = new GameObject( "CurriculumRoot" );
      var providerObject = new GameObject( "TrajectoryProvider" );
      var agentObject = new GameObject( "Agent" );

      try {
        providerObject.transform.SetParent( root.transform );
        agentObject.transform.SetParent( root.transform );

        Type providerType = FindType( "RLExcavator.TrajectoryProvider" );
        Type agentType = FindType( "RLExcavator.ExcavatorTrajectoryAgent" );
        Type curriculumType = FindType( "RLExcavator.TrajectoryTrainingCurriculum" );
        Type stageType = curriculumType.GetNestedType( "CurriculumStage" );

        Component provider = providerObject.AddComponent( providerType );
        Component agent = agentObject.AddComponent( agentType );
        Component curriculum = root.AddComponent( curriculumType );

        SetPublicField( curriculum, "trajectoryProvider", provider );
        SetPublicField( curriculum, "trajectoryAgent", agent );
        SetPublicField( curriculum, "stage", Enum.ToObject( stageType, stageValue ) );
        SetPublicField( curriculum, "logAppliedSettings", false );

        InvokePublic( curriculum, "Apply" );

        Assert.That( GetPublicField<int>( provider, "randomWaypointCount" ), Is.EqualTo( expectedWaypointCount ) );
        Assert.That( GetPublicField<bool>( provider, "reachableWorkspaceUseCurvedPaths" ), Is.EqualTo( expectedCurvedPaths ) );
        Assert.That( GetPublicField<float>( provider, "reachableWorkspaceCurveStrength" ), Is.EqualTo( expectedCurveStrength ).Within( 1e-6f ) );
        Assert.That( GetPublicField<float>( provider, "reachableWorkspaceDirectionJitter" ), Is.EqualTo( expectedJitter ).Within( 1e-6f ) );
        Assert.That( GetPublicField<float>( provider, "reachableWorkspaceHeightJitter" ), Is.EqualTo( expectedJitter ).Within( 1e-6f ) );
        Assert.That( GetPublicField<float>( provider, "reachableWorkspaceDepthJitter" ), Is.EqualTo( expectedJitter ).Within( 1e-6f ) );
        Assert.That( GetPublicField<bool>( provider, "useCurrentTipAsPathStart" ), Is.EqualTo( expectedUseCurrentTipAsPathStart ) );
        Assert.That( GetPublicField<float>( provider, "reachableWorkspaceMinEndpointDistance" ), Is.EqualTo( expectedMinEndpointDistance ).Within( 1e-6f ) );
        Assert.That( GetPublicField<float>( provider, "reachableWorkspaceMaxEndpointDistance" ), Is.EqualTo( expectedMaxEndpointDistance ).Within( 1e-6f ) );
        Assert.That( GetPublicField<bool>( agent, "scaleWaypointBonusByBucketAttitude" ), Is.EqualTo( expectedScalePitchBonus ) );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( root );
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

    private static void InvokePublic( object target, string methodName )
    {
      MethodInfo method = target.GetType().GetMethod( methodName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( method, Is.Not.Null, "Missing public method: " + methodName );
      method.Invoke( target, null );
    }

    private static T GetPublicField<T>( object target, string fieldName )
    {
      FieldInfo field = target.GetType().GetField( fieldName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( field, Is.Not.Null, "Missing public field: " + fieldName );
      object value = field.GetValue( target );
      return value is T typed ? typed : default;
    }

    private static void SetPublicField( object target, string fieldName, object value )
    {
      FieldInfo field = target.GetType().GetField( fieldName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( field, Is.Not.Null, "Missing public field: " + fieldName );
      field.SetValue( target, value );
    }
  }
}
