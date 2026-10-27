using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.MLAgents;
using UnityEngine;

namespace RLExcavator
{
  /// <summary>仅固定用例验收场景挂载；记录物理采样，不改变47维或策略动作。</summary>
  [DisallowMultipleComponent]
  public sealed class ContinuousPathBenchmark : MonoBehaviour
  {
    private JArray m_cases;
    private JObject m_case;
    private StreamWriter m_writer;
    private int m_index;
    private float m_startedAt;
    private bool m_recording;

    public void Prepare( ExcavatorTrajectoryAgent agent )
    {
      if ( m_cases == null ) {
        string suite = Argument( "--continuous-path-benchmark" );
        string output = Argument( "--benchmark-output" );
        if ( suite == null || output == null )
          throw new InvalidOperationException( "Fixed benchmark requires suite and output paths." );
        m_cases = (JArray)JObject.Parse( File.ReadAllText( suite ) )[ "cases" ];
        m_writer = new StreamWriter( output, false ) { AutoFlush = true };
      }
      m_recording = false;
      m_index = (int)Academy.Instance.EnvironmentParameters.GetWithDefault( "benchmark_case", 0f );
      m_case = (JObject)m_cases[ m_index ];
      var course = GetComponent<ContinuousPathMissionCourse>();
      float[] joints = m_case[ "start_joint_pose" ].ToObject<float[]>();
      course.startJointPose = new Vector4( joints[ 0 ], joints[ 1 ], joints[ 2 ], joints[ 3 ] );
      course.routeRosM = m_case[ "route_ros_m" ].Select( p => new Vector3(
        (float)p[ 0 ], (float)p[ 1 ], (float)p[ 2 ] ) ).ToArray();
      course.jointJitterM = course.swingJitterRad = course.routeJitterM = 0f;
      course.fullPathProbability = 1f;
      course.enabled = true;
      agent.randomizeTaskModeOnEpisodeBegin = false;
      agent.taskMode = (ExcavatorTrajectoryAgent.TrajectoryTaskMode)Enum.Parse(
        typeof( ExcavatorTrajectoryAgent.TrajectoryTaskMode ), (string)m_case[ "task_mode" ] );
      float pitch = (float)m_case[ "target_pitch_deg" ];
      if ( agent.taskMode == ExcavatorTrajectoryAgent.TrajectoryTaskMode.CarryMaterial )
        agent.carryMaterialTargetBucketPitchDeg = pitch;
      else
        agent.moveToDigTargetBucketPitchDeg = pitch;
    }

    public void RecordStart( ExcavatorTrajectoryAgent agent, Vector3[] path )
    {
      m_startedAt = Time.time;
      m_recording = true;
      var c = agent.controller;
      Write( new JObject {
        [ "kind" ] = "start", [ "elapsed_s" ] = 0f,
        [ "actual_joint_pose" ] = new JArray( c.BoomPosition, c.StickPosition, c.BucketPosition, c.SwingPosition ),
        [ "tip_ros_m" ] = Ros( path[ 0 ] ),
        [ "route_ros_m" ] = new JArray( path.Select( p => Ros( p ) ) ),
        [ "pitch_deg" ] = agent.CurrentBucketPitchDeg,
        [ "task_mode" ] = agent.taskMode.ToString(),
        [ "target_pitch_deg" ] = agent.taskMode == ExcavatorTrajectoryAgent.TrajectoryTaskMode.CarryMaterial
          ? agent.carryMaterialTargetBucketPitchDeg : agent.moveToDigTargetBucketPitchDeg
      } );
    }

    public void RecordSample( ExcavatorTrajectoryAgent agent, ContinuousPathTracking tracking, Vector3 tip )
    {
      if ( !m_recording ) return;
      Write( new JObject {
        [ "kind" ] = "sample", [ "elapsed_s" ] = Time.time - m_startedAt,
        [ "time_s" ] = Time.time, [ "tip_ros_m" ] = Ros( tip ),
        [ "pitch_deg" ] = agent.CurrentBucketPitchDeg, [ "progress" ] = tracking.Progress
      } );
    }

    public void RecordEnd( bool success, string reason )
    {
      if ( !m_recording ) return;
      Write( new JObject { [ "kind" ] = "end", [ "elapsed_s" ] = Time.time - m_startedAt,
        [ "success" ] = success, [ "reason" ] = reason } );
      m_recording = false;
    }

    private void Write( JObject row )
    {
      row[ "case_index" ] = m_index;
      row[ "case_id" ] = (string)m_case[ "id" ];
      m_writer.WriteLine( row.ToString( Formatting.None ) );
    }

    private static JArray Ros( Vector3 p ) => new JArray( p.z, -p.x, p.y );

    private static string Argument( string name )
    {
      string[] args = Environment.GetCommandLineArgs();
      int index = Array.IndexOf( args, name );
      return index >= 0 && index + 1 < args.Length ? args[ index + 1 ] : null;
    }

    private void OnDestroy() => m_writer?.Dispose();
  }
}
