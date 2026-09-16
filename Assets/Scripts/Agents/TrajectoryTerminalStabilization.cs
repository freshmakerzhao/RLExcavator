using UnityEngine;

namespace RLExcavator
{
  /// <summary>
  /// Pure terminal-approach shaping shared by training and deterministic tests.
  /// It does not alter actions; it only evaluates whether the policy is braking.
  /// </summary>
  public static class TrajectoryTerminalStabilization
  {
    public static float CalculatePenalty(
      float distance,
      float approachDistance,
      float normalizedSwingVelocity,
      float swingAction,
      float velocityWeight,
      float driveWeight )
    {
      if ( !IsFinite( distance )
        || !IsFinite( approachDistance )
        || !IsFinite( normalizedSwingVelocity )
        || !IsFinite( swingAction )
        || approachDistance <= 0f
        || distance >= approachDistance )
        return 0f;

      float proximity = Mathf.Clamp01( 1f - Mathf.Max( distance, 0f ) / approachDistance );
      float velocity = Mathf.Clamp( normalizedSwingVelocity, -1f, 1f );
      float action = Mathf.Clamp( swingAction, -1f, 1f );
      float sameDirectionDrive = Mathf.Max( velocity * action, 0f );
      float cost = Mathf.Max( velocityWeight, 0f ) * velocity * velocity
        + Mathf.Max( driveWeight, 0f ) * sameDirectionDrive * sameDirectionDrive;
      return -proximity * cost;
    }

    public static bool IsSettled(
      float distance,
      float reachThreshold,
      float normalizedSwingVelocity,
      float maxAbsNormalizedSwingVelocity )
    {
      if ( !IsFinite( distance )
        || !IsFinite( reachThreshold )
        || !IsFinite( normalizedSwingVelocity )
        || !IsFinite( maxAbsNormalizedSwingVelocity ) )
        return false;

      return distance <= Mathf.Max( reachThreshold, 0f )
        && Mathf.Abs( normalizedSwingVelocity ) <= Mathf.Max( maxAbsNormalizedSwingVelocity, 0f );
    }

    public static int NextStableCount( int currentCount, bool settled )
    {
      return settled ? Mathf.Max( currentCount, 0 ) + 1 : 0;
    }

    private static bool IsFinite( float value )
    {
      return !float.IsNaN( value ) && !float.IsInfinity( value );
    }
  }
}
