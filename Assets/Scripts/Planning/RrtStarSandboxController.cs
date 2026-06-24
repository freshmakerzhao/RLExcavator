using UnityEngine;
using UnityEngine.Rendering;

namespace RLExcavator
{
  public class RrtStarSandboxController : MonoBehaviour
  {
    public const string ObstacleLayerName = "RrtObstacle";

    [Header("Planning")]
    public Vector3 boundsCenter = new Vector3( 0f, 0.65f, 0f );
    public Vector3 boundsSize = new Vector3( 28f, 1.2f, 18f );
    [Tooltip("Move this marker in the scene to choose the RRT* start position.")]
    public Transform startPoint;
    [Tooltip("Move this marker in the scene to choose the RRT* goal position.")]
    public Transform goalPoint;
    public LayerMask obstacleMask;
    public RrtStar3DSettings settings = new RrtStar3DSettings {
      maxIterations = 1800,
      stepSize = 1.2f,
      goalBias = 0.15f,
      goalTolerance = 1.1f,
      rewireRadius = 2.8f,
      collisionRadius = 0.32f,
      edgeCheckResolution = 8,
      smoothingPasses = 4,
      seed = 11,
      preferDirectPathWhenClear = false
    };
    public bool autoPlanOnStart = true;

    [Header("Display")]
    public bool showSearchTree = false;
    public int maxSearchTreeEdges = 320;
    public bool showRawPath = false;
    public bool showSmoothedPath = true;
    public bool showBounds = false;
    public float treeLineWidth = 0.012f;
    public float rawPathWidth = 0.045f;
    public float smoothedPathWidth = 0.16f;
    public Color treeColor = new Color( 0.95f, 0.55f, 0.08f, 0.28f );
    public Color rawPathColor = new Color( 0.2f, 0.42f, 1f, 0.75f );
    public Color smoothedPathColor = new Color( 1f, 0.05f, 0.04f, 1f );
    public Color boundsColor = new Color( 0.8f, 0.8f, 0.8f, 0.35f );

    [Header("Runtime state")]
    [TextArea( 2, 4 )]
    public string lastSummary = "Not planned";
    public bool lastPlanSucceeded;
    public int lastNodeCount;

    private Transform m_visualRoot;
    private Material m_treeMaterial;
    private Material m_rawPathMaterial;
    private Material m_smoothedPathMaterial;
    private Material m_boundsMaterial;

    private void Start()
    {
      EnsureReferences();
      if ( autoPlanOnStart )
        PlanPath();
    }

    private void OnValidate()
    {
      boundsSize = new Vector3(
        Mathf.Max( boundsSize.x, 0.1f ),
        Mathf.Max( boundsSize.y, 0.1f ),
        Mathf.Max( boundsSize.z, 0.1f ) );

      maxSearchTreeEdges = Mathf.Max( maxSearchTreeEdges, 0 );
      if ( settings == null )
        settings = new RrtStar3DSettings();
    }

    [ContextMenu("Plan Path")]
    public void PlanPath()
    {
      EnsureReferences();
      ClearPlanningVisuals();

      if ( startPoint == null || goalPoint == null ) {
        lastPlanSucceeded = false;
        lastSummary = "Missing StartPoint or GoalPoint.";
        Debug.LogWarning( $"[RrtStarSandboxController] {lastSummary}", this );
        return;
      }

      Bounds bounds = new Bounds( boundsCenter, boundsSize );
      if ( !ValidateEndpoint( "StartPoint", startPoint.position, bounds ) || !ValidateEndpoint( "GoalPoint", goalPoint.position, bounds ) )
        return;

      var collisionChecker = new PhysicsRrtStar3DCollisionChecker( obstacleMask, settings.edgeCheckResolution );
      var planner = new RrtStar3DPlanner( settings, collisionChecker );
      RrtStar3DResult result = planner.Plan( bounds, startPoint.position, goalPoint.position );

      lastPlanSucceeded = result.success;
      lastNodeCount = result.nodes.Length;
      lastSummary = result.summary;

      DrawResult( result );
      Debug.Log( $"[RrtStarSandboxController] {lastSummary}", this );
    }

