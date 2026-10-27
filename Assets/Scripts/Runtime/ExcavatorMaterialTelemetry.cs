using System;
using AGXUnity.Model;
using UnityEngine;

namespace RLExcavator
{
  /// <summary>仅在阶段边界显式读取 AGX 铲斗与车厢区域内的物料，不推进或修改物理状态。</summary>
  public class ExcavatorMaterialTelemetry : MonoBehaviour
  {
    public DeformableTerrainShovel shovel;
    public ExcavatorRuntimeSoil soil;
    public Transform truckRegion;
    [Tooltip( "沿统计区域局部轴的世界米尺寸，不乘 Transform 缩放；零尺寸表示尚未配置。" )]
    public Vector3 truckRegionSize = Vector3.zero;
    [Tooltip( "可选第二接料区域；仅用于已知双卸料目标的同帧物料测量。" )]
    public Transform secondaryTruckRegion;
    public Vector3 secondaryTruckRegionSize = Vector3.zero;

    [Serializable]
    public sealed class ReceivingRegionState
    {
      public string id;
      public double mass_kg;
      public double particle_volume_m3;
      public int particle_count;
      public Vector3 center_unity_m;
      public Vector3 size_m;
      public Quaternion rotation_unity;
    }

    [Serializable]
    public sealed class State
    {
      public bool available;
      public string reason;
      public string source = "agx_shovel_and_truck_roi_v1";
      public double bucket_mass_kg;
      public double bucket_particle_volume_m3;
      public double bucket_bulk_volume_m3;
      // AGX 计算 dead-load 所用的斗内形状容积，并非厂家额定斗容。
      public double bucket_capacity_m3;
      public double bucket_fill_fraction_agx;
      public double truck_mass_kg;
      public double truck_particle_volume_m3;
      public int truck_particle_count;
      public Vector3 truck_roi_center_unity_m;
      public Vector3 truck_roi_size_m;
      public Quaternion truck_roi_rotation_unity;
      // 同一次原生粒子遍历；truck_*继续仅指原车厢A，不能因切换有效目标重定位它。
      public ReceivingRegionState[] receiving_regions = Array.Empty<ReceivingRegionState>();
    }

    public State ReadState()
    {
      try {
        string unavailable = UnavailableReason();
        if ( unavailable != null ) return new State { reason = unavailable };

        var nativeShovel = shovel.Native;
        var regions = ReadReceivingParticles();
        var truck = regions[0];
        var state = new State {
          available = true,
          reason = string.Empty,
          bucket_mass_kg = nativeShovel.getInnerSoilMass(),
          bucket_particle_volume_m3 = nativeShovel.getInnerParticleVolume(),
          bucket_bulk_volume_m3 = nativeShovel.getInnerSoilBulkVolume(),
          bucket_capacity_m3 = nativeShovel.getInnerVolume(),
          bucket_fill_fraction_agx = nativeShovel.getDeadLoadFraction(),
          truck_mass_kg = truck.mass_kg,
          truck_particle_volume_m3 = truck.particle_volume_m3,
          truck_particle_count = truck.particle_count,
          truck_roi_center_unity_m = truckRegion.position,
          truck_roi_size_m = truckRegionSize,
          truck_roi_rotation_unity = truckRegion.rotation,
          receiving_regions = regions
        };
        if ( !ValidMeasurements( state ) ) return new State { reason = "invalid_material_read" };
        return state;
      }
      catch ( Exception error ) {
        // 失败时丢弃部分结果，调用方必须把 available=false 当作缺测。
        return new State { reason = "material_read_failed: " + error.Message };
      }
    }

    private string UnavailableReason()
    {
      if ( shovel == null ) return "missing_shovel";
      if ( soil == null ) return "missing_soil";
      if ( truckRegion == null ) return "missing_truck_region";
      if ( !ValidRegion() ) return "invalid_truck_region";
      if ( secondaryTruckRegion != null && !ValidRegion( secondaryTruckRegion, secondaryTruckRegionSize ) )
        return "invalid_secondary_truck_region";
      if ( soil.IsResetting ) return "soil_resetting";
      if ( !string.IsNullOrEmpty( soil.ResetError ) ) return "soil_reset_failed";
      // 不调用 GetInitialized：观测不能顺带创建原生对象或改变物理状态。
      if ( shovel.Native == null ) return "shovel_uninitialized";
      if ( soil.ActiveTerrain == null || soil.ActiveTerrain.Native == null ) return "terrain_uninitialized";
      return null;
    }

