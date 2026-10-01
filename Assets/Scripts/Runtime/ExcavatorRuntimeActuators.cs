using AGXUnity;
using UnityEngine;
using AGXRigidBody = AGXUnity.RigidBody;

namespace RLExcavator
{
  /// <summary>AGX actuator driver for the external runtime scene.</summary>
  public sealed class ExcavatorRuntimeActuators : MonoBehaviour
  {
    // Matches the neutral stick interval in F407 manual_action.h.
    private const float ManualDeadzone = 0.15f;

    public Constraint[] boom;
    public Constraint[] stick;
    public Constraint[] bucket;
    public Constraint swing;

    public float boomAcceleration = 0.7f;
    public float bucketAcceleration = 0.7f;
    public int homeSettleSteps = 60;
    public int maxHomeSettleSteps = 300;
    public float homeTolerance = 0.01f;

    private MachineActuatorProfile[] m_limits;
    private float[] m_boomHome;
    private float[] m_stickHome;
    private float[] m_bucketHome;
    private float m_swingHome;
    private float m_boomSpeed;
    private float m_bucketSpeed;
    private Vector3 m_rootPosition;
    private Quaternion m_rootRotation;
    private bool m_ready;
    private bool m_homeCaptured;
    private Coroutine m_homeRoutine;

    public bool IsHoming { get; private set; }
    public bool HasActuators => ValidSet( boom ) && ValidSet( stick ) &&
      ValidSet( bucket ) && swing != null;

    private void Awake()
    {
      m_rootPosition = transform.position;
      m_rootRotation = transform.rotation;
      try {
        MachineProfile profile = MachineProfileLoader.Load();
        string[] order = profile.ActionOrder;
        if ( order.Length != 4 || order[0] != "boom" || order[1] != "stick" ||
             order[2] != "bucket" || order[3] != "swing" )
          throw new System.InvalidOperationException( "Unexpected action order in machine profile." );

        m_limits = new[] { profile.GetActuator( "boom" ), profile.GetActuator( "stick" ),
          profile.GetActuator( "bucket" ), profile.GetActuator( "swing" ) };
        if ( !HasActuators )
          throw new System.InvalidOperationException( "Missing AGX actuator constraint." );

        ApplyRange( boom, m_limits[0] );
        ApplyRange( stick, m_limits[1] );
        ApplyRange( bucket, m_limits[2] );
        ApplyRange( new[] { swing }, m_limits[3] );
        m_ready = true;
      }
      catch ( System.Exception error ) {
        Debug.LogError( "[ExcavatorRuntimeActuators] Initialization failed: " + error.Message, this );
      }
    }

    private void FixedUpdate()
    {
      if ( m_ready && !m_homeCaptured )
        CaptureHomePose();
    }

    public bool TrySetPhysicalSpeeds( float boomSpeed, float stickSpeed, float bucketSpeed, float swingSpeed )
    {
      if ( !m_ready || IsHoming || !ValidSpeed( boomSpeed, m_limits[0] ) ||
           !ValidSpeed( stickSpeed, m_limits[1] ) ||
           !ValidSpeed( bucketSpeed, m_limits[2] ) ||
           !ValidSpeed( swingSpeed, m_limits[3] ) ) {
        StopAll();
        return false;
      }

      float dt = Time.fixedDeltaTime;
      m_boomSpeed = Mathf.MoveTowards( m_boomSpeed, boomSpeed,
        Mathf.Max( boomAcceleration, 0.01f ) * dt );
      m_bucketSpeed = Mathf.MoveTowards( m_bucketSpeed, bucketSpeed,
        Mathf.Max( bucketAcceleration, 0.01f ) * dt );
      Drive( boom, m_boomSpeed );
      Drive( stick, stickSpeed );
      Drive( bucket, m_bucketSpeed );
      Drive( swing, swingSpeed );
      return true;
    }

    public bool TrySetManualAction( float boomAction, float stickAction, float bucketAction, float swingAction )
    {
      if ( !m_ready || !ValidAction( boomAction ) || !ValidAction( stickAction ) ||
           !ValidAction( bucketAction ) || !ValidAction( swingAction ) ) {
        StopAll();
        return false;
      }

      // The real manual command drives valves/ESCs. Until hydraulic response is
      // calibrated, use a deadzone and the shared speed limits as a sim proxy.
      return TrySetPhysicalSpeeds(
        ManualSpeed( boomAction, m_limits[0] ),
        ManualSpeed( stickAction, m_limits[1] ),
        ManualSpeed( bucketAction, m_limits[2] ),
        ManualSpeed( swingAction, m_limits[3] ) );
    }

    public void StopAll()
    {
      m_boomSpeed = 0f;
      m_bucketSpeed = 0f;
      Drive( boom, 0f );
      Drive( stick, 0f );
      Drive( bucket, 0f );
      Drive( swing, 0f );
    }

    public void BeginHomePoseReset()
    {
      if ( !m_ready )
        throw new System.InvalidOperationException( "Actuator driver is not ready." );
      CancelHomePoseReset();
      if ( !m_homeCaptured )
        CaptureHomePose();
      m_homeRoutine = StartCoroutine( ResetRoutine() );
    }

    public void CancelHomePoseReset()
    {
      if ( m_homeRoutine != null ) {
        StopCoroutine( m_homeRoutine );
        m_homeRoutine = null;
      }
      LockAll( false );
      IsHoming = false;
      StopAll();
    }