    [ContextMenu("Clear Planning Visuals")]
    public void ClearPlanningVisuals()
    {
      if ( m_visualRoot == null ) {
        Transform existing = transform.Find( "RrtStarVisuals" );
        if ( existing != null )
          m_visualRoot = existing;
      }

      if ( m_visualRoot != null )
        ClearChildren( m_visualRoot );
    }

    public Bounds PlanningBounds()
    {
      return new Bounds( boundsCenter, boundsSize );
    }

    private void EnsureReferences()
    {
      if ( settings == null )
        settings = new RrtStar3DSettings();

      if ( startPoint == null ) {
        Transform existing = FindEndpoint( "StartPoint" );
        if ( existing != null )
          startPoint = existing;
      }

      if ( goalPoint == null ) {
        Transform existing = FindEndpoint( "GoalPoint" );
        if ( existing != null )
          goalPoint = existing;
      }

      if ( m_visualRoot == null ) {
        Transform existing = transform.Find( "RrtStarVisuals" );
        m_visualRoot = existing != null ? existing : CreateChild( "RrtStarVisuals" );
      }

      if ( (int)obstacleMask == 0 ) {
        int layer = LayerMask.NameToLayer( ObstacleLayerName );
        if ( layer >= 0 )
          obstacleMask = 1 << layer;
      }
    }

    private Transform FindEndpoint( string endpointName )
    {
      Transform child = transform.Find( endpointName );
      if ( child != null )
        return child;

      GameObject sceneObject = GameObject.Find( endpointName );
      return sceneObject != null ? sceneObject.transform : null;
    }

    private Transform CreateChild( string childName )
    {
      var child = new GameObject( childName );
      child.transform.SetParent( transform, false );
      return child.transform;
    }

    private bool ValidateEndpoint( string endpointName, Vector3 position, Bounds bounds )
    {
      if ( !bounds.Contains( position ) ) {
        lastPlanSucceeded = false;
        lastNodeCount = 0;
        lastSummary = $"{endpointName} is outside planning bounds.";
        Debug.LogWarning( $"[RrtStarSandboxController] {lastSummary}", this );
        return false;
      }

      float radius = Mathf.Max( settings.collisionRadius, 0.01f );
      if ( (int)obstacleMask != 0 && Physics.CheckSphere( position, radius, obstacleMask, QueryTriggerInteraction.Ignore ) ) {
        lastPlanSucceeded = false;
        lastNodeCount = 0;
        lastSummary = $"{endpointName} overlaps an obstacle.";
        Debug.LogWarning( $"[RrtStarSandboxController] {lastSummary}", this );
        return false;
      }

      return true;
    }

    private void DrawResult( RrtStar3DResult result )
    {
      EnsureReferences();

      if ( showBounds )
        DrawBounds();

      if ( result == null )
        return;

      if ( showSearchTree )
        DrawSearchTree( result.nodes );

      if ( showRawPath )
        DrawPolyline( "RawPath", result.rawPath, rawPathWidth, rawPathColor, MaterialFor( ref m_rawPathMaterial, rawPathColor ) );

      if ( showSmoothedPath )
        DrawPolyline( "SmoothedPath", result.smoothedPath, smoothedPathWidth, smoothedPathColor, MaterialFor( ref m_smoothedPathMaterial, smoothedPathColor ) );
    }

    private void DrawSearchTree( RrtStar3DNode[] nodes )
    {
      if ( nodes == null || nodes.Length <= 1 || maxSearchTreeEdges <= 0 )
        return;

      Material material = MaterialFor( ref m_treeMaterial, treeColor );
      int stride = Mathf.Max( Mathf.CeilToInt( ( nodes.Length - 1 ) / (float)maxSearchTreeEdges ), 1 );
      for ( int i = 1; i < nodes.Length; i += stride ) {
        int parent = nodes[ i ].parentIndex;
        if ( parent < 0 || parent >= nodes.Length )
          continue;

        DrawLine( $"TreeEdge_{i:0000}", nodes[ parent ].position, nodes[ i ].position, treeLineWidth, material );
      }
    }

