using System;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class ExcavatorReferencePathTests
  {
    [Test]
    public void DisplaysOnlyTheSuppliedReferenceVerticesInMachineRootCoordinates()
    {
      Type type = Type.GetType("RLExcavator.ExcavatorReferencePath, Assembly-CSharp");
      Assert.That(type, Is.Not.Null);
      var go = new GameObject("reference-path-test");
      var root = new GameObject("machine-root-test");
      var camera = new GameObject("overview-test").AddComponent<Camera>();
      try {
        root.transform.SetPositionAndRotation(new Vector3(2, 3, 4), Quaternion.Euler(0, 40, 0));
        var component = go.AddComponent(type);
        var points = new[] { new Vector3(.6f, .1f, .2f), new Vector3(.9f, -.2f, .1f) };
        type.GetMethod("SetPath").Invoke(component, new object[] { points, root.transform, camera });
        var line = go.GetComponent<LineRenderer>();
        Assert.That(line.positionCount, Is.EqualTo(2));
        for (int i = 0; i < points.Length; i++) {
          var p = points[i];
          Assert.That(Vector3.Distance(line.GetPosition(i),
            root.transform.TransformPoint(new Vector3(-p.y, p.z, p.x))), Is.LessThan(1e-6));
        }
        Assert.That(line.enabled, Is.False, "Only the rendering camera callback may enable the line");
        type.GetMethod("Clear").Invoke(component, null);
        Assert.That(line.positionCount, Is.Zero);
      }
      finally {
        UnityEngine.Object.DestroyImmediate(go);
        UnityEngine.Object.DestroyImmediate(root);
        UnityEngine.Object.DestroyImmediate(camera.gameObject);
      }
    }
  }
}
