using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class ExcavatorRuntimeManualActionTests
  {
    [Test]
    public void ManualStickHasNeutralZoneAndDirectionalSpeedScale()
    {
      MachineProfile profile = MachineProfileLoader.LoadFromPath(
        Path.GetFullPath( Path.Combine( Application.dataPath, "../../shared/machine_profile.json" ) ) );
      MachineActuatorProfile boom = profile.GetActuator( "boom" );
      Type driver = Type.GetType( "RLExcavator.ExcavatorRuntimeActuators, Assembly-CSharp" );
      Assert.That( driver, Is.Not.Null );
      MethodInfo map = driver.GetMethod( "ManualSpeed", BindingFlags.Static | BindingFlags.NonPublic );
      Assert.That( map, Is.Not.Null );

      float Speed( float action ) => (float)map.Invoke( null, new object[] { action, boom } );
      Assert.That( Speed( 0.15f ), Is.Zero );
      Assert.That( Speed( -0.15f ), Is.Zero );
      Assert.That( Speed( 0.575f ), Is.EqualTo( boom.MaxSpeedPositive * 0.5f ).Within( 1e-6f ) );
      Assert.That( Speed( -1f ), Is.EqualTo( -boom.MaxSpeedNegative ).Within( 1e-6f ) );
    }
  }
}
