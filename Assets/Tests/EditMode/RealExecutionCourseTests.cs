using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class RealExecutionCourseTests
  {
    private static Type Course => Type.GetType( "RLExcavator.RealExecutionCourse, Assembly-CSharp", true );
    private static object Call( string name, params object[] args )
      => Course.GetMethod( name, BindingFlags.Static | BindingFlags.Public ).Invoke( null, args );
    private static T Field<T>( object target, string name ) => (T)target.GetType().GetField( name ).GetValue( target );

    [Test]
    public void CandidateCatalogIsDeterministicAndCoversEveryModeDirectionAndLength()
    {
      var first = (Array)Call( "GenerateCandidates", "train", 1123, 4 );
      var second = (Array)Call( "GenerateCandidates", "train", 1123, 4 );
      Assert.That( first.Length, Is.EqualTo( 32 ) );
      var cells = new Dictionary<string, int>();
      for ( int i = 0; i < first.Length; i++ ) {
        object a = first.GetValue( i );
        Assert.That( JsonUtility.ToJson( a ), Is.EqualTo( JsonUtility.ToJson( second.GetValue( i ) ) ) );
        string cell = Field<string>( a, "taskMode" ) + Field<int>( a, "direction" )
          + Field<bool>( a, "longerPath" );
        cells[ cell ] = cells.TryGetValue( cell, out int count ) ? count + 1 : 1;
        Assert.That( Field<Vector3[]>( a, "referenceUnityLocal" ), Is.Empty,
          "Joint-valid candidates are not resolved physical routes." );
      }
      Assert.That( cells.Count, Is.EqualTo( 8 ) );
      Assert.That( cells.Values.All( count => count == 4 ), Is.True );
    }

    [Test]
    public void CandidateSamplesStayInsideTheNewDomainWithoutClampingLegacySeeds()
    {
      foreach ( string split in new[] { "train", "dev", "holdout" } ) {
        var cases = (Array)Call( "GenerateCandidates", split, 9021, 2 );
        Assert.That( cases.Length, Is.EqualTo( 16 ) );
        foreach ( object item in cases ) {
          foreach ( Vector4 q in Field<Vector4[]>( item, "jointSamples" ) ) {
            Assert.That( q.x, Is.InRange( -0.077001f, 0.025501f ) );
            Assert.That( q.y, Is.InRange( -0.087001f, -0.020249f ) );
            Assert.That( q.z, Is.InRange( 0.001299f, 0.082001f ) );
            Assert.That( Mathf.Abs( q.w ), Is.LessThan( 1.6f ) );
          }
        }
      }
    }

    [Test]
    public void StartAndModeMatchedGoalStickPartitionsKeepAllSplitsSeparated()
    {
      foreach ( string mode in new[] { "MoveToDig", "CarryMaterial" } )
        foreach ( string endpoint in new[] { "startJointPose", "goalJointPose" } ) {
          var spans = new List<Vector2>();
          foreach ( string split in new[] { "train", "dev", "holdout" } ) {
            var values = new List<float>();
            for ( int seed = 0; seed < 10; seed++ )
              foreach ( object item in (Array)Call( "GenerateCandidates", split, seed, 2 ) )
                if ( Field<string>( item, "taskMode" ) == mode )
                  values.Add( Field<Vector4>( item, endpoint ).y );
            spans.Add( new Vector2( values.Min(), values.Max() ) );
          }
          for ( int i = 0; i < spans.Count; i++ )
            for ( int j = i + 1; j < spans.Count; j++ )
              Assert.That( spans[ i ].y < spans[ j ].x || spans[ j ].y < spans[ i ].x, Is.True );
        }
    }

    [Test]
    public void RevisedCandidatesUseElevatedQualificationRegionAndNewCatalogIdentities()
    {
      string[] splits = { "train", "dev", "holdout" };
      for ( int i = 0; i < splits.Length; i++ )
        foreach ( object item in (Array)Call( "GenerateCandidates", splits[ i ], 20261115 + i, i == 0 ? 4 : 2 ) ) {
          Vector4 start = Field<Vector4>( item, "startJointPose" );
          Assert.That( start.x, Is.InRange( 0.006f, 0.010f ) );
          Assert.That( start.z, Is.InRange( 0.060f, 0.072f ) );
          Assert.That( Field<string>( item, "id" ), Does.StartWith( splits[ i ] + "_" + ( 20261115 + i ) + "_" ) );
          if ( Field<string>( item, "taskMode" ) == "CarryMaterial" )
            Assert.That( Field<Vector4>( item, "goalJointPose" ).z, Is.EqualTo( 0.082f ) );
        }
    }

    [Test]
    public void GoalBucketScanProducesANewCandidateAndRejectsOutOfRangeValues()
    {
      object candidate = ( (Array)Call( "GenerateCandidates", "dev", 12, 2 ) ).GetValue( 0 );
      string original = JsonUtility.ToJson( candidate );
      object changed = Call( "WithGoalBucket", candidate, 0.075f );
      Assert.That( Field<Vector4>( changed, "goalJointPose" ).z, Is.EqualTo( 0.075f ) );
      Assert.That( Field<Vector4[]>( changed, "jointSamples" ).Last().z, Is.EqualTo( 0.075f ) );
      Assert.That( JsonUtility.ToJson( candidate ), Is.EqualTo( original ) );
      var error = Assert.Throws<TargetInvocationException>( () => Call( "WithGoalBucket", candidate, -0.02f ) );
      Assert.That( error.InnerException, Is.InstanceOf<ArgumentException>() );
    }

    [Test]
    public void PhysicalResolutionChecksPitchClearanceAndNonzeroPathWithoutMutatingInput()
    {
      object candidate = ( (Array)Call( "GenerateCandidates", "dev", 12, 2 ) ).GetValue( 0 );
      string before = JsonUtility.ToJson( candidate );
      Vector4[] joints = Field<Vector4[]>( candidate, "jointSamples" );
      var tips = Enumerable.Range( 0, joints.Length ).Select( i => Vector3.forward * ( 0.1f + i * 0.02f ) ).ToArray();
      float[] pitches = Enumerable.Repeat( 70f, joints.Length ).ToArray();
      bool[] clearance = Enumerable.Repeat( true, joints.Length ).ToArray();
      object resolved = Call( "ResolveReference", candidate, tips, pitches, clearance, "test physical samples" );
      Assert.That( Field<Vector3[]>( resolved, "referenceUnityLocal" ).Length, Is.EqualTo( joints.Length ) );
      Assert.That( JsonUtility.ToJson( candidate ), Is.EqualTo( before ) );
      tips[ 0 ] = Vector3.one * 9f;
      Assert.That( Field<Vector3[]>( resolved, "referenceUnityLocal" )[ 0 ], Is.Not.EqualTo( tips[ 0 ] ) );
      pitches[ pitches.Length - 1 ] = 130f;
      Assert.Throws<TargetInvocationException>(
        () => Call( "ResolveReference", candidate, Field<Vector3[]>( resolved, "referenceUnityLocal" ), pitches, clearance, "bad pitch" ) );
      pitches[ pitches.Length - 1 ] = 70f;
      clearance[ 2 ] = false;
      Assert.Throws<TargetInvocationException>(
        () => Call( "ResolveReference", candidate, Field<Vector3[]>( resolved, "referenceUnityLocal" ), pitches, clearance, "collision sample" ) );
    }

    [Test]
    public void UnresolvedRoutesAndBadResetCannotSilentlyStartTraining()
    {
      var root = new GameObject( "real-course-test" );
      root.SetActive( false );
      try {
        Component course = root.AddComponent( Course );
        Array candidates = (Array)Call( "GenerateCandidates", "train", 1, 4 );
        Course.GetField( "cases" ).SetValue( course, candidates );
        Assert.Throws<TargetInvocationException>(
          () => Course.GetMethod( "CreateReference" ).Invoke( course, new object[] { Vector3.zero } ) );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( root );
      }
    }

    [Test]
    public void PreparedResolvedCaseUsesDeclaredPoseAndChecksActualResetBeforeReference()
    {
      var root = new GameObject( "real-resolved-course-test" );
      root.SetActive( false );
      try {
        Component course = root.AddComponent( Course );
        Type controllerType = Type.GetType( "RLExcavator.ExcavatorController, Assembly-CSharp", true );
        Component controller = root.AddComponent( controllerType );
        Type constraintType = controllerType.GetField( "swing" ).FieldType;
        foreach ( string axis in new[] { "boom", "stick", "bucket" } ) {
          var part = new GameObject( axis );
          part.transform.SetParent( root.transform );
          Component constraint = part.AddComponent( constraintType );
          Array constraints = Array.CreateInstance( constraintType, axis == "boom" ? 2 : 1 );
          for ( int i = 0; i < constraints.Length; i++ ) constraints.SetValue( constraint, i );
          controllerType.GetField( axis ).SetValue( controller, constraints );
        }
        var swing = new GameObject( "swing" );
        swing.transform.SetParent( root.transform );
        controllerType.GetField( "swing" ).SetValue( controller, swing.AddComponent( constraintType ) );
        object candidate = ( (Array)Call( "GenerateCandidates", "train", 12, 4 ) ).GetValue( 0 );
        int count = Field<Vector4[]>( candidate, "jointSamples" ).Length;
        var tips = Enumerable.Range( 0, count ).Select( i => Vector3.forward * ( 0.1f + i * 0.02f ) ).ToArray();
        float targetPitch = Field<float>( candidate, "targetPitchDeg" );
        object resolved = Call( "ResolveReference", candidate, tips,
          Enumerable.Repeat( targetPitch, count ).ToArray(), Enumerable.Repeat( true, count ).ToArray(), "test samples" );
        Array cases = Array.CreateInstance( resolved.GetType(), 1 );
        cases.SetValue( resolved, 0 );
        Course.GetField( "cases" ).SetValue( course, cases );
        object selected = Course.GetMethod( "PrepareEpisode" ).Invoke( course, new object[] { controller } );
        Vector4 desired = Field<Vector4>( selected, "startJointPose" );
        CollectionAssert.AreEqual( new[] { desired.x, desired.x }, Field<float[]>( controller, "boomHomePose" ) );
        var validate = Course.GetMethod( "ValidateReset" );
        Assert.That( validate.Invoke( course, new object[] { desired, Vector4.zero } ), Is.EqualTo( true ) );
        Assert.That( validate.Invoke( course, new object[] { desired + new Vector4( 0.004f, 0f, 0f, 0f ), Vector4.zero } ), Is.EqualTo( false ) );
        Assert.That( validate.Invoke( course, new object[] { desired, new Vector4( 0.01f, 0f, 0f, 0f ) } ), Is.EqualTo( false ) );
        Vector3 actualStart = tips[ 0 ] + Vector3.up * 0.005f;
        var route = (Vector3[])Course.GetMethod( "CreateReference" ).Invoke( course, new object[] { actualStart } );
        Assert.That( route[ 0 ], Is.EqualTo( actualStart ) );
        Assert.That( route.Last(), Is.EqualTo( tips.Last() ) );
        Assert.That( Field<Vector3[]>( resolved, "referenceUnityLocal" )[ 0 ], Is.EqualTo( tips[ 0 ] ) );
        Assert.Throws<TargetInvocationException>(
          () => Course.GetMethod( "CreateReference" ).Invoke( course, new object[] { tips[ 0 ] + Vector3.up * 0.03f } ) );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( root );
      }
    }

    [Test]
    public void InvalidSplitIsRejectedRatherThanReusingTrainingCases()
    {
      var error = Assert.Throws<TargetInvocationException>( () => Call( "GenerateCandidates", "typo", 0, 2 ) );
      Assert.That( error.InnerException, Is.InstanceOf<ArgumentException>() );
    }
  }
}