    private ReceivingRegionState[] ReadReceivingParticles()
    {
      var particles = soil.ActiveTerrain.GetParticles();
      if ( particles == null ) throw new InvalidOperationException( "terrain_particles_unavailable" );
      var regions = secondaryTruckRegion == null
        ? new[] { RegionState( "A", truckRegion, truckRegionSize ) }
        : new[] { RegionState( "A", truckRegion, truckRegionSize ),
                  RegionState( "B", secondaryTruckRegion, secondaryTruckRegionSize ) };
      for ( uint index = 0; index < particles.size(); ++index ) {
        var particle = particles.at( index );
        try {
          if ( particle == null ) throw new InvalidOperationException( "particle_unavailable" );
          var position = particle.getPosition();
          // AGX 与 Unity 的手性不同，只翻转 x；不使用渲染或上采样粒子。
          var point = new Vector3( -(float)position.x, (float)position.y, (float)position.z );
          if ( !IsFinite( point ) ) throw new InvalidOperationException( "invalid_particle_position" );
          bool inPrimary = ContainsTruckPoint( point );
          bool inSecondary = secondaryTruckRegion != null && ContainsSecondaryTruckPoint( point );
          if ( !inPrimary && !inSecondary ) continue;
          double particleMass = particle.getMass();
          double radius = particle.getRadius();
          if ( !NonNegativeFinite( particleMass ) || !NonNegativeFinite( radius ) || radius <= 0 )
            throw new InvalidOperationException( "invalid_particle_mass_or_radius" );
          double volume = 4.0 * Math.PI * radius * radius * radius / 3.0;
          if ( inPrimary ) AddParticle( regions[0], particleMass, volume );
          if ( inSecondary ) AddParticle( regions[1], particleMass, volume );
        }
        finally { particle?.ReturnToPool(); }
      }
      return regions;
    }

    private static ReceivingRegionState RegionState( string id, Transform region, Vector3 size ) =>
      new ReceivingRegionState { id = id, center_unity_m = region.position,
        rotation_unity = region.rotation, size_m = size };

    private static void AddParticle( ReceivingRegionState region, double mass, double volume )
    {
      region.mass_kg += mass;
      region.particle_volume_m3 += volume;
      ++region.particle_count;
    }

    private bool ContainsTruckPoint( Vector3 point ) => ContainsPoint( truckRegion, truckRegionSize, point );

    private bool ContainsSecondaryTruckPoint( Vector3 point ) =>
      ContainsPoint( secondaryTruckRegion, secondaryTruckRegionSize, point );

    private static bool ContainsPoint( Transform region, Vector3 size, Vector3 point )
    {
      // 尺寸已经是世界米，不使用会再次除以 Transform 缩放的 InverseTransformPoint。
      Vector3 local = Quaternion.Inverse( region.rotation ) * ( point - region.position );
      if ( !IsFinite( local ) ) throw new InvalidOperationException( "invalid_particle_region_position" );
      Vector3 half = size * 0.5f;
      return Mathf.Abs( local.x ) <= half.x && Mathf.Abs( local.y ) <= half.y &&
        Mathf.Abs( local.z ) <= half.z;
    }

    private bool ValidRegion() => ValidRegion( truckRegion, truckRegionSize );

    private static bool ValidRegion( Transform region, Vector3 size )
    {
      if ( region == null || !IsFinite( size ) || size.x <= 0 || size.y <= 0 || size.z <= 0 ||
           !IsFinite( region.position ) ) return false;
      Quaternion rotation = region.rotation;
      return float.IsFinite( rotation.x ) && float.IsFinite( rotation.y ) &&
        float.IsFinite( rotation.z ) && float.IsFinite( rotation.w );
    }

    private static bool ValidMeasurements( State state )
    {
      return NonNegativeFinite( state.bucket_mass_kg ) && NonNegativeFinite( state.bucket_particle_volume_m3 ) &&
        NonNegativeFinite( state.bucket_bulk_volume_m3 ) && NonNegativeFinite( state.bucket_capacity_m3 ) &&
        state.bucket_capacity_m3 > 0 && NonNegativeFinite( state.bucket_fill_fraction_agx ) &&
        NonNegativeFinite( state.truck_mass_kg ) && NonNegativeFinite( state.truck_particle_volume_m3 );
    }

    private static bool NonNegativeFinite( double value ) => double.IsFinite( value ) && value >= 0;

    private static bool IsFinite( Vector3 value )
    {
      return float.IsFinite( value.x ) && float.IsFinite( value.y ) && float.IsFinite( value.z );
    }

    private void OnDrawGizmosSelected()
    {
      if ( !ValidRegion() ) return;
      Matrix4x4 previousMatrix = Gizmos.matrix;
      Color previousColor = Gizmos.color;
      try {
        Gizmos.matrix = Matrix4x4.TRS( truckRegion.position, truckRegion.rotation, Vector3.one );
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireCube( Vector3.zero, truckRegionSize );
      }
      finally {
        Gizmos.matrix = previousMatrix;
        Gizmos.color = previousColor;
      }
    }
  }
}
