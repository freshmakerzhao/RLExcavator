using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using AGXUnity;
using Unity.MLAgents;
using Unity.MLAgents.Policies;
using UnityEngine;

namespace RLExcavator
{
  [Serializable]
  public sealed class E4ReplayInput
  {
    public string schema_version;
    public string run_id;
    public string model_sha256;
    public float[] initial_normalized_q;
    public float initial_swing_rad;
    public string alignment_mode;
    public float initial_pitch_deg;
    public float initial_pitch_tolerance_deg = 2f;
    public float[] prepared_home_normalized_q;
    public float prepared_home_swing_rad;
    public Vector3 initial_tip_ros;
    public Vector3[] waypoints_ros;
    public float target_pitch_deg;
    public string phase;
    public float timeout_s = 30f;
    public float policy_period_s = .1f;
    public float waypoint_tolerance_m = 0.02f;
    public float[] waypoint_tolerances_m;
    public int initial_waypoint_index;
    public float initial_tip_tolerance_m = 0.03f;
    public float initial_normalized_q_tolerance = 0.03f;
    public float initial_swing_tolerance_rad = 0.03f;
  }

  public static class E4ReplayContract
  {
    public static void RequireMatchingPhysicsPeriods(float unityPeriod, float agxPeriod)
    {
      if (!float.IsFinite(unityPeriod) || !float.IsFinite(agxPeriod) || unityPeriod <= 0f ||
          agxPeriod <= 0f || Mathf.Abs(unityPeriod - agxPeriod) > 1e-7f)
        throw new ArgumentException("Unity and native AGX physics periods differ");
    }

    public static int DecisionPeriod(float policyPeriod, float physicsPeriod)
    {
      if (!float.IsFinite(policyPeriod) || !float.IsFinite(physicsPeriod) || policyPeriod <= 0f || physicsPeriod <= 0f)
        throw new ArgumentException("Invalid policy or physics period");
      float ratio = policyPeriod / physicsPeriod;
      int steps = Mathf.RoundToInt(ratio);
      if (steps < 1 || Mathf.Abs(ratio - steps) > 1e-4f)
        throw new ArgumentException("Policy period must be an integer multiple of E4 physics period");
      return steps;
    }

    public static Vector3 RosToUnityLocal(Vector3 point) => new Vector3(-point.y, point.z, point.x);
    public static Vector3 UnityLocalToRos(Vector3 point) => new Vector3(point.z, -point.x, point.y);

    public static float Denormalize(float normalized, float minimum, float maximum)
    {
      if (!float.IsFinite(normalized) || normalized < -1f || normalized > 1f ||
          !float.IsFinite(minimum) || !float.IsFinite(maximum) || maximum <= minimum)
        throw new ArgumentException("Invalid normalized state or AGX range");
      return minimum + (normalized + 1f) * 0.5f * (maximum - minimum);
    }

    public static Vector3[] ConvertPath(Vector3[] points)
    {
      if (points == null || points.Length < 2)
        throw new ArgumentException("Full reference must contain at least two waypoints");
      var result = new Vector3[points.Length];
      for (int i = 0; i < points.Length; i++) {
        RequireFinite(points[i]);
        result[i] = RosToUnityLocal(points[i]);
      }
      return result;
    }

    public static void InstallPath(TrajectoryProvider provider, Vector3[] worldPoints)
    {
      if (provider == null) throw new ArgumentException("Missing trajectory provider");
      provider.randomizePathOnReset = false;
      provider.SetPlannedPath(worldPoints);
      provider.QueuePlannedPath(worldPoints);
    }

