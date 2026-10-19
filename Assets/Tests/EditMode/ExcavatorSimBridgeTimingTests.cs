using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class ExcavatorSimBridgeTimingTests
  {
    [Test]
    public void StatePublishesTheAuthoredOriginIdentity()
    {
      var go = new GameObject("origin-identity-test");
      go.SetActive(false);
      try {
        Type bridgeType = Type.GetType("RLExcavator.ExcavatorSimBridge, Assembly-CSharp");
        Component bridge = go.AddComponent(bridgeType);
        bridgeType.GetField("machineFrameId").SetValue(bridge, "swing_axis_chassis_v1");
        Type replyType = bridgeType.GetNestedType("Reply", BindingFlags.NonPublic);
        object reply = Activator.CreateInstance(replyType, true);
        var populate = bridgeType.GetMethod("PopulateState", BindingFlags.NonPublic | BindingFlags.Instance)
          .MakeGenericMethod(replyType);
        object result = populate.Invoke(bridge, new[] { reply });
        // 即使尚未接好执行器，状态也必须说明原点身份，不能用同名 ROS frame 混淆新旧记录。
        StringAssert.Contains("\"machine_frame_id\":\"swing_axis_chassis_v1\"", JsonUtility.ToJson(result));
      }
      finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    [TestCase("{\"enabled\":true}", true, true)]
    [TestCase("{\"enabled\":false}", true, false)]
    [TestCase("{}", false, false)]
    [TestCase("{\"enabled\":null}", false, false)]
    [TestCase("{\"enabled\":1}", false, false)]
    [TestCase("{\"enabled\":0}", false, false)]
    [TestCase("{\"enabled\":\"true\"}", false, false)]
    [TestCase("{\"enabled\":\"false\"}", false, false)]
    [TestCase("{\"enabled\":{\"enabled\":true}}", false, false)]
    [TestCase("{\"enabled\":true,\"enabled\":false}", false, false)]
    [TestCase("{", false, false)]
    public void LidarConfigurationRequiresAnExplicitBooleanToken(string json, bool valid, bool enabled)
    {
      Type bridgeType = Type.GetType("RLExcavator.ExcavatorSimBridge, Assembly-CSharp");
      var parse = bridgeType.GetMethod("TryReadLidarEnabled", BindingFlags.NonPublic | BindingFlags.Static);
      Assert.That(parse, Is.Not.Null, "不能用 JsonUtility 把缺失/数字/字符串宽松转换为雷达开关");
      var args = new object[] { json, false };
      Assert.That(parse.Invoke(null, args), Is.EqualTo(valid));
      Assert.That(args[1], Is.EqualTo(enabled));
    }

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
