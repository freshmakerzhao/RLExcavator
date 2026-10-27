using System;
using System.Collections.Generic;
using System.Linq;
using AGXUnity.Sensor;
using UnityEngine;

namespace RLExcavator
{
  /// <summary>AGX 光线扫描的只读出口；按物理步累计一圈，不从土体真值补点。</summary>
  public sealed class ExcavatorLidarTelemetry : MonoBehaviour
  {
    public LidarSensor sensor;
    public string sensorId = "";
    [Tooltip("视场几何代理标记；不表示厂家扫描时序或精度复现。 ")]
    public string scanModel = "";
    public Transform machineRoot;
    [Tooltip("点云累积窗口；场景雷达为 10 Hz，0.2 秒覆盖两个扫描周期。")]
    public double scanWindowSeconds = 0.2;

    [Serializable]
    public sealed class State
    {
      public string sensor_id;
      public string scan_model;
      public string schema = "lidar_points_v1";
      public string source = "agx_lidar_raycast_v1";
      public string source_kind = "simulated_sensor";
      public string frame = "machine_root_ros";
      public string time_base = "simulation";
      public string encoding = "xyz_f32_le";
      public bool available;
      public string reason = "scan_warming_up";
      public int epoch;
      public int scan_start_sequence;
      public int scan_end_sequence;
      public double scan_start_time_s;
      public double scan_end_time_s;
      public bool scan_complete;
      public int point_count;
      public string data_base64 = "";
      public float[] sensor_position_ros_m = new float[3];
    }

    /// <summary>已配置雷达的共同窗口；每台保留来源，Python 校验后再拼接 XYZ。</summary>
    [Serializable]
    public sealed class ScanSet
    {
      public string schema = "lidar_scan_set_v1";
      public string source = "agx_lidar_raycast_v1";
      public string source_kind = "simulated_sensor";
      public string frame = "machine_root_ros";
      public string time_base = "simulation";
      public bool available, scan_complete;
      public string reason = "lidar_set_unconfigured";
      public int epoch, scan_start_sequence, scan_end_sequence, point_count;
      public double scan_start_time_s, scan_end_time_s;
      public State[] scans = Array.Empty<State>();
    }

    public static ScanSet ReadScanSet(ExcavatorLidarTelemetry[] readers, int epoch)
    {
      var scans = readers?.Where(r => r != null).Select(r => r.ReadState()).ToArray() ?? Array.Empty<State>();
      // 不同名字不能掩盖同一传感器或不同根坐标的错误接线。
      if (!HasValidBindings(readers))
        return new ScanSet { epoch = epoch, scans = scans, point_count = scans.Sum(s => s.point_count), reason = "lidar_set_invalid_bindings" };
      return CombineStates(scans, epoch);
    }

    /// <summary>先检查已配置子集的接线，再统一启停；失败时不切换任何一台。</summary>
    public static void ConfigureScanSet(ExcavatorLidarTelemetry[] readers, bool enabled)
    {
      if (!HasValidBindings(readers))
        throw new InvalidOperationException("lidar_set_invalid_bindings");
      if (!ValidSensorIds(readers.Select(r => r.sensorId).ToArray()))
        throw new InvalidOperationException("lidar_set_unconfigured");
      if (readers.Any(r => r.scanModel != (r.sensorId == "LidarE1RFront" ? "e1r_fov_proxy_v1" : "airy_fov_proxy_v1")))
        throw new InvalidOperationException("lidar_set_model_mismatch");
      foreach (var reader in readers) reader.SetScanningEnabled(enabled);
    }

    private static bool HasValidBindings(ExcavatorLidarTelemetry[] readers) =>
      readers != null && readers.Length >= 1 && readers.Length <= 3 && readers.All(r => r != null && r.sensor != null && r.machineRoot != null)
      && readers.Select(r => r.sensor).Distinct().Count() == readers.Length
      && readers.All(r => r.machineRoot == readers[0].machineRoot);

