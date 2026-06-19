using UnityEngine;
using UnityEngine.Rendering;

namespace RLExcavator
{
  /// <summary>
  /// Visualizes a calibrated bucket-tip reachable workspace.
  /// The default anchors are local positions measured in the env01 frame.
  /// </summary>
  [ExecuteAlways]
  public class ReachableWorkspaceVisualizer : MonoBehaviour
  {
    private const int DirectionCount = 5;
    private const int RadialCount = 3;
    private const int AnchorCount = DirectionCount * RadialCount;

    [Header("Reference frame")]
    [Tooltip("Frame that owns the calibrated local points. Leave empty when this component is attached to env01.")]
    public Transform workspaceFrame;

    [Tooltip("Bucket-tip anchor points in workspaceFrame local coordinates: 5 directions x 3 ranges.")]
    public Vector3[] localAnchorPoints = DefaultAnchors();

    [Header("Mesh")]
    public bool showMesh = true;
    public Color fillColor = new Color( 0.1f, 0.55f, 1.0f, 0.18f );

    [Header("Gizmos")]
    public bool drawGizmos = true;
    public bool drawInternalGrid = true;
    public bool drawAnchorSpheres = true;
    public Color boundaryColor = new Color( 0.0f, 0.85f, 1.0f, 1.0f );
    public Color internalGridColor = new Color( 0.2f, 0.95f, 0.65f, 0.8f );
    public Color anchorColor = new Color( 1.0f, 0.72f, 0.05f, 1.0f );
    public float anchorRadius = 0.12f;

    private const string VisualObjectName = "ReachableWorkspaceSectorVisual";
    private MeshFilter m_meshFilter;
    private MeshRenderer m_meshRenderer;
    private Mesh m_mesh;
    private Material m_material;
    private bool m_needsRebuild;

    private void OnEnable()
    {
      EnsureAnchorArray();
      RebuildVisual();
    }

    private void OnValidate()
    {
      EnsureAnchorArray();
      m_needsRebuild = true;
    }

    private void Update()
    {
      if ( !m_needsRebuild )
        return;

      m_needsRebuild = false;
      RebuildVisual();
    }

    private void OnDisable()
    {
      if ( m_meshRenderer != null )
        m_meshRenderer.enabled = false;
    }

    [ContextMenu("Rebuild Workspace Visual")]
    public void RebuildVisual()
    {
      EnsureAnchorArray();
      EnsureMeshObjects();

      if ( m_meshFilter == null || m_meshRenderer == null )
        return;

      ApplyMaterialColor( m_material );

      m_meshRenderer.enabled = showMesh;
      if ( !showMesh )
        return;

      Vector3[] vertices = new Vector3[ AnchorCount ];
      for ( int i = 0; i < AnchorCount; i++ )
        vertices[ i ] = WorldToVisualLocal( LocalToWorld( localAnchorPoints[ i ] ) );

      int[] triangles = new int[ ( DirectionCount - 1 ) * ( RadialCount - 1 ) * 6 ];
      int triangleIndex = 0;

      for ( int direction = 0; direction < DirectionCount - 1; direction++ ) {
        for ( int radial = 0; radial < RadialCount - 1; radial++ ) {
          int nearLeft = Index( direction, radial );
          int farLeft = Index( direction, radial + 1 );
          int nearRight = Index( direction + 1, radial );
          int farRight = Index( direction + 1, radial + 1 );

          triangles[ triangleIndex++ ] = nearLeft;
          triangles[ triangleIndex++ ] = farLeft;
          triangles[ triangleIndex++ ] = nearRight;

          triangles[ triangleIndex++ ] = farLeft;
          triangles[ triangleIndex++ ] = farRight;
          triangles[ triangleIndex++ ] = nearRight;
        }
      }

      m_mesh.Clear();
      m_mesh.name = "ReachableWorkspaceSectorMesh";
      m_mesh.vertices = vertices;
      m_mesh.triangles = triangles;
      m_mesh.RecalculateNormals();
      m_mesh.RecalculateBounds();
      m_meshFilter.sharedMesh = m_mesh;
    }

    [ContextMenu("Reset Anchors To Env01 Calibration")]
    public void ResetAnchorsToEnv01Calibration()
    {
      localAnchorPoints = DefaultAnchors();
      RebuildVisual();
    }

    private void EnsureMeshObjects()
    {
      Transform visual = transform.Find( VisualObjectName );
      if ( visual == null ) {
        var visualObject = new GameObject( VisualObjectName );
        visualObject.transform.SetParent( transform, false );
        visual = visualObject.transform;
      }

      visual.localPosition = Vector3.zero;
      visual.localRotation = Quaternion.identity;
      visual.localScale = Vector3.one;

      m_meshFilter = visual.GetComponent<MeshFilter>();
      if ( m_meshFilter == null )
        m_meshFilter = visual.gameObject.AddComponent<MeshFilter>();

      m_meshRenderer = visual.GetComponent<MeshRenderer>();
      if ( m_meshRenderer == null )
        m_meshRenderer = visual.gameObject.AddComponent<MeshRenderer>();

      if ( m_mesh == null ) {
        m_mesh = new Mesh {
          hideFlags = HideFlags.DontSave
        };
      }

      if ( m_material == null )
        m_material = CreateTransparentMaterial();

      if ( m_material != null )
        m_meshRenderer.sharedMaterial = m_material;
    }

