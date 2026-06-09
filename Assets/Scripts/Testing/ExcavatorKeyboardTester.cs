using UnityEngine;

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
      ExcavatorKeyboardInput.ReadDefault().ApplyTo( m_controller );
    }
  }
}
