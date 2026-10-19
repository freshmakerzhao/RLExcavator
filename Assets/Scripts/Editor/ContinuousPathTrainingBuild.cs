using System.IO;
using System.Linq;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RLExcavator.Editor
{
  /// <summary>
  /// Creates isolated short/expanded path training experiments. It does not modify the
  /// established training scenes/prefab or establish full loading-cycle success.
  /// </summary>
  public static class ContinuousPathTrainingBuild
  {
    public const string ScenePath =
      "Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPath.unity";
    public const string OutputPath =
      "Builds/ExcavatorContinuousPathV3/ExcavatorContinuousPath.x86_64";
    public const string ExpandedScenePath =
      "Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPathExpanded.unity";
    public const string ExpandedOutputPath =
      "Builds/ExcavatorContinuousPathV4/ExcavatorContinuousPath.x86_64";

    [MenuItem( "Tools/Excavator/Continuous Path/Create Expanded Training Scene" )]
    public static void CreateExpandedScene()
    {
      if ( EditorApplication.isPlayingOrWillChangePlaymode )
        throw new BuildFailedException( "Exit Play Mode before creating the training scene." );
      if ( AssetDatabase.LoadAssetAtPath<SceneAsset>( ExpandedScenePath ) != null )
        throw new BuildFailedException( $"Refusing to overwrite existing scene: {ExpandedScenePath}" );
      if ( !AssetDatabase.CopyAsset( ScenePath, ExpandedScenePath ) )
        throw new BuildFailedException( "Could not copy the short-path training scene." );
      UpdateExpandedTrainingScene();
    }

    [MenuItem( "Tools/Excavator/Continuous Path/Update Expanded Training Scene" )]
    public static void UpdateExpandedTrainingScene()
      => UpdateScene( ExpandedScenePath, expanded: true );

    [MenuItem( "Tools/Excavator/Continuous Path/Create Training Scene" )]
    public static void CreateScene()
    {
      if ( EditorApplication.isPlayingOrWillChangePlaymode )
        throw new BuildFailedException( "Exit Play Mode before creating the training scene." );
      if ( AssetDatabase.LoadAssetAtPath<SceneAsset>( ScenePath ) != null )
        throw new BuildFailedException( $"Refusing to overwrite existing scene: {ScenePath}" );
      if ( !AssetDatabase.CopyAsset( TrajectoryTrainingBuild.TrackingBaseScenePath, ScenePath ) )
        throw new BuildFailedException( "Could not copy the TrackingBase training scene." );

      UpdateTrainingScene();
    }

    [MenuItem( "Tools/Excavator/Continuous Path/Update Training Scene" )]
    public static void UpdateTrainingScene()
      => UpdateScene( ScenePath, expanded: false );

    private static void UpdateScene( string scenePath, bool expanded )
    {
      if ( EditorApplication.isPlayingOrWillChangePlaymode )
        throw new BuildFailedException( "Exit Play Mode before updating the training scene." );
      Scene previousActiveScene = SceneManager.GetActiveScene();
      Scene scene = SceneManager.GetSceneByPath( scenePath );
      bool openedHere = !scene.isLoaded;
      try {
        if ( openedHere )
          scene = EditorSceneManager.OpenScene( scenePath, OpenSceneMode.Additive );
        GameObject[] roots = scene.GetRootGameObjects();
        ExcavatorTrajectoryAgent[] agents = roots
          .SelectMany( root => root.GetComponentsInChildren<ExcavatorTrajectoryAgent>( true ) )
          .OrderBy( agent => agent.environmentRoot.name )
          .ToArray();
        if ( agents.Length != 4 )
          throw new BuildFailedException( $"Expected four training agents, found {agents.Length}." );

        foreach ( TrajectoryTrainingCurriculum curriculum in roots.SelectMany(
          root => root.GetComponentsInChildren<TrajectoryTrainingCurriculum>( true ) ) ) {
          curriculum.stage = TrajectoryTrainingCurriculum.CurriculumStage.Disabled;
          curriculum.applyOnAwake = false;
          curriculum.applyInEditMode = false;
          curriculum.enabled = false;
          RecordSceneOverride( curriculum );
        }
        for ( int i = 0; i < agents.Length; i++ )
          ConfigureAgent( agents[ i ], i, expanded );

        EditorSceneManager.MarkSceneDirty( scene );
        if ( !EditorSceneManager.SaveScene( scene ) )
          throw new BuildFailedException( $"Could not save training scene: {scenePath}" );
        Debug.Log( $"Continuous-path training scene configured: {scenePath}, expanded={expanded}" );
      }
      finally {
        if ( openedHere && scene.IsValid() && scene.isLoaded )
          EditorSceneManager.CloseScene( scene, true );
        if ( previousActiveScene.IsValid() && previousActiveScene.isLoaded )
          SceneManager.SetActiveScene( previousActiveScene );
      }
    }

    private static void ConfigureAgent( ExcavatorTrajectoryAgent agent, int index, bool expanded )
    {
      ContinuousPathTracking tracking = agent.GetComponent<ContinuousPathTracking>();
      if ( tracking == null )
        tracking = agent.gameObject.AddComponent<ContinuousPathTracking>();
      tracking.generateTrainingReference = true;
      tracking.preserveTrainingPathShape = expanded;
      tracking.endpointToleranceM = 0.03f;
      tracking.corridorRadiusM = 0.08f;
      tracking.previewDistancesM = new Vector3( 0.03f, 0.08f, 0.15f );
      tracking.terminalSpeedMps = 0.06f;
      tracking.terminalPitchToleranceDeg = 20f;
      tracking.terminalHoldSeconds = 0.2f;
      tracking.pitchProgressWeight = 10f;
      tracking.pitchCostPerSecond = 3f;
      RecordSceneOverride( tracking );

      // 每个物理步都包含两种任务；不受某类任务提前结束并重新抽样影响。
      agent.randomizeTaskModeOnEpisodeBegin = false;
      agent.taskMode = index % 2 == 0
        ? ExcavatorTrajectoryAgent.TrajectoryTaskMode.MoveToDig
        : ExcavatorTrajectoryAgent.TrajectoryTaskMode.CarryMaterial;
      RecordSceneOverride( agent );

      BehaviorParameters behavior = agent.GetComponent<BehaviorParameters>();
      behavior.Model = null;
      behavior.BehaviorType = BehaviorType.Default;
      behavior.BehaviorName = "ExcavatorContinuousPath";
      behavior.BrainParameters.VectorObservationSize = ContinuousPathTracking.ObservationSize;
      behavior.BrainParameters.NumStackedVectorObservations = 1;
      behavior.BrainParameters.ActionSpec = ActionSpec.MakeContinuous( 4 );
      RecordSceneOverride( behavior );

      DecisionRequester decision = agent.GetComponent<DecisionRequester>();
      decision.DecisionPeriod = 5;
      decision.DecisionStep = 0;
      decision.TakeActionsBetweenDecisions = true;
      RecordSceneOverride( decision );
      ConfigureCurvedPaths( agent.trajectory, agent.bucketTip, expanded );
    }

    private static void ConfigureCurvedPaths( TrajectoryProvider provider, Transform tip, bool expanded )
    {
      provider.randomizePathOnReset = true;
      provider.useCurrentTipAsPathStart = true;
      provider.currentTipPathStart = tip;
      provider.randomWaypointCount = expanded ? 9 : 5;
      provider.reachableWorkspaceMinEndpointDistance = expanded ? 0.35f : 0.06f;
      provider.reachableWorkspaceMaxEndpointDistance = expanded ? 1f : 0.20f;
      provider.nearbyPathParameterRadius = expanded ? 0.5f : 0.12f;
      provider.reachableWorkspaceUseCurvedPaths = true;
      provider.reachableWorkspaceCurveStrength = expanded ? 0.08f : 0.03f;
      provider.reachableWorkspaceDirectionJitter = 0f;
      provider.reachableWorkspaceHeightJitter = 0f;
      provider.reachableWorkspaceDepthJitter = 0f;
      RecordSceneOverride( provider );
    }

    private static void RecordSceneOverride( Object component )
    {
      EditorUtility.SetDirty( component );
      PrefabUtility.RecordPrefabInstancePropertyModifications( component );
    }

    [MenuItem( "Tools/Excavator/Continuous Path/Build Linux Player" )]
    public static void BuildLinux64()
      => BuildScene( ScenePath, OutputPath );

    [MenuItem( "Tools/Excavator/Continuous Path/Build Expanded Linux Player" )]
    public static void BuildExpandedLinux64()
      => BuildScene( ExpandedScenePath, ExpandedOutputPath );

    private static void BuildScene( string scenePath, string outputPath )
    {
      if ( AssetDatabase.LoadAssetAtPath<SceneAsset>( scenePath ) == null )
        throw new BuildFailedException( $"Create the training scene first: {scenePath}" );
      Directory.CreateDirectory( Path.GetDirectoryName( outputPath ) );
      // Keep the existing Mono project setting and global build-scene list.
      BuildReport report = BuildPipeline.BuildPlayer( new BuildPlayerOptions {
        scenes = new[] { scenePath },
        locationPathName = outputPath,
        target = BuildTarget.StandaloneLinux64,
        options = BuildOptions.None
      } );
      if ( report.summary.result != BuildResult.Succeeded )
        throw new BuildFailedException( $"Continuous-path build failed: {report.summary.result}" );

      string machineProfilePath = Path.GetFullPath(
        Path.Combine( Application.dataPath, "../../shared/machine_profile.jsonc" ) );
      TrajectoryTrainingBuild.StageRuntimeAssets( outputPath, machineProfilePath );
      Debug.Log( $"Continuous-path training build succeeded: {outputPath}" );
    }
  }
}
