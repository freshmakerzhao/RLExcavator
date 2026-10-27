using System;
using System.Collections.Generic;
using UnityEngine;

namespace RLExcavator
{
  /// <summary>
  /// 用局部二次 Bezier 圆角生成参考点列，再交给 ContinuousReferencePath 按弧长跟踪。
  /// 保留首尾，不修改动作；这是参考几何构造，不是全局规划或避障。
  /// </summary>
  public static class SmoothReferencePath
  {
    public static Vector3[] Build( IReadOnlyList<Vector3> points, float cornerCutDistanceM = 0.08f,
                                   float sampleSpacingM = 0.01f )
    {
      if ( points == null )
        throw new ArgumentNullException( nameof( points ) );
      if ( points.Count == 0 )
        throw new ArgumentException( "A reference path needs at least one point.", nameof( points ) );
      if ( !Finite( cornerCutDistanceM ) || cornerCutDistanceM < 0f )
        throw new ArgumentOutOfRangeException( nameof( cornerCutDistanceM ) );
      if ( !Finite( sampleSpacingM ) || sampleSpacingM <= 0f )
        throw new ArgumentOutOfRangeException( nameof( sampleSpacingM ) );

      var copy = new Vector3[ points.Count ];
      for ( int i = 0; i < points.Count; ++i ) {
        Vector3 point = points[ i ];
        if ( !Finite( point.x ) || !Finite( point.y ) || !Finite( point.z ) )
          throw new ArgumentException( "Reference points must be finite.", nameof( points ) );
        copy[ i ] = point;
      }
      if ( cornerCutDistanceM == 0f )
        return copy;

      var distinct = new List<Vector3> { copy[ 0 ] };
      for ( int i = 1; i < copy.Length; ++i ) {
        if ( ( copy[ i ] - distinct[ distinct.Count - 1 ] ).sqrMagnitude > 1e-12f )
          distinct.Add( copy[ i ] );
      }
      var result = new List<Vector3> { distinct[ 0 ] };
      for ( int i = 1; i < distinct.Count - 1; ++i ) {
        Vector3 corner = distinct[ i ];
        Vector3 incoming = corner - distinct[ i - 1 ];
        Vector3 outgoing = distinct[ i + 1 ] - corner;
        float dot = Vector3.Dot( incoming.normalized, outgoing.normalized );
        // 直线无需切角；180度折返保留原路线，不擅自构造绕行。
        if ( Mathf.Abs( dot ) > 0.999999f ) {
          AddLine( result, corner, sampleSpacingM );
          continue;
        }
        // 每个圆角最多占用相邻段的45%，相邻短段上的圆角不会交叉。
        float cut = Mathf.Min( cornerCutDistanceM, 0.45f * Mathf.Min( incoming.magnitude, outgoing.magnitude ) );
        Vector3 entry = corner - incoming.normalized * cut;
        Vector3 exit = corner + outgoing.normalized * cut;
        AddLine( result, entry, sampleSpacingM );
        int samples = Mathf.Max( 1, Mathf.CeilToInt( 2f * cut / sampleSpacingM ) );
        for ( int sample = 1; sample <= samples; ++sample ) {
          float t = (float)sample / samples;
          float u = 1f - t;
          result.Add( u * u * entry + 2f * u * t * corner + t * t * exit );
        }
      }
      AddLine( result, distinct[ distinct.Count - 1 ], sampleSpacingM );
      return result.ToArray();
    }

    private static void AddLine( List<Vector3> points, Vector3 end, float spacing )
    {
      Vector3 start = points[ points.Count - 1 ];
      float distance = Vector3.Distance( start, end );
      if ( distance <= 1e-6f )
        return;
      int samples = Mathf.Max( 1, Mathf.CeilToInt( distance / spacing ) );
      for ( int sample = 1; sample <= samples; ++sample )
        points.Add( Vector3.Lerp( start, end, (float)sample / samples ) );
    }

    private static bool Finite( float value ) => !float.IsNaN( value ) && !float.IsInfinity( value );
  }
}
