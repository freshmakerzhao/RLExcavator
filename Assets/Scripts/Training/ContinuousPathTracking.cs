using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;

namespace RLExcavator
{
  /// <summary>
  /// 独立候选实验：按弧长跟随整条路径，策略学习速度，不逐点停车。
  /// 仅新训练场景挂载；旧 38 维策略和奖励完全不使用本组件。
  /// 路径点、铲尖位置和速度均使用 MachineRoot 局部米制坐标。
  /// </summary>
  [DisallowMultipleComponent]
  public sealed class ContinuousPathTracking : MonoBehaviour
  {
    public const int ObservationSize = 47;
    public const int PathObservationSize = 21;

    [Header("Optional smooth-reference training (isolated curve scene only)")]
    [Range(0f, 1f)] public float smoothTrainingReferenceProbability = 0f;
    [Min(0f)] public float smoothCornerCutDistanceM = 0.08f;
    [Min(0.001f)] public float smoothSampleSpacingM = 0.01f;

    [Header("Training reference generation (episode reset only)")]
    [Tooltip("仅训练场景启用：使用采样终点，从复位后的真实铲尖生成弱弯参考。关闭时保持提供的路径。")]
    public bool generateTrainingReference = false;
    [Tooltip("长路径课程保留工作空间弧形；仅将采样起点偏差逐渐衰减到零。旧短路径课程保持关闭。")]
    public bool preserveTrainingPathShape = false;
    [Tooltip("仅新课程开启：复位等待不计入执行时间，与 Runtime 的技能时钟一致。旧候选保持原计时。") ]
    public bool resetClockAfterHoming = false;

    [Header("Path observation")]
    [Min(0.001f)] public float corridorRadiusM = 0.08f;
    public Vector3 previewDistancesM = new Vector3( 0.03f, 0.08f, 0.15f );

    [Header("Terminal skill handoff")]
    [Min(0.001f)] public float endpointToleranceM = 0.03f;
    [Min(0f)] public float terminalSpeedMps = 0.06f;
    [Min(0f)] public float terminalPitchToleranceDeg = 20f;
    [Min(0f)] public float terminalHoldSeconds = 0.2f;

    [Header("Reward: rates integrate simulation seconds")]
    [Min(0f)] public float progressWeight = 10f;
    [Min(0f)] public float contourCostPerSecond = 0.5f;
    [Tooltip("仅新训练课程开启：横偏代价使用几何最近点，有序投影仍负责进度和观测。")]
    public bool useGeometricContourCost = false;
    [Min(0f)] public float cornerContourGain = 0f;
    [Min(0.001f)] public float cornerInfluenceDistanceM = 0.2f;
    [Tooltip("角度改善奖励有正有负；与路径推进一样按差值计算，不乘时间步长。")]
    [Min(0f)] public float pitchProgressWeight = 10f;
    [Min(0f)] public float pitchCostPerSecond = 3f;
    [Min(0f)] public float actionChangeCostPerDecision = 0.05f;
    [Min(0f)] public float energyCostPerSecond = 0.03f;
    [Min(0f)] public float timeCostPerSecond = 0.1f;
    [Min(0f)] public float terminalSpeedCostPerSecond = 1f;
    [Min(0.001f)] public float terminalApproachDistanceM = 0.2f;
    [Min(0f)] public float completionBonus = 10f;

    private ContinuousReferencePath m_path;
    private ContinuousReferencePath.Projection m_projection;
    private Vector3 m_previousTip;
    private Vector4 m_previousDecisionAction;
    private float m_elapsedSeconds;
    private float m_settledSeconds;
    private float m_crossTrackIntegral;
    private float m_geometricCrossTrackIntegral;
    private float m_projectionArcGapIntegral;
    private float m_speedIntegral;
    private float m_pitchErrorIntegral;
    private float m_actionDeltaSum;
    private int m_decisionCount;
    private bool m_hasPitchSample;

    public bool Ready => m_path != null;
    public bool Completed { get; private set; }
    public float Progress => Ready && m_path.Length > 1e-6f ? m_projection.ArcLength / m_path.Length : 0f;
    public float RemainingDistanceM => Ready ? Mathf.Max( m_path.Length - m_projection.ArcLength, 0f ) : 0f;
    public float EndpointDistanceM { get; private set; }
    public float TipSpeedMps { get; private set; }
    public float CrossTrackDistanceM => Ready ? m_projection.Distance : 0f;
    public float MaxCrossTrackDistanceM { get; private set; }
    public float GeometricCrossTrackDistanceM { get; private set; }
    public float GeometricMaxCrossTrackDistanceM { get; private set; }
    public float GeometricMeanCrossTrackDistanceM => m_geometricCrossTrackIntegral / Mathf.Max( m_elapsedSeconds, 1e-6f );
    public float LastContourPenalty { get; private set; }
    public float LastPitchErrorDeg { get; private set; }
    public float LastProgressReward { get; private set; }
    public float LastPitchProgressReward { get; private set; }
    public float LastSmoothPenalty { get; private set; }

