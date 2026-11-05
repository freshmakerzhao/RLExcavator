using System;

namespace RLExcavator
{
  /// <summary>
  /// Isolated benchmark feasibility witness, not a learned policy or deployment controller.
  /// It requests joint-space convergence through the same execution proxy and gates.
  /// Joint coordinates and physical velocity references use m/m/s for cylinders,
  /// rad/rad/s for swing, in boom/stick/bucket/swing order. No minimum-speed floor.
  /// </summary>
  public static class RealExecutionWitness
  {
    // Benchmark-only gain: a 0.15-rad yaw error must exceed both original policy
    // deadzones after the caller converts this reference back to normalized action.
    public const float GainPerSecond = 5f;

    public static float[] Compute(float[] actualJointPosition,float[] goalJointPosition,
                                  RealExecutionProfile profile)
    {
      if(profile == null) throw new ArgumentNullException(nameof(profile));
      profile.Validate();
      RealExecutionProfile.CheckArray(actualJointPosition,4,nameof(actualJointPosition));
      RealExecutionProfile.CheckArray(goalJointPosition,4,nameof(goalJointPosition));
      var reference = new float[4];
      for(int axis=0; axis<4; axis++) {
        float error = goalJointPosition[axis]-actualJointPosition[axis];
        RealExecutionProfile.CheckFinite(error,"joint goal error");
        float positive = profile.maximumPositivePhysicalSpeed[axis];
        float negative = profile.maximumNegativePhysicalSpeed[axis];
        if(axis == 3) {
          positive = Math.Min(positive,profile.swingMaximumReferenceRadS);
          negative = Math.Min(negative,profile.swingMaximumReferenceRadS);
        }
        reference[axis] = Math.Max(-negative,Math.Min(positive,GainPerSecond*error));
      }
      return reference;
    }
  }
}