    public static E4ReplayInput Parse(string json)
    {
      var input = JsonUtility.FromJson<E4ReplayInput>(json);
      if (input == null || input.schema_version != "e4_historical_replay.v1" ||
          string.IsNullOrWhiteSpace(input.run_id) || string.IsNullOrWhiteSpace(input.model_sha256) ||
          input.model_sha256.Length != 64 || (input.phase != "dig" && input.phase != "dump"))
        throw new ArgumentException("Invalid E4 run identity, model, schema or phase");
      if (input.initial_normalized_q == null || input.initial_normalized_q.Length != 3)
        throw new ArgumentException("Expected three normalized cylinder positions in boom/stick/bucket order");
      foreach (float q in input.initial_normalized_q) Denormalize(q, -1f, 1f);
      if (input.waypoint_tolerances_m != null && input.waypoint_tolerances_m.Length > 0) {
        if (input.waypoints_ros == null || input.waypoint_tolerances_m.Length != input.waypoints_ros.Length)
          throw new ArgumentException("Each waypoint must have its recorded tolerance");
        foreach (float tolerance in input.waypoint_tolerances_m)
          if (!float.IsFinite(tolerance) || tolerance <= 0f) throw new ArgumentException("Invalid waypoint tolerance");
      }
      if (!string.IsNullOrEmpty(input.alignment_mode) && input.alignment_mode != "geometry_task_alignment")
        throw new ArgumentException("Unknown alignment mode");
      if (!float.IsFinite(input.initial_pitch_deg) || !float.IsFinite(input.initial_pitch_tolerance_deg) || input.initial_pitch_tolerance_deg <= 0f)
        throw new ArgumentException("Invalid initial pitch or tolerance");
      if (input.prepared_home_normalized_q != null && input.prepared_home_normalized_q.Length > 0) {
        if (input.prepared_home_normalized_q.Length != 3 || !float.IsFinite(input.prepared_home_swing_rad))
          throw new ArgumentException("Invalid prepared geometry home");
        foreach (float q in input.prepared_home_normalized_q) Denormalize(q, -1f, 1f);
      }
      DecisionPeriod(input.policy_period_s, .01f);
      RequireFinite(input.initial_tip_ros);
      ConvertPath(input.waypoints_ros);
      if (input.initial_waypoint_index < 0 || input.initial_waypoint_index >= input.waypoints_ros.Length)
        throw new ArgumentException("Invalid historical initial waypoint index");
      if (!float.IsFinite(input.initial_swing_rad) || !float.IsFinite(input.target_pitch_deg))
        throw new ArgumentException("Invalid initial yaw or target pitch");
      foreach (float value in new[] { input.timeout_s, input.waypoint_tolerance_m,
        input.initial_tip_tolerance_m, input.initial_normalized_q_tolerance, input.initial_swing_tolerance_rad })
        if (!float.IsFinite(value) || value <= 0f) throw new ArgumentException("Tolerances and timeout must be positive");
      return input;
    }

    private static void RequireFinite(Vector3 value)
    {
      if (!float.IsFinite(value.x) || !float.IsFinite(value.y) || !float.IsFinite(value.z))
        throw new ArgumentException("Non-finite coordinate");
    }
  }

  /// <summary>Opt-in simulation-only historical replay; no hardware interfaces or demo changes.</summary>
  [DefaultExecutionOrder(-32000)]
  public sealed class E4HistoricalReplayRunner : MonoBehaviour
  {
    [Serializable] public sealed class BuildIdentity { public string model_sha256; }
    [Serializable] private sealed class Result
    {
      public string schema_version = "e4_replay_result.v1";
      public string run_id;
      public string status;
      public string reason;
      public bool initial_state_matched;
      public float initial_tip_error_m;
      public float initial_q_max_error;
      public float initial_swing_error_rad;
      public float elapsed_sim_s;
      public float policy_period_s;
      public float physics_period_s;
      public float agx_physics_period_s;
      public float final_distance_m;
      public int policy_decisions;
      public int policy_action_callbacks;
      public string alignment_mode;
      public float initial_pitch_error_deg;
      public int initialization_iterations;
      public string initialization_reason;
      public float[] prepared_home_normalized_q;
      public float prepared_home_swing_rad;
      public int observed_policy_actions_before_tracking;
      public string model_sha256;
      public string machine_frame_binding;
      public float[] actual_initial_normalized_q;
      public float actual_initial_swing_rad;
      public Vector3 actual_initial_tip_ros;
      public float actual_initial_pitch_deg;
      public float configured_target_pitch_deg;
      public float configured_initial_waypoint_tolerance_m;
      public int configured_initial_waypoint_index;
      public Vector3[] imported_waypoints_ros;
    }
    [Serializable] private sealed class Sample
    {
      public string kind;
      public float sim_time_s;
      public Vector3 tip_ros;
      public Vector3 waypoint_ros;
      public float[] normalized_q;
      public float swing_rad;
      public float pitch_deg;
      public int waypoint_index;
      public Vector4 commanded_action;
      public Vector4 raw_policy_action;
    }