    private System.Collections.IEnumerator ResetRoutine()
    {
      IsHoming = true;
      StopAll();
      transform.SetPositionAndRotation( m_rootPosition, m_rootRotation );
      foreach ( AGXRigidBody body in GetComponentsInChildren<AGXRigidBody>( true ) )
        body.SyncNativeTransform();
      Physics.SyncTransforms();
      ClearBodyVelocities();
      LockAll( true );

      int minSteps = Mathf.Max( homeSettleSteps, 1 );
      int maxSteps = Mathf.Max( maxHomeSettleSteps, minSteps );
      for ( int steps = 0; steps < minSteps || ( steps < maxSteps && !AtHome() ); steps++ )
        yield return new WaitForFixedUpdate();

      LockAll( false );
      StopAll();
      ClearBodyVelocities();
      IsHoming = false;
      m_homeRoutine = null;
    }

    private void CaptureHomePose()
    {
      m_boomHome = Positions( boom );
      m_stickHome = Positions( stick );
      m_bucketHome = Positions( bucket );
      m_swingHome = swing.GetCurrentAngle();
      m_homeCaptured = true;
    }

    private void LockAll( bool enabled )
    {
      Lock( boom, m_boomHome, enabled );
      Lock( stick, m_stickHome, enabled );
      Lock( bucket, m_bucketHome, enabled );
      Lock( swing, m_swingHome, enabled );
    }

    private bool AtHome()
    {
      float tolerance = Mathf.Max( homeTolerance, 0.0001f );
      return AtHome( boom, m_boomHome, tolerance ) &&
        AtHome( stick, m_stickHome, tolerance ) &&
        AtHome( bucket, m_bucketHome, tolerance ) &&
        Mathf.Abs( swing.GetCurrentAngle() - m_swingHome ) <= tolerance;
    }

    private void ClearBodyVelocities()
    {
      foreach ( UnityEngine.Rigidbody body in GetComponentsInChildren<UnityEngine.Rigidbody>( true ) ) {
        body.linearVelocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
      }
      foreach ( AGXRigidBody body in GetComponentsInChildren<AGXRigidBody>( true ) ) {
        body.LinearVelocity = Vector3.zero;
        body.AngularVelocity = Vector3.zero;
      }
    }

    private static bool ValidSet( Constraint[] constraints )
    {
      if ( constraints == null || constraints.Length == 0 ) return false;
      foreach ( Constraint constraint in constraints )
        if ( constraint == null ) return false;
      return true;
    }

    private static bool ValidSpeed( float speed, MachineActuatorProfile limit )
    {
      return !float.IsNaN( speed ) && !float.IsInfinity( speed ) &&
        speed <= limit.MaxSpeedPositive && speed >= -limit.MaxSpeedNegative;
    }

    private static bool ValidAction( float value )
    {
      return !float.IsNaN( value ) && !float.IsInfinity( value ) && value >= -1f && value <= 1f;
    }

    private static float ManualSpeed( float action, MachineActuatorProfile limit )
    {
      // F407: positive boom/stick stick action extends, positive bucket retracts.
      // The scene's prismatic axes themselves point toward physical extension.
      if ( limit.Name == "bucket" ) action = -action;
      float magnitude = Mathf.Abs( action );
      if ( magnitude <= ManualDeadzone ) return 0f;
      float fraction = (magnitude - ManualDeadzone) / (1f - ManualDeadzone);
      return action > 0f ? fraction * limit.MaxSpeedPositive : -fraction * limit.MaxSpeedNegative;
    }

    private static void ApplyRange( Constraint[] constraints, MachineActuatorProfile actuator )
    {
      foreach ( Constraint constraint in constraints ) {
        RangeController range = constraint.GetController<RangeController>();
        if ( range == null ) {
          if ( actuator.RangeEnabledRequired )
            throw new System.InvalidOperationException( "Missing range controller: " + actuator.Name );
          continue;
        }
        if ( actuator.HasFiniteRange )
          range.Range = new RangeReal( actuator.RangeMin, actuator.RangeMax );
        range.Enable = actuator.HasFiniteRange && actuator.RangeEnabledRequired;
      }
    }

    private static float[] Positions( Constraint[] constraints )
    {
      var result = new float[ constraints.Length ];
      for ( int i = 0; i < constraints.Length; i++ )
        result[i] = constraints[i].GetCurrentAngle();
      return result;
    }

    private static bool AtHome( Constraint[] constraints, float[] home, float tolerance )
    {
      for ( int i = 0; i < constraints.Length; i++ )
        if ( Mathf.Abs( constraints[i].GetCurrentAngle() - home[i] ) > tolerance ) return false;
      return true;
    }

    private static void Lock( Constraint[] constraints, float[] home, bool enabled )
    {
      if ( constraints == null || home == null ) return;
      for ( int i = 0; i < constraints.Length; i++ )
        Lock( constraints[i], home[i], enabled );
    }

    private static void Lock( Constraint constraint, float home, bool enabled )
    {
      if ( constraint == null ) return;
      TargetSpeedController motor = constraint.GetController<TargetSpeedController>();
      LockController locker = constraint.GetController<LockController>();
      if ( motor != null ) { motor.Speed = 0f; motor.Enable = !enabled; }
      if ( locker != null ) { locker.Position = home; locker.Enable = enabled; }
    }

    private static void Drive( Constraint[] constraints, float speed )
    {
      if ( constraints == null ) return;
      foreach ( Constraint constraint in constraints ) Drive( constraint, speed );
    }

    private static void Drive( Constraint constraint, float speed )
    {
      if ( constraint == null ) return;
      LockController locker = constraint.GetController<LockController>();
      if ( locker != null ) locker.Enable = false;
      TargetSpeedController motor = constraint.GetController<TargetSpeedController>();
      if ( motor != null ) {
        motor.Enable = true;
        motor.LockAtZeroSpeed = false;
        motor.Speed = speed;
      }
    }

    private void OnDisable()
    {
      if ( Application.isPlaying ) CancelHomePoseReset();
    }
  }
}
