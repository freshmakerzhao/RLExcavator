using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RLExcavator.Tests
{
  public class ExcavatorRuntimeTerrainMigrationTests
  {
    [Test]
    public void FixedSoilKeepsMassUpdatesAndNewSideGeometryDisabled()
    {
      // 检查实际运行场景，防止 SDK 默认值让固定料池每步空引用或改变 ACT 图像。
      var scene = EditorSceneManager.OpenPreviewScene( "Assets/Scenes/ExcavatorRuntime.unity" );
      try {
        var type = Type.GetType( "AGXUnity.Model.MovableTerrain, AGXUnity" );
        Assert.That( type, Is.Not.Null );
        var terrain = scene.GetRootGameObjects()
          .SelectMany( root => root.GetComponentsInChildren( type, true ) )
          .Single( component => component.name == "SoilTerrain" );
        Assert.That( type.GetProperty( "RigidBody" ).GetValue( terrain ), Is.Null );
        Assert.That( type.GetProperty( "EnableDynamicMassUpdates" ).GetValue( terrain ), Is.False );
        Assert.That( type.GetProperty( "RenderSides" ).GetValue( terrain ), Is.False );
        var cells = (Vector2Int)type.GetProperty( "SizeCells" ).GetValue( terrain );
        Assert.That( terrain.GetComponent<MeshFilter>().sharedMesh.vertexCount,
                     Is.EqualTo( cells.x * cells.y ) );
      }
      finally { EditorSceneManager.ClosePreviewScene( scene ); }
    }
  }
}
