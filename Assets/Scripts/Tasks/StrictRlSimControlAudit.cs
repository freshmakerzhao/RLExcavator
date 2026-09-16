using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace RLExcavator
{
  /// <summary>
  /// Opt-in writer for policy commands produced during one continuous tracking segment.
  /// A suite freezes the maximum zero-based 10 Hz decision grid, not path geometry.
  /// Each segment may consume any non-empty contiguous prefix of that grid.
  /// </summary>
  internal sealed class StrictRlSimControlAudit
  {
    private const string kSchemaVersion = "rl_excavator_sim_control_audit.v1";
    private const string kMode = "control";
    private const string kBackend = "onnx_rl";
    private const string kTraceSemantics = "commanded_normalized_action";
    private static readonly string[] s_actionOrder = { "boom", "stick", "bucket", "swing" };
    private static readonly Encoding s_utf8WithoutBom = new UTF8Encoding( false );
    private static readonly Regex s_jsonFieldPattern = new Regex(
      "\\\"((?:\\\\.|[^\\\"\\\\])*)\\\"\\s*:",
      RegexOptions.CultureInvariant );

    private readonly string m_outputPath;
    private readonly string m_suiteSha256;
    private readonly int[] m_sampleIds;
    private readonly Action<string> m_warningSink;
    private readonly Queue<string> m_pendingJsonLines = new Queue<string>();
    private string m_traceRunId = "";
    private double m_segmentStartedAt = 0.0;
    private int m_nextSampleOrdinal = 0;
    private bool m_failed = false;

    private StrictRlSimControlAudit(
      string outputPath,
      StrictRlSimTrajectorySuiteSnapshot suite,
      Action<string> warningSink )
    {
      m_outputPath = outputPath;
      m_suiteSha256 = suite.sha256;
      m_sampleIds = (int[])suite.sample_ids.Clone();
      m_warningSink = warningSink;
      IsEnabled = true;
    }

    public bool IsEnabled { get; private set; }

    public static bool TryCreate(
      string outputPath,
      string suitePath,
      Action<string> warningSink,
      out StrictRlSimControlAudit audit )
    {
      audit = null;
      try {
        if ( string.IsNullOrWhiteSpace( outputPath ) )
          throw new InvalidDataException( "control audit output path is empty" );
        StrictRlSimTrajectorySuiteSnapshot suite = LoadStrictRlSimTrajectorySuiteSnapshot( suitePath );
        audit = new StrictRlSimControlAudit( outputPath, suite, warningSink );
        return true;
      }
      catch ( Exception exception ) {
        warningSink?.Invoke( "strict RL sim control audit disabled: " + exception.Message );
        return false;
      }
    }

    public void StartTrackingSegment( string traceRunId, double monotonicSeconds )
    {
      if ( !IsEnabled )
        return;
      if ( !string.IsNullOrEmpty( m_traceRunId ) ) {
        Fail( "tracking_segment_overlap", "new segment started before prior segment ended", -1, monotonicSeconds );
        return;
      }

      string normalizedId = string.IsNullOrWhiteSpace( traceRunId ) ? "" : traceRunId.Trim();
      if ( string.IsNullOrEmpty( normalizedId ) || !IsFiniteNonNegative( monotonicSeconds ) ) {
        Fail( "tracking_segment_identity_invalid", "trace_run_id or start timestamp is invalid", -1, monotonicSeconds );
        return;
      }

      m_traceRunId = normalizedId;
      m_segmentStartedAt = monotonicSeconds;
      m_nextSampleOrdinal = 0;
    }

    public void RecordPolicySample(
      Vector4 action,
      int sequence,
      double monotonicSeconds,
      StrictRlSimTrackingState tracking )
    {
      if ( !IsEnabled || string.IsNullOrEmpty( m_traceRunId ) )
        return;
      if ( !IsFiniteNormalizedAction( action ) || sequence < 0 || !IsFiniteNonNegative( monotonicSeconds ) ) {
        Fail( "policy_decision_invalid", "policy action, sequence, or timestamp is invalid", sequence, monotonicSeconds );
        return;
      }
      if ( m_nextSampleOrdinal >= m_sampleIds.Length ) {
        Fail( "trajectory_suite_sample_ids_exhausted", "policy decisions exceeded suite sample_ids", sequence, monotonicSeconds );
        return;
      }
      if ( !tracking.IsValid ) {
        Fail( "tracking_state_unavailable", "bucket tip or active waypoint state is invalid", sequence, monotonicSeconds );
        return;
      }

      int sampleId = m_sampleIds[ m_nextSampleOrdinal ];
      object record = PolicySampleRecord.Create(
        m_suiteSha256, m_traceRunId, sampleId, sequence, monotonicSeconds, action, tracking );
      if ( TryEnqueue( record ) )
        m_nextSampleOrdinal++;
    }

    public void FinishTrackingSegment( string traceRunId, string result, double monotonicSeconds )
    {
      if ( string.IsNullOrEmpty( m_traceRunId ) )
        return;

      bool identityMatches = string.Equals( m_traceRunId, traceRunId, StringComparison.Ordinal );
      string errorCode = identityMatches ? DecisionCountErrorCode() : "tracking_segment_id_mismatch";
      string normalizedResult = IsTerminalResult( result ) ? result : "rejected";
      bool errorAlreadyRecorded = m_failed;
      if ( !IsTerminalResult( result ) )
        errorCode = "terminal_result_invalid";
      if ( m_failed && string.IsNullOrEmpty( errorCode ) )
        errorCode = "audit_failed";

      if ( !errorAlreadyRecorded && !string.IsNullOrEmpty( errorCode ) )
        EnqueueAuditError( errorCode, "tracking segment terminal is not auditable", -1, monotonicSeconds );
      EnqueueTerminal( normalizedResult, monotonicSeconds, errorCode );
      if ( !string.IsNullOrEmpty( errorCode ) ) {
        m_failed = true;
        IsEnabled = false;
        Warn( "strict RL sim control audit failed: " + errorCode );
      }
      ClearSegment();
    }

    public void Flush()
    {
      if ( m_pendingJsonLines.Count == 0 )
        return;

      try {
        string directory = Path.GetDirectoryName( m_outputPath );
        if ( !string.IsNullOrEmpty( directory ) )
          Directory.CreateDirectory( directory );
        var payload = new StringBuilder();
        while ( m_pendingJsonLines.Count > 0 )
          payload.AppendLine( m_pendingJsonLines.Dequeue() );
        File.AppendAllText( m_outputPath, payload.ToString(), s_utf8WithoutBom );
      }
      catch ( Exception exception ) {
        m_pendingJsonLines.Clear();
        m_failed = true;
        IsEnabled = false;
        Warn( "strict RL sim control audit I/O failed: " + exception.Message );
      }
    }

    private void EnqueueTerminal( string result, double monotonicSeconds, string errorCode )
    {
      double safeTime = IsFiniteNonNegative( monotonicSeconds ) ? monotonicSeconds : m_segmentStartedAt;
      double elapsedSeconds = Math.Max( 0.0, safeTime - m_segmentStartedAt );
      TryEnqueue( TerminalRecord.Create(
        m_suiteSha256,
        m_traceRunId,
        safeTime,
        elapsedSeconds,
        result,
        m_nextSampleOrdinal,
        m_sampleIds.Length,
        errorCode ) );
    }

    private void Fail( string code, string message, int sequence, double monotonicSeconds )
    {
      if ( m_failed )
        return;
      m_failed = true;
      IsEnabled = false;
      double safeTime = IsFiniteNonNegative( monotonicSeconds ) ? monotonicSeconds : m_segmentStartedAt;
      EnqueueAuditError( code, message, sequence, safeTime );
      Warn( "strict RL sim control audit failed: " + code + ": " + message );
    }

    private void EnqueueAuditError( string code, string message, int sequence, double monotonicSeconds )
    {
      double safeTime = IsFiniteNonNegative( monotonicSeconds ) ? monotonicSeconds : m_segmentStartedAt;
      TryEnqueue( AuditErrorRecord.Create(
        m_suiteSha256, m_traceRunId, sequence, safeTime, code, message ) );
    }

    private bool TryEnqueue( object record )
    {
      try {
        m_pendingJsonLines.Enqueue( JsonUtility.ToJson( record ) );
        return true;
      }
      catch ( Exception exception ) {
        m_failed = true;
        IsEnabled = false;
        Warn( "strict RL sim control audit serialization failed: " + exception.Message );
        return false;
      }
    }

    private string DecisionCountErrorCode()
    {
      return m_nextSampleOrdinal == 0 ? "tracking_segment_zero_policy_samples" : "";
    }

    private void ClearSegment()
    {
      m_traceRunId = "";
      m_segmentStartedAt = 0.0;
      m_nextSampleOrdinal = 0;
    }

    private void Warn( string message )
    {
      m_warningSink?.Invoke( message );
    }

    internal static StrictRlSimTrajectorySuiteSnapshot LoadStrictRlSimTrajectorySuiteSnapshot( string path )
    {
      if ( string.IsNullOrWhiteSpace( path ) || !File.Exists( path ) )
        throw new FileNotFoundException( "trajectory suite file not found", path );

      byte[] bytes = File.ReadAllBytes( path );
      string json = new UTF8Encoding( false, true ).GetString( bytes );
      ValidateSuiteFields( json );
      StrictRlSimTrajectorySuite suite = JsonUtility.FromJson<StrictRlSimTrajectorySuite>( json );
      ValidateSuite( suite );
      return new StrictRlSimTrajectorySuiteSnapshot(
        suite.suite_id.Trim(), suite.sample_period_s, (int[])suite.sample_ids.Clone(), ComputeSha256( bytes ) );
    }

    private static void ValidateSuiteFields( string json )
    {
      string trimmed = string.IsNullOrWhiteSpace( json ) ? "" : json.Trim();
      if ( !trimmed.StartsWith( "{", StringComparison.Ordinal )
        || !trimmed.EndsWith( "}", StringComparison.Ordinal ) )
        throw new InvalidDataException( "trajectory suite must be one JSON object" );

      MatchCollection matches = s_jsonFieldPattern.Matches( trimmed );
      var fields = new HashSet<string>( StringComparer.Ordinal );
      foreach ( Match match in matches )
        fields.Add( match.Groups[ 1 ].Value );
      if ( matches.Count != 3 || fields.Count != 3
        || !fields.Contains( "suite_id" )
        || !fields.Contains( "sample_period_s" )
        || !fields.Contains( "sample_ids" ) )
        throw new InvalidDataException( "trajectory suite must contain exactly suite_id, sample_period_s, and sample_ids" );
    }

    private static void ValidateSuite( StrictRlSimTrajectorySuite suite )
    {
      if ( string.IsNullOrWhiteSpace( suite.suite_id ) || suite.sample_ids == null || suite.sample_ids.Length == 0 )
        throw new InvalidDataException( "trajectory suite must contain non-empty suite_id and sample_ids" );
      if ( double.IsNaN( suite.sample_period_s )
        || double.IsInfinity( suite.sample_period_s )
        || Math.Abs( suite.sample_period_s - 0.1 ) > 1e-12 )
        throw new InvalidDataException( "trajectory suite sample_period_s must equal 0.1" );

      for ( int index = 0; index < suite.sample_ids.Length; index++ ) {
        if ( suite.sample_ids[ index ] != index )
          throw new InvalidDataException( "trajectory suite sample_ids must be exactly 0..N-1" );
      }
    }

    private static string ComputeSha256( byte[] bytes )
    {
      using ( SHA256 sha = SHA256.Create() )
        return BitConverter.ToString( sha.ComputeHash( bytes ) ).Replace( "-", "" ).ToLowerInvariant();
    }

    private static bool IsTerminalResult( string result )
    {
      return result == "completed" || result == "timeout" || result == "interrupted" || result == "rejected";
    }

    private static bool IsFiniteNormalizedAction( Vector4 action )
    {
      return IsFiniteInRange( action.x, -1f, 1f )
        && IsFiniteInRange( action.y, -1f, 1f )
        && IsFiniteInRange( action.z, -1f, 1f )
        && IsFiniteInRange( action.w, -1f, 1f );
    }

    private static bool IsFiniteInRange( float value, float minimum, float maximum )
    {
      return !float.IsNaN( value ) && !float.IsInfinity( value ) && value >= minimum && value <= maximum;
    }

    private static bool IsFiniteNonNegative( double value )
    {
      return !double.IsNaN( value ) && !double.IsInfinity( value ) && value >= 0.0;
    }

    [Serializable]
    private struct StrictRlSimTrajectorySuite
    {
      public string suite_id;
      public double sample_period_s;
      public int[] sample_ids;
    }

    [Serializable]
    private struct PolicySampleRecord
    {
      public string schema_version;
      public string record_type;
      public string mode;
      public string status;
      public string result;
      public string trajectory_controller_backend;
      public string trajectory_suite_sha256;
      public string trace_semantics;
      public string trace_run_id;
      public int sample_id;
      public int policy_action_seq;
      public double runtime_monotonic_s;
      public string[] action_order;
      public float[] commanded_normalized_action;
      public float[] bucket_tip_ros_m;
      public float[] reference_waypoint_ros_m;
      public int waypoint_index;
      public float waypoint_distance_m;
      public float episode_progress;

      public static PolicySampleRecord Create(
        string suiteSha,
        string traceId,
        int sampleId,
        int sequence,
        double monotonicSeconds,
        Vector4 action,
        StrictRlSimTrackingState tracking )
      {
        return new PolicySampleRecord {
          schema_version = kSchemaVersion,
          record_type = "policy_sample",
          mode = kMode,
          status = "active",
          result = "active",
          trajectory_controller_backend = kBackend,
          trajectory_suite_sha256 = suiteSha,
          trace_semantics = kTraceSemantics,
          trace_run_id = traceId,
          sample_id = sampleId,
          policy_action_seq = sequence,
          runtime_monotonic_s = monotonicSeconds,
          action_order = (string[])s_actionOrder.Clone(),
          commanded_normalized_action = ToArray( action ),
          bucket_tip_ros_m = ToArray( tracking.bucketTipRos ),
          reference_waypoint_ros_m = ToArray( tracking.referenceWaypointRos ),
          waypoint_index = tracking.waypointIndex,
          waypoint_distance_m = tracking.waypointDistance,
          episode_progress = tracking.episodeProgress
        };
      }
    }

    [Serializable]
    private struct TerminalRecord
    {
      public string schema_version;
      public string record_type;
      public string mode;
      public string status;
      public string result;
      public string trajectory_controller_backend;
      public string trajectory_suite_sha256;
      public string trace_semantics;
      public string trace_run_id;
      public double runtime_monotonic_s;
      public double elapsed_s;
      public int consumed_sample_count;
      public int suite_sample_count;
      public string error_code;

      public static TerminalRecord Create(
        string suiteSha,
        string traceId,
        double monotonicSeconds,
        double elapsedSeconds,
        string result,
        int decisionCount,
        int maxDecisionCount,
        string errorCode )
      {
        return new TerminalRecord {
          schema_version = kSchemaVersion,
          record_type = "terminal",
          mode = kMode,
          status = string.IsNullOrEmpty( errorCode ) ? "terminal" : "error",
          result = result,
          trajectory_controller_backend = kBackend,
          trajectory_suite_sha256 = suiteSha,
          trace_semantics = kTraceSemantics,
          trace_run_id = traceId,
          runtime_monotonic_s = monotonicSeconds,
          elapsed_s = elapsedSeconds,
          consumed_sample_count = decisionCount,
          suite_sample_count = maxDecisionCount,
          error_code = errorCode ?? ""
        };
      }
    }

    [Serializable]
    private struct AuditErrorRecord
    {
      public string schema_version;
      public string record_type;
      public string mode;
      public string status;
      public string result;
      public string trajectory_controller_backend;
      public string trajectory_suite_sha256;
      public string trace_semantics;
      public string trace_run_id;
      public int policy_action_seq;
      public double runtime_monotonic_s;
      public string error_code;
      public string error_message;

      public static AuditErrorRecord Create(
        string suiteSha,
        string traceId,
        int sequence,
        double monotonicSeconds,
        string code,
        string message )
      {
        return new AuditErrorRecord {
          schema_version = kSchemaVersion,
          record_type = "audit_error",
          mode = kMode,
          status = "error",
          result = "rejected",
          trajectory_controller_backend = kBackend,
          trajectory_suite_sha256 = suiteSha,
          trace_semantics = kTraceSemantics,
          trace_run_id = traceId ?? "",
          policy_action_seq = sequence,
          runtime_monotonic_s = monotonicSeconds,
          error_code = code,
          error_message = message
        };
      }
    }

    private static float[] ToArray( Vector4 value )
    {
      return new[] { value.x, value.y, value.z, value.w };
    }

    private static float[] ToArray( Vector3 value )
    {
      return new[] { value.x, value.y, value.z };
    }
  }

  internal readonly struct StrictRlSimTrajectorySuiteSnapshot
  {
    public readonly string suite_id;
    public readonly double sample_period_s;
    public readonly int[] sample_ids;
    public readonly string sha256;

    public StrictRlSimTrajectorySuiteSnapshot(
      string suiteId,
      double samplePeriodSeconds,
      int[] sampleIds,
      string hash )
    {
      suite_id = suiteId;
      sample_period_s = samplePeriodSeconds;
      sample_ids = sampleIds;
      sha256 = hash;
    }
  }

  internal readonly struct StrictRlSimTrackingState
  {
    public readonly Vector3 bucketTipRos;
    public readonly Vector3 referenceWaypointRos;
    public readonly int waypointIndex;
    public readonly float waypointDistance;
    public readonly float episodeProgress;

    public StrictRlSimTrackingState(
      Vector3 bucketTip,
      Vector3 referenceWaypoint,
      int index,
      float distance,
      float progress )
    {
      bucketTipRos = bucketTip;
      referenceWaypointRos = referenceWaypoint;
      waypointIndex = index;
      waypointDistance = distance;
      episodeProgress = progress;
    }

    public bool IsValid => IsFinite( bucketTipRos )
      && IsFinite( referenceWaypointRos )
      && waypointIndex >= 0
      && IsFiniteInRange( waypointDistance, 0f, float.MaxValue )
      && IsFiniteInRange( episodeProgress, 0f, 1f );

    private static bool IsFinite( Vector3 value )
    {
      return IsFiniteInRange( value.x, -float.MaxValue, float.MaxValue )
        && IsFiniteInRange( value.y, -float.MaxValue, float.MaxValue )
        && IsFiniteInRange( value.z, -float.MaxValue, float.MaxValue );
    }

    private static bool IsFiniteInRange( float value, float minimum, float maximum )
    {
      return !float.IsNaN( value ) && !float.IsInfinity( value ) && value >= minimum && value <= maximum;
    }
  }
}
