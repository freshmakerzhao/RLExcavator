using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class MachineProfileCommandDeadzoneTests
  {
    [Test]
    public void SharedProfileLoadsMeasuredDirectionalCommandDeadzones()
    {
      MachineProfile profile = MachineProfileLoader.LoadFromPath( SharedProfilePath() );

      AssertDeadzone( profile.GetActuator( "boom" ), 0.05698005698f, 0.108108108108f );
      AssertDeadzone( profile.GetActuator( "stick" ), 0.045045045045f, 0.089635854342f );
      AssertDeadzone( profile.GetActuator( "bucket" ), 0.058479532164f, 0.047732696897f );
      AssertDeadzone( profile.GetActuator( "swing" ), 0.090175344686f, 0.119264165553f );
    }

    [Test]
    public void ControllerZerosCommandsAtOrInsideDirectionalDeadzone()
    {
      MachineProfile profile = MachineProfileLoader.LoadFromPath( SharedProfilePath() );
      var gameObject = new GameObject( "deadzone-controller-test" );

      try {
        Type controllerType = FindType( "RLExcavator.ExcavatorController" );
        Component controller = gameObject.AddComponent( controllerType );
        Invoke( controllerType, controller, "ApplyMachineProfileSpeeds", profile );
        Invoke( controllerType, controller, "ApplyMachineProfileCommandDeadzones", profile );

        Invoke(
          controllerType,
          controller,
          "SetNormalizedSpeeds",
          profile.GetActuator( "boom" ).CommandDeadzonePositiveNormalized,
          -profile.GetActuator( "stick" ).CommandDeadzoneNegativeNormalized,
          profile.GetActuator( "bucket" ).CommandDeadzonePositiveNormalized,
          -profile.GetActuator( "swing" ).CommandDeadzoneNegativeNormalized );

        Assert.That( GetFloatProperty( controllerType, controller, "LastBoomCommand" ), Is.Zero );
        Assert.That( GetFloatProperty( controllerType, controller, "LastStickCommand" ), Is.Zero );
        Assert.That( GetFloatProperty( controllerType, controller, "LastBucketCommand" ), Is.Zero );
        Assert.That( GetFloatProperty( controllerType, controller, "LastSwingCommand" ), Is.Zero );

        controllerType.GetMethod( "SetNormalizedSpeeds", BindingFlags.Instance | BindingFlags.Public ).Invoke(
          controller,
          new object[] {
            profile.GetActuator( "boom" ).CommandDeadzonePositiveNormalized + 0.01f,
            -(profile.GetActuator( "stick" ).CommandDeadzoneNegativeNormalized + 0.01f),
            profile.GetActuator( "bucket" ).CommandDeadzonePositiveNormalized + 0.01f,
            -(profile.GetActuator( "swing" ).CommandDeadzoneNegativeNormalized + 0.01f)
          } );

        Assert.That( GetFloatProperty( controllerType, controller, "LastBoomCommand" ),
          Is.EqualTo( profile.GetActuator( "boom" ).CommandDeadzonePositiveNormalized + 0.01f ).Within( 1e-6f ) );
        Assert.That( GetFloatProperty( controllerType, controller, "LastStickCommand" ),
          Is.EqualTo( -(profile.GetActuator( "stick" ).CommandDeadzoneNegativeNormalized + 0.01f) ).Within( 1e-6f ) );
        Assert.That( GetFloatProperty( controllerType, controller, "LastBucketCommand" ),
          Is.EqualTo( profile.GetActuator( "bucket" ).CommandDeadzonePositiveNormalized + 0.01f ).Within( 1e-6f ) );
        Assert.That( GetFloatProperty( controllerType, controller, "LastSwingCommand" ),
          Is.EqualTo( -(profile.GetActuator( "swing" ).CommandDeadzoneNegativeNormalized + 0.01f) ).Within( 1e-6f ) );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( gameObject );
      }
    }

    [Test]
    public void TrajectoryRewardMarksOnlyNonzeroActionsInsideDirectionalDeadzones()
    {
      MachineProfile profile = MachineProfileLoader.LoadFromPath( SharedProfilePath() );
      Type agentType = FindType( "RLExcavator.ExcavatorTrajectoryAgent" );
      MethodInfo maskMethod = agentType.GetMethod(
        "DirectionalDeadzoneNonzeroMask",
        BindingFlags.Static | BindingFlags.Public );

      Assert.That( maskMethod, Is.Not.Null, "Trajectory reward must expose its directional deadzone classification." );
      Vector4 mask = (Vector4)maskMethod.Invoke(
        null,
        new object[] {
          new Vector4(
            profile.GetActuator( "boom" ).CommandDeadzonePositiveNormalized,
            -profile.GetActuator( "stick" ).CommandDeadzoneNegativeNormalized,
            profile.GetActuator( "bucket" ).CommandDeadzonePositiveNormalized + 0.01f,
            0f ),
          profile
        } );

      Assert.That( mask, Is.EqualTo( new Vector4( 1f, 1f, 0f, 0f ) ) );
    }

    [Test]
    public void LegacyFlatPenaltyRemainsAvailableForBaselineRegression()
    {
      MachineProfile profile = MachineProfileLoader.LoadFromPath( SharedProfilePath() );
      Type agentType = FindType( "RLExcavator.ExcavatorTrajectoryAgent" );
      MethodInfo penaltyMethod = agentType.GetMethod(
        "DeadzoneNonzeroActionPenalty",
        BindingFlags.Static | BindingFlags.Public );

      Assert.That( penaltyMethod, Is.Not.Null, "The legacy flat deadzone penalty must remain available for baseline regression." );
      float penalty = (float)penaltyMethod.Invoke(
        null,
        new object[] {
          new Vector4(
            profile.GetActuator( "boom" ).CommandDeadzonePositiveNormalized,
            -profile.GetActuator( "stick" ).CommandDeadzoneNegativeNormalized,
            0f,
            0f ),
          profile
        } );

      Assert.That( penalty, Is.EqualTo( -0.006f ).Within( 1e-7f ) );
    }

    [Test]
    public void DeadzoneBumpPenalizesControllerBoundaryUntilEscapeMargin()
    {
      MachineProfile profile = MachineProfileLoader.LoadFromPath( SharedProfilePath() );
      Type agentType = FindType( "RLExcavator.ExcavatorTrajectoryAgent" );
      MethodInfo bumpMethod = agentType.GetMethod(
        "DirectionalDeadzoneBump",
        BindingFlags.Static | BindingFlags.Public );
      Assert.That( bumpMethod, Is.Not.Null, "Trajectory reward must expose the directional deadzone bump." );

      MachineActuatorProfile boom = profile.GetActuator( "boom" );
      float dPos = boom.CommandDeadzonePositiveNormalized;
      float dNeg = boom.CommandDeadzoneNegativeNormalized;
      const float escapeMargin = 0.1f;
      float escapePos = dPos * ( 1f + escapeMargin );
      float escapeNeg = dNeg * ( 1f + escapeMargin );

      float Bump( float a ) => (float)bumpMethod.Invoke( null, new object[] { a, boom, escapeMargin } );

      // 合法静止不罚；控制器边界仍被惩罚，直到动作清出逃逸余量。
      Assert.That( Bump( 0f ), Is.Zero );
      Assert.That( Bump( dPos ), Is.GreaterThan( 0f ) );
      Assert.That( Bump( -dNeg ), Is.GreaterThan( 0f ) );
      Assert.That( Bump( escapePos ), Is.EqualTo( 0f ).Within( 1e-6f ) );
      Assert.That( Bump( -escapeNeg ), Is.EqualTo( 0f ).Within( 1e-6f ) );

      // 峰值按扩展后的逃逸阈值计算；方向由动作符号选择对应死区。
      Assert.That( Bump( escapePos * 0.5f ), Is.EqualTo( 1f ).Within( 1e-5f ) );
      Assert.That( Bump( -escapeNeg * 0.5f ), Is.EqualTo( 1f ).Within( 1e-5f ) );
      // 单调：t=0.25 与 t=0.75 相等且小于峰值。
      Assert.That( Bump( escapePos * 0.25f ), Is.EqualTo( 0.75f ).Within( 1e-5f ) );
      Assert.That( Bump( escapePos * 0.75f ), Is.EqualTo( 0.75f ).Within( 1e-5f ) );
    }

    [Test]
    public void DeadzoneBumpPenaltyUsesConfiguredWeight()
    {
      MachineProfile profile = MachineProfileLoader.LoadFromPath( SharedProfilePath() );
      Type agentType = FindType( "RLExcavator.ExcavatorTrajectoryAgent" );
      MethodInfo penaltyMethod = agentType.GetMethod(
        "DeadzoneBumpPenalty",
        BindingFlags.Static | BindingFlags.Public );
      Assert.That( penaltyMethod, Is.Not.Null, "Trajectory reward must expose its weighted deadzone bump." );

      const float weight = 0.03f;
      const float escapeMargin = 0.1f;
      float boomHalfEscape = profile.GetActuator( "boom" ).CommandDeadzonePositiveNormalized
        * ( 1f + escapeMargin ) * 0.5f;
      float stickHalfEscape = profile.GetActuator( "stick" ).CommandDeadzoneNegativeNormalized
        * ( 1f + escapeMargin ) * 0.5f;

      float penalty = (float)penaltyMethod.Invoke(
        null,
        new object[] {
          new Vector4( boomHalfEscape, -stickHalfEscape, 0f, 0f ),
          profile,
          weight,
          escapeMargin
        } );

      Assert.That( penalty, Is.EqualTo( -2f * weight ).Within( 1e-6f ) );
    }

    [Test]
    public void DeadzoneEffectiveActionZeroesInsideBandAndKeepsOutside()
    {
      MachineProfile profile = MachineProfileLoader.LoadFromPath( SharedProfilePath() );
      Type agentType = FindType( "RLExcavator.ExcavatorTrajectoryAgent" );
      MethodInfo effMethod = agentType.GetMethod(
        "DirectionalDeadzoneEffectiveAction",
        BindingFlags.Static | BindingFlags.Public );
      Assert.That( effMethod, Is.Not.Null, "Trajectory reward must expose the post-deadzone effective action." );

      float boomPos = profile.GetActuator( "boom" ).CommandDeadzonePositiveNormalized;
      float bucketPos = profile.GetActuator( "bucket" ).CommandDeadzonePositiveNormalized;

      // boom 带内 -> 0；stick 恰为 0 -> 0；bucket 越界 -> 保留；swing 满量程 -> 保留。
      Vector4 effective = (Vector4)effMethod.Invoke(
        null,
        new object[] {
          new Vector4( boomPos, 0f, bucketPos + 0.01f, -1f ),
          profile
        } );

      Assert.That( effective.x, Is.Zero );
      Assert.That( effective.y, Is.Zero );
      Assert.That( effective.z, Is.EqualTo( bucketPos + 0.01f ).Within( 1e-6f ) );
      Assert.That( effective.w, Is.EqualTo( -1f ).Within( 1e-6f ) );
    }

    [Test]
    public void DeadzoneEnergyActionUsesRawOrEffectiveActionFromConfiguredMode()
    {
      MachineProfile profile = MachineProfileLoader.LoadFromPath( SharedProfilePath() );
      Type agentType = FindType( "RLExcavator.ExcavatorTrajectoryAgent" );
      MethodInfo energyActionMethod = agentType.GetMethod(
        "DeadzoneEnergyAction",
        BindingFlags.Static | BindingFlags.Public );
      Assert.That( energyActionMethod, Is.Not.Null, "Trajectory reward must expose its energy-action selection." );

      Vector4 rawAction = new Vector4(
        profile.GetActuator( "boom" ).CommandDeadzonePositiveNormalized * 0.5f,
        -profile.GetActuator( "stick" ).CommandDeadzoneNegativeNormalized * 0.5f,
        profile.GetActuator( "bucket" ).CommandDeadzonePositiveNormalized + 0.01f,
        -1f );

      Vector4 rawEnergyAction = (Vector4)energyActionMethod.Invoke(
        null,
        new object[] { rawAction, profile, false } );
      Vector4 effectiveEnergyAction = (Vector4)energyActionMethod.Invoke(
        null,
        new object[] { rawAction, profile, true } );

      Assert.That( rawEnergyAction, Is.EqualTo( rawAction ) );
      Assert.That( effectiveEnergyAction.x, Is.Zero );
      Assert.That( effectiveEnergyAction.y, Is.Zero );
      Assert.That( effectiveEnergyAction.z, Is.EqualTo( rawAction.z ).Within( 1e-6f ) );
      Assert.That( effectiveEnergyAction.w, Is.EqualTo( rawAction.w ).Within( 1e-6f ) );
    }

    [Test]
    public void DeadzoneDomainRandomizationMapsUnitSamplesIntoConfiguredBounds()
    {
      Type agentType = FindType( "RLExcavator.ExcavatorTrajectoryAgent" );
      MethodInfo sampleMethod = agentType.GetMethod(
        "DeadzoneScaleFromUnitSample",
        BindingFlags.Static | BindingFlags.Public );
      Assert.That( sampleMethod, Is.Not.Null, "Training must expose deterministic deadzone scale sampling for verification." );

      float Sample( float min, float max, float unit ) =>
        (float)sampleMethod.Invoke( null, new object[] { min, max, unit } );

      Assert.That( Sample( 0.8f, 1.2f, 0f ), Is.EqualTo( 0.8f ).Within( 1e-6f ) );
      Assert.That( Sample( 0.8f, 1.2f, 0.5f ), Is.EqualTo( 1f ).Within( 1e-6f ) );
      Assert.That( Sample( 0.8f, 1.2f, 1f ), Is.EqualTo( 1.2f ).Within( 1e-6f ) );
      Assert.That( Sample( 1.2f, 0.8f, 0.25f ), Is.EqualTo( 0.9f ).Within( 1e-6f ),
        "Reversed inspector bounds must be normalized rather than producing invalid thresholds." );
      Assert.That( Sample( -1f, float.PositiveInfinity, float.NaN ), Is.EqualTo( 1f ).Within( 1e-6f ),
        "Invalid inputs must fall back to the nominal machine-profile deadzone." );
    }

    [Test]
    public void ControllerAppliesPerAxisDeadzoneScalesBeforeDrivingPhysics()
    {
      MachineProfile profile = MachineProfileLoader.LoadFromPath( SharedProfilePath() );
      Type controllerType = FindType( "RLExcavator.ExcavatorController" );
      var gameObject = new GameObject( "deadzone-domain-randomization-controller-test" );

      try {
        Component controller = gameObject.AddComponent( controllerType );
        Invoke(
          controllerType,
          controller,
          "ApplyMachineProfileCommandDeadzoneScales",
          profile,
          new Vector4( 0.8f, 1.2f, 1f, 1f ) );

        MachineActuatorProfile boom = profile.GetActuator( "boom" );
        MachineActuatorProfile stick = profile.GetActuator( "stick" );
        Invoke(
          controllerType,
          controller,
          "SetNormalizedSpeeds",
          boom.CommandDeadzonePositiveNormalized * 0.9f,
          -stick.CommandDeadzoneNegativeNormalized * 1.1f,
          0f,
          0f );

        Assert.That(
          (float)controllerType.GetProperty( "LastBoomCommand" ).GetValue( controller ),
          Is.GreaterThan( 0f ),
          "Boom command must survive the randomized 0.8x deadzone." );
        Assert.That(
          (float)controllerType.GetProperty( "LastStickCommand" ).GetValue( controller ),
          Is.Zero,
          "Stick command must be removed by the randomized 1.2x deadzone." );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( gameObject );
      }
    }

    [Test]
    public void RewardAndEnergyUseTheSameRandomizedDeadzoneAsController()
    {
      MachineProfile profile = MachineProfileLoader.LoadFromPath( SharedProfilePath() );
      Type agentType = FindType( "RLExcavator.ExcavatorTrajectoryAgent" );
      MethodInfo maskMethod = agentType.GetMethod(
        "DirectionalDeadzoneNonzeroMaskWithScales",
        BindingFlags.Static | BindingFlags.Public );
      MethodInfo effectiveMethod = agentType.GetMethod(
        "DirectionalDeadzoneEffectiveActionWithScales",
        BindingFlags.Static | BindingFlags.Public );
      Assert.That( maskMethod, Is.Not.Null );
      Assert.That( effectiveMethod, Is.Not.Null );

      MachineActuatorProfile boom = profile.GetActuator( "boom" );
      MachineActuatorProfile stick = profile.GetActuator( "stick" );
      Vector4 action = new Vector4(
        boom.CommandDeadzonePositiveNormalized * 0.9f,
        -stick.CommandDeadzoneNegativeNormalized * 1.1f,
        0f,
        0f );
      Vector4 scales = new Vector4( 0.8f, 1.2f, 1f, 1f );

      Vector4 mask = (Vector4)maskMethod.Invoke( null, new object[] { action, profile, scales } );
      Vector4 effective = (Vector4)effectiveMethod.Invoke( null, new object[] { action, profile, scales } );

      Assert.That( mask.x, Is.Zero );
      Assert.That( effective.x, Is.EqualTo( action.x ).Within( 1e-6f ) );
      Assert.That( mask.y, Is.EqualTo( 1f ) );
      Assert.That( effective.y, Is.Zero );
    }

    [Test]
    public void ScaleEnvironmentPrefabKeepsInferenceSafeDeadzoneDefaults()
    {
      GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
        "Assets/Prefabs/ScaleTrajectoryEnvironment.prefab" );
      Assert.That( prefab, Is.Not.Null, "The scale trajectory training prefab must exist." );

      Type agentType = FindType( "RLExcavator.ExcavatorTrajectoryAgent" );
      Component agent = prefab.GetComponentInChildren( agentType, true );
      Assert.That( agent, Is.Not.Null, "The scale trajectory training prefab must contain the trajectory agent." );

      var serializedAgent = new SerializedObject( agent );
      Assert.That(
        serializedAgent.FindProperty( "deadzoneBumpWeight" ).floatValue,
        Is.EqualTo( 0.03f ).Within( 1e-6f ) );
      Assert.That(
        serializedAgent.FindProperty( "deadzoneEscapeMargin" ).floatValue,
        Is.EqualTo( 0.1f ).Within( 1e-6f ) );
      Assert.That(
        serializedAgent.FindProperty( "deadzoneEnergyUseEffective" ).boolValue,
        Is.False );
      Assert.That(
        serializedAgent.FindProperty( "randomizeCommandDeadzonesOnEpisodeBegin" ).boolValue,
        Is.False,
        "Shared environment prefab must not randomize deadzones during deterministic ONNX inference." );
      Assert.That(
        serializedAgent.FindProperty( "commandDeadzoneScaleMin" ).floatValue,
        Is.EqualTo( 0.8f ).Within( 1e-6f ) );
      Assert.That(
        serializedAgent.FindProperty( "commandDeadzoneScaleMax" ).floatValue,
        Is.EqualTo( 1.2f ).Within( 1e-6f ) );
    }

    [Test]
    public void TrainingSceneEnablesA2AndDeadzoneRandomizationForAllAgents()
    {
      var scene = EditorSceneManager.OpenScene(
        "Assets/Scenes/Training/ExcavatorRLTrain_Scale.unity",
        OpenSceneMode.Additive );

      try {
        Type agentType = FindType( "RLExcavator.ExcavatorTrajectoryAgent" );
        Type behaviorType = FindType( "Unity.MLAgents.Policies.BehaviorParameters" );
        int agentCount = 0;
        int trainingAgentCount = 0;

        foreach ( GameObject root in scene.GetRootGameObjects() ) {
          Component[] agents = root.GetComponentsInChildren( agentType, true );
          foreach ( Component agent in agents ) {
            agentCount++;
            var serializedAgent = new SerializedObject( agent );

            Component behavior = agent.GetComponent( behaviorType );
            Assert.That( behavior, Is.Not.Null, "Each trajectory agent must carry ML-Agents BehaviorParameters." );

            var serializedBehavior = new SerializedObject( behavior );
            int behaviorMode = serializedBehavior.FindProperty( "m_BehaviorType" ).intValue;
            Assert.That( behaviorMode, Is.EqualTo( 0 ), "Every trajectory agent in this scene must train with PPO." );
            trainingAgentCount++;
            Assert.That(
              serializedAgent.FindProperty( "deadzoneEnergyUseEffective" ).boolValue,
              Is.True,
              "PPO training agents must enable A2 effective-action energy." );
            Assert.That(
              serializedAgent.FindProperty( "randomizeCommandDeadzonesOnEpisodeBegin" ).boolValue,
              Is.True,
              "PPO training agents must enable deadzone domain randomization." );
            Assert.That(
              serializedBehavior.FindProperty( "m_Model" ).objectReferenceValue,
              Is.Null,
              "PPO training agents must not pin an inference ONNX model." );
          }
        }

        Assert.That( agentCount, Is.GreaterThan( 0 ), "Training scene must contain at least one trajectory agent." );
        Assert.That( trainingAgentCount, Is.GreaterThan( 0 ), "Training scene must contain PPO training agents." );
        Assert.That( trainingAgentCount, Is.EqualTo( agentCount ), "All trajectory agents must use the new training setup." );
      }
      finally {
        EditorSceneManager.CloseScene( scene, true );
      }
    }

    private static void AssertDeadzone(
      MachineActuatorProfile actuator,
      float expectedPositive,
      float expectedNegative )
    {
      Assert.That( actuator.CommandDeadzonePositiveNormalized, Is.EqualTo( expectedPositive ).Within( 1e-6f ) );
      Assert.That( actuator.CommandDeadzoneNegativeNormalized, Is.EqualTo( expectedNegative ).Within( 1e-6f ) );
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

    private static void Invoke( Type type, object target, string methodName, params object[] arguments )
    {
      MethodInfo method = type.GetMethod( methodName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( method, Is.Not.Null, "Missing public method: " + methodName );
      method.Invoke( target, arguments );
    }

    private static float GetFloatProperty( Type type, object target, string propertyName )
    {
      PropertyInfo property = type.GetProperty( propertyName, BindingFlags.Instance | BindingFlags.Public );
      Assert.That( property, Is.Not.Null, "Missing public property: " + propertyName );
      return (float)property.GetValue( target );
    }

    private static string SharedProfilePath()
    {
      return Path.GetFullPath( Path.Combine( Application.dataPath, "../../shared/machine_profile.jsonc" ) );
    }
  }
}
