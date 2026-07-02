using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class ExcavationDemoTaskManagerManualControlTests
  {
    [Test]
    public void ManualControlUsesKeyboardTesterWithoutDecisionRequester()
    {
      Type managerType = FindType( "RLExcavator.ExcavationDemoTaskManager" );
      Type controllerType = FindType( "RLExcavator.ExcavatorController" );
      Type agentType = FindType( "RLExcavator.ExcavatorTrajectoryAgent" );
      Type decisionRequesterType = FindType( "Unity.MLAgents.DecisionRequester" );
      Type keyboardTesterType = FindType( "RLExcavator.ExcavatorKeyboardTester" );

      var managerObject = new GameObject( "ManualControlManager" );
      var excavatorObject = new GameObject( "ManualControlExcavator" );
      try {
        Component controller = excavatorObject.AddComponent( controllerType );
        Component agent = excavatorObject.AddComponent( agentType );
        Component decisionRequester = excavatorObject.AddComponent( decisionRequesterType );
        Component manager = managerObject.AddComponent( managerType );

        SetObjectField( manager, "agent", agent );
        SetObjectField( manager, "controller", controller );
        SetObjectField( manager, "decisionRequester", decisionRequester );
        SetNonPublicField( controller, "<IsHoming>k__BackingField", true );

        InvokePublicMethod( manager, "StartManualControl" );

        Assert.That( GetBoolField( controller, "commandInputEnabled" ), Is.True );
        Assert.That( GetBoolProperty( controller, "IsHoming" ), Is.False );
        Assert.That( ( (Behaviour)agent ).enabled, Is.False );
        Assert.That( ( (Behaviour)decisionRequester ).enabled, Is.False );

        Component keyboardTester = excavatorObject.GetComponent( keyboardTesterType );
        Assert.That( keyboardTester, Is.Not.Null );
        Assert.That( ( (Behaviour)keyboardTester ).enabled, Is.True );
        Assert.That( GetEnumFieldName( manager, "state" ), Is.EqualTo( "ManualControl" ) );

        InvokePublicMethod( manager, "StopManualControl" );

        Assert.That( GetBoolField( controller, "commandInputEnabled" ), Is.False );
        Assert.That( ( (Behaviour)keyboardTester ).enabled, Is.False );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( managerObject );
        UnityEngine.Object.DestroyImmediate( excavatorObject );
      }
    }

    [Test]
    public void ManualControlDoesNotRequireTrajectoryAgent()
    {
      Type managerType = FindType( "RLExcavator.ExcavationDemoTaskManager" );
      Type controllerType = FindType( "RLExcavator.ExcavatorController" );
      Type keyboardTesterType = FindType( "RLExcavator.ExcavatorKeyboardTester" );

      var managerObject = new GameObject( "ManualControlNoAgentManager" );
      var excavatorObject = new GameObject( "ManualControlNoAgentExcavator" );
      try {
        Component controller = excavatorObject.AddComponent( controllerType );
        Component manager = managerObject.AddComponent( managerType );

        SetObjectField( manager, "controller", controller );

        InvokePublicMethod( manager, "StartManualControl" );

        Assert.That( GetBoolField( controller, "commandInputEnabled" ), Is.True );
        Component keyboardTester = excavatorObject.GetComponent( keyboardTesterType );
        Assert.That( keyboardTester, Is.Not.Null );
        Assert.That( ( (Behaviour)keyboardTester ).enabled, Is.True );
        Assert.That( GetEnumFieldName( manager, "state" ), Is.EqualTo( "ManualControl" ) );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( managerObject );
        UnityEngine.Object.DestroyImmediate( excavatorObject );
      }
    }

    [Test]
    public void ApplyingBucketTipToWorkspaceBoundaryDoesNotRequireTrajectoryAgent()
    {
      var managerObject = new GameObject( "ApplyBucketTipNoAgentManager" );
      var excavatorObject = new GameObject( "ApplyBucketTipNoAgentExcavator" );
      var workspaceObject = new GameObject( "MoveToDig" );

      try {
        Type managerType = FindType( "RLExcavator.ExcavationDemoTaskManager" );
        Type controllerType = FindType( "RLExcavator.ExcavatorController" );
        Type registryType = FindType( "RLExcavator.ExcavatorFrameRegistry" );
        Type workspaceType = FindType( "RLExcavator.ReachableWorkspaceVisualizer" );

        var framesObject = new GameObject( "Frames" );
        framesObject.transform.SetParent( excavatorObject.transform, false );

        var machineRootObject = new GameObject( "MachineRoot" );
        machineRootObject.transform.SetParent( framesObject.transform, false );
        machineRootObject.transform.localPosition = Vector3.zero;

        var bucketTipObject = new GameObject( "BucketTip" );
        bucketTipObject.transform.SetParent( excavatorObject.transform, false );
        bucketTipObject.transform.position = new Vector3( 1.25f, 2.5f, -0.75f );

        Component controller = excavatorObject.AddComponent( controllerType );
        Component registry = excavatorObject.AddComponent( registryType );
        SetObjectField( registry, "machineRoot", machineRootObject.transform );
        SetObjectField( registry, "bucketTip", bucketTipObject.transform );
        SetObjectField( registry, "fallbackMachineRootToSelf", false );

        Component workspace = workspaceObject.AddComponent( workspaceType );
        SetObjectField( workspace, "frameRegistry", registry );
        SetObjectField( workspace, "useFrameRegistryMachineRoot", true );
        SetObjectField( workspace, "workspaceFrame", machineRootObject.transform );
        SetObjectField( workspace, "selectedVolumeAnchorIndex", 0 );
        Vector3 before = GetVector3ArrayField( workspace, "localVolumeAnchorPoints" )[ 0 ];

        Component manager = managerObject.AddComponent( managerType );
        SetObjectField( manager, "controller", controller );
        SetObjectField( manager, "frameRegistry", registry );
        SetObjectField( manager, "reachableWorkspace", workspace );
        SetObjectField( manager, "moveToDigReachableWorkspace", workspace );

        InvokePublicMethod( manager, "ApplyBucketTipToSelectedWorkspaceBoundaryPoint" );

        Vector3 expected = machineRootObject.transform.InverseTransformPoint( bucketTipObject.transform.position );
        Vector3 after = GetVector3ArrayField( workspace, "localVolumeAnchorPoints" )[ 0 ];
        Assert.That( ( after - expected ).sqrMagnitude, Is.LessThan( 1e-8f ) );
        Assert.That( ( after - before ).sqrMagnitude, Is.GreaterThan( 1e-8f ) );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( managerObject );
        UnityEngine.Object.DestroyImmediate( excavatorObject );
        UnityEngine.Object.DestroyImmediate( workspaceObject );
      }
    }

    [Test]
    public void StartupStopsDemoRequesterEvenWhenAgentIsHeuristicOnly()
    {
      Type managerType = FindType( "RLExcavator.ExcavationDemoTaskManager" );
      Type controllerType = FindType( "RLExcavator.ExcavatorController" );
      Type agentType = FindType( "RLExcavator.ExcavatorTrajectoryAgent" );
      Type behaviorParametersType = FindType( "Unity.MLAgents.Policies.BehaviorParameters" );
      Type decisionRequesterType = FindType( "Unity.MLAgents.DecisionRequester" );

      var managerObject = new GameObject( "StartupManager" );
      var excavatorObject = new GameObject( "StartupExcavator" );
      try {
        Component controller = excavatorObject.AddComponent( controllerType );
        Component behaviorParameters = excavatorObject.AddComponent( behaviorParametersType );
        Component agent = excavatorObject.AddComponent( agentType );
        Component decisionRequester = excavatorObject.AddComponent( decisionRequesterType );
        Component manager = managerObject.AddComponent( managerType );

        SetProperty(
          behaviorParameters,
          "BehaviorType",
          EnumValue( behaviorParametersType.GetProperty( "BehaviorType" ).PropertyType, "HeuristicOnly" ) );
        SetObjectField( manager, "agent", agent );
        SetObjectField( manager, "controller", controller );
        SetObjectField( manager, "decisionRequester", decisionRequester );

        Assert.That( ( (Behaviour)decisionRequester ).enabled, Is.True );

        InvokePrivateMethod( manager, "Start" );

        Assert.That( GetBoolField( controller, "commandInputEnabled" ), Is.False );
        Assert.That( ( (Behaviour)agent ).enabled, Is.False );
        Assert.That( ( (Behaviour)decisionRequester ).enabled, Is.False );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( managerObject );
        UnityEngine.Object.DestroyImmediate( excavatorObject );
      }
    }

    [Test]
    public void SwitchingTrackingTabDoesNotForceInferenceMode()
    {
      Type managerType = FindType( "RLExcavator.ExcavationDemoTaskManager" );
      Type uiType = FindType( "RLExcavator.ExcavationDemoRuntimeUI" );
      Type agentType = FindType( "RLExcavator.ExcavatorTrajectoryAgent" );
      Type behaviorParametersType = FindType( "Unity.MLAgents.Policies.BehaviorParameters" );

      var managerObject = new GameObject( "TabSwitchManager" );
      var excavatorObject = new GameObject( "TabSwitchExcavator" );
      var uiObject = new GameObject( "TabSwitchUI" );
      try {
        Component behaviorParameters = excavatorObject.AddComponent( behaviorParametersType );
        Component agent = excavatorObject.AddComponent( agentType );
        Component manager = managerObject.AddComponent( managerType );
        Component ui = uiObject.AddComponent( uiType );

        SetProperty(
          behaviorParameters,
          "BehaviorType",
          EnumValue( behaviorParametersType.GetProperty( "BehaviorType" ).PropertyType, "HeuristicOnly" ) );
        SetObjectField( manager, "agent", agent );
        SetObjectField( ui, "taskManager", manager );

        Type tabType = uiType.GetNestedType( "DemoTab", BindingFlags.NonPublic );
        InvokePrivateMethod( ui, "SwitchTab", EnumValue( tabType, "TrajectoryTracking" ) );

        Assert.That( GetPropertyString( behaviorParameters, "BehaviorType" ), Is.EqualTo( "HeuristicOnly" ) );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( uiObject );
        UnityEngine.Object.DestroyImmediate( managerObject );
        UnityEngine.Object.DestroyImmediate( excavatorObject );
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

    private static void InvokePublicMethod( object target, string methodName )
    {
      MethodInfo method = target.GetType().GetMethod( methodName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( method, Is.Not.Null, "Missing public method: " + methodName );
      method.Invoke( target, null );
    }

    private static void SetObjectField( object target, string fieldName, object value )
    {
      FieldInfo field = target.GetType().GetField( fieldName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( field, Is.Not.Null, "Missing public field: " + fieldName );
      field.SetValue( target, value );
    }

    private static void SetProperty( object target, string propertyName, object value )
    {
      PropertyInfo property = target.GetType().GetProperty( propertyName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( property, Is.Not.Null, "Missing public property: " + propertyName );
      property.SetValue( target, value );
    }

    private static bool GetBoolField( object target, string fieldName )
    {
      FieldInfo field = target.GetType().GetField( fieldName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( field, Is.Not.Null, "Missing public field: " + fieldName );
      return (bool)field.GetValue( target );
    }

    private static void SetNonPublicField( object target, string fieldName, object value )
    {
      FieldInfo field = target.GetType().GetField( fieldName, BindingFlags.Instance | BindingFlags.NonPublic );
      Assert.That( field, Is.Not.Null, "Missing non-public field: " + fieldName );
      field.SetValue( target, value );
    }

    private static string GetEnumFieldName( object target, string fieldName )
    {
      FieldInfo field = target.GetType().GetField( fieldName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( field, Is.Not.Null, "Missing public field: " + fieldName );
      object value = field.GetValue( target );
      return value != null ? value.ToString() : string.Empty;
    }

    private static Vector3[] GetVector3ArrayField( object target, string fieldName )
    {
      FieldInfo field = target.GetType().GetField( fieldName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( field, Is.Not.Null, "Missing public field: " + fieldName );
      var value = field.GetValue( target ) as Vector3[];
      Assert.That( value, Is.Not.Null, "Field is not a Vector3 array: " + fieldName );
      return value;
    }

    private static string GetPropertyString( object target, string propertyName )
    {
      PropertyInfo property = target.GetType().GetProperty( propertyName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( property, Is.Not.Null, "Missing public property: " + propertyName );
      object value = property.GetValue( target );
      return value != null ? value.ToString() : string.Empty;
    }

    private static bool GetBoolProperty( object target, string propertyName )
    {
      PropertyInfo property = target.GetType().GetProperty( propertyName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( property, Is.Not.Null, "Missing public property: " + propertyName );
      return (bool)property.GetValue( target );
    }

    private static object EnumValue( Type enumType, string name )
    {
      Assert.That( enumType, Is.Not.Null, "Missing enum type." );
      return Enum.Parse( enumType, name );
    }

    private static void InvokePrivateMethod( object target, string methodName, params object[] args )
    {
      MethodInfo method = target.GetType().GetMethod( methodName, BindingFlags.Instance | BindingFlags.NonPublic );
      Assert.That( method, Is.Not.Null, "Missing private method: " + methodName );
      method.Invoke( target, args );
    }
  }
}