    public void InvalidatePath()
    {
      m_path = null;
    }

    /// <summary>
    /// 训练数据生成器，不是执行端路径平滑器。五点等采样的初级参考沿物理弦线单调推进，
    /// 避免可达空间参数投影的起点与实际铲尖不一致时，产生额外接入段及近180度折返。
    /// 只采用外部采样终点；垂直弦的弱弯幅度最多为弦长的10%。
    /// </summary>
    public static Vector3[] CreateTrainingReferencePath(
      Vector3 start, Vector3 end, int pointCount, Vector3 bendDirection, float bendFraction )
    {
      int count = Mathf.Max( pointCount, 2 );
      Vector3 chord = end - start;
      Vector3 offset = Vector3.ProjectOnPlane( bendDirection, chord ).normalized
        * chord.magnitude * Mathf.Clamp( bendFraction, 0f, 0.1f );
      var points = new Vector3[ count ];
      for ( int i = 0; i < count; i++ ) {
        float t = (float)i / ( count - 1 );
        points[ i ] = Vector3.Lerp( start, end, t ) + offset * ( 4f * t * ( 1f - t ) );
      }
      return points;
    }

    /// <summary>保留工作空间路线，真实起点偏差沿路线衰减，避免插入反向接入段或将大回转压成弦线。</summary>
    public static Vector3[] AlignTrainingReferenceStart( Vector3[] reference, Vector3 actualStart )
    {
      if ( reference == null || reference.Length < 2 )
        throw new System.ArgumentException( "A training reference needs at least two points.", nameof( reference ) );
      var aligned = new Vector3[ reference.Length ];
      Vector3 offset = actualStart - reference[ 0 ];
      for ( int i = 0; i < reference.Length; i++ ) {
        float fraction = (float)i / ( reference.Length - 1 );
        aligned[ i ] = reference[ i ] + offset * ( 1f - fraction );
      }
      return aligned;
    }

    public void ResetPath( Vector3[] points )
    {
      m_path = new ContinuousReferencePath( points );
      m_previousTip = m_path.StartPoint;
      m_projection = m_path.Project( m_previousTip, 0f, 0f );
      m_previousDecisionAction = Vector4.zero;
      m_elapsedSeconds = m_settledSeconds = 0f;
      m_crossTrackIntegral = m_speedIntegral = m_pitchErrorIntegral = 0f;
      m_geometricCrossTrackIntegral = m_projectionArcGapIntegral = 0f;
      GeometricCrossTrackDistanceM = GeometricMaxCrossTrackDistanceM = LastContourPenalty = 0f;
      m_actionDeltaSum = 0f;
      m_decisionCount = 0;
      m_hasPitchSample = false;
      Completed = false;
      EndpointDistanceM = Vector3.Distance( m_previousTip, m_path.EndPoint );
      TipSpeedMps = MaxCrossTrackDistanceM = LastPitchErrorDeg = 0f;
      LastProgressReward = LastSmoothPenalty = 0f;
      LastPitchProgressReward = 0f;
    }

    /// <summary>
    /// 21 维：投影误差3、切向3、三处弧长前瞻相对位置9、进度1、剩余距离1、
    /// 终点相对位置3、走廊半径1。前瞻点描述路径形状，不是待逐个完成的目标。
    /// </summary>
    public void AddPathObservations( VectorSensor sensor, Vector3 tip, float normalizer )
    {
      if ( !Ready ) {
        for ( int i = 0; i < PathObservationSize; i++ )
          sensor.AddObservation( 0f );
        return;
      }
      float scale = Mathf.Max( normalizer, 0.01f );
      // 在动作前观察当前物理状态，但只在 EvaluateStep 提交投影，避免观测吞掉推进奖励。
      var projection = ProjectionAt( tip );
      AddVector( sensor, ( projection.Point - tip ) / scale );
      AddVector( sensor, projection.Tangent );
      for ( int i = 0; i < 3; i++ ) {
        Vector3 preview = m_path.PointAt( projection.ArcLength + previewDistancesM[ i ] );
        AddVector( sensor, ( preview - tip ) / scale );
      }
      sensor.AddObservation( m_path.Length > 1e-6f ? projection.ArcLength / m_path.Length : 0f );
      sensor.AddObservation( Mathf.Clamp( ( m_path.Length - projection.ArcLength ) / scale, 0f, 5f ) );
      AddVector( sensor, ( m_path.EndPoint - tip ) / scale );
      sensor.AddObservation( corridorRadiusM / scale );
    }