    private static bool ValidSensorIds(string[] ids) =>
      ids.Length >= 1 && ids.Length <= 3 && ids.Distinct().Count() == ids.Length
      && ids.All(id => id == "LidarAiryLeft" || id == "LidarAiryRight" || id == "LidarE1RFront");

    public static ScanSet CombineStates(State[] scans, int epoch)
    {
      var result = new ScanSet { epoch = epoch, scans = scans ?? Array.Empty<State>(),
        point_count = scans?.Sum(s => s?.point_count ?? 0) ?? 0 };
      if (scans == null || scans.Any(s => s == null)
          || !ValidSensorIds(scans.Select(s => s.sensor_id).ToArray()))
        return result;
      if (scans.All(s => s.reason == "lidar_disabled_by_configuration")) {
        result.reason = "lidar_disabled_by_configuration";
        return result;
      }
      foreach (var scan in scans) {
        if (scan.scan_model != (scan.sensor_id == "LidarE1RFront" ? "e1r_fov_proxy_v1" : "airy_fov_proxy_v1")) {
          result.reason = "lidar_set_model_mismatch";
          return result;
        }
        if (!scan.available || !scan.scan_complete) {
          result.reason = scan.sensor_id + ":" + scan.reason;
          return result;
        }
      }
      var first = scans[0];
      if (scans.Any(s => s.epoch != epoch || s.scan_start_sequence != first.scan_start_sequence
          || s.scan_end_sequence != first.scan_end_sequence || s.scan_start_time_s != first.scan_start_time_s
          || s.scan_end_time_s != first.scan_end_time_s)) {
        result.reason = "lidar_set_window_mismatch";
        return result;
      }
      result.available = result.scan_complete = true;
      result.reason = "ok";
      result.scan_start_sequence = first.scan_start_sequence;
      result.scan_end_sequence = first.scan_end_sequence;
      result.scan_start_time_s = first.scan_start_time_s;
      result.scan_end_time_s = first.scan_end_time_s;
      return result;
    }

    private sealed class Batch
    {
      public int sequence;
      public double end, dt;
      public Vector3[] points;
    }

    private readonly Queue<Batch> m_batches = new Queue<Batch>();
    private LidarOutput m_output;
    private Vector3[] m_nativePoints;
    private int m_epoch;
    private string m_failure;
    private bool m_scanningEnabled = true;

    /// <summary>AGX OnDisable 会调用 Native.setEnable(false)，真正停止射线而非只隐藏回波。</summary>
    public void SetScanningEnabled(bool enabled)
    {
      if (sensor == null) throw new InvalidOperationException("lidar_sensor_missing");
      sensor.enabled = enabled;
      m_scanningEnabled = enabled;
      // 每次显式配置都建立新窗口，不能在重新开启后返回关闭前的旧扫描。
      m_batches.Clear();
      m_failure = null;
    }

    public void BeginEpoch(int epoch)
    {
      m_epoch = epoch;
      m_batches.Clear();
      m_failure = null;
    }

    /// <summary>桥接器完成一个正常物理步后调用；复位过程的物理步不计入本回合。</summary>
    public void CaptureStep(int epoch, int sequence, double time, double dt)
    {
      if (!m_scanningEnabled) return;
      if (sensor == null || machineRoot == null || !sensor.isActiveAndEnabled) {
        m_failure = "lidar_unconfigured_or_disabled";
        m_batches.Clear();
        return;
      }
      try {
        if (sensor.GetInitialized<LidarSensor>() == null || sensor.Native == null)
          throw new InvalidOperationException("Lidar initialization failed");
        if (m_output == null) {
          m_output = new LidarOutput(agxSensor.RtOutput.Field.XYZ_VEC3_F32);
          if (!sensor.Add(m_output)) throw new InvalidOperationException("Lidar output rejected");
          // 新添加的输出尚未经历一次光线扫描，不能把空缓冲算作有效扫描窗口。
          return;
        }
        m_nativePoints = m_output.View<Vector3>(out uint count, m_nativePoints);
        var toRoot = machineRoot.worldToLocalMatrix * sensor.GlobalTransform;
        var points = new List<Vector3>((int)count);
        for (int i = 0; i < count; ++i) {
          Vector3 raw = m_nativePoints[i];
          if (Finite(raw) && raw.sqrMagnitude > 1e-12f)
            points.Add(ToMachineRos(raw, toRoot));
        }
        RecordBatch(epoch, sequence, time, dt, points.ToArray());
      }
      catch (Exception error) {
        m_batches.Clear();
        string reason = "lidar_read_failed:" + error.GetType().Name;
        if (m_failure != reason) Debug.LogWarning("[ExcavatorLidarTelemetry] " + error.Message, this);
        m_failure = reason;
      }
    }

