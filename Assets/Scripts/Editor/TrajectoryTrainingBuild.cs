using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace RLExcavator.Editor
{
  /// <summary>
  /// Reproducible batch build for the existing scale trajectory training scene.
  /// The resulting player is launched by mlagents-learn via --env.
  /// </summary>
  public static class TrajectoryTrainingBuild
  {
    private const string DefaultOutputPath =
      "Builds/ExcavatorTrajectoryTraining/ExcavatorTrajectoryTraining.x86_64";

    public static string ScenePath => "Assets/Scenes/ExcavatorRLTrain_Scale.unity";

    public static void BuildLinux64()
    {
      string outputPath = GetArgValue( "-outputPath", DefaultOutputPath );
      if ( AssetDatabase.LoadAssetAtPath<SceneAsset>( ScenePath ) == null )
        throw new BuildFailedException( $"Missing trajectory training scene: {ScenePath}" );

      string outputDirectory = Path.GetDirectoryName( outputPath );
      if ( !string.IsNullOrEmpty( outputDirectory ) )
        Directory.CreateDirectory( outputDirectory );

      PlayerSettings.SetScriptingBackend(
        NamedBuildTarget.Standalone,
        ScriptingImplementation.Mono2x );

      var options = new BuildPlayerOptions {
        scenes = new[] { ScenePath },
        locationPathName = outputPath,
        target = BuildTarget.StandaloneLinux64,
        options = BuildOptions.None
      };
      BuildReport report = BuildPipeline.BuildPlayer( options );
      if ( report.summary.result != BuildResult.Succeeded )
        throw new BuildFailedException(
          $"Trajectory training build failed: {report.summary.result}" );

      string machineProfilePath = Path.GetFullPath(
        Path.Combine( Application.dataPath, "../../shared/machine_profile.json" ) );
      StageRuntimeAssets( outputPath, machineProfilePath );

      Debug.Log( $"Trajectory training build succeeded: {outputPath}" );
    }

    public static void StageRuntimeAssets(
      string outputPath,
      string machineProfilePath )
    {
      if ( string.IsNullOrWhiteSpace( outputPath ) )
        throw new BuildFailedException( "Training player output path is required." );
      if ( !File.Exists( machineProfilePath ) )
        throw new BuildFailedException(
          $"Machine profile is missing: {machineProfilePath}" );

      string outputDirectory = Path.GetDirectoryName( outputPath );
      string playerName = Path.GetFileNameWithoutExtension( outputPath );
      string dataPath = Path.Combine( outputDirectory, playerName + "_Data" );
      string grpcSource = Path.Combine(
        dataPath,
        "Plugins",
        "AnyCPU",
        "libgrpc_csharp_ext.x64.so" );
      if ( !File.Exists( grpcSource ) )
        throw new BuildFailedException(
          $"ML-Agents Linux gRPC plugin is missing: {grpcSource}" );

      string grpcTargetDirectory = Path.Combine( dataPath, "Plugins", "x86_64" );
      string streamingAssetsDirectory = Path.Combine( dataPath, "StreamingAssets" );
      Directory.CreateDirectory( grpcTargetDirectory );
      Directory.CreateDirectory( streamingAssetsDirectory );
      File.Copy(
        grpcSource,
        Path.Combine( grpcTargetDirectory, Path.GetFileName( grpcSource ) ),
        true );
      File.Copy(
        machineProfilePath,
        Path.Combine( streamingAssetsDirectory, "machine_profile.json" ),
        true );
    }

    private static string GetArgValue( string name, string fallback )
    {
      string[] args = System.Environment.GetCommandLineArgs();
      for ( int i = 0; i < args.Length - 1; i++ )
        if ( args[ i ] == name )
          return args[ i + 1 ];

      return fallback;
    }
  }
}
