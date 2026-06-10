using System.Collections;
using UnityEngine;

namespace RLExcavator
{
  /// <summary>
  /// Shared visual success feedback. Assign a floor/platform renderer and call Show().
  /// </summary>
  public class SuccessIndicator : MonoBehaviour
  {
    [Tooltip("成功时短暂变色的地板/平台 Renderer")]
    public Renderer targetRenderer;
    public Color successColor = new Color( 0.1f, 0.85f, 0.25f, 1f );
    public float durationSeconds = 1.0f;

    private Coroutine m_routine = null;

    public void Show()
    {
      if ( targetRenderer == null )
        return;

      if ( m_routine != null )
        StopCoroutine( m_routine );

      m_routine = StartCoroutine( ShowRoutine() );
    }

    private IEnumerator ShowRoutine()
    {
      var block = new MaterialPropertyBlock();
      targetRenderer.GetPropertyBlock( block );
      block.SetColor( "_BaseColor", successColor );
      block.SetColor( "_Color", successColor );
      targetRenderer.SetPropertyBlock( block );

      yield return new WaitForSeconds( Mathf.Max( durationSeconds, 0.01f ) );

      targetRenderer.SetPropertyBlock( null );
      m_routine = null;
    }
  }
}
