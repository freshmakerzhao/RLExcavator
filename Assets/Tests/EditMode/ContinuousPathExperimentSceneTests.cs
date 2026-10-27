using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RLExcavator.Tests
{
  public class ContinuousPathExperimentSceneTests
  {
    [Test]
    public void ExistingTrackingLeavesSmoothReferenceSamplingDisabled()
    {
      var go = new GameObject( "curve-default-test" );
      try {
        Type type = Type.GetType( "RLExcavator.ContinuousPathTracking, Assembly-CSharp", true );
        var component = go.AddComponent( type );
        Assert.That( (float)type.GetField( "smoothTrainingReferenceProbability" ).GetValue( component ), Is.Zero );
        Assert.That( (int)type.GetField( "ObservationSize" ).GetRawConstantValue(), Is.EqualTo( 47 ) );
      }
      finally { UnityEngine.Object.DestroyImmediate( go ); }
    }

    [TestCase("Assets/Scenes/Training/ExcavatorRLBench_ContinuousPathV5.unity", 1, 0f)]
    [TestCase("Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPathMissionCurve.unity", 4, 0.7f)]
    public void ExperimentScenesKeepTheExpectedAgentsAndReferenceModes( string path, int activeAgents, float curveProbability )
    {
      Scene previous = SceneManager.GetActiveScene();
      Scene scene = EditorSceneManager.OpenScene( path, OpenSceneMode.Additive );
      try {
        Type type = Type.GetType( "RLExcavator.ExcavatorTrajectoryAgent, Assembly-CSharp", true );
        var agents = scene.GetRootGameObjects().SelectMany( r => r.GetComponentsInChildren( type, true ) )
          .Where( a => a.gameObject.activeInHierarchy ).ToArray();
        Assert.That( agents.Length, Is.EqualTo( activeAgents ) );
        foreach ( var agent in agents ) {
          var tracking = agent.GetComponent( Type.GetType( "RLExcavator.ContinuousPathTracking, Assembly-CSharp", true ) );
          Assert.That( (float)tracking.GetType().GetField( "smoothTrainingReferenceProbability" ).GetValue( tracking ),
            Is.EqualTo( curveProbability ).Within( 1e-6f ) );
          var benchmark = agent.GetComponent( Type.GetType( "RLExcavator.ContinuousPathBenchmark, Assembly-CSharp", true ) );
          Assert.That( benchmark != null, Is.EqualTo( activeAgents == 1 ) );
          if ( activeAgents == 4 ) {
            Assert.That( (bool)tracking.GetType().GetField( "useGeometricContourCost" ).GetValue( tracking ), Is.False,
              "Curve sampling must retain the selected original V5 reward definition." );
            Assert.That( (float)tracking.GetType().GetField( "contourCostPerSecond" ).GetValue( tracking ), Is.EqualTo( 0.5f ) );
          }
        }
      }
      finally {
        EditorSceneManager.CloseScene( scene, true );
        SceneManager.SetActiveScene( previous );
      }
    }
  }
}
