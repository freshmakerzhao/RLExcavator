using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
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
        Is.EqualTo( "Assets/Scenes/ExcavatorRLTrain_Scale.unity" ) );
      Assert.That( AssetDatabase.LoadAssetAtPath<SceneAsset>(
        (string)scenePath.GetValue( null ) ), Is.Not.Null );
      Assert.That(
        type.GetMethod( "BuildLinux64", BindingFlags.Static | BindingFlags.Public ),
        Is.Not.Null );
    }

    [Test]
    public void TrainingAgentUsesProfileReachThresholdWithoutVisualRadius()
    {
      GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
        "Assets/perfabs/ScaleTrajectoryEnvironment.prefab" );
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
      string machineProfile = Path.Combine( root, "machine_profile.json" );

      try {
        Directory.CreateDirectory( Path.GetDirectoryName( grpcSource ) );
        File.WriteAllText( grpcSource, "grpc" );
        File.WriteAllText( machineProfile, "profile" );

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
            "machine_profile.json" ) ),
          Is.EqualTo( "profile" ) );
      }
      finally {
        if ( Directory.Exists( root ) )
          Directory.Delete( root, true );
      }
    }
  }
}
