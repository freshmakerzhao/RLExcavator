using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RLExcavator.Editor
{
  [CustomEditor( typeof( ReachableWorkspaceVisualizer ) )]
  public class ReachableWorkspaceVisualizerEditor : UnityEditor.Editor
  {
    private SerializedProperty m_workspaceFrame;
    private SerializedProperty m_frameRegistry;
    private SerializedProperty m_useFrameRegistryMachineRoot;
    private SerializedProperty m_localVolumeAnchorPoints;
    private SerializedProperty m_selectedVolumeAnchorIndex;
    private SerializedProperty m_showVolume;
    private SerializedProperty m_drawGizmos;
    private SerializedProperty m_drawVolumeBounds;
    private SerializedProperty m_volumeSectionArcSegments;
    private SerializedProperty m_volumeSectionArcBulge;
    private SerializedProperty m_volumeDirectionArcSegments;
    private SerializedProperty m_volumeColor;
    private SerializedProperty m_volumeBoundaryColor;
    private SerializedProperty m_volumeBoundaryLineWidth;
    private SerializedProperty m_editableAnchorVisualRadius;
    private SerializedProperty m_selectedVolumeAnchorVisualRadius;
    private SerializedProperty m_closestPointResolution;

    private bool m_showVolumePoints = true;
    private bool m_showAdvanced;

    private void OnEnable()
    {
      m_workspaceFrame = serializedObject.FindProperty( "workspaceFrame" );
      m_frameRegistry = serializedObject.FindProperty( "frameRegistry" );
      m_useFrameRegistryMachineRoot = serializedObject.FindProperty( "useFrameRegistryMachineRoot" );
      m_localVolumeAnchorPoints = serializedObject.FindProperty( "localVolumeAnchorPoints" );
      m_selectedVolumeAnchorIndex = serializedObject.FindProperty( "selectedVolumeAnchorIndex" );
      m_showVolume = serializedObject.FindProperty( "showVolume" );
      m_drawGizmos = serializedObject.FindProperty( "drawGizmos" );
      m_drawVolumeBounds = serializedObject.FindProperty( "drawVolumeBounds" );
      m_volumeSectionArcSegments = serializedObject.FindProperty( "volumeSectionArcSegments" );
      m_volumeSectionArcBulge = serializedObject.FindProperty( "volumeSectionArcBulge" );
      m_volumeDirectionArcSegments = serializedObject.FindProperty( "volumeDirectionArcSegments" );
      m_volumeColor = serializedObject.FindProperty( "volumeColor" );
      m_volumeBoundaryColor = serializedObject.FindProperty( "volumeBoundaryColor" );
      m_volumeBoundaryLineWidth = serializedObject.FindProperty( "volumeBoundaryLineWidth" );
      m_editableAnchorVisualRadius = serializedObject.FindProperty( "editableAnchorVisualRadius" );
      m_selectedVolumeAnchorVisualRadius = serializedObject.FindProperty( "selectedVolumeAnchorVisualRadius" );
      m_closestPointResolution = serializedObject.FindProperty( "closestPointResolution" );
    }

    public override void OnInspectorGUI()
    {
      serializedObject.Update();
      var workspace = (ReachableWorkspaceVisualizer)target;

      EditorGUILayout.LabelField( "必要绑定", EditorStyles.boldLabel );
      EditorGUILayout.PropertyField( m_frameRegistry, new GUIContent( "Frame Registry" ) );
      EditorGUILayout.PropertyField( m_useFrameRegistryMachineRoot, new GUIContent( "使用 MachineRoot" ) );
      EditorGUILayout.PropertyField( m_workspaceFrame, new GUIContent( "可达区域坐标系" ) );

      EditorGUILayout.Space( 6f );
      EditorGUILayout.LabelField( "20 点标定", EditorStyles.boldLabel );
      EditorGUILayout.HelpBox( "顺序为 D0-D4，每个方向 4 点：高远、低远、高近、低近。", MessageType.Info );
      EditorGUILayout.PropertyField( m_selectedVolumeAnchorIndex, new GUIContent( "当前点" ) );
      bool changed = serializedObject.ApplyModifiedProperties();
      EditorGUILayout.LabelField( "当前点坐标", workspace.SelectedVolumeAnchorLocalSummary() );

      DrawWorkflowButtons( workspace );

      serializedObject.Update();
      m_showVolumePoints = EditorGUILayout.Foldout( m_showVolumePoints, "20 点数组", true );
      if ( m_showVolumePoints )
        EditorGUILayout.PropertyField( m_localVolumeAnchorPoints, new GUIContent( "Local Volume Anchor Points" ), true );

      m_showAdvanced = EditorGUILayout.Foldout( m_showAdvanced, "显示与采样设置", true );
      if ( m_showAdvanced ) {
        EditorGUILayout.PropertyField( m_showVolume, new GUIContent( "显示体积" ) );
        EditorGUILayout.PropertyField( m_drawGizmos, new GUIContent( "绘制 Gizmos" ) );
        EditorGUILayout.PropertyField( m_drawVolumeBounds, new GUIContent( "绘制边界线" ) );
        EditorGUILayout.PropertyField( m_volumeSectionArcSegments, new GUIContent( "截面细分" ) );
        EditorGUILayout.PropertyField( m_volumeSectionArcBulge, new GUIContent( "截面外鼓" ) );
        EditorGUILayout.PropertyField( m_volumeDirectionArcSegments, new GUIContent( "方向细分" ) );
        EditorGUILayout.PropertyField( m_volumeColor, new GUIContent( "体积颜色" ) );
        EditorGUILayout.PropertyField( m_volumeBoundaryColor, new GUIContent( "边界颜色" ) );
        EditorGUILayout.PropertyField( m_volumeBoundaryLineWidth, new GUIContent( "边界线宽" ) );
        EditorGUILayout.PropertyField( m_editableAnchorVisualRadius, new GUIContent( "普通点半径" ) );
        EditorGUILayout.PropertyField( m_selectedVolumeAnchorVisualRadius, new GUIContent( "选中点半径" ) );
        EditorGUILayout.PropertyField( m_closestPointResolution, new GUIContent( "最近点采样" ) );
        if ( GUILayout.Button( "重置为默认体积标定" ) && EditorUtility.DisplayDialog( "重置可达区域", "将 20 点恢复为内置默认体积标定值。确定继续？", "确定", "取消" ) )
          RunAction( workspace, "Reset Workspace Calibration", workspace.ResetAnchorsToEnv01Calibration );
      }

      if ( serializedObject.ApplyModifiedProperties() || changed ) {
        workspace.RebuildVisual();
        MarkDirty( workspace );
      }
    }

    private static void DrawWorkflowButtons( ReachableWorkspaceVisualizer workspace )
    {
      EditorGUILayout.Space( 4f );
      using ( new EditorGUILayout.HorizontalScope() ) {
        if ( GUILayout.Button( "显示 20 点" ) )
          RunAction( workspace, "Show Reachable Workspace", workspace.ShowReachableVolumeAndBoundaryPoints );
        if ( GUILayout.Button( "隐藏" ) )
          RunAction( workspace, "Hide Reachable Workspace", workspace.HideReachableVolumeAndBoundaryPoints );
      }

      using ( new EditorGUILayout.HorizontalScope() ) {
        if ( GUILayout.Button( "上一个点" ) )
          RunAction( workspace, "Select Previous Workspace Point", workspace.SelectPreviousVolumeAnchor );
        if ( GUILayout.Button( "下一个点" ) )
          RunAction( workspace, "Select Next Workspace Point", workspace.SelectNextVolumeAnchor );
      }

      using ( new EditorGUILayout.HorizontalScope() ) {
        if ( GUILayout.Button( "创建 20 点对象" ) )
          RunAction( workspace, "Create Workspace Point Objects", workspace.CreateEditableVolumeAnchorObjects );
        if ( GUILayout.Button( "Bake 20 点" ) )
          RunAction( workspace, "Bake Workspace Points", workspace.BakeVolumeAnchorsFromEditableObjects );
      }

      using ( new EditorGUILayout.HorizontalScope() ) {
        if ( GUILayout.Button( "数组推到对象" ) )
          RunAction( workspace, "Push Workspace Points", workspace.PushVolumeAnchorsToEditableObjects );
      }
    }

    private static void RunAction( ReachableWorkspaceVisualizer workspace, string undoName, System.Action action )
    {
      if ( workspace == null || action == null )
        return;

      Undo.RegisterFullObjectHierarchyUndo( workspace.gameObject, undoName );
      Undo.RecordObject( workspace, undoName );
      action();
      MarkDirty( workspace );
      SceneView.RepaintAll();
    }

    private static void MarkDirty( ReachableWorkspaceVisualizer workspace )
    {
      if ( workspace == null )
        return;

      EditorUtility.SetDirty( workspace );
      if ( workspace.gameObject.scene.IsValid() )
        EditorSceneManager.MarkSceneDirty( workspace.gameObject.scene );
    }
  }
}
