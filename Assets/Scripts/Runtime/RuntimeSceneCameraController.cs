using UnityEngine;
using UnityEngine.EventSystems;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace RLExcavator
{
  /// <summary>
  /// Runtime camera controller that mimics the most useful Scene view controls in Play Mode.
  /// </summary>
  [DisallowMultipleComponent]
  public class RuntimeSceneCameraController : MonoBehaviour
  {
    [Header("Mode")]
    [Tooltip("运行时是否启用自由镜头。")]
    public bool freeModeEnabled = true;

    [Header("Motion")]
    [Tooltip("镜头基础移动速度，单位米/秒。")]
    public float moveSpeed = 4f;

    [Tooltip("按住 Shift 时的速度倍率。")]
    public float fastMoveMultiplier = 3f;

    [Tooltip("鼠标旋转灵敏度。")]
    public float lookSensitivity = 0.12f;

    [Tooltip("鼠标平移灵敏度。")]
    public float panSensitivity = 0.006f;

    [Tooltip("滚轮缩放灵敏度。")]
    public float zoomSensitivity = 0.02f;

    [Tooltip("未绑定环绕目标时，Alt 环绕使用的默认距离。")]
    public float orbitDistance = 6f;

    [Tooltip("按住右键时才响应键盘移动。")]
    public bool requireRightMouseForKeyboardMove = true;

    [Header("Optional")]
    [Tooltip("右键拖拽围绕目标旋转；关闭后右键为原地转向。")]
    public bool rightMouseOrbitsTarget = true;

    [Tooltip("为空时自动查找 MachineRoot 作为环绕锚点。")]
    public bool autoFindOrbitTarget = true;

    [Tooltip("右键/Alt 左键环绕的目标。为空时使用镜头前方固定距离点。")]
    public Transform orbitTarget;

    public bool FreeModeEnabled => freeModeEnabled;

    private Vector3 m_fixedPosition;
    private Quaternion m_fixedRotation;
    private bool m_hasFixedPose;

    private void Awake()
    {
      CacheFixedPose();
      ResolveOrbitTarget();
      freeModeEnabled = true;
    }

    private void OnDisable()
    {
      ReleaseCursor();
    }

    private void Update()
    {
      if ( !freeModeEnabled )
        return;

      Vector2 mouseDelta = MouseDelta();
      if ( IsRightMousePressed() ) {
        if ( rightMouseOrbitsTarget )
          Orbit( mouseDelta );
        else
          RotateInPlace( mouseDelta );
        MoveFromKeyboard();
        CaptureCursor();
      }
      else if ( IsMiddleMousePressed() ) {
        Pan( mouseDelta );
        ReleaseCursor();
      }
      else if ( IsAltPressed() && IsLeftMousePressed() ) {
        Orbit( mouseDelta );
        ReleaseCursor();
      }
      else {
        if ( !requireRightMouseForKeyboardMove )
          MoveFromKeyboard();
        ReleaseCursor();
      }

      float scroll = MouseScroll();
      if ( Mathf.Abs( scroll ) > 0.01f && !IsPointerOverUi() )
        transform.position += transform.forward * scroll * zoomSensitivity;
    }

    public void ToggleFreeMode()
    {
      SetFreeModeEnabled( !freeModeEnabled );
    }

    public void SetFreeModeEnabled( bool enabled )
    {
      if ( freeModeEnabled == enabled )
        return;

      if ( enabled ) {
        CacheFixedPose();
      }
      else {
        if ( m_hasFixedPose ) {
          transform.position = m_fixedPosition;
          transform.rotation = m_fixedRotation;
        }
        ReleaseCursor();
      }

      freeModeEnabled = enabled;
    }

    private void CacheFixedPose()
    {
      m_fixedPosition = transform.position;
      m_fixedRotation = transform.rotation;
      m_hasFixedPose = true;
    }

    private void ResolveOrbitTarget()
    {
      if ( !autoFindOrbitTarget || orbitTarget != null )
        return;

      Transform bestTarget = null;
      float bestDistanceSq = float.PositiveInfinity;
      ExcavatorFrameRegistry[] registries = FindObjectsByType<ExcavatorFrameRegistry>( FindObjectsInactive.Include, FindObjectsSortMode.None );
      for ( int i = 0; i < registries.Length; i++ ) {
        ExcavatorFrameRegistry registry = registries[ i ];
        if ( registry == null )
          continue;

        registry.ResolveMissingReferences();
        Transform candidate = registry.MachineRoot != null ? registry.MachineRoot : registry.BucketTip;
        if ( candidate == null )
          continue;

        float distanceSq = ( candidate.position - transform.position ).sqrMagnitude;
        if ( distanceSq >= bestDistanceSq )
          continue;

        bestTarget = candidate;
        bestDistanceSq = distanceSq;
      }

      orbitTarget = bestTarget;
    }

    private void RotateInPlace( Vector2 mouseDelta )
    {
      if ( IsPointerOverUi() )
        return;

      Vector3 euler = transform.eulerAngles;
      euler.x -= mouseDelta.y * lookSensitivity;
      euler.y += mouseDelta.x * lookSensitivity;
      transform.eulerAngles = euler;
    }

    private void MoveFromKeyboard()
    {
      Vector3 direction = Vector3.zero;
      if ( KeyPressed( KeyCode.W ) )
        direction += transform.forward;
      if ( KeyPressed( KeyCode.S ) )
        direction -= transform.forward;
      if ( KeyPressed( KeyCode.D ) )
        direction += transform.right;
      if ( KeyPressed( KeyCode.A ) )
        direction -= transform.right;
      if ( KeyPressed( KeyCode.E ) )
        direction += Vector3.up;
      if ( KeyPressed( KeyCode.Q ) )
        direction -= Vector3.up;

      if ( direction.sqrMagnitude < 0.0001f )
        return;

      float speed = Mathf.Max( moveSpeed, 0.01f );
      if ( KeyPressed( KeyCode.LeftShift ) || KeyPressed( KeyCode.RightShift ) )
        speed *= Mathf.Max( fastMoveMultiplier, 1f );

      transform.position += direction.normalized * speed * Time.unscaledDeltaTime;
    }

    private void Pan( Vector2 mouseDelta )
    {
      if ( IsPointerOverUi() )
        return;

      float distanceScale = Mathf.Max( orbitDistance, 0.1f );
      Vector3 delta = -transform.right * mouseDelta.x - transform.up * mouseDelta.y;
      transform.position += delta * panSensitivity * distanceScale;
    }

    private void Orbit( Vector2 mouseDelta )
    {
      if ( IsPointerOverUi() )
        return;

      Vector3 pivot = orbitTarget != null
        ? orbitTarget.position
        : transform.position + transform.forward * Mathf.Max( orbitDistance, 0.1f );

      transform.RotateAround( pivot, Vector3.up, mouseDelta.x * lookSensitivity );
      transform.RotateAround( pivot, transform.right, -mouseDelta.y * lookSensitivity );
      transform.LookAt( pivot );
    }

    private static void CaptureCursor()
    {
      Cursor.lockState = CursorLockMode.Locked;
      Cursor.visible = false;
    }

    private static void ReleaseCursor()
    {
      if ( Cursor.lockState == CursorLockMode.Locked )
        Cursor.lockState = CursorLockMode.None;
      Cursor.visible = true;
    }

    private static bool IsPointerOverUi()
    {
      return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
    }

    private static bool IsRightMousePressed()
    {
#if ENABLE_INPUT_SYSTEM
      return Mouse.current != null && Mouse.current.rightButton.isPressed;
#else
      return Input.GetMouseButton( 1 );
#endif
    }

    private static bool IsMiddleMousePressed()
    {
#if ENABLE_INPUT_SYSTEM
      return Mouse.current != null && Mouse.current.middleButton.isPressed;
#else
      return Input.GetMouseButton( 2 );
#endif
    }

    private static bool IsLeftMousePressed()
    {
#if ENABLE_INPUT_SYSTEM
      return Mouse.current != null && Mouse.current.leftButton.isPressed;
#else
      return Input.GetMouseButton( 0 );
#endif
    }

    private static Vector2 MouseDelta()
    {
#if ENABLE_INPUT_SYSTEM
      return Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;
#else
      return new Vector2( Input.GetAxis( "Mouse X" ), Input.GetAxis( "Mouse Y" ) );
#endif
    }

    private static float MouseScroll()
    {
#if ENABLE_INPUT_SYSTEM
      return Mouse.current != null ? Mouse.current.scroll.ReadValue().y : 0f;
#else
      return Input.mouseScrollDelta.y;
#endif
    }

    private static bool IsAltPressed()
    {
      return KeyPressed( KeyCode.LeftAlt ) || KeyPressed( KeyCode.RightAlt );
    }

    private static bool KeyPressed( KeyCode keyCode )
    {
#if ENABLE_INPUT_SYSTEM
      Keyboard keyboard = Keyboard.current;
      if ( keyboard == null )
        return false;

      Key key = ToInputSystemKey( keyCode );
      return key != Key.None && keyboard[ key ].isPressed;
#else
      return Input.GetKey( keyCode );
#endif
    }

#if ENABLE_INPUT_SYSTEM
    private static Key ToInputSystemKey( KeyCode keyCode )
    {
      switch ( keyCode ) {
        case KeyCode.W:
          return Key.W;
        case KeyCode.A:
          return Key.A;
        case KeyCode.S:
          return Key.S;
        case KeyCode.D:
          return Key.D;
        case KeyCode.Q:
          return Key.Q;
        case KeyCode.E:
          return Key.E;
        case KeyCode.LeftShift:
          return Key.LeftShift;
        case KeyCode.RightShift:
          return Key.RightShift;
        case KeyCode.LeftAlt:
          return Key.LeftAlt;
        case KeyCode.RightAlt:
          return Key.RightAlt;
        default:
          return Key.None;
      }
    }
#endif
  }
}
