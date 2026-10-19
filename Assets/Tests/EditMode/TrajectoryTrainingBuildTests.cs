using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class TrajectoryTrainingBuildTests
  {
    [Test]
    public void BatchTrainingBuildUsesTheScaleTrainingScene()
    {
      Type type = Type.GetType( "RLExcavator.Editor.TrajectoryTrainingBuild, Assembly-CSharp-Editor" );
      Assert.That( type, Is.Not.Null );

      PropertyInfo scenePath = type.GetProperty(
        "ScenePath",
        BindingFlags.Static | BindingFlags.Public );
      Assert.That( scenePath, Is.Not.Null );
      Assert.That(
        scenePath.GetValue( null ),
        Is.EqualTo( "Assets/Scenes/Training/ExcavatorRLTrain_Scale.unity" ) );
      Assert.That( AssetDatabase.LoadAssetAtPath<SceneAsset>(
        (string)scenePath.GetValue( null ) ), Is.Not.Null );
      Assert.That(
        type.GetMethod( "BuildLinux64", BindingFlags.Static | BindingFlags.Public ),
        Is.Not.Null );
      Assert.That(
        type.GetField( "DefaultOutputPath", BindingFlags.Static | BindingFlags.NonPublic ).GetRawConstantValue(),
        Is.EqualTo( "Builds/ExcavatorGroundOrigin/ExcavatorGroundOrigin.x86_64" ) );
    }

    [Test]
    public void TrainingAgentUsesProfileReachThresholdWithoutVisualRadius()
    {
      GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
        "Assets/Prefabs/ScaleTrajectoryEnvironment.prefab" );
      Assert.That( prefab, Is.Not.Null );
      MonoBehaviour agent = Array.Find(
        prefab.GetComponentsInChildren<MonoBehaviour>( true ),
        component => component.GetType().FullName == "RLExcavator.ExcavatorTrajectoryAgent" );
      Assert.That( agent, Is.Not.Null );
      FieldInfo useVisualRadius = agent.GetType().GetField(
        "includeTargetVisualRadiusInThreshold", BindingFlags.Instance | BindingFlags.Public );
      Assert.That( useVisualRadius, Is.Not.Null );
      Assert.That( useVisualRadius.GetValue( agent ), Is.False );
    }

    [Test]
    public void RuntimeAssetsAreStagedWhereTheLinuxPlayerLoadsThem()
    {
      string root = Path.Combine(
        Path.GetTempPath(),
        "trajectory-training-build-" + Guid.NewGuid().ToString( "N" ) );
      string outputPath = Path.Combine( root, "Training.x86_64" );
      string dataPath = Path.Combine( root, "Training_Data" );
      string grpcSource = Path.Combine(
        dataPath,
        "Plugins",
        "AnyCPU",
        "libgrpc_csharp_ext.x64.so" );
      string machineProfile = Path.Combine( root, "machine_profile.jsonc" );
      const string profileText = "// staged comment\n{\"machine_id\":\"fixture\","
        + "/* keep relative path */\"reachable_workspace_file\":\"reachable_workspaces/fixture.json\"}";
      string workspace = Path.Combine( root, "reachable_workspaces", "fixture.json" );
      const string workspaceText = "{\"coordinate_frame\":\"machine_root\",\"workspaces\":[]}";

      try {
        Directory.CreateDirectory( Path.GetDirectoryName( grpcSource ) );
        File.WriteAllText( grpcSource, "grpc" );
        File.WriteAllText( machineProfile, profileText );
        Directory.CreateDirectory( Path.GetDirectoryName( workspace ) );
        File.WriteAllText( workspace, workspaceText );

        Type type = Type.GetType(
          "RLExcavator.Editor.TrajectoryTrainingBuild, Assembly-CSharp-Editor" );
        MethodInfo stageRuntimeAssets = type.GetMethod(
          "StageRuntimeAssets",
          BindingFlags.Static | BindingFlags.Public );
        Assert.That( stageRuntimeAssets, Is.Not.Null );
        stageRuntimeAssets.Invoke( null, new object[] { outputPath, machineProfile } );

        Assert.That(
          File.ReadAllText( Path.Combine(
            dataPath,
            "Plugins",
            "x86_64",
            "libgrpc_csharp_ext.x64.so" ) ),
          Is.EqualTo( "grpc" ) );
        Assert.That(
          File.ReadAllText( Path.Combine(
            dataPath,
            "StreamingAssets",
            "machine_profile.jsonc" ) ),
          Is.EqualTo( profileText ) );
        Assert.That(
          File.ReadAllText( Path.Combine(
            dataPath, "StreamingAssets", "reachable_workspaces", "fixture.json" ) ),
          Is.EqualTo( workspaceText ) );
      }
      finally {
        if ( Directory.Exists( root ) )
          Directory.Delete( root, true );
      }
    }

    [TestCase( "{}", "relative" )]
    [TestCase( "{\"reachable_workspace_file\":42}", "relative" )]
    [TestCase( "{\"reachable_workspace_file\":\"\"}", "relative" )]
    [TestCase( "{\"reachable_workspace_file\":\"/tmp/fixture.json\"}", "relative" )]
    [TestCase( "{\"reachable_workspace_file\":\"../fixture.json\"}", "StreamingAssets" )]
    [TestCase( "{\"reachable_workspace_file\":\"reachable_workspaces/missing.json\"}", "missing" )]
    public void MissingOrUnstageableWorkspaceFailsTheBuild( string profileText, string expectedMessage )
    {
      string root = Path.Combine( Path.GetTempPath(), "trajectory-training-build-" + Guid.NewGuid().ToString( "N" ) );
      string outputPath = Path.Combine( root, "Training.x86_64" );
      string machineProfile = Path.Combine( root, "machine_profile.jsonc" );
      string grpcSource = Path.Combine( root, "Training_Data", "Plugins", "AnyCPU", "libgrpc_csharp_ext.x64.so" );
      try {
        Directory.CreateDirectory( Path.GetDirectoryName( grpcSource ) );
        File.WriteAllText( grpcSource, "grpc" );
        File.WriteAllText( machineProfile, profileText );
        Type type = Type.GetType( "RLExcavator.Editor.TrajectoryTrainingBuild, Assembly-CSharp-Editor" );
        MethodInfo stage = type.GetMethod( "StageRuntimeAssets", BindingFlags.Static | BindingFlags.Public );
        TargetInvocationException error = Assert.Throws<TargetInvocationException>(
          () => stage.Invoke( null, new object[] { outputPath, machineProfile } ) );
        Assert.That( error.InnerException, Is.InstanceOf<BuildFailedException>() );
        Assert.That( error.InnerException.Message, Does.Contain( expectedMessage ) );
        Assert.That( Directory.Exists( Path.Combine( root, "Training_Data", "StreamingAssets" ) ), Is.False );
      }
      finally {
        if ( Directory.Exists( root ) )
          Directory.Delete( root, true );
      }
    }
  }
}
