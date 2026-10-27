using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class ExcavatorLidarTelemetryTests
  {
    private Type Reader => Type.GetType("RLExcavator.ExcavatorLidarTelemetry, Assembly-CSharp");

    [Test]
    public void PlacementSelectionKeepsOnlySelectedSensorAndReaderEnabled()
    {
      var selector = Type.GetType("RLExcavator.Editor.LidarPlacementExperiment, Assembly-CSharp-Editor");
      Assert.That(selector, Is.Not.Null);
      var root = new GameObject("PlacementSelectionTest");
      root.SetActive(false);
      try {
        var readers = Array.CreateInstance(Reader, 2);
        var sensors = new Behaviour[2];
        for (int i = 0; i < 2; ++i) {
          var go = new GameObject("placement" + i);
          go.transform.SetParent(root.transform);
          var reader = go.AddComponent(Reader);
          sensors[i] = (Behaviour)go.AddComponent(Type.GetType("AGXUnity.Sensor.LidarSensor, AGXUnity"));
          Reader.GetField("sensor").SetValue(reader, sensors[i]);
          readers.SetValue(reader, i);
        }
        var bridge = root.AddComponent(Type.GetType("RLExcavator.ExcavatorSimBridge, Assembly-CSharp"));
        selector.GetMethod("Select").Invoke(null, new object[] { bridge, readers, readers.GetValue(1) });
        Assert.That(sensors[0].enabled, Is.False);
        Assert.That(sensors[1].enabled, Is.True);
        var selected = (Array)bridge.GetType().GetField("lidarSensors").GetValue(bridge);
        Assert.That(selected.Length, Is.EqualTo(1));
        Assert.That(selected.GetValue(0), Is.SameAs(readers.GetValue(1)));
      }
      finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    [Test]
    public void DisabledScanningClearsWindowAndRequiresFreshWindowWhenEnabled()
    {
      var setEnabled = Reader.GetMethod("SetScanningEnabled");
      Assert.That(setEnabled, Is.Not.Null, "缺少实际扫描启停接口");
      var go = new GameObject("LidarSwitchTest");
      go.SetActive(false);
      try {
        var reader = go.AddComponent(Reader);
        var sensor = (Behaviour)go.AddComponent(Type.GetType("AGXUnity.Sensor.LidarSensor, AGXUnity"));
        Reader.GetField("sensor").SetValue(reader, sensor);
        var record = Reader.GetMethod("RecordBatch");
        var read = Reader.GetMethod("ReadState");
        record.Invoke(reader, new object[] { 1, 1, .1, .1, new[] { Vector3.one } });
        record.Invoke(reader, new object[] { 1, 2, .2, .1, new[] { Vector3.one } });
        Assert.That(Field<bool>(read.Invoke(reader, null), "available"), Is.True);

        setEnabled.Invoke(reader, new object[] { false });
        Assert.That(sensor.enabled, Is.False, "必须关闭 AGX LidarSensor，不能只停止 Python 读取");
        record.Invoke(reader, new object[] { 1, 3, .3, .1, new[] { Vector3.one } });
        var disabled = read.Invoke(reader, null);
        Assert.That(Field<bool>(disabled, "available"), Is.False);
        Assert.That(Field<int>(disabled, "point_count"), Is.Zero);
        Assert.That(Field<string>(disabled, "reason"), Is.EqualTo("lidar_disabled_by_configuration"));
        Reader.GetMethod("BeginEpoch").Invoke(reader, new object[] { 2 });
        Assert.That(Field<string>(read.Invoke(reader, null), "reason"), Is.EqualTo("lidar_disabled_by_configuration"));

        setEnabled.Invoke(reader, new object[] { true });
        Assert.That(sensor.enabled, Is.True);
        Assert.That(Field<bool>(read.Invoke(reader, null), "available"), Is.False);
        record.Invoke(reader, new object[] { 2, 1, .1, .1, new[] { Vector3.right } });
        Assert.That(Field<bool>(read.Invoke(reader, null), "available"), Is.False);
        record.Invoke(reader, new object[] { 2, 2, .2, .1, new[] { Vector3.right } });
        var fresh = read.Invoke(reader, null);
        Assert.That(Field<bool>(fresh, "available"), Is.True);
        Assert.That(Field<int>(fresh, "point_count"), Is.EqualTo(2));
        Assert.That(Field<int>(fresh, "epoch"), Is.EqualTo(2));
      }
      finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    [Test]
    public void ConfigureScanSetValidatesAllBindingsBeforeChangingAnySensor()
    {
      var configure = Reader.GetMethod("ConfigureScanSet");
      Assert.That(configure, Is.Not.Null, "已配置雷达的开关必须先整体校验");
      var root = new GameObject("LidarSetSwitchTest");
      root.SetActive(false);
      try {
        var readers = Array.CreateInstance(Reader, 3);
        var sensors = new Behaviour[3];
        for (int i = 0; i < 3; ++i) {
          var go = new GameObject("lidar" + i);
          go.transform.SetParent(root.transform);
          var reader = go.AddComponent(Reader);
          sensors[i] = (Behaviour)go.AddComponent(Type.GetType("AGXUnity.Sensor.LidarSensor, AGXUnity"));
          Reader.GetField("sensor").SetValue(reader, sensors[i]);
          Reader.GetField("machineRoot").SetValue(reader, root.transform);
          Reader.GetField("sensorId").SetValue(reader, new[] { "LidarAiryLeft", "LidarAiryRight", "LidarE1RFront" }[i]);
          Reader.GetField("scanModel").SetValue(reader, i == 2 ? "e1r_fov_proxy_v1" : "airy_fov_proxy_v1");
          readers.SetValue(reader, i);
        }
        var empty = Array.CreateInstance(Reader, 0);
        Assert.Throws<TargetInvocationException>(() => configure.Invoke(null, new object[] { empty, false }));
        Assert.That(sensors, Has.All.Property("enabled").True);
        var pair = Array.CreateInstance(Reader, 2);
        pair.SetValue(readers.GetValue(0), 0);
        pair.SetValue(readers.GetValue(2), 1);
        configure.Invoke(null, new object[] { pair, false });
        Assert.That(sensors[0].enabled, Is.False);
        Assert.That(sensors[1].enabled, Is.True, "未纳入本次配置的传感器不能被改变");
        Assert.That(sensors[2].enabled, Is.False);
        configure.Invoke(null, new object[] { pair, true });
        Reader.GetField("sensor").SetValue(readers.GetValue(2), sensors[1]);
        var failure = Assert.Throws<TargetInvocationException>(() => configure.Invoke(null, new object[] { readers, false }));
        Assert.That(failure.InnerException, Is.TypeOf<InvalidOperationException>());
        Assert.That(sensors, Has.All.Property("enabled").True);
        Reader.GetField("sensor").SetValue(readers.GetValue(2), sensors[2]);
        configure.Invoke(null, new object[] { readers, false });
        Assert.That(sensors, Has.All.Property("enabled").False);
        var scan = Reader.GetMethod("ReadScanSet").Invoke(null, new object[] { readers, 0 });
        Assert.That(Field<bool>(scan, "available"), Is.False);
        Assert.That(Field<string>(scan, "reason"), Is.EqualTo("lidar_disabled_by_configuration"));
        configure.Invoke(null, new object[] { readers, true });
        Assert.That(sensors, Has.All.Property("enabled").True);
      }
      finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    [Test]
    public void NativePointUsesSensorPoseThenMachineRosAxes()
    {
      Assert.That(Reader, Is.Not.Null, "雷达遥测尚未实现");
      var transform = Matrix4x4.Translate(new Vector3(1, 2, 3));
      var point = (Vector3)Reader.GetMethod("ToMachineRos").Invoke(null,
        new object[] { new Vector3(0.2f, 0.3f, 0.4f), transform });
      Assert.That(point.x, Is.EqualTo(3.4f).Within(1e-6));
      Assert.That(point.y, Is.EqualTo(-0.8f).Within(1e-6));
      Assert.That(point.z, Is.EqualTo(2.3f).Within(1e-6));
    }

    [Test]
    public void WindowRequiresFullSweepAndResetDiscardsOldPoints()
    {
      Assert.That(Reader, Is.Not.Null, "雷达遥测尚未实现");
      var go = new GameObject("LidarBufferTest");
      try {
        var reader = go.AddComponent(Reader);
        var record = Reader.GetMethod("RecordBatch");
        var read = Reader.GetMethod("ReadState");
        record.Invoke(reader, new object[] { 1, 1, 0.1, 0.1, new[] { new Vector3(1, 2, 3) } });
        var warmup = read.Invoke(reader, null);
        Assert.That(Field<bool>(warmup, "available"), Is.False);
        record.Invoke(reader, new object[] { 1, 2, 0.2, 0.1, new[] { new Vector3(4, 5, 6) } });
        var full = read.Invoke(reader, null);
        Assert.That(Field<bool>(full, "scan_complete"), Is.True);
        Assert.That(Field<int>(full, "point_count"), Is.EqualTo(2));
        Assert.That(Field<double>(full, "scan_start_time_s"), Is.EqualTo(0).Within(1e-8));
        Assert.That(Field<double>(full, "scan_end_time_s"), Is.EqualTo(0.2).Within(1e-8));
        var bytes = Convert.FromBase64String(Field<string>(full, "data_base64"));
        Assert.That(bytes.Length, Is.EqualTo(24));
        Assert.That(BitConverter.ToSingle(bytes, 12), Is.EqualTo(4f));
        record.Invoke(reader, new object[] { 1, 3, 0.3, 0.1, new[] { new Vector3(7, 8, 9) } });
        Assert.That(Field<int>(read.Invoke(reader, null), "point_count"), Is.EqualTo(2));
        Reader.GetMethod("BeginEpoch").Invoke(reader, new object[] { 2 });
        var reset = read.Invoke(reader, null);
        Assert.That(Field<bool>(reset, "available"), Is.False);
        Assert.That(Field<int>(reset, "epoch"), Is.EqualTo(2));
        Assert.That(Field<int>(reset, "point_count"), Is.Zero);
      }
      finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    [Test]
    public void ConfiguredSensorSubsetRequiresMatchingModelsAndWindows()
    {
      var merge = Reader.GetMethod("CombineStates");
      Assert.That(merge, Is.Not.Null, "三雷达读取尚未实现");
      var type = Reader.GetNestedType("State");
      var scans = Array.CreateInstance(type, 3);
      for (int i = 0; i < 3; ++i) {
        var scan = Activator.CreateInstance(type);
        type.GetField("sensor_id").SetValue(scan, new[] { "LidarAiryRight", "LidarAiryLeft", "LidarE1RFront" }[i]);
        Assert.That(type.GetField("scan_model"), Is.Not.Null, "需要标记几何代理而非厂家精确模型");
        type.GetField("scan_model").SetValue(scan, i == 2 ? "e1r_fov_proxy_v1" : "airy_fov_proxy_v1");
        type.GetField("epoch").SetValue(scan, 3);
        type.GetField("available").SetValue(scan, true);
        type.GetField("scan_complete").SetValue(scan, true);
        type.GetField("scan_end_sequence").SetValue(scan, 10);
        type.GetField("scan_end_time_s").SetValue(scan, .2);
        type.GetField("point_count").SetValue(scan, i + 1);
        scans.SetValue(scan, i);
      }
      var full = merge.Invoke(null, new object[] { scans, 3 });
      Assert.That(Field<string>(full, "schema"), Is.EqualTo("lidar_scan_set_v1"));
      Assert.That(Field<bool>(full, "available"), Is.True);
      Assert.That(Field<int>(full, "point_count"), Is.EqualTo(6));
      Assert.That(Field<Array>(full, "scans").Length, Is.EqualTo(3));
      type.GetField("available").SetValue(scans.GetValue(1), false);
      type.GetField("reason").SetValue(scans.GetValue(1), "disabled");
      var missing = merge.Invoke(null, new object[] { scans, 3 });
      Assert.That(Field<bool>(missing, "available"), Is.False);
      Assert.That(Field<string>(missing, "reason"), Does.Contain("LidarAiryLeft"));
      Assert.That(Field<string>(Field<Array>(missing, "scans").GetValue(1), "reason"), Is.EqualTo("disabled"));
      type.GetField("available").SetValue(scans.GetValue(1), true);
      foreach (var change in new[] { "epoch", "scan_start_sequence", "scan_end_sequence", "scan_start_time_s", "scan_end_time_s", "sensor_id" }) {
        var field = type.GetField(change);
        var previous = field.GetValue(scans.GetValue(1));
        field.SetValue(scans.GetValue(1), change == "sensor_id" ? "LidarAiryRight" :
          field.FieldType == typeof(int) ? (object)((int)previous + 1) : (double)previous + .02);
        Assert.That(Field<bool>(merge.Invoke(null, new object[] { scans, 3 }), "available"), Is.False, change);
        field.SetValue(scans.GetValue(1), previous);
      }
      type.GetField("scan_model").SetValue(scans.GetValue(2), "airy_fov_proxy_v1");
      Assert.That(Field<bool>(merge.Invoke(null, new object[] { scans, 3 }), "available"), Is.False, "E1R model mismatch");
      type.GetField("scan_model").SetValue(scans.GetValue(2), "e1r_fov_proxy_v1");
      var single = Array.CreateInstance(type, 1);
      single.SetValue(scans.GetValue(0), 0);
      Assert.That(Field<bool>(merge.Invoke(null, new object[] { single, 3 }), "available"), Is.True);
      Assert.That(Field<int>(merge.Invoke(null, new object[] { single, 3 }), "point_count"), Is.EqualTo(1));
      var pair = Array.CreateInstance(type, 2);
      pair.SetValue(scans.GetValue(0), 0);
      pair.SetValue(scans.GetValue(2), 1);
      Assert.That(Field<bool>(merge.Invoke(null, new object[] { pair, 3 }), "available"), Is.True);
      Assert.That(Field<bool>(merge.Invoke(null, new object[] { Array.CreateInstance(type, 0), 3 }), "available"), Is.False);
      type.GetField("sensor_id").SetValue(single.GetValue(0), "unknown");
      Assert.That(Field<bool>(merge.Invoke(null, new object[] { single, 3 }), "available"), Is.False);
    }

    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name).GetValue(value);
  }
}
