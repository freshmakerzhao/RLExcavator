using UnityEngine;

namespace RLExcavator
{
  /// <summary>
  /// Inspector telemetry for manually checking a scale excavator scene.
  /// This component reads existing controller state only; it never drives the machine.
  /// </summary>
  public class ExcavatorTelemetryObserver : MonoBehaviour
  {
    [Header("观测目标")]
    [Tooltip("挖机语义坐标注册器。优先从这里读取 MachineRoot、BucketTip 和 Pitch 参考。")]
    public ExcavatorFrameRegistry frameRegistry;

    [Tooltip("待观测的挖掘机控制器。")]
    public ExcavatorController excavator;
    [Tooltip("铲斗尖端点。")]
    public Transform bucketTip;
    [Tooltip("铲斗姿态参考。为空时使用铲斗尖端。")]
    public Transform bucketAttitudeReference;
    [Tooltip("俯仰参考坐标系，建议绑定上车回转平台。")]
    public Transform bucketPitchFrame;
    [Tooltip("铲斗本地俯仰轴。")]
    public Vector3 bucketPitchAxisLocal = Vector3.forward;
    [Tooltip("额外显示的位置点。")]
    [HideInInspector]
    public Transform[] observedTransforms;
    [Tooltip("缺引用时按名称自动查找。")]
    [HideInInspector]
    public bool autoFindReferences = true;
    [Tooltip("在物理步刷新观测。")]
    [HideInInspector]
    public bool refreshInFixedUpdate = false;

    [Header("基础状态")]
    [Tooltip("是否已绑定挖掘机。")]
    [HideInInspector]
    public bool targetAssigned = false;
    [Tooltip("当前观测目标名称。")]
    [HideInInspector]
    public string targetName = "未绑定挖掘机。";
    [Tooltip("最近一次刷新时间。")]
    [HideInInspector]
    public float refreshTime = 0f;
    [Tooltip("控制输入是否开启。")]
    [HideInInspector]
    public bool commandInputEnabled = false;
    [Tooltip("是否正在回家复位。")]
    [HideInInspector]
    public bool isHoming = false;
    [Tooltip("挖掘机世界位置。")]
    [HideInInspector]
    public Vector3 rootPosition = Vector3.zero;
    [Tooltip("挖掘机世界欧拉角。")]
    [HideInInspector]
    public Vector3 rootEulerAngles = Vector3.zero;

    [Header("执行器状态")]
    [Tooltip("大臂状态。")]
    [HideInInspector]
    public ActuatorTelemetry boom = new ActuatorTelemetry( "Boom" );
    [Tooltip("小臂状态。")]
    [HideInInspector]
    public ActuatorTelemetry stick = new ActuatorTelemetry( "Stick" );
    [Tooltip("铲斗状态。")]
    [HideInInspector]
    public ActuatorTelemetry bucket = new ActuatorTelemetry( "Bucket" );
    [Tooltip("回转状态。")]
    [HideInInspector]
    public ActuatorTelemetry swing = new ActuatorTelemetry( "Swing" );

    [Header("铲斗Pitch")]
    [Tooltip("是否已绑定铲斗尖端。")]
    [HideInInspector]
    public bool bucketTipAssigned = false;
    [Tooltip("铲斗尖端名称。")]
    [HideInInspector]
    public string bucketTipName = "";
    [Tooltip("俯仰参考坐标系名称。")]
    [HideInInspector]
    public string bucketPitchFrameName = "";
    [Tooltip("当前铲斗俯仰角，单位度。")]
    [HideInInspector]
    public float bucketPitchDeg = 0f;
    [Tooltip("铲斗俯仰角速度，单位度每秒。")]
    [HideInInspector]
    public float bucketPitchVelocityDegPerSec = 0f;
    [Tooltip("MoveToDig 目标俯仰角，单位度。")]
    [HideInInspector]
    public float moveToDigTargetPitchDeg = -90f;
    [Tooltip("CarryMaterial 目标俯仰角，单位度。")]
    [HideInInspector]
    public float carryMaterialTargetPitchDeg = 0f;
    [Tooltip("当前俯仰到 MoveToDig 目标的误差。")]
    [HideInInspector]
    public float moveToDigPitchErrorDeg = 0f;
    [Tooltip("当前俯仰到 CarryMaterial 目标的误差。")]
    [HideInInspector]
    public float carryMaterialPitchErrorDeg = 0f;

