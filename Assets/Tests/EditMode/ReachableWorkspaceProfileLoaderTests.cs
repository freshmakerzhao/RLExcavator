using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class ReachableWorkspaceProfileLoaderTests
  {
    [Test]
    public void LoadSharedWorkspaceProfileReturnsBothTaskModes()
    {
      ReachableWorkspaceProfile profile = ReachableWorkspaceProfileLoader.LoadFromPath( SharedWorkspacePath() );

      Assert.That( profile.MachineId, Is.EqualTo( "scale_excavator_v1" ) );
      Assert.That( profile.CoordinateFrame, Is.EqualTo( "machine_root" ) );
      AssertWorkspace( profile.GetWorkspace( "MoveToDig" ) );
      AssertWorkspace( profile.GetWorkspace( "CarryMaterial" ) );
    }

    [Test]
    public void DefaultWorkspacePathUsesMachineProfileReference()
    {
      MachineProfile machineProfile = MachineProfileLoader.LoadFromPath( SharedProfilePath() );
      string resolvedPath = ReachableWorkspaceProfileLoader.ResolveWorkspacePath(
        SharedProfilePath(),
        machineProfile.ReachableWorkspaceFile );

      Assert.That( Path.GetFullPath( resolvedPath ), Is.EqualTo( SharedWorkspacePath() ) );
    }

    private static void AssertWorkspace( ReachableWorkspaceModeProfile workspace )
    {
      Assert.That( workspace.VolumeAnchorPoints, Has.Length.EqualTo( 20 ) );
    }

    private static string SharedProfilePath()
    {
      return Path.GetFullPath( Path.Combine( Application.dataPath, "../../shared/machine_profile.json" ) );
    }

    private static string SharedWorkspacePath()
    {
      return Path.GetFullPath( Path.Combine( Application.dataPath, "../../shared/reachable_workspaces/scale_excavator_workspace.json" ) );
    }
  }
}
