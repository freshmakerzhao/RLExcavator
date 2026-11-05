using System;
using AGXUnity;
using UnityEngine;

namespace RLExcavator
{
  [Serializable]
  public sealed class RealExecutionCase
  {
    public string id;
    public string split;
    public string taskMode;
    public int direction;
    public bool longerPath;
    public Vector4 startJointPose;
    public Vector4 goalJointPose;
    public Vector4[] jointSamples;
    public Vector3[] referenceUnityLocal = new Vector3[ 0 ];
    public float[] sampledPitchDeg = new float[ 0 ];
    public bool[] sampledClearance = new bool[ 0 ];
    public string validationSource = "";
    public float targetPitchDeg;
    public float pathLengthM;
  }

  [Serializable]
  public sealed class RealExecutionCaseSet
  {
    public string schema = "real_execution_cases_v1";
    public RealExecutionCase[] cases;
  }

  /// <summary>
  /// Isolated real-execution course. Joint candidates are NOT physical feasibility evidence.
  /// Only routes resolved from the model's sampled tip, pitch and clearance may run.
  /// Inter-sample collision freedom and real-machine validity are not implied by this check.
  /// </summary>
  [DisallowMultipleComponent]
  public sealed class RealExecutionCourse : MonoBehaviour
  {
    public RealExecutionCase[] cases;
    public string split = "train";
    public int seed = 20261115;
    [Min(0.001f)] public float maximumResetTipErrorM = 0.02f;
    public RealExecutionCase LastCase { get; private set; }

    // Intersection of V5 mechanical range and fixed firmware raw ranges, with 3 mm margin.
    public static Vector4 Minimum => new Vector4( -0.077f, -0.087f, 0.0013f, -1.6f );
    public static Vector4 Maximum => new Vector4( 0.0255f, -0.02025f, 0.082f, 1.6f );
    private System.Random m_random;
    private int m_nextIndex;
    private static MachineTaskProfile s_tasks;

    public RealExecutionCase PrepareEpisode( ExcavatorController controller )
    {
      if ( controller == null ) throw new ArgumentNullException( nameof( controller ) );
      CheckConstraints( controller.boom, "boom" );
      CheckConstraints( controller.stick, "stick" );
      CheckConstraints( controller.bucket, "bucket" );
      if ( controller.swing == null ) throw new InvalidOperationException( "Missing swing constraint." );
      ValidateSplit( split );
      if ( cases == null || cases.Length == 0 )
        throw new InvalidOperationException( "Real execution course requires physically resolved cases." );
      if ( m_random == null ) m_random = new System.Random( seed );
      int index = split == "train" ? m_random.Next( cases.Length ) : m_nextIndex++ % cases.Length;
      RealExecutionCase selected = cases[ index ];
      if ( selected == null || selected.split != split )
        throw new InvalidOperationException( "Course split does not match the selected case." );
      ValidateResolved( selected );
      LastCase = Copy( selected );
      Vector4 q = LastCase.startJointPose;
      controller.boomHomePose = Repeat( q.x, controller.boom.Length );
      controller.stickHomePose = Repeat( q.y, controller.stick.Length );
      controller.bucketHomePose = Repeat( q.z, controller.bucket.Length );
      controller.swingHomePose = q.w;
      controller.useInspectorHomePose = true;
      return Copy( LastCase );
    }

    public bool ValidateReset( Vector4 actualJointPose, Vector4 measuredVelocity )
    {
      if ( LastCase == null || !Finite( actualJointPose ) || !Finite( measuredVelocity ) ) return false;
      for ( int i = 0; i < 4; i++ ) {
        float tolerance = i == 3 ? 0.01f : 0.002f;
        float speedTolerance = i == 3 ? 0.02f : 0.003f;
        if ( Mathf.Abs( actualJointPose[ i ] - LastCase.startJointPose[ i ] ) > tolerance
          || Mathf.Abs( measuredVelocity[ i ] ) > speedTolerance ) return false;
      }
      return true;
    }

    public Vector3[] CreateReference( Vector3 actualStartUnityLocal )
    {
      if ( LastCase == null ) throw new InvalidOperationException( "Prepare a resolved case before its reference." );
      ValidateResolved( LastCase );
      if ( !Finite( actualStartUnityLocal ) || !Finite( maximumResetTipErrorM ) || maximumResetTipErrorM <= 0f
        || Vector3.Distance( actualStartUnityLocal, LastCase.referenceUnityLocal[ 0 ] ) > maximumResetTipErrorM )
        throw new InvalidOperationException( "Physical reset does not match the resolved reference start." );
      var route = (Vector3[])LastCase.referenceUnityLocal.Clone();
      route[ 0 ] = actualStartUnityLocal;
      return route;
    }

