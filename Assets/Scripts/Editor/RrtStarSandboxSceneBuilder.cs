using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RLExcavator.Editor
{
  public static class RrtStarSandboxSceneBuilder
  {
    private const string ScenePath = "Assets/Scenes/RrtStarSandboxScene.unity";
    private const string ObstacleLayerName = RrtStarSandboxController.ObstacleLayerName;

    [MenuItem( "RLExcavator/RRT*/Create Simple Sandbox Scene" )]
    public static void CreateScene()
    {
      CreateSceneInternal( true );
    }

    public static void CreateSceneBatch()
    {
      CreateSceneInternal( true );
    }

    private static void CreateSceneInternal( bool saveScene )
    {
      int obstacleLayer = EnsureLayer( ObstacleLayerName );
      Scene scene = EditorSceneManager.NewScene( NewSceneSetup.EmptyScene, NewSceneMode.Single );
      scene.name = "RrtStarSandboxScene";

      CreateCamera();
      CreateGround();

      var system = new GameObject( "RrtStarSandboxSystem" );
      var controller = system.AddComponent<RrtStarSandboxController>();
      controller.boundsCenter = new Vector3( 0f, 2.5f, 0f );
      controller.boundsSize = new Vector3( 28f, 5f, 18f );
      controller.obstacleMask = 1 << obstacleLayer;
      controller.autoPlanOnStart = true;
      controller.showSearchTree = false;
      controller.maxSearchTreeEdges = 320;
      controller.showRawPath = true;
      controller.showSmoothedPath = true;
      controller.showBounds = true;
      controller.settings.maxIterations = 3500;
      controller.settings.stepSize = 1.1f;
      controller.settings.goalBias = 0.15f;
      controller.settings.goalTolerance = 1.1f;
      controller.settings.rewireRadius = 3.0f;
      controller.settings.collisionRadius = 0.32f;
      controller.settings.edgeCheckResolution = 10;
      controller.settings.smoothingPasses = 6;
      controller.settings.seed = 11;

      var endpointRoot = new GameObject( "ManualEndpoints" );

      controller.startPoint = CreateMarker( "StartPoint", new Vector3( -12f, 0.6f, -7f ), Color.green ).transform;
      controller.startPoint.SetParent( endpointRoot.transform, true );

      controller.goalPoint = CreateMarker( "GoalPoint", new Vector3( 12f, 0.6f, 7f ), Color.blue ).transform;
      controller.goalPoint.SetParent( endpointRoot.transform, true );

      var obstacleRoot = new GameObject( "StaticObstacles" );
      CreateWall( obstacleRoot.transform, obstacleLayer, "Wall_Left", new Vector3( -5.5f, 1.25f, -1.8f ), new Vector3( 1.0f, 2.5f, 10.0f ) );
      CreateWall( obstacleRoot.transform, obstacleLayer, "Wall_Right", new Vector3( 5.0f, 1.25f, 2.2f ), new Vector3( 1.0f, 2.5f, 9.0f ) );
      CreateWall( obstacleRoot.transform, obstacleLayer, "ShortWall", new Vector3( 0.0f, 1.25f, -5.0f ), new Vector3( 7.0f, 2.5f, 1.0f ) );
      CreateWall( obstacleRoot.transform, obstacleLayer, "OverpassTestWall", new Vector3( 0.0f, 1.15f, 0.0f ), new Vector3( 26.5f, 2.3f, 0.8f ) );
      CreateCylinder( obstacleRoot.transform, obstacleLayer, "Cylinder_A", new Vector3( -1.5f, 0f, 3.8f ), 1.2f, 2.5f );
      CreateCylinder( obstacleRoot.transform, obstacleLayer, "Cylinder_B", new Vector3( 8.5f, 0f, -3.5f ), 1.4f, 2.5f );

      var visuals = new GameObject( "RrtStarVisuals" );
      visuals.transform.SetParent( system.transform, false );

      Selection.activeObject = system;

      if ( saveScene ) {
        Directory.CreateDirectory( "Assets/Scenes" );
        EditorSceneManager.SaveScene( scene, ScenePath );
        AssetDatabase.SaveAssets();
        Debug.Log( $"Created {ScenePath}." );
      }
    }

    private static void CreateCamera()
    {
      var cameraObject = new GameObject( "Main Camera" );
      var camera = cameraObject.AddComponent<Camera>();
      cameraObject.tag = "MainCamera";
      cameraObject.transform.position = new Vector3( 0f, 22f, -18f );
      cameraObject.transform.rotation = Quaternion.Euler( 58f, 0f, 0f );
      camera.fieldOfView = 48f;
      camera.nearClipPlane = 0.1f;
      camera.farClipPlane = 100f;
      camera.clearFlags = CameraClearFlags.Skybox;
    }

    private static void CreateGround()
    {
      GameObject ground = GameObject.CreatePrimitive( PrimitiveType.Cube );
      ground.name = "GroundPlane";
      ground.transform.position = new Vector3( 0f, -0.05f, 0f );
      ground.transform.localScale = new Vector3( 30f, 0.1f, 20f );
      ApplyMaterial( ground.GetComponent<Renderer>(), new Color( 0.82f, 0.84f, 0.82f, 1f ) );
    }

    private static GameObject CreateMarker( string name, Vector3 position, Color color )
    {
      GameObject marker = GameObject.CreatePrimitive( PrimitiveType.Sphere );
      marker.name = name;
      marker.transform.position = position;
      marker.transform.localScale = Vector3.one * 0.7f;
      Object.DestroyImmediate( marker.GetComponent<Collider>() );
      ApplyMaterial( marker.GetComponent<Renderer>(), color );
      return marker;
    }

    private static void CreateWall( Transform parent, int layer, string name, Vector3 position, Vector3 scale )
    {
      GameObject wall = GameObject.CreatePrimitive( PrimitiveType.Cube );
      wall.name = name;
      wall.layer = layer;
      wall.transform.SetParent( parent, false );
      wall.transform.position = position;
      wall.transform.localScale = scale;
      ApplyMaterial( wall.GetComponent<Renderer>(), new Color( 0.54f, 0.58f, 0.62f, 1f ) );
    }

    private static void CreateCylinder( Transform parent, int layer, string name, Vector3 baseCenter, float radius, float height )
    {
      GameObject cylinder = GameObject.CreatePrimitive( PrimitiveType.Cylinder );
      cylinder.name = name;
      cylinder.layer = layer;
      cylinder.transform.SetParent( parent, false );
      cylinder.transform.position = baseCenter + Vector3.up * height * 0.5f;
      cylinder.transform.localScale = new Vector3( radius * 2f, height * 0.5f, radius * 2f );
      Object.DestroyImmediate( cylinder.GetComponent<Collider>() );
      cylinder.AddComponent<BoxCollider>();
      ApplyMaterial( cylinder.GetComponent<Renderer>(), new Color( 0.42f, 0.54f, 0.62f, 1f ) );
    }

    private static void ApplyMaterial( Renderer renderer, Color color )
    {
      if ( renderer == null )
        return;

      Shader shader = Shader.Find( "Universal Render Pipeline/Lit" );
      if ( shader == null )
        shader = Shader.Find( "Universal Render Pipeline/Unlit" );
      if ( shader == null )
        shader = Shader.Find( "Standard" );

      var material = new Material( shader ) {
        name = "RRT* Sandbox Material"
      };
      material.SetColor( "_BaseColor", color );
      material.SetColor( "_Color", color );
      renderer.sharedMaterial = material;
    }

    private static int EnsureLayer( string layerName )
    {
      int existingLayer = LayerMask.NameToLayer( layerName );
      if ( existingLayer >= 0 )
        return existingLayer;

      var tagManager = new SerializedObject( AssetDatabase.LoadAllAssetsAtPath( "ProjectSettings/TagManager.asset" )[ 0 ] );
      SerializedProperty layers = tagManager.FindProperty( "layers" );
      for ( int i = 8; i < layers.arraySize; i++ ) {
        SerializedProperty layer = layers.GetArrayElementAtIndex( i );
        if ( !string.IsNullOrEmpty( layer.stringValue ) )
          continue;

        layer.stringValue = layerName;
        tagManager.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        return i;
      }

      throw new System.InvalidOperationException( $"No free Unity user layer slot is available for '{layerName}'." );
    }
  }
}
