using UnityEngine;
using Unity.MLAgents.Actuators;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace RLExcavator
{
  public struct ExcavatorControlCommand
  {
    public float boom;
    public float stick;
    public float bucket;
    public float swing;

    public bool HasInput =>
      Mathf.Abs( boom ) > 0f ||
      Mathf.Abs( stick ) > 0f ||
      Mathf.Abs( bucket ) > 0f ||
      Mathf.Abs( swing ) > 0f;

    public void ApplyTo( ExcavatorController controller )
    {
      if ( controller != null )
        controller.SetNormalizedSpeeds( boom, stick, bucket, swing );
    }
  }

  /// <summary>
  /// Shared default keyboard mapping for manual excavator control.
  /// Keep this as the single source of truth for Heuristic agents and test scripts.
  /// </summary>
  public static class ExcavatorKeyboardInput
  {
    public static ExcavatorControlCommand ReadDefault()
    {
      return new ExcavatorControlCommand {
        boom   = Axis( KeyCode.K, KeyCode.I ),
        stick  = Axis( KeyCode.G, KeyCode.T ),
        bucket = Axis( KeyCode.J, KeyCode.L ),
        swing  = Axis( KeyCode.H, KeyCode.F )
      };
    }

    public static void WriteDefaultTo( ActionSegment<float> actions )
    {
      var command = ReadDefault();

      if ( actions.Length > 0 ) actions[ 0 ] = command.boom;
      if ( actions.Length > 1 ) actions[ 1 ] = command.stick;
      if ( actions.Length > 2 ) actions[ 2 ] = command.bucket;
      if ( actions.Length > 3 ) actions[ 3 ] = command.swing;
    }

    private static float Axis( KeyCode positive, KeyCode negative )
    {
#if ENABLE_INPUT_SYSTEM
      var kb = Keyboard.current;
      if ( kb == null )
        return 0f;

      float v = 0f;
      if ( kb[ ToKey( positive ) ].isPressed ) v += 1f;
      if ( kb[ ToKey( negative ) ].isPressed ) v -= 1f;
      return v;
#else
      float v = 0f;
      if ( Input.GetKey( positive ) ) v += 1f;
      if ( Input.GetKey( negative ) ) v -= 1f;
      return v;
#endif
    }

#if ENABLE_INPUT_SYSTEM
    private static Key ToKey( KeyCode code )
    {
      switch ( code ) {
        case KeyCode.I: return Key.I;
        case KeyCode.K: return Key.K;
        case KeyCode.T: return Key.T;
        case KeyCode.G: return Key.G;
        case KeyCode.J: return Key.J;
        case KeyCode.L: return Key.L;
        case KeyCode.F: return Key.F;
        case KeyCode.H: return Key.H;
        default:        return Key.None;
      }
    }
#endif
  }
}
