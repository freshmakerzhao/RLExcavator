using UnityEngine;
using AGXUnity;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace RLExcavator
{
  /// <summary>
  /// Minimal keyboard tester for imported AGX constraints.
  /// </summary>
  public class ExcavatorActuatorPairTester : MonoBehaviour
  {
    [System.Serializable]
    public class Binding
    {
      public string label;
      public Constraint constraint;
      public KeyCode positiveKey = KeyCode.I;
      public KeyCode negativeKey = KeyCode.K;
    }

    public Binding[] bindings;
    public float driveSpeed = 0.12f;

    private void Update()
    {
      if ( bindings == null )
        return;

      foreach ( Binding binding in bindings ) {
        if ( binding == null )
          continue;

        if ( WasPressedThisFrame( binding.positiveKey ) )
          Debug.Log( $"[ConstraintTester] key={binding.positiveKey} constraint={BindingName( binding )} speed=+{Mathf.Abs( driveSpeed ):0.###}" );
        if ( WasPressedThisFrame( binding.negativeKey ) )
          Debug.Log( $"[ConstraintTester] key={binding.negativeKey} constraint={BindingName( binding )} speed=-{Mathf.Abs( driveSpeed ):0.###}" );

        float command = Axis( binding.positiveKey, binding.negativeKey );
        DriveConstraint( binding.constraint, command * Mathf.Abs( driveSpeed ) );
      }
    }

    private void OnDisable()
    {
      if ( bindings == null )
        return;

      foreach ( Binding binding in bindings )
        if ( binding != null )
          DriveConstraint( binding.constraint, 0f );
    }

    private static void DriveConstraint( Constraint constraint, float speed )
    {
      if ( constraint == null )
        return;

      var locker = constraint.GetController<LockController>();
      if ( locker != null )
        locker.Enable = false;

      var motor = constraint.GetController<TargetSpeedController>();
      if ( motor == null )
        return;

      motor.Enable = true;
      motor.LockAtZeroSpeed = false;
      motor.Speed = speed;
    }

    private static string BindingName( Binding binding )
    {
      if ( !string.IsNullOrWhiteSpace( binding.label ) )
        return binding.label;

      return binding.constraint != null ? binding.constraint.name : "<unbound>";
    }

    private static float Axis( KeyCode positive, KeyCode negative )
    {
      float value = 0f;
      if ( IsPressed( positive ) ) value += 1f;
      if ( IsPressed( negative ) ) value -= 1f;
      return value;
    }

    private static bool IsPressed( KeyCode code )
    {
#if ENABLE_INPUT_SYSTEM
      Key key = ToInputSystemKey( code );
      return key != Key.None && Keyboard.current != null && Keyboard.current[ key ].isPressed;
#else
      return Input.GetKey( code );
#endif
    }

    private static bool WasPressedThisFrame( KeyCode code )
    {
#if ENABLE_INPUT_SYSTEM
      Key key = ToInputSystemKey( code );
      return key != Key.None && Keyboard.current != null && Keyboard.current[ key ].wasPressedThisFrame;
#else
      return Input.GetKeyDown( code );
#endif
    }

#if ENABLE_INPUT_SYSTEM
    private static Key ToInputSystemKey( KeyCode code )
    {
      switch ( code ) {
        case KeyCode.A: return Key.A;
        case KeyCode.B: return Key.B;
        case KeyCode.C: return Key.C;
        case KeyCode.D: return Key.D;
        case KeyCode.E: return Key.E;
        case KeyCode.F: return Key.F;
        case KeyCode.G: return Key.G;
        case KeyCode.H: return Key.H;
        case KeyCode.I: return Key.I;
        case KeyCode.J: return Key.J;
        case KeyCode.K: return Key.K;
        case KeyCode.L: return Key.L;
        case KeyCode.M: return Key.M;
        case KeyCode.N: return Key.N;
        case KeyCode.O: return Key.O;
        case KeyCode.P: return Key.P;
        case KeyCode.Q: return Key.Q;
        case KeyCode.R: return Key.R;
        case KeyCode.S: return Key.S;
        case KeyCode.T: return Key.T;
        case KeyCode.U: return Key.U;
        case KeyCode.V: return Key.V;
        case KeyCode.W: return Key.W;
        case KeyCode.X: return Key.X;
        case KeyCode.Y: return Key.Y;
        case KeyCode.Z: return Key.Z;
        case KeyCode.Alpha0: return Key.Digit0;
        case KeyCode.Alpha1: return Key.Digit1;
        case KeyCode.Alpha2: return Key.Digit2;
        case KeyCode.Alpha3: return Key.Digit3;
        case KeyCode.Alpha4: return Key.Digit4;
        case KeyCode.Alpha5: return Key.Digit5;
        case KeyCode.Alpha6: return Key.Digit6;
        case KeyCode.Alpha7: return Key.Digit7;
        case KeyCode.Alpha8: return Key.Digit8;
        case KeyCode.Alpha9: return Key.Digit9;
        case KeyCode.Comma: return Key.Comma;
        case KeyCode.Period: return Key.Period;
        case KeyCode.LeftBracket: return Key.LeftBracket;
        case KeyCode.RightBracket: return Key.RightBracket;
        case KeyCode.Space: return Key.Space;
        case KeyCode.F1: return Key.F1;
        case KeyCode.F2: return Key.F2;
        case KeyCode.F3: return Key.F3;
        case KeyCode.F4: return Key.F4;
        case KeyCode.F5: return Key.F5;
        case KeyCode.F6: return Key.F6;
        case KeyCode.F7: return Key.F7;
        case KeyCode.F8: return Key.F8;
        case KeyCode.F9: return Key.F9;
        case KeyCode.F10: return Key.F10;
        case KeyCode.F11: return Key.F11;
        case KeyCode.F12: return Key.F12;
        default:        return Key.None;
      }
    }
#endif
  }
}
