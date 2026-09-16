using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class E4GeometryInitializerTests
  {
    private static object Call(string method, params object[] args)
    {
      Type type = AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType("RLExcavator.E4GeometryInitializer")).FirstOrDefault(t => t != null);
      Assert.That(type, Is.Not.Null, "Geometry initialization helper must exist");
      return type.GetMethod(method).Invoke(null, args);
    }

    [Test]
    public void IdentityJacobianMovesTowardCartesianError()
    {
      Vector4 error = new Vector4(.1f, -.2f, .05f, .1f);
      var step = (Vector4)Call("DampedStep", Matrix4x4.identity, error, .01f);
      Assert.That((step - error).magnitude, Is.LessThan(.0001f));
    }

    [Test]
    public void RankDeficientJacobianIsRegularized()
    {
      var step = (Vector4)Call("DampedStep", Matrix4x4.zero, Vector4.one, .025f);
      Assert.That(step, Is.EqualTo(Vector4.zero));
    }

    [Test]
    public void StepLimitsPreserveDirection()
    {
      var step = (Vector4)Call("BoundStep", new Vector4(1f, -.5f, 0f, 2f));
      Assert.That(Mathf.Abs(step.x), Is.LessThanOrEqualTo(.25f));
      Assert.That(Mathf.Abs(step.w), Is.LessThanOrEqualTo(.35f));
      Assert.That(step.y / step.x, Is.EqualTo(-.5f).Within(1e-6f));
    }

    [Test]
    public void PitchResidualUsesShortestAngle()
    {
      var residual = (Vector4)Call("Residual", Vector3.zero, 179f, Vector3.zero, -179f);
      Assert.That(residual.w, Is.EqualTo(2f * Mathf.Deg2Rad * .25f).Within(1e-6f));
    }
  }
}
