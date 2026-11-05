using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public sealed class RealExecutionPhysicsSamplerTests
  {
    private static Type Sampler => Type.GetType(
      "RLExcavator.RealExecutionPhysicsSampler, Assembly-CSharp", true );

    private static bool Valid( Vector4 requested, Vector4 actual, Vector4 velocity, Vector3 tip )
      => (bool)Sampler.GetMethod( "SampleMeetsChecks", BindingFlags.Public | BindingFlags.Static )
        .Invoke( null, new object[] { requested, actual, velocity, tip } );

    [Test]
    public void SampleRequiresSettledJointsAndGroundClearance()
    {
      Vector4 q = new Vector4( -.02f, -.05f, .03f, .1f );
      Assert.That( Valid( q, q, Vector4.zero, Vector3.up * .2f ), Is.True );
      Assert.That( Valid( q, q + new Vector4(.003f, 0f, 0f, 0f), Vector4.zero,
        Vector3.up * .2f ), Is.False );
      Assert.That( Valid( q, q, new Vector4(.003f, 0f, 0f, 0f), Vector3.up * .2f ), Is.False );
      Assert.That( Valid( q, q, Vector4.zero, Vector3.up * .04f ), Is.False );
    }

    [Test]
    public void SampleDoesNotAcceptNonfiniteMeasurements()
    {
      Vector4 q = new Vector4( -.02f, -.05f, .03f, .1f );
      Assert.That( Valid( q, new Vector4(float.NaN, -.05f, .03f, .1f), Vector4.zero,
        Vector3.up * .2f ), Is.False );
      Assert.That( Valid( q, q, Vector4.zero, new Vector3(0f, float.PositiveInfinity, 0f) ), Is.False );
    }

    [Test]
    public void BucketScanContainsOriginalAndOnlyLegalInteriorTargets()
    {
      var values = (float[])Sampler.GetMethod( "BucketScanTargets", BindingFlags.Public | BindingFlags.Static )
        .Invoke( null, new object[] { .031f } );
      Assert.That( values[0], Is.EqualTo(.031f) );
      Assert.That( values.Length, Is.LessThanOrEqualTo(11) );
      foreach (float value in values)
        Assert.That( value, Is.InRange(.0013f, .082f) );
    }

    [Test]
    public void DiagnosticGridCoversTheDeclaredJointDomainWithoutPolicyFiltering()
    {
      var values = (Vector4[])Sampler.GetMethod("DiagnosticGrid", BindingFlags.Public | BindingFlags.Static)
        .Invoke(null, null);
      Assert.That(values.Length, Is.EqualTo(120));
      Assert.That(values[0], Is.EqualTo(new Vector4(-.07f, -.085f, .0013f, 0f)));
      Assert.That(values[119], Is.EqualTo(new Vector4(.025f, -.022f, .082f, 0f)));
      foreach (Vector4 value in values) {
        Assert.That(value.x, Is.InRange(-.077f, .0255f));
        Assert.That(value.y, Is.InRange(-.087f, -.02025f));
        Assert.That(value.z, Is.InRange(.0013f, .082f));
        Assert.That(value.w, Is.Zero);
      }
    }

    [Test]
    public void DiagnosticHeightIsIndependentOfJointSettling()
    {
      Vector4 q = new Vector4(-.02f, -.05f, .03f, .1f);
      Vector3 tip = Vector3.up * .2f;
      Assert.That(Valid(q, q + new Vector4(.003f, 0f, 0f, 0f), Vector4.zero, tip), Is.False);
      Assert.That((bool)Sampler.GetMethod("TipAbovePlane").Invoke(null, new object[] { tip }), Is.True);
      Assert.That((bool)Sampler.GetMethod("TipAbovePlane").Invoke(null, new object[] { Vector3.up * .04f }), Is.False);
    }

    [Test]
    public void DiagnosticFiniteCheckIncludesPitchAndAllTipCoordinates()
    {
      MethodInfo check = Sampler.GetMethod("FiniteTipAndPitch");
      Assert.That((bool)check.Invoke(null, new object[] { Vector3.up * .04f, -179f }), Is.True);
      Assert.That((bool)check.Invoke(null, new object[] { Vector3.up, float.NaN }), Is.False);
      Assert.That((bool)check.Invoke(null, new object[] { new Vector3(float.NaN, .2f, 0f), 70f }), Is.False);
    }
  }
}
