using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RLExcavator.Tests
{
  public class RealExecutionTrainingBuildTests
  {
    private const string Source = "Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPathMission.unity";

    [TestCase("train", 4)]
    [TestCase("benchmark", 1)]
    [TestCase("sampler", 1)]
    public void ConfigureIsolatedExperimentReplacesOldCourseAndPreservesPolicyContract(
      string mode, int activeCount )
    {
      byte[] before = File.ReadAllBytes( Source );
      Scene previous = SceneManager.GetActiveScene();
      Scene scene = EditorSceneManager.OpenScene( Source, OpenSceneMode.Additive );
      var profile = new TextAsset( "{}" );
      var cases = new TextAsset( "{}" );
      try {
        Builder().GetMethod( "ConfigureScene", BindingFlags.Public | BindingFlags.Static )
          .Invoke( null, new object[] { scene, mode, profile, cases } );
        Type agentType = RuntimeType( "ExcavatorTrajectoryAgent" );
        Component[] agents = scene.GetRootGameObjects()
          .SelectMany( root => root.GetComponentsInChildren( agentType, true ) )
          .Where( item => item.gameObject.activeInHierarchy ).ToArray();
        Assert.That( agents.Length, Is.EqualTo( activeCount ) );
        foreach ( Component agent in agents ) {
          Assert.That( agent.GetComponent( RuntimeType( "ContinuousPathMissionCourse" ) ), Is.Null );
          Assert.That( agent.GetComponent( RuntimeType( "ContinuousPathBenchmark" ) ), Is.Null );
          Assert.That( agent.GetComponent( RuntimeType( "RealExecutionCourse" ) ), Is.Not.Null );
          Component runtime = agent.GetComponent( RuntimeType( "RealExecutionTrainingRuntime" ) );
          Assert.That( runtime, Is.Not.Null );
          var runtimeData = new SerializedObject( runtime );
          Assert.That( runtimeData.FindProperty( "runMode" ).stringValue, Is.EqualTo( mode ) );
          Assert.That( runtimeData.FindProperty( "executionProfile" ).objectReferenceValue, Is.SameAs( profile ) );
          Assert.That( runtimeData.FindProperty( "courseCases" ).objectReferenceValue, Is.SameAs( cases ) );
          Component tracking = agent.GetComponent( RuntimeType( "ContinuousPathTracking" ) );
          var trackingData = new SerializedObject( tracking );
          Assert.That( trackingData.FindProperty( "resetClockAfterHoming" ).boolValue, Is.True );
          Assert.That( trackingData.FindProperty( "useGeometricContourCost" ).boolValue, Is.False );
          Assert.That( trackingData.FindProperty( "contourCostPerSecond" ).floatValue, Is.EqualTo( 0.5f ) );
          Assert.That( trackingData.FindProperty( "smoothTrainingReferenceProbability" ).floatValue, Is.Zero );
          Assert.That( (int)RuntimeType( "ContinuousPathTracking" ).GetField( "ObservationSize" )
            .GetRawConstantValue(), Is.EqualTo( 47 ) );
          Type behaviorType = Type.GetType( "Unity.MLAgents.Policies.BehaviorParameters, Unity.ML-Agents", true );
          Component behavior = agent.GetComponent( behaviorType );
          object brain = behaviorType.GetProperty( "BrainParameters" ).GetValue( behavior );
          Assert.That( (int)brain.GetType().GetField( "VectorObservationSize" ).GetValue( brain ), Is.EqualTo( 47 ) );
          object spec = brain.GetType().GetProperty( "ActionSpec" ).GetValue( brain );
          Assert.That( (int)spec.GetType().GetProperty( "NumContinuousActions" ).GetValue( spec ), Is.EqualTo( 4 ) );
          Assert.That( behaviorType.GetProperty( "Model" ).GetValue( behavior ), Is.Null );
          var agentData = new SerializedObject( agent );
          Assert.That( agentData.FindProperty( "randomizeCommandDeadzonesOnEpisodeBegin" ).boolValue, Is.False );
          Assert.That( agentData.FindProperty( "randomizeTaskModeOnEpisodeBegin" ).boolValue, Is.False );
          Assert.That( ((Behaviour)agent).enabled, Is.EqualTo( mode != "sampler" ) );
          Type decisionType = Type.GetType( "Unity.MLAgents.DecisionRequester, Unity.ML-Agents", true );
          var decision = (Behaviour)agent.GetComponent( decisionType );
          if ( mode == "sampler" ) Assert.That( decision, Is.Null );
          else Assert.That( decision.enabled, Is.True );
        }
        if ( mode == "train" ) {
          int[] modes = agents.Select( agent => new SerializedObject( agent )
            .FindProperty( "taskMode" ).enumValueIndex ).ToArray();
          Assert.That( modes.Distinct().Count(), Is.EqualTo( 2 ) );
          Assert.That( modes.GroupBy( value => value ).All( group => group.Count() == 2 ), Is.True );
        }
        Assert.DoesNotThrow( () => Builder().GetMethod( "ValidateConfiguration" )
          .Invoke( null, new object[] { scene, mode } ) );
      }
      finally {
        EditorSceneManager.CloseScene( scene, true );
        if ( previous.IsValid() && previous.isLoaded ) SceneManager.SetActiveScene( previous );
        UnityEngine.Object.DestroyImmediate( profile );
        UnityEngine.Object.DestroyImmediate( cases );
      }
      CollectionAssert.AreEqual( before, File.ReadAllBytes( Source ), "Configuring a copy must never save the source." );
    }

    [Test]
    public void ExistingSceneCannotBeOverwritten()
    {
      byte[] before = File.ReadAllBytes( Source );
      var error = Assert.Throws<TargetInvocationException>( () => Builder().GetMethod( "CopyScene" )
        .Invoke( null, new object[] { Source, Source } ) );
      StringAssert.Contains( "overwrite", error.InnerException.Message );
      CollectionAssert.AreEqual( before, File.ReadAllBytes( Source ) );
    }

    [Test]
    public void TrainingWithoutFrozenCasesFailsBeforeTouchingAnyScene()
    {
      var profile = new TextAsset( "{}" );
      try {
        var error = Assert.Throws<TargetInvocationException>( () => Builder().GetMethod( "ConfigureScene" )
          .Invoke( null, new object[] { default(Scene), "train", profile, null } ) );
        StringAssert.Contains( "freeze feasible cases", error.InnerException.Message );
      }
      finally { UnityEngine.Object.DestroyImmediate( profile ); }
    }

    private static Type Builder()
      => Type.GetType( "RLExcavator.Editor.RealExecutionTrainingBuild, Assembly-CSharp-Editor", true );

    private static Type RuntimeType( string name )
      => Type.GetType( "RLExcavator." + name + ", Assembly-CSharp", true );
  }
}
