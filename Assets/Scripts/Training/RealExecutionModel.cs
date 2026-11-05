using System;

namespace RLExcavator
{
  /// <summary>
  /// Software-chain replica plus an explicitly conditional linear response proxy.
  /// Call at the configured control period; no motor scheduling or physical reset here.
  /// Axis order is boom, stick, bucket, swing. Physical cylinder signs are extension-positive.
  /// </summary>
  public sealed class RealExecutionModel
  {
    private const float RadToDeg = 57.2957795f;
    private readonly RealExecutionProfile profile;
    private readonly RealExecutionMapping mapping;

    public RealExecutionModel(RealExecutionProfile profile,
                              RealExecutionMapping mapping = RealExecutionMapping.CandidateAutomatic)
    {
      if(profile == null) throw new ArgumentNullException(nameof(profile));
      if(!Enum.IsDefined(typeof(RealExecutionMapping),mapping)) throw new ArgumentException("Unknown mapping.");
      this.profile = profile.Snapshot();
      this.mapping = mapping;
    }

    /// <param name="reference">m/s for cylinders, rad/s for swing; policy deadzone already applied.</param>
    /// <param name="position">Training cylinder q in m, model swing radians.</param>
    /// <param name="measuredSpeed">Actual AGX cylinder m/s and swing rad/s, never last commanded speeds.</param>
    public RealExecutionResult Step(float[] reference,float[] position,float[] measuredSpeed)
    {
      RealExecutionProfile.CheckArray(reference,4,nameof(reference));
      RealExecutionProfile.CheckArray(position,4,nameof(position));
      RealExecutionProfile.CheckArray(measuredSpeed,4,nameof(measuredSpeed));
      var raw = RawPosition(position);
      float rawSwing = WrapDegrees(profile.swingZeroSensorDeg +
                                    position[3]*RadToDeg/profile.swingSensorSign);
      var target = ToValveUnits(reference,true);
      var measured = ToValveUnits(measuredSpeed,false);
      var limitedTarget = Gate(target,raw,rawSwing);
      var requestedEffort = new float[4];
      for(int axis=0; axis<4; axis++) {
        float desired = limitedTarget[axis];
        float gain = desired > 0f ? profile.positiveFeedforward[axis] : profile.negativeFeedforward[axis];
        requestedEffort[axis] = desired == 0f ? 0f : Clamp(desired*gain +
          profile.proportionalGain[axis]*(desired-measured[axis]),-1f,1f);
      }
      var effort = Gate(requestedEffort,raw,rawSwing);
      var opening = new float[4];
      int neutralMask = 0;
      for(int axis=0; axis<4; axis++) {
        opening[axis] = EffectiveOpening(effort[axis],axis);
        if(effort[axis] != 0f && opening[axis] == 0f) neutralMask |= 1 << axis;
      }
      var angles = ValveAngles(opening);
      float swingPercent = opening[3]*profile.swingMaximumPercent;
      float pumpPercent = opening[0] != 0f || opening[1] != 0f || opening[2] != 0f ? profile.pumpPercent : 0f;
      var pwm = new[] {AngleCount(angles[0]),AngleCount(angles[1]),AngleCount(angles[2]),
                       SpeedCount(swingPercent),SpeedCount(pumpPercent)};
      return new RealExecutionResult(reference,raw,rawSwing,target,FromValveUnits(limitedTarget),
        requestedEffort,effort,opening,angles,swingPercent,pumpPercent,pwm,Response(opening),
        ChangedMask(target,limitedTarget) | ChangedMask(requestedEffort,effort),neutralMask);
    }

    private float[] RawPosition(float[] position)
    {
      var raw = new float[3];
      for(int i=0; i<3; i++) {
        float mm = (position[i]+profile.cylinderOffsetM[i])*1000f;
        RealExecutionProfile.CheckFinite(mm,"raw cylinder position");
        // The firmware receives integer hundredths of a millimetre.
        raw[i] = (float)Math.Round(mm*100d,MidpointRounding.AwayFromZero)/100f;
      }
      return raw;
    }

    private float[] ToValveUnits(float[] physical,bool clampSwing)
    {
      float swing = clampSwing ? Clamp(physical[3],-profile.swingMaximumReferenceRadS,
                                       profile.swingMaximumReferenceRadS) : physical[3];
      var values = new[] {physical[0]*1000f,physical[1]*1000f,-physical[2]*1000f,swing*RadToDeg};
      RealExecutionProfile.CheckArray(values,4,"valve units");
      return values;
    }

    private static float[] FromValveUnits(float[] values) =>
      new[] { values[0]/1000f,values[1]/1000f,-values[2]/1000f,values[3]/RadToDeg };

    private float[] Gate(float[] request,float[] raw,float rawSwing)
    {
      var result = (float[])request.Clone();
      for(int axis=0; axis<3; axis++) {
        float physicalDirection = axis == 2 ? -request[axis] : request[axis];
        if((raw[axis] >= profile.rawMaximumMm[axis] && physicalDirection > 0f) ||
           (raw[axis] <= profile.rawMinimumMm[axis] && physicalDirection < 0f)) result[axis] = 0f;
      }
      if((rawSwing >= profile.swingMaximumDeg && request[3] > 0f) ||
         (rawSwing <= profile.swingMinimumDeg && request[3] < 0f)) result[3] = 0f;
      return result;
    }

