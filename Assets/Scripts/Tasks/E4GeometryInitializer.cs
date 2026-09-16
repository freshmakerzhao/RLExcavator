using System;
using System.Collections;
using UnityEngine;

namespace RLExcavator
{
  /// <summary>Bounded numerical pose initialization using the existing AGX home locks as FK.
  /// This runs before policy stepping and does not change historical Cartesian targets.</summary>
  public sealed class E4GeometryInitializer
  {
    private const float PitchScale = .25f;
    private readonly ExcavatorController controller;
    private readonly Action<Vector4> setHome;
    private readonly Func<Vector3> tip;
    private readonly Func<float> pitch;
    private readonly Vector3 targetTip;
    private readonly float targetPitch;
    private readonly float tipTolerance;
    private readonly float pitchTolerance;

    public Vector4 Solution { get; private set; }
    public int Iterations { get; private set; }
    public string FailureReason { get; private set; }
    public bool Succeeded { get; private set; }

    public E4GeometryInitializer(ExcavatorController controller, Action<Vector4> setHome,
      Func<Vector3> tip, Func<float> pitch, Vector3 targetTip, float targetPitch,
      float tipTolerance, float pitchTolerance)
    {
      this.controller = controller; this.setHome = setHome; this.tip = tip; this.pitch = pitch;
      this.targetTip = targetTip; this.targetPitch = targetPitch;
      this.tipTolerance = tipTolerance; this.pitchTolerance = pitchTolerance;
    }

    public static Vector4 Residual(Vector3 actualTip, float actualPitch, Vector3 targetTip, float targetPitch)
    {
      Vector3 delta = targetTip - actualTip;
      return new Vector4(delta.x, delta.y, delta.z, Mathf.DeltaAngle(actualPitch, targetPitch) * Mathf.Deg2Rad * PitchScale);
    }

    public static Vector4 DampedStep(Matrix4x4 jacobian, Vector4 residual, float damping)
    {
      Matrix4x4 normal = jacobian.transpose * jacobian;
      for (int axis = 0; axis < 4; axis++) normal[axis, axis] += damping * damping;
      return normal.inverse * (jacobian.transpose * residual);
    }

    public static Vector4 BoundStep(Vector4 step)
    {
      float scale = Mathf.Max(1f, Mathf.Abs(step.x) / .25f, Mathf.Abs(step.y) / .25f,
        Mathf.Abs(step.z) / .25f, Mathf.Abs(step.w) / .35f);
      return step / scale;
    }

    private static Vector4 LimitCylinders(Vector4 value)
    {
      return new Vector4(Mathf.Clamp(value.x, -1f, 1f), Mathf.Clamp(value.y, -1f, 1f),
        Mathf.Clamp(value.z, -1f, 1f), value.w);
    }

    private IEnumerator MoveHome(Vector4 value)
    {
      setHome(value);
      controller.BeginHomePoseReset(40);
      while (controller.IsHoming) yield return new WaitForFixedUpdate();
    }

    public IEnumerator Solve(Vector4 seed)
    {
      controller.homePoseTolerance = .00002f;
      Solution = LimitCylinders(seed);
      yield return MoveHome(Solution);
      Vector3 baseTip = tip();
      float basePitch = pitch();
      for (int iteration = 0; iteration < 30; iteration++) {
        Iterations = iteration;
        if (Vector3.Distance(baseTip, targetTip) <= tipTolerance &&
            Mathf.Abs(Mathf.DeltaAngle(basePitch, targetPitch)) <= pitchTolerance) {
          Succeeded = true;
          yield break;
        }
        Vector4 residual = Residual(baseTip, basePitch, targetTip, targetPitch);
        Matrix4x4 jacobian = Matrix4x4.zero;
        for (int axis = 0; axis < 4; axis++) {
          Vector4 probe = Solution;
          float epsilon = axis == 3 ? .03f : .04f;
          if (axis < 3 && probe[axis] + epsilon > 1f) epsilon = -epsilon;
          probe[axis] += epsilon;
          yield return MoveHome(probe);
          Vector3 spatial = (tip() - baseTip) / epsilon;
          float angular = Mathf.DeltaAngle(basePitch, pitch()) * Mathf.Deg2Rad * PitchScale / epsilon;
          jacobian.SetColumn(axis, new Vector4(spatial.x, spatial.y, spatial.z, angular));
        }
        Vector4 step = BoundStep(DampedStep(jacobian, residual, .025f));
        bool accepted = false;
        for (int backtrack = 0; backtrack < 4; backtrack++) {
          Vector4 candidate = LimitCylinders(Solution + step * Mathf.Pow(.5f, backtrack));
          yield return MoveHome(candidate);
          Vector3 candidateTip = tip();
          float candidatePitch = pitch();
          if (Residual(candidateTip, candidatePitch, targetTip, targetPitch).sqrMagnitude < residual.sqrMagnitude) {
            Solution = candidate; baseTip = candidateTip; basePitch = candidatePitch;
            accepted = true;
            break;
          }
        }
        Debug.Log("[E4 geometry init] iteration=" + iteration + " tip_error_m=" +
          Vector3.Distance(baseTip, targetTip) + " pitch_error_deg=" + Mathf.Abs(Mathf.DeltaAngle(basePitch, targetPitch)));
        if (!accepted) { FailureReason = "geometry_initialization_stalled"; break; }
      }
      yield return MoveHome(Solution);
      Succeeded = Vector3.Distance(tip(), targetTip) <= tipTolerance &&
        Mathf.Abs(Mathf.DeltaAngle(pitch(), targetPitch)) <= pitchTolerance;
      if (!Succeeded && string.IsNullOrEmpty(FailureReason)) FailureReason = "geometry_initialization_iteration_limit";
    }
  }
}