    [Header("额外点状态")]
    [Tooltip("额外位置点的实时状态。")]
    [HideInInspector]
    public TransformTelemetry[] observedTransformState = new TransformTelemetry[ 0 ];

    private bool m_hasPreviousBucketPitch = false;
    private float m_previousBucketPitchDeg = 0f;
    private float m_previousBucketPitchTime = 0f;
    private bool m_taskProfileLoaded = false;

    private void Awake()
    {
      EnsureTelemetryObjects();
      RefreshTelemetry();
    }

    private void Update()
    {
      if ( !refreshInFixedUpdate )
        RefreshTelemetry();
    }

    private void FixedUpdate()
    {
      if ( refreshInFixedUpdate )
        RefreshTelemetry();
    }

    [ContextMenu("刷新观测")]
    public void RefreshTelemetry()
    {
      EnsureTelemetryObjects();
      LoadTaskProfileTargetsIfNeeded();
      ResolveReferencesIfNeeded();

      refreshTime = Time.time;

      if ( excavator == null ) {
        ClearTargetState();
        CaptureBucketPitch();
        CaptureObservedTransforms();
        return;
      }

      targetAssigned = true;
      targetName = excavator.name;
      commandInputEnabled = excavator.CommandInputEnabled;
      isHoming = excavator.IsHoming;
      Transform rootFrame = frameRegistry != null && frameRegistry.MachineRoot != null
        ? frameRegistry.MachineRoot
        : excavator.transform;
      rootPosition = rootFrame.position;
      rootEulerAngles = rootFrame.eulerAngles;

      boom.Capture( excavator.boom, SafeReadFloat( () => excavator.BoomNormalizedPosition ), excavator.boomMaxSpeed );
      stick.Capture( excavator.stick, SafeReadFloat( () => excavator.StickNormalizedPosition ), excavator.stickMaxSpeed );
      bucket.Capture( excavator.bucket, SafeReadFloat( () => excavator.BucketNormalizedPosition ), excavator.bucketMaxSpeed );
      swing.Capture( excavator.swing, SafeReadFloat( () => excavator.SwingNormalizedPosition ), excavator.swingMaxSpeed );

      CaptureBucketPitch();
      CaptureObservedTransforms();
    }

    private void ResolveReferencesIfNeeded()
    {
      if ( !autoFindReferences )
        return;

      if ( frameRegistry == null )
        frameRegistry = ExcavatorFrameRegistry.FindNearest( this );

      if ( frameRegistry != null )
        frameRegistry.ResolveMissingReferences();

      if ( excavator == null )
        excavator = frameRegistry != null
          ? frameRegistry.GetComponent<ExcavatorController>() ?? FindFirstObjectByType<ExcavatorController>()
          : FindFirstObjectByType<ExcavatorController>();

      Transform root = excavator != null ? excavator.transform : null;

      if ( bucketTip == null )
        bucketTip = frameRegistry != null && frameRegistry.BucketTip != null
          ? frameRegistry.BucketTip
          : FindChildByName( root, "BucketTip" );

      if ( bucketAttitudeReference == null )
        bucketAttitudeReference =
          FindChildByName( root, "Body_Bucket" ) ??
          FindChildByName( root, "Bucket" ) ??
          bucketTip;

      if ( bucketPitchFrame == null ) {
        if ( frameRegistry != null && frameRegistry.BucketPitchFrame != null )
          bucketPitchFrame = frameRegistry.BucketPitchFrame;
        else
          bucketPitchFrame =
            FindChildByName( root, "Body_Swing" ) ??
            FindChildByName( root, "UpperCarriage" ) ??
            FindChildByName( root, "Cabin" ) ??
            FindChildByName( root, "Chassisbody" ) ??
            root;
      }
    }

