using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RLExcavator.Editor
{
  public class HierarchyMaterialAssignerWindow : EditorWindow
  {
    private GameObject m_root;
    private Material m_material;

    [MenuItem( "RLExcavator/Materials/Bulk Assign Material" )]
    public static void Open()
    {
      GetWindow<HierarchyMaterialAssignerWindow>( "Bulk Material" );
    }

    private void OnEnable()
    {
      UseSelection();
    }

    private void OnGUI()
    {
      EditorGUILayout.LabelField( "Assign Material To Hierarchy", EditorStyles.boldLabel );
      EditorGUILayout.HelpBox( "Drag a hierarchy root such as Body_Track, then drag the material to assign to every child Renderer.", MessageType.Info );

      m_root = (GameObject)EditorGUILayout.ObjectField( "Root", m_root, typeof( GameObject ), true );
      m_material = (Material)EditorGUILayout.ObjectField( "Material", m_material, typeof( Material ), false );

      EditorGUILayout.Space( 6f );
      using ( new EditorGUILayout.HorizontalScope() ) {
        if ( GUILayout.Button( "Use Selection" ) )
          UseSelection();

        using ( new EditorGUI.DisabledScope( m_root == null || m_material == null ) ) {
          int rendererCount = CountRenderers();
          if ( GUILayout.Button( $"Apply To {rendererCount} Renderers" ) )
            ApplyMaterial();
        }
      }
    }

    private void UseSelection()
    {
      GameObject selectedRoot = null;
      Material selectedMaterial = null;
      foreach ( Object selectedObject in Selection.objects ) {
        if ( selectedRoot == null && selectedObject is GameObject selectedGameObject )
          selectedRoot = selectedGameObject;
        else if ( selectedMaterial == null && selectedObject is Material material )
          selectedMaterial = material;
      }

      if ( selectedRoot != null )
        m_root = selectedRoot;
      if ( selectedMaterial != null )
        m_material = selectedMaterial;

      Repaint();
    }

    private int CountRenderers()
    {
      if ( m_root == null )
        return 0;

      return m_root.GetComponentsInChildren<Renderer>( true ).Length;
    }

    private void ApplyMaterial()
    {
      if ( m_root == null || m_material == null )
        return;

      Renderer[] renderers = m_root.GetComponentsInChildren<Renderer>( true );
      if ( renderers.Length == 0 ) {
        Debug.LogWarning( $"[HierarchyMaterialAssigner] No Renderer found under '{m_root.name}'." );
        return;
      }

      Undo.RecordObjects( renderers, "Bulk Assign Material" );

      int changedRendererCount = 0;
      int changedSlotCount = 0;
      foreach ( Renderer renderer in renderers ) {
        Material[] sharedMaterials = renderer.sharedMaterials;
        if ( sharedMaterials == null || sharedMaterials.Length == 0 )
          continue;

        bool changed = false;
        for ( int i = 0; i < sharedMaterials.Length; i++ ) {
          if ( sharedMaterials[ i ] == m_material )
            continue;

          sharedMaterials[ i ] = m_material;
          changed = true;
          changedSlotCount++;
        }

        if ( !changed )
          continue;

        renderer.sharedMaterials = sharedMaterials;
        if ( PrefabUtility.IsPartOfPrefabInstance( renderer ) )
          PrefabUtility.RecordPrefabInstancePropertyModifications( renderer );
        EditorUtility.SetDirty( renderer );
        changedRendererCount++;
      }

      if ( m_root.scene.IsValid() )
        EditorSceneManager.MarkSceneDirty( m_root.scene );
      else
        EditorUtility.SetDirty( m_root );

      Debug.Log( $"[HierarchyMaterialAssigner] Assigned '{m_material.name}' to {changedSlotCount} material slot(s) on {changedRendererCount} renderer(s) under '{m_root.name}'." );
    }
  }
}
