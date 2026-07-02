using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace RLExcavator
{
  public static class ReachableWorkspaceProfileLoader
  {
    public static ReachableWorkspaceProfile LoadDefault()
    {
      return LoadFromPath( DefaultWorkspacePath() );
    }

    public static string DefaultWorkspacePath()
    {
      string machineProfilePath = MachineProfileLoader.DefaultProfilePath();
      MachineProfile machineProfile = MachineProfileLoader.LoadFromPath( machineProfilePath );
      return ResolveWorkspacePath( machineProfilePath, machineProfile.ReachableWorkspaceFile );
    }

    public static string ResolveWorkspacePath( string machineProfilePath, string reachableWorkspaceFile )
    {
      if ( string.IsNullOrEmpty( machineProfilePath ) )
        throw new ArgumentException( "Machine profile path must not be empty.", nameof( machineProfilePath ) );
      if ( string.IsNullOrEmpty( reachableWorkspaceFile ) )
        throw new ArgumentException( "Reachable workspace file must not be empty.", nameof( reachableWorkspaceFile ) );

      if ( Path.IsPathRooted( reachableWorkspaceFile ) )
        return Path.GetFullPath( reachableWorkspaceFile );

      string profileDirectory = Path.GetDirectoryName( Path.GetFullPath( machineProfilePath ) );
      if ( string.IsNullOrEmpty( profileDirectory ) )
        throw new InvalidOperationException( "Cannot resolve machine profile directory: " + machineProfilePath );

      return Path.GetFullPath( Path.Combine( profileDirectory, reachableWorkspaceFile ) );
    }

    public static ReachableWorkspaceProfile LoadFromPath( string path )
    {
      if ( string.IsNullOrEmpty( path ) )
        throw new ArgumentException( "Reachable workspace path must not be empty.", nameof( path ) );
      if ( !File.Exists( path ) )
        throw new FileNotFoundException( "Reachable workspace profile not found.", path );

      return LoadFromJson( File.ReadAllText( path ) );
    }

    public static ReachableWorkspaceProfile LoadFromJson( string json )
    {
      if ( string.IsNullOrEmpty( json ) )
        throw new ArgumentException( "Reachable workspace JSON must not be empty.", nameof( json ) );

      ReachableWorkspaceProfileDto dto = JsonUtility.FromJson<ReachableWorkspaceProfileDto>( json );
      if ( dto == null )
        throw new FormatException( "Invalid reachable workspace JSON." );
      if ( dto.workspaces == null || dto.workspaces.Length == 0 )
        throw new FormatException( "Reachable workspace JSON must contain workspaces." );

      var workspaces = new List<ReachableWorkspaceModeProfile>();
      for ( int i = 0; i < dto.workspaces.Length; i++ ) {
        WorkspaceModeDto workspace = dto.workspaces[ i ];
        if ( workspace == null )
          throw new FormatException( "Reachable workspace JSON contains a null workspace entry." );

        workspaces.Add( new ReachableWorkspaceModeProfile(
          workspace.mode,
          workspace.volume_anchor_points ) );
      }

      return new ReachableWorkspaceProfile(
        dto.schema_version,
        dto.machine_id,
        dto.coordinate_frame,
        workspaces.ToArray() );
    }

    public static void SaveModeToDefaultProfile(
      string mode,
      Vector3[] volumeAnchorPoints )
    {
      SaveModeToPath( DefaultWorkspacePath(), mode, volumeAnchorPoints );
    }

    public static void SaveModeToPath(
      string path,
      string mode,
      Vector3[] volumeAnchorPoints )
    {
      if ( string.IsNullOrEmpty( path ) )
        throw new ArgumentException( "Reachable workspace path must not be empty.", nameof( path ) );
      if ( string.IsNullOrEmpty( mode ) )
        throw new ArgumentException( "Reachable workspace mode must not be empty.", nameof( mode ) );
      if ( volumeAnchorPoints == null || volumeAnchorPoints.Length == 0 )
        throw new ArgumentException( "Reachable workspace mode must contain volume anchors.", nameof( volumeAnchorPoints ) );

      ReachableWorkspaceProfileDto dto;
      if ( File.Exists( path ) )
        dto = JsonUtility.FromJson<ReachableWorkspaceProfileDto>( File.ReadAllText( path ) );
      else
        dto = NewDefaultDto();

      if ( dto == null )
        dto = NewDefaultDto();
      if ( dto.workspaces == null )
        dto.workspaces = new WorkspaceModeDto[ 0 ];

      WorkspaceModeDto[] workspaces = dto.workspaces;
      int index = IndexOfMode( workspaces, mode );
      WorkspaceModeDto replacement = new WorkspaceModeDto {
        mode = mode,
        volume_anchor_points = (Vector3[])volumeAnchorPoints.Clone()
      };

      if ( index >= 0 )
        workspaces[ index ] = replacement;
      else {
        var expanded = new WorkspaceModeDto[ workspaces.Length + 1 ];
        Array.Copy( workspaces, expanded, workspaces.Length );
        expanded[ expanded.Length - 1 ] = replacement;
        dto.workspaces = expanded;
      }

      string directory = Path.GetDirectoryName( Path.GetFullPath( path ) );
      if ( !string.IsNullOrEmpty( directory ) )
        Directory.CreateDirectory( directory );

      File.WriteAllText( path, JsonUtility.ToJson( dto, true ) + Environment.NewLine );
    }

    private static int IndexOfMode( WorkspaceModeDto[] workspaces, string mode )
    {
      for ( int i = 0; i < workspaces.Length; i++ ) {
        if ( workspaces[ i ] != null && workspaces[ i ].mode == mode )
          return i;
      }

      return -1;
    }

    private static ReachableWorkspaceProfileDto NewDefaultDto()
    {
      MachineProfile machineProfile = MachineProfileLoader.Load();
      return new ReachableWorkspaceProfileDto {
        schema_version = "0.1.0",
        machine_id = machineProfile.MachineId,
        coordinate_frame = "machine_root",
        workspaces = new WorkspaceModeDto[ 0 ]
      };
    }

    [Serializable]
    private sealed class ReachableWorkspaceProfileDto
    {
      public string schema_version;
      public string machine_id;
      public string coordinate_frame;
      public WorkspaceModeDto[] workspaces;
    }

    [Serializable]
    private sealed class WorkspaceModeDto
    {
      public string mode;
      public Vector3[] volume_anchor_points;
    }
  }
}
