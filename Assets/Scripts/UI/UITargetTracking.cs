using UnityEngine;
using UnityEngine.UI;
using System.Linq;

public class UITargetTracking : MonoBehaviour
{
  public RLExcavator.ExcavatorTrajectoryAgent trajectoryAgent;

  Text m_infoText;

  protected void Awake()
  {
    var texts = GetComponentsInChildren<Text>();
    m_infoText = texts.First( t => t.name == "Information" );

    Debug.Assert( m_infoText );

    if ( trajectoryAgent == null )
      trajectoryAgent = FindActiveInScene<RLExcavator.ExcavatorTrajectoryAgent>() ?? FindInScene<RLExcavator.ExcavatorTrajectoryAgent>();
  }

  private T FindInScene<T>() where T : Component
  {
    var roots = gameObject.scene.GetRootGameObjects();
    foreach ( var root in roots ) {
      var component = root.GetComponentInChildren<T>( true );
      if ( component != null )
        return component;
    }

    return null;
  }

  private T FindActiveInScene<T>() where T : Component
  {
    var roots = gameObject.scene.GetRootGameObjects();
    foreach ( var root in roots ) {
      var components = root.GetComponentsInChildren<T>( true );
      foreach ( var component in components ) {
        if ( component is Behaviour behaviour && behaviour.isActiveAndEnabled )
          return component;
      }
    }

    return null;
  }

  void Update()
  {
    if ( trajectoryAgent == null || !trajectoryAgent.isActiveAndEnabled )
      trajectoryAgent = FindActiveInScene<RLExcavator.ExcavatorTrajectoryAgent>();

    string info = "";
    if ( trajectoryAgent != null && trajectoryAgent.isActiveAndEnabled ) {
      info += string.Format( "Episode: \t\t{0}\n", trajectoryAgent.CurrentEpisode );
      info += string.Format( "Score: \t\t{0:f3}\n", trajectoryAgent.CurrentScore );
      info += string.Format( "Step: \t\t{0}\n", trajectoryAgent.StepCount );
      // info += string.Format( "Target: \t\t{0}/{1}\n", trajectoryAgent.CurrentTargetIndex + 1, trajectoryAgent.CurrentTargetCount );
      info += string.Format( "Distance: \t\t{0:f3} m\n", trajectoryAgent.CurrentDistanceToTarget );
      // info += string.Format( "Threshold: \t\t{0:f3} m", trajectoryAgent.CurrentTargetThreshold );
    }
    else {
      info = "No active trajectory agent found.";
    }

    m_infoText.text = info;
  }
}