using UnityEngine;
using UnityEngine.Rendering;

namespace RLExcavator
{
  /// <summary>
  /// Runtime-visible marker for trajectory target points.
  /// Gizmos are editor-only; this creates a small mesh marker visible in Game view.
  /// </summary>
  public class TargetPointVisual : MonoBehaviour
  {
    public Color color = new Color( 1f, 0.85f, 0.05f, 1f );
    public float radius = 0.18f;
    public bool faceCamera = false;

    private Transform m_visual;
    private Material m_material;

    private void Awake()
    {
      CacheExistingVisual();
    }

    private void Start()
    {
      EnsureVisual();
    }

    private void OnValidate()
    {
      CacheExistingVisual();
      UpdateVisualProperties();
    }

    private void LateUpdate()
    {
      if ( m_visual == null && Application.isPlaying )
        EnsureVisual();

      if ( m_visual != null && faceCamera && Camera.main != null )
        m_visual.rotation = Camera.main.transform.rotation;
    }

    public void SetStyle( Color newColor, float newRadius )
    {
      color = newColor;
      radius = newRadius;

      if ( Application.isPlaying )
        EnsureVisual();
      else
        UpdateVisualProperties();
    }

    public void SetVisible( bool visible )
    {
      if ( visible && Application.isPlaying )
        EnsureVisual();
      else
        CacheExistingVisual();

      if ( m_visual != null )
        m_visual.gameObject.SetActive( visible );
    }

    private void CacheExistingVisual()
    {
      if ( m_visual == null ) {
        var existing = transform.Find( "TargetPointVisual" );
        if ( existing != null )
          m_visual = existing;
      }
    }

    private void EnsureVisual()
    {
      CacheExistingVisual();

      if ( m_visual == null ) {
        var visualObject = GameObject.CreatePrimitive( PrimitiveType.Sphere );
        visualObject.name = "TargetPointVisual";
        visualObject.transform.SetParent( transform, false );
        visualObject.transform.localPosition = Vector3.zero;
        m_visual = visualObject.transform;

        var collider = visualObject.GetComponent<Collider>();
        if ( collider != null )
          Destroy( collider );
      }

      UpdateVisualProperties();
    }

    private void UpdateVisualProperties()
    {
      if ( m_visual == null )
        return;

      m_visual.localScale = Vector3.one * Mathf.Max( radius * 2f, 0.01f );

      var renderer = m_visual.GetComponent<Renderer>();
      if ( renderer != null ) {
        EnsureMaterial( renderer );
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        var block = new MaterialPropertyBlock();
        renderer.GetPropertyBlock( block );
        block.SetColor( "_BaseColor", color );
        block.SetColor( "_Color", color );
        renderer.SetPropertyBlock( block );
      }
    }

    private void EnsureMaterial( Renderer renderer )
    {
      if ( m_material == null ) {
        Shader shader = Shader.Find( "Universal Render Pipeline/Unlit" );
        if ( shader == null )
          shader = Shader.Find( "Universal Render Pipeline/Lit" );
        if ( shader == null )
          shader = Shader.Find( "Unlit/Color" );
        if ( shader == null )
          shader = Shader.Find( "Standard" );

        if ( shader != null ) {
          m_material = new Material( shader ) {
            name = "TargetPointVisualMaterial"
          };
        }
      }

      if ( m_material == null )
        return;

      if ( m_material.HasProperty( "_BaseColor" ) )
        m_material.SetColor( "_BaseColor", color );
      if ( m_material.HasProperty( "_Color" ) )
        m_material.SetColor( "_Color", color );

      renderer.sharedMaterial = m_material;
    }
  }
}
