using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;

namespace RLExcavator.Tests
{
  public class E4ReplayBuildTests
  {
    private bool CanRestore(SceneSetup[] setup)
    {
      Type type = AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType("RLExcavator.Editor.E4HistoricalReplayBuild")).First(t => t != null);
      MethodInfo method = type.GetMethod("CanRestoreSceneSetup");
      Assert.That(method, Is.Not.Null, "Build must guard invalid batchmode scene setup");
      return (bool)method.Invoke(null, new object[] { setup });
    }

    [Test]
    public void EmptyBatchSetupIsNotRestored() => Assert.That(CanRestore(new SceneSetup[0]), Is.False);

    [Test]
    public void UnloadedSetupIsNotRestored() => Assert.That(CanRestore(new[] {
      new SceneSetup { path = "Assets/Scenes/Example.unity", isLoaded = false, isActive = false }
    }), Is.False);

    [Test]
    public void UnsavedEmptySceneUsesNewSceneFallback() => Assert.That(CanRestore(new[] {
      new SceneSetup { path = "", isLoaded = true, isActive = true }
    }), Is.False);

    [Test]
    public void SavedLoadedActiveSceneCanBeRestored() => Assert.That(CanRestore(new[] {
      new SceneSetup { path = "Assets/Scenes/Example.unity", isLoaded = true, isActive = true }
    }), Is.True);
  }
}
