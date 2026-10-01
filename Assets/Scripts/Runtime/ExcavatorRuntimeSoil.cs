using System;
using System.Collections;
using AGXUnity.Model;
using AGXUnity.Rendering;
using UnityEngine;

namespace RLExcavator
{
  /// <summary>Recreate the soil after homing, including deformation and loose particles.</summary>
  [DefaultExecutionOrder(-1000)]
  public sealed class ExcavatorRuntimeSoil : MonoBehaviour
  {
    public MovableTerrain terrainTemplate;
    public MovableTerrain ActiveTerrain { get; private set; }
    public bool IsResetting { get; private set; }
    public string ResetError { get; private set; }

    private void Awake()
    {
      // Keep the authored terrain for the next reset; only its runtime copy is simulated.
      if ( terrainTemplate != null ) terrainTemplate.gameObject.SetActive(false);
    }

    private void Start() { BeginReset(); }

    public void BeginReset()
    {
      if ( IsResetting ) throw new InvalidOperationException("Soil reset is already running.");
      StartCoroutine(Rebuild());
    }

    private IEnumerator Rebuild()
    {
      IsResetting = true;
      ResetError = null;
      if ( ActiveTerrain != null ) {
        var mesh = ActiveTerrain.TerrainMesh.sharedMesh;
        ActiveTerrain.gameObject.SetActive(false);
        Destroy(ActiveTerrain.gameObject);
        Destroy(mesh);
        // AGX OnDestroy removes the native terrain and its particles before replacement.
        yield return null;
      }
      try {
        if ( terrainTemplate == null ) throw new InvalidOperationException("Soil template is missing.");
        ActiveTerrain = Instantiate(terrainTemplate, terrainTemplate.transform.parent);
        ActiveTerrain.name = "SoilTerrain (Runtime)";
        // MovableTerrain writes its mesh during initialization and deformation.
        ActiveTerrain.TerrainMesh.mesh = Instantiate(terrainTemplate.TerrainMesh.sharedMesh);
        ActiveTerrain.enabled = true;
        var particles = ActiveTerrain.GetComponent<DeformableTerrainParticleRenderer>();
        particles.enabled = true;
        ActiveTerrain.gameObject.SetActive(true);
        if ( ActiveTerrain.GetInitialized<MovableTerrain>() == null ||
             particles.GetInitialized<DeformableTerrainParticleRenderer>() == null )
          throw new InvalidOperationException("Soil initialization failed.");
      }
      catch ( Exception error ) {
        ResetError = error.Message;
        Debug.LogError("[ExcavatorRuntimeSoil] " + ResetError, this);
      }
      finally { IsResetting = false; }
    }

    private void OnDestroy()
    {
      if ( ActiveTerrain != null ) {
        Destroy(ActiveTerrain.TerrainMesh.sharedMesh);
        Destroy(ActiveTerrain.gameObject);
      }
    }
  }
}