    private ExcavatorTrajectoryAgent agent;
    private ExcavatorController controller;
    private DecisionRequester decisions;
    private E4ReplayInput input;
    private readonly Result result = new Result();
    private StreamWriter trace;
    private string outputDirectory;
    private float trackingStartedAt;
    private bool tracking;
    private bool outputWritable;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
      if (string.IsNullOrWhiteSpace(Argument("--e4-input"))) return;
      new GameObject("E4 Historical Replay (simulation only)").AddComponent<E4HistoricalReplayRunner>();
    }

    public static string Argument(string key)
    {
      string[] args = Environment.GetCommandLineArgs();
      for (int i = 0; i < args.Length - 1; i++) if (args[i] == key) return args[i + 1];
      return "";
    }

    public static bool HasArgument(string key) => Array.IndexOf(Environment.GetCommandLineArgs(), key) >= 0;

    private void Awake()
    {
      Academy.Instance.AutomaticSteppingEnabled = false;
      Time.fixedDeltaTime = .01f; // This opt-in E4 player only; source scenes/profile remain unchanged.
      AGXUnity.Simulation.Instance.TimeStep = Time.fixedDeltaTime;
      agent = FindFirstObjectByType<ExcavatorTrajectoryAgent>(FindObjectsInactive.Include);
      if (agent != null) agent.PolicyActionReceived += RecordDecision;
      foreach (var cycle in FindObjectsByType<ExcavationCycleTask>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
        cycle.StopTask();
        cycle.enabled = false;
      }
      agent = FindFirstObjectByType<ExcavatorTrajectoryAgent>(FindObjectsInactive.Include);
      if (agent == null) return;
      controller = agent.controller;
      decisions = agent.GetComponent<DecisionRequester>();
      if (decisions != null) decisions.enabled = false;
      agent.enabled = false;
      if (controller != null) { controller.DisableCommandInput(); controller.StopAll(); }
      // Complete the first Academy reset without any Agent subscribed to action events.
      // This package invokes DecisionRequester even when its component is disabled.
      if (decisions != null) {
        decisions.DecisionPeriod = 2;
        decisions.DecisionStep = 1;
        decisions.TakeActionsBetweenDecisions = false;
      }
      Academy.Instance.EnvironmentStep();
    }

    private IEnumerator Start()
    {
      var operations = new Stack<IEnumerator>();
      operations.Push(Run());
      while (operations.Count > 0) {
        object next;
        try {
          if (!operations.Peek().MoveNext()) { operations.Pop(); continue; }
          next = operations.Peek().Current;
          if (next is IEnumerator nested) { operations.Push(nested); continue; }
        } catch (System.Exception error) {
          Complete("failed", error.ToString());
          yield break;
        }
        yield return next;
      }
    }