    private void DrawBounds()
    {
      Bounds bounds = PlanningBounds();
      Vector3 min = bounds.min;
      Vector3 max = bounds.max;
      Vector3[] corners = {
        new Vector3( min.x, min.y, min.z ),
        new Vector3( max.x, min.y, min.z ),
        new Vector3( max.x, min.y, max.z ),
        new Vector3( min.x, min.y, max.z ),
        new Vector3( min.x, max.y, min.z ),
        new Vector3( max.x, max.y, min.z ),
        new Vector3( max.x, max.y, max.z ),
        new Vector3( min.x, max.y, max.z )
      };

      int[,] edges = {
        { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 0 },
        { 4, 5 }, { 5, 6 }, { 6, 7 }, { 7, 4 },
        { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 }
      };

      Material material = MaterialFor( ref m_boundsMaterial, boundsColor );
      for ( int i = 0; i < edges.GetLength( 0 ); i++ )
        DrawLine( $"BoundsEdge_{i:00}", corners[ edges[ i, 0 ] ], corners[ edges[ i, 1 ] ], treeLineWidth * 1.25f, material );
    }

    private void DrawPolyline( string lineName, Vector3[] points, float width, Color color, Material material )
    {
      if ( points == null || points.Length < 2 )
        return;

      Vector3[] filteredPoints = FilterDrawablePoints( points );
      if ( filteredPoints.Length < 2 )
        return;

      var lineObject = new GameObject( lineName );
      lineObject.transform.SetParent( m_visualRoot, false );
      var line = lineObject.AddComponent<LineRenderer>();
      ConfigureLineRenderer( line, width, material );
      line.positionCount = filteredPoints.Length;
      line.SetPositions( filteredPoints );
      line.startColor = color;
      line.endColor = color;
    }

    private void DrawLine( string lineName, Vector3 from, Vector3 to, float width, Material material )
    {
      if ( !IsDrawableSegment( from, to ) )
        return;

      var lineObject = new GameObject( lineName );
      lineObject.transform.SetParent( m_visualRoot, false );
      var line = lineObject.AddComponent<LineRenderer>();
      ConfigureLineRenderer( line, width, material );
      line.positionCount = 2;
      line.SetPosition( 0, from );
      line.SetPosition( 1, to );
    }

    private static Vector3[] FilterDrawablePoints( Vector3[] points )
    {
      var filtered = new System.Collections.Generic.List<Vector3>( points.Length );
      for ( int i = 0; i < points.Length; i++ ) {
        Vector3 point = points[ i ];
        if ( !IsFinite( point ) )
          continue;

        if ( filtered.Count > 0 && !IsDrawableSegment( filtered[ filtered.Count - 1 ], point ) )
          continue;

        filtered.Add( point );
      }

      return filtered.ToArray();
    }

    private static bool IsDrawableSegment( Vector3 from, Vector3 to )
    {
      if ( !IsFinite( from ) || !IsFinite( to ) )
        return false;

      return ( to - from ).sqrMagnitude > 0.0001f;
    }

    private static bool IsFinite( Vector3 value )
    {
      return IsFinite( value.x ) && IsFinite( value.y ) && IsFinite( value.z );
    }

    private static bool IsFinite( float value )
    {
      return !float.IsNaN( value ) && !float.IsInfinity( value );
    }

    private static void ConfigureLineRenderer( LineRenderer line, float width, Material material )
    {
      line.useWorldSpace = true;
      line.widthMultiplier = Mathf.Max( width, 0.001f );
      line.numCornerVertices = 2;
      line.numCapVertices = 2;
      line.shadowCastingMode = ShadowCastingMode.Off;
      line.receiveShadows = false;
      line.material = material;
    }

    private Material MaterialFor( ref Material cached, Color color )
    {
      if ( cached == null )
        cached = CreateLineMaterial( color );

      return cached;
    }

    private static Material CreateLineMaterial( Color color )
    {
      Shader shader = Shader.Find( "Universal Render Pipeline/Unlit" );
      if ( shader == null )
        shader = Shader.Find( "Sprites/Default" );
      if ( shader == null )
        shader = Shader.Find( "Unlit/Color" );

      var material = new Material( shader ) {
        name = "RRT* Line Material",
        hideFlags = HideFlags.DontSave
      };
      material.SetColor( "_BaseColor", color );
      material.SetColor( "_Color", color );
      return material;
    }

    private static void ClearChildren( Transform root )
    {
      if ( root == null )
        return;

      for ( int i = root.childCount - 1; i >= 0; i-- ) {
        Transform child = root.GetChild( i );
        if ( Application.isPlaying )
          Destroy( child.gameObject );
        else
          DestroyImmediate( child.gameObject );
      }
    }
  }
}
