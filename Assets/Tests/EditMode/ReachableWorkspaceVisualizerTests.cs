using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace RLExcavator.Tests
{
  public class ReachableWorkspaceVisualizerTests
  {
    [Test]
    public void DefaultVolumeAnchorsUseManualTwentyPointLayout()
    {
      Type workspaceType = FindType( "RLExcavator.ReachableWorkspaceVisualizer" );

      Vector3[] anchors = InvokeStatic<Vector3[]>( workspaceType, "Env01VolumeAnchors" );

      Assert.That( GetStaticInt( workspaceType, "DirectionCount" ), Is.EqualTo( 5 ) );
      Assert.That( GetStaticInt( workspaceType, "VolumeCornerCount" ), Is.EqualTo( 4 ) );
      Assert.That( GetStaticInt( workspaceType, "VolumeAnchorCount" ), Is.EqualTo( 20 ) );
      Assert.That( anchors, Has.Length.EqualTo( GetStaticInt( workspaceType, "VolumeAnchorCount" ) ) );
    }

    [Test]
    public void SelectVolumeAnchorWrapsIndex()
    {
      Type workspaceType = FindType( "RLExcavator.ReachableWorkspaceVisualizer" );

      var root = new GameObject( "ReachableWorkspace" );
      try {
        Component workspace = root.AddComponent( workspaceType );

        InvokePublicMethod( workspace, "SelectVolumeAnchor", -1 );
        Assert.That( GetIntField( workspace, "selectedVolumeAnchorIndex" ), Is.EqualTo( GetStaticInt( workspaceType, "VolumeAnchorCount" ) - 1 ) );

        InvokePublicMethod( workspace, "SelectVolumeAnchor", GetStaticInt( workspaceType, "VolumeAnchorCount" ) );
        Assert.That( GetIntField( workspace, "selectedVolumeAnchorIndex" ), Is.EqualTo( 0 ) );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( root );
      }
    }

    [Test]
    public void SetSelectedVolumeAnchorWorldPositionStoresFrameLocalPoint()
    {
      Type workspaceType = FindType( "RLExcavator.ReachableWorkspaceVisualizer" );

      var frameObject = new GameObject( "WorkspaceFrame" );
      var workspaceObject = new GameObject( "ReachableWorkspace" );
      try {
        frameObject.transform.position = new Vector3( 10f, 2f, -3f );
        frameObject.transform.rotation = Quaternion.Euler( 0f, 90f, 0f );

        Component workspace = workspaceObject.AddComponent( workspaceType );
        SetObjectField( workspace, "workspaceFrame", frameObject.transform );
        InvokePublicMethod( workspace, "SelectVolumeAnchor", 3 );

        Vector3 expectedLocal = new Vector3( 0.25f, 1.5f, -0.75f );
        InvokePublicMethod( workspace, "SetSelectedVolumeAnchorWorldPosition", frameObject.transform.TransformPoint( expectedLocal ) );

        Vector3[] points = GetVector3ArrayField( workspace, "localVolumeAnchorPoints" );
        Assert.That( points[ 3 ].x, Is.EqualTo( expectedLocal.x ).Within( 0.0001f ) );
        Assert.That( points[ 3 ].y, Is.EqualTo( expectedLocal.y ).Within( 0.0001f ) );
        Assert.That( points[ 3 ].z, Is.EqualTo( expectedLocal.z ).Within( 0.0001f ) );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( workspaceObject );
        UnityEngine.Object.DestroyImmediate( frameObject );
      }
    }

    [Test]
    public void BakeVolumeAnchorsAcceptsExistingUnchangedEditableObjects()
    {
      Type workspaceType = FindType( "RLExcavator.ReachableWorkspaceVisualizer" );

      var root = new GameObject( "ReachableWorkspace" );
      try {
        Component workspace = root.AddComponent( workspaceType );

        InvokePublicMethod( workspace, "CreateEditableVolumeAnchorObjects" );
        InvokePublicMethod( workspace, "BakeVolumeAnchorsFromEditableObjects" );

        LogAssert.NoUnexpectedReceived();
      }
      finally {
        UnityEngine.Object.DestroyImmediate( root );
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

    private static T InvokeStatic<T>( Type type, string methodName )
    {
      MethodInfo method = type.GetMethod( methodName, BindingFlags.Static | BindingFlags.Public );
      Assert.That( method, Is.Not.Null, "Missing public static method: " + methodName );
      return (T)method.Invoke( null, null );
    }

    private static void InvokePublicMethod( object target, string methodName, params object[] args )
    {
      MethodInfo method = null;
      MethodInfo[] methods = target.GetType().GetMethods( BindingFlags.Instance | BindingFlags.Public );
      for ( int i = 0; i < methods.Length; i++ ) {
        if ( methods[ i ].Name != methodName )
          continue;

        ParameterInfo[] parameters = methods[ i ].GetParameters();
        if ( parameters.Length == args.Length ) {
          method = methods[ i ];
          break;
        }
      }

      Assert.That( method, Is.Not.Null, "Missing public method: " + methodName );
      method.Invoke( target, args );
    }

    private static int GetStaticInt( Type type, string fieldName )
    {
      FieldInfo field = type.GetField( fieldName, BindingFlags.Static | BindingFlags.Public );
      Assert.That( field, Is.Not.Null, "Missing public static field: " + fieldName );
      return (int)field.GetValue( null );
    }

    private static int GetIntField( object target, string fieldName )
    {
      FieldInfo field = target.GetType().GetField( fieldName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( field, Is.Not.Null, "Missing public field: " + fieldName );
      return (int)field.GetValue( target );
    }

    private static void SetObjectField( object target, string fieldName, object value )
    {
      FieldInfo field = target.GetType().GetField( fieldName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( field, Is.Not.Null, "Missing public field: " + fieldName );
      field.SetValue( target, value );
    }

    private static Vector3[] GetVector3ArrayField( object target, string fieldName )
    {
      FieldInfo field = target.GetType().GetField( fieldName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( field, Is.Not.Null, "Missing public field: " + fieldName );
      return (Vector3[])field.GetValue( target );
    }
  }
}
