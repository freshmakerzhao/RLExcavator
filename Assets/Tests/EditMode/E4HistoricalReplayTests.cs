using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class E4HistoricalReplayTests
  {
    private static Type Contract => AppDomain.CurrentDomain.GetAssemblies()
      .Select(a => a.GetType("RLExcavator.E4ReplayContract")).FirstOrDefault(t => t != null);

    private object Invoke(string method, params object[] arguments)
    {
      Assert.That(Contract, Is.Not.Null, "E4 replay import contract must exist");
      return Contract.GetMethod(method).Invoke(null, arguments);
    }

    [Test]
    public void NativeAgxAndUnityPhysicsMustUseSameTimeStep()
    {
      Assert.DoesNotThrow(() => Invoke("RequireMatchingPhysicsPeriods", .01f, .01f));
      var error = Assert.Throws<TargetInvocationException>(() => Invoke("RequireMatchingPhysicsPeriods", .01f, .02f));
      Assert.That(error.InnerException, Is.TypeOf<ArgumentException>());
    }

    [Test]
    public void RecordedTwentyHertzMapsToFivePhysicsSteps()
    {
      Assert.That((int)Invoke("DecisionPeriod", .05f, .01f), Is.EqualTo(5));
    }

    [TestCase(.055f)]
    [TestCase(0f)]
    [TestCase(float.NaN)]
    public void InvalidOrFractionalDecisionPeriodIsRejected(float period)
    {
      var error = Assert.Throws<TargetInvocationException>(() => Invoke("DecisionPeriod", period, .01f));
      Assert.That(error.InnerException, Is.TypeOf<ArgumentException>());
    }

    [TestCase("0.05", true)]
    [TestCase("0.055", false)]
    public void InputParserPreservesOrRejectsRecordedPeriod(string period, bool valid)
    {
      string json = "{\"schema_version\":\"e4_historical_replay.v1\",\"run_id\":\"cadence-test\",\"model_sha256\":\"" +
        new string('a', 64) + "\",\"phase\":\"dig\",\"initial_normalized_q\":[0,0,0]," +
        "\"waypoints_ros\":[{\"x\":0,\"y\":0,\"z\":0},{\"x\":1,\"y\":0,\"z\":0}]," +
        "\"timeout_s\":60,\"waypoint_tolerance_m\":0.25,\"initial_tip_tolerance_m\":0.03," +
        "\"initial_normalized_q_tolerance\":0.03,\"initial_swing_tolerance_rad\":0.03," +
        "\"initial_pitch_tolerance_deg\":2,\"policy_period_s\":" + period + "}";
      if (valid) {
        object parsed = Invoke("Parse", json);
        Assert.That((float)parsed.GetType().GetField("policy_period_s").GetValue(parsed), Is.EqualTo(.05f));
      } else {
        var error = Assert.Throws<TargetInvocationException>(() => Invoke("Parse", json));
        Assert.That(error.InnerException, Is.TypeOf<ArgumentException>());
      }
    }

    [Test]
    public void RosPointUsesMachineRootAxes()
    {
      Assert.That((Vector3)Invoke("RosToUnityLocal", new Vector3(1f, 2f, 3f)),
        Is.EqualTo(new Vector3(-2f, 3f, 1f)));
    }

    [TestCase(-1f, -0.095f)]
    [TestCase(0f, -0.0075f)]
    [TestCase(1f, 0.08f)]
    public void NormalizedObservationMapsToAgxRange(float normalized, float expected)
    {
      Assert.That((float)Invoke("Denormalize", normalized, -0.095f, 0.08f),
        Is.EqualTo(expected).Within(1e-6f));
    }

    [TestCase(1.01f)]
    [TestCase(float.NaN)]
    public void InvalidInitialObservationIsNotClamped(float normalized)
    {
      var error = Assert.Throws<TargetInvocationException>(() => Invoke("Denormalize", normalized, -0.095f, 0.08f));
      Assert.That(error.InnerException, Is.TypeOf<ArgumentException>());
    }

    [Test]
    public void FullReferenceOrderIsPreserved()
    {
      Vector3[] source = { new Vector3(1f, 0f, 0f), new Vector3(2f, 1f, 0f), new Vector3(3f, 0f, 1f) };
      var converted = (Vector3[])Invoke("ConvertPath", source);
      Assert.That(converted.Length, Is.EqualTo(3));
      Assert.That(converted[1], Is.EqualTo(new Vector3(-1f, 0f, 2f)));
      Assert.That(converted[2], Is.EqualTo(new Vector3(0f, 1f, 3f)));
      Assert.That(source[1], Is.EqualTo(new Vector3(2f, 1f, 0f)));
    }

    [Test]
    public void InstallingHistorySurvivesEpisodeResetAndReplacesPendingPath()
    {
      Type providerType = AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType("RLExcavator.TrajectoryProvider")).First(t => t != null);
      var root = new GameObject("e4-import-contract");
      try {
        var provider = root.AddComponent(providerType);
        providerType.GetMethod("QueuePlannedPath").Invoke(provider, new object[] {
          new[] { Vector3.zero, new Vector3(99f, 99f, 99f) }
        });
        Vector3[] history = { Vector3.zero, new Vector3(1f, 2f, 3f), new Vector3(4f, 5f, 6f) };
        Invoke("InstallPath", provider, history);
        providerType.GetMethod("ResetPath").Invoke(provider, null);
        Assert.That(providerType.GetProperty("TargetCount").GetValue(provider), Is.EqualTo(3));
        Assert.That((Vector3)providerType.GetMethod("GetWaypointPosition").Invoke(provider, new object[] { 2 }),
          Is.EqualTo(history[2]));
      } finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    [Test]
    public void EmptyPathIsRejected()
    {
      var error = Assert.Throws<TargetInvocationException>(() => Invoke("ConvertPath", (object)new Vector3[0]));
      Assert.That(error.InnerException, Is.TypeOf<ArgumentException>());
    }
  }
}
