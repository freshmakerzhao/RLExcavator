using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RLExcavator.Editor
{
  /// <summary>Run with -executeMethod RLExcavator.Editor.JevTestSceneBuilder.Build.</summary>
  public static class JevTestSceneBuilder
  {
    public const string SourceScene = "Assets/Scenes/ExcavatorRuntime.unity";
    public const string DestinationScene = "Assets/Scenes/jev_test.unity";
    public const string SourceMesh = "Assets/Environment/RuntimeSoilSurface.asset";
    public const string DestinationMesh = "Assets/JevTest/jev_soil_surface.asset";

    public static async void ConnectForValidation()
    {
      try {
        bool started = await MCPForUnity.Editor.Services.MCPServiceLocator.TransportManager.StartAsync(
          MCPForUnity.Editor.Services.Transport.TransportMode.Http);
        if (!started) throw new InvalidOperationException("The MCP HTTP validation transport did not start.");
        Debug.Log("Jev validation MCP HTTP transport started for this editor session.");
      }
      catch (Exception error) {
        Debug.LogException(error);
        if (Application.isBatchMode) EditorApplication.Exit(1);
      }
    }

    public static void RefreshOwnedPreview()
    {
      if (EditorApplication.isPlayingOrWillChangePlaymode)
        throw new InvalidOperationException("Refresh the Jev preview outside Play mode.");
      var scene = EditorSceneManager.OpenScene(DestinationScene, OpenSceneMode.Single);
      // A plane is the known AGX editor-cache reset; other edits require review.
      ValidateScene(scene, true);
      Unique<JevTestTerrain>(scene).PreviewTerrain();
      if (!EditorSceneManager.SaveScene(scene))
        throw new InvalidOperationException("Unable to save the refreshed Jev scene.");
      ValidateScene(scene);
      Debug.Log("Refreshed the owned Jev terrain preview after validating its geometry.");
    }

    public static void Build()
    {
      if (EditorApplication.isPlayingOrWillChangePlaymode)
        throw new InvalidOperationException("Build jev_test outside Play mode.");
      if (AssetDatabase.LoadAssetAtPath<SceneAsset>(DestinationScene) != null) {
        var existing = EditorSceneManager.OpenScene(DestinationScene, OpenSceneMode.Single);
        ValidateScene(existing);
        Debug.Log("Existing jev_test scene and mesh validated; nothing overwritten.");
        return;
      }
      if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SourceScene) == null ||
          AssetDatabase.LoadAssetAtPath<Mesh>(SourceMesh) == null)
        throw new InvalidOperationException("The standard runtime scene or soil mesh is missing.");

      var scene = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Single);
      var bridge = Unique<ExcavatorSimBridge>(scene);
      if (bridge.soil == null || bridge.soil.terrainTemplate == null)
        throw new InvalidOperationException("The source scene has no runtime soil template.");
      if (AssetDatabase.GetAssetPath(bridge.soil.terrainTemplate.TerrainMesh.sharedMesh) != SourceMesh)
        throw new InvalidOperationException("The source scene soil mesh changed; review before copying.");
      if (!EditorSceneManager.SaveScene(scene, DestinationScene))
        throw new InvalidOperationException("Unable to save the new jev_test scene.");

      bool meshAlreadyExists = AssetDatabase.LoadAssetAtPath<Mesh>(DestinationMesh) != null;
      if (!meshAlreadyExists && !AssetDatabase.CopyAsset(SourceMesh, DestinationMesh))
        throw new InvalidOperationException("Unable to copy the independent Jev soil mesh.");
      var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(DestinationMesh);
      if (mesh == null) throw new InvalidOperationException("The Jev mesh asset could not be loaded.");
      bridge.soil.terrainTemplate.TerrainMesh.sharedMesh = mesh;
      bridge.port = 18791;
      var initializer = bridge.gameObject.AddComponent<JevTestTerrain>();
      initializer.soil = bridge.soil;
      initializer.machineRoot = bridge.terrainTelemetry != null ?
        bridge.terrainTelemetry.machineRoot : bridge.machineRoot;
      if (initializer.machineRoot == null || initializer.machineRoot != bridge.machineRoot)
        throw new InvalidOperationException("The bridge and terrain reader must share the machine root.");
      if (meshAlreadyExists)
        ValidateMesh(initializer);
      else
        initializer.PreviewTerrain();

      EditorSceneManager.MarkSceneDirty(scene);
      if (!EditorSceneManager.SaveScene(scene))
        throw new InvalidOperationException("Unable to save Jev scene references.");
      ValidateScene(scene);
      Debug.Log("Built jev_test with its own soil mesh and loopback port 18791.");
    }

    private static T Unique<T>(Scene scene) where T : Component
    {
      var matches = scene.GetRootGameObjects()
        .SelectMany(root => root.GetComponentsInChildren<T>(true)).ToArray();
      if (matches.Length != 1)
        throw new InvalidOperationException("Expected one " + typeof(T).Name + ", found " + matches.Length);
      return matches[0];
    }

    private static void ValidateScene(Scene scene, bool allowFlatMesh = false)
    {
      var bridge = Unique<ExcavatorSimBridge>(scene);
      var initializer = Unique<JevTestTerrain>(scene);
      if (scene.path != DestinationScene || bridge.port != 18791 ||
          initializer.soil != bridge.soil || initializer.machineRoot != bridge.machineRoot ||
          !initializer.enabled || bridge.soil == null || bridge.soil.terrainTemplate == null ||
          AssetDatabase.GetAssetPath(bridge.soil.terrainTemplate.TerrainMesh.sharedMesh) != DestinationMesh)
        throw new InvalidOperationException("Existing Jev scene does not match the isolated configuration.");
      if (bridge.terrainTelemetry != null && bridge.terrainTelemetry.machineRoot != initializer.machineRoot)
        throw new InvalidOperationException("Existing terrain telemetry uses a different machine root.");
      ValidateMesh(initializer, allowFlatMesh);
    }

    private static void ValidateMesh(JevTestTerrain initializer, bool allowFlatMesh = false)
    {
      var terrain = initializer.soil.terrainTemplate;
      var mesh = terrain.TerrainMesh.sharedMesh;
      var source = AssetDatabase.LoadAssetAtPath<Mesh>(SourceMesh);
      if (source == null || mesh.vertexCount != source.vertexCount || mesh == source)
        throw new InvalidOperationException("Existing Jev mesh is missing or does not match the soil grid.");
      var expectedVertices = source.vertices;
      var vertices = mesh.vertices;
      bool shaped = true, flatMesh = true;
      for (int i = 0; i < vertices.Length; ++i) {
        var flat = expectedVertices[i];
        flat.y = 0f;
        float expected = JevTestTerrain.HeightDeltaAtVertex(terrain.transform, initializer.machineRoot, flat);
        if (Mathf.Abs(vertices[i].x - flat.x) > .00001f || Mathf.Abs(vertices[i].z - flat.z) > .00001f)
          throw new InvalidOperationException("Existing Jev mesh has unexpected geometry; it was not overwritten.");
        shaped &= Mathf.Abs(vertices[i].y - expected) <= .00001f;
        flatMesh &= Mathf.Abs(vertices[i].y) <= .00001f;
      }
      if (!shaped && !(allowFlatMesh && flatMesh))
        throw new InvalidOperationException("Existing Jev mesh has unexpected heights; it was not overwritten.");
    }
  }
}
