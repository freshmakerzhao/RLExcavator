using UnityEngine;
using UnityEngine.UI;
using System.Linq;

public class UITargetTracking : MonoBehaviour
{
  public RLExcavator.ExcavatorTrajectoryAgent trajectoryAgent;
  [Tooltip("UI 所属训练环境根节点。为空时使用当前 UI 的最顶层父节点。")]
  public Transform environmentRoot;

  private Text m_infoText;

  protected void Awake()
  {
    var texts = GetComponentsInChildren<Text>();
    m_infoText = texts.First( t => t.name == "Information" );

    Debug.Assert( m_infoText );

    if ( environmentRoot == null )
      environmentRoot = transform.root;

    if ( trajectoryAgent == null )
      trajectoryAgent = FindActiveInEnvironment<RLExcavator.ExcavatorTrajectoryAgent>() ?? FindInEnvironment<RLExcavator.ExcavatorTrajectoryAgent>();
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
        string.Format( "Progress: \t\t{0:P0}", trajectoryAgent.CurrentPathProgress );
      return;
    }

    m_infoText.text = "No active trajectory agent found.";
  }
}
