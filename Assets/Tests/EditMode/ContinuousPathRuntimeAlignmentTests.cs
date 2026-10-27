using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RLExcavator.Tests
{
  public class ContinuousPathRuntimeAlignmentTests
  {
    [Test]
    public void AlignmentCopiesRuntimeAssemblyWithoutChangingFramesOrCrossSceneReferences()
    {
      const string runtimePath = "Assets/Scenes/ExcavatorRuntime.unity";
      const string trainingPath = "Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPathMission.unity";
      string[] preservedPaths = { runtimePath, trainingPath,
        "Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPathExpanded.unity",
        "Assets/Prefabs/ScaleTrajectoryEnvironment.prefab" };
      byte[][] filesBefore = preservedPaths.Select( File.ReadAllBytes ).ToArray();
      Scene runtime = EditorSceneManager.OpenPreviewScene( runtimePath );
      Scene training = default;
      try {
        training = EditorSceneManager.OpenPreviewScene( trainingPath );
        MonoBehaviour sourceFrames = Components( runtime ).Single( c => c.GetType().Name == "ExcavatorFrameRegistry" );
        Transform sourceModel = Property<Transform>( sourceFrames, "ModelRoot" );
        var sourceBodies = BodiesByUuid( sourceModel );
        Assert.That( sourceBodies.Count, Is.EqualTo( 26 ) );
        var sourceBefore = sourceBodies.ToDictionary( p => p.Key, p => new PoseSnapshot( p.Value.transform ) );
        MonoBehaviour sourceSwing = Swing( sourceModel );
        object sourcePair = Property<object>( sourceSwing, "AttachmentPair" );
        Quaternion sourceHingeRotation = Property<Quaternion>( Property<object>( sourcePair, "ReferenceFrame" ), "LocalRotation" );
        Transform sourceTip = Property<Transform>( sourceFrames, "BucketTip" );
        var sourceTipBefore = new PoseSnapshot( sourceTip );
        MonoBehaviour[] agents = Components( training ).Where( c => c.GetType().Name == "ExcavatorTrajectoryAgent" ).ToArray();
        Assert.That( agents.Length, Is.EqualTo( 4 ) );

        foreach ( MonoBehaviour agent in agents ) {
          object frames = agent.GetType().GetField( "frameRegistry" ).GetValue( agent );
          Transform model = Property<Transform>( frames, "ModelRoot" );
          Transform machineRoot = Property<Transform>( frames, "MachineRoot" );
          var machineRootBefore = new PoseSnapshot( machineRoot );
          var bodies = BodiesByUuid( model );
          Assert.That( bodies.Keys, Is.EquivalentTo( sourceBodies.Keys ) );
          Transform tip = (Transform)agent.GetType().GetField( "bucketTip" ).GetValue( agent );
          MonoBehaviour swing = Swing( model );
          object pair = Property<object>( swing, "AttachmentPair" );
          object reference = Property<object>( pair, "ReferenceFrame" );
          object connected = Property<object>( pair, "ConnectedFrame" );
          GameObject referenceObject = Property<GameObject>( pair, "ReferenceObject" );
          GameObject connectedObject = Property<GameObject>( pair, "ConnectedObject" );
          Vector3 referencePosition = Property<Vector3>( reference, "LocalPosition" );
          Vector3 connectedPosition = Property<Vector3>( connected, "LocalPosition" );
          Quaternion connectedRotation = Property<Quaternion>( connected, "LocalRotation" );
          Assert.That( Property<bool>( pair, "Synchronized" ), Is.False );

          // Keep this fixture useful after the scene builder has already aligned the saved V5 scene.
          Transform perturbedBody = bodies.First().Value.transform;
          perturbedBody.localPosition += Vector3.right * 0.01f;
          perturbedBody.localRotation *= Quaternion.Euler( 0f, 8f, 0f );
          perturbedBody.localScale *= 1.01f;
          tip.localPosition += Vector3.up * 0.01f;
          tip.localRotation *= Quaternion.Euler( 5f, 0f, 0f );
          reference.GetType().GetProperty( "LocalRotation" ).SetValue( reference, Quaternion.Euler( 0f, 9f, 0f ) );

          Type helper = AppDomain.CurrentDomain.GetAssemblies()
            .Select( assembly => assembly.GetType( "RLExcavator.Editor.ContinuousPathRuntimeAlignment" ) )
            .FirstOrDefault( type => type != null );
          Assert.That( helper, Is.Not.Null, "V5 needs an explicit Runtime assembly alignment helper." );
          helper.GetMethod( "Apply" ).Invoke( null, new object[] { agent, sourceFrames } );

          foreach ( var body in bodies )
            new PoseSnapshot( sourceBodies[ body.Key ].transform ).AssertMatches( body.Value.transform );
          new PoseSnapshot( sourceTip ).AssertMatches( tip );
          machineRootBefore.AssertMatches( machineRoot );
          AssertRotation( Property<Quaternion>( reference, "LocalRotation" ), sourceHingeRotation );
          Assert.That( Property<GameObject>( pair, "ReferenceObject" ), Is.SameAs( referenceObject ) );
          Assert.That( Property<GameObject>( pair, "ConnectedObject" ), Is.SameAs( connectedObject ) );
          Assert.That( Property<object>( pair, "ReferenceFrame" ), Is.SameAs( reference ) );
          Assert.That( Property<object>( pair, "ConnectedFrame" ), Is.SameAs( connected ) );
          Assert.That( Property<Vector3>( reference, "LocalPosition" ), Is.EqualTo( referencePosition ) );
          Assert.That( Property<Vector3>( connected, "LocalPosition" ), Is.EqualTo( connectedPosition ) );
          AssertRotation( Property<Quaternion>( connected, "LocalRotation" ), connectedRotation );
          Assert.That( Property<bool>( pair, "Synchronized" ), Is.False );
        }

        foreach ( var body in sourceBodies )
          sourceBefore[ body.Key ].AssertMatches( body.Value.transform );
        sourceTipBefore.AssertMatches( sourceTip );
        AssertRotation( Property<Quaternion>( Property<object>( sourcePair, "ReferenceFrame" ), "LocalRotation" ), sourceHingeRotation );
        for ( int i = 0; i < preservedPaths.Length; ++i )
          Assert.That( File.ReadAllBytes( preservedPaths[ i ] ), Is.EqualTo( filesBefore[ i ] ), preservedPaths[ i ] );
      }
      finally {
        if ( training.IsValid() ) EditorSceneManager.ClosePreviewScene( training );
        EditorSceneManager.ClosePreviewScene( runtime );
      }
    }

    private static MonoBehaviour[] Components( Scene scene )
      => scene.GetRootGameObjects().SelectMany( root => root.GetComponentsInChildren<MonoBehaviour>( true ) )
        .Where( component => component != null ).ToArray();

    private static Dictionary<string, MonoBehaviour> BodiesByUuid( Transform model )
      => model.GetComponentsInChildren<MonoBehaviour>( true ).Where( c => c != null && c.GetType().Name == "RigidBody" )
        .ToDictionary( c => Property<string>( c.GetComponents<MonoBehaviour>().Single(
          component => component != null && component.GetType().Name == "Uuid" ), "Str" ) );

    private static MonoBehaviour Swing( Transform model )
      => model.GetComponentsInChildren<MonoBehaviour>( true ).Single(
        component => component != null && component.GetType().Name == "Constraint" && component.name == "Hinge_Swing" );

    private static T Property<T>( object target, string name )
      => (T)target.GetType().GetProperty( name ).GetValue( target );

    private static void AssertRotation( Quaternion actual, Quaternion expected )
      => Assert.That( Mathf.Abs( Quaternion.Dot( actual.normalized, expected.normalized ) ), Is.GreaterThan( 1f - 1e-6f ) );

    private readonly struct PoseSnapshot
    {
      private readonly Vector3 m_position;
      private readonly Quaternion m_rotation;
      private readonly Vector3 m_scale;

      public PoseSnapshot( Transform transform )
      {
        m_position = transform.localPosition;
        m_rotation = transform.localRotation;
        m_scale = transform.localScale;
      }

      public void AssertMatches( Transform transform )
      {
        Assert.That( Vector3.Distance( transform.localPosition, m_position ), Is.LessThan( 1e-6f ), transform.name );
        AssertRotation( transform.localRotation, m_rotation );
        Assert.That( Vector3.Distance( transform.localScale, m_scale ), Is.LessThan( 1e-6f ), transform.name );
      }
    }
  }
}
