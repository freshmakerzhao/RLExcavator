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
    public const int DirectionCount = 5;
    public const int RadialCount = 3;
    public const int AnchorCount = DirectionCount * RadialCount;
    public const int VolumeCornerCount = 4;
    public const int VolumeAnchorCount = DirectionCount * VolumeCornerCount;

    [Header("Reference frame")]
    [Tooltip("Frame that owns the calibrated local points. Leave empty when this component is attached to env01.")]
    public Transform workspaceFrame;

    [Tooltip("Bucket-tip anchor points in workspaceFrame local coordinates: 5 directions x 3 ranges.")]
    public Vector3[] localAnchorPoints = DefaultAnchors();

    [Header("Volume anchors")]
    [Tooltip("3D workspace cross-section anchors: 5 directions x 4 corners. Corner order per direction: high-far, low-far, high-near, low-near.")]
    public Vector3[] localVolumeAnchorPoints = DefaultVolumeAnchors();

    [Header("Mesh")]
    public bool showMesh = true;
    public Color fillColor = new Color( 0.1f, 0.55f, 1.0f, 0.18f );

    [Header("Volume constraint")]
    [Tooltip("显示由可达锚点扩展得到的 3D 可达空间体积。Cartesian RRT* 可直接使用这个体积作为规划边界。")]
    public bool showVolume = true;

    [Tooltip("每个方向的 4 点截面会被采样成弧形边界。1 近似四边形，数值越大越像扇形。")]
    [Range( 1, 8 )]
    public int volumeSectionArcSegments = 4;

    [Tooltip("截面弧边相对近远方向的外鼓程度。0 为直边，0.1-0.3 更像扇形容器。")]
    [Range( 0f, 0.6f )]
    public float volumeSectionArcBulge = 0.18f;

    [Tooltip("相邻方向之间按挖掘机回转中心插值的弧线细分数。1 为直连，数值越大方向间越接近圆弧。")]
    [Range( 1, 24 )]
    public int volumeDirectionArcSegments = 12;

    [Tooltip("3D 可达空间体积的半透明颜色。")]
    public Color volumeColor = new Color( 0.2f, 0.55f, 1.0f, 0.08f );

    [Header("Gizmos")]
    public bool drawGizmos = true;
    [Tooltip("Legacy 15-anchor surface debug drawing. Keep disabled for the current Move/Carry volume workflow.")]
    public bool drawSurfaceGizmos = false;
    public bool drawInternalGrid = true;
    public bool drawAnchorSpheres = true;
    public bool drawVolumeBounds = true;
    public Color boundaryColor = new Color( 0.0f, 0.85f, 1.0f, 1.0f );
    public Color internalGridColor = new Color( 0.2f, 0.95f, 0.65f, 0.8f );
    public Color anchorColor = new Color( 1.0f, 0.72f, 0.05f, 1.0f );
    public Color volumeBoundaryColor = new Color( 0.35f, 0.75f, 1.0f, 0.85f );
    public float anchorRadius = 0.12f;

    [Header("Editable anchors")]
    [Tooltip("Play/Edit 模式下自动创建 15 个可拖拽锚点对象。关闭时仍可通过右键菜单手动创建。")]
    public bool autoCreateEditableAnchors = false;

    [Tooltip("开启后每帧从可拖拽锚点对象回写 localAnchorPoints，用于手动标定可达空间。")]
    public bool syncFromEditableAnchors = false;

    [Tooltip("可拖拽锚点在 Game/Scene 中显示的小球半径。")]
    public float editableAnchorVisualRadius = 0.10f;

    [Tooltip("可拖拽锚点小球颜色。")]
    public Color editableAnchorColor = new Color( 1.0f, 0.42f, 0.05f, 1.0f );

    [Header("Runtime volume editing")]
    [Tooltip("当前正在通过 Game UI 调整的 3D 可达空间边界点。0-19，对应 5 个方向 x 4 个截面点。")]
    public int selectedVolumeAnchorIndex = 0;

    [Tooltip("当前选中的可达空间边界点颜色。")]
    public Color selectedVolumeAnchorColor = new Color( 1.0f, 0.08f, 0.02f, 1.0f );

    [Tooltip("当前选中的可达空间边界点小球半径。")]
    public float selectedVolumeAnchorVisualRadius = 0.16f;

    [Header("Sampling")]
    [Tooltip("ClosestWorldPoint 近似投影时的默认采样分辨率。数值越大越精确，但调试时更慢。")]
    [Range( 4, 40 )]
    public int closestPointResolution = 18;

    private const string VisualObjectName = "ReachableWorkspaceSectorVisual";
    private const string VolumeVisualObjectName = "ReachableWorkspaceVolumeVisual";
    private const string VolumeEdgeObjectName = "ReachableWorkspaceVolumeEdges";
    private const string AnchorControlsRootName = "ReachableWorkspaceAnchorControls";
    private MeshFilter m_meshFilter;
    private MeshRenderer m_meshRenderer;
    private MeshFilter m_volumeMeshFilter;
    private MeshRenderer m_volumeMeshRenderer;
    private Mesh m_mesh;
    private Mesh m_volumeMesh;
    private Material m_material;
    private Material m_volumeMaterial;
    private Material m_volumeEdgeMaterial;
    private Transform m_anchorControlsRoot;
    private Transform m_volumeAnchorControlsRoot;
    private Transform m_volumeEdgeRoot;
    private bool m_needsRebuild;

    private void OnEnable()
    {
      EnsureAnchorArray();
      EnsureVolumeAnchorArray();
      if ( autoCreateEditableAnchors )
        CreateEditableAnchorObjects();
      RebuildVisual();
    }

    private void OnValidate()
    {
      EnsureAnchorArray();
      EnsureVolumeAnchorArray();
      m_needsRebuild = true;
    }

    private void Update()
    {
      if ( syncFromEditableAnchors && TrySyncAnchorsFromEditableObjects() )
        m_needsRebuild = true;

      if ( !m_needsRebuild )
        return;

      m_needsRebuild = false;
      RebuildVisual();
    }

    private void OnDisable()
    {
      if ( m_meshRenderer != null )
        m_meshRenderer.enabled = false;
      if ( m_volumeMeshRenderer != null )
        m_volumeMeshRenderer.enabled = false;
      if ( m_volumeEdgeRoot != null )
        m_volumeEdgeRoot.gameObject.SetActive( false );
    }

    [ContextMenu("Rebuild Workspace Visual")]
    public void RebuildVisual()
    {
      EnsureAnchorArray();
      EnsureMeshObjects();

      if ( m_meshFilter == null || m_meshRenderer == null )
        return;

      ApplyMaterialColor( m_material );
      ApplyMaterialColor( m_volumeMaterial, volumeColor );

      m_meshRenderer.enabled = showMesh;
      RebuildVolumeVisual();
      if ( !showMesh )
        return;

      Vector3[] vertices = new Vector3[ AnchorCount ];
      for ( int i = 0; i < AnchorCount; i++ )
        vertices[ i ] = WorldToVisualLocal( LocalPointToWorld( localAnchorPoints[ i ] ) );

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
      localVolumeAnchorPoints = DefaultVolumeAnchors();
      PushAnchorsToEditableObjects();
      PushVolumeAnchorsToEditableObjects();
      RebuildVisual();
    }

    public void CopyCalibrationFrom( ReachableWorkspaceVisualizer source )
    {
      if ( source == null )
        return;

      workspaceFrame = source.workspaceFrame != null ? source.workspaceFrame : source.transform;
      localAnchorPoints = source.localAnchorPoints != null ? (Vector3[])source.localAnchorPoints.Clone() : DefaultAnchors();
      localVolumeAnchorPoints = source.localVolumeAnchorPoints != null ? (Vector3[])source.localVolumeAnchorPoints.Clone() : DefaultVolumeAnchors();
      showMesh = source.showMesh;
      fillColor = source.fillColor;
      showVolume = source.showVolume;
      volumeSectionArcSegments = source.volumeSectionArcSegments;
      volumeSectionArcBulge = source.volumeSectionArcBulge;
      volumeDirectionArcSegments = source.volumeDirectionArcSegments;
      volumeColor = source.volumeColor;
      drawGizmos = source.drawGizmos;
      drawSurfaceGizmos = source.drawSurfaceGizmos;
      drawInternalGrid = source.drawInternalGrid;
      drawAnchorSpheres = source.drawAnchorSpheres;
      drawVolumeBounds = source.drawVolumeBounds;
      boundaryColor = source.boundaryColor;
      internalGridColor = source.internalGridColor;
      anchorColor = source.anchorColor;
      volumeBoundaryColor = source.volumeBoundaryColor;
      anchorRadius = source.anchorRadius;
      autoCreateEditableAnchors = false;
      syncFromEditableAnchors = false;
      editableAnchorVisualRadius = source.editableAnchorVisualRadius;
      editableAnchorColor = source.editableAnchorColor;
      selectedVolumeAnchorIndex = source.selectedVolumeAnchorIndex;
      selectedVolumeAnchorColor = source.selectedVolumeAnchorColor;
      selectedVolumeAnchorVisualRadius = source.selectedVolumeAnchorVisualRadius;
      closestPointResolution = source.closestPointResolution;
      RebuildVisual();
    }

    [ContextMenu("Create Editable Anchor Objects")]
    public void CreateEditableAnchorObjects()
    {
      EnsureAnchorArray();
      EnsureAnchorControlsRoot();

      for ( int i = 0; i < AnchorCount; i++ ) {
        Transform anchor = FindAnchorControl( i );
        if ( anchor == null ) {
          var anchorObject = new GameObject( AnchorObjectName( i ) );
          anchorObject.transform.SetParent( m_anchorControlsRoot, false );
          anchor = anchorObject.transform;
        }

        anchor.position = LocalPointToWorld( localAnchorPoints[ i ] );
        EnsureEditableAnchorVisual( anchor );
      }
    }

    [ContextMenu("Create Editable Volume Anchor Objects")]
    public void CreateEditableVolumeAnchorObjects()
    {
      EnsureVolumeAnchorArray();
      EnsureVolumeAnchorControlsRoot();

      for ( int i = 0; i < VolumeAnchorCount; i++ ) {
        Transform anchor = FindVolumeAnchorControl( i );
        if ( anchor == null ) {
          var anchorObject = new GameObject( VolumeAnchorObjectName( i ) );
          anchorObject.transform.SetParent( m_volumeAnchorControlsRoot, false );
          anchor = anchorObject.transform;
        }

        anchor.position = LocalPointToWorld( localVolumeAnchorPoints[ i ] );
        EnsureEditableAnchorVisual( anchor );
      }
    }

    [ContextMenu("Bake Anchors From Editable Objects")]
    public void BakeAnchorsFromEditableObjects()
    {
      if ( !TrySyncAnchorsFromEditableObjects() ) {
        Debug.LogWarning( "[ReachableWorkspaceVisualizer] Editable anchor objects are missing. Use 'Create Editable Anchor Objects' first." );
        return;
      }

      RebuildVisual();
    }

    [ContextMenu("Bake Volume Anchors From Editable Objects")]
    public void BakeVolumeAnchorsFromEditableObjects()
    {
      if ( !TrySyncVolumeAnchorsFromEditableObjects() ) {
        Debug.LogWarning( "[ReachableWorkspaceVisualizer] Editable volume anchor objects are missing. Use 'Create Editable Volume Anchor Objects' first." );
        return;
      }

      RebuildVisual();
    }

    [ContextMenu("Push Anchors To Editable Objects")]
    public void PushAnchorsToEditableObjects()
    {
      EnsureAnchorArray();
      EnsureAnchorControlsRoot();

      for ( int i = 0; i < AnchorCount; i++ ) {
        Transform anchor = FindAnchorControl( i );
        if ( anchor == null )
          continue;

        anchor.position = LocalPointToWorld( localAnchorPoints[ i ] );
        EnsureEditableAnchorVisual( anchor );
      }
    }

    [ContextMenu("Push Volume Anchors To Editable Objects")]
    public void PushVolumeAnchorsToEditableObjects()
    {
      EnsureVolumeAnchorArray();
      EnsureVolumeAnchorControlsRoot();

      for ( int i = 0; i < VolumeAnchorCount; i++ ) {
        Transform anchor = FindVolumeAnchorControl( i );
        if ( anchor == null )
          continue;

        anchor.position = LocalPointToWorld( localVolumeAnchorPoints[ i ] );
        EnsureEditableAnchorVisual( anchor );
      }

      HighlightSelectedVolumeAnchor();
    }

    public void ShowReachableVolumeAndBoundaryPoints()
    {
      showMesh = false;
      showVolume = true;
      drawGizmos = true;
      drawSurfaceGizmos = false;
      drawInternalGrid = false;
      drawAnchorSpheres = false;
      drawVolumeBounds = true;
      selectedVolumeAnchorIndex = ClampVolumeAnchorIndex( selectedVolumeAnchorIndex );
      CreateEditableVolumeAnchorObjects();
      SetVolumeAnchorControlsVisible( true );
      HighlightSelectedVolumeAnchor();
      RebuildVisual();
    }

    public void HideReachableVolumeAndBoundaryPoints()
    {
      showVolume = false;
      SetVolumeAnchorControlsVisible( false );
      SetVolumeEdgesVisible( false );
      RebuildVisual();
    }

    public void SelectNextVolumeAnchor()
    {
      SelectVolumeAnchor( selectedVolumeAnchorIndex + 1 );
    }

    public void SelectPreviousVolumeAnchor()
    {
      SelectVolumeAnchor( selectedVolumeAnchorIndex - 1 );
    }

    public void SelectVolumeAnchor( int index )
    {
      selectedVolumeAnchorIndex = ClampVolumeAnchorIndex( index );
      CreateEditableVolumeAnchorObjects();
      SetVolumeAnchorControlsVisible( true );
      HighlightSelectedVolumeAnchor();
    }

    public bool MoveSelectedVolumeAnchor( Vector3 localDirection, Transform directionFrame, float distance )
    {
      if ( localDirection.sqrMagnitude < 0.000001f || distance <= 0f )
        return false;

      selectedVolumeAnchorIndex = ClampVolumeAnchorIndex( selectedVolumeAnchorIndex );
      CreateEditableVolumeAnchorObjects();
      SetVolumeAnchorControlsVisible( true );

      Transform anchor = FindVolumeAnchorControl( selectedVolumeAnchorIndex );
      if ( anchor == null )
        return false;

      Transform frame = directionFrame != null ? directionFrame : workspaceFrame != null ? workspaceFrame : transform;
      Vector3 worldDirection = frame.TransformDirection( localDirection.normalized );
      if ( worldDirection.sqrMagnitude < 0.000001f )
        return false;

      anchor.position += worldDirection.normalized * distance;
      TrySyncVolumeAnchorsFromEditableObjects();
      HighlightSelectedVolumeAnchor();
      RebuildVisual();
      RebuildVolumeEdgeVisual();
      return true;
    }

    public bool SetSelectedVolumeAnchorWorldPosition( Vector3 worldPosition )
    {
      EnsureVolumeAnchorArray();
      selectedVolumeAnchorIndex = ClampVolumeAnchorIndex( selectedVolumeAnchorIndex );

      Vector3 localPoint = WorldPointToLocal( worldPosition );
      localVolumeAnchorPoints[ selectedVolumeAnchorIndex ] = localPoint;

      CreateEditableVolumeAnchorObjects();
      SetVolumeAnchorControlsVisible( true );

      Transform anchor = FindVolumeAnchorControl( selectedVolumeAnchorIndex );
      if ( anchor != null )
        anchor.position = worldPosition;

      HighlightSelectedVolumeAnchor();
      RebuildVisual();
      RebuildVolumeEdgeVisual();
      return true;
    }

    public string SelectedVolumeAnchorCoordinateSummary()
    {
      EnsureVolumeAnchorArray();
      selectedVolumeAnchorIndex = ClampVolumeAnchorIndex( selectedVolumeAnchorIndex );
      Vector3 localPoint = localVolumeAnchorPoints[ selectedVolumeAnchorIndex ];
      Vector3 worldPoint = LocalPointToWorld( localPoint );
      return $"{VolumeAnchorDisplayName( selectedVolumeAnchorIndex )} local={FormatVector( localPoint )} world={FormatVector( worldPoint )}";
    }

    public string SelectedVolumeAnchorLocalSummary()
    {
      EnsureVolumeAnchorArray();
      selectedVolumeAnchorIndex = ClampVolumeAnchorIndex( selectedVolumeAnchorIndex );
      Vector3 localPoint = localVolumeAnchorPoints[ selectedVolumeAnchorIndex ];
      return $"{VolumeAnchorDisplayName( selectedVolumeAnchorIndex )} {FormatVector( localPoint )}";
    }

    public void LogSelectedVolumeAnchorCoordinate()
    {
      Debug.Log( $"[ReachableWorkspaceVisualizer] {SelectedVolumeAnchorCoordinateSummary()}" );
    }

    public Vector3 SampleLocal( float direction01, float range01 )
    {
      EnsureAnchorArray();

      float directionScaled = Mathf.Clamp01( direction01 ) * ( DirectionCount - 1 );
      float rangeScaled = Mathf.Clamp01( range01 ) * ( RadialCount - 1 );
      int directionIndex = Mathf.Clamp( Mathf.FloorToInt( directionScaled ), 0, DirectionCount - 2 );
      int rangeIndex = Mathf.Clamp( Mathf.FloorToInt( rangeScaled ), 0, RadialCount - 2 );
      float directionT = directionScaled - directionIndex;
      float rangeT = rangeScaled - rangeIndex;

      Vector3 nearLeft = localAnchorPoints[ Index( directionIndex, rangeIndex ) ];
      Vector3 farLeft = localAnchorPoints[ Index( directionIndex, rangeIndex + 1 ) ];
      Vector3 nearRight = localAnchorPoints[ Index( directionIndex + 1, rangeIndex ) ];
      Vector3 farRight = localAnchorPoints[ Index( directionIndex + 1, rangeIndex + 1 ) ];

      Vector3 left = Vector3.Lerp( nearLeft, farLeft, rangeT );
      Vector3 right = Vector3.Lerp( nearRight, farRight, rangeT );
      return Vector3.Lerp( left, right, directionT );
    }

    public Vector3 SampleWorld( float direction01, float range01 )
    {
      return LocalPointToWorld( SampleLocal( direction01, range01 ) );
    }

    public Vector3 SampleWorld( Vector2 parameters )
    {
      return SampleWorld( parameters.x, parameters.y );
    }

    public Vector3 LocalPointToWorld( Vector3 localPoint )
    {
      Transform frame = workspaceFrame != null ? workspaceFrame : transform;
      return frame.TransformPoint( localPoint );
    }

    public Vector3 WorldPointToLocal( Vector3 worldPoint )
    {
      Transform frame = workspaceFrame != null ? workspaceFrame : transform;
      return frame.InverseTransformPoint( worldPoint );
    }

    public Vector3 ClosestLocalPoint( Vector3 localPoint )
    {
      return ClosestLocalPoint( localPoint, closestPointResolution );
    }

    public Vector3 ClosestLocalPoint( Vector3 localPoint, int resolution )
    {
      Vector2 parameters = ClosestLocalParameters( localPoint, resolution );
      return SampleLocal( parameters.x, parameters.y );
    }

    public Vector2 ClosestWorldParameters( Vector3 worldPoint )
    {
      return ClosestLocalParameters( WorldPointToLocal( worldPoint ) );
    }

    public Vector2 ClosestLocalParameters( Vector3 localPoint )
    {
      return ClosestLocalParameters( localPoint, closestPointResolution );
    }

    public Vector2 ClosestLocalParameters( Vector3 localPoint, int resolution )
    {
      EnsureAnchorArray();

      int steps = Mathf.Max( resolution, 2 );
      Vector2 bestParameters = Vector2.zero;
      Vector3 best = SampleLocal( bestParameters.x, bestParameters.y );
      float bestDistance = ( best - localPoint ).sqrMagnitude;

      for ( int d = 0; d <= steps; d++ ) {
        float direction = (float)d / steps;
        for ( int r = 0; r <= steps; r++ ) {
          float range = (float)r / steps;
          Vector3 candidate = SampleLocal( direction, range );
          float distance = ( candidate - localPoint ).sqrMagnitude;
          if ( distance >= bestDistance )
            continue;

          bestParameters = new Vector2( direction, range );
          best = candidate;
          bestDistance = distance;
        }
      }

      return bestParameters;
    }

    public Vector3 ClosestWorldPoint( Vector3 worldPoint )
    {
      Vector3 localPoint = WorldPointToLocal( worldPoint );
      return LocalPointToWorld( ClosestLocalPoint( localPoint ) );
    }

    public bool ContainsWorldPointInVolume( Vector3 worldPoint )
    {
      return ContainsLocalPointInVolume( WorldPointToLocal( worldPoint ) );
    }

    public bool ContainsLocalPointInVolume( Vector3 localPoint )
    {
      if ( !LocalVolumeBounds().Contains( localPoint ) )
        return false;

      return IsLocalPointInsideVolumeMesh( localPoint );
    }

    public Vector3 ClosestWorldPointInVolume( Vector3 worldPoint )
    {
      Vector3 localPoint = WorldPointToLocal( worldPoint );
      if ( ContainsWorldPointInVolume( worldPoint ) )
        return worldPoint;

      return LocalPointToWorld( ClosestLocalPointOnVolumeMesh( localPoint ) );
    }

    public Vector3 ClosestLocalPointInVolume( Vector3 localPoint )
    {
      if ( ContainsLocalPointInVolume( localPoint ) )
        return localPoint;

      return ClosestLocalPointOnVolumeMesh( localPoint );
    }

    public bool TryGetLocalVolumeBounds( out Bounds bounds )
    {
      bounds = LocalVolumeBounds();
      return true;
    }

    public bool TryGetWorldBounds( out Bounds bounds )
    {
      EnsureAnchorArray();

      if ( localAnchorPoints == null || localAnchorPoints.Length == 0 ) {
        bounds = new Bounds();
        return false;
      }

      bounds = new Bounds( LocalPointToWorld( localAnchorPoints[ 0 ] ), Vector3.zero );
      for ( int i = 1; i < localAnchorPoints.Length; i++ )
        bounds.Encapsulate( LocalPointToWorld( localAnchorPoints[ i ] ) );
      return true;
    }

    public bool TryGetWorldVolumeBounds( out Bounds bounds )
    {
      Bounds localBounds = LocalVolumeBounds();
      Vector3 min = localBounds.min;
      Vector3 max = localBounds.max;
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

      bounds = new Bounds( LocalPointToWorld( corners[ 0 ] ), Vector3.zero );
      for ( int i = 1; i < corners.Length; i++ )
        bounds.Encapsulate( LocalPointToWorld( corners[ i ] ) );

      return true;
    }

    private Bounds LocalVolumeBounds()
    {
      EnsureVolumeAnchorArray();
      Bounds bounds = new Bounds( localVolumeAnchorPoints[ 0 ], Vector3.zero );
      for ( int i = 1; i < localVolumeAnchorPoints.Length; i++ )
        bounds.Encapsulate( localVolumeAnchorPoints[ i ] );
      return bounds;
    }

    private Bounds LocalAnchorBounds()
    {
      EnsureAnchorArray();
      Bounds bounds = new Bounds( localAnchorPoints[ 0 ], Vector3.zero );
      for ( int i = 1; i < localAnchorPoints.Length; i++ )
        bounds.Encapsulate( localAnchorPoints[ i ] );
      return bounds;
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

      EnsureVolumeObjects();
    }

    private void EnsureVolumeObjects()
    {
      Transform visual = transform.Find( VolumeVisualObjectName );
      if ( visual == null ) {
        var visualObject = new GameObject( VolumeVisualObjectName );
        visualObject.transform.SetParent( transform, false );
        visual = visualObject.transform;
      }

      visual.localPosition = Vector3.zero;
      visual.localRotation = Quaternion.identity;
      visual.localScale = Vector3.one;

      m_volumeMeshFilter = visual.GetComponent<MeshFilter>();
      if ( m_volumeMeshFilter == null )
        m_volumeMeshFilter = visual.gameObject.AddComponent<MeshFilter>();

      m_volumeMeshRenderer = visual.GetComponent<MeshRenderer>();
      if ( m_volumeMeshRenderer == null )
        m_volumeMeshRenderer = visual.gameObject.AddComponent<MeshRenderer>();

      if ( m_volumeMesh == null ) {
        m_volumeMesh = new Mesh {
          hideFlags = HideFlags.DontSave
        };
      }

      if ( m_volumeMaterial == null )
        m_volumeMaterial = CreateTransparentMaterial( "Reachable Workspace Volume", volumeColor );

      if ( m_volumeMaterial != null )
        m_volumeMeshRenderer.sharedMaterial = m_volumeMaterial;
    }

    private void RebuildVolumeVisual()
    {
      EnsureVolumeObjects();
      if ( m_volumeMeshFilter == null || m_volumeMeshRenderer == null )
        return;

      ApplyMaterialColor( m_volumeMaterial, volumeColor );
      m_volumeMeshRenderer.enabled = showVolume;
      if ( !showVolume )
        return;

      EnsureVolumeAnchorArray();
      Vector3[] localVertices = BuildVolumeVerticesLocal();
      Vector3[] vertices = new Vector3[ localVertices.Length ];
      for ( int i = 0; i < vertices.Length; i++ )
        vertices[ i ] = WorldToVisualLocal( LocalPointToWorld( localVertices[ i ] ) );

      int[] triangles = VolumeMeshTriangles( VolumeSectionRowCount(), VolumeDirectionSampleCount() );

      m_volumeMesh.Clear();
      m_volumeMesh.name = "ReachableWorkspaceVolumeMesh";
      m_volumeMesh.vertices = vertices;
      m_volumeMesh.triangles = triangles;
      m_volumeMesh.RecalculateNormals();
      m_volumeMesh.RecalculateBounds();
      m_volumeMeshFilter.sharedMesh = m_volumeMesh;
      RebuildVolumeEdgeVisual();
    }

    private void RebuildVolumeEdgeVisual()
    {
      EnsureVolumeEdgeRoot();
      if ( m_volumeEdgeRoot == null )
        return;

      for ( int i = m_volumeEdgeRoot.childCount - 1; i >= 0; i-- )
        DestroyRuntimeObject( m_volumeEdgeRoot.GetChild( i ).gameObject );

      m_volumeEdgeRoot.gameObject.SetActive( showVolume );
      if ( !showVolume )
        return;

      EnsureVolumeAnchorArray();
      int rowCount = VolumeSectionRowCount();
      int directionSampleCount = VolumeDirectionSampleCount();
      int edgeIndex = 0;

      for ( int direction = 0; direction < directionSampleCount - 1; direction++ ) {
        for ( int row = 0; row < rowCount; row++ ) {
          AddVolumeEdge( edgeIndex++, VolumeDirectionSamplePoint( direction, row, true ), VolumeDirectionSamplePoint( direction + 1, row, true ) );
          AddVolumeEdge( edgeIndex++, VolumeDirectionSamplePoint( direction, row, false ), VolumeDirectionSamplePoint( direction + 1, row, false ) );
        }
      }

      for ( int direction = 0; direction < directionSampleCount; direction++ ) {
        for ( int row = 0; row < rowCount - 1; row++ ) {
          AddVolumeEdge( edgeIndex++, VolumeDirectionSamplePoint( direction, row, true ), VolumeDirectionSamplePoint( direction, row + 1, true ) );
          AddVolumeEdge( edgeIndex++, VolumeDirectionSamplePoint( direction, row, false ), VolumeDirectionSamplePoint( direction, row + 1, false ) );
        }

        AddVolumeEdge( edgeIndex++, VolumeDirectionSamplePoint( direction, 0, true ), VolumeDirectionSamplePoint( direction, 0, false ) );
        AddVolumeEdge( edgeIndex++, VolumeDirectionSamplePoint( direction, rowCount - 1, true ), VolumeDirectionSamplePoint( direction, rowCount - 1, false ) );
      }
    }

    private void AddVolumeEdge( int index, Vector3 fromLocal, Vector3 toLocal )
    {
      var lineObject = new GameObject( $"Edge_{index:00}" );
      lineObject.transform.SetParent( m_volumeEdgeRoot, false );

      var line = lineObject.AddComponent<LineRenderer>();
      line.useWorldSpace = true;
      line.positionCount = 2;
      line.SetPosition( 0, LocalPointToWorld( fromLocal ) );
      line.SetPosition( 1, LocalPointToWorld( toLocal ) );
      line.startColor = volumeBoundaryColor;
      line.endColor = volumeBoundaryColor;
      line.startWidth = 0.035f;
      line.endWidth = 0.035f;
      line.numCapVertices = 2;
      line.numCornerVertices = 2;

      if ( m_volumeEdgeMaterial == null )
        m_volumeEdgeMaterial = CreateTransparentMaterial( "Reachable Workspace Edges", volumeBoundaryColor );
      if ( m_volumeEdgeMaterial != null )
        line.sharedMaterial = m_volumeEdgeMaterial;
    }

    private Vector3 SurfaceNormalLocal( float direction01, float range01 )
    {
      const float epsilon = 0.01f;
      float d0 = Mathf.Clamp01( direction01 - epsilon );
      float d1 = Mathf.Clamp01( direction01 + epsilon );
      float r0 = Mathf.Clamp01( range01 - epsilon );
      float r1 = Mathf.Clamp01( range01 + epsilon );
      Vector3 directionTangent = SampleLocal( d1, range01 ) - SampleLocal( d0, range01 );
      Vector3 rangeTangent = SampleLocal( direction01, r1 ) - SampleLocal( direction01, r0 );
      Vector3 normal = Vector3.Cross( directionTangent, rangeTangent );
      return normal.sqrMagnitude > 0.000001f ? normal.normalized : Vector3.up;
    }

    private static void AddQuad( System.Collections.Generic.List<int> triangles, int a, int b, int c, int d )
    {
      triangles.Add( a );
      triangles.Add( b );
      triangles.Add( c );
      triangles.Add( b );
      triangles.Add( d );
      triangles.Add( c );
    }

    private Material CreateTransparentMaterial()
    {
      return CreateTransparentMaterial( "Reachable Workspace Fill", fillColor );
    }

    private Material CreateTransparentMaterial( string materialName, Color color )
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
        name = materialName,
        hideFlags = HideFlags.DontSave,
        renderQueue = (int)RenderQueue.Transparent
      };

      ApplyMaterialColor( material, color );
      ConfigureTransparency( material );
      return material;
    }

    private void ApplyMaterialColor( Material material )
    {
      ApplyMaterialColor( material, fillColor );
    }

    private void ApplyMaterialColor( Material material, Color color )
    {
      if ( material == null )
        return;

      if ( material.HasProperty( "_BaseColor" ) )
        material.SetColor( "_BaseColor", color );
      if ( material.HasProperty( "_Color" ) )
        material.SetColor( "_Color", color );
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

    private void EnsureVolumeAnchorArray()
    {
      if ( localVolumeAnchorPoints != null && localVolumeAnchorPoints.Length == VolumeAnchorCount )
        return;

      localVolumeAnchorPoints = DefaultVolumeAnchors();
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

      if ( drawSurfaceGizmos ) {
        EnsureAnchorArray();

        if ( drawInternalGrid )
          DrawInternalGrid();

        DrawBoundary();

        if ( drawAnchorSpheres )
          DrawAnchors();
      }

      if ( drawVolumeBounds )
        DrawVolumeBounds();
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
        Gizmos.DrawSphere( LocalPointToWorld( localAnchorPoints[ i ] ), Mathf.Max( anchorRadius, 0.01f ) );
    }

    private void DrawVolumeBounds()
    {
      EnsureVolumeAnchorArray();
      Gizmos.color = volumeBoundaryColor;
      int rowCount = VolumeSectionRowCount();
      int directionSampleCount = VolumeDirectionSampleCount();

      for ( int direction = 0; direction < directionSampleCount - 1; direction++ ) {
        for ( int row = 0; row < rowCount; row++ ) {
          DrawVolumeSampleLine( direction, row, true, direction + 1, row, true );
          DrawVolumeSampleLine( direction, row, false, direction + 1, row, false );
        }
      }

      for ( int direction = 0; direction < directionSampleCount; direction++ ) {
        for ( int row = 0; row < rowCount - 1; row++ ) {
          DrawVolumeSampleLine( direction, row, true, direction, row + 1, true );
          DrawVolumeSampleLine( direction, row, false, direction, row + 1, false );
        }

        DrawVolumeSampleLine( direction, 0, true, direction, 0, false );
        DrawVolumeSampleLine( direction, rowCount - 1, true, direction, rowCount - 1, false );
      }
    }

    private void DrawVolumeSampleLine( int fromDirection, int fromRow, bool fromFar, int toDirection, int toRow, bool toFar )
    {
      Gizmos.DrawLine(
        LocalPointToWorld( VolumeDirectionSamplePoint( fromDirection, fromRow, fromFar ) ),
        LocalPointToWorld( VolumeDirectionSamplePoint( toDirection, toRow, toFar ) ) );
    }

    private void DrawLine( int fromIndex, int toIndex )
    {
      Gizmos.DrawLine( LocalPointToWorld( localAnchorPoints[ fromIndex ] ), LocalPointToWorld( localAnchorPoints[ toIndex ] ) );
    }

    private static int Index( int direction, int radial )
    {
      return direction * RadialCount + radial;
    }

    private void EnsureAnchorControlsRoot()
    {
      if ( m_anchorControlsRoot != null )
        return;

      Transform existing = transform.Find( AnchorControlsRootName );
      if ( existing != null ) {
        m_anchorControlsRoot = existing;
        return;
      }

      var rootObject = new GameObject( AnchorControlsRootName );
      rootObject.transform.SetParent( transform, false );
      m_anchorControlsRoot = rootObject.transform;
    }

    private Transform FindAnchorControl( int index )
    {
      EnsureAnchorControlsRoot();
      return m_anchorControlsRoot != null ? m_anchorControlsRoot.Find( AnchorObjectName( index ) ) : null;
    }

    private void EnsureVolumeAnchorControlsRoot()
    {
      if ( m_volumeAnchorControlsRoot != null )
        return;

      Transform existing = transform.Find( "ReachableWorkspaceVolumeAnchorControls" );
      if ( existing != null ) {
        m_volumeAnchorControlsRoot = existing;
        return;
      }

      var rootObject = new GameObject( "ReachableWorkspaceVolumeAnchorControls" );
      rootObject.transform.SetParent( transform, false );
      m_volumeAnchorControlsRoot = rootObject.transform;
    }

    private void SetVolumeAnchorControlsVisible( bool visible )
    {
      EnsureVolumeAnchorControlsRoot();
      if ( m_volumeAnchorControlsRoot != null )
        m_volumeAnchorControlsRoot.gameObject.SetActive( visible );
    }

    private void EnsureVolumeEdgeRoot()
    {
      if ( m_volumeEdgeRoot != null )
        return;

      Transform existing = transform.Find( VolumeEdgeObjectName );
      if ( existing != null ) {
        m_volumeEdgeRoot = existing;
        return;
      }

      var rootObject = new GameObject( VolumeEdgeObjectName );
      rootObject.transform.SetParent( transform, false );
      m_volumeEdgeRoot = rootObject.transform;
    }

    private void SetVolumeEdgesVisible( bool visible )
    {
      EnsureVolumeEdgeRoot();
      if ( m_volumeEdgeRoot != null )
        m_volumeEdgeRoot.gameObject.SetActive( visible );
    }

    private Transform FindVolumeAnchorControl( int index )
    {
      EnsureVolumeAnchorControlsRoot();
      return m_volumeAnchorControlsRoot != null ? m_volumeAnchorControlsRoot.Find( VolumeAnchorObjectName( index ) ) : null;
    }

    private bool TrySyncAnchorsFromEditableObjects()
    {
      EnsureAnchorArray();
      Transform root = m_anchorControlsRoot != null ? m_anchorControlsRoot : transform.Find( AnchorControlsRootName );
      if ( root == null )
        return false;

      bool changed = false;
      for ( int i = 0; i < AnchorCount; i++ ) {
        Transform anchor = root.Find( AnchorObjectName( i ) );
        if ( anchor == null )
          return false;

        Vector3 localPoint = WorldPointToLocal( anchor.position );
        if ( ( localAnchorPoints[ i ] - localPoint ).sqrMagnitude > 0.000001f ) {
          localAnchorPoints[ i ] = localPoint;
          changed = true;
        }
      }

      return changed;
    }

    private bool TrySyncVolumeAnchorsFromEditableObjects()
    {
      EnsureVolumeAnchorArray();
      Transform root = m_volumeAnchorControlsRoot != null ? m_volumeAnchorControlsRoot : transform.Find( "ReachableWorkspaceVolumeAnchorControls" );
      if ( root == null )
        return false;

      bool changed = false;
      for ( int i = 0; i < VolumeAnchorCount; i++ ) {
        Transform anchor = root.Find( VolumeAnchorObjectName( i ) );
        if ( anchor == null )
          return false;

        Vector3 localPoint = WorldPointToLocal( anchor.position );
        if ( ( localVolumeAnchorPoints[ i ] - localPoint ).sqrMagnitude > 0.000001f ) {
          localVolumeAnchorPoints[ i ] = localPoint;
          changed = true;
        }
      }

      return changed;
    }

    private void EnsureEditableAnchorVisual( Transform anchor )
    {
      if ( anchor == null )
        return;

      var visual = anchor.GetComponent<TargetPointVisual>();
      if ( visual == null )
        visual = anchor.gameObject.AddComponent<TargetPointVisual>();

      visual.SetStyle( editableAnchorColor, editableAnchorVisualRadius );
      visual.SetVisible( true );
    }

    private void HighlightSelectedVolumeAnchor()
    {
      EnsureVolumeAnchorArray();
      EnsureVolumeAnchorControlsRoot();
      selectedVolumeAnchorIndex = ClampVolumeAnchorIndex( selectedVolumeAnchorIndex );

      for ( int i = 0; i < VolumeAnchorCount; i++ ) {
        Transform anchor = FindVolumeAnchorControl( i );
        if ( anchor == null )
          continue;

        var visual = anchor.GetComponent<TargetPointVisual>();
        if ( visual == null )
          visual = anchor.gameObject.AddComponent<TargetPointVisual>();

        bool selected = i == selectedVolumeAnchorIndex;
        visual.SetStyle(
          selected ? selectedVolumeAnchorColor : editableAnchorColor,
          selected ? selectedVolumeAnchorVisualRadius : editableAnchorVisualRadius );
        visual.SetVisible( true );
      }
    }

    private static int ClampVolumeAnchorIndex( int index )
    {
      if ( VolumeAnchorCount <= 0 )
        return 0;

      int wrapped = index % VolumeAnchorCount;
      return wrapped < 0 ? wrapped + VolumeAnchorCount : wrapped;
    }

    private static string FormatVector( Vector3 value )
    {
      return $"({value.x:0.00}, {value.y:0.00}, {value.z:0.00})";
    }

    private static void DestroyRuntimeObject( Object target )
    {
      if ( target == null )
        return;

      if ( Application.isPlaying )
        Destroy( target );
      else
        DestroyImmediate( target );
    }

    private static string AnchorObjectName( int index )
    {
      int direction = index / RadialCount;
      int radial = index % RadialCount;
      return $"Anchor_D{direction}_R{radial}";
    }

    private static string VolumeAnchorObjectName( int index )
    {
      int direction = index / VolumeCornerCount;
      int corner = index % VolumeCornerCount;
      string cornerName = corner == 0 ? "HighFar" : corner == 1 ? "LowFar" : corner == 2 ? "HighNear" : "LowNear";
      return $"Volume_D{direction}_{cornerName}";
    }

    private static string VolumeAnchorDisplayName( int index )
    {
      int direction = index / VolumeCornerCount;
      int corner = index % VolumeCornerCount;
      string cornerName = corner == 0 ? "高远" : corner == 1 ? "低远" : corner == 2 ? "高近" : "低近";
      return $"D{direction}-{cornerName}";
    }

    private static int VolumeIndex( int direction, int corner )
    {
      return direction * VolumeCornerCount + corner;
    }

    private int VolumeSectionRowCount()
    {
      return Mathf.Max( volumeSectionArcSegments, 1 ) + 1;
    }

    private int VolumeDirectionStepCount()
    {
      return Mathf.Max( volumeDirectionArcSegments, 1 );
    }

    private int VolumeDirectionSampleCount()
    {
      return ( DirectionCount - 1 ) * VolumeDirectionStepCount() + 1;
    }

    private Vector3[] BuildVolumeVerticesLocal()
    {
      int rowCount = VolumeSectionRowCount();
      int directionSampleCount = VolumeDirectionSampleCount();
      Vector3[] vertices = new Vector3[ directionSampleCount * rowCount * 2 ];
      for ( int direction = 0; direction < directionSampleCount; direction++ ) {
        for ( int row = 0; row < rowCount; row++ ) {
          vertices[ VolumeVertexIndex( direction, row, true, rowCount ) ] = VolumeDirectionSamplePoint( direction, row, true );
          vertices[ VolumeVertexIndex( direction, row, false, rowCount ) ] = VolumeDirectionSamplePoint( direction, row, false );
        }
      }

      return vertices;
    }

    private Vector3 VolumeDirectionSamplePoint( int directionSample, int row, bool far )
    {
      int stepCount = VolumeDirectionStepCount();
      int maxSample = VolumeDirectionSampleCount() - 1;
      int clampedSample = Mathf.Clamp( directionSample, 0, maxSample );
      int direction = Mathf.Clamp( clampedSample / stepCount, 0, DirectionCount - 2 );
      float t = (float)( clampedSample - direction * stepCount ) / stepCount;

      if ( clampedSample == maxSample ) {
        direction = DirectionCount - 2;
        t = 1f;
      }

      return SmoothDirectionPoint( direction, row, far, t );
    }

    private Vector3 VolumeSectionPoint( int direction, int row, bool far )
    {
      int rowCount = VolumeSectionRowCount();
      float t = rowCount <= 1 ? 0f : (float)row / ( rowCount - 1 );
      Vector3 highFar = localVolumeAnchorPoints[ VolumeIndex( direction, 0 ) ];
      Vector3 lowFar = localVolumeAnchorPoints[ VolumeIndex( direction, 1 ) ];
      Vector3 highNear = localVolumeAnchorPoints[ VolumeIndex( direction, 2 ) ];
      Vector3 lowNear = localVolumeAnchorPoints[ VolumeIndex( direction, 3 ) ];
      Vector3 farMid = ( highFar + lowFar ) * 0.5f;
      Vector3 nearMid = ( highNear + lowNear ) * 0.5f;
      Vector3 radial = farMid - nearMid;
      Vector3 curveOffset = radial * Mathf.Clamp( volumeSectionArcBulge, 0f, 0.6f );

      if ( far )
        return QuadraticBezier( highFar, farMid + curveOffset, lowFar, t );

      return QuadraticBezier( highNear, nearMid - curveOffset, lowNear, t );
    }

    private static Vector3 QuadraticBezier( Vector3 a, Vector3 b, Vector3 c, float t )
    {
      float u = 1f - t;
      return u * u * a + 2f * u * t * b + t * t * c;
    }

    private Vector3 SmoothDirectionPoint( int direction, int row, bool far, float t )
    {
      int p0Direction = Mathf.Max( direction - 1, 0 );
      int p1Direction = Mathf.Clamp( direction, 0, DirectionCount - 1 );
      int p2Direction = Mathf.Clamp( direction + 1, 0, DirectionCount - 1 );
      int p3Direction = Mathf.Min( direction + 2, DirectionCount - 1 );

      return CatmullRom(
        VolumeSectionPoint( p0Direction, row, far ),
        VolumeSectionPoint( p1Direction, row, far ),
        VolumeSectionPoint( p2Direction, row, far ),
        VolumeSectionPoint( p3Direction, row, far ),
        Mathf.Clamp01( t ) );
    }

    private static Vector3 CatmullRom( Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t )
    {
      float t2 = t * t;
      float t3 = t2 * t;
      return 0.5f * (
        2f * p1 +
        ( -p0 + p2 ) * t +
        ( 2f * p0 - 5f * p1 + 4f * p2 - p3 ) * t2 +
        ( -p0 + 3f * p1 - 3f * p2 + p3 ) * t3 );
    }

    private static int VolumeVertexIndex( int direction, int row, bool far, int rowCount )
    {
      return direction * rowCount * 2 + row * 2 + ( far ? 0 : 1 );
    }

    private static int[] VolumeMeshTriangles( int rowCount, int directionSampleCount )
    {
      var triangles = new System.Collections.Generic.List<int>();

      for ( int direction = 0; direction < directionSampleCount - 1; direction++ ) {
        for ( int row = 0; row < rowCount - 1; row++ ) {
          AddQuad(
            triangles,
            VolumeVertexIndex( direction, row, true, rowCount ),
            VolumeVertexIndex( direction + 1, row, true, rowCount ),
            VolumeVertexIndex( direction, row + 1, true, rowCount ),
            VolumeVertexIndex( direction + 1, row + 1, true, rowCount ) );
          AddQuad(
            triangles,
            VolumeVertexIndex( direction, row, false, rowCount ),
            VolumeVertexIndex( direction, row + 1, false, rowCount ),
            VolumeVertexIndex( direction + 1, row, false, rowCount ),
            VolumeVertexIndex( direction + 1, row + 1, false, rowCount ) );
        }

        AddQuad(
          triangles,
          VolumeVertexIndex( direction, 0, true, rowCount ),
          VolumeVertexIndex( direction, 0, false, rowCount ),
          VolumeVertexIndex( direction + 1, 0, true, rowCount ),
          VolumeVertexIndex( direction + 1, 0, false, rowCount ) );
        AddQuad(
          triangles,
          VolumeVertexIndex( direction, rowCount - 1, true, rowCount ),
          VolumeVertexIndex( direction + 1, rowCount - 1, true, rowCount ),
          VolumeVertexIndex( direction, rowCount - 1, false, rowCount ),
          VolumeVertexIndex( direction + 1, rowCount - 1, false, rowCount ) );
      }

      for ( int row = 0; row < rowCount - 1; row++ ) {
        AddQuad(
          triangles,
          VolumeVertexIndex( 0, row, true, rowCount ),
          VolumeVertexIndex( 0, row + 1, true, rowCount ),
          VolumeVertexIndex( 0, row, false, rowCount ),
          VolumeVertexIndex( 0, row + 1, false, rowCount ) );
      }

      int last = directionSampleCount - 1;
      for ( int row = 0; row < rowCount - 1; row++ ) {
        AddQuad(
          triangles,
          VolumeVertexIndex( last, row, true, rowCount ),
          VolumeVertexIndex( last, row, false, rowCount ),
          VolumeVertexIndex( last, row + 1, true, rowCount ),
          VolumeVertexIndex( last, row + 1, false, rowCount ) );
      }

      return triangles.ToArray();
    }

    private bool IsLocalPointInsideVolumeMesh( Vector3 point )
    {
      Vector3[] vertices = BuildVolumeVerticesLocal();
      int[] triangles = VolumeMeshTriangles( VolumeSectionRowCount(), VolumeDirectionSampleCount() );
      int hitCount = 0;
      Vector3 rayOrigin = point + Vector3.left * 10000f;
      Vector3 rayDirection = Vector3.right;

      for ( int i = 0; i < triangles.Length; i += 3 ) {
        if ( RayIntersectsTriangle(
          rayOrigin,
          rayDirection,
          vertices[ triangles[ i ] ],
          vertices[ triangles[ i + 1 ] ],
          vertices[ triangles[ i + 2 ] ],
          out float distance ) && distance >= 0f ) {
          hitCount++;
        }
      }

      return hitCount % 2 == 1;
    }

    private Vector3 ClosestLocalPointOnVolumeMesh( Vector3 point )
    {
      Vector3[] vertices = BuildVolumeVerticesLocal();
      int[] triangles = VolumeMeshTriangles( VolumeSectionRowCount(), VolumeDirectionSampleCount() );
      Vector3 best = localVolumeAnchorPoints[ 0 ];
      float bestDistance = float.PositiveInfinity;

      for ( int i = 0; i < triangles.Length; i += 3 ) {
        Vector3 candidate = ClosestPointOnTriangle(
          point,
          vertices[ triangles[ i ] ],
          vertices[ triangles[ i + 1 ] ],
          vertices[ triangles[ i + 2 ] ] );
        float distance = ( candidate - point ).sqrMagnitude;
        if ( distance >= bestDistance )
          continue;

        best = candidate;
        bestDistance = distance;
      }

      return best;
    }

    private static bool RayIntersectsTriangle( Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance )
    {
      distance = 0f;
      Vector3 edge1 = b - a;
      Vector3 edge2 = c - a;
      Vector3 h = Vector3.Cross( direction, edge2 );
      float determinant = Vector3.Dot( edge1, h );
      if ( Mathf.Abs( determinant ) < 0.000001f )
        return false;

      float inverseDeterminant = 1f / determinant;
      Vector3 s = origin - a;
      float u = inverseDeterminant * Vector3.Dot( s, h );
      if ( u < 0f || u > 1f )
        return false;

      Vector3 q = Vector3.Cross( s, edge1 );
      float v = inverseDeterminant * Vector3.Dot( direction, q );
      if ( v < 0f || u + v > 1f )
        return false;

      distance = inverseDeterminant * Vector3.Dot( edge2, q );
      return distance > 0.000001f;
    }

    private static Vector3 ClosestPointOnTriangle( Vector3 point, Vector3 a, Vector3 b, Vector3 c )
    {
      Vector3 ab = b - a;
      Vector3 ac = c - a;
      Vector3 ap = point - a;
      float d1 = Vector3.Dot( ab, ap );
      float d2 = Vector3.Dot( ac, ap );
      if ( d1 <= 0f && d2 <= 0f )
        return a;

      Vector3 bp = point - b;
      float d3 = Vector3.Dot( ab, bp );
      float d4 = Vector3.Dot( ac, bp );
      if ( d3 >= 0f && d4 <= d3 )
        return b;

      float vc = d1 * d4 - d3 * d2;
      if ( vc <= 0f && d1 >= 0f && d3 <= 0f )
        return a + ab * ( d1 / ( d1 - d3 ) );

      Vector3 cp = point - c;
      float d5 = Vector3.Dot( ab, cp );
      float d6 = Vector3.Dot( ac, cp );
      if ( d6 >= 0f && d5 <= d6 )
        return c;

      float vb = d5 * d2 - d1 * d6;
      if ( vb <= 0f && d2 >= 0f && d6 <= 0f )
        return a + ac * ( d2 / ( d2 - d6 ) );

      float va = d3 * d6 - d5 * d4;
      if ( va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f )
        return b + ( c - b ) * ( ( d4 - d3 ) / ( ( d4 - d3 ) + ( d5 - d6 ) ) );

      float denominator = 1f / ( va + vb + vc );
      float v = vb * denominator;
      float w = vc * denominator;
      return a + ab * v + ac * w;
    }

    public static Vector3[] Env01CalibrationAnchors()
    {
      return DefaultAnchors();
    }

    public static Vector3[] Env01VolumeAnchors()
    {
      return DefaultVolumeAnchors();
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

    private static Vector3[] DefaultVolumeAnchors()
    {
      return new[] {
        new Vector3( -4.77f, 4.70f, 12.18f ),
        new Vector3( -5.10f, 2.25f, 10.95f ),
        new Vector3( -4.98f, 4.25f, 9.20f ),
        new Vector3( -4.98f, 2.35f, 9.20f ),

        new Vector3( 1.70f, 4.65f, 9.20f ),
        new Vector3( 0.03f, 2.30f, 9.15f ),
        new Vector3( -0.34f, 4.25f, 7.00f ),
        new Vector3( -0.34f, 2.45f, 7.00f ),

        new Vector3( 3.95f, 4.05f, 2.41f ),
        new Vector3( 2.82f, 2.20f, 3.39f ),
        new Vector3( 1.03f, 4.15f, 3.24f ),
        new Vector3( 1.03f, 2.40f, 3.24f ),

        new Vector3( -0.10f, 4.05f, -4.24f ),
        new Vector3( 0.64f, 2.20f, -2.23f ),
        new Vector3( -0.36f, 4.15f, -0.63f ),
        new Vector3( -0.36f, 2.40f, -0.63f ),

        new Vector3( -4.94f, 4.05f, -5.68f ),
        new Vector3( -4.94f, 2.05f, -4.64f ),
        new Vector3( -5.13f, 4.15f, -2.80f ),
        new Vector3( -5.13f, 2.35f, -2.80f )
      };
    }
  }
}
