using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace RLExcavator.Tests
{
  public sealed class RealExecutionModelTests
  {
    private static readonly float[] Interior = { -0.02f, -0.06f, 0.04f, 0f };

    [Test]
    public void ZeroReferenceRemainsNeutralDespiteMeasuredMotion()
    {
      object result = Step( new float[4], Interior, new[] { 0.02f, -0.03f, 0.01f, 0.2f } );
      Assert.That( Values(result, "LimitedEffort"), Is.EqualTo(new float[4]) );
      Assert.That( Values(result, "ProxyPhysicalSpeed"), Is.EqualTo(new float[4]) );
      Assert.That( Values(result, "ValveAnglesDeg"), Is.EqualTo(new[] {90f, 90f, 90f}) );
    }

    [Test]
    public void CandidatePreservesSmallEffortAndLegacyMappingCutsItToNeutral()
    {
      var reference = new[] { 0.001f, 0.001f, 0.001f, 0.01f };
      object candidate = Step(reference, Interior, new float[4]);
      object legacy = Step(reference, Interior, new float[4], "LegacyManualDeadzone");
      Assert.That( Values(candidate, "ProxyPhysicalSpeed").All(x => x > 0f), Is.True );
      Assert.That( Values(legacy, "ProxyPhysicalSpeed"), Is.EqualTo(new float[4]) );
      Assert.That( Values(candidate, "ValveAnglesDeg")[2], Is.LessThan(90f), "Bucket extension uses negative valve effort exactly once." );
      Assert.That( Values(legacy, "ValveAnglesDeg"), Is.EqualTo(new[] {90f, 90f, 90f}) );
    }

    [Test]
    public void RawPositionUpperAndLowerGatesBlockOnlyOutwardRequests()
    {
      var upper = new[] { 0.0285f, -0.01725f, 0.0983f, 100f*(float)Math.PI/180f };
      var lower = new[] { -0.0815f, -0.17725f, -0.0017f, -100f*(float)Math.PI/180f };
      var positive = new[] { 0.01f, 0.01f, 0.01f, 0.2f };
      var negative = positive.Select(x => -x).ToArray();
      Assert.That( Values(Step(positive,upper,new float[4]),"LimitedPhysicalReference"), Is.EqualTo(new float[4]) );
      Assert.That( Values(Step(negative,lower,new float[4]),"LimitedPhysicalReference"), Is.EqualTo(new float[4]) );
      Assert.That( Values(Step(negative,upper,new float[4]),"LimitedPhysicalReference"), Is.EqualTo(negative) );
      Assert.That( Values(Step(positive,lower,new float[4]),"LimitedPhysicalReference"), Is.EqualTo(positive) );
    }

    [Test]
    public void SecondGateStopsOutwardPidWhenInwardReferenceIsOverspeeding()
    {
      object result = Step(new[] {-0.001f,0f,0f,0f},
        new[] {0.0285f,-0.06f,0.04f,0f}, new[] {-0.03f,0f,0f,0f});
      Assert.That( Values(result,"PidEffort")[0], Is.GreaterThan(0f) );
      Assert.That( Values(result,"LimitedEffort")[0], Is.Zero );
      Assert.That( Integer(result,"LimitMask") & 1, Is.EqualTo(1) );
    }

    [Test]
    public void SwingUsesClampedPhysicalReferenceAndWrappedRawReference()
    {
      object result = Step(new[] {0f,0f,0f,0.6f}, Interior, new float[4]);
      Assert.That( Values(result,"LimitedPhysicalReference")[3], Is.EqualTo(0.34f).Within(1e-6f) );
      Assert.That( Values(result,"RequestedValveTarget")[3], Is.EqualTo(0.34f*180f/(float)Math.PI).Within(1e-5f) );
      object blocked = Step(new[] {0f,0f,0f,-0.2f}, new[] {-0.02f,-0.06f,0.04f,250f*(float)Math.PI/180f}, new float[4]);
      Assert.That( Values(blocked,"LimitedPhysicalReference")[3], Is.Zero );
    }

    [Test]
    public void PhysicalBucketFeedbackAndAsymmetricFeedforwardUseValveSign()
    {
      object result = Step(new[] {0f,0f,0.01f,0f}, Interior, new[] {0f,0f,0.01f,0f});
      Assert.That( Values(result,"PidEffort")[2], Is.EqualTo(-10f/36.5f).Within(1e-6f) );
      Assert.That( Values(result,"ProxyPhysicalSpeed")[2], Is.EqualTo(0.01f).Within(1e-6f) );
    }

    [Test]
    public void InputArraysAndResultArraysDoNotMutateTheModel()
    {
      var input = new[] {0.01f,0f,0f,0f};
      object result = Step(input, Interior, new float[4]);
      input[0] = 0.5f;
      float[] returned = Values(result,"OriginalReference");
      returned[0] = 0.4f;
      Assert.That( Values(result,"OriginalReference")[0], Is.EqualTo(0.01f) );
    }

    [Test]
    public void NonFiniteAndWrongShapeInputsFailExplicitly()
    {
      Assert.Throws<ArgumentException>(() => Step(new[] {float.NaN,0f,0f,0f},Interior,new float[4]));
      Assert.Throws<ArgumentException>(() => Step(new float[3],Interior,new float[4]));
    }

    [Test]
    public void ProfileRejectsUnimplementedNegativeSwingSensorSign()
    {
      Type type = FindType("RLExcavator.RealExecutionProfile");
      object profile = type.GetMethod("DefaultCandidate").Invoke(null,null);
      Assert.DoesNotThrow(() => type.GetMethod("Validate").Invoke(profile,null));
      type.GetField("swingSensorSign").SetValue(profile,-1f);
      var error = Assert.Throws<TargetInvocationException>(() => type.GetMethod("Validate").Invoke(profile,null));
      Assert.That(error.InnerException,Is.TypeOf<ArgumentException>());
    }

    private static object Step(float[] reference, float[] position, float[] measured,
                               string mapping = "CandidateAutomatic")
    {
      Type profileType = FindType("RLExcavator.RealExecutionProfile");
      object profile = profileType.GetMethod("DefaultCandidate").Invoke(null,null);
      Type mappingType = FindType("RLExcavator.RealExecutionMapping");
      object model = Activator.CreateInstance(FindType("RLExcavator.RealExecutionModel"),
        new[] {profile, Enum.Parse(mappingType,mapping)});
      try { return model.GetType().GetMethod("Step").Invoke(model,new object[] {reference,position,measured}); }
      catch(TargetInvocationException ex) { throw ex.InnerException; }
    }

    private static Type FindType(string name)
    {
      Type type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
      Assert.That(type,Is.Not.Null,"Required execution model type: " + name);
      return type;
    }

    private static float[] Values(object result,string name) => (float[])result.GetType().GetProperty(name).GetValue(result);
    private static int Integer(object result,string name) => (int)result.GetType().GetProperty(name).GetValue(result);
  }
}