    private Material CreateTransparentMaterial()
    {
      Shader shader = Shader.Find( "Universal Render Pipeline/Unlit" );
      if ( shader == null )
        shader = Shader.Find( "Unlit/Color" );
      if ( shader == null )
        shader = Shader.Find( "Standard" );
      if ( shader == null )
        shader = Shader.Find( "Hidden/Internal-Colored" );
      if ( shader == null )
        return null;

      var material = new Material( shader ) {
        name = "Reachable Workspace Fill",
        hideFlags = HideFlags.DontSave,
        renderQueue = (int)RenderQueue.Transparent
      };

      ApplyMaterialColor( material );
      ConfigureTransparency( material );
      return material;
    }

    private void ApplyMaterialColor( Material material )
    {
      if ( material == null )
        return;

      if ( material.HasProperty( "_BaseColor" ) )
        material.SetColor( "_BaseColor", fillColor );
      if ( material.HasProperty( "_Color" ) )
        material.SetColor( "_Color", fillColor );
    }

    private void ConfigureTransparency( Material material )
    {
      if ( material == null )
        return;

      if ( material.HasProperty( "_Surface" ) )
        material.SetFloat( "_Surface", 1f );
      if ( material.HasProperty( "_SrcBlend" ) )
        material.SetFloat( "_SrcBlend", (float)BlendMode.SrcAlpha );
      if ( material.HasProperty( "_DstBlend" ) )
        material.SetFloat( "_DstBlend", (float)BlendMode.OneMinusSrcAlpha );
      if ( material.HasProperty( "_ZWrite" ) )
        material.SetFloat( "_ZWrite", 0f );

      material.EnableKeyword( "_SURFACE_TYPE_TRANSPARENT" );
      material.EnableKeyword( "_ALPHABLEND_ON" );
    }

    private void EnsureAnchorArray()
    {
      if ( localAnchorPoints != null && localAnchorPoints.Length == AnchorCount )
        return;

      localAnchorPoints = DefaultAnchors();
    }

    private Vector3 LocalToWorld( Vector3 localPoint )
    {
      Transform frame = workspaceFrame != null ? workspaceFrame : transform;
      return frame.TransformPoint( localPoint );
    }

    private Vector3 WorldToVisualLocal( Vector3 worldPoint )
    {
      return transform.InverseTransformPoint( worldPoint );
    }

    private void OnDrawGizmos()
    {
      if ( !drawGizmos ) {
        return;
      }

      EnsureAnchorArray();

      if ( drawInternalGrid )
        DrawInternalGrid();

      DrawBoundary();

      if ( drawAnchorSpheres )
        DrawAnchors();
    }

    private void DrawInternalGrid()
    {
      Gizmos.color = internalGridColor;

      for ( int direction = 0; direction < DirectionCount; direction++ ) {
        for ( int radial = 0; radial < RadialCount - 1; radial++ )
          DrawLine( Index( direction, radial ), Index( direction, radial + 1 ) );
      }

      for ( int radial = 0; radial < RadialCount; radial++ ) {
        for ( int direction = 0; direction < DirectionCount - 1; direction++ )
          DrawLine( Index( direction, radial ), Index( direction + 1, radial ) );
      }
    }

    private void DrawBoundary()
    {
      Gizmos.color = boundaryColor;

      for ( int direction = 0; direction < DirectionCount - 1; direction++ ) {
        DrawLine( Index( direction, 0 ), Index( direction + 1, 0 ) );
        DrawLine( Index( direction, RadialCount - 1 ), Index( direction + 1, RadialCount - 1 ) );
      }

      for ( int radial = 0; radial < RadialCount - 1; radial++ ) {
        DrawLine( Index( 0, radial ), Index( 0, radial + 1 ) );
        DrawLine( Index( DirectionCount - 1, radial ), Index( DirectionCount - 1, radial + 1 ) );
      }
    }

    private void DrawAnchors()
    {
      Gizmos.color = anchorColor;
      for ( int i = 0; i < AnchorCount; i++ )
        Gizmos.DrawSphere( LocalToWorld( localAnchorPoints[ i ] ), Mathf.Max( anchorRadius, 0.01f ) );
    }

    private void DrawLine( int fromIndex, int toIndex )
    {
      Gizmos.DrawLine( LocalToWorld( localAnchorPoints[ fromIndex ] ), LocalToWorld( localAnchorPoints[ toIndex ] ) );
    }

    private static int Index( int direction, int radial )
    {
      return direction * RadialCount + radial;
    }

    public static Vector3[] Env01CalibrationAnchors()
    {
      return DefaultAnchors();
    }

    private static Vector3[] DefaultAnchors()
    {
      return new[] {
        new Vector3( -4.98f, 3.48f, 9.20f ),
        new Vector3( -5.10f, 3.08f, 10.95f ),
        new Vector3( -4.77f, 4.23f, 12.18f ),

        new Vector3( -0.34f, 3.46f, 7.00f ),
        new Vector3( 0.03f, 3.10f, 9.15f ),
        new Vector3( 1.70f, 4.19f, 9.20f ),

        new Vector3( 1.03f, 3.45f, 3.24f ),
        new Vector3( 2.82f, 3.15f, 3.39f ),
        new Vector3( 3.95f, 2.82f, 2.41f ),

        new Vector3( -0.36f, 3.44f, -0.63f ),
        new Vector3( 0.64f, 3.18f, -2.23f ),
        new Vector3( -0.10f, 2.78f, -4.24f ),

        new Vector3( -5.13f, 3.42f, -2.80f ),
        new Vector3( -4.94f, 3.22f, -4.64f ),
        new Vector3( -4.94f, 2.75f, -5.68f )
      };
    }
  }
}