    private void ClearTargetState()
    {
      targetAssigned = false;
      targetName = "未绑定挖掘机。";
      commandInputEnabled = false;
      isHoming = false;
      rootPosition = Vector3.zero;
      rootEulerAngles = Vector3.zero;

      boom.Clear();
      stick.Clear();
      bucket.Clear();
      swing.Clear();
    }

    private void EnsureTelemetryObjects()
    {
      if ( boom == null )
        boom = new ActuatorTelemetry( "Boom" );
      if ( stick == null )
        stick = new ActuatorTelemetry( "Stick" );
      if ( bucket == null )
        bucket = new ActuatorTelemetry( "Bucket" );
      if ( swing == null )
        swing = new ActuatorTelemetry( "Swing" );
    }

    private void LoadTaskProfileTargetsIfNeeded()
    {
      if ( m_taskProfileLoaded )
        return;

      m_taskProfileLoaded = true;
      try {
        MachineTaskProfile taskProfile = MachineProfileLoader.Load().TaskProfile;
        moveToDigTargetPitchDeg = taskProfile.MoveToDigBucketPitchTargetDeg;
        carryMaterialTargetPitchDeg = taskProfile.CarryMaterialBucketPitchTargetDeg;
      }
      catch ( System.Exception exception ) {
        Debug.LogWarning( "[ExcavatorTelemetryObserver] Failed to load task pitch targets from machine_profile.json: " + exception.Message, this );
      }
    }

    private void CaptureBucketPitch()
    {
      Transform reference = bucketAttitudeReference != null ? bucketAttitudeReference : bucketTip;
      bucketTipAssigned = bucketTip != null;
      bucketTipName = bucketTip != null ? bucketTip.name : "";
      Transform frame = bucketPitchFrame != null
        ? bucketPitchFrame
        : excavator != null ? excavator.transform : transform;
      bucketPitchFrameName = frame != null ? frame.name : "";

      if ( reference == null || frame == null ) {
        bucketPitchDeg = 0f;
        bucketPitchVelocityDegPerSec = 0f;
        moveToDigPitchErrorDeg = 0f;
        carryMaterialPitchErrorDeg = 0f;
        m_hasPreviousBucketPitch = false;
        return;
      }

      Vector3 localAxis = bucketPitchAxisLocal.sqrMagnitude > 1e-6f ? bucketPitchAxisLocal.normalized : Vector3.forward;
      Vector3 bucketAxis = reference.TransformDirection( localAxis );
      Vector3 pitchAxis = frame.right;
      Vector3 frameForward = Vector3.ProjectOnPlane( frame.forward, pitchAxis );
      Vector3 bucketForward = Vector3.ProjectOnPlane( bucketAxis, pitchAxis );

      if ( frameForward.sqrMagnitude < 1e-6f || bucketForward.sqrMagnitude < 1e-6f ) {
        bucketPitchDeg = 0f;
        bucketPitchVelocityDegPerSec = 0f;
      }
      else {
        bucketPitchDeg = Vector3.SignedAngle( frameForward.normalized, bucketForward.normalized, pitchAxis );
        float dt = refreshTime - m_previousBucketPitchTime;
        bucketPitchVelocityDegPerSec = m_hasPreviousBucketPitch && dt > 1e-5f
          ? Mathf.DeltaAngle( m_previousBucketPitchDeg, bucketPitchDeg ) / dt
          : 0f;
      }

      m_previousBucketPitchDeg = bucketPitchDeg;
      m_previousBucketPitchTime = refreshTime;
      m_hasPreviousBucketPitch = true;
      moveToDigPitchErrorDeg = Mathf.DeltaAngle( bucketPitchDeg, moveToDigTargetPitchDeg );
      carryMaterialPitchErrorDeg = Mathf.DeltaAngle( bucketPitchDeg, carryMaterialTargetPitchDeg );
    }

