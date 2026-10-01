using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Unity.InferenceEngine;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Actuators;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RLExcavator.Editor
{
  /// <summary>Historical replay helper. Requires restoring the archived demonstration scene.</summary>
  public static class E4HistoricalReplayBuild
  {
    private const string SourceScene = "Assets/Scenes/ICRA2027_RLTrackingExperiments.unity";
    private const string TemporaryScene = "Assets/Scenes/E4HistoricalReplayBuildTemp.unity";

    public static bool CanRestoreSceneSetup(SceneSetup[] setup)
    {
      return setup != null && setup.Any(s => s.isLoaded && s.isActive) &&
        setup.Where(s => s.isLoaded).All(s => !string.IsNullOrWhiteSpace(s.path));
    }

    public static void RecoverAndBuildLinux64()
    {
      if (!Application.isBatchMode) throw new BuildFailedException("E4 recovery is batchmode only");
      EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
      if (AssetDatabase.LoadAssetAtPath<SceneAsset>(TemporaryScene) != null && !AssetDatabase.DeleteAsset(TemporaryScene))
        throw new BuildFailedException("Could not remove this E4 build's temporary scene");
      BuildLinux64();
    }

    public static void BuildLinux64()
    {
      if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SourceScene) == null)
        throw new BuildFailedException("Historical E4 scene was retired; restore it from the historical revision before replay: " + SourceScene);
      string modelPath = E4HistoricalReplayRunner.Argument("-e4ModelPath");
      string output = E4HistoricalReplayRunner.Argument("-outputPath");
      string profilePath = E4HistoricalReplayRunner.Argument("-e4ProfilePath");
      if (string.IsNullOrWhiteSpace(profilePath) || !File.Exists(profilePath))
        throw new BuildFailedException("-e4ProfilePath must name the frozen historical machine profile");
      MachineProfileLoader.LoadFromPath(profilePath);
      if (string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(output))
        throw new BuildFailedException("-e4ModelPath and -outputPath are required");
      if (AssetDatabase.LoadAssetAtPath<SceneAsset>(TemporaryScene) != null)
        throw new BuildFailedException("Temporary E4 scene exists; refusing to overwrite it");
      var model = AssetDatabase.LoadAssetAtPath<ModelAsset>(modelPath);
      if (model == null) throw new BuildFailedException("Missing ONNX asset: " + modelPath);
      string hash;
      using (var sha = SHA256.Create())
      using (var stream = File.OpenRead(modelPath))
        hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
      var previous = EditorSceneManager.GetSceneManagerSetup();
      try {
        if (!AssetDatabase.CopyAsset(SourceScene, TemporaryScene))
          throw new BuildFailedException("Cannot copy demonstration scene for isolated E4 build");
        var scene = EditorSceneManager.OpenScene(TemporaryScene, OpenSceneMode.Single);
        var agents = UnityEngine.Object.FindObjectsByType<ExcavatorTrajectoryAgent>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (agents.Length != 1) throw new BuildFailedException("Replay scene must have exactly one trajectory agent");
        var agent = agents[0];
        if (agent.frameRegistry == null || agent.frameRegistry.MachineRoot == null || agent.bucketTip == null || agent.controller == null)
          throw new BuildFailedException("Scale replay scene must explicitly bind machine frame, bucket tip and controller");
        string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(agent.gameObject);
        if (!prefabPath.EndsWith("/ExcavatorScaleDown.prefab", StringComparison.Ordinal))
          throw new BuildFailedException("Replay agent is not bound to the validated scale excavator prefab: " + prefabPath);
        foreach (var component in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
          if (component is ExcavationDemoTaskManager || component is ExcavationCycleTask ||
              component is ExcavationDemoRuntimeUI || component is SimpleTrajectoryPlanner ||
              component is OpenLoopVelocityRecorder || component is ExcavatorKeyboardTester) component.enabled = false;
        var behavior = agent.GetComponent<BehaviorParameters>();
        if (behavior == null) throw new BuildFailedException("Replay agent has no behavior");
        var serializedAgent = new SerializedObject(agents[0]);
        serializedAgent.FindProperty("randomizeCommandDeadzonesOnEpisodeBegin").boolValue = false;
        serializedAgent.ApplyModifiedPropertiesWithoutUndo();
        behavior.BrainParameters.VectorObservationSize = 38;
        behavior.BrainParameters.NumStackedVectorObservations = 1;
        behavior.BrainParameters.ActionSpec = ActionSpec.MakeContinuous(4);
        behavior.Model = model;
        behavior.BehaviorType = BehaviorType.InferenceOnly;
        behavior.InferenceDevice = InferenceDevice.Burst;
        behavior.DeterministicInference = true;
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
          scenes = new[] { TemporaryScene }, locationPathName = output,
          target = BuildTarget.StandaloneLinux64, options = BuildOptions.None
        });
        if (report.summary.result != BuildResult.Succeeded)
          throw new BuildFailedException("E4 replay build failed: " + report.summary.result);
        TrajectoryTrainingBuild.StageRuntimeAssets(output, profilePath);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)), "e4_build_identity.json"),
          JsonUtility.ToJson(new E4HistoricalReplayRunner.BuildIdentity { model_sha256 = hash }, true));
        Debug.Log("[E4 replay build] model_sha256=" + hash + " output=" + output);
      } finally {
        try {
          if (CanRestoreSceneSetup(previous)) EditorSceneManager.RestoreSceneManagerSetup(previous);
          else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        } finally {
          AssetDatabase.DeleteAsset(TemporaryScene);
        }
      }
    }
  }
}
