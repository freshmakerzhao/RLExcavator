using UnityEngine;

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
        var block = new MaterialPropertyBlock();
        renderer.GetPropertyBlock( block );
        block.SetColor( "_BaseColor", color );
        block.SetColor( "_Color", color );
        renderer.SetPropertyBlock( block );
      }
    }
  }
}