    private void CaptureObservedTransforms()
    {
      if ( observedTransforms == null || observedTransforms.Length == 0 ) {
        observedTransformState = new TransformTelemetry[ 0 ];
        return;
      }

      if ( observedTransformState == null || observedTransformState.Length != observedTransforms.Length )
        observedTransformState = new TransformTelemetry[ observedTransforms.Length ];

      for ( int i = 0; i < observedTransforms.Length; i++ ) {
        if ( observedTransformState[ i ] == null )
          observedTransformState[ i ] = new TransformTelemetry();

        observedTransformState[ i ].Capture( observedTransforms[ i ] );
      }
    }

    private static Transform FindChildByName( Transform root, string childName )
    {
      if ( root == null || string.IsNullOrEmpty( childName ) )
        return null;

      if ( root.name == childName )
        return root;

      for ( int i = 0; i < root.childCount; i++ ) {
        Transform match = FindChildByName( root.GetChild( i ), childName );
        if ( match != null )
          return match;
      }

      return null;
    }

    private static float SafeReadFloat( System.Func<float> valueProvider )
    {
      try {
        return valueProvider();
      }
      catch ( System.Exception ) {
        return float.NaN;
      }
    }
  }

  [System.Serializable]
  public class ActuatorTelemetry
  {
    [Tooltip("执行器名称。")]
    public string label = "";
    [Tooltip("已绑定约束数量。")]
    public int boundConstraintCount = 0;
    [Tooltip("最大目标速度。")]
    public float maxSpeed = 0f;
    [Tooltip("归一化位置。")]
    public float normalizedPosition = 0f;
    [Tooltip("原始关节位置。")]
    public float[] rawPositions = new float[ 0 ];
    [Tooltip("每个约束状态。")]
    public ConstraintTelemetry[] constraints = new ConstraintTelemetry[ 0 ];

    public ActuatorTelemetry()
    {
    }

    public ActuatorTelemetry( string label )
    {
      this.label = label;
    }

    public void Capture(
      AGXUnity.Constraint[] sourceConstraints,
      float sourceNormalizedPosition,
      float sourceMaxSpeed )
    {
      maxSpeed = sourceMaxSpeed;
      normalizedPosition = sourceNormalizedPosition;

      int length = sourceConstraints != null ? sourceConstraints.Length : 0;
      EnsureConstraintArray( length );
      EnsureRawPositionArray( length );

      boundConstraintCount = 0;
      for ( int i = 0; i < length; i++ ) {
        if ( sourceConstraints[ i ] != null )
          boundConstraintCount++;

        rawPositions[ i ] = SafeReadAngle( sourceConstraints[ i ] );
        constraints[ i ].Capture( sourceConstraints[ i ] );
      }
    }

    public void Capture(
      AGXUnity.Constraint sourceConstraint,
      float sourceNormalizedPosition,
      float sourceMaxSpeed )
    {
      maxSpeed = sourceMaxSpeed;
      normalizedPosition = sourceNormalizedPosition;
      rawPositions = new[] { SafeReadAngle( sourceConstraint ) };

      EnsureConstraintArray( 1 );
      boundConstraintCount = sourceConstraint != null ? 1 : 0;
      constraints[ 0 ].Capture( sourceConstraint );
    }

    public void Clear()
    {
      boundConstraintCount = 0;
      maxSpeed = 0f;
      normalizedPosition = 0f;
      rawPositions = new float[ 0 ];
      constraints = new ConstraintTelemetry[ 0 ];
    }

    private void EnsureConstraintArray( int length )
    {
      if ( constraints == null || constraints.Length != length )
        constraints = new ConstraintTelemetry[ length ];

      for ( int i = 0; i < length; i++ )
        if ( constraints[ i ] == null )
          constraints[ i ] = new ConstraintTelemetry();
    }