    public float EvaluateStep( Vector3 tip, float pitchErrorDeg, Vector4 action,
                               float dt, bool isNewPolicyDecision )
    {
      if ( !Ready || Completed )
        return 0f;
      float elapsed = Mathf.Max( dt, 1e-6f );
      float movement = Vector3.Distance( tip, m_previousTip );
      float previousArc = m_projection.ArcLength;
      // 局部有序投影。没有固定前进时钟；零运动时搜索窗口也为零，不能站着刷进度。
      m_projection = ProjectionAt( tip );
      TipSpeedMps = movement / elapsed;
      EndpointDistanceM = Vector3.Distance( tip, m_path.EndPoint );
      float pitchError = Mathf.Abs( pitchErrorDeg );
      // 第一帧只建立基准。变差时扣回改善奖励，避免靠来回转斗反复得分。
      LastPitchProgressReward = m_hasPitchSample
        ? pitchProgressWeight * ( LastPitchErrorDeg - pitchError ) / 180f : 0f;
      LastPitchErrorDeg = pitchError;
      m_hasPitchSample = true;
      float arcDelta = m_projection.ArcLength - previousArc;
      float creditedDelta = CrossTrackDistanceM <= corridorRadiusM ? arcDelta : Mathf.Min( arcDelta, 0f );
      LastProgressReward = progressWeight * creditedDelta / Mathf.Max( m_path.Length, 0.01f );
      LastSmoothPenalty = DecisionSmoothPenalty( action, isNewPolicyDecision );

      // 几何距离单独统计，避免把拐角处的有序投影滞后误报为横向偏差。
      var geometric = m_path.Project( tip, m_projection.ArcLength, m_path.Length );
      GeometricCrossTrackDistanceM = geometric.Distance;
      m_projectionArcGapIntegral += Mathf.Abs( geometric.ArcLength - m_projection.ArcLength ) * elapsed;
      float contourDistance = useGeometricContourCost ? geometric.Distance : CrossTrackDistanceM;
      float cornerWeight = useGeometricContourCost
        ? 1f + cornerContourGain * m_path.CornerImportance( geometric.ArcLength, cornerInfluenceDistanceM ) : 1f;
      float contourRatio = Mathf.Min( contourDistance / Mathf.Max( corridorRadiusM, 0.001f ), 4f );
      LastContourPenalty = -elapsed * contourCostPerSecond * cornerWeight * contourRatio * contourRatio;
      float terminalBlend = Mathf.Clamp01( 1f - EndpointDistanceM / Mathf.Max( terminalApproachDistanceM, 0.001f ) );
      float speedCost = terminalSpeedCostPerSecond * terminalBlend * Mathf.Pow( TipSpeedMps / 0.5f, 2f );
      float reward = LastProgressReward + LastPitchProgressReward + LastSmoothPenalty + LastContourPenalty - elapsed * (
        pitchCostPerSecond * LastPitchErrorDeg / 180f
        + energyCostPerSecond * action.sqrMagnitude / 4f
        + timeCostPerSecond + speedCost );

      bool settled = RemainingDistanceM <= endpointToleranceM
        && EndpointDistanceM <= endpointToleranceM
        && TipSpeedMps <= terminalSpeedMps
        && LastPitchErrorDeg <= terminalPitchToleranceDeg;
      m_settledSeconds = settled ? m_settledSeconds + elapsed : 0f;
      Completed = settled && m_settledSeconds >= terminalHoldSeconds;
      AccumulateMeasurements( elapsed );
      m_previousTip = tip;
      return reward + ( Completed ? completionBonus : 0f );
    }

    private ContinuousReferencePath.Projection ProjectionAt( Vector3 tip )
    {
      return m_path.Project( tip, m_projection.ArcLength, Vector3.Distance( tip, m_previousTip ) * 1.5f );
    }

    private float DecisionSmoothPenalty( Vector4 action, bool isNewPolicyDecision )
    {
      if ( !isNewPolicyDecision )
        return 0f;
      float delta = ( action - m_previousDecisionAction ).sqrMagnitude / 4f;
      m_previousDecisionAction = action;
      m_actionDeltaSum += delta;
      m_decisionCount++;
      return -actionChangeCostPerDecision * delta;
    }