    private float EffectiveOpening(float effort,int axis)
    {
      if(mapping == RealExecutionMapping.CandidateAutomatic) return effort;
      if(Math.Abs(effort) <= profile.manualDeadzone) return 0f;
      // ESC uses the clipped effort directly, not the valve deadzone rescaling.
      if(axis == 3) return effort;
      return Math.Sign(effort)*(Math.Abs(effort)-profile.manualDeadzone)/(1f-profile.manualDeadzone);
    }

    private float[] ValveAngles(float[] opening)
    {
      var angles = new float[3];
      for(int axis=0; axis<3; axis++) {
        float value = opening[axis];
        if(value == 0f) angles[axis] = 90f;
        else if(value > 0f) angles[axis] = profile.positiveValveStartDeg[axis] + value*
          (profile.positiveValveEndDeg[axis]-profile.positiveValveStartDeg[axis]);
        else angles[axis] = profile.negativeValveStartDeg[axis] + (-value)*
          (profile.negativeValveEndDeg[axis]-profile.negativeValveStartDeg[axis]);
      }
      return angles;
    }

    private float[] Response(float[] opening)
    {
      var speed = new float[4];
      for(int axis=0; axis<4; axis++) {
        float value = opening[axis];
        if(value == 0f) continue;
        float gain = value > 0f ? profile.positiveFeedforward[axis] : profile.negativeFeedforward[axis];
        float physical = value/gain/(axis == 3 ? RadToDeg : 1000f);
        if(axis == 2) physical = -physical;
        speed[axis] = Clamp(physical,-profile.maximumNegativePhysicalSpeed[axis],
                            profile.maximumPositivePhysicalSpeed[axis]);
      }
      return speed;
    }

    private int AngleCount(float angle) => PulseCount((int)(500f+2000f*Clamp(angle,0f,180f)/180f+0.5f));
    private int SpeedCount(float percent) => PulseCount((int)(1500f+1000f*Clamp(percent,-100f,100f)/100f+0.5f));

    private int PulseCount(int pulseUs)
    {
      long divisor = 4096L*profile.pwmFrequencyHz;
      long ratio = (profile.pwmOscillatorHz+divisor/2L)/divisor;
      long prescale = Math.Max(0L,Math.Min(255L,ratio-1L));
      long numerator = (long)pulseUs*profile.pwmOscillatorHz;
      long denominator = 1000000L*(prescale+1L);
      return (int)Math.Min(4095L,(numerator+denominator/2L)/denominator);
    }

    private static int ChangedMask(float[] requested,float[] limited)
    {
      int result=0;
      for(int i=0;i<4;i++) if(requested[i] != limited[i]) result |= 1 << i;
      return result;
    }

    private static float WrapDegrees(float value)
    {
      RealExecutionProfile.CheckFinite(value,"raw swing angle");
      value %= 360f;
      return value > 180f ? value-360f : (value < -180f ? value+360f : value);
    }

    private static float Clamp(float value,float minimum,float maximum) => Math.Max(minimum,Math.Min(maximum,value));
  }

  /// <summary>Snapshot arrays are copied on input and access; PWM order is boom/stick/bucket/swing/pump.</summary>
  public sealed class RealExecutionResult
  {
    private readonly float[][] vectors;
    private readonly int[] pwm;
    public float[] OriginalReference => (float[])vectors[0].Clone();
    public float[] RawPositionMm => (float[])vectors[1].Clone();
    public float[] RequestedValveTarget => (float[])vectors[2].Clone();
    public float[] LimitedPhysicalReference => (float[])vectors[3].Clone();
    public float[] PidEffort => (float[])vectors[4].Clone();
    public float[] LimitedEffort => (float[])vectors[5].Clone();
    public float[] OpeningFraction => (float[])vectors[6].Clone();
    public float[] ValveAnglesDeg => (float[])vectors[7].Clone();
    public float[] ProxyPhysicalSpeed => (float[])vectors[8].Clone();
    public int[] PwmCounts => (int[])pwm.Clone();
    public float RawSwingDegrees { get; }
    public float SwingPercent { get; }
    public float PumpPercent { get; }
    public int LimitMask { get; }
    public int NeutralMask { get; }

    internal RealExecutionResult(float[] original,float[] raw,float rawSwing,float[] requested,
      float[] limited,float[] pid,float[] effort,float[] opening,float[] angles,float swingPercent,
      float pumpPercent,int[] pwm,float[] response,int mask,int neutralMask)
    {
      var input = new[] {original,raw,requested,limited,pid,effort,opening,angles,response};
      vectors = Array.ConvertAll(input,v => (float[])v.Clone());
      this.pwm = (int[])pwm.Clone();
      RawSwingDegrees=rawSwing; SwingPercent=swingPercent; PumpPercent=pumpPercent;
      LimitMask=mask; NeutralMask=neutralMask;
    }
  }
}
