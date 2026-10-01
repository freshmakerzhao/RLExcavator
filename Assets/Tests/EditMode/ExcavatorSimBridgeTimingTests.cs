using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class ExcavatorSimBridgeTimingTests
  {
    [Test]
    public void PythonRoundTripTimeIsAcceptedButFutureAndExpiredCommandsAreRejected()
    {
      var gameObject = new GameObject( "bridge-time-test" );
      gameObject.SetActive( false );
      try {
        Type bridgeType = Type.GetType( "RLExcavator.ExcavatorSimBridge, Assembly-CSharp" );
        Component bridge = gameObject.AddComponent( bridgeType );
        BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        Type requestType = bridgeType.GetNestedType( "Request", BindingFlags.NonPublic );
        // Recorded at AGX step 47; Python's JSON spelling rounds upward in JsonUtility.
        double now = 0.93999962881207466;
        bridgeType.GetField( "m_epoch", fields ).SetValue( bridge, 1 );
        bridgeType.GetField( "m_simTime", fields ).SetValue( bridge, now );
        object request = JsonUtility.FromJson(
          "{\"kind\":\"manual_action\",\"epoch\":1,\"sequence\":1," +
          "\"issued_sim_time_s\":0.9399996288120747,\"expires_sim_time_s\":1.1899996288120747," +
          "\"axis_order\":[\"boom\",\"stick\",\"bucket\",\"swing\"],\"values\":[0,0,0,0]}", requestType );
        MethodInfo validate = bridgeType.GetMethod( "ValidateCommand", fields );
        Assert.That( validate.Invoke( bridge, new[] { request } ), Is.Null );

        requestType.GetField( "issued_sim_time_s" ).SetValue( request, now + 0.001 );
        Assert.That( validate.Invoke( bridge, new[] { request } ), Is.EqualTo( "expired_or_future_command" ) );
        requestType.GetField( "issued_sim_time_s" ).SetValue( request, now );
        requestType.GetField( "expires_sim_time_s" ).SetValue( request, now );
        Assert.That( validate.Invoke( bridge, new[] { request } ), Is.EqualTo( "expired_or_future_command" ) );
      }
      finally {
        UnityEngine.Object.DestroyImmediate( gameObject );
      }
    }
  }
}
