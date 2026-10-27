using System;
using System.Collections.Generic;
using UnityEngine;

namespace RLExcavator
{
  /// <summary>按弧长表达原始折线路径；不生成平滑曲线，也不修改策略动作。</summary>
  public sealed class ContinuousReferencePath
  {
    public readonly struct Projection
    {
      public float ArcLength { get; }
      public Vector3 Point { get; }
      public Vector3 Tangent { get; }
      public float Distance { get; }

      public Projection( float arcLength, Vector3 point, Vector3 tangent, float distance )
      {
        ArcLength = arcLength;
        Point = point;
        Tangent = tangent;
        Distance = distance;
      }
    }

    private readonly Vector3[] m_points;
    private readonly float[] m_arcLengths;

    public ContinuousReferencePath( IReadOnlyList<Vector3> points )
    {
      if ( points == null )
        throw new ArgumentNullException( nameof( points ) );
      if ( points.Count == 0 )
        throw new ArgumentException( "A reference path needs at least one point.", nameof( points ) );

      // 复制输入并去掉连续重合点，使每条保留线段均有明确方向。
      var distinct = new List<Vector3> { points[ 0 ] };
      for ( int i = 1; i < points.Count; ++i ) {
        if ( ( points[ i ] - distinct[ distinct.Count - 1 ] ).sqrMagnitude > 1e-12f )
          distinct.Add( points[ i ] );
      }
      m_points = distinct.ToArray();
      m_arcLengths = new float[ m_points.Length ];
      for ( int i = 1; i < m_points.Length; ++i )
        m_arcLengths[ i ] = m_arcLengths[ i - 1 ] + Vector3.Distance( m_points[ i - 1 ], m_points[ i ] );
    }

    public float Length => m_arcLengths[ m_arcLengths.Length - 1 ];
    public Vector3 StartPoint => m_points[ 0 ];
    public Vector3 EndPoint => m_points[ m_points.Length - 1 ];

    public Vector3 PointAt( float arcLength )
    {
      if ( m_points.Length == 1 )
        return StartPoint;
      float clampedArc = Mathf.Clamp( arcLength, 0f, Length );
      int segment = SegmentAt( clampedArc );
      return m_points[ segment ] + SegmentTangent( segment ) * ( clampedArc - m_arcLengths[ segment ] );
    }

    /// <summary>折点使用下一段方向；终点使用最后一段方向。</summary>
    public Vector3 TangentAt( float arcLength )
      => m_points.Length == 1 ? Vector3.zero : SegmentTangent( SegmentAt( arcLength ) );

    /// <summary>弧长邻域内的转角重要度：直线0、直角0.5；相邻角取最大值，不重复叠罚。</summary>
    public float CornerImportance( float arcLength, float influenceDistanceM )
    {
      if ( influenceDistanceM <= 0f )
        return 0f;
      float importance = 0f;
      for ( int i = 1; i < m_points.Length - 1; ++i ) {
        float proximity = Mathf.Clamp01( 1f - Mathf.Abs( arcLength - m_arcLengths[ i ] ) / influenceDistanceM );
        float cosine = Mathf.Clamp( Vector3.Dot( SegmentTangent( i - 1 ), SegmentTangent( i ) ), -1f, 1f );
        importance = Mathf.Max( importance, proximity * ( 1f - cosine ) * 0.5f );
      }
      return importance;
    }

    /// <summary>
    /// 在上次沿程位置前后 maxAdvance 米内投影，避免自交路径跳到远处线段。
    /// maxAdvance 由调用者按实际位移给出；本类不积累进度、不要求进入路径点球。
    /// </summary>
    public Projection Project( Vector3 position, float previousArcLength, float maxAdvance )
    {
      if ( maxAdvance < 0f )
        throw new ArgumentOutOfRangeException( nameof( maxAdvance ) );
      float previous = Mathf.Clamp( previousArcLength, 0f, Length );
      float lower = Mathf.Max( 0f, previous - maxAdvance );
      float upper = Mathf.Min( Length, previous + maxAdvance );
      float bestArc = previous;
      Vector3 bestPoint = PointAt( previous );
      float bestSquaredDistance = ( position - bestPoint ).sqrMagnitude;

      for ( int i = 0; i < m_points.Length - 1; ++i ) {
        float segmentLower = Mathf.Max( lower, m_arcLengths[ i ] );
        float segmentUpper = Mathf.Min( upper, m_arcLengths[ i + 1 ] );
        if ( segmentLower > segmentUpper )
          continue;
        Vector3 tangent = SegmentTangent( i );
        float arc = Mathf.Clamp( m_arcLengths[ i ] + Vector3.Dot( position - m_points[ i ], tangent ),
                                segmentLower, segmentUpper );
        Vector3 point = m_points[ i ] + tangent * ( arc - m_arcLengths[ i ] );
        float squaredDistance = ( position - point ).sqrMagnitude;
        // 距离相同则保留更接近原沿程位置的投影，不凭空向前跳。
        bool nearerArc = Mathf.Abs( arc - previous ) < Mathf.Abs( bestArc - previous );
        if ( squaredDistance < bestSquaredDistance - 1e-8f ||
             ( Mathf.Abs( squaredDistance - bestSquaredDistance ) <= 1e-8f && nearerArc ) ) {
          bestArc = arc;
          bestPoint = point;
          bestSquaredDistance = squaredDistance;
        }
      }
      return new Projection( bestArc, bestPoint, TangentAt( bestArc ), Mathf.Sqrt( bestSquaredDistance ) );
    }

    private int SegmentAt( float arcLength )
    {
      for ( int i = 0; i < m_points.Length - 2; ++i ) {
        if ( arcLength < m_arcLengths[ i + 1 ] )
          return i;
      }
      return m_points.Length - 2;
    }

    private Vector3 SegmentTangent( int segment )
      => ( m_points[ segment + 1 ] - m_points[ segment ] ) /
         ( m_arcLengths[ segment + 1 ] - m_arcLengths[ segment ] );
  }
}