    private void EnsureRawPositionArray( int length )
    {
      if ( rawPositions == null || rawPositions.Length != length )
        rawPositions = new float[ length ];
    }

    private static float SafeReadAngle( AGXUnity.Constraint constraint )
    {
      if ( constraint == null )
        return 0f;

      try {
        return constraint.GetCurrentAngle();
      }
      catch ( System.Exception ) {
        return float.NaN;
      }
    }
  }

  [System.Serializable]
  public class ConstraintTelemetry
  {
    [Tooltip("是否已绑定。")]
    public bool bound = false;
    [Tooltip("约束名称。")]
    public string constraintName = "";
    [Tooltip("当前关节位置。")]
    public float position = 0f;
    [Tooltip("是否有速度控制器。")]
    public bool targetSpeedControllerFound = false;
    [Tooltip("速度控制器是否启用。")]
    public bool targetSpeedEnabled = false;
    [Tooltip("当前目标速度。")]
    public float targetSpeed = 0f;
    [Tooltip("是否有行程控制器。")]
    public bool rangeControllerFound = false;
    [Tooltip("行程限制是否启用。")]
    public bool rangeEnabled = false;
    [Tooltip("行程下限。")]
    public float rangeMin = 0f;
    [Tooltip("行程上限。")]
    public float rangeMax = 0f;

    public void Capture( AGXUnity.Constraint constraint )
    {
      if ( constraint == null ) {
        Clear();
        return;
      }

      bound = true;
      constraintName = constraint.name;
      position = SafeReadFloat( () => constraint.GetCurrentAngle() );

      var motor = constraint.GetController<AGXUnity.TargetSpeedController>();
      targetSpeedControllerFound = motor != null;
      targetSpeedEnabled = motor != null && motor.Enable;
      targetSpeed = motor != null ? motor.Speed : 0f;

      var range = constraint.GetController<AGXUnity.RangeController>();
      rangeControllerFound = range != null;
      rangeEnabled = range != null && range.Enable;
      if ( range != null ) {
        rangeMin = range.Range.Min;
        rangeMax = range.Range.Max;
      }
      else {
        rangeMin = 0f;
        rangeMax = 0f;
      }
    }

    public void Clear()
    {
      bound = false;
      constraintName = "";
      position = 0f;
      targetSpeedControllerFound = false;
      targetSpeedEnabled = false;
      targetSpeed = 0f;
      rangeControllerFound = false;
      rangeEnabled = false;
      rangeMin = 0f;
      rangeMax = 0f;
    }

    private static float SafeReadFloat( System.Func<float> valueProvider )
    {
      try {
        return valueProvider();
      }
      catch ( System.Exception ) {
        return float.NaN;
      }
    }
  }

  [System.Serializable]
  public class TransformTelemetry
  {
    [Tooltip("是否已绑定。")]
    public bool bound = false;
    [Tooltip("对象名称。")]
    public string transformName = "";
    [Tooltip("世界位置。")]
    public Vector3 position = Vector3.zero;
    [Tooltip("本地位置。")]
    public Vector3 localPosition = Vector3.zero;
    [Tooltip("世界欧拉角。")]
    public Vector3 eulerAngles = Vector3.zero;
    [Tooltip("本地欧拉角。")]
    public Vector3 localEulerAngles = Vector3.zero;

    public void Capture( Transform source )
    {
      if ( source == null ) {
        Clear();
        return;
      }

      bound = true;
      transformName = source.name;
      position = source.position;
      localPosition = source.localPosition;
      eulerAngles = source.eulerAngles;
      localEulerAngles = source.localEulerAngles;
    }

    public void Clear()
    {
      bound = false;
      transformName = "";
      position = Vector3.zero;
      localPosition = Vector3.zero;
      eulerAngles = Vector3.zero;
      localEulerAngles = Vector3.zero;
    }
  }
}
