using System.IO;
using Unity.InferenceEngine;
using Unity.MLAgents.Policies;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RLExcavator.Editor
{
  /// <summary>
  /// Batchmode build entry for the standalone algorithm validation demo.
  /// It keeps the training scenes out of the build and prepares the demo scene for ONNX inference.
  /// </summary>
  public static class AlgorithmValidationBuild
  {
    private const string ScenePath = "Assets/Scenes/ExcavatorAlgorithmValidationScene.unity";
    private const string ModelPath = "Assets/AIModels/ExcavatorTrajectoryCarryAndMove.onnx";
    private const string DefaultOutputPath = "Builds/ExcavatorAlgorithmValidation/ExcavatorAlgorithmValidation.x86_64";

    public static void BuildLinux64()
    {
      var outputPath = GetArgValue( "-outputPath", DefaultOutputPath );
      PrepareSceneForInference();
      Build( BuildTarget.StandaloneLinux64, outputPath );
    }

    private static void PrepareSceneForInference()
    {
      var scene = EditorSceneManager.OpenScene( ScenePath, OpenSceneMode.Single );
      var model = AssetDatabase.LoadAssetAtPath<ModelAsset>( ModelPath );
      if ( model == null )
        throw new BuildFailedException( $"Missing ONNX model asset: {ModelPath}" );

      var behaviorParameters = Object.FindFirstObjectByType<BehaviorParameters>();
      if ( behaviorParameters == null )
        throw new BuildFailedException( $"No {nameof( BehaviorParameters )} found in {ScenePath}." );

      behaviorParameters.Model                  = model;
      behaviorParameters.BehaviorType           = BehaviorType.InferenceOnly;
      behaviorParameters.InferenceDevice        = InferenceDevice.Default;
      behaviorParameters.DeterministicInference = true;

      EditorSceneManager.MarkSceneDirty( scene );
      EditorSceneManager.SaveScene( scene );

      Debug.Log( $"Prepared {ScenePath} for inference with {ModelPath}." );
    }

    private static void Build( BuildTarget target, string outputPath )
    {
      var outputDirectory = Path.GetDirectoryName( outputPath );
      if ( !string.IsNullOrEmpty( outputDirectory ) )
        Directory.CreateDirectory( outputDirectory );

      PlayerSettings.SetScriptingBackend( NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x );

      var options = new BuildPlayerOptions {
        scenes           = new[] { ScenePath },
        locationPathName = outputPath,
        target           = target,
        options          = BuildOptions.None
      };

      var report = BuildPipeline.BuildPlayer( options );
      if ( report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded )
        throw new BuildFailedException( $"Algorithm validation build failed: {report.summary.result}" );

      Debug.Log( $"Algorithm validation build succeeded: {outputPath}" );
    }

    private static string GetArgValue( string name, string fallback )
    {
      var args = System.Environment.GetCommandLineArgs();
      for ( var i = 0; i < args.Length - 1; ++i )
        if ( args[ i ] == name )
          return args[ i + 1 ];

      return fallback;
    }
  }
}
