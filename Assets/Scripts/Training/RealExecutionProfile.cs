using System;

namespace RLExcavator
{
  public enum RealExecutionMapping { CandidateAutomatic, LegacyManualDeadzone }

  /// <summary>
  /// Independent experiment configuration. It does not change policy observations,
  /// machine_profile.jsonc, AGX hard limits, or the deployed firmware.
  /// Public fields allow Unity JsonUtility to deserialize the versioned TextAsset.
  /// </summary>
  [Serializable]
  public sealed class RealExecutionProfile
  {
    public string schema;
    public string executionVersion;
    public string responseModel;
    public string assumptions;
    public float controlPeriodSeconds;
    public float[] cylinderOffsetM;
    public float[] rawMinimumMm;
    public float[] rawMaximumMm;
    public float swingZeroSensorDeg;
    public float swingSensorSign;
    public float swingMinimumDeg;
    public float swingMaximumDeg;
    public float swingMaximumReferenceRadS;
    public float manualDeadzone;
    public float[] proportionalGain;
    public float[] positiveFeedforward;
    public float[] negativeFeedforward;
    public float[] positiveValveStartDeg;
    public float[] positiveValveEndDeg;
    public float[] negativeValveStartDeg;
    public float[] negativeValveEndDeg;
    public float[] maximumPositivePhysicalSpeed;
    public float[] maximumNegativePhysicalSpeed;
    public float swingMaximumPercent;
    public float pumpPercent;
    public int pwmOscillatorHz;
    public int pwmFrequencyHz;

    /// <summary>Frozen reference fixture; production experiment loads its JSON asset.</summary>
    public static RealExecutionProfile DefaultCandidate()
    {
      return new RealExecutionProfile {
        schema = "real_execution_profile_v1",
        executionVersion = "f407_20261105_candidate_auto_map",
        responseModel = "CONDITIONAL_LINEAR_PROXY",
        assumptions = "Inverse-feedforward opening gain; no physical minimum-speed, lag, load or PWM-response identification. Zero randomization.",
        controlPeriodSeconds = 0.05f,
        cylinderOffsetM = new[] { 0.1615f, 0.23725f, 0.0617f },
        rawMinimumMm = new[] { 80f, 60f, 60f },
        rawMaximumMm = new[] { 190f, 220f, 160f },
        swingZeroSensorDeg = 0f, swingSensorSign = 1f,
        swingMinimumDeg = -100f, swingMaximumDeg = 100f,
        swingMaximumReferenceRadS = 0.34f, manualDeadzone = 0.15f,
        proportionalGain = new[] { 0.003f, 0.003f, 0.003f, 0.01f },
        positiveFeedforward = new[] { 1f/30f, 1f/60f, 1f/61f, 1f/50f },
        negativeFeedforward = new[] { 1f/30f, 1f/42.5f, 1f/36.5f, 1f/50f },
        positiveValveStartDeg = new[] { 110f,105f,105f },
        positiveValveEndDeg = new[] { 115f,110f,110f },
        negativeValveStartDeg = new[] { 78f,75f,76f },
        negativeValveEndDeg = new[] { 73f,69f,71f },
        maximumPositivePhysicalSpeed = new[] { 0.0351f,0.0444f,0.0342f,0.6f },
        maximumNegativePhysicalSpeed = new[] { 0.0185f,0.0357f,0.0419f,0.6f },
        swingMaximumPercent = 20f, pumpPercent = -30f,
        pwmOscillatorHz = 27443200, pwmFrequencyHz = 50
      };
    }

