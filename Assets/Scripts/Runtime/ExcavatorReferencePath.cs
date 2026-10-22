using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace RLExcavator
{
  /// <summary>Display only the reference vertices supplied to RL; never record the actual tip trail.</summary>
  public sealed class ExcavatorReferencePath : MonoBehaviour
  {
    public bool visible = true;
    private LineRenderer m_line;
    private Material m_material;
    private Camera m_overview;

    public void SetPath(Vector3[] pointsRos, Transform machineRoot, Camera overview)
    {
      if (pointsRos == null || machineRoot == null) throw new ArgumentException("Reference path/root missing");
      var world = new Vector3[pointsRos.Length];
      for (int i = 0; i < world.Length; i++) {
        var p = pointsRos[i];
        if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z))
          throw new ArgumentException("Reference path requires finite points");
        world[i] = machineRoot.TransformPoint(new Vector3(-p.y, p.z, p.x));
      }
      if (m_line == null) {
        m_line = gameObject.AddComponent<LineRenderer>();
        m_material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        m_material.SetColor("_BaseColor", new Color(0f, .85f, 1f));
        m_line.sharedMaterial = m_material;
        m_line.useWorldSpace = true;
        m_line.widthMultiplier = .015f;
        m_line.shadowCastingMode = ShadowCastingMode.Off;
        m_line.receiveShadows = false;
      }
      m_overview = overview;
      m_line.enabled = false;
      m_line.positionCount = world.Length;
      m_line.SetPositions(world);
    }

    public void Clear()
    {
      if (m_line == null) return;
      m_line.enabled = false;
      m_line.positionCount = 0;
    }

    private void OnEnable()
    {
      RenderPipelineManager.beginCameraRendering += BeginCamera;
      RenderPipelineManager.endCameraRendering += EndCamera;
    }

    private void BeginCamera(ScriptableRenderContext context, Camera camera)
    {
      if (m_line != null)
        m_line.enabled = visible && m_line.positionCount > 1 &&
          (camera == m_overview || camera.cameraType == CameraType.SceneView);
    }

    private void EndCamera(ScriptableRenderContext context, Camera camera)
    {
      if (m_line != null) m_line.enabled = false;
    }

    private void OnDisable()
    {
      RenderPipelineManager.beginCameraRendering -= BeginCamera;
      RenderPipelineManager.endCameraRendering -= EndCamera;
      Clear();
    }

    private void OnDestroy()
    {
      if (m_material == null) return;
      if (Application.isPlaying) Destroy(m_material);
      else DestroyImmediate(m_material);
    }
  }
}
