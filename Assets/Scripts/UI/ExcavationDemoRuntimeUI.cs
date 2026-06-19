using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace RLExcavator
{
  /// <summary>
  /// Runtime HUD for the interactive excavation demo.
  /// This script only builds its own UI and calls ExcavationDemoTaskManager methods.
  /// </summary>
  public class ExcavationDemoRuntimeUI : MonoBehaviour
  {
    private enum DemoTab
    {
      PerceptionMapping = 0,
      TrajectoryPlanning = 1,
      TrajectoryTracking = 2
    }

    [Header("References")]
    public ExcavationDemoTaskManager taskManager;
    public Camera minimapCamera;
    public Texture minimapFallbackTexture;

    [Header("Runtime build")]
    public bool buildOnStart = true;
    public bool createEventSystemIfMissing = true;
    public bool assignStatusTextToTaskManager = true;

    [Header("Header")]
    public string systemTitle = "算法验证";
    public string organizationText = "单位 / Logo";
    public string headerRightText = "矿山无人挖装算法验证平台";

    [Header("Layout")]
    public Vector2 referenceResolution = new Vector2( 1920f, 1080f );
    public Vector2 leftMenuSize = new Vector2( 250f, 520f );
    public Vector2 monitorSize = new Vector2( 400f, 270f );
    public int fontSize = 18;

    [Header("Minimap")]
    public Vector2Int minimapTextureSize = new Vector2Int( 800, 480 );

    private readonly Button[] m_tabButtons = new Button[ 3 ];
    private readonly Image[] m_tabImages = new Image[ 3 ];
    private readonly Outline[] m_tabOutlines = new Outline[ 3 ];

    private GameObject m_root;
    private RectTransform m_leftMenuPanel;
    private RectTransform m_leftMenuContent;
    private Text m_leftMenuTitle;
    private Text m_statusText;
    private Font m_font;
    private RenderTexture m_minimapRenderTexture;
    private DemoTab m_activeTab = DemoTab.TrajectoryTracking;

    private const float kMenuRowStep = 42f;
    private const float kMenuButtonHeight = 34f;
    private const float kMenuStatusHeight = 158f;
    private const float kMenuChromeHeight = 86f;

    private static readonly Color s_panelColor = new Color( 0.025f, 0.075f, 0.12f, 0.82f );
    private static readonly Color s_panelBorderColor = new Color( 0.10f, 0.68f, 1.0f, 0.62f );
    private static readonly Color s_buttonColor = new Color( 0.045f, 0.16f, 0.25f, 0.92f );
    private static readonly Color s_buttonDisabledColor = new Color( 0.08f, 0.10f, 0.12f, 0.58f );
    private static readonly Color s_activeBlue = new Color( 0.05f, 0.55f, 1.0f, 0.95f );
    private static readonly Color s_textColor = new Color( 0.92f, 0.96f, 1.0f, 1.0f );
    private static readonly Color s_mutedTextColor = new Color( 0.70f, 0.82f, 0.92f, 1.0f );

    private void Awake()
    {
      ResolveReferences();
    }

    private void Start()
    {
      if ( buildOnStart )
        Build();
    }

    [ContextMenu("Build Demo UI")]
    public void Build()
    {
      ResolveReferences();

      if ( taskManager == null ) {
        Debug.LogWarning( "[ExcavationDemoRuntimeUI] Missing ExcavationDemoTaskManager reference." );
        return;
      }

      Clear();
      EnsureEventSystem();

      m_font = LoadBuiltinFont();

      m_root = new GameObject( "ExcavationDemoHUD", typeof( Canvas ), typeof( CanvasScaler ), typeof( GraphicRaycaster ) );
      m_root.transform.SetParent( transform, false );

      var canvas = m_root.GetComponent<Canvas>();
      canvas.renderMode = RenderMode.ScreenSpaceOverlay;
      canvas.sortingOrder = 100;

      var scaler = m_root.GetComponent<CanvasScaler>();
      scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
      scaler.referenceResolution = referenceResolution;
      scaler.matchWidthOrHeight = 0.5f;

      BuildHeader( m_root.transform );
      BuildTabs( m_root.transform );
      BuildLeftMenu( m_root.transform );
      BuildMinimapMonitor( m_root.transform );

      SwitchTab( m_activeTab );
    }

    [ContextMenu("Clear Demo UI")]
    public void Clear()
    {
      if ( minimapCamera != null && minimapCamera.targetTexture == m_minimapRenderTexture )
        minimapCamera.targetTexture = null;

      if ( m_minimapRenderTexture != null ) {
        m_minimapRenderTexture.Release();
        DestroyRuntimeObject( m_minimapRenderTexture );
        m_minimapRenderTexture = null;
      }

      if ( m_root != null ) {
        DestroyRuntimeObject( m_root );
        m_root = null;
      }
    }

    private void ResolveReferences()
    {
      if ( taskManager == null )
        taskManager = GetComponent<ExcavationDemoTaskManager>();

      if ( taskManager == null )
        taskManager = GetComponentInChildren<ExcavationDemoTaskManager>( true );
    }

    private void EnsureEventSystem()
    {
      if ( !createEventSystemIfMissing || EventSystem.current != null )
        return;

      var eventSystemObject = new GameObject( "EventSystem", typeof( EventSystem ), typeof( InputSystemUIInputModule ) );
      eventSystemObject.transform.SetParent( transform, false );
    }

    private static Font LoadBuiltinFont()
    {
      try {
        var font = Resources.GetBuiltinResource<Font>( "LegacyRuntime.ttf" );
        if ( font != null )
          return font;
      }
      catch ( System.ArgumentException ) {
      }

      try {
        return Resources.GetBuiltinResource<Font>( "Arial.ttf" );
      }
      catch ( System.ArgumentException ) {
        return null;
      }
    }

    private void BuildHeader( Transform parent )
    {
      RectTransform header = CreatePanel( parent, "TopInfoBar", AnchorPreset.TopStretch, new Vector2( 0f, -8f ), new Vector2( -28f, 76f ), new Color( 0.02f, 0.12f, 0.22f, 0.86f ) );
      AddOutline( header.gameObject, s_panelBorderColor, new Vector2( 1f, -1f ) );

      Text title = CreateText( header, "Title", systemTitle, 28, FontStyle.Bold, TextAnchor.MiddleLeft );
      title.rectTransform.anchorMin = new Vector2( 0f, 0f );
      title.rectTransform.anchorMax = new Vector2( 0.35f, 1f );
      title.rectTransform.offsetMin = new Vector2( 28f, 0f );
      title.rectTransform.offsetMax = new Vector2( 0f, 0f );

      Text subtitle = CreateText( header, "Subtitle", headerRightText, 15, FontStyle.Normal, TextAnchor.MiddleRight );
      subtitle.color = s_mutedTextColor;
      subtitle.rectTransform.anchorMin = new Vector2( 0.62f, 0f );
      subtitle.rectTransform.anchorMax = new Vector2( 0.82f, 1f );
      subtitle.rectTransform.offsetMin = Vector2.zero;
      subtitle.rectTransform.offsetMax = Vector2.zero;

      Text organization = CreateText( header, "Organization", organizationText, 18, FontStyle.Bold, TextAnchor.MiddleRight );
      organization.rectTransform.anchorMin = new Vector2( 0.82f, 0f );
      organization.rectTransform.anchorMax = new Vector2( 1f, 1f );
      organization.rectTransform.offsetMin = Vector2.zero;
      organization.rectTransform.offsetMax = new Vector2( -28f, 0f );
    }

    private void BuildTabs( Transform parent )
    {
      RectTransform tabGroup = CreateRect( parent, "FunctionTabs", AnchorPreset.TopCenter, new Vector2( 0f, -24f ), new Vector2( 650f, 50f ) );
      string[] labels = { "感知建图", "生成轨迹", "轨迹追踪" };
      for ( int i = 0; i < labels.Length; i++ ) {
        int index = i;
        RectTransform tab = CreatePanel( tabGroup, $"Tab_{labels[ i ]}", AnchorPreset.MiddleLeft, new Vector2( i * 218f, 0f ), new Vector2( 204f, 40f ), s_buttonColor );
        tab.anchorMin = new Vector2( 0f, 0.5f );
        tab.anchorMax = new Vector2( 0f, 0.5f );
        tab.pivot = new Vector2( 0f, 0.5f );

        var button = tab.gameObject.AddComponent<Button>();
        button.targetGraphic = tab.GetComponent<Image>();
        button.onClick.AddListener( () => SwitchTab( (DemoTab)index ) );

        Text text = CreateText( tab, "Text", labels[ i ], 18, FontStyle.Bold, TextAnchor.MiddleCenter );
        text.rectTransform.anchorMin = Vector2.zero;
        text.rectTransform.anchorMax = Vector2.one;
        text.rectTransform.offsetMin = Vector2.zero;
        text.rectTransform.offsetMax = Vector2.zero;

        m_tabButtons[ i ] = button;
        m_tabImages[ i ] = tab.GetComponent<Image>();
        m_tabOutlines[ i ] = AddOutline( tab.gameObject, s_panelBorderColor, new Vector2( 1f, -1f ) );
      }
    }

    private void BuildLeftMenu( Transform parent )
    {
      m_leftMenuPanel = CreatePanel( parent, "LeftControlMenu", AnchorPreset.LeftTop, new Vector2( 28f, -116f ), leftMenuSize, s_panelColor );
      AddOutline( m_leftMenuPanel.gameObject, s_panelBorderColor, new Vector2( 1f, -1f ) );

      m_leftMenuTitle = CreateText( m_leftMenuPanel, "MenuTitle", "", 21, FontStyle.Bold, TextAnchor.MiddleLeft );
      m_leftMenuTitle.rectTransform.anchorMin = new Vector2( 0f, 1f );
      m_leftMenuTitle.rectTransform.anchorMax = new Vector2( 1f, 1f );
      m_leftMenuTitle.rectTransform.pivot = new Vector2( 0.5f, 1f );
      m_leftMenuTitle.rectTransform.anchoredPosition = new Vector2( 0f, -10f );
      m_leftMenuTitle.rectTransform.sizeDelta = new Vector2( -28f, 42f );

      m_leftMenuContent = CreateRect( m_leftMenuPanel, "MenuContent", AnchorPreset.Stretch, Vector2.zero, Vector2.zero );
      m_leftMenuContent.offsetMin = new Vector2( 14f, 16f );
      m_leftMenuContent.offsetMax = new Vector2( -14f, -58f );
    }

    private void BuildMinimapMonitor( Transform parent )
    {
      RectTransform monitor = CreatePanel( parent, "MinimapMonitor", AnchorPreset.RightTop, new Vector2( -28f, -104f ), monitorSize, new Color( 0.025f, 0.075f, 0.12f, 0.86f ) );
      AddOutline( monitor.gameObject, s_panelBorderColor, new Vector2( 1f, -1f ) );

      Text title = CreateText( monitor, "MonitorTitle", "态势监控 / 俯视窗口", 17, FontStyle.Bold, TextAnchor.MiddleLeft );
      title.rectTransform.anchorMin = new Vector2( 0f, 1f );
      title.rectTransform.anchorMax = new Vector2( 1f, 1f );
      title.rectTransform.pivot = new Vector2( 0.5f, 1f );
      title.rectTransform.anchoredPosition = new Vector2( 0f, -8f );
      title.rectTransform.sizeDelta = new Vector2( -24f, 30f );

      RectTransform viewport = CreateRect( monitor, "Viewport", AnchorPreset.Stretch, Vector2.zero, Vector2.zero );
      viewport.offsetMin = new Vector2( 12f, 12f );
      viewport.offsetMax = new Vector2( -12f, -46f );

      var image = viewport.gameObject.AddComponent<Image>();
      image.color = new Color( 0.08f, 0.10f, 0.11f, 0.95f );

      AddOutline( viewport.gameObject, new Color( 0.35f, 0.72f, 1f, 0.45f ), new Vector2( 1f, -1f ) );

      if ( minimapCamera != null || minimapFallbackTexture != null ) {
        RectTransform feed = CreateRect( viewport, "CameraFeed", AnchorPreset.Stretch, Vector2.zero, Vector2.zero );
        feed.offsetMin = Vector2.zero;
        feed.offsetMax = Vector2.zero;
        var rawImage = feed.gameObject.AddComponent<RawImage>();
        rawImage.color = Color.white;
        rawImage.texture = minimapFallbackTexture != null ? minimapFallbackTexture : CreateMinimapRenderTexture();
        var fitter = feed.gameObject.AddComponent<AspectRatioFitter>();
        fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        fitter.aspectRatio = RawImageAspectRatio( rawImage.texture );
      }
      else {
        CreatePlaceholderMap( viewport );
      }
    }

    private Texture CreateMinimapRenderTexture()
    {
      if ( minimapCamera == null )
        return null;

      int width = Mathf.Max( minimapTextureSize.x, 64 );
      int height = Mathf.Max( minimapTextureSize.y, 64 );
      m_minimapRenderTexture = new RenderTexture( width, height, 16, RenderTextureFormat.ARGB32 );
      m_minimapRenderTexture.name = "ExcavationDemoMinimapRT";
      minimapCamera.targetTexture = m_minimapRenderTexture;
      return m_minimapRenderTexture;
    }

    private static float RawImageAspectRatio( Texture texture )
    {
      if ( texture == null || texture.height <= 0 )
        return 16f / 9f;

      return (float)texture.width / texture.height;
    }

    private void CreatePlaceholderMap( RectTransform parent )
    {
      CreateMapLine( parent, "Road", new Vector2( 0.5f, 0.5f ), new Vector2( 36f, 210f ), new Color( 0.25f, 0.30f, 0.33f, 1f ), 8f );
      CreateMapLine( parent, "LaneLeft", new Vector2( 0.43f, 0.5f ), new Vector2( 4f, 190f ), new Color( 0.88f, 0.90f, 0.84f, 0.92f ), 0f );
      CreateMapLine( parent, "LaneRight", new Vector2( 0.57f, 0.5f ), new Vector2( 4f, 190f ), new Color( 0.88f, 0.90f, 0.84f, 0.92f ), 0f );
      CreateMapMarker( parent, "Vehicle", new Vector2( 0.50f, 0.34f ), new Vector2( 34f, 48f ), new Color( 0.95f, 0.68f, 0.18f, 1f ) );
      CreateMapMarker( parent, "Target", new Vector2( 0.62f, 0.66f ), new Vector2( 22f, 22f ), s_activeBlue );
      CreateMapMarker( parent, "Obstacle", new Vector2( 0.35f, 0.58f ), new Vector2( 18f, 18f ), new Color( 0.85f, 0.18f, 0.16f, 1f ) );

      Text placeholder = CreateText( parent, "Placeholder", "绑定 Minimap Camera 后显示真实俯视画面", 13, FontStyle.Normal, TextAnchor.LowerCenter );
      placeholder.color = s_mutedTextColor;
      placeholder.rectTransform.anchorMin = Vector2.zero;
      placeholder.rectTransform.anchorMax = Vector2.one;
      placeholder.rectTransform.offsetMin = new Vector2( 8f, 6f );
      placeholder.rectTransform.offsetMax = new Vector2( -8f, -6f );
    }

    private void SwitchTab( DemoTab tab )
    {
      m_activeTab = tab;
      UpdateTabVisuals();
      PopulateLeftMenu();
    }

    private void UpdateTabVisuals()
    {
      for ( int i = 0; i < m_tabImages.Length; i++ ) {
        bool active = i == (int)m_activeTab;
        if ( m_tabImages[ i ] != null )
          m_tabImages[ i ].color = active ? new Color( 0.04f, 0.38f, 0.70f, 0.96f ) : s_buttonColor;
        if ( m_tabOutlines[ i ] != null ) {
          m_tabOutlines[ i ].effectColor = active ? new Color( 0.30f, 0.78f, 1f, 1f ) : s_panelBorderColor;
          m_tabOutlines[ i ].effectDistance = active ? new Vector2( 3f, -3f ) : new Vector2( 1f, -1f );
        }
      }
    }

    private void PopulateLeftMenu()
    {
      if ( m_leftMenuContent == null || m_leftMenuTitle == null )
        return;

      for ( int i = m_leftMenuContent.childCount - 1; i >= 0; i-- )
        DestroyRuntimeObject( m_leftMenuContent.GetChild( i ).gameObject );

      switch ( m_activeTab ) {
        case DemoTab.PerceptionMapping:
          m_leftMenuTitle.text = "感知建图";
          AddMenuButton( "启动感知", 0, null, false );
          AddMenuButton( "加载点云", 1, null, false );
          AddMenuButton( "生成地图", 2, null, false );
          AddMenuButton( "保存地图", 3, null, false );
          AddStatusBlock( "模块状态：待接入雷达 / SLAM 输出", 5 );
          FitLeftMenuHeight( 4, 5 );
          break;
        case DemoTab.TrajectoryPlanning:
          m_leftMenuTitle.text = "生成轨迹";
          AddMenuButton( "编辑起点", 0, taskManager.SelectPlanStartPoint, true );
          AddMenuButton( "编辑终点", 1, taskManager.SelectPlanEndPoint, true );
          AddMenuButton( "控制点前移", 2, taskManager.MoveSelectedPlanPointForward, true );
          AddMenuButton( "控制点后移", 3, taskManager.MoveSelectedPlanPointBackward, true );
          AddMenuButton( "控制点左移", 4, taskManager.MoveSelectedPlanPointLeft, true );
          AddMenuButton( "控制点右移", 5, taskManager.MoveSelectedPlanPointRight, true );
          AddMenuButton( "控制点上移", 6, taskManager.MoveSelectedPlanPointUp, true );
          AddMenuButton( "控制点下移", 7, taskManager.MoveSelectedPlanPointDown, true );
          AddMenuButton( "生成轨迹", 8, taskManager.PlanPath, true );
          AddMenuButton( "生成并追踪", 9, taskManager.PlanAndStartTracking, true );
          AddStatusBlock( "状态：-\n模式：-\n控制输入：-\n进度：-\n编辑点：-\n起点：-\n终点：-\n铲斗尖局部：-", 11 );
          if ( assignStatusTextToTaskManager )
            taskManager.statusText = m_statusText;
          FitLeftMenuHeight( 10, 11 );
          break;
        case DemoTab.TrajectoryTracking:
          m_leftMenuTitle.text = "轨迹追踪";
          AddMenuButton( "赴挖掘点模式", 0, taskManager.SetModeMoveToDig, true );
          AddMenuButton( "运土保持模式", 1, taskManager.SetModeCarryMaterial, true );
          AddMenuButton( "生成轨迹", 2, taskManager.PlanPath, true );
          AddMenuButton( "开始追踪", 3, taskManager.StartTracking, true );
          AddMenuButton( "停止追踪", 4, taskManager.StopTracking, true );
          AddMenuButton( "生成并追踪", 5, taskManager.PlanAndStartTracking, true );
          AddMenuButton( "重置演示", 6, taskManager.ResetDemo, true );
          AddStatusBlock( "状态：-\n模式：-\n控制输入：-\n进度：-\n编辑点：-\n起点：-\n终点：-\n铲斗尖局部：-", 8 );
          if ( assignStatusTextToTaskManager )
            taskManager.statusText = m_statusText;
          FitLeftMenuHeight( 7, 8 );
          break;
      }
    }

    private void AddMenuButton( string label, int row, UnityEngine.Events.UnityAction action, bool interactable )
    {
      RectTransform rect = CreatePanel( m_leftMenuContent, label, AnchorPreset.TopStretch, new Vector2( 0f, -row * kMenuRowStep ), new Vector2( 0f, kMenuButtonHeight ), interactable ? s_buttonColor : s_buttonDisabledColor );
      AddOutline( rect.gameObject, interactable ? new Color( 0.12f, 0.48f, 0.75f, 0.42f ) : new Color( 0.12f, 0.16f, 0.18f, 0.35f ), new Vector2( 1f, -1f ) );
      var button = rect.gameObject.AddComponent<Button>();
      button.targetGraphic = rect.GetComponent<Image>();
      button.interactable = interactable;
      if ( action != null )
        button.onClick.AddListener( action );

      Text text = CreateText( rect, "Text", label, 15, FontStyle.Bold, TextAnchor.MiddleCenter );
      text.color = interactable ? s_textColor : s_mutedTextColor;
      text.rectTransform.anchorMin = Vector2.zero;
      text.rectTransform.anchorMax = Vector2.one;
      text.rectTransform.offsetMin = Vector2.zero;
      text.rectTransform.offsetMax = Vector2.zero;
    }

    private void AddStatusBlock( string content, int row )
    {
      RectTransform rect = CreatePanel( m_leftMenuContent, "StatusBlock", AnchorPreset.TopStretch, new Vector2( 0f, -row * kMenuRowStep ), new Vector2( 0f, kMenuStatusHeight ), new Color( 0.015f, 0.055f, 0.09f, 0.78f ) );
      AddOutline( rect.gameObject, new Color( 0.25f, 0.54f, 0.75f, 0.45f ), new Vector2( 1f, -1f ) );
      m_statusText = CreateText( rect, "StatusText", content, 14, FontStyle.Normal, TextAnchor.UpperLeft );
      m_statusText.color = s_mutedTextColor;
      m_statusText.rectTransform.anchorMin = Vector2.zero;
      m_statusText.rectTransform.anchorMax = Vector2.one;
      m_statusText.rectTransform.offsetMin = new Vector2( 10f, 8f );
      m_statusText.rectTransform.offsetMax = new Vector2( -10f, -8f );
    }

    private void FitLeftMenuHeight( int buttonCount, int statusRow )
    {
      if ( m_leftMenuPanel == null )
        return;

      float requiredHeight = Mathf.Max(
        leftMenuSize.y,
        kMenuChromeHeight + Mathf.Max( buttonCount * kMenuRowStep, statusRow * kMenuRowStep + kMenuStatusHeight )
      );
      m_leftMenuPanel.sizeDelta = new Vector2( leftMenuSize.x, requiredHeight );
    }

    private void CreateMapLine( RectTransform parent, string name, Vector2 anchor, Vector2 size, Color color, float rotation )
    {
      RectTransform rect = CreatePanel( parent, name, AnchorPreset.Custom, Vector2.zero, size, color );
      rect.anchorMin = anchor;
      rect.anchorMax = anchor;
      rect.pivot = new Vector2( 0.5f, 0.5f );
      rect.anchoredPosition = Vector2.zero;
      rect.localRotation = Quaternion.Euler( 0f, 0f, rotation );
    }

    private void CreateMapMarker( RectTransform parent, string name, Vector2 anchor, Vector2 size, Color color )
    {
      RectTransform rect = CreatePanel( parent, name, AnchorPreset.Custom, Vector2.zero, size, color );
      rect.anchorMin = anchor;
      rect.anchorMax = anchor;
      rect.pivot = new Vector2( 0.5f, 0.5f );
      rect.anchoredPosition = Vector2.zero;
      AddOutline( rect.gameObject, Color.white, new Vector2( 1f, -1f ) );
    }

    private RectTransform CreatePanel( Transform parent, string name, AnchorPreset preset, Vector2 anchoredPosition, Vector2 size, Color color )
    {
      RectTransform rect = CreateRect( parent, name, preset, anchoredPosition, size );
      var image = rect.gameObject.AddComponent<Image>();
      image.color = color;
      return rect;
    }

    private RectTransform CreateRect( Transform parent, string name, AnchorPreset preset, Vector2 anchoredPosition, Vector2 size )
    {
      var gameObject = new GameObject( name, typeof( RectTransform ) );
      gameObject.transform.SetParent( parent, false );
      var rect = gameObject.GetComponent<RectTransform>();
      ApplyAnchorPreset( rect, preset );
      rect.anchoredPosition = anchoredPosition;
      rect.sizeDelta = size;
      return rect;
    }

    private Text CreateText( RectTransform parent, string name, string content, int size, FontStyle style, TextAnchor alignment )
    {
      var textObject = new GameObject( name, typeof( RectTransform ), typeof( Text ) );
      textObject.transform.SetParent( parent, false );
      var text = textObject.GetComponent<Text>();
      text.text = content;
      if ( m_font != null )
        text.font = m_font;
      text.fontSize = size;
      text.fontStyle = style;
      text.color = s_textColor;
      text.alignment = alignment;
      text.raycastTarget = false;
      return text;
    }

    private static Outline AddOutline( GameObject target, Color color, Vector2 distance )
    {
      var outline = target.AddComponent<Outline>();
      outline.effectColor = color;
      outline.effectDistance = distance;
      return outline;
    }

    private static void ApplyAnchorPreset( RectTransform rect, AnchorPreset preset )
    {
      switch ( preset ) {
        case AnchorPreset.TopStretch:
          rect.anchorMin = new Vector2( 0f, 1f );
          rect.anchorMax = new Vector2( 1f, 1f );
          rect.pivot = new Vector2( 0.5f, 1f );
          break;
        case AnchorPreset.TopCenter:
          rect.anchorMin = new Vector2( 0.5f, 1f );
          rect.anchorMax = new Vector2( 0.5f, 1f );
          rect.pivot = new Vector2( 0.5f, 1f );
          break;
        case AnchorPreset.LeftTop:
          rect.anchorMin = new Vector2( 0f, 1f );
          rect.anchorMax = new Vector2( 0f, 1f );
          rect.pivot = new Vector2( 0f, 1f );
          break;
        case AnchorPreset.RightTop:
          rect.anchorMin = new Vector2( 1f, 1f );
          rect.anchorMax = new Vector2( 1f, 1f );
          rect.pivot = new Vector2( 1f, 1f );
          break;
        case AnchorPreset.MiddleLeft:
          rect.anchorMin = new Vector2( 0f, 0.5f );
          rect.anchorMax = new Vector2( 0f, 0.5f );
          rect.pivot = new Vector2( 0f, 0.5f );
          break;
        case AnchorPreset.Stretch:
          rect.anchorMin = Vector2.zero;
          rect.anchorMax = Vector2.one;
          rect.pivot = new Vector2( 0.5f, 0.5f );
          break;
        case AnchorPreset.Custom:
          break;
      }
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

    private enum AnchorPreset
    {
      TopStretch,
      TopCenter,
      LeftTop,
      RightTop,
      MiddleLeft,
      Stretch,
      Custom
    }
  }
}
