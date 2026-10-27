using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class ExcavatorRuntimeManualActionTests
  {
    [Test]
    public void Cat365SceneUsesNativeAssetAndIndependentRuntimeBindings()
    {
      var scene = EditorSceneManager.OpenPreviewScene("Assets/Scenes/ExcavatorRuntimeCAT365.unity");
      try {
        var machine = scene.GetRootGameObjects().Single(root => root.name == "CAT365_Official");
        Assert.That(AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromSource(machine)),
          Does.StartWith("Packages/com.algoryx.agxunity.machines.cat365/"));
        Assert.That(machine.transform.lossyScale, Is.EqualTo(Vector3.one));
        var components = machine.GetComponentsInChildren<MonoBehaviour>(true);
        var baseBody = components.Single(c => c != null && c.GetType().Name == "RigidBody" && c.name == "UnderCarriageBody");
        Assert.That(baseBody.GetType().GetProperty("MotionControl").GetValue(baseBody).ToString(),
          Is.EqualTo("STATIC"), "The persisted test scene must hold the stationary working base.");
        var driver = components.Single(c => c != null && c.GetType().Name == "ExcavatorRuntimeActuators");
        Assert.That(new SerializedObject(driver).FindProperty("machineProfilePath").stringValue,
          Is.EqualTo("../shared/machines/cat365_sim.jsonc"));
        var bridge = new SerializedObject(components.Single(c => c != null && c.GetType().Name == "ExcavatorSimBridge"));
        Assert.That(bridge.FindProperty("machineFrameId").stringValue, Is.EqualTo("cat365_swing_ground_chassis_v1"));
        foreach (string field in new[] { "machineRoot", "bucketTip", "bucketPitchFrame", "frontCamera", "soil", "materialTelemetry" })
          Assert.That(bridge.FindProperty(field).objectReferenceValue, Is.Not.Null, field);
        Assert.That(bridge.FindProperty("lidarSensors").arraySize, Is.EqualTo(3));
        Assert.That(components.Where(c => c != null && c.GetType().Name == "ExcavatorInputController")
          .All(c => !c.enabled), Is.True, "Only the shared runtime may write actuator commands.");
      }
      finally { EditorSceneManager.ClosePreviewScene(scene); }
    }

    [Test]
    public void RuntimeDriverSelectsItsMachineProfileWithoutReplacingTheDefault()
    {
      string original = File.ReadAllText(MachineProfileLoader.DefaultProfilePath());
      string path = Path.Combine(Application.temporaryCachePath, "cat365-profile-test.jsonc");
      var go = new GameObject("machine-profile-selection-test");
      go.SetActive(false);
      try {
        File.WriteAllText(path, original.Replace("scale_excavator_v1", "cat365_test"));
        Type driver = Type.GetType("RLExcavator.ExcavatorRuntimeActuators, Assembly-CSharp");
        Component component = go.AddComponent(driver);
        FieldInfo selector = driver.GetField("machineProfilePath");
        Assert.That(selector, Is.Not.Null, "New machine scene must select its own profile");
        MethodInfo load = driver.GetMethod("LoadMachineProfile", BindingFlags.Instance | BindingFlags.NonPublic);
        selector.SetValue(component, path);
        var selected = (MachineProfile)load.Invoke(component, null);
        Assert.That(selected.MachineId, Is.EqualTo("cat365_test"));
        selector.SetValue(component, "");
        Assert.That(((MachineProfile)load.Invoke(component, null)).MachineId, Is.EqualTo("scale_excavator_v1"));
        Assert.That(File.ReadAllText(MachineProfileLoader.DefaultProfilePath()), Is.EqualTo(original));
      }
      finally {
        UnityEngine.Object.DestroyImmediate(go);
        if (File.Exists(path)) File.Delete(path);
      }
    }

    [TestCase( "boom", 1f )]
    [TestCase( "stick", 1f )]
    [TestCase( "bucket", -1f )]
    [TestCase( "swing", 1f )]
    public void ManualStickHasNeutralZoneAndDirectionalSpeedScale( string axis, float direction )
    {
      MachineProfile profile = MachineProfileLoader.LoadFromPath(
        Path.GetFullPath( Path.Combine( Application.dataPath, "../../shared/machine_profile.jsonc" ) ) );
      MachineActuatorProfile limit = profile.GetActuator( axis );
      Type driver = Type.GetType( "RLExcavator.ExcavatorRuntimeActuators, Assembly-CSharp" );
      Assert.That( driver, Is.Not.Null );
      MethodInfo map = driver.GetMethod( "ManualSpeed", BindingFlags.Static | BindingFlags.NonPublic );
      Assert.That( map, Is.Not.Null );

      float Speed( float action ) => (float)map.Invoke( null, new object[] { action, limit } );
      Assert.That( Speed( 0.15f ), Is.Zero );
      Assert.That( Speed( -0.15f ), Is.Zero );
      float positive = direction > 0f ? limit.MaxSpeedPositive : -limit.MaxSpeedNegative;
      float negative = direction > 0f ? -limit.MaxSpeedNegative : limit.MaxSpeedPositive;
      Assert.That( Speed( 0.575f ), Is.EqualTo( positive * 0.5f ).Within( 1e-6f ) );
      Assert.That( Speed( 1f ), Is.EqualTo( positive ).Within( 1e-6f ) );
      Assert.That( Speed( -1f ), Is.EqualTo( negative ).Within( 1e-6f ) );
    }
  }
}
