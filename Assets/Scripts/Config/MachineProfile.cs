using System;
using System.Collections.Generic;

namespace RLExcavator
{
  public sealed class MachineProfile
  {
    private readonly string[] m_actionOrder;
    private readonly MachineActuatorProfile[] m_actuators;
    private readonly Dictionary<string, MachineActuatorProfile> m_actuatorsByName;

    public MachineProfile(
      string schemaVersion,
      string machineId,
      string machineType,
      string reachableWorkspaceFile,
      string[] actionOrder,
      MachineActuatorProfile[] actuators,
      MachineObservationSchema observationSchema,
      MachineTaskProfile taskProfile )
    {
      SchemaVersion = RequireText( schemaVersion, nameof( schemaVersion ) );
      MachineId = RequireText( machineId, nameof( machineId ) );
      MachineType = RequireText( machineType, nameof( machineType ) );
      ReachableWorkspaceFile = RequireText( reachableWorkspaceFile, nameof( reachableWorkspaceFile ) );
      ObservationSchema = observationSchema ?? throw new ArgumentNullException( nameof( observationSchema ) );
      TaskProfile = taskProfile ?? throw new ArgumentNullException( nameof( taskProfile ) );

      if ( actionOrder == null || actionOrder.Length == 0 )
        throw new ArgumentException( "Profile action_order must not be empty.", nameof( actionOrder ) );
      if ( actuators == null || actuators.Length == 0 )
        throw new ArgumentException( "Profile actuators must not be empty.", nameof( actuators ) );

      m_actionOrder = (string[])actionOrder.Clone();
      m_actuators = (MachineActuatorProfile[])actuators.Clone();
      m_actuatorsByName = new Dictionary<string, MachineActuatorProfile>( StringComparer.Ordinal );

      for ( int i = 0; i < m_actuators.Length; i++ ) {
        MachineActuatorProfile actuator = m_actuators[ i ];
        if ( actuator == null )
          throw new ArgumentException( "Profile actuators must not contain null entries.", nameof( actuators ) );
        if ( m_actuatorsByName.ContainsKey( actuator.Name ) )
          throw new ArgumentException( "Duplicate actuator name in profile: " + actuator.Name, nameof( actuators ) );

        m_actuatorsByName.Add( actuator.Name, actuator );
      }

      for ( int i = 0; i < m_actionOrder.Length; i++ ) {
        string actuatorName = RequireText( m_actionOrder[ i ], "action_order[" + i + "]" );
        if ( !m_actuatorsByName.ContainsKey( actuatorName ) )
          throw new ArgumentException( "action_order references missing actuator: " + actuatorName, nameof( actionOrder ) );
      }
    }

    public string SchemaVersion { get; }
    public string MachineId { get; }
    public string MachineType { get; }
    public string ReachableWorkspaceFile { get; }
    public MachineObservationSchema ObservationSchema { get; }
    public MachineTaskProfile TaskProfile { get; }

    public string[] ActionOrder
    {
      get { return (string[])m_actionOrder.Clone(); }
    }

    public MachineActuatorProfile[] Actuators
    {
      get { return (MachineActuatorProfile[])m_actuators.Clone(); }
    }

    public MachineActuatorProfile GetActuator( string name )
    {
      MachineActuatorProfile actuator;
      if ( m_actuatorsByName.TryGetValue( name, out actuator ) )
        return actuator;

      throw new KeyNotFoundException( "Machine profile does not contain actuator: " + name );
    }

    private static string RequireText( string value, string fieldName )
    {
      if ( string.IsNullOrEmpty( value ) )
        throw new ArgumentException( "Profile field must not be empty: " + fieldName, fieldName );

      return value;
    }
  }

  public sealed class MachineTaskProfile
  {
    public MachineTaskProfile( float moveToDigBucketPitchTargetDeg, float carryMaterialBucketPitchTargetDeg )
    {
      MoveToDigBucketPitchTargetDeg = moveToDigBucketPitchTargetDeg;
      CarryMaterialBucketPitchTargetDeg = carryMaterialBucketPitchTargetDeg;
    }

    public float MoveToDigBucketPitchTargetDeg { get; }
    public float CarryMaterialBucketPitchTargetDeg { get; }
  }

  public sealed class MachineActuatorProfile
  {
    public MachineActuatorProfile(
      string name,
      int actionIndex,
      string jointType,
      float maxSpeed,
      bool hasFiniteRange,
      float rangeMin,
      float rangeMax,
      bool rangeEnabledRequired,
      int sign,
      int deploySign,
      int deployPcaChannel,
      string deployDrive )
    {
      if ( string.IsNullOrEmpty( name ) )
        throw new ArgumentException( "Actuator name must not be empty.", nameof( name ) );
      if ( string.IsNullOrEmpty( jointType ) )
        throw new ArgumentException( "Actuator joint_type must not be empty.", nameof( jointType ) );
      if ( string.IsNullOrEmpty( deployDrive ) )
        throw new ArgumentException( "Actuator deploy_drive must not be empty.", nameof( deployDrive ) );

      Name = name;
      ActionIndex = actionIndex;
      JointType = jointType;
      MaxSpeed = maxSpeed;
      HasFiniteRange = hasFiniteRange;
      RangeMin = rangeMin;
      RangeMax = rangeMax;
      RangeEnabledRequired = rangeEnabledRequired;
      Sign = sign;
      DeploySign = deploySign;
      DeployPcaChannel = deployPcaChannel;
      DeployDrive = deployDrive;
    }

    public string Name { get; }
    public int ActionIndex { get; }
    public string JointType { get; }
    public float MaxSpeed { get; }
    public bool HasFiniteRange { get; }
    public float RangeMin { get; }
    public float RangeMax { get; }
    public bool RangeEnabledRequired { get; }
    public int Sign { get; }
    public int DeploySign { get; }
    public int DeployPcaChannel { get; }
    public string DeployDrive { get; }
  }

  public sealed class MachineObservationSchema
  {
    public MachineObservationSchema(
      string id,
      int totalDim,
      int actionDim,
      int waypointLookahead,
      int numTaskModes,
      MachineObservationNormalizers normalizers )
    {
      if ( string.IsNullOrEmpty( id ) )
        throw new ArgumentException( "Observation schema id must not be empty.", nameof( id ) );

      Id = id;
      TotalDim = totalDim;
      ActionDim = actionDim;
      WaypointLookahead = waypointLookahead;
      NumTaskModes = numTaskModes;
      Normalizers = normalizers ?? throw new ArgumentNullException( nameof( normalizers ) );
    }

    public string Id { get; }
    public int TotalDim { get; }
    public int ActionDim { get; }
    public int WaypointLookahead { get; }
    public int NumTaskModes { get; }
    public MachineObservationNormalizers Normalizers { get; }
  }

  public sealed class MachineObservationNormalizers
  {
    public MachineObservationNormalizers(
      float positionNormalizer,
      float tipVelocityScale,
      float distanceNormalizer,
      float tubeRadius,
      float targetThreshold,
      float pitchNormDeg )
    {
      PositionNormalizer = positionNormalizer;
      TipVelocityScale = tipVelocityScale;
      DistanceNormalizer = distanceNormalizer;
      TubeRadius = tubeRadius;
      TargetThreshold = targetThreshold;
      PitchNormDeg = pitchNormDeg;
    }

    public float PositionNormalizer { get; }
    public float TipVelocityScale { get; }
    public float DistanceNormalizer { get; }
    public float TubeRadius { get; }
    public float TargetThreshold { get; }
    public float PitchNormDeg { get; }
  }
}
