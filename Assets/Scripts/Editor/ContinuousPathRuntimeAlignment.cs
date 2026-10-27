using System;
using System.Linq;
using AGXUnity;
using AGXUnity.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RLExcavator.Editor
{
  /// <summary>
  /// V5 构建时对齐当前 Runtime 装配，消除训练 prefab 遗留的上车初始回转差异。
  /// 只改目标场景实例的局部值；不复制对象引用，不改 MachineRoot 或共享 prefab。
  /// </summary>
  public static class ContinuousPathRuntimeAlignment
  {
    public static void Apply( ExcavatorTrajectoryAgent agent, ExcavatorFrameRegistry runtimeFrames )
    {
      if ( agent == null )
        throw new ArgumentNullException( nameof( agent ) );
      if ( runtimeFrames == null )
        throw new ArgumentNullException( nameof( runtimeFrames ) );
      if ( agent.frameRegistry == null || agent.frameRegistry.ModelRoot == null || runtimeFrames.ModelRoot == null )
        throw new InvalidOperationException( "Runtime alignment needs both model roots and the training frame registry." );
      if ( agent.bucketTip == null || runtimeFrames.BucketTip == null )
        throw new InvalidOperationException( "Runtime alignment needs both bucket-tip transforms." );

      Transform sourceModel = runtimeFrames.ModelRoot;
      Transform targetModel = agent.frameRegistry.ModelRoot;
      var sourceBodies = sourceModel.GetComponentsInChildren<RigidBody>( true )
        .ToDictionary( BodyUuid );
      RigidBody[] targetBodies = targetModel.GetComponentsInChildren<RigidBody>( true );
      if ( targetBodies.Length != sourceBodies.Count )
        throw new InvalidOperationException( "Training and Runtime must contain the same imported rigid bodies." );

      // Imported AGX IDs are stable across prefab instances; display names are not the matching contract.
      foreach ( RigidBody target in targetBodies ) {
        RigidBody source;
        if ( !sourceBodies.TryGetValue( BodyUuid( target ), out source ) )
          throw new InvalidOperationException( "No Runtime rigid body matches training body " + target.name + "." );
        CopyLocalTransform( source.transform, target.transform );
      }

      Constraint sourceSwing = SwingConstraint( sourceModel );
      Constraint targetSwing = SwingConstraint( targetModel );
      // Keep the target ReferenceObject/ConnectedObject and both frame instances. These belong to its own environment.
      // Only the reference-frame orientation differs; Synchronized remains false and the connected frame is untouched.
      targetSwing.AttachmentPair.ReferenceFrame.LocalRotation = sourceSwing.AttachmentPair.ReferenceFrame.LocalRotation;
      Record( targetSwing );
      CopyLocalTransform( runtimeFrames.BucketTip, agent.bucketTip );
      EditorSceneManager.MarkSceneDirty( agent.gameObject.scene );
    }

    private static string BodyUuid( RigidBody body )
    {
      Uuid uuid = body.GetComponent<Uuid>();
      if ( uuid == null || string.IsNullOrEmpty( uuid.Str ) )
        throw new InvalidOperationException( "Imported body has no AGX UUID: " + body.name );
      return uuid.Str;
    }

    private static Constraint SwingConstraint( Transform model )
      => model.GetComponentsInChildren<Constraint>( true ).Single( constraint => constraint.name == "Hinge_Swing" );

    private static void CopyLocalTransform( Transform source, Transform target )
    {
      target.localPosition = source.localPosition;
      target.localRotation = source.localRotation;
      target.localScale = source.localScale;
      Record( target );
    }

    private static void Record( UnityEngine.Object component )
    {
      if ( PrefabUtility.IsPartOfPrefabInstance( component ) )
        PrefabUtility.RecordPrefabInstancePropertyModifications( component );
      EditorUtility.SetDirty( component );
    }
  }
}