    /// <summary>
    /// 2 modes × 2 swing directions × 2 joint-displacement classes × repetitions.
    /// Start-stick and mode-matched goal-stick intervals are disjoint across splits.
    /// Elevated candidates use the policy-independent FK qualification grid, never clamped old seeds.
    /// "longerPath" denotes the nominal joint-displacement class until actual FK length is recorded.
    /// </summary>
    public static RealExecutionCase[] GenerateCandidates( string split, int seed, int repeatsPerCell = 2 )
    {
      ValidateSplit( split );
      if ( repeatsPerCell < 1 || repeatsPerCell > 100 ) throw new ArgumentOutOfRangeException( nameof( repeatsPerCell ) );
      var random = new System.Random( seed );
      var result = new RealExecutionCase[ 8 * repeatsPerCell ];
      Vector2 stickStartRange = split == "train" ? new Vector2( -0.063f, -0.058f )
        : split == "dev" ? new Vector2( -0.052f, -0.047f ) : new Vector2( -0.041f, -0.036f );
      int index = 0;
      for ( int mode = 0; mode < 2; mode++ )
        foreach ( int side in new[] { -1, 1 } )
          for ( int length = 0; length < 2; length++ )
            for ( int repetition = 0; repetition < repeatsPerCell; repetition++ ) {
              var start = new Vector4( Uniform( random, 0.006f, 0.010f ),
                Uniform( random, stickStartRange.x, stickStartRange.y ), Uniform( random, 0.060f, 0.072f ),
                side * Uniform( random, 0.05f, 0.20f ) );
              float scale = length == 0 ? 1f : 2f;
              var goal = new Vector4( start.x + ( mode == 0 ? 0.002f : 0.006f ) * scale,
                start.y + ( mode == 0 ? 0.003f : -0.004f ) * scale,
                mode == 0 ? 0.015f : 0.082f,
                start.w + side * ( length == 0 ? 0.15f : 0.40f ) );
              var item = new RealExecutionCase {
                id = split + "_" + seed + "_" + index.ToString( "D3" ), split = split,
                taskMode = mode == 0 ? "MoveToDig" : "CarryMaterial", direction = side,
                longerPath = length != 0, startJointPose = start, goalJointPose = goal,
                targetPitchDeg = TaskPitch( mode == 0 ? "MoveToDig" : "CarryMaterial" ),
                jointSamples = JointInterpolation( start, goal, 9 )
              };
              ValidateCandidate( item );
              result[ index++ ] = item;
            }
      return result;
    }

    /// <summary>Goal-bucket scan is independent of policy success; callers must retain rejected samples.</summary>
    public static RealExecutionCase WithGoalBucket( RealExecutionCase candidate, float bucketQ )
    {
      ValidateCandidate( candidate );
      if ( !Finite( bucketQ ) || bucketQ < Minimum.z || bucketQ > Maximum.z )
        throw new ArgumentOutOfRangeException( nameof( bucketQ ) );
      RealExecutionCase changed = Copy( candidate );
      changed.goalJointPose = new Vector4( candidate.goalJointPose.x, candidate.goalJointPose.y,
        bucketQ, candidate.goalJointPose.w );
      changed.jointSamples = JointInterpolation( changed.startJointPose, changed.goalJointPose,
        candidate.jointSamples.Length );
      changed.referenceUnityLocal = new Vector3[ 0 ];
      changed.sampledPitchDeg = new float[ 0 ];
      changed.sampledClearance = new bool[ 0 ];
      changed.validationSource = "";
      changed.pathLengthM = 0f;
      return changed;
    }

    public static RealExecutionCase ResolveReference( RealExecutionCase candidate,
      Vector3[] actualTipUnityLocal, float[] actualPitchDeg, bool[] clearanceVerified, string source )
    {
      ValidateCandidate( candidate );
      if ( actualTipUnityLocal == null || actualPitchDeg == null || clearanceVerified == null
        || actualTipUnityLocal.Length != candidate.jointSamples.Length
        || actualPitchDeg.Length != actualTipUnityLocal.Length
        || clearanceVerified.Length != actualTipUnityLocal.Length || string.IsNullOrWhiteSpace( source ) )
        throw new ArgumentException( "Physical tip, pitch, clearance and source are required for every joint sample." );
      var resolved = Copy( candidate );
      resolved.referenceUnityLocal = (Vector3[])actualTipUnityLocal.Clone();
      resolved.sampledPitchDeg = (float[])actualPitchDeg.Clone();
      resolved.sampledClearance = (bool[])clearanceVerified.Clone();
      resolved.validationSource = source;
      float length = 0f;
      for ( int i = 1; i < actualTipUnityLocal.Length; i++ )
        length += Vector3.Distance( actualTipUnityLocal[ i - 1 ], actualTipUnityLocal[ i ] );
      resolved.pathLengthM = length;
      ValidateResolved( resolved );
      return resolved;
    }

