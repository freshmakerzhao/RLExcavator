using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class TrajectoryRewardAblationTests
  {
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private Dictionary<string, Func<float>> Parameters()
    {
      Type academy = FindType( "Unity.MLAgents.Academy" );
      object instance = academy.GetProperty( "Instance" ).GetValue( null );
      object parameters = academy.GetProperty( "EnvironmentParameters" ).GetValue( instance );
      object channel = parameters.GetType().GetField( "m_Channel", Fields ).GetValue( parameters );
      return (Dictionary<string, Func<float>>)channel.GetType().GetField( "m_Parameters", Fields ).GetValue( channel );
    }

    [TearDown]
    public void ClearParameters()
    {
      Parameters().Remove( "reward_pitch_scale" );
      Parameters().Remove( "reward_deadzone_scale" );
    }

    [Test]
    public void OmittedScalesPreserveFullReward()
    {
      float baseline = StepReward( null, null );
      Assert.That( StepReward( 1f, 1f ), Is.EqualTo( baseline ).Within( 1e-6f ) );
    }

    [Test]
    public void PitchAblationChangesOnlyStepPitchPenalty()
    {
      float full = StepReward( 1f, 1f );
      float ablated = StepReward( 0f, 1f );
      Assert.That( ablated - full, Is.EqualTo( 0.2f ).Within( 1e-5f ) );
      Assert.That( StepReward( 0.5f, 1f ), Is.EqualTo( (full + ablated) / 2f ).Within( 1e-5f ) );
    }

    [Test]
    public void DeadzoneAblationLeavesEnergyAndPitchRewardsIntact()
    {
      MachineProfile profile = Profile();
      Type agent = FindType( "RLExcavator.ExcavatorTrajectoryAgent" );
      float bump = (float)agent.GetMethod( "DeadzoneBumpPenaltyWithScales" ).Invoke(
        null, new object[] { new Vector4( 0.03f, 0f, 0f, 0f ), profile, 0.03f, 0.1f, Vector4.one } );
      Assert.That( bump, Is.LessThan( 0f ) );
      Assert.That( StepReward( 1f, 0f ) - StepReward( 1f, 1f ), Is.EqualTo( -bump ).Within( 1e-5f ) );
    }

    [TestCase( float.NaN )]
    [TestCase( float.PositiveInfinity )]
    [TestCase( -0.01f )]
    [TestCase( 1.01f )]
    public void InvalidScaleRejectsEpisode( float scale )
    {
      var error = Assert.Throws<TargetInvocationException>( () => StepReward( scale, 1f ) );
      Assert.That( error.InnerException, Is.TypeOf<ArgumentOutOfRangeException>() );
      error = Assert.Throws<TargetInvocationException>( () => StepReward( 1f, scale ) );
      Assert.That( error.InnerException, Is.TypeOf<ArgumentOutOfRangeException>() );
    }

    [Test]
    public void ParametersAreFrozenUntilTheNextEpisode()
    {
      float full = StepReward( 1f, 1f );
      float frozen = StepReward( 1f, 1f, agent => {
        Parameters()[ "reward_pitch_scale" ] = () => 0f;
        Assert.That( agent.GetType().GetProperty( "EpisodeRewardPitchScale" ).GetValue( agent ), Is.EqualTo( 1f ) );
      } );
      Assert.That( frozen, Is.EqualTo( full ).Within( 1e-6f ) );
      StepReward( 1f, 1f, agent => {
        Parameters()[ "reward_pitch_scale" ] = () => 0f;
        Parameters()[ "reward_deadzone_scale" ] = () => 0f;
        agent.GetType().GetMethod( "OnEpisodeBegin" ).Invoke( agent, null );
        Assert.That( agent.GetType().GetProperty( "EpisodeRewardPitchScale" ).GetValue( agent ), Is.EqualTo( 0f ) );
        Assert.That( agent.GetType().GetProperty( "EpisodeRewardDeadzoneScale" ).GetValue( agent ), Is.EqualTo( 0f ) );
      } );
    }

    private float StepReward( float? pitch, float? deadzone, Action<Component> afterBegin = null )
    {
      var parameters = Parameters();
      parameters.Remove( "reward_pitch_scale" );
      parameters.Remove( "reward_deadzone_scale" );
      if ( pitch.HasValue ) parameters[ "reward_pitch_scale" ] = () => pitch.Value;
      if ( deadzone.HasValue ) parameters[ "reward_deadzone_scale" ] = () => deadzone.Value;
      var root = new GameObject( "reward-test" );
      root.SetActive( false );
      try {
        Component agent = root.AddComponent( FindType( "RLExcavator.ExcavatorTrajectoryAgent" ) );
        var tip = new GameObject( "tip" );
        tip.transform.SetParent( root.transform );
        var target = new GameObject( "target" );
        target.transform.SetParent( root.transform );
        target.transform.position = Vector3.forward;
        Component trajectory = root.AddComponent( FindType( "RLExcavator.TrajectoryProvider" ) );
        Set( trajectory, "waypoints", new[] { target.transform } );
        Set( agent, "trajectory", trajectory );
        Set( agent, "bucketTip", tip.transform );
        Set( agent, "randomizeTaskModeOnEpisodeBegin", false );
        Set( agent, "resetPoseOnEpisodeBegin", false );
        Set( agent, "debugEpisodeEvents", false );
        Set( agent, "moveToDigTargetBucketPitchDeg", 90f );
        Set( agent, "bucketPitchNormalizerDeg", 90f );
        Set( agent, "m_machineProfile", Profile() );
        agent.GetType().GetMethod( "OnEpisodeBegin" ).Invoke( agent, null );
        afterBegin?.Invoke( agent );
        Type buffers = FindType( "Unity.MLAgents.Actuators.ActionBuffers" );
        object actions = Activator.CreateInstance( buffers, new object[] { new[] { 0.03f, 0f, 0f, 0f }, new int[ 0 ] } );
        agent.GetType().GetMethod( "OnActionReceived" ).Invoke( agent, new[] { actions } );
        return (float)agent.GetType().GetMethod( "GetCumulativeReward" ).Invoke( agent, null );
      }
      finally { UnityEngine.Object.DestroyImmediate( root ); }
    }

    private static MachineProfile Profile() => MachineProfileLoader.LoadFromPath(
      Path.GetFullPath( Path.Combine( Application.dataPath, "../../shared/machine_profile.json" ) ) );

    private static void Set( object target, string name, object value ) =>
      target.GetType().GetField( name, Fields ).SetValue( target, value );

    private static Type FindType( string name )
    {
      foreach ( Assembly assembly in AppDomain.CurrentDomain.GetAssemblies() ) {
        Type type = assembly.GetType( name );
        if ( type != null ) return type;
      }
      throw new InvalidOperationException( name );
    }
  }
}
