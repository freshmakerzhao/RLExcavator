using System;
using System.Collections.Generic;
using UnityEngine;

namespace RLExcavator
{
  public sealed class ReachableWorkspaceProfile
  {
    private readonly ReachableWorkspaceModeProfile[] m_workspaces;
    private readonly Dictionary<string, ReachableWorkspaceModeProfile> m_workspacesByMode;

    public ReachableWorkspaceProfile(
      string schemaVersion,
      string machineId,
      string coordinateFrame,
      ReachableWorkspaceModeProfile[] workspaces )
    {
      SchemaVersion = RequireText( schemaVersion, nameof( schemaVersion ) );
      MachineId = RequireText( machineId, nameof( machineId ) );
      CoordinateFrame = RequireText( coordinateFrame, nameof( coordinateFrame ) );

      if ( workspaces == null || workspaces.Length == 0 )
        throw new ArgumentException( "Reachable workspace profile must contain at least one mode.", nameof( workspaces ) );

      m_workspaces = (ReachableWorkspaceModeProfile[])workspaces.Clone();
      m_workspacesByMode = new Dictionary<string, ReachableWorkspaceModeProfile>( StringComparer.Ordinal );

      for ( int i = 0; i < m_workspaces.Length; i++ ) {
        ReachableWorkspaceModeProfile workspace = m_workspaces[ i ];
        if ( workspace == null )
          throw new ArgumentException( "Reachable workspace profile contains a null mode.", nameof( workspaces ) );
        if ( m_workspacesByMode.ContainsKey( workspace.Mode ) )
          throw new ArgumentException( "Duplicate reachable workspace mode: " + workspace.Mode, nameof( workspaces ) );

        m_workspacesByMode.Add( workspace.Mode, workspace );
      }
    }

    public string SchemaVersion { get; }
    public string MachineId { get; }
    public string CoordinateFrame { get; }

    public ReachableWorkspaceModeProfile[] Workspaces
    {
      get { return (ReachableWorkspaceModeProfile[])m_workspaces.Clone(); }
    }

    public ReachableWorkspaceModeProfile GetWorkspace( string mode )
    {
      ReachableWorkspaceModeProfile workspace;
      if ( TryGetWorkspace( mode, out workspace ) )
        return workspace;

      throw new KeyNotFoundException( "Reachable workspace profile does not contain mode: " + mode );
    }

    public bool TryGetWorkspace( string mode, out ReachableWorkspaceModeProfile workspace )
    {
      workspace = null;
      if ( string.IsNullOrEmpty( mode ) )
        return false;

      return m_workspacesByMode.TryGetValue( mode, out workspace );
    }

    private static string RequireText( string value, string fieldName )
    {
      if ( string.IsNullOrEmpty( value ) )
        throw new ArgumentException( "Reachable workspace profile field must not be empty: " + fieldName, fieldName );

      return value;
    }
  }

  public sealed class ReachableWorkspaceModeProfile
  {
    private readonly Vector3[] m_volumeAnchorPoints;

    public ReachableWorkspaceModeProfile(
      string mode,
      Vector3[] volumeAnchorPoints )
    {
      if ( string.IsNullOrEmpty( mode ) )
        throw new ArgumentException( "Reachable workspace mode must not be empty.", nameof( mode ) );
      if ( volumeAnchorPoints == null || volumeAnchorPoints.Length == 0 )
        throw new ArgumentException( "Reachable workspace mode must contain volume anchors.", nameof( volumeAnchorPoints ) );

      Mode = mode;
      m_volumeAnchorPoints = (Vector3[])volumeAnchorPoints.Clone();
    }

    public string Mode { get; }

    public Vector3[] VolumeAnchorPoints
    {
      get { return (Vector3[])m_volumeAnchorPoints.Clone(); }
    }
  }
}