    private IEnumerator Run()
    {
      bool preflight = HasArgument("--e4-preflight-only");
      bool execute = HasArgument("--execute");
      if (preflight == execute) throw new ArgumentException("Choose exactly one of --e4-preflight-only or --execute");
      outputDirectory = Argument("--e4-output");
      if (string.IsNullOrWhiteSpace(outputDirectory)) throw new ArgumentException("--e4-output is required");
      Directory.CreateDirectory(outputDirectory);
      if (File.Exists(Path.Combine(outputDirectory, "result.json"))) throw new IOException("Output already contains result.json");
      if (File.Exists(Path.Combine(outputDirectory, "trace.jsonl"))) throw new IOException("Output already contains trace.jsonl");
      outputWritable = true;
      input = E4ReplayContract.Parse(File.ReadAllText(Argument("--e4-input")));
      result.run_id = input.run_id;
      result.policy_period_s = input.policy_period_s;
      result.physics_period_s = Time.fixedDeltaTime;
      result.agx_physics_period_s = (float)AGXUnity.Simulation.Instance.Native.getTimeStep();
      E4ReplayContract.RequireMatchingPhysicsPeriods(result.physics_period_s, result.agx_physics_period_s);
      result.alignment_mode = GeometryAlignment ? "geometry_task_alignment" : "normalized_observation_alignment";
      if (execute && GeometryAlignment && !HasPreparedHome)
        throw new ArgumentException("Formal geometry replay requires a preflight-prepared home");
      result.model_sha256 = input.model_sha256;
      var identity = JsonUtility.FromJson<BuildIdentity>(File.ReadAllText(
        Path.Combine(Application.dataPath, "..", "e4_build_identity.json")));
      if (identity == null || !string.Equals(identity.model_sha256, input.model_sha256, StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("Historical and built policy model hashes differ");
      if (agent == null) throw new InvalidOperationException("Replay scene is missing trajectory agent");
      if (decisions == null) throw new InvalidOperationException("Replay agent is missing DecisionRequester");
      var behavior = agent.GetComponent<BehaviorParameters>();
      if (behavior == null || behavior.Model == null || behavior.BehaviorType != BehaviorType.InferenceOnly)
        throw new InvalidOperationException("Expected baked deterministic ONNX inference model");
      trace = new StreamWriter(Path.Combine(outputDirectory, "trace.jsonl"), false);
      // OnDisable destroys ML-Agents initialization. Enable while decisions and input
      // are disabled, then configure the historical task after Initialize has loaded defaults.
      agent.resetPoseOnEpisodeBegin = false;
      agent.randomizeTaskModeOnEpisodeBegin = false;
      agent.enabled = true;
      agent.LazyInitialize();
      controller = agent.controller;
      if (controller == null || agent.bucketTip == null || agent.trajectory == null || agent.frameRegistry == null || agent.frameRegistry.MachineRoot == null)
        throw new InvalidOperationException("Replay references after Initialize: controller=" + (controller != null) +
          ", bucketTip=" + (agent.bucketTip != null) + ", trajectory=" + (agent.trajectory != null) + ", frameRegistry=" + (agent.frameRegistry != null));
      controller.DisableCommandInput();
      controller.StopAll();
      result.machine_frame_binding = agent.frameRegistry != null && agent.frameRegistry.MachineRoot != null
        ? "frame_registry" : "agent_transform_fallback_matching_policy";
      yield return new WaitForFixedUpdate();
      yield return InitializePose();
      VerifyInitialState();
      if (!result.initial_state_matched) { Complete("initial_state_mismatch", "AGX initialization did not reproduce historical state"); yield break; }
      ConfigureTracking();
      agent.OnEpisodeBegin();
      while (agent.CurrentTargetIndex < input.initial_waypoint_index) agent.trajectory.AdvanceTarget();
      ApplyCurrentWaypointTolerance();
      VerifyImportedTask();
      if (result.observed_policy_actions_before_tracking != 0)
        throw new InvalidOperationException("Policy action callbacks occurred during initialization/preflight");
      if (preflight) { Complete("preflight_passed", "Initial state and task matched; RL tracking was not started"); yield break; }
      int episode = agent.CurrentEpisode;
      int serial = agent.CompletedPathSerial;
      trackingStartedAt = Time.fixedTime;
      tracking = true;
      FixedUpdate();
      WriteSample("initial", Vector4.zero);
      controller.EnableCommandInput();
      decisions.enabled = true;
      Academy.Instance.AutomaticSteppingEnabled = true;
      while (Time.fixedTime - trackingStartedAt < input.timeout_s) {
        yield return new WaitForFixedUpdate();
        if (agent.CompletedPathSerial > serial) { Complete("succeeded", "Full historical reference completed"); yield break; }
        if (agent.CurrentEpisode != episode) { Complete("failed", agent.CurrentEpisodeFailureReason); yield break; }
      }
      Complete("timeout", "Historical reference did not complete within timeout");
    }

    private bool GeometryAlignment => input.alignment_mode == "geometry_task_alignment";
    private bool HasPreparedHome => input.prepared_home_normalized_q != null && input.prepared_home_normalized_q.Length == 3;

    private IEnumerator InitializePose()
    {
      float[] q = HasPreparedHome ? input.prepared_home_normalized_q : input.initial_normalized_q;
      Vector4 home = new Vector4(q[0], q[1], q[2], HasPreparedHome ? input.prepared_home_swing_rad : input.initial_swing_rad);
      if (GeometryAlignment && !HasPreparedHome) {
        var initializer = new E4GeometryInitializer(controller, ConfigureHome, TipRos,
          () => agent.CurrentBucketPitchDeg, input.initial_tip_ros, input.initial_pitch_deg,
          input.initial_tip_tolerance_m, input.initial_pitch_tolerance_deg);
        yield return initializer.Solve(home);
        home = initializer.Solution;
        result.initialization_iterations = initializer.Iterations;
        result.initialization_reason = initializer.Succeeded ? "geometry_solved" : initializer.FailureReason;
      } else {
        controller.homePoseTolerance = .00002f;
        ConfigureHome(home);
        controller.BeginHomePoseReset(40);
        while (controller.IsHoming) yield return new WaitForFixedUpdate();
        result.initialization_reason = HasPreparedHome ? "prepared_home_restored" : "observation_seed_restored";
      }
      result.prepared_home_normalized_q = new[] { home.x, home.y, home.z };
      result.prepared_home_swing_rad = home.w;
    }

    private void ConfigureHome(Vector4 home)
    {
      controller.useInspectorHomePose = true;
      controller.boomHomePose = HomeTargets(controller.boom, home.x);
      controller.stickHomePose = HomeTargets(controller.stick, home.y);
      controller.bucketHomePose = HomeTargets(controller.bucket, home.z);
      controller.swingHomePose = home.w;
    }

    private static float[] HomeTargets(Constraint[] constraints, float normalized)
    {
      if (constraints == null || constraints.Length == 0) throw new InvalidOperationException("Missing cylinder constraints");
      var targets = new float[constraints.Length];
      for (int i = 0; i < constraints.Length; i++) {
        var range = constraints[i] == null ? null : constraints[i].GetController<RangeController>();
        if (range == null) throw new InvalidOperationException("Missing cylinder range");
        targets[i] = E4ReplayContract.Denormalize(normalized, range.Range.Min, range.Range.Max);
      }
      return targets;
    }

    private void VerifyInitialState()
    {
      result.actual_initial_tip_ros = TipRos();
      result.actual_initial_swing_rad = controller.SwingPosition;
      result.actual_initial_pitch_deg = agent.CurrentBucketPitchDeg;
      result.actual_initial_normalized_q = NormalizedQ();
      result.initial_tip_error_m = Vector3.Distance(TipRos(), input.initial_tip_ros);
      float[] actual = NormalizedQ();
      for (int i = 0; i < 3; i++) result.initial_q_max_error = Mathf.Max(result.initial_q_max_error, Mathf.Abs(actual[i] - input.initial_normalized_q[i]));
      result.initial_swing_error_rad = Mathf.Abs(Mathf.DeltaAngle(controller.SwingPosition * Mathf.Rad2Deg, input.initial_swing_rad * Mathf.Rad2Deg)) * Mathf.Deg2Rad;
      result.initial_pitch_error_deg = Mathf.Abs(Mathf.DeltaAngle(result.actual_initial_pitch_deg, input.initial_pitch_deg));
      result.initial_state_matched = result.initial_tip_error_m <= input.initial_tip_tolerance_m &&
        (GeometryAlignment ? result.initial_pitch_error_deg <= input.initial_pitch_tolerance_deg :
          result.initial_q_max_error <= input.initial_normalized_q_tolerance &&
          result.initial_swing_error_rad <= input.initial_swing_tolerance_rad);
      WriteSample("initialized", Vector4.zero);
    }

    private void ConfigureTracking()
    {
      agent.resetPoseOnEpisodeBegin = false;
      agent.randomizeTaskModeOnEpisodeBegin = false;
      agent.taskMode = input.phase == "dig" ? ExcavatorTrajectoryAgent.TrajectoryTaskMode.MoveToDig : ExcavatorTrajectoryAgent.TrajectoryTaskMode.CarryMaterial;
      agent.moveToDigTargetBucketPitchDeg = input.target_pitch_deg;
      agent.carryMaterialTargetBucketPitchDeg = input.target_pitch_deg;
      agent.targetThreshold = input.waypoint_tolerance_m;
      agent.includeTargetVisualRadiusInThreshold = false;
      agent.trajectory.randomizePathOnReset = false;
      Vector3[] points = E4ReplayContract.ConvertPath(input.waypoints_ros);
      for (int i = 0; i < points.Length; i++) points[i] = MachineFrame.TransformPoint(points[i]);
      E4ReplayContract.InstallPath(agent.trajectory, points);
      decisions.DecisionPeriod = E4ReplayContract.DecisionPeriod(input.policy_period_s, Time.fixedDeltaTime);
      decisions.DecisionStep = Academy.Instance.StepCount % decisions.DecisionPeriod;
      decisions.TakeActionsBetweenDecisions = true;
    }

    private void FixedUpdate()
    {
      if (!tracking || input == null || agent == null) return;
      ApplyCurrentWaypointTolerance();
    }

    private void ApplyCurrentWaypointTolerance()
    {
      agent.targetThreshold = input.waypoint_tolerances_m != null && input.waypoint_tolerances_m.Length > 0
        ? input.waypoint_tolerances_m[Mathf.Clamp(agent.CurrentTargetIndex, 0, input.waypoint_tolerances_m.Length - 1)]
        : input.waypoint_tolerance_m;
    }

    private void VerifyImportedTask()
    {
      result.configured_target_pitch_deg = agent.CurrentTargetBucketPitchDeg;
      result.configured_initial_waypoint_tolerance_m = agent.CurrentTargetThreshold;
      result.configured_initial_waypoint_index = agent.CurrentTargetIndex;
      float expectedTolerance = input.waypoint_tolerances_m != null && input.waypoint_tolerances_m.Length > 0
        ? input.waypoint_tolerances_m[input.initial_waypoint_index] : input.waypoint_tolerance_m;
      if (Mathf.Abs(Mathf.DeltaAngle(result.configured_target_pitch_deg, input.target_pitch_deg)) > 0.001f ||
          Mathf.Abs(agent.CurrentTargetThreshold - expectedTolerance) > 1e-6f || agent.CurrentTargetIndex != input.initial_waypoint_index)
        throw new InvalidOperationException("Initialized task pitch, tolerance or waypoint index differs from imported task");
      if (agent.trajectory.TargetCount != input.waypoints_ros.Length)
        throw new InvalidOperationException("Initialized task dropped reference waypoints");
      result.imported_waypoints_ros = new Vector3[agent.trajectory.TargetCount];
      for (int i = 0; i < result.imported_waypoints_ros.Length; i++) {
        result.imported_waypoints_ros[i] = E4ReplayContract.UnityLocalToRos(
          MachineFrame.InverseTransformPoint(agent.trajectory.GetWaypointPosition(i)));
        if (Vector3.Distance(result.imported_waypoints_ros[i], input.waypoints_ros[i]) > 1e-5f)
          throw new InvalidOperationException("Initialized task changed historical waypoint " + i);
      }
    }

    private Transform MachineFrame => agent.frameRegistry != null && agent.frameRegistry.MachineRoot != null
      ? agent.frameRegistry.MachineRoot : agent.transform;

    private Vector3 TipRos() => E4ReplayContract.UnityLocalToRos(MachineFrame.InverseTransformPoint(agent.bucketTip.position));
    private float[] NormalizedQ() => new[] { controller.BoomNormalizedPosition, controller.StickNormalizedPosition, controller.BucketNormalizedPosition };

    private void RecordDecision(Vector4 action, int sequence, double wallTime)
    {
      result.policy_decisions++; // Legacy field: action callbacks, not ONNX inference count.
      result.policy_action_callbacks++;
      if (!tracking) result.observed_policy_actions_before_tracking++;
      WriteSample(tracking ? "decision" : "unexpected_setup_policy_action", action);
    }

    private void WriteSample(string kind, Vector4 action)
    {
      if (trace == null) return;
      var sample = new Sample { kind = kind, sim_time_s = tracking ? Time.fixedTime - trackingStartedAt : 0f,
        tip_ros = TipRos(), normalized_q = NormalizedQ(), swing_rad = controller.SwingPosition,
        pitch_deg = agent.CurrentBucketPitchDeg, waypoint_index = agent.CurrentTargetIndex,
        waypoint_ros = E4ReplayContract.UnityLocalToRos(MachineFrame.InverseTransformPoint(agent.trajectory.CurrentTargetPosition)),
        commanded_action = action, raw_policy_action = agent.LastRawPolicyAction };
      trace.WriteLine(JsonUtility.ToJson(sample));
      trace.Flush();
    }

    private void Complete(string status, string reason)
    {
      if (Academy.IsInitialized) Academy.Instance.AutomaticSteppingEnabled = false;
      if (decisions != null) decisions.enabled = false;
      if (controller != null) { controller.DisableCommandInput(); controller.StopAll(); }
      if (agent != null) { agent.PolicyActionReceived -= RecordDecision; agent.enabled = false; }
      result.status = status;
      result.reason = reason;
      result.elapsed_sim_s = tracking ? Time.fixedTime - trackingStartedAt : 0f;
      if (tracking) {
        result.final_distance_m = Vector3.Distance(TipRos(), input.waypoints_ros[input.waypoints_ros.Length - 1]);
        WriteSample("terminal", Vector4.zero);
      }
      trace?.Dispose();
      trace = null;
      if (outputWritable)
        File.WriteAllText(Path.Combine(outputDirectory, "result.json"), JsonUtility.ToJson(result, true));
      Debug.Log("[E4 replay] " + JsonUtility.ToJson(result));
      Application.Quit(status == "succeeded" || status == "preflight_passed" ? 0 : 2);
    }
  }
}
