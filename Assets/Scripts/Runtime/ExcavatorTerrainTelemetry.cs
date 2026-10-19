using System;
using UnityEngine;

namespace RLExcavator
{
  /// <summary>按需导出活动 AGX 地形，不推进物理、不采集图像，也不把颗粒混入高度场。</summary>
  public sealed class ExcavatorTerrainTelemetry : MonoBehaviour
  {
    public ExcavatorRuntimeSoil soil;
    public Transform machineRoot;

    [Serializable]
    public sealed class State
    {
      public string schema = "terrain_grid_v1";
      public bool available;
      public string reason;
      public string source = "agx_terrain_heightfield_v1";
      public string source_kind = "simulation_ground_truth";
      public string frame = "machine_root_ros";
      public int rows;
      public int cols;
      public double[] origin_xy_m = Array.Empty<double>();
      public double[] column_step_xy_m = Array.Empty<double>();
      public double[] row_step_xy_m = Array.Empty<double>();
      public double[] surface_height_m = Array.Empty<double>();
      public bool[] surface_valid = Array.Empty<bool>();
      public double[] bed_height_m = Array.Empty<double>();
      public bool[] bed_valid = Array.Empty<bool>();
      public bool dynamic_particles_included;
    }

    public State ReadState()
    {
      if ( soil == null || machineRoot == null ) return Missing( "terrain_reader_unconfigured" );
      if ( soil.IsResetting ) return Missing( "soil_resetting" );
      if ( !string.IsNullOrEmpty( soil.ResetError ) ) return Missing( "soil_reset_failed" );
      // reset 会替换 ActiveTerrain。每次现取，不缓存 Native，也不调用 GetInitialized。
      var terrain = soil.ActiveTerrain;
      if ( terrain == null || terrain.Native == null ) return Missing( "terrain_uninitialized" );
      try {
        var native = terrain.Native;
        int cols = (int)native.getResolutionX();
        int rows = (int)native.getResolutionY();
        float spacing = (float)native.getElementSize();
        Matrix4x4 toRoot = machineRoot.worldToLocalMatrix * terrain.transform.localToWorldMatrix;
        // 与 MovableTerrain.SetupMesh 相同：采样点以网格中心对称，不能再减 MaximumDepth。
        Vector3 origin = Ros( toRoot.MultiplyPoint3x4(
          new Vector3( -(cols - 1) * spacing * .5f, 0, -(rows - 1) * spacing * .5f ) ) );
        Vector3 column = Ros( toRoot.MultiplyVector( Vector3.right * spacing ) );
        Vector3 row = Ros( toRoot.MultiplyVector( Vector3.forward * spacing ) );
        Vector3 height = Ros( toRoot.MultiplyVector( Vector3.up ) );
        return SampleGrid( rows, cols, origin, column, row, height,
          (r, c) => native.getHeight( new agx.Vec2i( cols - 1 - c, rows - 1 - r ) ),
          (r, c) => native.getMinimumHeight( new agx.Vec2i( cols - 1 - c, rows - 1 - r ) ) );
      }
      catch ( Exception error ) {
        // 不发布部分地图，不把读取故障解释成空料池。
        return Missing( "terrain_read_failed:" + error.GetType().Name );
      }
    }

    /// <summary>同一采样核心供原生读取和契约测试使用；输入向量已在机器 ROS 坐标中。</summary>
    public static State SampleGrid( int rows, int cols, Vector3 originRos,
      Vector3 columnStepRos, Vector3 rowStepRos, Vector3 heightAxisRos,
      Func<int, int, double> surfaceReader, Func<int, int, double> bedReader )
    {
      if ( rows <= 0 || cols <= 0 || (long)rows * cols > int.MaxValue )
        return Missing( "invalid_grid_shape" );
      if ( !Finite( originRos ) || !ValidDirections( columnStepRos, rowStepRos, heightAxisRos ) )
        return Missing( "unsupported_grid_orientation" );

      int count = rows * cols;
      var surface = new double[count];
      var bed = new double[count];
      var surfaceValid = new bool[count];
      var bedValid = new bool[count];
      bool hasSurface = false;
      for ( int row = 0; row < rows; ++row ) {
        for ( int col = 0; col < cols; ++col ) {
          int index = row * cols + col;
          double surfaceZ = originRos.z + surfaceReader( row, col ) * heightAxisRos.z;
          double bedZ = originRos.z + bedReader( row, col ) * heightAxisRos.z;
          surfaceValid[index] = Finite( surfaceZ );
          bedValid[index] = Finite( bedZ );
          surface[index] = surfaceValid[index] ? surfaceZ : 0;
          bed[index] = bedValid[index] ? bedZ : 0;
          hasSurface |= surfaceValid[index];
          if ( surfaceValid[index] && bedValid[index] && surfaceZ + 1e-5 < bedZ )
            return Missing( "invalid_height_layers" );
        }
      }
      return new State {
        available = hasSurface, reason = hasSurface ? "ok" : "no_valid_surface",
        rows = rows, cols = cols,
        origin_xy_m = new double[] { originRos.x, originRos.y },
        column_step_xy_m = new double[] { columnStepRos.x, columnStepRos.y },
        row_step_xy_m = new double[] { rowStepRos.x, rowStepRos.y },
        surface_height_m = surface, surface_valid = surfaceValid,
        bed_height_m = bed, bed_valid = bedValid
      };
    }

    private static bool ValidDirections( Vector3 column, Vector3 row, Vector3 height )
    {
      if ( !Finite( column ) || !Finite( row ) || !Finite( height ) ) return false;
      double c = Math.Sqrt( (double)column.x * column.x + (double)column.y * column.y );
      double r = Math.Sqrt( (double)row.x * row.x + (double)row.y * row.y );
      double h = Math.Sqrt( (double)height.x * height.x + (double)height.y * height.y +
                            (double)height.z * height.z );
      if ( c <= 1e-9 || r <= 1e-9 || h <= 0 || height.z <= 0 ) return false;
      // 不把倾斜高度场或带竖向行列增量的网格压扁为 XY 网格。
      return Math.Abs( ((double)column.x * row.x + (double)column.y * row.y) / (c * r) ) <= 1e-5 &&
             Math.Abs( column.z ) / c <= 1e-5 && Math.Abs( row.z ) / r <= 1e-5 &&
             Math.Sqrt( (double)height.x * height.x + (double)height.y * height.y ) / h <= 1e-5;
    }

    private static Vector3 Ros( Vector3 local ) => new Vector3( local.z, -local.x, local.y );
    private static bool Finite( double value ) => !double.IsNaN( value ) && !double.IsInfinity( value );
    private static bool Finite( Vector3 value ) => Finite( value.x ) && Finite( value.y ) && Finite( value.z );
    private static State Missing( string reason ) => new State { reason = reason };
  }
}
