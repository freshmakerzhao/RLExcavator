using System;
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
using Object = UnityEngine.Object;

namespace RLExcavator.Editor
{
  /// <summary>Isolated real-execution proxy experiments; never updates the Runtime or original V5 scene.</summary>
  public static class RealExecutionTrainingBuild
  {
    public const string SourceScene = "Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPathMission.unity";
    public const string ProfilePath = "Assets/TrainingData/RealExecutionProfileV1.json";
    public const string CasesPath = "Assets/TrainingData/RealExecutionCasesV1.json";
    public const string TrainingScene = "Assets/Scenes/Training/ExcavatorRLTrain_RealExecutionV1.unity";
    public const string BenchmarkScene = "Assets/Scenes/Training/ExcavatorRLBench_RealExecutionV1.unity";
    public const string SamplerScene = "Assets/Scenes/Training/ExcavatorRLSample_RealExecutionV1.unity";
    public const string TrainingOutput = "Builds/ExcavatorContinuousPathRealV1/ExcavatorContinuousPath.x86_64";
    public const string BenchmarkOutput = "Builds/ExcavatorContinuousPathRealV1Benchmark/ExcavatorContinuousPath.x86_64";
    public const string SamplerOutput = "Builds/ExcavatorContinuousPathRealV1Sampler/ExcavatorContinuousPath.x86_64";

    public static void CreateTrainingScene() => CreateScene( TrainingScene, "train" );
    public static void CreateBenchmarkScene() => CreateScene( BenchmarkScene, "benchmark" );
    public static void CreateSamplerScene() => CreateScene( SamplerScene, "sampler" );
    public static void BuildTrainingLinux64() => BuildScene( TrainingScene, TrainingOutput );
    public static void BuildBenchmarkLinux64() => BuildScene( BenchmarkScene, BenchmarkOutput );
    public static void BuildSamplerLinux64() => BuildScene( SamplerScene, SamplerOutput );
    public static void CreateAndBuildLearningPlayers()
    {
      CreateTrainingScene();
      BuildTrainingLinux64();
      CreateBenchmarkScene();
      BuildBenchmarkLinux64();
    }
    public static void RebuildLearningPlayers()
    {
      BuildTrainingLinux64();
      BuildBenchmarkLinux64();
    }

    private static void CreateScene( string destination, string mode )
    {
      CheckEditMode();
      TextAsset profile = AssetDatabase.LoadAssetAtPath<TextAsset>( ProfilePath );
      TextAsset cases = AssetDatabase.LoadAssetAtPath<TextAsset>( CasesPath );
      ValidateInputs( mode, profile, cases );
      RuntimeType(); // Detect an incomplete installation before copying any scene.
      CourseType();
      CopyScene( SourceScene, destination );
      Scene previous = SceneManager.GetActiveScene();
      Scene scene = EditorSceneManager.OpenScene( destination, OpenSceneMode.Additive );
      try {
        ConfigureScene( scene, mode, profile, cases );
        EditorSceneManager.MarkSceneDirty( scene );
        if ( !EditorSceneManager.SaveScene( scene ) )
          throw new BuildFailedException( $"Could not save new experiment scene: {destination}" );
        Debug.Log( $"Real-execution scene created: {destination}, mode={mode}" );
      }
      finally {
        EditorSceneManager.CloseScene( scene, true );
        if ( previous.IsValid() && previous.isLoaded ) SceneManager.SetActiveScene( previous );
      }
    }

    public static void CopyScene( string source, string destination )
    {
      CheckEditMode();
      if ( string.IsNullOrEmpty( destination ) ||
           !destination.StartsWith( "Assets/Scenes/Training/", StringComparison.Ordinal ) )
        throw new BuildFailedException( "Experiment scene must be under Assets/Scenes/Training/." );
      if ( File.Exists( destination ) || AssetDatabase.LoadAssetAtPath<SceneAsset>( destination ) != null )
        throw new BuildFailedException( $"Refusing to overwrite existing scene: {destination}" );
      if ( AssetDatabase.LoadAssetAtPath<SceneAsset>( source ) == null ||
           !AssetDatabase.CopyAsset( source, destination ) )
        throw new BuildFailedException( $"Could not copy source training scene: {source}" );
    }

