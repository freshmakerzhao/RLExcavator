using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace RLExcavator.Editor
{
  /// <summary>仅在 Scene 视图标出机器原点，不创建网格、碰撞体或传感器目标。</summary>
  internal static class MachineRootGizmo
  {
    private const float AxisLength = 0.24f;

    [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
    private static void Draw(ExcavatorFrameRegistry registry, GizmoType gizmoType)
    {
      // 不画到 Game/FrontCamera；未绑定 machineRoot 时不绘制。
      if (Camera.current == null || Camera.current.cameraType != CameraType.SceneView ||
          registry.machineRoot == null)
        return;

      Transform root = registry.machineRoot;
      Color previousColor = Handles.color;
      Matrix4x4 previousMatrix = Handles.matrix;
      CompareFunction previousDepth = Handles.zTest;
      try {
        Handles.matrix = Matrix4x4.identity;
        // 原点在车身下方，允许辅助标记透过机体显示，避免误认为原点消失。
        Handles.zTest = CompareFunction.Always;
        Vector3 origin = root.position;
        Handles.color = new Color(1f, 0.85f, 0.1f);
        Handles.SphereHandleCap(0, origin, Quaternion.identity, 0.025f, EventType.Repaint);
        Handles.DrawWireDisc(origin, root.up, 0.045f);
        var label = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };
        label.normal.textColor = Handles.color;
        Handles.Label(origin + root.up * 0.33f,
          root.name + " (0, 0, 0)\nROS / track support plane", label);

        // ROS 与 Unity 的轴名不同：ROS X前=Unity +Z，Y左=Unity -X，Z上=Unity +Y。
        DrawAxis(origin, root.forward, new Color(1f, 0.25f, 0.25f), "ROS X / Front");
        DrawAxis(origin, -root.right, new Color(0.25f, 1f, 0.35f), "ROS Y / Left");
        DrawAxis(origin, root.up, new Color(0.25f, 0.65f, 1f), "ROS Z / Up");
      }
      finally {
        Handles.color = previousColor;
        Handles.matrix = previousMatrix;
        Handles.zTest = previousDepth;
      }
    }

    private static void DrawAxis(Vector3 origin, Vector3 direction, Color color, string text)
    {
      Handles.color = color;
      Handles.ArrowHandleCap(0, origin, Quaternion.LookRotation(direction), AxisLength, EventType.Repaint);
      var label = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
      label.normal.textColor = color;
      Handles.Label(origin + direction * (AxisLength + 0.025f), text, label);
    }
  }
}
