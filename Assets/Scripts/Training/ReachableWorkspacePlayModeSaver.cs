using UnityEngine;

namespace RLExcavator
{
#if UNITY_EDITOR
  [UnityEditor.InitializeOnLoad]
  internal static class ReachableWorkspacePlayModeSaver
  {
    private const string PendingKey = "RLExcavator.PendingReachableWorkspaceChanges";

    static ReachableWorkspacePlayModeSaver()
    {
      UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
      UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    public static void Queue( ReachableWorkspaceVisualizer workspace )
    {
      if ( workspace == null || workspace.localVolumeAnchorPoints == null )
        return;

      if ( workspace.useWorkspaceProfile && workspace.saveWorkspaceProfileChanges ) {
        string saveMessage;
        if ( workspace.TrySaveToWorkspaceProfile( out saveMessage ) )
          Debug.Log( "[ReachableWorkspacePlayModeSaver] " + saveMessage );
        else
          Debug.LogWarning( "[ReachableWorkspacePlayModeSaver] " + saveMessage );
      }

      PendingWorkspaceStore store = Load();
      string scenePath = workspace.gameObject.scene.path;
      string hierarchyPath = HierarchyPath( workspace.transform );

      for ( int i = store.changes.Count - 1; i >= 0; i-- ) {
        PendingWorkspaceChange existing = store.changes[ i ];
        if ( existing.scenePath == scenePath && existing.hierarchyPath == hierarchyPath )
          store.changes.RemoveAt( i );
      }

      store.changes.Add( new PendingWorkspaceChange {
        scenePath = scenePath,
        hierarchyPath = hierarchyPath,
        selectedVolumeAnchorIndex = workspace.selectedVolumeAnchorIndex,
        localVolumeAnchorPoints = (Vector3[])workspace.localVolumeAnchorPoints.Clone()
      } );

      Save( store );
      Debug.Log( $"[ReachableWorkspacePlayModeSaver] Queued workspace change for '{hierarchyPath}'. It will be applied after exiting Play Mode." );
    }

    private static void OnPlayModeStateChanged( UnityEditor.PlayModeStateChange state )
    {
      if ( state == UnityEditor.PlayModeStateChange.EnteredEditMode )
        ApplyPendingChanges();
    }

    private static void ApplyPendingChanges()
    {
      PendingWorkspaceStore store = Load();
      if ( store.changes == null || store.changes.Count == 0 )
        return;

      int applied = 0;
      foreach ( PendingWorkspaceChange change in store.changes ) {
        UnityEngine.SceneManagement.Scene scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath( change.scenePath );
        if ( !scene.IsValid() || !scene.isLoaded ) {
          Debug.LogWarning( $"[ReachableWorkspacePlayModeSaver] Scene is not loaded: {change.scenePath}" );
          continue;
        }

        Transform target = FindTransformByPath( scene, change.hierarchyPath );
        if ( target == null ) {
          Debug.LogWarning( $"[ReachableWorkspacePlayModeSaver] Cannot find workspace: {change.hierarchyPath}" );
          continue;
        }

        ReachableWorkspaceVisualizer workspace = target.GetComponent<ReachableWorkspaceVisualizer>();
        if ( workspace == null ) {
          Debug.LogWarning( $"[ReachableWorkspacePlayModeSaver] Missing ReachableWorkspaceVisualizer on: {change.hierarchyPath}" );
          continue;
        }

        workspace.localVolumeAnchorPoints = change.localVolumeAnchorPoints != null
          ? (Vector3[])change.localVolumeAnchorPoints.Clone()
          : workspace.localVolumeAnchorPoints;
        workspace.selectedVolumeAnchorIndex = change.selectedVolumeAnchorIndex;
        workspace.RebuildVisual();

        UnityEditor.EditorUtility.SetDirty( workspace );
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty( scene );
        applied++;
      }

      UnityEditor.EditorPrefs.DeleteKey( PendingKey );
      if ( applied > 0 )
        Debug.Log( $"[ReachableWorkspacePlayModeSaver] Applied {applied} reachable workspace change(s) after Play Mode. Save the scene to persist them on disk." );
    }

    private static Transform FindTransformByPath( UnityEngine.SceneManagement.Scene scene, string hierarchyPath )
    {
      if ( string.IsNullOrEmpty( hierarchyPath ) )
        return null;

      string[] parts = hierarchyPath.Split( '/' );
      if ( parts.Length == 0 )
        return null;

      GameObject[] roots = scene.GetRootGameObjects();
      Transform current = null;
      for ( int i = 0; i < roots.Length; i++ ) {
        if ( roots[ i ].name == parts[ 0 ] ) {
          current = roots[ i ].transform;
          break;
        }
      }

      for ( int i = 1; current != null && i < parts.Length; i++ )
        current = current.Find( parts[ i ] );

      return current;
    }

    private static string HierarchyPath( Transform transform )
    {
      if ( transform == null )
        return string.Empty;

      var names = new System.Collections.Generic.List<string>();
      Transform current = transform;
      while ( current != null ) {
        names.Add( current.name );
        current = current.parent;
      }

      names.Reverse();
      return string.Join( "/", names );
    }

    private static PendingWorkspaceStore Load()
    {
      string json = UnityEditor.EditorPrefs.GetString( PendingKey, string.Empty );
      if ( string.IsNullOrEmpty( json ) )
        return new PendingWorkspaceStore();

      PendingWorkspaceStore store = JsonUtility.FromJson<PendingWorkspaceStore>( json );
      return store != null ? store : new PendingWorkspaceStore();
    }

    private static void Save( PendingWorkspaceStore store )
    {
      UnityEditor.EditorPrefs.SetString( PendingKey, JsonUtility.ToJson( store ) );
    }

    [System.Serializable]
    private class PendingWorkspaceStore
    {
      public System.Collections.Generic.List<PendingWorkspaceChange> changes = new System.Collections.Generic.List<PendingWorkspaceChange>();
    }

    [System.Serializable]
    private class PendingWorkspaceChange
    {
      public string scenePath;
      public string hierarchyPath;
      public int selectedVolumeAnchorIndex;
      public Vector3[] localVolumeAnchorPoints;
    }
  }
#endif
}
