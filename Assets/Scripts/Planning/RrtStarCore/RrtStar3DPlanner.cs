using System;
using System.Collections.Generic;
using UnityEngine;

namespace RLExcavator
{
  public interface IRrtStar3DCollisionChecker
  {
    bool IsPointValid( Vector3 point, float radius );
    bool IsSegmentValid( Vector3 from, Vector3 to, float radius );
  }

  [Serializable]
  public class RrtStar3DSettings
  {
    public int maxIterations = 1200;
    public float stepSize = 4.5f;
    [Range( 0f, 0.6f )]
    public float goalBias = 0.12f;
    public float goalTolerance = 4.5f;
    public float rewireRadius = 9f;
    public float collisionRadius = 1.0f;
    public int edgeCheckResolution = 8;
    public int smoothingPasses = 3;
    public int seed = 7;
    public bool preferDirectPathWhenClear = false;
  }

  public readonly struct RrtStar3DNode
  {
    public readonly Vector3 position;
    public readonly int parentIndex;
    public readonly float cost;

    public RrtStar3DNode( Vector3 position, int parentIndex, float cost )
    {
      this.position = position;
      this.parentIndex = parentIndex;
      this.cost = cost;
    }
  }

  public class RrtStar3DResult
  {
    public bool success;
    public Vector3[] rawPath = Array.Empty<Vector3>();
    public Vector3[] smoothedPath = Array.Empty<Vector3>();
    public RrtStar3DNode[] nodes = Array.Empty<RrtStar3DNode>();
    public int iterations;
    public string summary = "Not planned";
  }

  public class PhysicsRrtStar3DCollisionChecker : IRrtStar3DCollisionChecker
  {
    private readonly LayerMask m_obstacleMask;
    private readonly int m_edgeCheckResolution;

    public PhysicsRrtStar3DCollisionChecker( LayerMask obstacleMask, int edgeCheckResolution )
    {
      m_obstacleMask = obstacleMask;
      m_edgeCheckResolution = Mathf.Max( edgeCheckResolution, 1 );
    }

    public bool IsPointValid( Vector3 point, float radius )
    {
      if ( (int)m_obstacleMask == 0 || radius <= 0f )
        return true;

      return !Physics.CheckSphere( point, radius, m_obstacleMask, QueryTriggerInteraction.Ignore );
    }

    public bool IsSegmentValid( Vector3 from, Vector3 to, float radius )
    {
      if ( !IsPointValid( from, radius ) || !IsPointValid( to, radius ) )
        return false;

      Vector3 delta = to - from;
      float sqrDistance = delta.sqrMagnitude;
      if ( sqrDistance < 0.0001f )
        return true;

      for ( int i = 1; i < m_edgeCheckResolution; i++ ) {
        Vector3 point = Vector3.Lerp( from, to, (float)i / m_edgeCheckResolution );
        if ( !IsPointValid( point, radius ) )
          return false;
      }

      return true;
    }

  }

  public class RrtStar3DPlanner
  {
    private readonly RrtStar3DSettings m_settings;
    private readonly IRrtStar3DCollisionChecker m_collisionChecker;
    private readonly System.Random m_random;

    public RrtStar3DPlanner( RrtStar3DSettings settings, IRrtStar3DCollisionChecker collisionChecker = null )
    {
      m_settings = settings ?? new RrtStar3DSettings();
      m_collisionChecker = collisionChecker;
      m_random = new System.Random( m_settings.seed );
    }

    public RrtStar3DResult Plan( Bounds planningBounds, Vector3 start, Vector3 goal )
    {
      var result = new RrtStar3DResult();
      var nodes = new List<RrtStar3DNode>();

      if ( !planningBounds.Contains( start ) || !planningBounds.Contains( goal ) ) {
        result.summary = "Start or goal is outside planning bounds.";
        return result;
      }

      if ( !IsPointValid( start ) || !IsPointValid( goal ) ) {
        result.summary = "Start or goal collides with an obstacle.";
        return result;
      }

      if ( m_settings.preferDirectPathWhenClear && IsSegmentValid( start, goal ) ) {
        result.success = true;
        result.rawPath = new[] { start, goal };
        result.smoothedPath = result.rawPath;
        result.nodes = new[] { new RrtStar3DNode( start, -1, 0f ) };
        result.summary = "Direct path is collision free.";
        return result;
      }

      nodes.Add( new RrtStar3DNode( start, -1, 0f ) );

      int bestGoalParent = -1;
      float bestGoalCost = float.PositiveInfinity;
      int maxIterations = Mathf.Max( m_settings.maxIterations, 1 );
      float stepSize = Mathf.Max( m_settings.stepSize, 0.01f );
      float goalTolerance = Mathf.Max( m_settings.goalTolerance, 0.01f );
      float rewireRadius = Mathf.Max( m_settings.rewireRadius, stepSize );

      for ( int iteration = 0; iteration < maxIterations; iteration++ ) {
        result.iterations = iteration + 1;

        Vector3 sample = NextFloat01() < m_settings.goalBias ? goal : SamplePoint( planningBounds );
        int nearestIndex = NearestNodeIndex( nodes, sample );
        Vector3 candidate = Steer( nodes[ nearestIndex ].position, sample, stepSize );

        if ( !planningBounds.Contains( candidate ) )
          continue;

        if ( Vector3.Distance( nodes[ nearestIndex ].position, candidate ) < 0.03f )
          continue;

        if ( !IsPointValid( candidate ) || !IsSegmentValid( nodes[ nearestIndex ].position, candidate ) )
          continue;

        int parentIndex = nearestIndex;
        float bestCost = nodes[ nearestIndex ].cost + Vector3.Distance( nodes[ nearestIndex ].position, candidate );

        for ( int i = 0; i < nodes.Count; i++ ) {
          float distance = Vector3.Distance( nodes[ i ].position, candidate );
          if ( distance > rewireRadius )
            continue;

          float candidateCost = nodes[ i ].cost + distance;
          if ( candidateCost >= bestCost || !IsSegmentValid( nodes[ i ].position, candidate ) )
            continue;

          parentIndex = i;
          bestCost = candidateCost;
        }

        int newIndex = nodes.Count;
        nodes.Add( new RrtStar3DNode( candidate, parentIndex, bestCost ) );

        for ( int i = 1; i < nodes.Count - 1; i++ ) {
          if ( IsAncestor( nodes, parentIndex, i ) )
            continue;

          float distance = Vector3.Distance( nodes[ i ].position, candidate );
          if ( distance > rewireRadius )
            continue;

          float rewiredCost = bestCost + distance;
          if ( rewiredCost >= nodes[ i ].cost || !IsSegmentValid( candidate, nodes[ i ].position ) )
            continue;

          nodes[ i ] = new RrtStar3DNode( nodes[ i ].position, newIndex, rewiredCost );
        }

        float distanceToGoal = Vector3.Distance( candidate, goal );
        if ( distanceToGoal > goalTolerance || !IsSegmentValid( candidate, goal ) )
          continue;

        float goalCost = bestCost + distanceToGoal;
        if ( goalCost < bestGoalCost ) {
          bestGoalParent = newIndex;
          bestGoalCost = goalCost;
        }
      }

      result.nodes = nodes.ToArray();
      if ( bestGoalParent < 0 ) {
        result.summary = $"RRT* failed to connect the goal. Nodes: {nodes.Count}.";
        return result;
      }

      result.rawPath = BuildRawPath( nodes, bestGoalParent, goal );
      result.smoothedPath = SmoothPath( result.rawPath, Mathf.Max( m_settings.smoothingPasses, 0 ) );
      result.success = true;
      result.summary = $"RRT* success. Iterations: {result.iterations}, nodes: {nodes.Count}, raw points: {result.rawPath.Length}, smoothed points: {result.smoothedPath.Length}.";
      return result;
    }

