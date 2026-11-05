using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace RLExcavator
{
  /// <summary>
  /// Offline AGX pose witnesses for the isolated execution-domain experiment.
  /// Uses physical home locks, never teleports rigid bodies or evaluates a policy.
  /// Clearance means sampled bucket-tip height only, not full collision certification.
  /// </summary>
  [DisallowMultipleComponent]
  public sealed class RealExecutionPhysicsSampler : MonoBehaviour
  {
    public bool quitWhenFinished = true;
    private const int MinimumSteps = 30;
    private const int MaximumSteps = 300;
    private const int StableSteps = 10;
    private const string Scope = "AGX home-lock witnesses; sampled_tip_clearance_only; no policy-success selection";
    private ExcavatorTrajectoryAgent m_agent;
    private ExcavatorController m_controller;
    private StreamWriter m_writer;
    private string m_output;
    private PoseSample m_sample;
    private int m_attempts;
    private int m_rejected;
    private int m_poseSamples;
    private bool m_started;
    private bool m_finished;
    private bool m_gridMode;
    private readonly JArray m_gridSamples = new JArray();
    private readonly List<RealExecutionCase> m_resolved = new List<RealExecutionCase>();

    private sealed class PoseSample
    {
      public Vector4 requested;
      public Vector4 actual;
      public Vector4 velocity;
      public Vector3 tip;
      public float pitch;
      public int steps;
      public bool valid;
      public bool settled;
    }

    public void Begin( ExcavatorTrajectoryAgent agent, string outputPath )
    {
      if ( m_started ) throw new InvalidOperationException( "Sampler already started." );
      if ( agent == null || agent.controller == null || agent.bucketTip == null
        || agent.frameRegistry == null || agent.frameRegistry.MachineRoot == null )
        throw new ArgumentException( "Sampler requires controller, tip and MachineRoot references." );
      if ( string.IsNullOrWhiteSpace( outputPath ) ) throw new ArgumentException( "Sampler output is required." );
      m_started = true;
      m_gridMode = System.Array.IndexOf(Environment.GetCommandLineArgs(), "--real-execution-grid") >= 0;
      m_agent = agent;
      m_controller = agent.controller;
      m_output = Path.GetFullPath( outputPath );
      Directory.CreateDirectory( m_output );
      m_writer = new StreamWriter( new FileStream( Path.Combine(m_output, "samples.jsonl"),
        FileMode.CreateNew, FileAccess.Write ) ) { AutoFlush = true };
      agent.enabled = false;
      var requester = agent.GetComponent<Unity.MLAgents.DecisionRequester>();
      if ( requester != null ) requester.enabled = false;
      m_controller.commandInputEnabled = false;
      m_controller.CancelHomePoseReset();
      m_controller.SetZeroSpeedPositionHoldEnabled( false );
      Write( new JObject { ["kind"] = "begin", ["scope"] = Scope,
        ["diagnostic_grid_only"] = m_gridMode,
        ["fixed_delta_time_s"] = Time.fixedDeltaTime, ["minimum_steps"] = MinimumSteps,
        ["maximum_steps"] = MaximumSteps, ["stable_steps"] = StableSteps } );
      StartCoroutine( Guarded( Run() ) );
    }

    private IEnumerator Guarded( IEnumerator routine )
    {
      var stack = new Stack<IEnumerator>();
      stack.Push( routine );
      while ( stack.Count > 0 ) {
        object next = null;
        bool moved = false;
        Exception failure = null;
        try {
          moved = stack.Peek().MoveNext();
          if ( moved ) next = stack.Peek().Current;
        }
        catch ( Exception error ) { failure = error; }
        if ( failure != null ) {
          Write( new JObject { ["kind"] = "fatal", ["error"] = failure.ToString() } );
          Finish( false, "exception" );
          yield break;
        }
        if ( !moved ) { stack.Pop(); continue; }
        if ( next is IEnumerator nested ) stack.Push( nested );
        else yield return next;
      }
    }

    private IEnumerator Run()
    {
      for ( int i = 0; i < 10; ++i ) yield return new WaitForFixedUpdate();
      WriteFrameSnapshot();
      if ( m_gridMode ) {
        yield return RunGrid();
        yield break;
      }
      foreach ( string split in new[] { "train", "dev", "holdout" } ) {
        int seed = split == "train" ? 20261115 : split == "dev" ? 20261116 : 20261117;
        RealExecutionCase[] candidates = RealExecutionCourse.GenerateCandidates(
          split, seed, split == "train" ? 4 : 2 );
        foreach ( RealExecutionCase candidate in candidates ) {
          m_attempts++;
          m_agent.taskMode = (ExcavatorTrajectoryAgent.TrajectoryTaskMode)Enum.Parse(
            typeof( ExcavatorTrajectoryAgent.TrajectoryTaskMode ), candidate.taskMode );
          RealExecutionCase selected = null;
          foreach ( float bucket in BucketScanTargets( candidate.goalJointPose.z ) ) {
            RealExecutionCase revised = RealExecutionCourse.WithGoalBucket( candidate, bucket );
            yield return SamplePose( revised.goalJointPose, revised.id, "goal_scan", -1 );
            if ( m_sample.valid && Mathf.Abs(Mathf.DeltaAngle(candidate.targetPitchDeg, m_sample.pitch)) <= 20f ) {
              selected = revised;
              break;
            }
          }
          if ( selected == null ) {
            Reject( candidate, "no_sampled_goal_meets_pitch_and_tip_clearance" );
            continue;
          }
          var tips = new Vector3[selected.jointSamples.Length];
          var pitches = new float[selected.jointSamples.Length];
          var clearance = new bool[selected.jointSamples.Length];
          bool valid = true;
          for ( int i = 0; i < selected.jointSamples.Length; ++i ) {
            yield return SamplePose( selected.jointSamples[i], selected.id, "route_node", i );
            tips[i] = m_sample.tip;
            pitches[i] = m_sample.pitch;
            clearance[i] = m_sample.valid;
            if ( !m_sample.valid ) { valid = false; break; }
          }
          if ( !valid ) { Reject(selected, "route_node_failed_pose_settling_or_tip_clearance"); continue; }
          RealExecutionCase resolved = null;
          string invalidReason = null;
          try {
            resolved = RealExecutionCourse.ResolveReference( selected, tips, pitches, clearance,
              "AGX physical home lock; sampled_tip_clearance_only: MachineRoot y > 0.05 m; "
              + "q error <= 0.002 m / 0.01 rad; settled >= 0.2 s; no full-body collision certification" );
          }
          catch ( ArgumentException error ) { invalidReason = error.Message; }
          catch ( InvalidOperationException error ) { invalidReason = error.Message; }
          if ( resolved == null ) { Reject(selected, "reference_validation: " + invalidReason); continue; }
          m_resolved.Add( resolved );
          Write( new JObject { ["kind"] = "accepted", ["case"] = JObject.Parse(JsonUtility.ToJson(resolved)) } );
        }
      }
      Finish( m_resolved.Count == 64, m_resolved.Count == 64 ? "complete" : "incomplete_feasible_coverage" );
    }

    /// <summary>Policy-independent diagnostic coverage, including samples below the declared tip plane.</summary>
    public static Vector4[] DiagnosticGrid()
    {
      var poses = new List<Vector4>();
      foreach ( float boom in new[] { -.07f, -.04f, -.01f, .01f, .025f } )
        foreach ( float stick in new[] { -.085f, -.06f, -.04f, -.022f } )
          foreach ( float bucket in new[] { .0013f, .0114f, .0215f, .04165f, .0618f, .082f } )
            poses.Add(new Vector4(boom, stick, bucket, 0f));
      return poses.ToArray();
    }

    private IEnumerator RunGrid()
    {
      Vector4[] poses = DiagnosticGrid();
      int unsettled = 0;
      int nonfiniteMeasurements = 0;
      for ( int i = 0; i < poses.Length; ++i ) {
        m_attempts++;
        yield return SamplePose(poses[i], "grid_" + i.ToString("D3"), "diagnostic_grid", i);
        if ( !m_sample.settled ) unsettled++;
        bool finiteMeasurement = FiniteTipAndPitch(m_sample.tip, m_sample.pitch);
        if ( !finiteMeasurement ) nonfiniteMeasurements++;
        m_gridSamples.Add(new JObject { ["index"] = i, ["requested_q"] = Array(poses[i]),
          ["actual_q"] = Array(m_sample.actual), ["tip_unity_local_m"] = new JArray(m_sample.tip.x, m_sample.tip.y, m_sample.tip.z),
          ["pitch_deg"] = m_sample.pitch, ["settled"] = m_sample.settled,
          ["finite_tip_pitch"] = finiteMeasurement,
          ["above_tip_plane"] = TipAbovePlane(m_sample.tip), ["qualified"] = m_sample.valid });
      }
      bool complete = unsettled == 0 && nonfiniteMeasurements == 0;
      string reason = nonfiniteMeasurements > 0 ? "diagnostic_grid_nonfinite_measurements"
        : unsettled > 0 ? "diagnostic_grid_unsettled_samples" : "diagnostic_grid_complete";
      Finish(complete, reason);
    }

    private void WriteFrameSnapshot()
    {
      Transform frame = m_agent.frameRegistry.MachineRoot;
      var parents = new JArray();
      for ( Transform item = frame; item != null; item = item.parent )
        parents.Add(new JObject { ["name"] = item.name, ["world_position"] = V3(item.position),
          ["local_position"] = V3(item.localPosition), ["world_up"] = V3(item.up),
          ["world_forward"] = V3(item.forward), ["lossy_scale"] = V3(item.lossyScale) });
      var bounds = new JArray();
      Transform model = m_agent.frameRegistry.ModelRoot;
      if ( model != null )
        foreach ( Renderer renderer in model.GetComponentsInChildren<Renderer>(true) )
          bounds.Add(new JObject { ["name"] = renderer.name,
            ["world_min"] = V3(renderer.bounds.min), ["world_max"] = V3(renderer.bounds.max) });
      Write(new JObject { ["kind"] = "coordinate_reference", ["frame_ancestors"] = parents,
        ["model_renderer_bounds"] = bounds,
        ["tip_plane_definition"] = "MachineRoot-local y > 0.05 m; support plane coincidence must be checked from frame/model evidence" });
    }

    private IEnumerator SamplePose( Vector4 q, string caseId, string phase, int index )
    {
      m_controller.StopAll();
      m_controller.boomHomePose = Repeat(q.x, m_controller.boom.Length);
      m_controller.stickHomePose = Repeat(q.y, m_controller.stick.Length);
      m_controller.bucketHomePose = Repeat(q.z, m_controller.bucket.Length);
      m_controller.swingHomePose = q.w;
      m_controller.useInspectorHomePose = true;
      m_controller.SetHomeLock( true );
      Vector4 previous = ReadPose();
      int stable = 0;
      PoseSample sample = null;
      for ( int step = 1; step <= MaximumSteps; ++step ) {
        yield return new WaitForFixedUpdate();
        Vector4 actual = ReadPose();
        Vector4 velocity = (actual - previous) / Time.fixedDeltaTime;
        previous = actual;
        Vector3 tip = m_agent.frameRegistry.MachineRoot.InverseTransformPoint(m_agent.bucketTip.position);
        bool settled = PoseSettled(q, actual, velocity);
        stable = settled ? stable + 1 : 0;
        sample = new PoseSample { requested = q, actual = actual, velocity = velocity,
          tip = tip, pitch = Mathf.DeltaAngle(0f, m_agent.CurrentBucketPitchDeg), steps = step,
          settled = stable >= StableSteps, valid = false };
        if ( step >= MinimumSteps && stable >= StableSteps ) break;
      }
      sample.valid = sample.settled && SampleMeetsChecks(q, sample.actual, sample.velocity, sample.tip)
        && Finite(sample.pitch);
      m_sample = sample;
      m_poseSamples++;
      Write( new JObject { ["kind"] = "pose", ["case_id"] = caseId,
        ["phase"] = phase, ["node_index"] = index, ["requested_q"] = Array(q),
        ["actual_q"] = Array(sample.actual), ["velocity_q"] = Array(sample.velocity),
        ["tip_unity_local_m"] = new JArray(sample.tip.x, sample.tip.y, sample.tip.z),
        ["pitch_deg"] = sample.pitch, ["steps"] = sample.steps, ["settled"] = sample.settled,
        ["valid"] = sample.valid, ["clearance_scope"] = "sampled_tip_clearance_only" } );
    }

    public static bool SampleMeetsChecks( Vector4 requested, Vector4 actual, Vector4 velocity, Vector3 tip )
      => PoseSettled(requested, actual, velocity) && TipAbovePlane(tip);

    public static bool TipAbovePlane(Vector3 tip)
      => Finite(tip.x) && Finite(tip.y) && Finite(tip.z) && tip.y > .05f;

    public static bool FiniteTipAndPitch(Vector3 tip, float pitch)
      => Finite(tip.x) && Finite(tip.y) && Finite(tip.z) && Finite(pitch);

    private static bool PoseSettled( Vector4 requested, Vector4 actual, Vector4 velocity )
    {
      for ( int i = 0; i < 4; ++i ) {
        if ( !Finite(requested[i]) || !Finite(actual[i]) || !Finite(velocity[i]) ) return false;
        if ( Mathf.Abs(actual[i] - requested[i]) > (i == 3 ? .01f : .002f) ) return false;
        if ( Mathf.Abs(velocity[i]) > (i == 3 ? .01f : .002f) ) return false;
      }
      return true;
    }

    public static float[] BucketScanTargets( float original )
    {
      if ( !Finite(original) || original < .0013f || original > .082f )
        throw new ArgumentOutOfRangeException(nameof(original));
      var values = new List<float> { original };
      for ( int i = 0; i <= 8; ++i ) {
        float value = Mathf.Lerp(.0013f, .082f, i / 8f);
        if ( Mathf.Abs(value - original) > 1e-5f ) values.Add(value);
      }
      return values.ToArray();
    }

    private Vector4 ReadPose() => new Vector4(m_controller.BoomPosition, m_controller.StickPosition,
      m_controller.BucketPosition, m_controller.SwingPosition);
    private static float[] Repeat( float value, int count )
    {
      var result = new float[count];
      for ( int i = 0; i < count; ++i ) result[i] = value;
      return result;
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static JArray Array(Vector4 value) => new JArray(value.x, value.y, value.z, value.w);
    private static JArray V3(Vector3 value) => new JArray(value.x, value.y, value.z);
    private void Reject(RealExecutionCase candidate, string reason)
    {
      m_rejected++;
      Write(new JObject { ["kind"] = "rejected", ["case_id"] = candidate.id,
        ["split"] = candidate.split, ["reason"] = reason });
    }
    private void Write(JObject row)
    {
      row["simulation_time_s"] = Time.time;
      m_writer.WriteLine(row.ToString(Formatting.None));
    }
    private void Finish(bool complete, string reason)
    {
      if ( m_finished ) return;
      m_finished = true;
      m_controller.SetHomeLock(false);
      m_controller.StopAll();
      if ( m_gridMode )
        File.WriteAllText(Path.Combine(m_output, "grid_samples.json"),
          new JObject { ["scope"] = "Diagnostic FK grid only, not a resolved training course",
            ["samples"] = m_gridSamples }.ToString(Formatting.Indented) + "\n");
      else {
        var set = new RealExecutionCaseSet { cases = m_resolved.ToArray() };
        File.WriteAllText(Path.Combine(m_output, "resolved_cases.json"), JsonUtility.ToJson(set, true) + "\n");
      }
      var summary = new JObject { ["status"] = complete ? "COMPLETE" : "INCOMPLETE",
        ["reason"] = reason, ["scope"] = Scope, ["diagnostic_grid_only"] = m_gridMode, ["candidates"] = m_attempts,
        ["accepted"] = m_resolved.Count, ["rejected"] = m_rejected, ["pose_samples"] = m_poseSamples };
      if ( m_gridMode ) summary["required_grid_poses"] = DiagnosticGrid().Length;
      else {
        summary["required_train"] = 32;
        summary["required_dev"] = 16;
        summary["required_holdout"] = 16;
      }
      File.WriteAllText(Path.Combine(m_output, "summary.json"), summary.ToString(Formatting.Indented) + "\n");
      Write(new JObject { ["kind"] = "end", ["summary"] = summary });
      m_writer.Dispose();
      m_writer = null;
      Debug.Log("[RealExecutionPhysicsSampler] " + summary.ToString(Formatting.None));
      if ( quitWhenFinished ) Application.Quit(complete ? 0 : 2);
    }
    private void OnDestroy() { m_writer?.Dispose(); }
  }
}
