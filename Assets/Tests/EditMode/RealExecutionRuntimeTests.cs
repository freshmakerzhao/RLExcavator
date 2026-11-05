using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace RLExcavator.Tests
{
  public sealed class RealExecutionRuntimeTests
  {
    [Test]
    public void TwentyHertzControllerKeepsItsPhaseOnFiftyHertzPhysics()
    {
      var type = Type.GetType("RLExcavator.RealExecutionTrainingRuntime, Assembly-CSharp", true);
      var advance = type.GetMethod("AdvanceControlClock", BindingFlags.Public | BindingFlags.Static);
      Assert.That(advance, Is.Not.Null);
      float remainder = 0f;
      var ticks = new List<int>();
      for (int i = 1; i <= 100; ++i) {
        object[] arguments = {remainder, 0.02f, 0.05f};
        if ((bool)advance.Invoke(null, arguments)) ticks.Add(i);
        remainder = (float)arguments[0];
      }
      Assert.That(ticks.Count, Is.EqualTo(40));
      Assert.That(ticks[0], Is.EqualTo(3));
      for (int i = 1; i < ticks.Count; ++i)
        Assert.That(ticks[i] - ticks[i-1], Is.InRange(2, 3));
      Assert.That(remainder, Is.InRange(0f, 0.00001f));
    }

    [Test]
    public void InvalidClockIntervalsCannotTriggerExecution()
    {
      var type = Type.GetType("RLExcavator.RealExecutionTrainingRuntime, Assembly-CSharp", true);
      var advance = type.GetMethod("AdvanceControlClock");
      var error = Assert.Throws<TargetInvocationException>(() =>
        advance.Invoke(null, new object[] {0f, float.NaN, 0.05f}));
      Assert.That(error.InnerException, Is.TypeOf<ArgumentException>());
    }
  }
}
