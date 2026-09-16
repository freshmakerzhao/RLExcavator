using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RLExcavator.Tests
{
  public class RlSimulationExperimentSceneTests
  {
    private const string ScenePath = "Assets/Scenes/ICRA2027_RLTrackingExperiments.unity";

    [Test]
    public void DedicatedSceneContainsRunnableTrackingExperimentContract()
    {
      Assert.That( File.Exists( ScenePath ), Is.True, "Dedicated experiment scene must exist." );

      Scene scene = EditorSceneManager.OpenScene( ScenePath, OpenSceneMode.Additive );
      try {
        GameObject marker = FindGameObject( scene, "ICRA2027_RL_TRACKING_EXPERIMENTS" );
        Transform machineRoot = FindGameObject( scene, "MachineRoot" ).transform;
        Transform start = FindGameObject( scene, "DemoStartPoint" ).transform;
        Transform end = FindGameObject( scene, "DemoEndPoint" ).transform;

        Assert.That( marker, Is.Not.Null );
        Assert.That( FindComponent( scene, "RLExcavator.ExcavationCycleTask" ), Is.Not.Null );
        Assert.That(
          FindComponent( scene, "RLExcavator.ExcavatorTrajectoryAgent" ),
          Is.Not.Null );
        Assert.That(
          FindComponent( scene, "Unity.MLAgents.Policies.BehaviorParameters" ),
          Is.Not.Null );

        AssertVector(
          machineRoot.InverseTransformPoint( start.position ),
          new Vector3( -0.55f, 0f, 1.10f ) );
        AssertVector(
          machineRoot.InverseTransformPoint( end.position ),
          new Vector3( 0.90f, 0.10f, -0.20f ) );
        Assert.That( start.position, Is.Not.EqualTo( end.position ) );
        Assert.That( scene.isDirty, Is.False, "Opening the experiment scene must not mutate it." );
      }
      finally {
        EditorSceneManager.CloseScene( scene, true );
      }
    }

    private static GameObject FindGameObject( Scene scene, string name )
    {
      foreach ( GameObject root in scene.GetRootGameObjects() ) {
        Transform[] transforms = root.GetComponentsInChildren<Transform>( true );
        foreach ( Transform candidate in transforms )
          if ( candidate.name == name )
            return candidate.gameObject;
      }

      Assert.Fail( $"Missing GameObject '{name}' in {ScenePath}." );
      return null;
    }

    private static Component FindComponent( Scene scene, string fullTypeName )
    {
      Type type = FindType( fullTypeName );
      foreach ( GameObject root in scene.GetRootGameObjects() ) {
        Component component = root.GetComponentInChildren( type, true );
        if ( component != null )
          return component;
      }

      return null;
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

    private static void AssertVector( Vector3 actual, Vector3 expected )
    {
      Assert.That( actual.x, Is.EqualTo( expected.x ).Within( 1e-4f ) );
      Assert.That( actual.y, Is.EqualTo( expected.y ).Within( 1e-4f ) );
      Assert.That( actual.z, Is.EqualTo( expected.z ).Within( 1e-4f ) );
    }
  }
}
