using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace RLExcavator
{
  /// <summary>
  /// 键盘手动驱动挖掘机，用来在接 RL 之前验证关节连线和挖土是否正常。
  ///
  /// 操作：
  ///   大臂 Boom   : I / K
  ///   小臂 Stick  : T / G
  ///   铲斗 Bucket : J / L
  ///   回转 Swing  : F / H
  ///   松开按键 = 该关节停止。
  ///
  /// 验证目标：能把铲斗插进土堆、卷斗挖起一斗土、抬臂提起来。手感不对就回到
  /// ExcavatorController 调 *MaxSpeed，或检查关节是否拖反/拖错。
  /// </summary>
  [RequireComponent( typeof( ExcavatorController ) )]
  public class ExcavatorKeyboardTester : MonoBehaviour
  {
    private ExcavatorController m_controller;

    private void Awake()
    {
      m_controller = GetComponent<ExcavatorController>();
    }

    // 用 FixedUpdate：物理驱动应与 AGX 的固定步对齐，而不是渲染帧。
    private void FixedUpdate()
    {
      float boom   = Axis( KeyCode.K, KeyCode.I );
      float stick  = Axis( KeyCode.G, KeyCode.T );
      float bucket = Axis( KeyCode.J, KeyCode.L );
      float swing  = Axis( KeyCode.H, KeyCode.F );

      m_controller.SetNormalizedSpeeds( boom, stick, bucket, swing );
    }

    // 返回 +1 / -1 / 0
    private static float Axis( KeyCode positive, KeyCode negative )
    {
#if ENABLE_INPUT_SYSTEM
      // 新版 Input System：用 Keyboard.current 读键。
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
    // 把少量用到的 KeyCode 映射到 Input System 的 Key。
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