    /// <summary>Configures only the supplied in-memory scene; callers decide whether to save their new copy.</summary>
    public static void ConfigureScene( Scene scene, string mode, TextAsset profile, TextAsset cases )
    {
      ValidateInputs( mode, profile, cases );
      if ( !scene.IsValid() || !scene.isLoaded )
        throw new BuildFailedException( "The experiment scene must already be loaded." );
      var roots = scene.GetRootGameObjects();
      var agents = roots.SelectMany( root => root.GetComponentsInChildren<ExcavatorTrajectoryAgent>( true ) )
        .OrderBy( agent => agent.environmentRoot.name ).ToArray();
      if ( agents.Length != 4 )
        throw new BuildFailedException( $"Expected four source agents, found {agents.Length}." );
      foreach ( var curriculum in roots.SelectMany( root =>
        root.GetComponentsInChildren<TrajectoryTrainingCurriculum>( true ) ) ) {
        curriculum.stage = TrajectoryTrainingCurriculum.CurriculumStage.Disabled;
        curriculum.applyOnAwake = curriculum.applyInEditMode = false;
        curriculum.enabled = false;
        Record( curriculum );
      }
      for ( int index = 0; index < agents.Length; ++index ) {
        var agent = agents[ index ];
        agent.environmentRoot.gameObject.SetActive( mode == "train" || index == 0 );
        ConfigureAgent( agent, index, mode, profile, cases );
      }
    }

    private static void ConfigureAgent( ExcavatorTrajectoryAgent agent, int index, string mode,
                                        TextAsset profile, TextAsset cases )
    {
      var oldCourse = agent.GetComponent<ContinuousPathMissionCourse>();
      if ( oldCourse != null ) Object.DestroyImmediate( oldCourse );
      var oldBenchmark = agent.GetComponent<ContinuousPathBenchmark>();
      if ( oldBenchmark != null ) Object.DestroyImmediate( oldBenchmark );
      if ( agent.GetComponent( CourseType() ) == null ) agent.gameObject.AddComponent( CourseType() );
      Component runtime = agent.GetComponent( RuntimeType() ) ?? agent.gameObject.AddComponent( RuntimeType() );
      var runtimeData = new SerializedObject( runtime );
      Property( runtimeData, "runMode" ).stringValue = mode;
      Property( runtimeData, "executionProfile" ).objectReferenceValue = profile;
      Property( runtimeData, "courseCases" ).objectReferenceValue = cases;
      runtimeData.ApplyModifiedPropertiesWithoutUndo();
      Record( runtime );

      agent.enabled = mode != "sampler";
      agent.randomizeTaskModeOnEpisodeBegin = false;
      agent.taskMode = index % 2 == 0 ? ExcavatorTrajectoryAgent.TrajectoryTaskMode.MoveToDig
        : ExcavatorTrajectoryAgent.TrajectoryTaskMode.CarryMaterial;
      agent.resetPoseOnEpisodeBegin = true;
      agent.controller.warnOnHomeTimeout = true;
      agent.trajectory.randomizePathOnReset = false;
      var agentData = new SerializedObject( agent );
      Property( agentData, "randomizeCommandDeadzonesOnEpisodeBegin" ).boolValue = false;
      agentData.ApplyModifiedPropertiesWithoutUndo();
      ConfigureTracking( agent.GetComponent<ContinuousPathTracking>() );
      var behavior = agent.GetComponent<BehaviorParameters>();
      behavior.Model = null;
      behavior.BehaviorType = BehaviorType.Default;
      behavior.BehaviorName = "ExcavatorContinuousPath";
      behavior.BrainParameters.VectorObservationSize = 47;
      behavior.BrainParameters.NumStackedVectorObservations = 1;
      behavior.BrainParameters.ActionSpec = ActionSpec.MakeContinuous( 4 );
      var decision = agent.GetComponent<DecisionRequester>();
      if ( mode == "sampler" ) {
        // This ML-Agents version subscribes in Awake and does not check enabled in MakeRequests.
        if ( decision != null ) Object.DestroyImmediate( decision );
      }
      else {
        if ( decision == null ) decision = agent.gameObject.AddComponent<DecisionRequester>();
        decision.enabled = true;
        decision.DecisionPeriod = 5;
        decision.DecisionStep = 0;
        decision.TakeActionsBetweenDecisions = true;
        Record( decision );
      }
      foreach ( Object item in new Object[] { agent, agent.controller, agent.trajectory, behavior } )
        Record( item );
    }

    private static void ConfigureTracking( ContinuousPathTracking tracking )
    {
      if ( tracking == null ) throw new BuildFailedException( "Source agent is missing ContinuousPathTracking." );
      tracking.generateTrainingReference = true;
      tracking.resetClockAfterHoming = true;
      tracking.preserveTrainingPathShape = true;
      tracking.smoothTrainingReferenceProbability = 0f;
      tracking.useGeometricContourCost = false;
      tracking.contourCostPerSecond = 0.5f;
      tracking.cornerContourGain = 0f;
      tracking.endpointToleranceM = 0.03f;
      tracking.corridorRadiusM = 0.08f;
      tracking.previewDistancesM = new Vector3( 0.03f, 0.08f, 0.15f );
      tracking.terminalSpeedMps = 0.06f;
      tracking.terminalPitchToleranceDeg = 20f;
      tracking.terminalHoldSeconds = 0.2f;
      tracking.pitchProgressWeight = 10f;
      tracking.pitchCostPerSecond = 3f;
      Record( tracking );
    }

