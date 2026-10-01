using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class ExcavatorRuntimeManualActionTests
  {
    [TestCase( "boom", 1f )]
    [TestCase( "stick", 1f )]
    [TestCase( "bucket", -1f )]
    [TestCase( "swing", 1f )]
    public void ManualStickHasNeutralZoneAndDirectionalSpeedScale( string axis, float direction )
    {
      MachineProfile profile = MachineProfileLoader.LoadFromPath(
        Path.GetFullPath( Path.Combine( Application.dataPath, "../../shared/machine_profile.json" ) ) );
      MachineActuatorProfile limit = profile.GetActuator( axis );
      Type driver = Type.GetType( "RLExcavator.ExcavatorRuntimeActuators, Assembly-CSharp" );
      Assert.That( driver, Is.Not.Null );
      MethodInfo map = driver.GetMethod( "ManualSpeed", BindingFlags.Static | BindingFlags.NonPublic );
      Assert.That( map, Is.Not.Null );

      float Speed( float action ) => (float)map.Invoke( null, new object[] { action, limit } );
      Assert.That( Speed( 0.15f ), Is.Zero );
      Assert.That( Speed( -0.15f ), Is.Zero );
      float positive = direction > 0f ? limit.MaxSpeedPositive : -limit.MaxSpeedNegative;
      float negative = direction > 0f ? -limit.MaxSpeedNegative : limit.MaxSpeedPositive;
      Assert.That( Speed( 0.575f ), Is.EqualTo( positive * 0.5f ).Within( 1e-6f ) );
      Assert.That( Speed( 1f ), Is.EqualTo( positive ).Within( 1e-6f ) );
      Assert.That( Speed( -1f ), Is.EqualTo( negative ).Within( 1e-6f ) );
    }
  }
}