    public void Validate()
    {
      if(schema != "real_execution_profile_v1" || string.IsNullOrEmpty(executionVersion) ||
          responseModel != "CONDITIONAL_LINEAR_PROXY")
        throw new ArgumentException("Unknown execution profile/version/response model.");
      CheckArray(cylinderOffsetM,3,"cylinderOffsetM");
      CheckArray(rawMinimumMm,3,"rawMinimumMm");
      CheckArray(rawMaximumMm,3,"rawMaximumMm");
      CheckArray(proportionalGain,4,"proportionalGain",false);
      CheckArray(positiveFeedforward,4,"positiveFeedforward",true);
      CheckArray(negativeFeedforward,4,"negativeFeedforward",true);
      CheckArray(positiveValveStartDeg,3,"positiveValveStartDeg");
      CheckArray(positiveValveEndDeg,3,"positiveValveEndDeg");
      CheckArray(negativeValveStartDeg,3,"negativeValveStartDeg");
      CheckArray(negativeValveEndDeg,3,"negativeValveEndDeg");
      CheckArray(maximumPositivePhysicalSpeed,4,"maximumPositivePhysicalSpeed",true);
      CheckArray(maximumNegativePhysicalSpeed,4,"maximumNegativePhysicalSpeed",true);
      CheckFinite(controlPeriodSeconds,"controlPeriodSeconds");
      CheckFinite(swingZeroSensorDeg,"swingZeroSensorDeg");
      CheckFinite(swingMinimumDeg,"swingMinimumDeg");
      CheckFinite(swingMaximumDeg,"swingMaximumDeg");
      CheckFinite(swingMaximumReferenceRadS,"swingMaximumReferenceRadS");
      CheckFinite(manualDeadzone,"manualDeadzone");
      CheckFinite(swingMaximumPercent,"swingMaximumPercent");
      CheckFinite(pumpPercent,"pumpPercent");
      // V1 reproduces the frozen +1 swing convention only. A negative sensor
      // sign would also need explicit reference/feedback conversion in Step.
      if(controlPeriodSeconds <= 0f || swingSensorSign != 1f ||
          swingMinimumDeg >= swingMaximumDeg || swingMinimumDeg < -180f || swingMaximumDeg > 180f ||
          swingMaximumReferenceRadS <= 0f || manualDeadzone < 0f || manualDeadzone >= 1f ||
          swingMaximumPercent <= 0f || swingMaximumPercent > 100f || Math.Abs(pumpPercent) > 100f ||
          pwmOscillatorHz <= 0 || pwmFrequencyHz <= 0)
        throw new ArgumentException("Invalid execution scalar range.");
      for(int axis=0; axis<3; axis++) {
        if(rawMinimumMm[axis] >= rawMaximumMm[axis] ||
            positiveValveStartDeg[axis] <= 90f || positiveValveEndDeg[axis] < positiveValveStartDeg[axis] || positiveValveEndDeg[axis] > 180f ||
            negativeValveStartDeg[axis] >= 90f || negativeValveEndDeg[axis] > negativeValveStartDeg[axis] || negativeValveEndDeg[axis] < 0f)
          throw new ArgumentException("Invalid cylinder limits or valve endpoints.");
      }
    }

    internal RealExecutionProfile Snapshot()
    {
      Validate();
      var copy = (RealExecutionProfile)MemberwiseClone();
      copy.cylinderOffsetM = (float[])cylinderOffsetM.Clone();
      copy.rawMinimumMm = (float[])rawMinimumMm.Clone(); copy.rawMaximumMm = (float[])rawMaximumMm.Clone();
      copy.proportionalGain = (float[])proportionalGain.Clone();
      copy.positiveFeedforward = (float[])positiveFeedforward.Clone(); copy.negativeFeedforward = (float[])negativeFeedforward.Clone();
      copy.positiveValveStartDeg = (float[])positiveValveStartDeg.Clone(); copy.positiveValveEndDeg = (float[])positiveValveEndDeg.Clone();
      copy.negativeValveStartDeg = (float[])negativeValveStartDeg.Clone(); copy.negativeValveEndDeg = (float[])negativeValveEndDeg.Clone();
      copy.maximumPositivePhysicalSpeed = (float[])maximumPositivePhysicalSpeed.Clone();
      copy.maximumNegativePhysicalSpeed = (float[])maximumNegativePhysicalSpeed.Clone();
      return copy;
    }

    internal static void CheckArray(float[] values,int length,string name,bool? positive = null)
    {
      if(values == null || values.Length != length) throw new ArgumentException(name + " must have length " + length);
      foreach(float value in values) {
        CheckFinite(value,name);
        if(positive.HasValue && (positive.Value ? value <= 0f : value < 0f))
          throw new ArgumentException(name + " contains an invalid gain/speed.");
      }
    }

    internal static void CheckFinite(float value,string name)
    {
      if(float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentException(name + " must be finite.");
    }
  }
}
