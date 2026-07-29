using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class InferenceActionLoggingTests
  {
    [Test]
    public void TrajectoryAgentPublishesRawPolicyActionBeforeDeadzoneFiltering()
    {
      Type agentType = FindType( "RLExcavator.ExcavatorTrajectoryAgent" );
      var gameObject = new GameObject( "raw-policy-action-test" );

      try {
        Component agent = gameObject.AddComponent( agentType );
        object actions = CreateContinuousActionBuffer( 0.03f, -0.04f, 0.05f, -0.06f );

        try {
          agentType.GetMethod( "OnActionReceived", BindingFlags.Instance | BindingFlags.Public )
            .Invoke( agent, new object[] { actions } );
        }
        catch ( TargetInvocationException exception ) when ( exception.InnerException is NullReferenceException ) {
          // This isolated test intentionally omits a full ML-Agents Academy/trajectory setup.
          // Raw action capture must happen before the environment-dependent reward path.
        }

        PropertyInfo property = agentType.GetProperty( "LastRawPolicyAction", BindingFlags.Instance | BindingFlags.Public );
        Assert.That( property, Is.Not.Null, "Trajectory agent must expose the raw policy action for inference diagnostics." );
        Assert.That( (Vector4)property.GetValue( agent ), Is.EqualTo( new Vector4( 0.03f, -0.04f, 0.05f, -0.06f ) ) );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( gameObject );
      }
    }

    [Test]
    public void RecorderAppendsRawPolicyActionsAfterLegacyColumns()
    {
      Type agentType = FindType( "RLExcavator.ExcavatorTrajectoryAgent" );
      Type controllerType = FindType( "RLExcavator.ExcavatorController" );
      Type recorderType = FindType( "RLExcavator.OpenLoopVelocityRecorder" );
      var gameObject = new GameObject( "raw-policy-action-csv-test" );
      string directory = Path.Combine( Application.temporaryCachePath, "raw-action-csv-" + Guid.NewGuid().ToString( "N" ) );

      try {
        Component agent = gameObject.AddComponent( agentType );
        PublishRawAction( agentType, agent, 0.03f, -0.04f, 0.05f, -0.06f );
        Component controller = gameObject.AddComponent( controllerType );
        Component recorder = gameObject.AddComponent( recorderType );
        SetPublicField( recorderType, recorder, "agent", agent );
        SetPublicField( recorderType, recorder, "controller", controller );
        SetPublicField( recorderType, recorder, "outputDirectoryName", directory );
        SetPublicField( recorderType, recorder, "recordOnCycleTask", false );

        InvokePublic( recorderType, recorder, "StartRecording" );
        InvokePublic( recorderType, recorder, "StopAndExport" );

        string path = (string)recorderType.GetProperty( "LastExportPath", BindingFlags.Instance | BindingFlags.Public ).GetValue( recorder );
        string[] lines = File.ReadAllLines( path );
        string header = Array.Find( lines, line => !line.StartsWith( "#", StringComparison.Ordinal ) );
        string sample = Array.Find( lines, line => line.Length > 0 && !line.StartsWith( "#", StringComparison.Ordinal ) && line != header );
        string[] columns = header.Split( ',' );
        string[] values = sample.Split( ',' );

        Assert.That( columns.Length, Is.EqualTo( values.Length ) );
        Assert.That( columns[ columns.Length - 4 ], Is.EqualTo( "boom_policy_action_raw" ) );
        Assert.That( columns[ columns.Length - 1 ], Is.EqualTo( "swing_policy_action_raw" ) );
        Assert.That( float.Parse( values[ values.Length - 4 ], CultureInfo.InvariantCulture ), Is.EqualTo( 0.03f ).Within( 1e-6f ) );
        Assert.That( float.Parse( values[ values.Length - 1 ], CultureInfo.InvariantCulture ), Is.EqualTo( -0.06f ).Within( 1e-6f ) );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( gameObject );
        if ( Directory.Exists( directory ) )
          Directory.Delete( directory, true );
      }
    }

    [Test]
    public void RecorderResolvesAgentAndControllerFromEnvironmentChildren()
    {
      Type agentType = FindType( "RLExcavator.ExcavatorTrajectoryAgent" );
      Type controllerType = FindType( "RLExcavator.ExcavatorController" );
      Type cycleTaskType = FindType( "RLExcavator.ExcavationCycleTask" );
      Type recorderType = FindType( "RLExcavator.OpenLoopVelocityRecorder" );
      var environment = new GameObject( "recorder-environment-root" );
      var rig = new GameObject( "excavator-rig" );
      string directory = Path.Combine( Application.temporaryCachePath, "recorder-child-reference-" + Guid.NewGuid().ToString( "N" ) );

      try {
        rig.transform.SetParent( environment.transform );
        rig.AddComponent( controllerType );
        rig.AddComponent( agentType );
        Component cycleTask = rig.AddComponent( cycleTaskType );
        Component recorder = environment.AddComponent( recorderType );
        SetPublicField( recorderType, recorder, "outputDirectoryName", directory );
        SetPublicField( recorderType, recorder, "recordOnCycleTask", false );

        InvokePublic( recorderType, recorder, "StartRecording" );

        Assert.That(
          recorderType.GetField( "cycleTask", BindingFlags.Instance | BindingFlags.Public ).GetValue( recorder ),
          Is.EqualTo( cycleTask ),
          "A recorder attached to an environment root must resolve the child cycle task for automatic recording." );
        Assert.That(
          (bool)recorderType.GetProperty( "IsRecording", BindingFlags.Instance | BindingFlags.Public ).GetValue( recorder ),
          Is.True,
          "A recorder attached to an environment root must resolve the rig agent/controller below it." );

        InvokePublic( recorderType, recorder, "StopAndExport" );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( environment );
        if ( Directory.Exists( directory ) )
          Directory.Delete( directory, true );
      }
    }

    [Test]
    public void RecorderResolvesDefaultOutputDirectoryUnderProjectRoot()
    {
      Type recorderType = FindType( "RLExcavator.OpenLoopVelocityRecorder" );
      var gameObject = new GameObject( "recorder-output-directory-test" );

      try {
        Component recorder = gameObject.AddComponent( recorderType );
        MethodInfo resolveMethod = recorderType.GetMethod(
          "ResolveOutputDirectory",
          BindingFlags.Instance | BindingFlags.NonPublic );

        Assert.That( resolveMethod, Is.Not.Null, "Recorder must resolve its configured output directory." );
        string actual = (string)resolveMethod.Invoke( recorder, null );
        string expected = Path.GetFullPath(
          Path.Combine( Application.dataPath, "..", "open_loop_velocity_exports" ) );

        Assert.That( actual, Is.EqualTo( expected ) );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( gameObject );
      }
    }

    private static Type FindType( string fullName )
    {
      foreach ( Assembly assembly in AppDomain.CurrentDomain.GetAssemblies() ) {
        Type type = assembly.GetType( fullName );
        if ( type != null )
          return type;
      }

      Assert.Fail( "Type not found: " + fullName );
      return null;
    }

    private static object CreateContinuousActionBuffer( params float[] values )
    {
      Type actionBuffersType = FindType( "Unity.MLAgents.Actuators.ActionBuffers" );

      return Activator.CreateInstance( actionBuffersType, new object[] { values, Array.Empty<int>() } );
    }

    private static void PublishRawAction( Type agentType, object agent, params float[] values )
    {
      try {
        agentType.GetMethod( "OnActionReceived", BindingFlags.Instance | BindingFlags.Public )
          .Invoke( agent, new[] { CreateContinuousActionBuffer( values ) } );
      }
      catch ( TargetInvocationException exception ) when ( exception.InnerException is NullReferenceException ) {
        // This isolated test intentionally omits a full ML-Agents Academy/trajectory setup.
      }
    }

    private static void SetPublicField( Type type, object target, string fieldName, object value )
    {
      FieldInfo field = type.GetField( fieldName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( field, Is.Not.Null, "Missing public field: " + fieldName );
      field.SetValue( target, value );
    }

    private static void InvokePublic( Type type, object target, string methodName )
    {
      MethodInfo method = type.GetMethod( methodName, BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null );
      Assert.That( method, Is.Not.Null, "Missing public method: " + methodName );
      method.Invoke( target, null );
    }
  }
}
