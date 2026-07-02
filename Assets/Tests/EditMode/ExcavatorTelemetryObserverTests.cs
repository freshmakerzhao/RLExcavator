using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class ExcavatorTelemetryObserverTests
  {
    [Test]
    public void ObserverReportsMissingTargetInInspectorFields()
    {
      Type observerType = FindType( "RLExcavator.ExcavatorTelemetryObserver" );

      var gameObject = new GameObject( "ExcavatorTelemetryObserverTest" );
      try {
        Component observer = gameObject.AddComponent( observerType );
        SetBoolField( observer, "autoFindReferences", false );
        MethodInfo refresh = observerType.GetMethod( "RefreshTelemetry", BindingFlags.Instance | BindingFlags.Public );

        Assert.That( refresh, Is.Not.Null, "ExcavatorTelemetryObserver must expose RefreshTelemetry()." );

        refresh.Invoke( observer, null );

        Assert.That( GetBoolField( observer, "targetAssigned" ), Is.False );
        Assert.That( GetStringField( observer, "targetName" ), Is.EqualTo( "未绑定挖掘机。" ) );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( gameObject );
      }
    }

    [Test]
    public void ObserverHasExplicitExcavatorReference()
    {
      Type observerType = FindType( "RLExcavator.ExcavatorTelemetryObserver" );

      FieldInfo excavatorField = observerType.GetField( "excavator", BindingFlags.Instance | BindingFlags.Public );

      Assert.That( excavatorField, Is.Not.Null, "Telemetry observer must expose an excavator field for drag-and-drop binding." );
    }

    [Test]
    public void ObserverExposesBucketPitchCalibrationFields()
    {
      Type observerType = FindType( "RLExcavator.ExcavatorTelemetryObserver" );

      Assert.That( observerType.GetField( "bucketTip", BindingFlags.Instance | BindingFlags.Public ), Is.Not.Null );
      Assert.That( observerType.GetField( "bucketAttitudeReference", BindingFlags.Instance | BindingFlags.Public ), Is.Not.Null );
      Assert.That( observerType.GetField( "bucketPitchFrame", BindingFlags.Instance | BindingFlags.Public ), Is.Not.Null );
      Assert.That( observerType.GetField( "bucketPitchDeg", BindingFlags.Instance | BindingFlags.Public ), Is.Not.Null );
      Assert.That( observerType.GetField( "moveToDigPitchErrorDeg", BindingFlags.Instance | BindingFlags.Public ), Is.Not.Null );
      Assert.That( observerType.GetField( "carryMaterialPitchErrorDeg", BindingFlags.Instance | BindingFlags.Public ), Is.Not.Null );
    }

    [Test]
    public void ObserverDoesNotExposeReachableSamplingControls()
    {
      Type observerType = FindType( "RLExcavator.ExcavatorTelemetryObserver" );

      Assert.That( observerType.GetField( "recordReachableSamples", BindingFlags.Instance | BindingFlags.Public ), Is.Null );
      Assert.That( observerType.GetField( "reachableSamplingMode", BindingFlags.Instance | BindingFlags.Public ), Is.Null );
      Assert.That( observerType.GetField( "reachableSampleCount", BindingFlags.Instance | BindingFlags.Public ), Is.Null );
      Assert.That( observerType.GetField( "reachableSampleLocalPoints", BindingFlags.Instance | BindingFlags.Public ), Is.Null );
      Assert.That( observerType.GetField( "moveToDigReachable", BindingFlags.Instance | BindingFlags.Public ), Is.Null );
      Assert.That( observerType.GetField( "carryMaterialReachable", BindingFlags.Instance | BindingFlags.Public ), Is.Null );
      Assert.That( observerType.GetField( "drawReachableSamplesInScene", BindingFlags.Instance | BindingFlags.Public ), Is.Null );
      Assert.That( observerType.GetMethod( "StartReachableSampling", BindingFlags.Instance | BindingFlags.Public ), Is.Null );
      Assert.That( observerType.GetMethod( "StopReachableSampling", BindingFlags.Instance | BindingFlags.Public ), Is.Null );
      Assert.That( observerType.GetMethod( "ClearReachableSamples", BindingFlags.Instance | BindingFlags.Public ), Is.Null );
      Assert.That( observerType.GetMethod( "CaptureReachableSampleNow", BindingFlags.Instance | BindingFlags.Public ), Is.Null );
      Assert.That( observerType.GetMethod( "ApplyPersistedReachableSamples", BindingFlags.Instance | BindingFlags.Public ), Is.Null );
      Assert.That( observerType.GetMethod( "SetMoveToDigTargetPitchFromCurrent", BindingFlags.Instance | BindingFlags.Public ), Is.Null );
      Assert.That( observerType.GetMethod( "SetCarryTargetPitchFromCurrent", BindingFlags.Instance | BindingFlags.Public ), Is.Null );

      Assert.That( TryFindType( "RLExcavator.ReachableSamplingMode" ), Is.Null );
      Assert.That( TryFindType( "RLExcavator.ReachableWorkspaceTelemetry" ), Is.Null );
    }

    [Test]
    public void ObserverHidesRuntimeTelemetryFieldsFromDefaultInspector()
    {
      Type observerType = FindType( "RLExcavator.ExcavatorTelemetryObserver" );

      AssertFieldHasAttribute<HideInInspector>( observerType, "targetAssigned" );
      AssertFieldHasAttribute<HideInInspector>( observerType, "targetName" );
      AssertFieldHasAttribute<HideInInspector>( observerType, "refreshTime" );
      AssertFieldHasAttribute<HideInInspector>( observerType, "boom" );
      AssertFieldHasAttribute<HideInInspector>( observerType, "bucketPitchDeg" );
      AssertFieldHasAttribute<HideInInspector>( observerType, "moveToDigPitchErrorDeg" );
      AssertFieldHasAttribute<HideInInspector>( observerType, "observedTransformState" );
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

    private static Type TryFindType( string fullName )
    {
      foreach ( Assembly assembly in AppDomain.CurrentDomain.GetAssemblies() ) {
        Type type = assembly.GetType( fullName );
        if ( type != null )
          return type;
      }

      return null;
    }

    private static bool GetBoolField( object target, string fieldName )
    {
      FieldInfo field = target.GetType().GetField( fieldName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( field, Is.Not.Null, "Missing public field: " + fieldName );
      return (bool)field.GetValue( target );
    }

    private static void SetBoolField( object target, string fieldName, bool value )
    {
      FieldInfo field = target.GetType().GetField( fieldName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( field, Is.Not.Null, "Missing public field: " + fieldName );
      field.SetValue( target, value );
    }

    private static string GetStringField( object target, string fieldName )
    {
      FieldInfo field = target.GetType().GetField( fieldName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( field, Is.Not.Null, "Missing public field: " + fieldName );
      return (string)field.GetValue( target );
    }

    private static void AssertFieldHasAttribute<TAttribute>( Type type, string fieldName )
      where TAttribute : Attribute
    {
      FieldInfo field = type.GetField( fieldName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( field, Is.Not.Null, "Missing public field: " + fieldName );
      Assert.That(
        field.GetCustomAttributes( typeof( TAttribute ), true ).Length,
        Is.GreaterThan( 0 ),
        $"Missing {typeof( TAttribute ).Name} on {fieldName}." );
    }
  }
}
