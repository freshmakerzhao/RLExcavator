using System;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class ExcavatorTerrainTelemetryTests
  {
    [Test]
    public void RotatedGridKeepsRowMajorOrderAndConvertsNativeHeightToAbsoluteRosMeters()
    {
      // 高度必须同时应用局部尺度和原点 Z；行列方向不能被强行改成 ROS 的 X/Y 轴。
      object state = Sample( 2, 3, new Vector3( 0.6f, -0.2f, -0.9f ),
        new Vector3( 0, 0.04f, 0 ), new Vector3( -0.05f, 0, 0 ),
        new Vector3( 0, 0, 0.5f ), (row, col) => 0.8 + 0.02 * (row * 3 + col),
        (row, col) => 0.2 );

      Assert.That( Field<bool>( state, "available" ), Is.True );
      Assert.That( Field<string>( state, "reason" ), Is.EqualTo( "ok" ) );
      Assert.That( Field<string>( state, "schema" ), Is.EqualTo( "terrain_grid_v1" ) );
      Assert.That( Field<string>( state, "source" ), Is.EqualTo( "agx_terrain_heightfield_v1" ) );
      Assert.That( Field<string>( state, "source_kind" ), Is.EqualTo( "simulation_ground_truth" ) );
      Assert.That( Field<string>( state, "frame" ), Is.EqualTo( "machine_root_ros" ) );
      Assert.That( Field<bool>( state, "dynamic_particles_included" ), Is.False );
      Assert.That( Field<int>( state, "rows" ), Is.EqualTo( 2 ) );
      Assert.That( Field<int>( state, "cols" ), Is.EqualTo( 3 ) );
      AssertNumbers( state, "origin_xy_m", 0.6, -0.2 );
      AssertNumbers( state, "column_step_xy_m", 0, 0.04 );
      AssertNumbers( state, "row_step_xy_m", -0.05, 0 );
      AssertNumbers( state, "surface_height_m", -0.5, -0.49, -0.48, -0.47, -0.46, -0.45 );
      AssertNumbers( state, "bed_height_m", -0.8, -0.8, -0.8, -0.8, -0.8, -0.8 );
      Assert.That( Field<bool[]>( state, "surface_valid" ), Is.All.True );
      Assert.That( Field<bool[]>( state, "bed_valid" ), Is.All.True );
    }

    [Test]
    public void MissingSamplesHaveIndependentMasksAndFiniteZeroPlaceholders()
    {
      double[] surface = { double.NaN, 0.8, double.PositiveInfinity, 0.9 };
      double[] bed = { 0.2, double.NegativeInfinity, double.NaN, 0.1 };
      object state = Sample( 2, 2, Vector3.zero, Vector3.right, Vector3.up, Vector3.forward,
        (row, col) => surface[row * 2 + col], (row, col) => bed[row * 2 + col] );

      // 表面缺测不得抹掉可用的底层，底层缺测也不得伪造一层零高度地面。
      Assert.That( Field<bool>( state, "available" ), Is.True );
      Assert.That( Field<bool[]>( state, "surface_valid" ), Is.EqualTo( new[] { false, true, false, true } ) );
      Assert.That( Field<bool[]>( state, "bed_valid" ), Is.EqualTo( new[] { true, false, false, true } ) );
      AssertNumbers( state, "surface_height_m", 0, 0.8, 0, 0.9 );
      AssertNumbers( state, "bed_height_m", 0.2, 0, 0, 0.1 );
    }

    [Test]
    public void AllMissingSurfaceIsUnavailableEvenWhenBedIsReadable()
    {
      object state = SampleDefault( (row, col) => double.NaN, (row, col) => -0.8 );
      AssertUnavailable( state, "no_valid_surface" );
      Assert.That( Field<bool[]>( state, "surface_valid" ), Is.EqualTo( new[] { false } ) );
      AssertNumbers( state, "surface_height_m", 0 );
    }

    [TestCase( 0f, 0f, -1f )]
    [TestCase( 0.001f, 0f, 1f )]
    [TestCase( 0f, 0f, 0f )]
    [TestCase( float.NaN, 0f, 1f )]
    public void NonUpwardOrTiltedHeightAxisCannotDescribeAPlanarRosGrid( float x, float y, float z )
    {
      object state = Sample( 1, 1, Vector3.zero, Vector3.right, Vector3.up,
        new Vector3( x, y, z ), (row, col) => 1, (row, col) => 0 );
      AssertUnavailable( state, "unsupported_grid_orientation" );
    }

    [TestCase( 0f, 0f )]
    [TestCase( 1f, 0f )]
    [TestCase( 1f, 1f )]
    public void ZeroOrNonOrthogonalGridStepsAreRejected( float rowX, float rowY )
    {
      object state = Sample( 1, 1, Vector3.zero, Vector3.right,
        new Vector3( rowX, rowY, 0 ), Vector3.forward,
        (row, col) => 1, (row, col) => 0 );
      AssertUnavailable( state, "unsupported_grid_orientation" );
    }

    [Test]
    public void GridStepWithVerticalOffsetCannotBeEncodedAsAPlanarGrid()
    {
      object state = Sample( 1, 1, Vector3.zero, new Vector3( 1, 0, 0.1f ),
        Vector3.up, Vector3.forward, (row, col) => 1, (row, col) => 0 );
      AssertUnavailable( state, "unsupported_grid_orientation" );
    }

    [Test]
    public void SurfaceBelowBedBeyondToleranceIsRejectedRatherThanClamped()
    {
      AssertUnavailable( SampleDefault( (row, col) => -0.00002, (row, col) => 0 ),
        "invalid_height_layers" );
    }

    [Test]
    public void SurfaceBelowBedWithinToleranceKeepsOriginalMeasurement()
    {
      object state = SampleDefault( (row, col) => -0.000005, (row, col) => 0 );
      Assert.That( Field<bool>( state, "available" ), Is.True );
      AssertNumbers( state, "surface_height_m", -0.000005 );
      AssertNumbers( state, "bed_height_m", 0 );
    }

    [TestCase( 0, 2 )]
    [TestCase( 2, 0 )]
    [TestCase( -1, 2 )]
    public void InvalidDimensionsAreRejectedBeforeReadingSamples( int rows, int cols )
    {
      int reads = 0;
      Func<int, int, double> reader = (row, col) => { ++reads; return 0; };
      object state = Sample( rows, cols, Vector3.zero, Vector3.right, Vector3.up,
        Vector3.forward, reader, reader );
      AssertUnavailable( state, "invalid_grid_shape" );
      Assert.That( reads, Is.Zero );
    }

    [Test]
    public void MissingConfigurationReturnsUnavailable()
    {
      var host = new GameObject( "terrain-telemetry-test" );
      host.SetActive( false );
      try {
        Type readerType = ReaderType();
        Component reader = host.AddComponent( readerType );
        object state = readerType.GetMethod( "ReadState" ).Invoke( reader, null );
        Assert.That( Field<bool>( state, "available" ), Is.False );
        Assert.That( Field<string>( state, "reason" ), Is.Not.Null.And.Not.Empty );
        Assert.That( Field<string>( state, "source" ), Is.EqualTo( "agx_terrain_heightfield_v1" ) );
      }
      finally { UnityEngine.Object.DestroyImmediate( host ); }
    }

    [Test]
    public void ReadingUninitializedTerrainDoesNotInitializeNativeObjects()
    {
      var host = new GameObject( "uninitialized-terrain-telemetry-test" );
      host.SetActive( false );
      Component soil = null;
      try {
        Type readerType = ReaderType();
        Component reader = host.AddComponent( readerType );
        soil = host.AddComponent( Type.GetType( "RLExcavator.ExcavatorRuntimeSoil, Assembly-CSharp" ) );
        Component terrain = host.AddComponent( Type.GetType( "AGXUnity.Model.MovableTerrain, AGXUnity" ) );
        soil.GetType().GetProperty( "ActiveTerrain" ).SetValue( soil, terrain );
        readerType.GetField( "soil" ).SetValue( reader, soil );
        readerType.GetField( "machineRoot" ).SetValue( reader, host.transform );
        PropertyInfo native = terrain.GetType().GetProperty( "Native" );
        Assert.That( native.GetValue( terrain ), Is.Null );

        object state = readerType.GetMethod( "ReadState" ).Invoke( reader, null );
        Assert.That( Field<bool>( state, "available" ), Is.False );
        Assert.That( Field<string>( state, "reason" ), Is.Not.Null.And.Not.Empty );
        // 只读观测不能用 GetInitialized 补建 AGX 原生对象。
        Assert.That( native.GetValue( terrain ), Is.Null );
      }
      finally {
        if ( soil != null ) soil.GetType().GetProperty( "ActiveTerrain" ).SetValue( soil, null );
        UnityEngine.Object.DestroyImmediate( host );
      }
    }

    [Test]
    public void OrdinaryBridgeRepliesDoNotSerializeAnEmptyTerrainPayload()
    {
      // 同一个实际发送出口覆盖 reset、普通状态和 RGB 回复，避免默认空地图污染协议。
      foreach ( string kind in new[] { "reset_result", "state", "observation" } ) {
        object reply = NewBridgeReply( "Reply", kind );
        string json = CompleteBridgeReply( reply );
        Assert.That( json, Does.Not.Contain( "\"terrain_state\"" ), kind );
        object restored = JsonUtility.FromJson( json, reply.GetType() );
        Assert.That( Field<string>( restored, "kind" ), Is.EqualTo( kind ) );
        Assert.That( Field<int>( restored, "epoch" ), Is.EqualTo( 7 ) );
      }
    }

    [Test]
    public void TerrainBridgeReplyPreservesStateEnvelopeBodyAndBooleanMasks()
    {
      object reply = NewBridgeReply( "TerrainReply", "terrain" );
      object terrain = Sample( 1, 2, Vector3.zero, Vector3.right, Vector3.up, Vector3.forward,
        (row, col) => col == 0 ? 0.2 : double.NaN,
        (row, col) => col == 0 ? double.NaN : -0.1 );
      reply.GetType().GetField( "terrain_state" ).SetValue( reply, terrain );
      object actuators = NewBridgeValue( "ActuatorState" );
      object boom = NewBridgeValue( "AxisState" );
      boom.GetType().GetField( "position_m" ).SetValue( boom, 0.023f );
      actuators.GetType().GetField( "boom" ).SetValue( actuators, boom );
      reply.GetType().GetField( "actuator_state" ).SetValue( reply, actuators );
      object pose = NewBridgeValue( "BucketTipPose" );
      pose.GetType().GetField( "position_ros_m" ).SetValue( pose, new Vector3( 0.6f, 0.1f, -0.7f ) );
      reply.GetType().GetField( "bucket_tip_pose" ).SetValue( reply, pose );

      string json = CompleteBridgeReply( reply );
      object restored = JsonUtility.FromJson( json, reply.GetType() );
      Assert.That( Field<string>( restored, "kind" ), Is.EqualTo( "terrain" ) );
      Assert.That( Field<bool>( restored, "accepted" ), Is.True );
      Assert.That( Field<bool>( restored, "state_valid" ), Is.True );
      Assert.That( Field<int>( restored, "epoch" ), Is.EqualTo( 7 ) );
      Assert.That( Field<int>( restored, "state_sequence" ), Is.EqualTo( 31 ) );
      Assert.That( Field<double>( restored, "sim_time_s" ), Is.EqualTo( 0.62 ).Within( 1e-10 ) );
      object restoredBoom = Field<object>( Field<object>( restored, "actuator_state" ), "boom" );
      Assert.That( Field<float>( restoredBoom, "position_m" ), Is.EqualTo( 0.023f ) );
      Assert.That( Field<Vector3>( Field<object>( restored, "bucket_tip_pose" ), "position_ros_m" ),
                   Is.EqualTo( new Vector3( 0.6f, 0.1f, -0.7f ) ) );
      object restoredTerrain = Field<object>( restored, "terrain_state" );
      Assert.That( Field<bool>( restoredTerrain, "available" ), Is.True );
      Assert.That( Field<string>( restoredTerrain, "schema" ), Is.EqualTo( "terrain_grid_v1" ) );
      Assert.That( Field<bool[]>( restoredTerrain, "surface_valid" ), Is.EqualTo( new[] { true, false } ) );
      Assert.That( Field<bool[]>( restoredTerrain, "bed_valid" ), Is.EqualTo( new[] { false, true } ) );
      // 还要核对线上的 JSON token，不能仅依赖反序列化器把数字宽松地转换为 bool。
      Assert.That( json, Does.Match( "\"surface_valid\"\\s*:\\s*\\[\\s*true\\s*,\\s*false\\s*\\]" ) );
      Assert.That( json, Does.Match( "\"bed_valid\"\\s*:\\s*\\[\\s*false\\s*,\\s*true\\s*\\]" ) );
    }

    private static object NewBridgeReply( string typeName, string kind )
    {
      object reply = NewBridgeValue( typeName );
      Type type = reply.GetType();
      type.GetField( "kind" ).SetValue( reply, kind );
      type.GetField( "epoch" ).SetValue( reply, 7 );
      type.GetField( "state_sequence" ).SetValue( reply, 31 );
      type.GetField( "sim_time_s" ).SetValue( reply, 0.62 );
      type.GetField( "accepted" ).SetValue( reply, true );
      type.GetField( "state_valid" ).SetValue( reply, true );
      return reply;
    }

    private static object NewBridgeValue( string name )
    {
      Type type = BridgeType().GetNestedType( name, BindingFlags.NonPublic );
      Assert.That( type, Is.Not.Null, "Missing bridge wire type: " + name );
      return Activator.CreateInstance( type, true );
    }

    private static string CompleteBridgeReply( object reply )
    {
      object pending = NewBridgeValue( "Pending" );
      var completion = new TaskCompletionSource<string>();
      pending.GetType().GetField( "reply" ).SetValue( pending, completion );
      MethodInfo complete = BridgeType().GetMethod( "Complete", BindingFlags.NonPublic | BindingFlags.Static );
      Assert.That( complete, Is.Not.Null );
      complete.Invoke( null, new[] { pending, reply } );
      Assert.That( completion.Task.IsCompleted, Is.True );
      return completion.Task.GetAwaiter().GetResult();
    }

    private static Type BridgeType()
    {
      Type type = Type.GetType( "RLExcavator.ExcavatorSimBridge, Assembly-CSharp" );
      Assert.That( type, Is.Not.Null );
      return type;
    }

    private static object SampleDefault( Func<int, int, double> surface, Func<int, int, double> bed )
    {
      return Sample( 1, 1, Vector3.zero, Vector3.right, Vector3.up, Vector3.forward, surface, bed );
    }

    private static object Sample( int rows, int cols, Vector3 origin, Vector3 columnStep,
                                  Vector3 rowStep, Vector3 heightAxis,
                                  Func<int, int, double> surface, Func<int, int, double> bed )
    {
      MethodInfo method = ReaderType().GetMethod( "SampleGrid", BindingFlags.Public | BindingFlags.Static );
      Assert.That( method, Is.Not.Null );
      return method.Invoke( null, new object[] { rows, cols, origin, columnStep, rowStep, heightAxis, surface, bed } );
    }

    private static Type ReaderType()
    {
      Type type = Type.GetType( "RLExcavator.ExcavatorTerrainTelemetry, Assembly-CSharp" );
      Assert.That( type, Is.Not.Null, "D1 地形采样器尚未实现。" );
      return type;
    }

    private static void AssertUnavailable( object state, string reason )
    {
      Assert.That( Field<bool>( state, "available" ), Is.False );
      Assert.That( Field<string>( state, "reason" ), Is.EqualTo( reason ) );
    }

    private static void AssertNumbers( object state, string name, params double[] expected )
    {
      Array actual = Field<Array>( state, name );
      Assert.That( actual.Length, Is.EqualTo( expected.Length ), name );
      for ( int i = 0; i < expected.Length; ++i )
        Assert.That( Convert.ToDouble( actual.GetValue( i ) ), Is.EqualTo( expected[i] ).Within( 1e-7 ), name + "[" + i + "]" );
    }

    private static T Field<T>( object value, string name )
    {
      return (T)value.GetType().GetField( name ).GetValue( value );
    }
  }
}
