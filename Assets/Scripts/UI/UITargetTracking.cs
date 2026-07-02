using UnityEngine;
using UnityEngine.UI;

public class UITargetTracking : MonoBehaviour
{
  public RLExcavator.ExcavatorTrajectoryAgent trajectoryAgent;
  [Tooltip("UI 所属训练环境根节点。为空时使用当前 UI 的最顶层父节点。")]
  public Transform environmentRoot;
  [Tooltip("找不到 Information 文本时，在左上角用 IMGUI 显示训练信息。")]
  public bool useImmediateModeFallback = true;
  [Tooltip("IMGUI 训练信息面板位置和尺寸。")]
  public Rect fallbackPanelRect = new Rect( 10f, 10f, 390f, 320f );

  private Text m_infoText;
  private GUIStyle m_fallbackBoxStyle;
  private GUIStyle m_fallbackTextStyle;

  protected void Awake()
  {
    m_infoText = FindInformationText();
    if ( m_infoText == null && !useImmediateModeFallback )
      Debug.LogWarning( $"[{nameof( UITargetTracking )}] No child Text named 'Information' found under {name}. UI updates disabled.", this );

    if ( environmentRoot == null )
      environmentRoot = transform.root;

    if ( trajectoryAgent == null )
      trajectoryAgent = FindActiveInEnvironment<RLExcavator.ExcavatorTrajectoryAgent>() ?? FindInEnvironment<RLExcavator.ExcavatorTrajectoryAgent>();
  }

  private Text FindInformationText()
  {
    var texts = GetComponentsInChildren<Text>( true );
    foreach ( var text in texts ) {
      if ( text.name == "Information" )
        return text;
    }

    return null;
  }

  private T FindInEnvironment<T>() where T : Component
  {
    Transform root = environmentRoot != null ? environmentRoot : transform.root;
    return root != null ? root.GetComponentInChildren<T>( true ) : null;
  }

  private T FindActiveInEnvironment<T>() where T : Component
  {
    Transform root = environmentRoot != null ? environmentRoot : transform.root;
    if ( root == null )
      return null;

    var components = root.GetComponentsInChildren<T>( true );
    foreach ( var component in components ) {
      if ( component is Behaviour behaviour && behaviour.isActiveAndEnabled )
        return component;
    }

    return null;
  }

  private void Update()
  {
    if ( m_infoText == null && !useImmediateModeFallback ) {
      enabled = false;
      return;
    }

    if ( trajectoryAgent == null || !trajectoryAgent.isActiveAndEnabled )
      trajectoryAgent = FindActiveInEnvironment<RLExcavator.ExcavatorTrajectoryAgent>();

    if ( m_infoText == null )
      return;

    m_infoText.text = BuildInformationText();
  }

  private void OnGUI()
  {
    if ( m_infoText != null || !useImmediateModeFallback )
      return;

    EnsureFallbackStyles();
    GUI.Box( fallbackPanelRect, GUIContent.none, m_fallbackBoxStyle );
    GUI.Label( Inset( fallbackPanelRect, 12f, 10f ), BuildInformationText(), m_fallbackTextStyle );
  }

  private string BuildInformationText()
  {
    if ( trajectoryAgent == null || !trajectoryAgent.isActiveAndEnabled )
      return "No active trajectory agent found.";

    int targetCount = trajectoryAgent.CurrentTargetCount;
    int targetNumber = targetCount > 0 ? Mathf.Clamp( trajectoryAgent.CurrentTargetIndex + 1, 1, targetCount ) : 0;

    return
      string.Format( "Episode: \t\t{0}\n", trajectoryAgent.CurrentEpisode ) +
      string.Format( "Step: \t\t{0}\n", trajectoryAgent.StepCount ) +
      string.Format( "Reward: \t\t{0:f3}\n", trajectoryAgent.CurrentScore ) +
      string.Format( "Target: \t\t{0}/{1}\n", targetNumber, targetCount ) +
      string.Format( "Distance: \t\t{0:f3} m\n", trajectoryAgent.CurrentDistanceToTarget ) +
      string.Format( "Threshold: \t\t{0:f3} m\n", trajectoryAgent.CurrentTargetThreshold ) +
      string.Format( "Progress: \t\t{0:P0}\n", trajectoryAgent.CurrentPathProgress ) +
      string.Format( "Mode: \t\t{0}\n", trajectoryAgent.CurrentTaskMode ) +
      string.Format( "Last reset: \t{0}\n", string.IsNullOrEmpty( trajectoryAgent.CurrentEpisodeFailureReason ) ? "-" : trajectoryAgent.CurrentEpisodeFailureReason ) +
      string.Format( "Waypoint step: \t{0}/{1}\n", trajectoryAgent.CurrentWaypointSteps, trajectoryAgent.maxStepsPerWaypoint ) +
      string.Format( "OOB steps: \t{0}/{1}\n", trajectoryAgent.CurrentOutOfBoundsSteps, trajectoryAgent.outOfBoundsFailureSteps ) +
      string.Format( "Stuck samples: \t{0}/{1}\n", trajectoryAgent.CurrentStuckWindowSamples, trajectoryAgent.stuckWindowSteps ) +
      string.Format( "Bucket pitch: \t{0:f1}/{1:f1} deg\n", trajectoryAgent.CurrentBucketPitchDeg, trajectoryAgent.CurrentTargetBucketPitchDeg ) +
      string.Format( "Pitch error: \t{0:f1} deg", trajectoryAgent.CurrentBucketPitchErrorDeg );
  }

  private void EnsureFallbackStyles()
  {
    if ( m_fallbackBoxStyle != null && m_fallbackTextStyle != null )
      return;

    m_fallbackBoxStyle = new GUIStyle( GUI.skin.box );
    m_fallbackBoxStyle.normal.background = Texture2D.grayTexture;

    m_fallbackTextStyle = new GUIStyle( GUI.skin.label ) {
      fontSize = 18,
      fontStyle = FontStyle.Bold,
      normal = { textColor = Color.white }
    };
  }

  private static Rect Inset( Rect rect, float x, float y )
  {
    return new Rect( rect.x + x, rect.y + y, rect.width - 2f * x, rect.height - 2f * y );
  }
}
