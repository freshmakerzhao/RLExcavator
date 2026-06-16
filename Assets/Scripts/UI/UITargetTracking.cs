using UnityEngine;
using UnityEngine.UI;

public class UITargetTracking : MonoBehaviour
{
  public RLExcavator.ExcavatorTrajectoryAgent trajectoryAgent;
  [Tooltip("UI 所属训练环境根节点。为空时使用当前 UI 的最顶层父节点。")]
  public Transform environmentRoot;

  private Text m_infoText;

  protected void Awake()
  {
    m_infoText = FindInformationText();
    if ( m_infoText == null ) {
      Debug.LogWarning( $"[{nameof( UITargetTracking )}] No child Text named 'Information' found under {name}. UI updates disabled.", this );
      enabled = false;
      return;
    }

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
    if ( m_infoText == null ) {
      enabled = false;
      return;
    }

    if ( trajectoryAgent == null || !trajectoryAgent.isActiveAndEnabled )
      trajectoryAgent = FindActiveInEnvironment<RLExcavator.ExcavatorTrajectoryAgent>();

    if ( trajectoryAgent != null && trajectoryAgent.isActiveAndEnabled ) {
      int targetCount = trajectoryAgent.CurrentTargetCount;
      int targetNumber = targetCount > 0 ? Mathf.Clamp( trajectoryAgent.CurrentTargetIndex + 1, 1, targetCount ) : 0;

      m_infoText.text =
        string.Format( "Episode: \t\t{0}\n", trajectoryAgent.CurrentEpisode ) +
        string.Format( "Step: \t\t{0}\n", trajectoryAgent.StepCount ) +
        string.Format( "Reward: \t\t{0:f3}\n", trajectoryAgent.CurrentScore ) +
        string.Format( "Target: \t\t{0}/{1}\n", targetNumber, targetCount ) +
        string.Format( "Distance: \t\t{0:f3} m\n", trajectoryAgent.CurrentDistanceToTarget ) +
        string.Format( "Threshold: \t\t{0:f3} m\n", trajectoryAgent.CurrentTargetThreshold ) +
        string.Format( "Progress: \t\t{0:P0}\n", trajectoryAgent.CurrentPathProgress ) +
        string.Format( "Mode: \t\t{0}\n", trajectoryAgent.CurrentTaskMode ) +
        string.Format( "Bucket pitch: \t{0:f1}/{1:f1} deg\n", trajectoryAgent.CurrentBucketPitchDeg, trajectoryAgent.CurrentTargetBucketPitchDeg ) +
        string.Format( "Pitch error: \t{0:f1} deg", trajectoryAgent.CurrentBucketPitchErrorDeg );
      return;
    }

    m_infoText.text = "No active trajectory agent found.";
  }
}