    private static void ValidateInputs( string mode, TextAsset profile, TextAsset cases )
    {
      if ( mode != "train" && mode != "benchmark" && mode != "sampler" )
        throw new BuildFailedException( $"Unknown real-execution mode: {mode}" );
      if ( profile == null ) throw new BuildFailedException( $"Missing execution profile: {ProfilePath}" );
      if ( mode != "sampler" && cases == null )
        throw new BuildFailedException( $"Sample and freeze feasible cases before creating {mode}: {CasesPath}" );
    }

    private static void BuildScene( string scene, string output )
    {
      CheckEditMode();
      if ( AssetDatabase.LoadAssetAtPath<SceneAsset>( scene ) == null )
        throw new BuildFailedException( $"Create the experiment scene first: {scene}" );
      string mode = scene == TrainingScene ? "train" : scene == BenchmarkScene ? "benchmark" : "sampler";
      Scene previous = SceneManager.GetActiveScene();
      Scene loaded = SceneManager.GetSceneByPath( scene );
      bool openedHere = !loaded.isLoaded;
      try {
        if ( openedHere ) loaded = EditorSceneManager.OpenScene( scene, OpenSceneMode.Additive );
        ValidateConfiguration( loaded, mode );
      }
      finally {
        if ( openedHere && loaded.IsValid() && loaded.isLoaded ) EditorSceneManager.CloseScene( loaded, true );
        if ( previous.IsValid() && previous.isLoaded ) SceneManager.SetActiveScene( previous );
      }
      Directory.CreateDirectory( Path.GetDirectoryName( output ) );
      BuildReport report = BuildPipeline.BuildPlayer( new BuildPlayerOptions {
        scenes = new[] { scene }, locationPathName = output,
        target = BuildTarget.StandaloneLinux64, options = BuildOptions.None
      } );
      if ( report.summary.result != BuildResult.Succeeded )
        throw new BuildFailedException( $"Real-execution build failed: {report.summary.result}" );
      // These copies are confined to this player's _Data directory, never Builds/shared or an old player.
      string machineProfile = Path.GetFullPath( Path.Combine( Application.dataPath, "../../shared/machine_profile.jsonc" ) );
      TrajectoryTrainingBuild.StageRuntimeAssets( output, machineProfile );
      Debug.Log( $"Real-execution player built: {output}" );
    }

    public static void ValidateConfiguration( Scene scene, string mode )
    {
      var agents = scene.GetRootGameObjects().SelectMany( root =>
        root.GetComponentsInChildren<ExcavatorTrajectoryAgent>( true ) ).ToArray();
      int expectedActive = mode == "train" ? 4 : 1;
      if ( agents.Length != 4 || agents.Count( a => a.gameObject.activeInHierarchy ) != expectedActive )
        throw new BuildFailedException( $"Invalid {mode} agent count in new scene." );
      foreach ( var agent in agents ) {
        Component runtime = agent.GetComponent( RuntimeType() );
        if ( runtime == null || agent.GetComponent( CourseType() ) == null )
          throw new BuildFailedException( "New scene is not configured with real-execution components." );
        var data = new SerializedObject( runtime );
        if ( Property( data, "runMode" ).stringValue != mode )
          throw new BuildFailedException( "New scene runtime mode does not match the build." );
        ValidateInputs( mode, Property( data, "executionProfile" ).objectReferenceValue as TextAsset,
          Property( data, "courseCases" ).objectReferenceValue as TextAsset );
        var decision = agent.GetComponent<DecisionRequester>();
        if ( mode == "sampler" ? agent.enabled || decision != null
          : !agent.enabled || decision == null || !decision.enabled )
          throw new BuildFailedException( $"Invalid policy request configuration in {mode} scene." );
        if ( agent.GetComponent<ContinuousPathMissionCourse>() != null ||
             agent.GetComponent<ContinuousPathBenchmark>() != null )
          throw new BuildFailedException( "Old mission or benchmark still controls the new scene." );
      }
    }

    private static SerializedProperty Property( SerializedObject data, string name )
      => data.FindProperty( name ) ?? throw new BuildFailedException( $"Missing runtime field: {name}" );
    private static Type RuntimeType()
      => Type.GetType( "RLExcavator.RealExecutionTrainingRuntime, Assembly-CSharp", true );
    private static Type CourseType()
      => Type.GetType( "RLExcavator.RealExecutionCourse, Assembly-CSharp", true );
    private static void CheckEditMode()
    {
      if ( EditorApplication.isPlayingOrWillChangePlaymode )
        throw new BuildFailedException( "Exit Play Mode before creating or building an experiment." );
    }
    private static void Record( Object item )
    {
      EditorUtility.SetDirty( item );
      PrefabUtility.RecordPrefabInstancePropertyModifications( item );
    }
  }
}
