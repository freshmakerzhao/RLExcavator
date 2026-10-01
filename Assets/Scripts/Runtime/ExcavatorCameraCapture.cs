using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace RLExcavator
{
  /// <summary>On-demand RGB capture on the main thread; never advances physics.</summary>
  public static class ExcavatorCameraCapture
  {
    public const int Width = 640;
    public const int Height = 480;

    public static byte[] CapturePng( Camera camera )
    {
      if ( camera == null ) throw new InvalidOperationException( "Front camera is not assigned." );
      RenderTexture previous = RenderTexture.active;
      RenderTexture target = RenderTexture.GetTemporary( Width, Height, 24, RenderTextureFormat.ARGB32,
        RenderTextureReadWrite.sRGB );
      Texture2D pixels = new Texture2D( Width, Height, TextureFormat.RGB24, false );
      try {
        var request = new UniversalRenderPipeline.SingleCameraRequest { destination = target };
        RenderPipeline.SubmitRenderRequest( camera, request );
        RenderTexture.active = target;
        pixels.ReadPixels( new Rect( 0, 0, Width, Height ), 0, 0 );
        pixels.Apply();
        return pixels.EncodeToPNG();
      }
      finally {
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary( target );
        UnityEngine.Object.Destroy( pixels );
      }
    }
  }
}