    /// <summary>Native XYZ 是右手传感器坐标；先与 AGX 渲染器一致翻 X，再转机器 ROS。</summary>
    public static Vector3 ToMachineRos(Vector3 native, Matrix4x4 sensorToRoot)
    {
      Vector3 local = sensorToRoot.MultiplyPoint3x4(new Vector3(-native.x, native.y, native.z));
      return new Vector3(local.z, -local.x, local.y);
    }

    public void RecordBatch(int epoch, int sequence, double time, double dt, Vector3[] points)
    {
      if (!m_scanningEnabled) return;
      if (epoch != m_epoch) BeginEpoch(epoch);
      m_failure = null;
      m_batches.Enqueue(new Batch { sequence = sequence, end = time, dt = dt, points = points });
      while (m_batches.Count > 1 && m_batches.Peek().end <= time - scanWindowSeconds + 1e-7)
        m_batches.Dequeue();
    }

    /// <summary>只读最近窗口；重复请求不扫描、不推进物理，也不消耗命令序号。</summary>
    public State ReadState()
    {
      var result = new State { sensor_id = sensorId, scan_model = scanModel, epoch = m_epoch, reason = m_failure ?? "scan_warming_up" };
      if (machineRoot != null && sensor != null) {
        Vector3 p = machineRoot.InverseTransformPoint(sensor.GlobalTransform.MultiplyPoint3x4(Vector3.zero));
        result.sensor_position_ros_m = new[] { p.z, -p.x, p.y };
      }
      if (!m_scanningEnabled) {
        result.reason = "lidar_disabled_by_configuration";
        return result;
      }
      if (m_batches.Count == 0) return result;
      Batch first = m_batches.Peek(), last = m_batches.Last();
      result.scan_start_sequence = first.sequence - 1;
      result.scan_end_sequence = last.sequence;
      result.scan_start_time_s = first.end - first.dt;
      result.scan_end_time_s = last.end;
      result.scan_complete = last.end - result.scan_start_time_s >= scanWindowSeconds - 1e-7;
      if (!result.scan_complete) return result;
      var points = m_batches.SelectMany(b => b.points).ToArray();
      result.point_count = points.Length;
      var packed = new float[points.Length * 3];
      for (int i = 0; i < points.Length; ++i) {
        packed[3 * i] = points[i].x;
        packed[3 * i + 1] = points[i].y;
        packed[3 * i + 2] = points[i].z;
      }
      var bytes = new byte[packed.Length * sizeof(float)];
      Buffer.BlockCopy(packed, 0, bytes, 0, bytes.Length);
      if (!BitConverter.IsLittleEndian)
        for (int i = 0; i < bytes.Length; i += 4) Array.Reverse(bytes, i, 4);
      result.data_base64 = Convert.ToBase64String(bytes);
      result.available = points.Length > 0;
      result.reason = result.available ? "ok" : "no_returns";
      return result;
    }

    private static bool Finite(Vector3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);

    private void OnDestroy()
    {
      if (m_output != null && sensor != null) sensor.Remove(m_output);
    }
  }
}
