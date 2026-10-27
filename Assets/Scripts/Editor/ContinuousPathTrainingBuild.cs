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

    public const string MissionScenePath =
      "Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPathMission.unity";
    public const string MissionOutputPath =
      "Builds/ExcavatorContinuousPathV5/ExcavatorContinuousPath.x86_64";
    public const string RefinementScenePath =
      "Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPathMissionCorner.unity";
    public const string RefinementOutputPath =
      "Builds/ExcavatorContinuousPathV5Refine/ExcavatorContinuousPath.x86_64";
    public const string BenchmarkScenePath =
      "Assets/Scenes/Training/ExcavatorRLBench_ContinuousPathV5.unity";
    public const string BenchmarkOutputPath =
      "Builds/ExcavatorContinuousPathV5Benchmark/ExcavatorContinuousPath.x86_64";
    public const string CurveScenePath =
      "Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPathMissionCurve.unity";
    public const string CurveOutputPath =
      "Builds/ExcavatorContinuousPathV5Curve/ExcavatorContinuousPath.x86_64";

    [MenuItem( "Tools/Excavator/Continuous Path/Create Fixed Benchmark Scene" )]
    public static void CreateBenchmarkScene()
    {
      CopyExperimentScene( RefinementScenePath, BenchmarkScenePath );
      Scene previous = SceneManager.GetActiveScene();
      Scene scene = EditorSceneManager.OpenScene( BenchmarkScenePath, OpenSceneMode.Additive );
      try {
        var agents = scene.GetRootGameObjects().SelectMany(
          root => root.GetComponentsInChildren<ExcavatorTrajectoryAgent>( true ) )
          .OrderBy( a => a.environmentRoot.name ).ToArray();
        for ( int i = 1; i < agents.Length; ++i )
          agents[ i ].environmentRoot.gameObject.SetActive( false );
        var agent = agents[ 0 ];
        var serialized = new SerializedObject( agent );
        serialized.FindProperty( "randomizeCommandDeadzonesOnEpisodeBegin" ).boolValue = false;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        if ( agent.GetComponent<ContinuousPathMissionCourse>() == null )
          agent.gameObject.AddComponent<ContinuousPathMissionCourse>();
        agent.gameObject.AddComponent<ContinuousPathBenchmark>();
        RecordSceneOverride( agent );
        EditorSceneManager.MarkSceneDirty( scene );
        EditorSceneManager.SaveScene( scene );
      }
      finally {
        EditorSceneManager.CloseScene( scene, true );
        SceneManager.SetActiveScene( previous );
      }
    }

    private static void CopyExperimentScene( string source, string destination )
    {
      if ( EditorApplication.isPlayingOrWillChangePlaymode )
        throw new BuildFailedException( "Exit Play Mode before creating an experiment." );
      if ( AssetDatabase.LoadAssetAtPath<SceneAsset>( destination ) != null )
        throw new BuildFailedException( $"Refusing to overwrite existing scene: {destination}" );
      if ( !AssetDatabase.CopyAsset( source, destination ) )
        throw new BuildFailedException( $"Could not copy experiment scene: {source}" );
    }

    [MenuItem( "Tools/Excavator/Continuous Path/Create Curve Training Scene" )]
    public static void CreateCurveScene()
    {
      CopyExperimentScene( MissionScenePath, CurveScenePath );
      Scene previous = SceneManager.GetActiveScene();
      Scene scene = EditorSceneManager.OpenScene( CurveScenePath, OpenSceneMode.Additive );
      try {
        foreach ( var tracking in scene.GetRootGameObjects().SelectMany(
          root => root.GetComponentsInChildren<ContinuousPathTracking>( true ) ) ) {
          tracking.smoothTrainingReferenceProbability = 0.7f;
          tracking.smoothCornerCutDistanceM = 0.08f;
          tracking.smoothSampleSpacingM = 0.01f;
          RecordSceneOverride( tracking );
        }
        EditorSceneManager.MarkSceneDirty( scene );
        EditorSceneManager.SaveScene( scene );
      }
      finally {
        EditorSceneManager.CloseScene( scene, true );
        SceneManager.SetActiveScene( previous );
      }
    }

    [System.Serializable]
    private class MissionSeedSet { public MissionSeed[] cases; }
    [System.Serializable]
    private class MissionSeed {
      public string name;
      public string source;
      public Vector4 startJointPose;
      public Vector3[] routeRosM;
    }

    [MenuItem( "Tools/Excavator/Continuous Path/Create Mission Training Scene" )]
    public static void CreateMissionTrainingScene()
    {
      if ( EditorApplication.isPlayingOrWillChangePlaymode )
        throw new BuildFailedException( "Exit Play Mode before creating the mission course." );
      if ( AssetDatabase.LoadAssetAtPath<SceneAsset>( MissionScenePath ) == null
        && !AssetDatabase.CopyAsset( ExpandedScenePath, MissionScenePath ) )
        throw new BuildFailedException( "Could not copy the expanded training scene." );
      UpdateMissionTrainingScene();
    }

    [MenuItem( "Tools/Excavator/Continuous Path/Update Mission Training Scene" )]
    public static void UpdateMissionTrainingScene()
      => UpdateScene( MissionScenePath, expanded: true, mission: true );

    [MenuItem( "Tools/Excavator/Continuous Path/Create Corner Refinement Training Scene" )]
    public static void CreateRefinementTrainingScene()
    {
      if ( EditorApplication.isPlayingOrWillChangePlaymode )
        throw new BuildFailedException( "Exit Play Mode before creating the corner refinement course." );
      if ( AssetDatabase.LoadAssetAtPath<SceneAsset>( RefinementScenePath ) == null
        && !AssetDatabase.CopyAsset( MissionScenePath, RefinementScenePath ) )
        throw new BuildFailedException( "Could not copy the mission training scene." );
      UpdateScene( RefinementScenePath, expanded: true, mission: true, refinement: true );
    }

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

    private static void UpdateScene( string scenePath, bool expanded, bool mission = false, bool refinement = false )
    {
      if ( EditorApplication.isPlayingOrWillChangePlaymode )
        throw new BuildFailedException( "Exit Play Mode before updating the training scene." );
      Scene previousActiveScene = SceneManager.GetActiveScene();
      Scene scene = SceneManager.GetSceneByPath( scenePath );
      bool openedHere = !scene.isLoaded;
      Scene runtimeScene = default;
      bool openedRuntimeHere = false;
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

        ExcavatorFrameRegistry runtimeFrames = null;
        if ( mission ) {
          const string runtimePath = "Assets/Scenes/ExcavatorRuntime.unity";
          runtimeScene = SceneManager.GetSceneByPath( runtimePath );
          openedRuntimeHere = !runtimeScene.isLoaded;
          if ( openedRuntimeHere )
            runtimeScene = EditorSceneManager.OpenScene( runtimePath, OpenSceneMode.Additive );
          runtimeFrames = runtimeScene.GetRootGameObjects().SelectMany(
            root => root.GetComponentsInChildren<ExcavatorFrameRegistry>( true ) )
            .Single( frames => frames.ModelRoot != null && frames.BucketTip != null );
        }

        foreach ( TrajectoryTrainingCurriculum curriculum in roots.SelectMany(
          root => root.GetComponentsInChildren<TrajectoryTrainingCurriculum>( true ) ) ) {
          curriculum.stage = TrajectoryTrainingCurriculum.CurriculumStage.Disabled;
          curriculum.applyOnAwake = false;
          curriculum.applyInEditMode = false;
          curriculum.enabled = false;
          RecordSceneOverride( curriculum );
        }
        for ( int i = 0; i < agents.Length; i++ ) {
          ConfigureAgent( agents[ i ], i, expanded );
          if ( mission ) {
            ContinuousPathRuntimeAlignment.Apply( agents[ i ], runtimeFrames );
            agents[ i ].GetComponent<ContinuousPathTracking>().resetClockAfterHoming = true;
            RecordSceneOverride( agents[ i ].GetComponent<ContinuousPathTracking>() );
            if ( i >= 2 ) ConfigureMissionCourse( agents[ i ], i - 2 );
          }
          if ( refinement ) {
            ContinuousPathTracking tracking = agents[ i ].GetComponent<ContinuousPathTracking>();
            tracking.useGeometricContourCost = true;
            tracking.contourCostPerSecond = 1f;
            tracking.cornerContourGain = 2f;
            tracking.cornerInfluenceDistanceM = 0.2f;
            RecordSceneOverride( tracking );
          }
        }

        EditorSceneManager.MarkSceneDirty( scene );
        if ( !EditorSceneManager.SaveScene( scene ) )
          throw new BuildFailedException( $"Could not save training scene: {scenePath}" );
        Debug.Log( $"Continuous-path training scene configured: {scenePath}, expanded={expanded}" );
      }
      finally {
        if ( openedRuntimeHere && runtimeScene.IsValid() && runtimeScene.isLoaded )
          EditorSceneManager.CloseScene( runtimeScene, true );
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

    private static void ConfigureMissionCourse( ExcavatorTrajectoryAgent agent, int seedIndex )
    {
      var asset = AssetDatabase.LoadAssetAtPath<TextAsset>( "Assets/TrainingData/ContinuousPathMissionSeeds.json" );
      if ( asset == null ) throw new BuildFailedException( "Missing recorded mission training seeds." );
      var seeds = JsonUtility.FromJson<MissionSeedSet>( asset.text );
      if ( seeds == null || seeds.cases == null || seeds.cases.Length != 2 )
        throw new BuildFailedException( "Expected recorded carry and return training seeds." );
      MissionSeed seed = seeds.cases[ seedIndex ];
      var course = agent.GetComponent<ContinuousPathMissionCourse>();
      if ( course == null ) course = agent.gameObject.AddComponent<ContinuousPathMissionCourse>();
      course.startJointPose = seed.startJointPose;
      course.routeRosM = seed.routeRosM;
      agent.taskMode = seedIndex == 0
        ? ExcavatorTrajectoryAgent.TrajectoryTaskMode.CarryMaterial
        : ExcavatorTrajectoryAgent.TrajectoryTaskMode.MoveToDig;
      agent.trajectory.randomizePathOnReset = false;
      agent.controller.warnOnHomeTimeout = true;
      RecordSceneOverride( agent.controller );
      RecordSceneOverride( agent.trajectory );
      RecordSceneOverride( agent );
      RecordSceneOverride( course );
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

    [MenuItem( "Tools/Excavator/Continuous Path/Build Mission Linux Player" )]
    public static void BuildMissionLinux64()
      => BuildScene( MissionScenePath, MissionOutputPath );

    [MenuItem( "Tools/Excavator/Continuous Path/Build Corner Refinement Linux Player" )]
    public static void BuildRefinementLinux64()
      => BuildScene( RefinementScenePath, RefinementOutputPath );

    [MenuItem( "Tools/Excavator/Continuous Path/Build Fixed Benchmark Linux Player" )]
    public static void BuildBenchmarkLinux64()
      => BuildScene( BenchmarkScenePath, BenchmarkOutputPath );

    [MenuItem( "Tools/Excavator/Continuous Path/Build Curve Linux Player" )]
    public static void BuildCurveLinux64()
      => BuildScene( CurveScenePath, CurveOutputPath );

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
