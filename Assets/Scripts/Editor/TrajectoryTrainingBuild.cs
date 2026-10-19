using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
      "Builds/ExcavatorGroundOrigin/ExcavatorGroundOrigin.x86_64";
    private const string TrackingBaseOutputPath =
      "Builds/ExcavatorTrackingBase/ExcavatorTrackingBase.x86_64";

    public static string ScenePath => "Assets/Scenes/Training/ExcavatorRLTrain_Scale.unity";
    public static string TrackingBaseScenePath =>
      "Assets/Scenes/Training/ExcavatorRLTrain_TrackingBase.unity";

    public static void BuildLinux64()
    {
      BuildLinux64FromScene( ScenePath, GetArgValue( "-outputPath", DefaultOutputPath ) );
    }

    public static void BuildLinux64TrackingBase()
    {
      BuildLinux64FromScene(
        TrackingBaseScenePath,
        GetArgValue( "-outputPath", TrackingBaseOutputPath ) );
    }

    private static void BuildLinux64FromScene( string scenePath, string outputPath )
    {
      if ( AssetDatabase.LoadAssetAtPath<SceneAsset>( scenePath ) == null )
        throw new BuildFailedException( $"Missing trajectory training scene: {scenePath}" );

      string outputDirectory = Path.GetDirectoryName( outputPath );
      if ( !string.IsNullOrEmpty( outputDirectory ) )
        Directory.CreateDirectory( outputDirectory );

      PlayerSettings.SetScriptingBackend(
        NamedBuildTarget.Standalone,
        ScriptingImplementation.Mono2x );

      var options = new BuildPlayerOptions {
        scenes = new[] { scenePath },
        locationPathName = outputPath,
        target = BuildTarget.StandaloneLinux64,
        options = BuildOptions.None
      };
      BuildReport report = BuildPipeline.BuildPlayer( options );
      if ( report.summary.result != BuildResult.Succeeded )
        throw new BuildFailedException(
          $"Trajectory training build failed: {report.summary.result}" );

      string machineProfilePath = Path.GetFullPath(
        Path.Combine( Application.dataPath, "../../shared/machine_profile.jsonc" ) );
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
      string workspaceFile = ReadWorkspaceRelativePath( machineProfilePath );
      string workspaceSource = Path.GetFullPath( Path.Combine(
        Path.GetDirectoryName( Path.GetFullPath( machineProfilePath ) ), workspaceFile ) );
      string workspaceTarget = Path.GetFullPath( Path.Combine( streamingAssetsDirectory, workspaceFile ) );
      if ( !workspaceTarget.StartsWith(
        Path.GetFullPath( streamingAssetsDirectory ) + Path.DirectorySeparatorChar,
        System.StringComparison.Ordinal ) )
        throw new BuildFailedException( "Workspace path must stay inside StreamingAssets." );
      if ( !File.Exists( workspaceSource ) )
        throw new BuildFailedException( $"Reachable workspace is missing: {workspaceSource}" );

      // 配置与工作空间成对部署，避免播放器悄悄退回 prefab 中的旧原点锚点。
      Directory.CreateDirectory( grpcTargetDirectory );
      Directory.CreateDirectory( streamingAssetsDirectory );
      Directory.CreateDirectory( Path.GetDirectoryName( workspaceTarget ) );
      File.Copy(
        grpcSource,
        Path.Combine( grpcTargetDirectory, Path.GetFileName( grpcSource ) ),
        true );
      File.Copy(
        machineProfilePath,
        Path.Combine( streamingAssetsDirectory, "machine_profile.jsonc" ),
        true );
      File.Copy( workspaceSource, workspaceTarget, true );
    }

    private static string ReadWorkspaceRelativePath( string machineProfilePath )
    {
      try {
        // 与 MachineProfileLoader 一样使用现有 Newtonsoft 读取带注释配置。
        using ( var reader = new JsonTextReader( File.OpenText( machineProfilePath ) ) ) {
          reader.DateParseHandling = DateParseHandling.None;
          JObject profile = JObject.Load( reader, new JsonLoadSettings {
            CommentHandling = CommentHandling.Ignore
          } );
          JToken value = profile[ "reachable_workspace_file" ];
          string path = value?.Type == JTokenType.String ? value.Value<string>() : null;
          if ( string.IsNullOrWhiteSpace( path ) || Path.IsPathRooted( path ) )
            throw new BuildFailedException( "reachable_workspace_file must be a nonempty relative path." );
          return path;
        }
      }
      catch ( JsonException exception ) {
        throw new BuildFailedException( "Invalid machine profile JSONC: " + exception.Message );
      }
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
