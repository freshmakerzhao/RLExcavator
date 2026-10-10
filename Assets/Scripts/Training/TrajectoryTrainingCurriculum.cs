using UnityEngine;

namespace RLExcavator
{
  /// <summary>
  /// Applies trajectory-training difficulty for the scale excavator.
  /// Machine speed limits still come only from machine_profile.jsonc.
  /// </summary>
  [DisallowMultipleComponent]
  [DefaultExecutionOrder( -10000 )]
  public class TrajectoryTrainingCurriculum : MonoBehaviour
  {
    public enum CurriculumStage
    {
      Disabled = 0,
      EasyTraining = 1,
      FullTraining = 2
    }

    [Header( "References" )]
    [Tooltip( "轨迹点生成器。" )]
    public TrajectoryProvider trajectoryProvider;

    [Tooltip( "轨迹跟踪 Agent。" )]
    public ExcavatorTrajectoryAgent trajectoryAgent;

    [Header( "Curriculum" )]
    [Tooltip( "当前课程阶段。" )]
    public CurriculumStage stage = CurriculumStage.EasyTraining;

    [Tooltip( "Awake 时自动应用当前阶段参数。" )]
    public bool applyOnAwake = true;

    [Tooltip( "OnValidate 时同步 Inspector 参数，方便编辑 prefab。" )]
    public bool applyInEditMode = false;

    [Tooltip( "应用课程参数时打印一次日志。" )]
    public bool logAppliedSettings = true;

    private void Awake()
    {
      if ( applyOnAwake )
        Apply();
    }

    private void OnValidate()
    {
      if ( applyInEditMode && !Application.isPlaying )
        Apply();
    }

    [ContextMenu( "Apply Curriculum Stage" )]
    public void Apply()
    {
      ResolveReferences();

      if ( stage == CurriculumStage.Disabled )
        return;

      if ( trajectoryProvider == null || trajectoryAgent == null ) {
        Debug.LogWarning( "[TrajectoryTrainingCurriculum] Missing TrajectoryProvider or ExcavatorTrajectoryAgent.", this );
        return;
      }

      switch ( stage ) {
        case CurriculumStage.EasyTraining:
          ApplyToProvider(
            randomWaypointCount: 2,
            useCurvedPaths: false,
            curveStrength: 0f,
            jitter: 0.03f,
            useCurrentTipAsPathStart: true,
            minEndpointDistance: 0.06f,
            maxEndpointDistance: 0.20f,
            nearbyParameterRadius: 0.12f );
          ApplyToAgent( scaleWaypointBonusByBucketAttitude: false );
          break;
        case CurriculumStage.FullTraining:
          ApplyToProvider(
            randomWaypointCount: 5,
            useCurvedPaths: true,
            curveStrength: 0.16f,
            jitter: 0.12f,
            useCurrentTipAsPathStart: false,
            minEndpointDistance: 0.1f,
            maxEndpointDistance: 0f,
            nearbyParameterRadius: 0.12f );
          ApplyToAgent( scaleWaypointBonusByBucketAttitude: true );
          break;
      }

      if ( logAppliedSettings ) {
        Debug.Log(
          "[TrajectoryTrainingCurriculum] Applied "
          + $"{stage}: waypoints={trajectoryProvider.randomWaypointCount}, "
          + $"curved={trajectoryProvider.reachableWorkspaceUseCurvedPaths}, "
          + $"curve={trajectoryProvider.reachableWorkspaceCurveStrength:F2}, "
          + $"jitter=({trajectoryProvider.reachableWorkspaceDirectionJitter:F2},"
          + $"{trajectoryProvider.reachableWorkspaceHeightJitter:F2},"
          + $"{trajectoryProvider.reachableWorkspaceDepthJitter:F2}), "
          + $"tip_start={trajectoryProvider.useCurrentTipAsPathStart}, "
          + $"distance=[{trajectoryProvider.reachableWorkspaceMinEndpointDistance:F2},"
          + $"{trajectoryProvider.reachableWorkspaceMaxEndpointDistance:F2}], "
          + $"pitch_bonus_scale={trajectoryAgent.scaleWaypointBonusByBucketAttitude}",
          this );
      }
    }

    private void ResolveReferences()
    {
      if ( trajectoryProvider == null )
        trajectoryProvider = GetComponentInChildren<TrajectoryProvider>( true );

      if ( trajectoryAgent == null )
        trajectoryAgent = GetComponentInChildren<ExcavatorTrajectoryAgent>( true );
    }

    private void ApplyToProvider(
      int randomWaypointCount,
      bool useCurvedPaths,
      float curveStrength,
      float jitter,
      bool useCurrentTipAsPathStart,
      float minEndpointDistance,
      float maxEndpointDistance,
      float nearbyParameterRadius )
    {
      trajectoryProvider.randomWaypointCount = Mathf.Max( randomWaypointCount, 1 );
      trajectoryProvider.reachableWorkspaceUseCurvedPaths = useCurvedPaths;
      trajectoryProvider.reachableWorkspaceCurveStrength = Mathf.Clamp( curveStrength, 0f, 0.5f );
      trajectoryProvider.reachableWorkspaceDirectionJitter = Mathf.Clamp( jitter, 0f, 0.35f );
      trajectoryProvider.reachableWorkspaceHeightJitter = Mathf.Clamp( jitter, 0f, 0.35f );
      trajectoryProvider.reachableWorkspaceDepthJitter = Mathf.Clamp( jitter, 0f, 0.35f );
      trajectoryProvider.useCurrentTipAsPathStart = useCurrentTipAsPathStart;
      trajectoryProvider.currentTipPathStart = useCurrentTipAsPathStart ? trajectoryAgent.bucketTip : null;
      trajectoryProvider.reachableWorkspaceMinEndpointDistance = Mathf.Max( minEndpointDistance, 0f );
      trajectoryProvider.reachableWorkspaceMaxEndpointDistance = Mathf.Max( maxEndpointDistance, 0f );
      trajectoryProvider.nearbyPathParameterRadius = Mathf.Clamp( nearbyParameterRadius, 0.01f, 0.5f );
    }

    private void ApplyToAgent( bool scaleWaypointBonusByBucketAttitude )
    {
      trajectoryAgent.scaleWaypointBonusByBucketAttitude = scaleWaypointBonusByBucketAttitude;
    }
  }
}
