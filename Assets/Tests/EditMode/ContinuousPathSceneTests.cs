using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RLExcavator.Tests
{
  public class ContinuousPathSceneTests
  {
    private static readonly string[] ScenePaths = {
      "Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPath.unity",
      "Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPathExpanded.unity",
      "Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPathMission.unity",
      "Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPathMissionCorner.unity"
    };
    private byte[][] m_sceneBytes;
    private Scene m_previousActiveScene;

    [SetUp]
    public void PreserveTrainingSceneFiles()
    {
      m_sceneBytes = null;
      foreach ( string path in ScenePaths ) {
        if ( SceneManager.GetSceneByPath( path ).isLoaded )
          Assert.Ignore( "Close the training scene before running its builder tests: " + path );
      }
      m_previousActiveScene = SceneManager.GetActiveScene();
      m_sceneBytes = ScenePaths.Select( File.ReadAllBytes ).ToArray();
    }

    [TearDown]
    public void RestoreTrainingSceneFiles()
    {
      if ( m_sceneBytes == null ) return;
      // Only these scenes were closed at setup. Never save or reopen the user's active scene,
      // which may be untitled or contain unsaved edits unrelated to the builder tests.
      for ( int i = 0; i < ScenePaths.Length; ++i ) {
        string path = ScenePaths[ i ];
        Scene scene = SceneManager.GetSceneByPath( path );
        if ( scene.isLoaded ) EditorSceneManager.CloseScene( scene, true );
        if ( !File.ReadAllBytes( path ).SequenceEqual( m_sceneBytes[ i ] ) ) {
          File.WriteAllBytes( path, m_sceneBytes[ i ] );
          AssetDatabase.ImportAsset( path );
        }
      }
      if ( m_previousActiveScene.IsValid() && m_previousActiveScene.isLoaded )
        SceneManager.SetActiveScene( m_previousActiveScene );
    }

    [Test]
    public void CornerRefinementHasIsolatedGeometricRewardsAndPreservesExistingScenes()
    {
      var builder = System.AppDomain.CurrentDomain.GetAssemblies()
        .Select( assembly => assembly.GetType( "RLExcavator.Editor.ContinuousPathTrainingBuild" ) )
        .First( type => type != null );
      var create = builder.GetMethod( "CreateRefinementTrainingScene" );
      Assert.That( create, Is.Not.Null, "Corner refinement needs its own scene entry." );
      string[] originals = {
        "Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPathMission.unity",
        "Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPathExpanded.unity",
        "Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPath.unity",
        "Assets/Scenes/ExcavatorRuntime.unity",
        "Assets/Prefabs/ScaleTrajectoryEnvironment.prefab"
      };
      byte[][] before = originals.Select( System.IO.File.ReadAllBytes ).ToArray();
      var previous = EditorSceneManager.GetActiveScene();
      create.Invoke( null, null );
      const string path = "Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPathMissionCorner.unity";
      Assert.That( builder.GetField( "RefinementScenePath" ).GetValue( null ), Is.EqualTo( path ) );
      Assert.That( builder.GetField( "RefinementOutputPath" ).GetValue( null ),
        Is.EqualTo( "Builds/ExcavatorContinuousPathV5Refine/ExcavatorContinuousPath.x86_64" ) );
      Assert.That( builder.GetMethod( "BuildRefinementLinux64" ), Is.Not.Null );
      var scene = EditorSceneManager.OpenPreviewScene( path );
      try {
        var trackers = scene.GetRootGameObjects().SelectMany(
          root => root.GetComponentsInChildren<MonoBehaviour>( true ) )
          .Where( component => component != null && component.GetType().Name == "ContinuousPathTracking" ).ToArray();
        Assert.That( trackers.Length, Is.EqualTo( 4 ) );
        int missionCourses = 0;
        foreach ( var tracker in trackers ) {
          var settings = new SerializedObject( tracker );
          Assert.That( settings.FindProperty( "useGeometricContourCost" ).boolValue, Is.True );
          Assert.That( settings.FindProperty( "contourCostPerSecond" ).floatValue, Is.EqualTo( 1f ) );
          Assert.That( settings.FindProperty( "cornerContourGain" ).floatValue, Is.EqualTo( 2f ) );
          Assert.That( settings.FindProperty( "cornerInfluenceDistanceM" ).floatValue, Is.EqualTo( 0.2f ) );
          Assert.That( settings.FindProperty( "resetClockAfterHoming" ).boolValue, Is.True );
          Assert.That( settings.FindProperty( "endpointToleranceM" ).floatValue, Is.EqualTo( 0.03f ) );
          Assert.That( settings.FindProperty( "terminalSpeedMps" ).floatValue, Is.EqualTo( 0.06f ) );
          Assert.That( settings.FindProperty( "terminalPitchToleranceDeg" ).floatValue, Is.EqualTo( 20f ) );
          Assert.That( settings.FindProperty( "terminalHoldSeconds" ).floatValue, Is.EqualTo( 0.2f ) );
          if ( tracker.GetComponents<MonoBehaviour>().Any(
            component => component != null && component.GetType().Name == "ContinuousPathMissionCourse" ) )
            missionCourses++;
        }
        Assert.That( missionCourses, Is.EqualTo( 2 ), "Refinement retains the physical carry/return curriculum." );
        Assert.That( EditorSceneManager.GetActiveScene(), Is.EqualTo( previous ) );
        for ( int i = 0; i < originals.Length; ++i )
          Assert.That( System.IO.File.ReadAllBytes( originals[ i ] ), Is.EqualTo( before[ i ] ), originals[ i ] );
      }
      finally {
        EditorSceneManager.ClosePreviewScene( scene );
      }
    }

    [Test]
    public void MissionTrainingHasPhysicalHandoffStartsAndPreservesExistingScenes()
    {
      var builder = System.AppDomain.CurrentDomain.GetAssemblies()
        .Select( assembly => assembly.GetType( "RLExcavator.Editor.ContinuousPathTrainingBuild" ) )
        .First( type => type != null );
      var create = builder.GetMethod( "CreateMissionTrainingScene" );
      Assert.That( create, Is.Not.Null, "A mission course needs an isolated scene entry." );
      string[] originals = {
        "Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPathExpanded.unity",
        "Assets/Scenes/ExcavatorRuntime.unity"
      };
      var before = originals.Select( System.IO.File.ReadAllBytes ).ToArray();
      create.Invoke( null, null );
      const string path = "Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPathMission.unity";
      var previous = EditorSceneManager.GetActiveScene();
      var scene = EditorSceneManager.OpenScene( path, OpenSceneMode.Additive );
      try {
        var trackers = scene.GetRootGameObjects().SelectMany(
          root => root.GetComponentsInChildren<MonoBehaviour>( true ) )
          .Where( c => c != null && c.GetType().Name == "ContinuousPathTracking" ).ToArray();
        Assert.That( trackers.Length, Is.EqualTo( 4 ) );
        int missionCourses = 0;
        foreach ( var tracker in trackers ) {
          var course = tracker.GetComponents<MonoBehaviour>().FirstOrDefault(
            c => c.GetType().Name == "ContinuousPathMissionCourse" );
          if ( course != null ) missionCourses++;
          var tracking = new SerializedObject( tracker );
          Assert.That( tracking.FindProperty( "endpointToleranceM" ).floatValue, Is.EqualTo( 0.03f ) );
          Assert.That( tracking.FindProperty( "terminalSpeedMps" ).floatValue, Is.EqualTo( 0.06f ) );
          Assert.That( tracking.FindProperty( "resetClockAfterHoming" ).boolValue, Is.True );
          var agent = tracker.GetComponents<MonoBehaviour>().Single(
            c => c.GetType().Name == "ExcavatorTrajectoryAgent" );
          agent.GetType().GetMethod( "Initialize" ).Invoke( agent, null );
          Assert.That( (int)agent.GetType().GetField( "MaxStep" ).GetValue( agent ), Is.Zero,
            "Profile loading must not re-enable automatic timeout during physical homing." );
          agent.gameObject.SetActive( false );
          agent.GetType().GetMethod( "Initialize" ).Invoke( agent, null );
          Assert.That( (int)agent.GetType().GetField( "MaxStep" ).GetValue( agent ), Is.Zero,
            "Clock configuration must survive initialization before tracking becomes active." );
        }
        Assert.That( missionCourses, Is.EqualTo( 2 ), "Keep two original courses and add carry/return from handoff poses." );
        for ( int i = 0; i < originals.Length; i++ )
          Assert.That( System.IO.File.ReadAllBytes( originals[ i ] ), Is.EqualTo( before[ i ] ) );
      }
      finally {
        EditorSceneManager.CloseScene( scene, true );
        EditorSceneManager.SetActiveScene( previous );
      }
    }

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
