using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace RLExcavator.Tests
{
  public sealed class RealExecutionWitnessTests
  {
    [Test]
    public void CoincidentJointGoalProducesExactZero()
    {
      var q = new[] {-0.02f,-0.06f,0.04f,0.3f};
      Assert.That(Compute(q,q),Is.EqualTo(new float[4]));
    }

    [Test]
    public void JointErrorUsesCommonGainAndPhysicalSignsWithoutFloor()
    {
      var q = new[] {-0.02f,-0.06f,0.04f,0.3f};
      var goal = new[] {-0.019f,-0.062f,0.043f,0.26f};
      float[] actual = Compute(q,goal);
      for(int i=0;i<4;i++) Assert.That(actual[i],Is.EqualTo((goal[i]-q[i])*5f).Within(1e-8f));
      Assert.That(q,Is.EqualTo(new[] {-0.02f,-0.06f,0.04f,0.3f}));
    }

    [Test]
    public void ReferenceCapsRespectDirectionAndSwingFirmwareCap()
    {
      Assert.That(Compute(new float[4],new[] {10f,10f,10f,10f}),
        Is.EqualTo(new[] {0.0351f,0.0444f,0.0342f,0.34f}));
      Assert.That(Compute(new float[4],new[] {-10f,-10f,-10f,-10f}),
        Is.EqualTo(new[] {-0.0185f,-0.0357f,-0.0419f,-0.34f}));
    }

    [Test]
    public void ShortSwingGoalExceedsFrozenPolicyDeadzoneInBothDirections()
    {
      // Frozen shared/machine_profile.jsonc V5 swing speed and directional deadzones.
      const float positiveDeadzoneRadS = 0.6f*0.090175344686f;
      const float negativeDeadzoneRadS = 0.6f*0.119264165553f;
      float positive = Compute(new float[4],new[] {0f,0f,0f,0.15f})[3];
      float negative = Compute(new float[4],new[] {0f,0f,0f,-0.15f})[3];
      Assert.That(positive,Is.GreaterThan(positiveDeadzoneRadS));
      Assert.That(-negative,Is.GreaterThan(negativeDeadzoneRadS));
      Assert.That(Math.Abs(positive),Is.LessThanOrEqualTo(0.34f));
      Assert.That(Math.Abs(negative),Is.LessThanOrEqualTo(0.34f));
    }

    [Test]
    public void InvalidShapesAndNonFiniteJointInputsFailExplicitly()
    {
      Assert.Throws<ArgumentException>(() => Compute(new float[3],new float[4]));
      Assert.Throws<ArgumentException>(() => Compute(new float[4],new[] {float.NaN,0f,0f,0f}));
      Assert.Throws<ArgumentException>(() => Compute(new[] {0f,0f,float.PositiveInfinity,0f},new float[4]));
      Assert.Throws<ArgumentException>(() => Compute(new[] {float.MaxValue,0f,0f,0f},new[] {-float.MaxValue,0f,0f,0f}));
    }

    private static float[] Compute(float[] actual,float[] goal)
    {
      Type profileType = FindType("RLExcavator.RealExecutionProfile");
      object profile = profileType.GetMethod("DefaultCandidate").Invoke(null,null);
      Type type = FindType("RLExcavator.RealExecutionWitness");
      try { return (float[])type.GetMethod("Compute").Invoke(null,new[] {(object)actual,goal,profile}); }
      catch(TargetInvocationException ex) { throw ex.InnerException; }
    }

    private static Type FindType(string name)
    {
      Type type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType(name)).FirstOrDefault(t=>t!=null);
      Assert.That(type,Is.Not.Null,"Required witness type: "+name);
      return type;
    }
  }
}
