using System.Text;
using UnityEngine;

namespace RLExcavator
{
  /// <summary>
  /// Central registry for semantic excavator frames.
  /// Algorithm code should use MachineRoot as the only local coordinate frame.
  /// Imported model roots remain physics/mesh containers and must not define
  /// observations, trajectories, or reachable workspace coordinates.
  /// </summary>
  [DisallowMultipleComponent]
  public class ExcavatorFrameRegistry : MonoBehaviour
  {
    [Header("语义坐标系")]
    [Tooltip("算法坐标系唯一来源。观测、轨迹、可达区域局部点都相对它。")]
    public Transform machineRoot;

    [Tooltip("导入模型根节点，只作为物理和网格容器。")]
    public Transform modelRoot;

    [Tooltip("上车回转参考。用于铲斗姿态等随回转坐标。")]
    public Transform swingFrame;

    [Tooltip("铲斗 Pitch 参考。为空时使用上车回转参考。")]
    public Transform bucketPitchFrame;

    [Tooltip("真实铲斗尖端点。")]
    public Transform bucketTip;

    [Header("自动绑定")]
    [Tooltip("缺少引用时按约定名称自动查找。")]
    public bool autoFindReferences = true;

    [Tooltip("缺少 MachineRoot 时临时退回当前物体。正式 prefab 应显式绑定。")]
    public bool fallbackMachineRootToSelf = true;

    public Transform MachineRoot => machineRoot != null
      ? machineRoot
      : fallbackMachineRootToSelf ? transform : null;

    public Transform ModelRoot => modelRoot;
    public Transform SwingFrame => swingFrame;
    public Transform BucketPitchFrame => bucketPitchFrame != null ? bucketPitchFrame : swingFrame;
    public Transform BucketTip => bucketTip;

    private void Reset()
    {
      ResolveMissingReferences();
    }

    private void Awake()
    {
      if ( autoFindReferences )
        ResolveMissingReferences();
    }

    private void OnValidate()
    {
      if ( autoFindReferences )
        ResolveMissingReferences();
    }

    public Vector3 WorldToMachineLocal( Vector3 worldPoint )
    {
      Transform frame = MachineRoot;
      return frame != null ? frame.InverseTransformPoint( worldPoint ) : worldPoint;
    }

    public Vector3 MachineLocalToWorld( Vector3 machineLocalPoint )
    {
      Transform frame = MachineRoot;
      return frame != null ? frame.TransformPoint( machineLocalPoint ) : machineLocalPoint;
    }

    public void ResolveMissingReferences()
    {
      if ( machineRoot == null || machineRoot == transform ) {
        Transform frames = FindChildByName( transform, "Frames" );
        Transform resolvedMachineRoot = FindChildByPath( transform, "Frames/MachineRoot" )
          ?? FindChildByName( frames, "MachineRoot" )
          ?? FindChildByName( transform, "MachineRoot" );
        if ( resolvedMachineRoot != null )
          machineRoot = resolvedMachineRoot;
      }

      if ( modelRoot == null )
        modelRoot = FindChildByName( transform, "CAT365_v3" );

      if ( swingFrame == null )
        swingFrame =
          FindChildByName( transform, "Body_Swing" ) ??
          FindChildByName( transform, "UpperCarriage" ) ??
          FindChildByName( transform, "Cabin" ) ??
          FindChildByName( transform, "Chassisbody" );

      if ( bucketPitchFrame == null )
        bucketPitchFrame =
          FindChildByName( transform, "BucketPitchFrame" ) ??
          swingFrame;

      if ( bucketTip == null )
        bucketTip =
          FindChildByName( transform, "BucketTip" ) ??
          FindChildByName( transform, "Traj_BucketTip" );

      if ( machineRoot == null && fallbackMachineRootToSelf )
        machineRoot = transform;
    }

    public bool TryValidate( out string message )
    {
      var builder = new StringBuilder();

      if ( MachineRoot == null )
        builder.AppendLine( "MachineRoot 未绑定。" );
      if ( BucketTip == null )
        builder.AppendLine( "BucketTip 未绑定。" );
      if ( BucketPitchFrame == null )
        builder.AppendLine( "BucketPitchFrame/SwingFrame 未绑定。" );

      message = builder.ToString().Trim();
      return string.IsNullOrEmpty( message );
    }

    public static ExcavatorFrameRegistry FindNearest( Component owner )
    {
      if ( owner == null )
        return null;

      ExcavatorFrameRegistry registry = owner.GetComponentInParent<ExcavatorFrameRegistry>( true );
      if ( registry != null )
        return registry;

      Transform cursor = owner.transform != null ? owner.transform.parent : null;
      while ( cursor != null ) {
        registry = cursor.GetComponentInChildren<ExcavatorFrameRegistry>( true );
        if ( registry != null )
          return registry;

        cursor = cursor.parent;
      }

      return null;
    }

    private static Transform FindChildByPath( Transform root, string path )
    {
      if ( root == null || string.IsNullOrEmpty( path ) )
        return null;

      return root.Find( path );
    }

    private static Transform FindChildByName( Transform root, string childName )
    {
      if ( root == null || string.IsNullOrEmpty( childName ) )
        return null;

      if ( root.name == childName )
        return root;

      for ( int i = 0; i < root.childCount; i++ ) {
        Transform match = FindChildByName( root.GetChild( i ), childName );
        if ( match != null )
          return match;
      }

      return null;
    }
  }
}
