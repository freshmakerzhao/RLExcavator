using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.JevTests
{
  public sealed class JevTestTerrainTests
  {
    private static Type TerrainType => Type.GetType("RLExcavator.JevTestTerrain, Assembly-CSharp", true);

    [TestCase(.82f, .40f, .12f)]
    [TestCase(.80f, 0f, .14f)]
    [TestCase(.72f, -.40f, .16f)]
    [TestCase(1.1f, -.40f, 0f)]
    public void ThreeActionBandsHaveTheDeclaredRelativeHeights(float x, float y, float expected)
    {
      var method = TerrainType.GetMethod("HeightDelta", BindingFlags.Public | BindingFlags.Static);
      var delta = (float)method.Invoke(null, new object[] { new Vector2(x, y) });
      Assert.That(delta, Is.EqualTo(expected).Within(1e-6));
    }

    [TestCase(.72f, .71f, 0f)]
    [TestCase(.72f, -.71f, 0f)]
    [TestCase(.41f, -.40f, 0f)]
    [TestCase(1.03f, -.40f, 0f)]
    public void OutsideStockpilesAndPoolFrontRemainUnmodified(float x, float y, float expected)
    {
      var method = TerrainType.GetMethod("HeightDelta", BindingFlags.Public | BindingFlags.Static);
      var delta = (float)method.Invoke(null, new object[] { new Vector2(x, y) });
      Assert.That(delta, Is.EqualTo(expected).Within(1e-6));
    }

    [Test]
    public void PreviewConvertsTerrainLocalVerticesThroughTheMachineFrame()
    {
      var machine = new GameObject("jev-test-machine");
      var terrain = new GameObject("jev-test-soil");
      try {
        machine.transform.SetPositionAndRotation(new Vector3(2, 1, 3), Quaternion.Euler(0, 35, 0));
        terrain.transform.SetPositionAndRotation(new Vector3(-1, .35f, 4), Quaternion.Euler(0, -20, 0));
        var targetWorld = machine.transform.TransformPoint(new Vector3(-.40f, -.1f, .82f));
        var vertex = terrain.transform.InverseTransformPoint(targetWorld);
        var method = TerrainType.GetMethod("HeightDeltaAtVertex", BindingFlags.Public | BindingFlags.Static);
        var delta = (float)method.Invoke(null, new object[] { terrain.transform, machine.transform, vertex });
        Assert.That(delta, Is.EqualTo(.12f).Within(1e-6));
      }
      finally {
        UnityEngine.Object.DestroyImmediate(machine);
        UnityEngine.Object.DestroyImmediate(terrain);
      }
    }

    [TestCase("Assets/JevTest/JevTestSoilSurface.asset", true)]
    [TestCase("Assets/Environment/RuntimeSoilSurface.asset", false)]
    [TestCase("Assets/JevTestOther/Soil.asset", false)]
    [TestCase("Assets/JevTest/../Environment/Soil.asset", false)]
    [TestCase("", false)]
    public void PreviewRejectsSharedOrUnownedMeshAssets(string path, bool expected)
    {
      var method = TerrainType.GetMethod("IsOwnedMeshAssetPath", BindingFlags.Public | BindingFlags.Static);
      Assert.That(method.Invoke(null, new object[] { path }), Is.EqualTo(expected));
    }
  }
}
