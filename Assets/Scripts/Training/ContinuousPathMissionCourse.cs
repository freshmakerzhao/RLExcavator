using System;
using System.Collections.Generic;
using AGXUnity;
using UnityEngine;

namespace RLExcavator
{
  /// <summary>
  /// 训练专用任务课程：从实际技能末态物理复位，学习真实运输／返回路线及其前缀。
  /// 仅生成训练样本，不改变执行端路径或策略动作。
  /// </summary>
  [DisallowMultipleComponent]
  public sealed class ContinuousPathMissionCourse : MonoBehaviour
  {
    [Tooltip("boom/stick/bucket 缸位(m)，swing 角度(rad)；不是 raw_joint_angles。")]
    public Vector4 startJointPose;
    [Tooltip("完整名义路线，包含首点；ROS：前、左、上，单位米。")]
    public Vector3[] routeRosM;
    [Min(0f)] public float jointJitterM = 0.002f;
    [Min(0f)] public float swingJitterRad = 0.03f;
    [Min(0f)] public float routeJitterM = 0.015f;
    [Range(0f, 1f)] public float fullPathProbability = 0.5f;
    [Range(0f, 1f)] public float minPrefixFraction = 0.3f;

    private MachineProfile m_profile;
    public float LastPrefixFraction { get; private set; }

    /// <summary>只写下一次复位的 home 目标；调用者继续使用原 BeginHomePoseReset 物理锁定复位。</summary>
    public void PrepareEpisode( ExcavatorController controller )
    {
      if ( controller == null )
        throw new ArgumentNullException( nameof( controller ) );
      ValidateConstraints( controller.boom, "boom" );
      ValidateConstraints( controller.stick, "stick" );
      ValidateConstraints( controller.bucket, "bucket" );
      if ( controller.swing == null )
        throw new InvalidOperationException( "Mission course requires the controller swing constraint." );
      if ( m_profile == null )
        m_profile = MachineProfileLoader.Load();

      // 每组缸只采样一次；双大臂缸使用同一目标，不能各自随机。
      Vector4 pose = startJointPose + new Vector4(
        UnityEngine.Random.Range( -jointJitterM, jointJitterM ),
        UnityEngine.Random.Range( -jointJitterM, jointJitterM ),
        UnityEngine.Random.Range( -jointJitterM, jointJitterM ),
        UnityEngine.Random.Range( -swingJitterRad, swingJitterRad ) );
      controller.boomHomePose = HomeTargets( controller.boom.Length, ClampCylinder( "boom", pose.x ) );
      controller.stickHomePose = HomeTargets( controller.stick.Length, ClampCylinder( "stick", pose.y ) );
      controller.bucketHomePose = HomeTargets( controller.bucket.Length, ClampCylinder( "bucket", pose.z ) );
      controller.swingHomePose = pose.w;
      controller.useInspectorHomePose = true;
    }

    public Vector3[] CreateReference( Vector3 actualStartUnityLocal )
    {
      if ( routeRosM == null || routeRosM.Length < 2 )
        throw new InvalidOperationException( "Mission course routeRosM needs a start and at least one waypoint." );
      var points = new Vector3[ routeRosM.Length ];
      for ( int i = 0; i < points.Length; ++i ) {
        Vector3 ros = routeRosM[ i ];
        points[ i ] = new Vector3( -ros.y, ros.z, ros.x );
      }
      float fraction = UnityEngine.Random.value < fullPathProbability ? 1f
        : UnityEngine.Random.Range( Mathf.Clamp01( minPrefixFraction ), 1f );
      LastPrefixFraction = fraction;
      return BuildPrefix( actualStartUnityLocal, points, fraction,
                          UnityEngine.Random.insideUnitSphere * routeJitterM );
    }

    /// <summary>
    /// points 包含名义首点；以真实首点替换它，其余折点整体平移，再按弧长截取前缀。
    /// 保留所有已走到的折点，只在线段内插入终点；不平滑、不修改输入。
    /// </summary>
    public static Vector3[] BuildPrefix( Vector3 actualStart, Vector3[] points, float fraction, Vector3 offset )
    {
      if ( points == null )
        throw new ArgumentNullException( nameof( points ) );
      if ( points.Length < 2 )
        throw new ArgumentException( "Mission route needs at least two points.", nameof( points ) );
      var shifted = new Vector3[ points.Length ];
      shifted[ 0 ] = actualStart;
      float length = 0f;
      for ( int i = 1; i < shifted.Length; ++i ) {
        shifted[ i ] = points[ i ] + offset;
        length += Vector3.Distance( shifted[ i - 1 ], shifted[ i ] );
      }
      if ( fraction >= 1f )
        return shifted;

      float remaining = length * Mathf.Clamp01( fraction );
      var prefix = new List<Vector3> { actualStart };
      for ( int i = 1; i < shifted.Length && remaining > 0f; ++i ) {
        float segment = Vector3.Distance( shifted[ i - 1 ], shifted[ i ] );
        if ( segment <= 1e-6f )
          continue;
        if ( remaining < segment ) {
          prefix.Add( Vector3.Lerp( shifted[ i - 1 ], shifted[ i ], remaining / segment ) );
          break;
        }
        prefix.Add( shifted[ i ] );
        remaining -= segment;
      }
      return prefix.ToArray();
    }

    private float ClampCylinder( string name, float value )
    {
      MachineActuatorProfile actuator = m_profile.GetActuator( name );
      return Mathf.Clamp( value, actuator.RangeMin, actuator.RangeMax );
    }

    private static float[] HomeTargets( int count, float position )
    {
      var targets = new float[ count ];
      for ( int i = 0; i < count; ++i )
        targets[ i ] = position;
      return targets;
    }

    private static void ValidateConstraints( Constraint[] constraints, string name )
    {
      if ( constraints == null || constraints.Length == 0 )
        throw new InvalidOperationException( "Mission course requires controller " + name + " constraints." );
      for ( int i = 0; i < constraints.Length; ++i ) {
        if ( constraints[ i ] == null )
          throw new InvalidOperationException( "Mission course has a missing " + name + " constraint at " + i + "." );
      }
    }
  }
}
