using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class SideCameraSceneTests
  {
    [Test]
    public void SideCamerasAreIndependentOnDemandViewsOnSwingBody()
    {
      var scene = EditorSceneManager.OpenPreviewScene("Assets/Scenes/ExcavatorRuntimeLidarPlacements.unity");
      try {
        var cameras = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Camera>(true)).ToArray();
        var front = cameras.Single(c => c.name == "FrontCamera");
        Assert.That(front.fieldOfView, Is.EqualTo(65f));
        foreach (string name in new[] { "SideCameraLeft", "SideCameraRight" }) {
          var side = cameras.SingleOrDefault(c => c.name == name);
          Assert.That(side, Is.Not.Null, name + " must be installed");
          Assert.That(side.transform.parent, Is.SameAs(front.transform.parent));
          Assert.That(side.transform.parent.name, Is.EqualTo("Body_Swing"));
          Assert.That(side.enabled, Is.False, "Auxiliary images render only when requested");
          Assert.That(side.CompareTag("MainCamera"), Is.False);
          Assert.That(side.GetComponent<AudioListener>(), Is.Null);
          Assert.That(side.cullingMask, Is.EqualTo(front.cullingMask), "Exclude the RL overlay as in policy camera");
          Assert.That(side.fieldOfView, Is.EqualTo(75f));
        }
        Assert.That(cameras.Single(c => c.name == "SideCameraLeft").transform.position.x, Is.LessThan(0f));
        Assert.That(cameras.Single(c => c.name == "SideCameraRight").transform.position.x, Is.GreaterThan(0f));
        var bridge = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<MonoBehaviour>(true))
          .Single(c => c != null && c.GetType().Name == "ExcavatorSimBridge");
        Assert.That(bridge.GetType().GetField("frontCamera").GetValue(bridge), Is.SameAs(front));
      }
      finally { EditorSceneManager.ClosePreviewScene(scene); }
    }
  }
}
