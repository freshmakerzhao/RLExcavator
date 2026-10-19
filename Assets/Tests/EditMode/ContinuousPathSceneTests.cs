using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class ContinuousPathSceneTests
  {
    [Test]
    public void ExpandedTrainingUsesLongWorkspaceArcsWithoutChangingTheV3Scene()
    {
      var builder = System.AppDomain.CurrentDomain.GetAssemblies()
        .Select( assembly => assembly.GetType( "RLExcavator.Editor.ContinuousPathTrainingBuild" ) )
        .First( type => type != null );
      var update = builder.GetMethod( "UpdateExpandedTrainingScene" );
      Assert.That( update, Is.Not.Null, "Expanded training must have its own scene and update entry." );
      const string expandedPath = "Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPathExpanded.unity";
      const string v3Path = "Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPath.unity";
      byte[] before = System.IO.File.ReadAllBytes( v3Path );
      var previous = EditorSceneManager.GetActiveScene();
      var scene = EditorSceneManager.OpenScene( expandedPath, OpenSceneMode.Additive );
      try {
        update.Invoke( null, null );
        var components = scene.GetRootGameObjects().SelectMany(
          root => root.GetComponentsInChildren<MonoBehaviour>( true ) ).ToArray();
        var trackers = components.Where( c => c != null && c.GetType().Name == "ContinuousPathTracking" ).ToArray();
        Assert.That( trackers.Length, Is.EqualTo( 4 ) );
        foreach ( var tracker in trackers ) {
          var tracking = new SerializedObject( tracker );
          Assert.That( tracking.FindProperty( "preserveTrainingPathShape" ).boolValue, Is.True );
          Assert.That( tracking.FindProperty( "endpointToleranceM" ).floatValue, Is.EqualTo( 0.03f ) );
          Assert.That( tracking.FindProperty( "corridorRadiusM" ).floatValue, Is.EqualTo( 0.08f ) );
        }
        foreach ( var provider in components.Where( c => c != null && c.GetType().Name == "TrajectoryProvider" ) ) {
          var path = new SerializedObject( provider );
          Assert.That( path.FindProperty( "randomWaypointCount" ).intValue, Is.EqualTo( 9 ) );
          Assert.That( path.FindProperty( "reachableWorkspaceMinEndpointDistance" ).floatValue, Is.EqualTo( 0.35f ) );
          Assert.That( path.FindProperty( "reachableWorkspaceMaxEndpointDistance" ).floatValue, Is.EqualTo( 1f ) );
          Assert.That( path.FindProperty( "nearbyPathParameterRadius" ).floatValue, Is.EqualTo( 0.5f ) );
        }
        Assert.That( System.IO.File.ReadAllBytes( v3Path ), Is.EqualTo( before ), "Do not rewrite the frozen V3 scene." );
      }
      finally {
        EditorSceneManager.CloseScene( scene, true );
        EditorSceneManager.SetActiveScene( previous );
      }
    }

    [Test]
    public void UpdatingAnAlreadyLoadedExpandedTrainingSceneKeepsItOpen()
    {
      var previous = EditorSceneManager.GetActiveScene();
      var scene = EditorSceneManager.OpenScene(
        "Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPathExpanded.unity", OpenSceneMode.Additive );
      try {
        EditorSceneManager.SetActiveScene( scene );
        var builder = System.AppDomain.CurrentDomain.GetAssemblies()
          .Select( assembly => assembly.GetType( "RLExcavator.Editor.ContinuousPathTrainingBuild" ) )
          .First( type => type != null );
        builder.GetMethod( "UpdateExpandedTrainingScene" ).Invoke( null, null );
        Assert.That( scene.isLoaded, Is.True );
        Assert.That( EditorSceneManager.GetActiveScene(), Is.EqualTo( scene ) );
      }
      finally {
        if ( scene.isLoaded )
          EditorSceneManager.CloseScene( scene, true );
        EditorSceneManager.SetActiveScene( previous );
      }
    }

    [Test]
    public void CandidateSceneUsesAnIndependentContractWithoutChangingTheSharedPrefab()
    {
      const string path = "Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPath.unity";
      Assert.That( AssetDatabase.LoadAssetAtPath<SceneAsset>( path ), Is.Not.Null,
        "Create the candidate with the Unity Editor scene builder first." );
      var previous = EditorSceneManager.GetActiveScene();
      var scene = EditorSceneManager.OpenScene( path, OpenSceneMode.Additive );
      try {
        var trackers = scene.GetRootGameObjects()
          .SelectMany( root => root.GetComponentsInChildren<MonoBehaviour>( true ) )
          .Where( component => component != null && component.GetType().Name == "ContinuousPathTracking" )
          .ToArray();
        Assert.That( trackers.Length, Is.EqualTo( 4 ) );
        int moveToDigCount = 0;
        foreach ( var tracker in trackers ) {
          var agent = tracker.GetComponents<MonoBehaviour>()
            .Single( component => component.GetType().Name == "ExcavatorTrajectoryAgent" );
          var agentSettings = new SerializedObject( agent );
          Assert.That( agentSettings.FindProperty( "randomizeTaskModeOnEpisodeBegin" ).boolValue,
            Is.False, "Fixed parallel task coverage must not change after a short successful episode." );
          if ( agentSettings.FindProperty( "taskMode" ).enumValueIndex == 0 )
            moveToDigCount++;
          Assert.That( new SerializedObject( tracker ).FindProperty( "generateTrainingReference" ).boolValue,
            Is.True, "Only this training scene enables reference generation." );
          var behavior = tracker.GetComponents<MonoBehaviour>()
            .Single( component => component.GetType().Name == "BehaviorParameters" );
          var serialized = new SerializedObject( behavior );
          Assert.That( serialized.FindProperty( "m_BehaviorName" ).stringValue,
            Is.EqualTo( "ExcavatorContinuousPath" ) );
          Assert.That( serialized.FindProperty( "m_BrainParameters.VectorObservationSize" ).intValue,
            Is.EqualTo( 47 ) );
          Assert.That( serialized.FindProperty( "m_Model" ).objectReferenceValue, Is.Null );
        }
        Assert.That( moveToDigCount, Is.EqualTo( 2 ), "Every player must contain two agents for each task." );
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>( "Assets/Prefabs/ScaleTrajectoryEnvironment.prefab" );
        Assert.That( prefab.GetComponentsInChildren<MonoBehaviour>( true ).Any(
          component => component != null && component.GetType().Name == "ContinuousPathTracking" ), Is.False,
          "Candidate changes must remain scene overrides." );
      }
      finally {
        EditorSceneManager.CloseScene( scene, true );
        EditorSceneManager.SetActiveScene( previous );
      }
    }
  }
}
