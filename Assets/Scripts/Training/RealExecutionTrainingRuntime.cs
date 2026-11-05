using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Unity.MLAgents;
using UnityEngine;

namespace RLExcavator
{
  /// <summary>Opt-in simulation experiment only. Does not change policy observations or production execution.</summary>
  [DefaultExecutionOrder(-200)]
  [DisallowMultipleComponent]
  public sealed class RealExecutionTrainingRuntime : MonoBehaviour
  {
    public string runMode = "train";
    public TextAsset executionProfile;
    public TextAsset courseCases;
    private ExcavatorTrajectoryAgent m_agent;
    private ExcavatorController m_controller;
    private RealExecutionCourse m_course;
    private RealExecutionCase[] m_cases;
    private RealExecutionModel m_model;
    private RealExecutionProfile m_profile;
    private StreamWriter m_writer;
    private Vector4 m_requested, m_previousQ, m_velocity;
    private float m_clock, m_lastControlTime, m_startedAt;
    private bool m_haveQ, m_recording, m_initialized;
    private bool m_witness;
    private int m_caseIndex;
    private string m_mapping;
    public RealExecutionResult LastResult { get; private set; }
    public bool ExecutionEnabled => enabled && runMode != "sampler" && m_initialized;
    public Vector4 MeasuredVelocity => m_velocity;
    public Vector4 JointPose => new Vector4(m_controller.BoomPosition, m_controller.StickPosition,
                                           m_controller.BucketPosition, m_controller.SwingPosition);

    private void Awake()
    {
      m_agent = GetComponent<ExcavatorTrajectoryAgent>();
      m_controller = m_agent.controller;
      m_course = GetComponent<RealExecutionCourse>();
      if (runMode == "sampler") return;
      InitializeExecution();
    }

