using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class ExcavatorWallContactsTests
  {
    [Test]
    public void MissingConfigurationIsUnavailableRatherThanAConfirmedClearScene()
    {
      var host = new GameObject( "wall-contact-test" );
      host.SetActive( false );
      try {
        Type monitorType = Type.GetType( "RLExcavator.ExcavatorWallContacts, Assembly-CSharp" );
        Assert.That( monitorType, Is.Not.Null );
        Component monitor = host.AddComponent( monitorType );
        object state = monitorType.GetMethod( "ReadState" ).Invoke( monitor, null );
        Assert.That( Field<bool>( state, "available" ), Is.False );
        Assert.That( Field<bool>( state, "detected" ), Is.False );
        Assert.That( Field<int>( state, "contact_steps" ), Is.Zero );
      }
      finally { UnityEngine.Object.DestroyImmediate( host ); }
    }

    [Test]
    public void OnlyEnabledExcavatorToConfiguredWallContactsAreRecordedInEitherOrder()
    {
      using ( var scene = new ContactScene() ) {
        scene.Call( "BeginEpisode" );
        scene.Call( "BeginStep" );
        scene.Contact( scene.Machine, scene.Other ); // Floor or an unconfigured wall.
        scene.Contact( scene.Other, scene.Walls[0] ); // A soil particle or other machine.
        scene.Contact( null, scene.Walls[0] ); // Native particles may have no Unity component.
        scene.Contact( scene.Walls[0], scene.Walls[1] );
        scene.Contact( scene.Machine, scene.Walls[0], enabled: false );
        scene.Contact( scene.Machine, scene.Walls[0], pointEnabled: false );
        Assert.That( Field<bool>( scene.State, "detected" ), Is.False );

        scene.Frame.position = new Vector3( 10, 20, 30 );
        Vector3 worldPoint = scene.Frame.TransformPoint( new Vector3( 1, 2, 3 ) );
        Assert.That( scene.Contact( scene.Machine, scene.Walls[0], worldPoint, pointCount: 3 ), Is.False );
        Assert.That( Field<bool>( scene.State, "detected" ), Is.True );
        Assert.That( Field<int>( scene.State, "contact_steps" ), Is.EqualTo( 1 ) );
        Assert.That( Field<Vector3>( scene.State, "point_ros_m" ), Is.EqualTo( new Vector3( 3, -1, 2 ) ) );
        Assert.That( scene.Contact( scene.Walls[1], scene.Machine, worldPoint ), Is.False );
        Assert.That( Field<string>( scene.State, "wall" ), Is.EqualTo( "Back" ) );
        Assert.That( Field<string>( scene.State, "machine_part" ), Is.EqualTo( "Body_Bucket/BucketShape" ) );
        Assert.That( Field<int>( scene.State, "contact_steps" ), Is.EqualTo( 1 ) );
      }
    }

    [Test]
    public void TransientContactSurvivesFiveStepsAndResetExcludesHomingContacts()
    {
      using ( var scene = new ContactScene() ) {
        scene.Call( "BeginEpisode" );
        scene.Call( "BeginStep" );
        scene.Contact( scene.Machine, scene.Walls[0] );
        scene.Call( "EndStep" );
        object firstSnapshot = scene.State;
        for ( int i = 0; i < 4; ++i ) {
          scene.Call( "BeginStep" );
          scene.Call( "EndStep" );
        }
        Assert.That( Field<bool>( scene.State, "detected" ), Is.True );
        Assert.That( Field<int>( scene.State, "contact_steps" ), Is.EqualTo( 1 ) );
        scene.Call( "BeginStep" );
        scene.Contact( scene.Machine, scene.Walls[1] );
        scene.Call( "EndStep" );
        Assert.That( Field<int>( scene.State, "contact_steps" ), Is.EqualTo( 2 ) );
        Assert.That( Field<int>( firstSnapshot, "contact_steps" ), Is.EqualTo( 1 ) );

        scene.Call( "BeginReset" );
        scene.Call( "BeginStep" );
        scene.Contact( scene.Machine, scene.Walls[0] );
        scene.Call( "EndStep" );
        Assert.That( Field<bool>( scene.State, "detected" ), Is.False );
        Assert.That( Field<int>( scene.State, "contact_steps" ), Is.Zero );
        scene.Call( "BeginEpisode" );
        Assert.That( Field<int>( scene.State, "contact_steps" ), Is.Zero );
        Assert.That( Field<string>( scene.State, "wall" ), Is.Null.Or.Empty );
        scene.Contact( scene.Machine, scene.Walls[0] ); // Outside a physics step.
        Assert.That( Field<bool>( scene.State, "detected" ), Is.False );
      }
    }

    [Test]
    public void BridgeReturnsAnExplicitUnavailableContactStateWhenMonitorIsMissing()
    {
      var host = new GameObject( "bridge-wall-contact-test" );
      host.SetActive( false );
      try {
        Type bridgeType = Type.GetType( "RLExcavator.ExcavatorSimBridge, Assembly-CSharp" );
        Component bridge = host.AddComponent( bridgeType );
        object reply = bridgeType.GetMethod( "ReadState", BindingFlags.Instance | BindingFlags.NonPublic )
          .Invoke( bridge, null );
        object contactState = Field<object>( reply, "wall_contact" );
        Assert.That( contactState, Is.Not.Null );
        Assert.That( Field<bool>( contactState, "available" ), Is.False );
      }
      finally { UnityEngine.Object.DestroyImmediate( host ); }
    }

    [Test]
    public void DisablingTheListenerPreservesEpisodeTotalsForResumedSampling()
    {
      using ( var scene = new ContactScene() ) {
        scene.Call( "BeginEpisode" );
        scene.Call( "BeginStep" );
        scene.Contact( scene.Machine, scene.Walls[0] );
        scene.Call( "OnDisable" );
        scene.Contact( scene.Machine, scene.Walls[0] );
        Assert.That( Field<bool>( scene.State, "available" ), Is.False );
        Assert.That( Field<int>( scene.State, "contact_steps" ), Is.EqualTo( 1 ) );
        scene.Call( "BeginStep" );
        scene.Contact( scene.Machine, scene.Walls[1] );
        scene.Call( "EndStep" );
        Assert.That( Field<int>( scene.State, "contact_steps" ), Is.EqualTo( 2 ) );
      }
    }

    private sealed class ContactScene : IDisposable
    {
      private readonly GameObject m_host = new GameObject( "wall-contact-test" );
      private readonly Component m_monitor;
      private readonly Type m_monitorType;
      public readonly Transform Frame;
      public readonly Component Machine;
      public readonly Component Other;
      public readonly Component[] Walls;

      public ContactScene()
      {
        m_host.SetActive( false );
        m_monitorType = Type.GetType( "RLExcavator.ExcavatorWallContacts, Assembly-CSharp" );
        Assert.That( m_monitorType, Is.Not.Null );
        m_monitor = m_host.AddComponent( m_monitorType );
        Frame = Child( "MachineRoot" );
        Transform geometry = Child( "MachineGeometry" );
        Machine = Shape( "BucketShape", Child( "Body_Bucket", geometry ) );
        Other = Shape( "FloorOrParticle", m_host.transform );
        Walls = new[] { Shape( "Front" ), Shape( "Back" ), Shape( "Left" ), Shape( "Right" ) };
        m_monitorType.GetField( "machineRoot" ).SetValue( m_monitor, Frame );
        m_monitorType.GetField( "machineGeometryRoot" ).SetValue( m_monitor, geometry );
        Type shapeType = Type.GetType( "AGXUnity.Collide.Shape, AGXUnity" );
        Array walls = Array.CreateInstance( shapeType, Walls.Length );
        for ( int i = 0; i < Walls.Length; ++i ) walls.SetValue( Walls[i], i );
        m_monitorType.GetField( "walls" ).SetValue( m_monitor, walls );
      }

      public object State => m_monitorType.GetMethod( "ReadState" ).Invoke( m_monitor, null );
      public void Call( string method )
      {
        m_monitorType.GetMethod( method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic )
          .Invoke( m_monitor, null );
      }

      public bool Contact( Component first, Component second, Vector3? point = null,
                           bool enabled = true, bool pointEnabled = true, int pointCount = 1 )
      {
        Type pointType = Type.GetType( "AGXUnity.ContactPointData, AGXUnity" );
        object contactPoint = Activator.CreateInstance( pointType );
        pointType.GetField( "Enabled" ).SetValue( contactPoint, pointEnabled );
        pointType.GetField( "Position" ).SetValue( contactPoint, point ?? Vector3.zero );
        Array points = Array.CreateInstance( pointType, pointCount );
        for ( int i = 0; i < pointCount; ++i ) points.SetValue( contactPoint, i );
        Type contactType = Type.GetType( "AGXUnity.ContactData, AGXUnity" );
        object contact = Activator.CreateInstance( contactType );
        contactType.GetField( "Component1" ).SetValue( contact, first );
        contactType.GetField( "Component2" ).SetValue( contact, second );
        contactType.GetField( "Enabled" ).SetValue( contact, enabled );
        FieldInfo segment = contactType.GetField( "Points" );
        segment.SetValue( contact, Activator.CreateInstance( segment.FieldType, points, 0, pointCount ) );
        return (bool)m_monitorType.GetMethod( "OnContact", BindingFlags.Instance | BindingFlags.NonPublic )
          .Invoke( m_monitor, new[] { contact } );
      }

      private Transform Child( string name, Transform parent = null )
      {
        var child = new GameObject( name );
        child.SetActive( false );
        child.transform.SetParent( parent ?? m_host.transform, false );
        return child.transform;
      }

      private Component Shape( string name, Transform parent = null )
      {
        return Child( name, parent ).gameObject.AddComponent( Type.GetType( "AGXUnity.Collide.Box, AGXUnity" ) );
      }

      public void Dispose() { UnityEngine.Object.DestroyImmediate( m_host ); }
    }

    private static T Field<T>( object value, string name )
    {
      return (T)value.GetType().GetField( name ).GetValue( value );
    }
  }
}