    private void AccumulateMeasurements( float dt )
    {
      m_elapsedSeconds += dt;
      m_crossTrackIntegral += CrossTrackDistanceM * dt;
      m_geometricCrossTrackIntegral += GeometricCrossTrackDistanceM * dt;
      GeometricMaxCrossTrackDistanceM = Mathf.Max( GeometricMaxCrossTrackDistanceM, GeometricCrossTrackDistanceM );
      m_speedIntegral += TipSpeedMps * dt;
      m_pitchErrorIntegral += LastPitchErrorDeg * dt;
      MaxCrossTrackDistanceM = Mathf.Max( MaxCrossTrackDistanceM, CrossTrackDistanceM );
    }

    public void RecordEpisodeStats( bool completed, string taskMode )
    {
      if ( !Ready )
        return;
      var stats = Academy.Instance.StatsRecorder;
      float duration = Mathf.Max( m_elapsedSeconds, 1e-6f );
      stats.Add( "ContinuousPath/SuccessRate", completed ? 1f : 0f, StatAggregationMethod.Average );
      stats.Add( "ContinuousPath/Progress", Progress, StatAggregationMethod.Average );
      stats.Add( "ContinuousPath/PathLengthM", m_path.Length, StatAggregationMethod.Average );
      stats.Add( "ContinuousPath/DurationS", m_elapsedSeconds, StatAggregationMethod.Average );
      stats.Add( "ContinuousPath/MeanCrossTrackM", m_crossTrackIntegral / duration, StatAggregationMethod.Average );
      stats.Add( "ContinuousPath/MaxCrossTrackM", MaxCrossTrackDistanceM, StatAggregationMethod.Average );
      stats.Add( "ContinuousPath/GeometricMeanCrossTrackM", GeometricMeanCrossTrackDistanceM, StatAggregationMethod.Average );
      stats.Add( "ContinuousPath/GeometricMaxCrossTrackM", GeometricMaxCrossTrackDistanceM, StatAggregationMethod.Average );
      stats.Add( "ContinuousPath/MeanProjectionArcGapM", m_projectionArcGapIntegral / duration, StatAggregationMethod.Average );
      stats.Add( "ContinuousPath/MeanTipSpeedMps", m_speedIntegral / duration, StatAggregationMethod.Average );
      stats.Add( "ContinuousPath/MeanActionDeltaSquared", m_actionDeltaSum / Mathf.Max( m_decisionCount, 1 ), StatAggregationMethod.Average );
      stats.Add( "ContinuousPath/MeanPitchErrorDeg", m_pitchErrorIntegral / duration, StatAggregationMethod.Average );
      stats.Add( "ContinuousPath/TerminalErrorM", EndpointDistanceM, StatAggregationMethod.Average );
      stats.Add( "ContinuousPath/TerminalSpeedMps", TipSpeedMps, StatAggregationMethod.Average );
      stats.Add( "ContinuousPath/TerminalPitchErrorDeg", LastPitchErrorDeg, StatAggregationMethod.Average );
      stats.Add( $"ContinuousPath/{taskMode}/SuccessRate", completed ? 1f : 0f, StatAggregationMethod.Average );
      stats.Add( $"ContinuousPath/{taskMode}/TerminalErrorM", EndpointDistanceM, StatAggregationMethod.Average );
      stats.Add( $"ContinuousPath/{taskMode}/TerminalPitchErrorDeg", LastPitchErrorDeg, StatAggregationMethod.Average );
      stats.Add( $"ContinuousPath/{taskMode}/MeanCrossTrackM", m_crossTrackIntegral / duration, StatAggregationMethod.Average );
      stats.Add( $"ContinuousPath/{taskMode}/GeometricMeanCrossTrackM", GeometricMeanCrossTrackDistanceM, StatAggregationMethod.Average );
      stats.Add( $"ContinuousPath/{taskMode}/GeometricMaxCrossTrackM", GeometricMaxCrossTrackDistanceM, StatAggregationMethod.Average );
    }

    private static void AddVector( VectorSensor sensor, Vector3 vector )
    {
      sensor.AddObservation( Mathf.Clamp( vector.x, -5f, 5f ) );
      sensor.AddObservation( Mathf.Clamp( vector.y, -5f, 5f ) );
      sensor.AddObservation( Mathf.Clamp( vector.z, -5f, 5f ) );
    }
  }
}