    private void Start()
    {
      if (runMode == "sampler") {
        string output = Argument("--real-execution-output");
        if (string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException("Sampler output directory required.");
        // Accelerate wall time only; preserve the scene's physical integration step.
        Time.timeScale = 20f;
        Application.targetFrameRate = -1;
        Debug.Log("[RealExecution] Sampler time scale=20, fixed dt=" + Time.fixedDeltaTime);
        gameObject.AddComponent<RealExecutionPhysicsSampler>().Begin(m_agent, output);
      }
    }

    private void InitializeExecution()
    {
      if (m_initialized) return;
      if (executionProfile == null) throw new InvalidOperationException("Execution profile is required.");
      m_profile = JsonUtility.FromJson<RealExecutionProfile>(executionProfile.text);
      m_profile.Validate();
      m_witness = Environment.GetEnvironmentVariable("REAL_EXECUTION_WITNESS") == "1";
      if (m_witness && runMode != "benchmark")
        throw new InvalidOperationException("Joint witness is only allowed in an explicit benchmark.");
      m_mapping = Argument("--real-execution-mapping") ?? "candidate";
      if (m_mapping != "candidate" && m_mapping != "legacy") throw new ArgumentException("Unknown execution mapping.");
      m_model = new RealExecutionModel(m_profile, m_mapping == "legacy"
        ? RealExecutionMapping.LegacyManualDeadzone : RealExecutionMapping.CandidateAutomatic);
      string suite = Argument("--continuous-path-benchmark");
      string json = runMode == "benchmark" && suite != null ? File.ReadAllText(suite)
        : courseCases != null ? courseCases.text : throw new InvalidOperationException("Resolved course is required.");
      var set = JsonUtility.FromJson<RealExecutionCaseSet>(json);
      if (set == null || set.schema != "real_execution_cases_v1" || set.cases == null)
        throw new InvalidOperationException("Unknown course schema.");
      m_cases = set.cases;
      foreach (var item in m_cases) RealExecutionCourse.ValidateResolved(item);
      if (runMode == "train") {
        m_course.split = "train";
        m_course.cases = m_cases.Where(item => item.split == "train" && item.taskMode == m_agent.taskMode.ToString()).ToArray();
        if (m_course.cases.Length == 0) throw new InvalidOperationException("Missing training mode coverage.");
        // Stable environment identity gives parallel agents different task order.
        m_course.seed += m_agent.environmentRoot.name.Aggregate(0, (value, c) => (value * 31 + c) & 0x7fff);
      }
      string output = Argument("--benchmark-output");
      if (runMode == "benchmark" && output != null)
        m_writer = new StreamWriter(output, false) { AutoFlush = true };
      m_controller.realExecutionRuntime = this;
      m_initialized = true;
      Debug.Log("[RealExecution] " + m_profile.executionVersion + "/" + m_mapping + "/" + m_profile.responseModel);
    }

    public void PrepareEpisode(ExcavatorTrajectoryAgent agent)
    {
      InitializeExecution();
      m_recording = false;
      ClearReference();
      m_haveQ = false;
      m_clock = 0f;
      if (runMode == "benchmark") {
        m_caseIndex = (int)Academy.Instance.EnvironmentParameters.GetWithDefault("benchmark_case", 0f);
        if (m_caseIndex < 0 || m_caseIndex >= m_cases.Length) throw new ArgumentOutOfRangeException("benchmark_case");
        m_course.split = m_cases[m_caseIndex].split;
        m_course.cases = new[] {m_cases[m_caseIndex]};
      }
      var selected = m_course.PrepareEpisode(m_controller);
      agent.taskMode = (ExcavatorTrajectoryAgent.TrajectoryTaskMode)Enum.Parse(
        typeof(ExcavatorTrajectoryAgent.TrajectoryTaskMode), selected.taskMode);
    }

    public Vector3[] BeginReference(Vector3 actualTip)
    {
      try {
        if (!m_course.ValidateReset(JointPose, m_velocity))
          throw new InvalidOperationException("Joint pose or speed does not match the course reset.");
        return m_course.CreateReference(actualTip);
      }
      catch (Exception error) {
        Write(new JObject { ["kind"]="reset_failed", ["q"]=Array(JointPose),
          ["velocity"]=Array(m_velocity), ["error"]=error.Message });
        m_controller.StopAll();
        Debug.LogError("[RealExecution] Invalid physical reset: " + error.Message);
        m_agent.enabled = false;
        enabled = false;
        Application.Quit(3);
        throw;
      }
    }

    public void RecordStart(Vector3[] path)
    {
      m_startedAt = Time.fixedTime;
      m_lastControlTime = Time.fixedTime;
      m_clock = 0f;
      m_recording = true;
      ClearReference();
      Write(new JObject { ["kind"]="start", ["elapsed_s"]=0f,
        ["actual_joint_pose"]=Array(JointPose), ["tip_ros_m"]=Ros(path[0]),
        ["route_ros_m"]=new JArray(path.Select(p => Ros(p))),
        ["pitch_deg"]=m_agent.CurrentBucketPitchDeg, ["task_mode"]=m_agent.taskMode.ToString(),
        ["target_pitch_deg"]=m_agent.CurrentTargetBucketPitchDeg,
        ["response_model"]=m_profile.responseModel, ["mapping"]=m_mapping,
        ["controller"]=m_witness ? "joint-witness" : "policy" });
    }

    public void RecordSample(ContinuousPathTracking tracking, Vector3 tip)
    {
      if (!m_recording) return;
      Write(new JObject { ["kind"]="sample", ["elapsed_s"]=Time.fixedTime-m_startedAt,
        ["time_s"]=Time.fixedTime, ["tip_ros_m"]=Ros(tip), ["pitch_deg"]=m_agent.CurrentBucketPitchDeg,
        ["progress"]=tracking.Progress, ["remaining_m"]=tracking.RemainingDistanceM,
        ["tip_speed_mps"]=tracking.TipSpeedMps });
    }

    public void RecordEnd(bool success, string reason)
    {
      if (!m_recording) return;
      Write(new JObject { ["kind"]="end", ["elapsed_s"]=Time.fixedTime-m_startedAt,
        ["success"]=success, ["reason"]=reason });
      m_recording = false;
      ClearReference();
    }

    public void SubmitReference(Vector4 reference) { m_requested = reference; }
    public void ClearReference() { m_requested = Vector4.zero; LastResult = null; }

    private void FixedUpdate()
    {
      if (!ExecutionEnabled) return;
      Vector4 q = JointPose;
      float dt = Time.fixedDeltaTime;
      m_velocity = m_haveQ ? (q-m_previousQ)/dt : Vector4.zero;
      m_previousQ = q;
      m_haveQ = true;
      if (m_controller.IsHoming || !m_recording || !m_controller.CommandInputEnabled) {
        ClearReference();
        m_clock = 0f;
        return;
      }
      if (!AdvanceControlClock(ref m_clock, dt, m_profile.controlPeriodSeconds)) return;
      if (m_witness) {
        float[] witness = RealExecutionWitness.Compute(Values(q), Values(m_course.LastCase.goalJointPose), m_profile);
        // Use the same normalized-action/deadzone path as a policy; the witness cannot bypass it.
        var normalized = Vector4.zero;
        for (int i = 0; i < 4; ++i)
          normalized[i] = witness[i] / (witness[i] >= 0f
            ? m_profile.maximumPositivePhysicalSpeed[i] : m_profile.maximumNegativePhysicalSpeed[i]);
        m_controller.SetNormalizedSpeeds(normalized.x, normalized.y, normalized.z, normalized.w);
      }
      LastResult = m_model.Step(Values(m_requested), Values(q), Values(m_velocity));
      float[] speed = LastResult.ProxyPhysicalSpeed;
      m_controller.ApplyExecutionSpeeds(new Vector4(speed[0],speed[1],speed[2],speed[3]));
      if (m_writer != null) Write(new JObject { ["kind"]="execution",
        ["elapsed_s"]=Time.fixedTime-m_startedAt, ["time_s"]=Time.fixedTime,
        ["control_dt_s"]=Time.fixedTime-m_lastControlTime,
        ["q"]=Array(q), ["measured_velocity"]=Array(m_velocity), ["raw_position_mm"]=new JArray(LastResult.RawPositionMm),
        ["raw_swing_deg"]=LastResult.RawSwingDegrees, ["action"]=Array(m_agent.LastRawPolicyAction),
        ["policy_action_used"]=!m_witness,
        ["requested_velocity"]=Array(m_requested), ["limited_reference"]=new JArray(LastResult.LimitedPhysicalReference),
        ["pid_effort"]=new JArray(LastResult.PidEffort), ["limited_effort"]=new JArray(LastResult.LimitedEffort),
        ["valve_angle_deg"]=new JArray(LastResult.ValveAnglesDeg), ["pwm"]=new JArray(LastResult.PwmCounts),
        ["proxy_velocity"]=new JArray(speed), ["limit_mask"]=LastResult.LimitMask, ["neutral_mask"]=LastResult.NeutralMask });
      m_lastControlTime = Time.fixedTime;
    }

    public static bool AdvanceControlClock(ref float remainder, float dt, float period)
    {
      if (float.IsNaN(dt) || float.IsInfinity(dt) || dt <= 0f || float.IsNaN(period)
        || float.IsInfinity(period) || period <= 0f || dt > period)
        throw new ArgumentException("Control clock requires finite positive dt <= period.");
      remainder += dt;
      if (remainder + 1e-7f < period) return false;
      remainder = Mathf.Max(0f, remainder-period);
      return true;
    }

    private void Write(JObject row)
    {
      if (m_writer == null) return;
      row["case_index"] = m_caseIndex;
      row["case_id"] = m_course.LastCase != null ? m_course.LastCase.id : "unprepared";
      m_writer.WriteLine(row.ToString(Newtonsoft.Json.Formatting.None));
    }
    private void OnDestroy() { m_writer?.Dispose(); }
    private static float[] Values(Vector4 value) => new[] {value.x,value.y,value.z,value.w};
    private static JArray Array(Vector4 value) => new JArray(value.x,value.y,value.z,value.w);
    private static JArray Ros(Vector3 value) => new JArray(value.z,-value.x,value.y);
    private static string Argument(string name)
    {
      var args = Environment.GetCommandLineArgs();
      int index = System.Array.IndexOf(args,name);
      return index >= 0 && index+1 < args.Length ? args[index+1] : null;
    }
  }
}
