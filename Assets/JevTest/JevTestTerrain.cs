using System;
using AGXUnity;
using AGXUnity.Model;
using UnityEngine;

namespace RLExcavator
{
  /// <summary>Only jev_test owns this terrain initializer; shared runtime code is unchanged.</summary>
  [DefaultExecutionOrder(-1100)]
  public sealed class JevTestTerrain : MonoBehaviour
  {
    public ExcavatorRuntimeSoil soil;
    public Transform machineRoot;

    public int AppliedTerrainCount { get; private set; }
    public bool IsAppliedToCurrentTerrain => soil != null && soil.ActiveTerrain != null &&
      m_appliedTerrain == soil.ActiveTerrain;

    private MovableTerrain m_appliedTerrain;

    private void OnEnable()
    {
      if (!Application.isPlaying) return;
      ValidateConfiguration();
      ScriptComponent.OnInitialized += OnTerrainInitialized;
    }

    private void Start()
    {
      // Also supports enabling this component after the terrain has initialized.
      if (soil != null && soil.ActiveTerrain != null && soil.ActiveTerrain.Native != null)
        ApplyToTerrain(soil.ActiveTerrain);
    }

    private void OnDisable() => ScriptComponent.OnInitialized -= OnTerrainInitialized;

    private void OnTerrainInitialized(ScriptComponent component)
    {
      if (soil != null && component == soil.ActiveTerrain)
        ApplyToTerrain(soil.ActiveTerrain);
    }

    private void ValidateConfiguration()
    {
      if (gameObject.scene.name != "jev_test")
        throw new InvalidOperationException("JevTestTerrain may only run in the jev_test scene.");
      if (soil == null || soil.terrainTemplate == null || machineRoot == null ||
          soil.gameObject.scene != gameObject.scene || machineRoot.gameObject.scene != gameObject.scene ||
          soil.terrainTemplate.gameObject.scene != gameObject.scene)
        throw new InvalidOperationException("Jev terrain, template and machine root must belong to this scene.");
#if UNITY_EDITOR
      var path = UnityEditor.AssetDatabase.GetAssetPath(soil.terrainTemplate.TerrainMesh.sharedMesh);
      if (!IsOwnedMeshAssetPath(path))
        throw new InvalidOperationException("Jev terrain requires its own mesh asset under Assets/JevTest/.");
#endif
    }

    private void ApplyToTerrain(MovableTerrain terrain)
    {
      if (terrain == m_appliedTerrain) return;
      ValidateConfiguration();
      if (terrain.Native == null)
        throw new InvalidOperationException("Jev terrain native initialization failed.");

      var native = terrain.Native;
      int cols = (int)native.getResolutionX(), rows = (int)native.getResolutionY();
      var toRoot = machineRoot.worldToLocalMatrix * terrain.transform.localToWorldMatrix;
      float heightAxisZ = toRoot.MultiplyVector(Vector3.up).y;
      if (Mathf.Abs(heightAxisZ - 1f) > .001f)
        throw new InvalidOperationException("Jev test requires horizontal terrain with unit vertical scale.");
      var proposed = new float[rows * cols];
      var vertices = terrain.TerrainMesh.sharedMesh.vertices;
      if (vertices.Length != proposed.Length)
        throw new InvalidOperationException("Jev test expects the regular terrain mesh without side skirts.");

      for (int row = 0; row < rows; ++row) {
        for (int col = 0; col < cols; ++col) {
          int k = row * cols + col;
          var idx = new agx.Vec2i(cols - 1 - col, rows - 1 - row);
          float oldHeight = (float)native.getHeight(idx);
          var world = native.getSurfacePositionWorld(idx);
          var local = machineRoot.InverseTransformPoint(
            new Vector3(-(float)world.x, (float)world.y, (float)world.z));
          float delta = HeightDelta(new Vector2(local.z, -local.x));
          proposed[k] = Mathf.Max((float)native.getMinimumHeight(idx) + .02f,
            oldHeight + delta / heightAxisZ);
          vertices[k].y = proposed[k];
        }
      }

      // This callback runs synchronously inside soil.GetInitialized, before reset acknowledgement.
      for (int row = 0; row < rows; ++row)
        for (int col = 0; col < cols; ++col)
          terrain.SetHeight(col, row, proposed[row * cols + col]);
      for (int row = 0; row < rows; ++row)
        for (int col = 0; col < cols; ++col) {
          var idx = new agx.Vec2i(cols - 1 - col, rows - 1 - row);
          if (Math.Abs(native.getHeight(idx) - proposed[row * cols + col]) > .00001)
            throw new InvalidOperationException("AGX did not retain the Jev terrain patch.");
        }

      // A new runtime instance still has an unprimed MaximumDepth cache. With
      // Native ready, this rebuild also seeds AGX's private vertex cache from
      // the patched heightfield, so later particle updates retain the shape.
      terrain.EditorUpdate();
      var mesh = terrain.TerrainMesh.sharedMesh;
      mesh.vertices = vertices;
      mesh.RecalculateNormals();
      mesh.RecalculateBounds();
      m_appliedTerrain = terrain;
      AppliedTerrainCount++;
    }