    public static void ValidateResolved( RealExecutionCase candidate )
    {
      ValidateCandidate( candidate );
      int count = candidate.jointSamples.Length;
      if ( candidate.referenceUnityLocal == null || candidate.referenceUnityLocal.Length != count
        || candidate.sampledPitchDeg == null || candidate.sampledPitchDeg.Length != count
        || candidate.sampledClearance == null || candidate.sampledClearance.Length != count
        || string.IsNullOrWhiteSpace( candidate.validationSource ) )
        throw new InvalidOperationException( "Unresolved physical course candidate: " + candidate.id );
      float length = 0f;
      for ( int i = 0; i < count; i++ ) {
        if ( !Finite( candidate.referenceUnityLocal[ i ] ) || !Finite( candidate.sampledPitchDeg[ i ] )
          || !candidate.sampledClearance[ i ] )
          throw new InvalidOperationException( "Invalid or obstructed physical sample: " + candidate.id );
        if ( i > 0 ) length += Vector3.Distance( candidate.referenceUnityLocal[ i - 1 ], candidate.referenceUnityLocal[ i ] );
      }
      if ( length < 0.06f || Vector3.Distance( candidate.referenceUnityLocal[ 0 ], candidate.referenceUnityLocal[ count - 1 ] ) < 0.06f )
        throw new InvalidOperationException( "Reference must start outside the terminal tolerance: " + candidate.id );
      if ( Mathf.Abs( Mathf.DeltaAngle( candidate.sampledPitchDeg[ count - 1 ], candidate.targetPitchDeg ) ) > 20f )
        throw new InvalidOperationException( "Goal pitch cannot satisfy the unchanged task terminal: " + candidate.id );
      if ( !Finite( candidate.pathLengthM ) || Mathf.Abs( candidate.pathLengthM - length ) > 0.0001f )
        throw new InvalidOperationException( "Reference length does not match physical samples: " + candidate.id );
    }

    public static void ValidateCandidate( RealExecutionCase candidate )
    {
      if ( candidate == null || string.IsNullOrWhiteSpace( candidate.id ) ) throw new ArgumentException( "Missing case identity." );
      ValidateSplit( candidate.split );
      float target = TaskPitch( candidate.taskMode );
      if ( !Finite( target ) || !Finite( candidate.targetPitchDeg ) || Mathf.Abs( target - candidate.targetPitchDeg ) > 0.001f
        || ( candidate.direction != -1 && candidate.direction != 1 ) )
        throw new ArgumentException( "Invalid task mode, target pitch or swing direction." );
      CheckJointPose( candidate.startJointPose );
      CheckJointPose( candidate.goalJointPose );
      if ( candidate.jointSamples == null || candidate.jointSamples.Length < 2 ) throw new ArgumentException( "Joint samples are required." );
      foreach ( Vector4 q in candidate.jointSamples ) CheckJointPose( q );
      if ( Vector4.Distance( candidate.jointSamples[ 0 ], candidate.startJointPose ) > 1e-6f
        || Vector4.Distance( candidate.jointSamples[ candidate.jointSamples.Length - 1 ], candidate.goalJointPose ) > 1e-6f )
        throw new ArgumentException( "Joint sample endpoints differ from the declared case." );
    }

    private static void CheckJointPose( Vector4 q )
    {
      if ( !Finite( q ) ) throw new ArgumentException( "Nonfinite joint pose." );
      for ( int i = 0; i < 4; i++ )
        if ( q[ i ] < Minimum[ i ] - 1e-7f || q[ i ] > Maximum[ i ] + 1e-7f )
          throw new ArgumentOutOfRangeException( nameof( q ), "Candidate lies outside the margin-adjusted execution domain." );
    }

    private static Vector4[] JointInterpolation( Vector4 start, Vector4 goal, int count )
    {
      var samples = new Vector4[ count ];
      for ( int i = 0; i < count; i++ ) samples[ i ] = Vector4.Lerp( start, goal, (float)i / ( count - 1 ) );
      return samples;
    }

    private static RealExecutionCase Copy( RealExecutionCase item )
      => JsonUtility.FromJson<RealExecutionCase>( JsonUtility.ToJson( item ) );

    private static float Uniform( System.Random random, float low, float high )
      => low + ( high - low ) * (float)random.NextDouble();

    private static float[] Repeat( float value, int count )
    {
      var values = new float[ count ];
      for ( int i = 0; i < count; i++ ) values[ i ] = value;
      return values;
    }

    private static void ValidateSplit( string value )
    {
      if ( value != "train" && value != "dev" && value != "holdout" )
        throw new ArgumentException( "Expected train, dev or holdout split." );
    }

    private static float TaskPitch( string mode )
    {
      if ( mode != "MoveToDig" && mode != "CarryMaterial" ) return float.NaN;
      if ( s_tasks == null ) s_tasks = MachineProfileLoader.Load().TaskProfile;
      return mode == "MoveToDig" ? s_tasks.MoveToDigBucketPitchTargetDeg : s_tasks.CarryMaterialBucketPitchTargetDeg;
    }

    private static void CheckConstraints( Constraint[] constraints, string axis )
    {
      if ( constraints == null || constraints.Length == 0 ) throw new InvalidOperationException( "Missing " + axis + " constraints." );
      foreach ( Constraint constraint in constraints )
        if ( constraint == null ) throw new InvalidOperationException( "Missing " + axis + " constraint." );
    }

    private static bool Finite( float value ) => !float.IsNaN( value ) && !float.IsInfinity( value );
    private static bool Finite( Vector3 value ) => Finite( value.x ) && Finite( value.y ) && Finite( value.z );
    private static bool Finite( Vector4 value ) => Finite( value.x ) && Finite( value.y ) && Finite( value.z ) && Finite( value.w );
  }
}
