using System;
using AGXUnity;
using AGXUnity.Collide;
using UnityEngine;

namespace RLExcavator
{
  /// <summary>Read-only AGX wall contacts accumulated since the latest completed reset.</summary>
  public sealed class ExcavatorWallContacts : MonoBehaviour
  {
    [Serializable]
    public sealed class State
    {
      public bool available;
      public bool detected;
      public int contact_steps;
      public string wall;
      public string machine_part;
      public Vector3 point_ros_m;
    }

    [Tooltip("Fixed machine frame used to report contact points in ROS coordinates.")]
    public Transform machineRoot;
    [Tooltip("Root containing only the excavator's AGX collision shapes.")]
    public Transform machineGeometryRoot;
    [Tooltip("The four soil-container wall shapes; exclude the floor and soil.")]
    public Shape[] walls = new Shape[4];

    private Simulation m_simulation;
    private State m_state = new State();
    private bool m_started;
    private bool m_collecting;
    private bool m_inStep;
    private bool m_contactThisStep;

    private void Start()
    {
      m_started = true;
      Subscribe();
    }

    private void OnEnable()
    {
      if ( m_started ) Subscribe();
    }

    private void Subscribe()
    {
      if ( m_simulation != null || !HasConfiguration() ) return;
      Simulation simulation = Simulation.Instance;
      if ( simulation == null || machineGeometryRoot.GetComponentsInChildren<Shape>().Length == 0 ) return;
      // Native UUIDs must exist before AGX initializes its callback filters.
      foreach ( Shape wall in walls )
        if ( wall.GetInitialized<Shape>() == null || !wall.CollisionsEnabled || wall.IsSensor ) return;
      foreach ( Shape wall in walls ) simulation.ContactCallbacks.OnContact( OnContact, wall );
      m_simulation = simulation;
    }

    private bool HasConfiguration()
    {
      if ( machineRoot == null || machineGeometryRoot == null || walls == null || walls.Length != 4 )
        return false;
      for ( int i = 0; i < walls.Length; ++i ) {
        if ( walls[i] == null || walls[i].transform.IsChildOf( machineGeometryRoot ) ) return false;
        for ( int j = 0; j < i; ++j ) if ( walls[i] == walls[j] ) return false;
      }
      return true;
    }

    public void BeginReset()
    {
      m_collecting = false;
      m_inStep = false;
      m_contactThisStep = false;
      m_state = new State();
    }

    public void BeginEpisode()
    {
      BeginReset();
      m_collecting = true;
    }

    public void BeginStep()
    {
      m_inStep = m_collecting;
      m_contactThisStep = false;
    }

    public void EndStep() { m_inStep = false; }

    public State ReadState()
    {
      return new State {
        available = m_simulation != null && isActiveAndEnabled && HasConfiguration(),
        detected = m_state.detected,
        contact_steps = m_state.contact_steps,
        wall = m_state.wall,
        machine_part = m_state.machine_part,
        point_ros_m = m_state.point_ros_m
      };
    }

    private bool OnContact( ref ContactData contact )
    {
      if ( !m_inStep || !contact.Enabled || !HasConfiguration() ) return false;
      if ( !TryMatch( contact.Component1, contact.Component2, out Shape wall, out Shape machine ) &&
           !TryMatch( contact.Component2, contact.Component1, out wall, out machine ) ) return false;
      foreach ( ref ContactPointData point in contact.Points ) {
        if ( !point.Enabled || !float.IsFinite( point.Position.x ) ||
             !float.IsFinite( point.Position.y ) || !float.IsFinite( point.Position.z ) ) continue;
        Vector3 local = machineRoot.InverseTransformPoint( point.Position );
        m_state = new State {
          detected = true,
          contact_steps = m_state.contact_steps + ( m_contactThisStep ? 0 : 1 ),
          wall = wall.name,
          machine_part = MachinePartPath( machine.transform ),
          point_ros_m = new Vector3( local.z, -local.x, local.y )
        };
        m_contactThisStep = true;
      }
      return false; // Observation only: never synchronize modifications to AGX contacts.
    }

    private bool TryMatch( ScriptComponent first, ScriptComponent second, out Shape wall, out Shape machine )
    {
      wall = first as Shape;
      machine = second as Shape;
      return wall != null && machine != null && Array.IndexOf( walls, wall ) >= 0 &&
        machine.transform.IsChildOf( machineGeometryRoot );
    }

    private string MachinePartPath( Transform part )
    {
      string path = part.name;
      while ( part != machineGeometryRoot && part.parent != null && part.parent != machineGeometryRoot ) {
        part = part.parent;
        path = part.name + "/" + path;
      }
      return path;
    }

    private void OnDisable()
    {
      if ( m_simulation != null ) m_simulation.ContactCallbacks.Remove( OnContact );
      m_simulation = null;
      m_inStep = false;
      m_contactThisStep = false;
    }
  }
}
