using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using AGXUnity;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class ResetTerrain : MonoBehaviour
{
  [Header("Episode reset")]
  [Tooltip("按 R 时同步复位挖掘机姿态；留空则运行时自动查找")]
  public RLExcavator.ExcavatorController ExcavatorController;
  [Tooltip("按 R 时同步通知 Agent 重新开始本局计数；留空则运行时自动查找")]
  public RLExcavator.ExcavatorDigAgent Agent;
  [Tooltip("按 R 或成功 reset 时同步清空质量/体积 UI 计数；留空则运行时自动查找")]
  public MassVolumeCounter MassCounter;

#if ENABLE_INPUT_SYSTEM
  private InputAction ResetAction;
#else
  public KeyCode ResetTerrainKey = KeyCode.R;
#endif

  private Coroutine m_resetRoutine = null;
  private bool m_inactiveResetWarningShown = false;
  public bool IsResetting { get; private set; } = false;

  // Start is called before the first frame update
  void Start()
  {
#if ENABLE_INPUT_SYSTEM
    ResetAction = new InputAction("Reset", binding: "<Keyboard>/r");
    ResetAction.Enable();
#endif

    if (ExcavatorController == null)
      ExcavatorController = FindInScene<RLExcavator.ExcavatorController>();

    if (Agent == null)
      Agent = FindInScene<RLExcavator.ExcavatorDigAgent>();

    if (MassCounter == null)
      MassCounter = FindInScene<MassVolumeCounter>();

  }

  public void ResetTerrainHeights()
  {
    var terrain = GetComponent<AGXUnity.Model.DeformableTerrain>();
    if (terrain != null)
      terrain.ResetHeightsAndRecreateNative();
  }

  public void ResetEpisode()
  {
    ResetEpisode( true );
  }

  public void ResetEpisode( bool restartAgentMeasurements )
  {
    if (!isActiveAndEnabled || !gameObject.activeInHierarchy) {
      if (!m_inactiveResetWarningShown) {
        Debug.LogWarning("[ResetTerrain] ResetEpisode ignored because ResetTerrain is inactive.");
        m_inactiveResetWarningShown = true;
      }
      return;
    }

    if (m_resetRoutine != null)
      StopCoroutine(m_resetRoutine);

    m_resetRoutine = StartCoroutine(ResetEpisodeRoutine(restartAgentMeasurements));
  }

  private IEnumerator ResetEpisodeRoutine( bool restartAgentMeasurements )
  {
    IsResetting = true;

    if (ExcavatorController != null)
      ExcavatorController.BeginHomePoseReset();

    while (ExcavatorController != null && ExcavatorController.IsHoming)
      yield return new WaitForFixedUpdate();

    ResetTerrainHeights();

    if (MassCounter != null)
      MassCounter.ResetCounters();

    if (restartAgentMeasurements && Agent != null)
      Agent.RestartEpisodeMeasurements();

    IsResetting = false;
    m_resetRoutine = null;
  }

  private T FindInScene<T>() where T : Component
  {
    var roots = gameObject.scene.GetRootGameObjects();
    foreach (var root in roots) {
      var component = root.GetComponentInChildren<T>(true);
      if (component != null)
        return component;
    }

    return null;
  }

  // Update is called once per frame
  void Update()
  {
#if ENABLE_INPUT_SYSTEM
    if (ResetAction.triggered)
#else
    if (Input.GetKeyDown(ResetTerrainKey))
#endif
    {
      ResetEpisode();
    }
  }
}

