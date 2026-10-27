using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RLExcavator.Editor
{
  /// <summary>单颗雷达的互斥安装候选；只在编辑状态选择，不进入任务控制路径。</summary>
  public static class LidarPlacementExperiment
  {
    [MenuItem("Tools/Excavator/Lidar placement/Boom")]
    public static void Boom() => Use("LidarE1RFront");
    [MenuItem("Tools/Excavator/Lidar placement/Ceiling - 1.5m above soil")]
    public static void CeilingLow() => Use("E1R_CeilingLow");
    [MenuItem("Tools/Excavator/Lidar placement/Ceiling - 2.0m above soil")]
    public static void CeilingHigh() => Use("E1R_CeilingHigh");
    [MenuItem("Tools/Excavator/Lidar placement/Front wall")]
    public static void WallFront() => Use("E1R_WallFront");
    [MenuItem("Tools/Excavator/Lidar placement/Left wall")]
    public static void WallLeft() => Use("E1R_WallLeft");
    [MenuItem("Tools/Excavator/Lidar placement/Shelf 1.10m - solid state (default)")]
    public static void ShelfSolidState() => Use("E1R_Shelf110");
    [MenuItem("Tools/Excavator/Lidar placement/Shelf 1.10m - 360 degrees")]
    public static void Shelf360() => Use("Airy_Shelf110");

    private static void Use(string name)
    {
      if (EditorApplication.isPlayingOrWillChangePlaymode)
        throw new InvalidOperationException("Stop Play before changing the LiDAR placement.");
      var bridge = UnityEngine.Object.FindFirstObjectByType<ExcavatorSimBridge>();
      var readers = UnityEngine.Object.FindObjectsByType<ExcavatorLidarTelemetry>(FindObjectsSortMode.None);
      var selected = readers.SingleOrDefault(r => r.name == name);
      if (bridge == null || selected == null)
        throw new InvalidOperationException("Open ExcavatorRuntimeLidarPlacements before selecting this placement.");
      Select(bridge, readers, selected);
      foreach (var reader in readers) EditorUtility.SetDirty(reader.sensor);
      EditorUtility.SetDirty(bridge);
      EditorSceneManager.MarkSceneDirty(bridge.gameObject.scene);
      Selection.activeGameObject = selected.gameObject;
    }

    public static void Select(ExcavatorSimBridge bridge, ExcavatorLidarTelemetry[] readers,
                              ExcavatorLidarTelemetry selected)
    {
      foreach (var reader in readers) reader.SetScanningEnabled(reader == selected);
      // 每次只选一颗及其安装位置，不同时合并多个同名 sensor_id。
      bridge.lidarSensors = new[] { selected };
    }
  }
}