    // Robot-frame locations and dimensions of the real, deformable soil geometry.
    public const float PatchForwardM = .72f;
    // Center/left approaches enter farther forward; align initial soil to their contact bands.
    public const float LeftPatchForwardM = .82f;
    public const float CenterPatchForwardM = .80f;
    public const float PatchLateralM = .40f;
    public const float PatchRadiusM = .30f;
    public const float LeftStockpileHeightM = .12f;
    public const float CenterStockpileHeightM = .14f;
    public const float RightStockpileHeightM = .16f;

    public static float HeightDelta(Vector2 rosXY)
    {
      // Three competing initial stockpiles. Only initialization applies this shape;
      // subsequent changes and any region switching come from physics and Jev.
      return CosinePatch(rosXY, new Vector2(LeftPatchForwardM, PatchLateralM), LeftStockpileHeightM) +
        CosinePatch(rosXY, new Vector2(CenterPatchForwardM, 0f), CenterStockpileHeightM) +
        CosinePatch(rosXY, new Vector2(PatchForwardM, -PatchLateralM), RightStockpileHeightM);
    }

    private static float CosinePatch(Vector2 point, Vector2 center, float amplitude)
    {
      float dx = (point.x - center.x) / PatchRadiusM, dy = (point.y - center.y) / PatchRadiusM;
      float radius = Mathf.Sqrt(dx * dx + dy * dy);
      return radius >= 1f ? 0f : amplitude * .5f * (1f + Mathf.Cos(Mathf.PI * radius));
    }

    public static float HeightDeltaAtVertex(Transform terrainTransform, Transform root, Vector3 vertex)
    {
      var p = root.InverseTransformPoint(terrainTransform.TransformPoint(vertex));
      return HeightDelta(new Vector2(p.z, -p.x));
    }

    public static bool IsOwnedMeshAssetPath(string path)
    {
      return !string.IsNullOrEmpty(path) && path.StartsWith("Assets/JevTest/", StringComparison.Ordinal) &&
        path.EndsWith(".asset", StringComparison.Ordinal) && !path.Contains("/../");
    }

    [ContextMenu("Preview Jev Terrain")]
    public void PreviewTerrain()
    {
      if (Application.isPlaying)
        throw new InvalidOperationException("Preview is only available outside Play mode.");
      ValidateConfiguration();
#if UNITY_EDITOR
      var terrain = soil.terrainTemplate;
      // MovableTerrain caches MaximumDepth only in EditorUpdate. Prime it before
      // previewing, otherwise its next editor tick rebuilds the mesh as a plane.
      terrain.EditorUpdate();
      var mesh = terrain.TerrainMesh.sharedMesh;
      var vertices = mesh.vertices;
      for (int i = 0; i < vertices.Length; ++i) {
        // Absolute local height keeps repeated previews idempotent.
        vertices[i].y = 0f;
        vertices[i].y = HeightDeltaAtVertex(terrain.transform, machineRoot, vertices[i]);
      }
      mesh.vertices = vertices;
      mesh.RecalculateNormals();
      mesh.RecalculateBounds();
      UnityEditor.EditorUtility.SetDirty(mesh);
      UnityEditor.AssetDatabase.SaveAssetIfDirty(mesh);
#endif
    }
  }
}