    public bool IsResultPathCollisionFree( Vector3[] path )
    {
      if ( path == null || path.Length == 0 )
        return false;

      for ( int i = 0; i < path.Length; i++ )
        if ( !IsPointValid( path[ i ] ) )
          return false;

      for ( int i = 1; i < path.Length; i++ )
        if ( !IsSegmentValid( path[ i - 1 ], path[ i ] ) )
          return false;

      return true;
    }

    private Vector3[] BuildRawPath( List<RrtStar3DNode> nodes, int goalParentIndex, Vector3 goal )
    {
      var reversed = new List<Vector3> { goal };
      int index = goalParentIndex;
      int guard = 0;
      while ( index >= 0 ) {
        reversed.Add( nodes[ index ].position );
        index = nodes[ index ].parentIndex;
        guard++;
        if ( guard > nodes.Count )
          break;
      }

      reversed.Reverse();
      return reversed.ToArray();
    }

    private static bool IsAncestor( List<RrtStar3DNode> nodes, int childIndex, int possibleAncestorIndex )
    {
      int index = childIndex;
      while ( index >= 0 && index < nodes.Count ) {
        if ( index == possibleAncestorIndex )
          return true;

        index = nodes[ index ].parentIndex;
      }

      return false;
    }

    private Vector3[] SmoothPath( Vector3[] rawPath, int passes )
    {
      if ( rawPath == null || rawPath.Length <= 2 || passes <= 0 )
        return rawPath ?? Array.Empty<Vector3>();

      var path = new List<Vector3>( rawPath );
      for ( int pass = 0; pass < passes; pass++ ) {
        int index = 0;
        while ( index < path.Count - 2 ) {
          if ( IsSegmentValid( path[ index ], path[ index + 2 ] ) )
            path.RemoveAt( index + 1 );
          else
            index++;
        }
      }

      return path.ToArray();
    }

    private bool IsPointValid( Vector3 point )
    {
      return m_collisionChecker == null || m_collisionChecker.IsPointValid( point, Mathf.Max( m_settings.collisionRadius, 0f ) );
    }

    private bool IsSegmentValid( Vector3 from, Vector3 to )
    {
      return m_collisionChecker == null || m_collisionChecker.IsSegmentValid( from, to, Mathf.Max( m_settings.collisionRadius, 0f ) );
    }

    private Vector3 SamplePoint( Bounds bounds )
    {
      Vector3 min = bounds.min;
      Vector3 max = bounds.max;
      return new Vector3(
        Mathf.Lerp( min.x, max.x, NextFloat01() ),
        Mathf.Lerp( min.y, max.y, NextFloat01() ),
        Mathf.Lerp( min.z, max.z, NextFloat01() ) );
    }

    private float NextFloat01()
    {
      return (float)m_random.NextDouble();
    }

    private static Vector3 Steer( Vector3 from, Vector3 to, float maxDistance )
    {
      Vector3 delta = to - from;
      float distance = delta.magnitude;
      if ( distance <= maxDistance || distance < 0.0001f )
        return to;

      return from + delta / distance * maxDistance;
    }

    private static int NearestNodeIndex( List<RrtStar3DNode> nodes, Vector3 sample )
    {
      int bestIndex = 0;
      float bestDistance = ( nodes[ 0 ].position - sample ).sqrMagnitude;
      for ( int i = 1; i < nodes.Count; i++ ) {
        float distance = ( nodes[ i ].position - sample ).sqrMagnitude;
        if ( distance >= bestDistance )
          continue;

        bestIndex = i;
        bestDistance = distance;
      }

      return bestIndex;
    }
  }
}
