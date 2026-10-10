using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class ExcavatorMaterialTelemetryTests
  {
    [Test]
    public void MissingConfigurationIsUnavailableRatherThanValidZeroMaterial()
    {
      var host = new GameObject( "material-telemetry-test" );
      host.SetActive( false );
      try {
        Type readerType = Type.GetType( "RLExcavator.ExcavatorMaterialTelemetry, Assembly-CSharp" );
        Assert.That( readerType, Is.Not.Null );
        Component reader = host.AddComponent( readerType );
        object state = readerType.GetMethod( "ReadState" ).Invoke( reader, null );
        Assert.That( Field<bool>( state, "available" ), Is.False );
        Assert.That( Field<string>( state, "reason" ), Is.Not.Null.And.Not.Empty );
        Assert.That( Field<string>( state, "source" ), Is.EqualTo( "agx_shovel_and_truck_roi_v1" ) );
        Assert.That( readerType.GetField( "truckRegionSize" ).GetValue( reader ), Is.EqualTo( Vector3.zero ) );
      }
      finally { UnityEngine.Object.DestroyImmediate( host ); }
    }

    [TestCase( 0f, 1f, 1f )]
    [TestCase( 1f, -1f, 1f )]
    [TestCase( 1f, 1f, float.NaN )]
    [TestCase( float.PositiveInfinity, 1f, 1f )]
    public void InvalidRegionSizeCannotProduceAnAvailableMeasurement( float x, float y, float z )
    {
      using ( var scene = new MaterialScene() ) {
        scene.Set( "truckRegionSize", new Vector3( x, y, z ) );
        Assert.That( Field<bool>( scene.State, "available" ), Is.False );
        Assert.That( Field<string>( scene.State, "reason" ), Is.EqualTo( "invalid_truck_region" ) );
      }
    }

    [Test]
    public void SoilResetAndUninitializedShovelRemainUnavailableWithoutInitializingNativeObjects()
    {
      using ( var scene = new MaterialScene() ) {
        scene.Soil.GetType().GetProperty( "IsResetting" ).SetValue( scene.Soil, true );
        Assert.That( Field<string>( scene.State, "reason" ), Is.EqualTo( "soil_resetting" ) );
        scene.Soil.GetType().GetProperty( "IsResetting" ).SetValue( scene.Soil, false );
        Assert.That( Field<bool>( scene.State, "available" ), Is.False );
        Assert.That( Field<string>( scene.State, "reason" ), Is.EqualTo( "shovel_uninitialized" ) );
        Assert.That( scene.Shovel.GetType().GetProperty( "Native" ).GetValue( scene.Shovel ), Is.Null );
      }
    }

    [Test]
    public void TruckRegionUsesItsRotatedAxesAndWorldMeterDimensionsRegardlessOfTransformScale()
    {
      using ( var scene = new MaterialScene() ) {
        scene.Region.position = new Vector3( 10, 20, 30 );
        scene.Region.rotation = Quaternion.Euler( 0, 90, 0 );
        scene.Region.localScale = new Vector3( 10, 0.5f, 3 );
        scene.Set( "truckRegionSize", new Vector3( 2, 4, 6 ) );

        Assert.That( scene.Contains( scene.Region.position ), Is.True );
        Assert.That( scene.Contains( scene.Region.position + Vector3.right * 2.5f ), Is.True );
        Assert.That( scene.Contains( scene.Region.position + Vector3.forward * 1.1f ), Is.False );
        Assert.That( scene.Contains( scene.Region.position + Vector3.up * 2f ), Is.True );
        Assert.That( scene.Contains( scene.Region.position + Vector3.up * 2.1f ), Is.False );
      }
    }

    private sealed class MaterialScene : IDisposable
    {
      private readonly GameObject m_host = new GameObject( "material-telemetry-test" );
      private readonly Component m_reader;
      private readonly Type m_readerType;
      public readonly Component Shovel;
      public readonly Component Soil;
      public readonly Transform Region;

      public MaterialScene()
      {
        m_host.SetActive( false );
        m_readerType = Type.GetType( "RLExcavator.ExcavatorMaterialTelemetry, Assembly-CSharp" );
        Assert.That( m_readerType, Is.Not.Null );
        m_reader = m_host.AddComponent( m_readerType );
        GameObject shovelHost = Child( "Shovel" ).gameObject;
        shovelHost.AddComponent( Type.GetType( "AGXUnity.RigidBody, AGXUnity" ) );
        Shovel = shovelHost.AddComponent(
          Type.GetType( "AGXUnity.Model.DeformableTerrainShovel, AGXUnity" ) );
        Soil = Child( "Soil" ).gameObject.AddComponent(
          Type.GetType( "RLExcavator.ExcavatorRuntimeSoil, Assembly-CSharp" ) );
        Region = Child( "TruckRegion" );
        Set( "shovel", Shovel );
        Set( "soil", Soil );
        Set( "truckRegion", Region );
        Set( "truckRegionSize", Vector3.one );
      }

      public object State => m_readerType.GetMethod( "ReadState" ).Invoke( m_reader, null );
      public void Set( string field, object value ) => m_readerType.GetField( field ).SetValue( m_reader, value );
      public bool Contains( Vector3 point )
      {
        // 和运行时调用同一个区域判定，覆盖旋转/缩放造成的常见统计边界错误。
        return (bool)m_readerType.GetMethod( "ContainsTruckPoint", BindingFlags.Instance | BindingFlags.NonPublic )
          .Invoke( m_reader, new object[] { point } );
      }

      private Transform Child( string name )
      {
        var child = new GameObject( name );
        child.SetActive( false );
        child.transform.SetParent( m_host.transform, false );
        return child.transform;
      }

      public void Dispose() { UnityEngine.Object.DestroyImmediate( m_host ); }
    }

    private static T Field<T>( object value, string name )
    {
      return (T)value.GetType().GetField( name ).GetValue( value );
    }
  }
}
