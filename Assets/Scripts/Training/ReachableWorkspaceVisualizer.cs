using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RLExcavator
{
  /// <summary>
  /// Visualizes and edits the calibrated 20-point bucket-tip reachable volume.
  /// </summary>
  [ExecuteAlways]
  public class ReachableWorkspaceVisualizer : MonoBehaviour
  {
    public const int DirectionCount = 5;
    public const int VolumeCornerCount = 4;
    public const int VolumeAnchorCount = DirectionCount * VolumeCornerCount;

    [Header("Reference frame")]
    [Tooltip("挖机语义坐标注册器。开启下方选项时，可达区域局部点相对 MachineRoot。")]
    public ExcavatorFrameRegistry frameRegistry;

    [Tooltip("使用 FrameRegistry.MachineRoot 作为可达区域坐标系。正式缩比模型应保持开启。")]
    public bool useFrameRegistryMachineRoot = true;

    [Tooltip("Frame that owns the calibrated local points. Leave empty when this component is attached to the machine root.")]
    public Transform workspaceFrame;

    public Transform EffectiveWorkspaceFrame => ResolveWorkspaceFrame();

    [Header("Workspace profile")]
    [Tooltip("从 machine_profile 指向的可达区域文件读取本模式标定点。")]
    public bool useWorkspaceProfile = false;

    [Tooltip("可达区域模式名，例如 MoveToDig 或 CarryMaterial。为空时按对象名推断。")]
    public string workspaceMode = "";

    [Tooltip("标定点变化时写回共享可达区域文件。")]
    public bool saveWorkspaceProfileChanges = true;

    [Header("Volume anchors")]
    [Tooltip("3D workspace cross-section anchors: 5 directions x 4 corners. Corner order per direction: high-far, low-far, high-near, low-near.")]
    public Vector3[] localVolumeAnchorPoints = DefaultVolumeAnchors();

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
    public bool drawAnchorSpheres = true;
    public bool drawVolumeBounds = true;
    public Color anchorColor = new Color( 1.0f, 0.72f, 0.05f, 1.0f );
    public Color volumeBoundaryColor = new Color( 0.35f, 0.75f, 1.0f, 0.85f );
    [Tooltip("3D 可达空间边界线宽度。")]
    [Min( 0.001f )]
    public float volumeBoundaryLineWidth = 0.035f;
    public float anchorRadius = 0.04f;

    [Header("Editable anchors")]
    [Tooltip("可拖拽锚点在 Game/Scene 中显示的小球半径。")]
    public float editableAnchorVisualRadius = 0.04f;

    [Tooltip("可拖拽锚点小球颜色。")]
    public Color editableAnchorColor = new Color( 1.0f, 0.42f, 0.05f, 1.0f );

    [Header("Runtime volume editing")]
    [Tooltip("当前正在通过 Game UI 调整的 3D 可达空间边界点。0-19，对应 5 个方向 x 4 个截面点。")]
    public int selectedVolumeAnchorIndex = 0;

    [Tooltip("当前选中的可达空间边界点颜色。")]
    public Color selectedVolumeAnchorColor = new Color( 1.0f, 0.08f, 0.02f, 1.0f );

    [Tooltip("当前选中的可达空间边界点小球半径。")]
    public float selectedVolumeAnchorVisualRadius = 0.06f;

    [Header("Sampling")]
    [Tooltip("最近点近似投影时的默认采样分辨率。数值越大越精确，但调试时更慢。")]
    [Range( 4, 40 )]
    public int closestPointResolution = 18;

    private const string VolumeVisualObjectName = "ReachableWorkspaceVolumeVisual";
    private const string VolumeEdgeObjectName = "ReachableWorkspaceVolumeEdges";
    private const string VolumeAnchorControlsRootName = "ReachableWorkspaceVolumeAnchorControls";
    private const HideFlags GeneratedVisualHideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;

    private MeshFilter m_volumeMeshFilter;
    private MeshRenderer m_volumeMeshRenderer;
    private Mesh m_volumeMesh;
    private Material m_volumeMaterial;
    private Material m_volumeEdgeMaterial;
    private Transform m_volumeAnchorControlsRoot;
    private Transform m_volumeEdgeRoot;
    private bool m_needsRebuild;
    private bool m_profileWarningShown;
    private bool m_isApplyingWorkspaceProfile;

    private void OnEnable()
    {
      ResolveFrameRegistryIfNeeded();
      ApplyWorkspaceProfileIfEnabled();
      EnsureVolumeAnchorArray();
      RebuildVisual();
    }

    private void OnValidate()
    {
      ResolveFrameRegistryIfNeeded();
      ApplyWorkspaceProfileIfEnabled( false );
      EnsureVolumeAnchorArray();
      m_needsRebuild = true;
    }

    private void Update()
    {
      if ( !m_needsRebuild )
        return;

      m_needsRebuild = false;
      RebuildVisual();
      RefreshExistingVolumeAnchorVisuals();
    }

    private void OnDisable()
    {
      if ( m_volumeMeshRenderer != null )
        m_volumeMeshRenderer.enabled = false;
      if ( m_volumeEdgeRoot != null )
        m_volumeEdgeRoot.gameObject.SetActive( false );
    }

    [ContextMenu("Rebuild Workspace Visual")]
    public void RebuildVisual()
    {
      EnsureVolumeAnchorArray();

      if ( !showVolume ) {
        DestroyGeneratedVisualObjects();
        return;
      }

      RebuildVolumeVisual();
    }

    [ContextMenu("Reset Anchors To Env01 Calibration")]
    public void ResetAnchorsToEnv01Calibration()
    {
      localVolumeAnchorPoints = DefaultVolumeAnchors();
      PushVolumeAnchorsToEditableObjects();
      RebuildVisual();
      MarkCalibrationDirty();
    }

    [ContextMenu("Load From Workspace Profile")]
    public void LoadFromWorkspaceProfile()
    {
      string message;
      if ( TryLoadFromWorkspaceProfile( out message ) )
        Debug.Log( "[ReachableWorkspace] " + message, this );
      else
        Debug.LogWarning( "[ReachableWorkspace] " + message, this );
    }

    [ContextMenu("Save To Workspace Profile")]
    public void SaveToWorkspaceProfile()
    {
      string message;
      if ( TrySaveToWorkspaceProfile( out message ) )
        Debug.Log( "[ReachableWorkspace] " + message, this );
      else
        Debug.LogWarning( "[ReachableWorkspace] " + message, this );
    }

    public bool TryLoadFromWorkspaceProfile( out string message )
    {
      return TryLoadFromWorkspaceProfile( out message, true );
    }

    private bool TryLoadFromWorkspaceProfile( out string message, bool updateVisuals )
    {
      message = string.Empty;
      string mode = EffectiveWorkspaceMode();
      if ( string.IsNullOrEmpty( mode ) ) {
        message = "Workspace mode is empty; cannot load reachable workspace profile.";
        return false;
      }

      try {
        ReachableWorkspaceProfile profile = ReachableWorkspaceProfileLoader.LoadDefault();
        ReachableWorkspaceModeProfile modeProfile;
        if ( !profile.TryGetWorkspace( mode, out modeProfile ) ) {
          message = "Workspace profile does not contain mode: " + mode;
          return false;
        }

        Vector3[] volumeAnchors = modeProfile.VolumeAnchorPoints;
        if ( volumeAnchors.Length != VolumeAnchorCount ) {
          message = "Workspace mode " + mode + " has " + volumeAnchors.Length
            + " volume anchors; expected " + VolumeAnchorCount + ".";
          return false;
        }

        m_isApplyingWorkspaceProfile = true;
        localVolumeAnchorPoints = volumeAnchors;
        m_isApplyingWorkspaceProfile = false;

        if ( updateVisuals ) {
          PushVolumeAnchorsToEditableObjects();
          RebuildVisual();
        }
        else {
          m_needsRebuild = true;
        }

        message = "Loaded mode " + mode + " from " + ReachableWorkspaceProfileLoader.DefaultWorkspacePath();
        return true;
      }
      catch ( Exception exception ) {
        m_isApplyingWorkspaceProfile = false;
        message = exception.Message;
        return false;
      }
    }

    public bool TrySaveToWorkspaceProfile( out string message )
    {
      message = string.Empty;
      string mode = EffectiveWorkspaceMode();
      if ( string.IsNullOrEmpty( mode ) ) {
        message = "Workspace mode is empty; cannot save reachable workspace profile.";
        return false;
      }

      EnsureVolumeAnchorArray();
      if ( localVolumeAnchorPoints == null || localVolumeAnchorPoints.Length != VolumeAnchorCount ) {
        message = "Workspace mode " + mode + " must contain " + VolumeAnchorCount + " volume anchors before saving.";
        return false;
      }

      try {
        string path = ReachableWorkspaceProfileLoader.DefaultWorkspacePath();
        ReachableWorkspaceProfileLoader.SaveModeToPath( path, mode, localVolumeAnchorPoints );
        message = "Saved mode " + mode + " to " + path;
        return true;
      }
      catch ( Exception exception ) {
        message = exception.Message;
        return false;
      }
    }

    public void CopyCalibrationFrom( ReachableWorkspaceVisualizer source )
    {
      if ( source == null )
        return;

      frameRegistry = source.frameRegistry;
      useFrameRegistryMachineRoot = source.useFrameRegistryMachineRoot;
      workspaceFrame = source.EffectiveWorkspaceFrame != null ? source.EffectiveWorkspaceFrame : source.transform;
      localVolumeAnchorPoints = source.localVolumeAnchorPoints != null ? (Vector3[])source.localVolumeAnchorPoints.Clone() : DefaultVolumeAnchors();
      showVolume = source.showVolume;
      volumeSectionArcSegments = source.volumeSectionArcSegments;
      volumeSectionArcBulge = source.volumeSectionArcBulge;
      volumeDirectionArcSegments = source.volumeDirectionArcSegments;
      volumeColor = source.volumeColor;
      drawGizmos = source.drawGizmos;
      drawAnchorSpheres = source.drawAnchorSpheres;
      drawVolumeBounds = source.drawVolumeBounds;
      anchorColor = source.anchorColor;
      volumeBoundaryColor = source.volumeBoundaryColor;
      volumeBoundaryLineWidth = source.volumeBoundaryLineWidth;
      anchorRadius = source.anchorRadius;
      editableAnchorVisualRadius = source.editableAnchorVisualRadius;
      editableAnchorColor = source.editableAnchorColor;
      selectedVolumeAnchorIndex = source.selectedVolumeAnchorIndex;
      selectedVolumeAnchorColor = source.selectedVolumeAnchorColor;
      selectedVolumeAnchorVisualRadius = source.selectedVolumeAnchorVisualRadius;
      closestPointResolution = source.closestPointResolution;
      RebuildVisual();
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

      HighlightSelectedVolumeAnchor();
    }

    [ContextMenu("Bake Volume Anchors From Editable Objects")]
    public void BakeVolumeAnchorsFromEditableObjects()
    {
      if ( !TrySyncVolumeAnchorsFromEditableObjects( out _ ) ) {
        Debug.LogWarning( "[ReachableWorkspaceVisualizer] Editable volume anchor objects are missing. Use 'Create Editable Volume Anchor Objects' first." );
        return;
      }

      RebuildVisual();
      MarkCalibrationDirty();
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
      showVolume = true;
      drawGizmos = true;
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
      drawGizmos = false;
      SetVolumeMeshVisible( false );
      SetVolumeAnchorControlsVisible( false );
      SetVolumeEdgesVisible( false );
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

      Transform frame = directionFrame != null ? directionFrame : ResolveWorkspaceFrame();
      Vector3 worldDirection = frame.TransformDirection( localDirection.normalized );
      if ( worldDirection.sqrMagnitude < 0.000001f )
        return false;

      anchor.position += worldDirection.normalized * distance;
      TrySyncVolumeAnchorsFromEditableObjects( out _ );
      HighlightSelectedVolumeAnchor();
      RebuildVisual();
      RebuildVolumeEdgeVisual();
      MarkCalibrationDirty();
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
      MarkCalibrationDirty();
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

    public string VolumeAnchorLocalSummary( int index )
    {
      EnsureVolumeAnchorArray();
      int clampedIndex = ClampVolumeAnchorIndex( index );
      Vector3 localPoint = localVolumeAnchorPoints[ clampedIndex ];
      return $"{VolumeAnchorDisplayName( clampedIndex )} {FormatVector( localPoint )}";
    }

    public void LogSelectedVolumeAnchorCoordinate()
    {
      Debug.Log( $"[ReachableWorkspaceVisualizer] {SelectedVolumeAnchorCoordinateSummary()}" );
    }

    public Vector3 SampleLocalVolume( float direction01, float height01, float depth01 )
    {
      EnsureVolumeAnchorArray();

      float directionScaled = Mathf.Clamp01( direction01 ) * ( DirectionCount - 1 );
      int directionIndex = Mathf.Clamp( Mathf.FloorToInt( directionScaled ), 0, DirectionCount - 2 );
      float directionT = directionScaled - directionIndex;
      if ( direction01 >= 1f ) {
        directionIndex = DirectionCount - 2;
        directionT = 1f;
      }

      Vector3 left = SampleVolumeSectionLinear( directionIndex, height01, depth01 );
      Vector3 right = SampleVolumeSectionLinear( directionIndex + 1, height01, depth01 );
      return Vector3.Lerp( left, right, directionT );
    }

    public Vector3 SampleWorldVolume( float direction01, float height01, float depth01 )
    {
      return LocalPointToWorld( SampleLocalVolume( direction01, height01, depth01 ) );
    }

    public Vector3 SampleWorldVolume( Vector3 parameters )
    {
      return SampleWorldVolume( parameters.x, parameters.y, parameters.z );
    }

    public Vector3 LocalPointToWorld( Vector3 localPoint )
    {
      Transform frame = ResolveWorkspaceFrame();
      return frame.TransformPoint( localPoint );
    }

    public Vector3 WorldPointToLocal( Vector3 worldPoint )
    {
      Transform frame = ResolveWorkspaceFrame();
      return frame.InverseTransformPoint( worldPoint );
    }

    public Vector3 ClosestWorldVolumeParameters( Vector3 worldPoint )
    {
      return ClosestLocalVolumeParameters( WorldPointToLocal( worldPoint ), closestPointResolution );
    }

    public Vector3 ClosestLocalVolumeParameters( Vector3 localPoint, int resolution )
    {
      EnsureVolumeAnchorArray();

      int steps = Mathf.Max( resolution, 2 );
      Vector3 bestParameters = Vector3.zero;
      Vector3 best = SampleLocalVolume( bestParameters.x, bestParameters.y, bestParameters.z );
      float bestDistance = ( best - localPoint ).sqrMagnitude;

      for ( int direction = 0; direction <= steps; direction++ ) {
        float direction01 = (float)direction / steps;
        for ( int height = 0; height <= steps; height++ ) {
          float height01 = (float)height / steps;
          for ( int depth = 0; depth <= steps; depth++ ) {
            float depth01 = (float)depth / steps;
            Vector3 candidate = SampleLocalVolume( direction01, height01, depth01 );
            float distance = ( candidate - localPoint ).sqrMagnitude;
            if ( distance >= bestDistance )
              continue;

            bestParameters = new Vector3( direction01, height01, depth01 );
            best = candidate;
            bestDistance = distance;
          }
        }
      }

      return bestParameters;
    }

    public bool ContainsWorldPointInVolume( Vector3 worldPoint )
    {
      return ContainsLocalPointInVolume( WorldPointToLocal( worldPoint ) );
    }

    public bool ContainsLocalPointInVolume( Vector3 localPoint )
    {
      if ( !LocalVolumeBounds().Contains( localPoint ) )
        return false;

      return IsLocalPointInsideLinearVolume( localPoint );
    }

    public Vector3 ClosestWorldPointInVolume( Vector3 worldPoint )
    {
      Vector3 localPoint = WorldPointToLocal( worldPoint );
      if ( ContainsLocalPointInVolume( localPoint ) )
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
      return TryGetWorldVolumeBounds( out bounds );
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

    private void EnsureVolumeObjects()
    {
      Transform visual = transform.Find( VolumeVisualObjectName );
      if ( visual == null ) {
        var visualObject = new GameObject( VolumeVisualObjectName );
        visualObject.transform.SetParent( transform, false );
        visual = visualObject.transform;
      }

      visual.gameObject.hideFlags = GeneratedVisualHideFlags;
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
      lineObject.hideFlags = GeneratedVisualHideFlags;
      lineObject.transform.SetParent( m_volumeEdgeRoot, false );

      var line = lineObject.AddComponent<LineRenderer>();
      line.useWorldSpace = true;
      line.positionCount = 2;
      line.SetPosition( 0, LocalPointToWorld( fromLocal ) );
      line.SetPosition( 1, LocalPointToWorld( toLocal ) );
      line.startColor = volumeBoundaryColor;
      line.endColor = volumeBoundaryColor;
      float width = Mathf.Max( volumeBoundaryLineWidth, 0.001f );
      line.startWidth = width;
      line.endWidth = width;
      line.numCapVertices = 2;
      line.numCornerVertices = 2;

      if ( m_volumeEdgeMaterial == null )
        m_volumeEdgeMaterial = CreateTransparentMaterial( "Reachable Workspace Edges", volumeBoundaryColor );
      if ( m_volumeEdgeMaterial != null )
        line.sharedMaterial = m_volumeEdgeMaterial;
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

    private void EnsureVolumeAnchorArray()
    {
      if ( localVolumeAnchorPoints != null && localVolumeAnchorPoints.Length == VolumeAnchorCount )
        return;

      localVolumeAnchorPoints = DefaultVolumeAnchors();
    }

    private Transform ResolveWorkspaceFrame()
    {
      ResolveFrameRegistryIfNeeded();

      if ( useFrameRegistryMachineRoot && frameRegistry != null && frameRegistry.MachineRoot != null )
        return frameRegistry.MachineRoot;

      return workspaceFrame != null ? workspaceFrame : transform;
    }

    private void ResolveFrameRegistryIfNeeded()
    {
      if ( frameRegistry == null )
        frameRegistry = ExcavatorFrameRegistry.FindNearest( this );

      if ( workspaceFrame == null && frameRegistry != null && frameRegistry.MachineRoot != null )
        workspaceFrame = frameRegistry.MachineRoot;
    }

    private Vector3 WorldToVisualLocal( Vector3 worldPoint )
    {
      return transform.InverseTransformPoint( worldPoint );
    }

    private void OnDrawGizmos()
    {
      if ( !drawGizmos )
        return;

      if ( drawVolumeBounds )
        DrawVolumeBounds();

      if ( drawAnchorSpheres )
        DrawVolumeAnchorSpheres();
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

    private void DrawVolumeAnchorSpheres()
    {
      EnsureVolumeAnchorArray();
      Gizmos.color = anchorColor;
      float radius = Mathf.Max( anchorRadius, 0.0001f );
      for ( int i = 0; i < VolumeAnchorCount; i++ )
        Gizmos.DrawSphere( LocalPointToWorld( localVolumeAnchorPoints[ i ] ), radius );
    }

    private void EnsureVolumeAnchorControlsRoot()
    {
      if ( m_volumeAnchorControlsRoot != null )
        return;

      Transform existing = transform.Find( VolumeAnchorControlsRootName );
      if ( existing != null ) {
        m_volumeAnchorControlsRoot = existing;
        return;
      }

      var rootObject = new GameObject( VolumeAnchorControlsRootName );
      rootObject.transform.SetParent( transform, false );
      m_volumeAnchorControlsRoot = rootObject.transform;
    }

    private void SetVolumeAnchorControlsVisible( bool visible )
    {
      EnsureVolumeAnchorControlsRoot();
      if ( m_volumeAnchorControlsRoot != null )
        m_volumeAnchorControlsRoot.gameObject.SetActive( visible );
    }

    private void SetVolumeMeshVisible( bool visible )
    {
      if ( m_volumeMeshRenderer == null ) {
        Transform visual = transform.Find( VolumeVisualObjectName );
        if ( visual != null )
          m_volumeMeshRenderer = visual.GetComponent<MeshRenderer>();
      }

      if ( m_volumeMeshRenderer != null )
        m_volumeMeshRenderer.enabled = visible;
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
      rootObject.hideFlags = GeneratedVisualHideFlags;
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

    private bool TrySyncVolumeAnchorsFromEditableObjects( out bool changed )
    {
      EnsureVolumeAnchorArray();
      changed = false;
      Transform root = m_volumeAnchorControlsRoot != null ? m_volumeAnchorControlsRoot : transform.Find( VolumeAnchorControlsRootName );
      if ( root == null )
        return false;

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

      return true;
    }

    private void MarkCalibrationDirty()
    {
#if UNITY_EDITOR
      if ( useWorkspaceProfile && saveWorkspaceProfileChanges && !m_isApplyingWorkspaceProfile ) {
        if ( Application.isPlaying ) {
          ReachableWorkspacePlayModeSaver.Queue( this );
        }
        else {
          string saveMessage;
          if ( !TrySaveToWorkspaceProfile( out saveMessage ) )
            Debug.LogWarning( "[ReachableWorkspace] " + saveMessage, this );
        }
      }

      UnityEditor.EditorUtility.SetDirty( this );
      if ( !Application.isPlaying && gameObject.scene.IsValid() )
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty( gameObject.scene );
#endif
    }

    private void ApplyWorkspaceProfileIfEnabled()
    {
      ApplyWorkspaceProfileIfEnabled( true );
    }

    private void ApplyWorkspaceProfileIfEnabled( bool updateVisuals )
    {
      if ( !useWorkspaceProfile || m_isApplyingWorkspaceProfile )
        return;

      string message;
      if ( TryLoadFromWorkspaceProfile( out message, updateVisuals ) ) {
        m_profileWarningShown = false;
        return;
      }

      if ( m_profileWarningShown )
        return;

      Debug.LogWarning( "[ReachableWorkspace] " + message, this );
      m_profileWarningShown = true;
    }

    private string EffectiveWorkspaceMode()
    {
      if ( !string.IsNullOrEmpty( workspaceMode ) )
        return workspaceMode;

      string lowerName = name.ToLowerInvariant();
      if ( lowerName.Contains( "carrymaterial" ) || lowerName.Contains( "carry" ) )
        return "CarryMaterial";
      if ( lowerName.Contains( "movetodig" ) || lowerName.Contains( "dig" ) )
        return "MoveToDig";

      return string.Empty;
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

    private void RefreshExistingVolumeAnchorVisuals()
    {
      Transform root = m_volumeAnchorControlsRoot != null ? m_volumeAnchorControlsRoot : transform.Find( VolumeAnchorControlsRootName );
      if ( root == null )
        return;

      selectedVolumeAnchorIndex = ClampVolumeAnchorIndex( selectedVolumeAnchorIndex );
      for ( int i = 0; i < VolumeAnchorCount; i++ ) {
        Transform anchor = root.Find( VolumeAnchorObjectName( i ) );
        if ( anchor == null )
          continue;

        var visual = anchor.GetComponent<TargetPointVisual>();
        if ( visual == null )
          continue;

        bool selected = i == selectedVolumeAnchorIndex;
        visual.SetStyle(
          selected ? selectedVolumeAnchorColor : editableAnchorColor,
          selected ? selectedVolumeAnchorVisualRadius : editableAnchorVisualRadius );
        visual.SetVisible( root.gameObject.activeSelf );
      }
    }

    private static int ClampVolumeAnchorIndex( int index )
    {
      int wrapped = index % VolumeAnchorCount;
      return wrapped < 0 ? wrapped + VolumeAnchorCount : wrapped;
    }

    private static string FormatVector( Vector3 value )
    {
      return $"({value.x:0.00}, {value.y:0.00}, {value.z:0.00})";
    }

    private void DestroyGeneratedVisualObjects()
    {
      DestroyGeneratedVisualObject( VolumeVisualObjectName );
      DestroyGeneratedVisualObject( VolumeEdgeObjectName );
      m_volumeMeshFilter = null;
      m_volumeMeshRenderer = null;
      m_volumeEdgeRoot = null;
    }

    private void DestroyGeneratedVisualObject( string objectName )
    {
      Transform generated = transform.Find( objectName );
      if ( generated != null )
        DestroyRuntimeObject( generated.gameObject );
    }

    private static void DestroyRuntimeObject( UnityEngine.Object target )
    {
      if ( target == null )
        return;

      if ( Application.isPlaying )
        Destroy( target );
      else
        DestroyImmediate( target );
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

    private Vector3 SampleVolumeSectionLinear( int direction, float height01, float depth01 )
    {
      int clampedDirection = Mathf.Clamp( direction, 0, DirectionCount - 1 );
      int start = clampedDirection * VolumeCornerCount;
      Vector3 highFar = localVolumeAnchorPoints[ start ];
      Vector3 lowFar = localVolumeAnchorPoints[ start + 1 ];
      Vector3 highNear = localVolumeAnchorPoints[ start + 2 ];
      Vector3 lowNear = localVolumeAnchorPoints[ start + 3 ];
      Vector3 high = Vector3.Lerp( highNear, highFar, Mathf.Clamp01( depth01 ) );
      Vector3 low = Vector3.Lerp( lowNear, lowFar, Mathf.Clamp01( depth01 ) );
      return Vector3.Lerp( high, low, Mathf.Clamp01( height01 ) );
    }

    private bool IsLocalPointInsideLinearVolume( Vector3 point )
    {
      EnsureVolumeAnchorArray();

      for ( int direction = 0; direction < DirectionCount - 1; direction++ ) {
        int left = direction * VolumeCornerCount;
        int right = ( direction + 1 ) * VolumeCornerCount;

        Vector3 v000 = localVolumeAnchorPoints[ left + 2 ];
        Vector3 v001 = localVolumeAnchorPoints[ left ];
        Vector3 v010 = localVolumeAnchorPoints[ left + 3 ];
        Vector3 v011 = localVolumeAnchorPoints[ left + 1 ];
        Vector3 v100 = localVolumeAnchorPoints[ right + 2 ];
        Vector3 v101 = localVolumeAnchorPoints[ right ];
        Vector3 v110 = localVolumeAnchorPoints[ right + 3 ];
        Vector3 v111 = localVolumeAnchorPoints[ right + 1 ];

        if (
          IsPointInsideTetrahedron( point, v000, v001, v011, v111 ) ||
          IsPointInsideTetrahedron( point, v000, v011, v010, v111 ) ||
          IsPointInsideTetrahedron( point, v000, v010, v110, v111 ) ||
          IsPointInsideTetrahedron( point, v000, v110, v100, v111 ) ||
          IsPointInsideTetrahedron( point, v000, v100, v101, v111 ) ||
          IsPointInsideTetrahedron( point, v000, v101, v001, v111 ) )
          return true;
      }

      return false;
    }

    private static bool IsPointInsideTetrahedron( Vector3 point, Vector3 a, Vector3 b, Vector3 c, Vector3 d )
    {
      const float epsilon = 0.0001f;
      float volume = SignedTetrahedronVolume( a, b, c, d );
      if ( Mathf.Abs( volume ) < 0.000001f )
        return false;

      float wa = SignedTetrahedronVolume( point, b, c, d ) / volume;
      float wb = SignedTetrahedronVolume( a, point, c, d ) / volume;
      float wc = SignedTetrahedronVolume( a, b, point, d ) / volume;
      float wd = SignedTetrahedronVolume( a, b, c, point ) / volume;

      return wa >= -epsilon && wb >= -epsilon && wc >= -epsilon && wd >= -epsilon
        && wa <= 1f + epsilon && wb <= 1f + epsilon && wc <= 1f + epsilon && wd <= 1f + epsilon;
    }

    private static float SignedTetrahedronVolume( Vector3 a, Vector3 b, Vector3 c, Vector3 d )
    {
      return Vector3.Dot( b - a, Vector3.Cross( c - a, d - a ) ) / 6f;
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
      var triangles = new List<int>();

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

    private static void AddQuad( List<int> triangles, int a, int b, int c, int d )
    {
      triangles.Add( a );
      triangles.Add( b );
      triangles.Add( c );
      triangles.Add( b );
      triangles.Add( d );
      triangles.Add( c );
    }

    private Vector3 ClosestLocalPointOnVolumeMesh( Vector3 point )
    {
      EnsureVolumeAnchorArray();
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

    public static Vector3[] Env01VolumeAnchors()
    {
      return DefaultVolumeAnchors();
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
