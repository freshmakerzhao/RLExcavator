using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace RLExcavator
{
  /// <summary>
  /// Controller for the algorithm demo HUD.
  /// The HUD can exist in the scene for edit-time layout work; this script binds runtime actions.
  /// </summary>
  public class ExcavationDemoRuntimeUI : MonoBehaviour
  {
    private enum DemoTab
    {
      PerceptionMapping = 0,
      TrajectoryPlanning = 1,
      TrajectoryTracking = 2,
      ManualControl = 3
    }

    [Header("References")]
    public ExcavationDemoTaskManager taskManager;
    public Camera mainCamera;
    public Camera minimapCamera;
    public Texture minimapFallbackTexture;
    public RuntimeSceneCameraController sceneCameraController;

    [Header("Runtime")]
    [Tooltip("Play 时如果场景中没有 HUD，则自动创建一份。已有 HUD 时只绑定按钮，不重建。")]
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

    private readonly Button[] m_tabButtons = new Button[ 4 ];
    private readonly Image[] m_tabImages = new Image[ 4 ];
    private readonly Outline[] m_tabOutlines = new Outline[ 4 ];
    private readonly RectTransform[] m_tabPages = new RectTransform[ 4 ];

    private GameObject m_root;
    private RectTransform m_leftMenuPanel;
    private RectTransform m_leftMenuContent;
    private Text m_leftMenuTitle;
    private Text m_statusText;
    private RawImage m_minimapFeed;
    private AspectRatioFitter m_minimapFitter;
    private Font m_font;
    private RenderTexture m_minimapRenderTexture;
    private DemoTab m_activeTab = DemoTab.TrajectoryTracking;

    private const float kMenuRowStep = 42f;
    private const float kMenuButtonHeight = 34f;
    private const float kMenuStatusHeight = 278f;
    private const float kMenuChromeHeight = 86f;

    private static readonly Color s_panelColor = new Color( 0.025f, 0.075f, 0.12f, 0.82f );
    private static readonly Color s_panelBorderColor = new Color( 0.10f, 0.68f, 1.0f, 0.62f );
    private static readonly Color s_buttonColor = new Color( 0.045f, 0.16f, 0.25f, 0.92f );
    private static readonly Color s_buttonDisabledColor = new Color( 0.08f, 0.10f, 0.12f, 0.58f );
    private static readonly Color s_activeBlue = new Color( 0.05f, 0.55f, 1.0f, 0.95f );
    private static readonly Color s_textColor = new Color( 0.92f, 0.96f, 1.0f, 1.0f );
    private static readonly Color s_mutedTextColor = new Color( 0.70f, 0.82f, 0.92f, 1.0f );

    private static readonly string[] s_tabLabels = { "感知建图", "生成轨迹", "轨迹追踪", "手动控制" };
    private static readonly string[] s_pageNames = { "Page_PerceptionMapping", "Page_TrajectoryPlanning", "Page_TrajectoryTracking", "Page_ManualControl" };
    private static readonly string[] s_menuTitles = { "感知建图", "生成轨迹", "轨迹追踪", "手动控制" };

    private void Awake()
    {
      ResolveReferences();
    }

    private void Start()
    {
      ResolveReferences();

      if ( TryBindExistingHud() ) {
        EnsureEventSystem();
        WireRuntimeButtons();
        ConfigureMinimapFeed();
        SwitchTab( m_activeTab );
        return;
      }

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
      BuildMenuPages();
      BuildMinimapMonitor( m_root.transform );

      WireRuntimeButtons();
      ConfigureMinimapFeed();
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

      GameObject existingRoot = m_root != null ? m_root : FindExistingRoot();
      if ( existingRoot != null )
        DestroyRuntimeObject( existingRoot );

      m_root = null;
      m_leftMenuPanel = null;
      m_leftMenuContent = null;
      m_leftMenuTitle = null;
      m_statusText = null;
      m_minimapFeed = null;
      m_minimapFitter = null;

      for ( int i = 0; i < m_tabButtons.Length; i++ ) {
        m_tabButtons[ i ] = null;
        m_tabImages[ i ] = null;
        m_tabOutlines[ i ] = null;
        m_tabPages[ i ] = null;
      }
    }

    private void ResolveReferences()
    {
      if ( taskManager == null )
        taskManager = GetComponent<ExcavationDemoTaskManager>();

      if ( taskManager == null )
        taskManager = GetComponentInChildren<ExcavationDemoTaskManager>( true );

      if ( mainCamera == null )
        mainCamera = Camera.main;

      if ( sceneCameraController == null && mainCamera != null )
        sceneCameraController = mainCamera.GetComponent<RuntimeSceneCameraController>();

      if ( sceneCameraController == null && Application.isPlaying && mainCamera != null )
        sceneCameraController = mainCamera.gameObject.AddComponent<RuntimeSceneCameraController>();
    }

    private bool TryBindExistingHud()
    {
      GameObject existingRoot = FindExistingRoot();
      if ( existingRoot == null )
        return false;

      m_root = existingRoot;
      m_font = LoadBuiltinFont();

      m_leftMenuPanel = FindRect( m_root.transform, "LeftControlMenu" );
      m_leftMenuContent = FindRect( m_root.transform, "LeftControlMenu/MenuContent" );
      m_leftMenuTitle = FindText( m_root.transform, "LeftControlMenu/MenuTitle" );

      Transform tabGroup = FindChild( m_root.transform, "FunctionTabs" );
      for ( int i = 0; i < s_tabLabels.Length; i++ ) {
        Transform tab = tabGroup != null ? tabGroup.Find( $"Tab_{s_tabLabels[ i ]}" ) : null;
        if ( tab == null )
          continue;

        m_tabButtons[ i ] = tab.GetComponent<Button>();
        m_tabImages[ i ] = tab.GetComponent<Image>();
        m_tabOutlines[ i ] = tab.GetComponent<Outline>();
      }

      for ( int i = 0; i < s_pageNames.Length; i++ )
        m_tabPages[ i ] = FindRect( m_root.transform, $"LeftControlMenu/MenuContent/{s_pageNames[ i ]}" );

      m_minimapFeed = FindComponent<RawImage>( m_root.transform, "MinimapMonitor/Viewport/CameraFeed" );
      m_minimapFitter = m_minimapFeed != null ? m_minimapFeed.GetComponent<AspectRatioFitter>() : null;

      return m_leftMenuPanel != null && m_leftMenuContent != null;
    }

    private GameObject FindExistingRoot()
    {
      Transform existing = transform.Find( "ExcavationDemoHUD" );
      return existing != null ? existing.gameObject : null;
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
      RectTransform tabGroup = CreateRect( parent, "FunctionTabs", AnchorPreset.TopCenter, new Vector2( 0f, -24f ), new Vector2( 820f, 50f ) );
      for ( int i = 0; i < s_tabLabels.Length; i++ ) {
        RectTransform tab = CreatePanel( tabGroup, $"Tab_{s_tabLabels[ i ]}", AnchorPreset.MiddleLeft, new Vector2( i * 208f, 0f ), new Vector2( 194f, 40f ), s_buttonColor );
        tab.anchorMin = new Vector2( 0f, 0.5f );
        tab.anchorMax = new Vector2( 0f, 0.5f );
        tab.pivot = new Vector2( 0f, 0.5f );

        var button = tab.gameObject.AddComponent<Button>();
        button.targetGraphic = tab.GetComponent<Image>();

        Text text = CreateText( tab, "Text", s_tabLabels[ i ], 18, FontStyle.Bold, TextAnchor.MiddleCenter );
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

    private void BuildMenuPages()
    {
      if ( m_leftMenuContent == null )
        return;

      for ( int i = 0; i < s_pageNames.Length; i++ ) {
        RectTransform page = CreateRect( m_leftMenuContent, s_pageNames[ i ], AnchorPreset.Stretch, Vector2.zero, Vector2.zero );
        page.offsetMin = Vector2.zero;
        page.offsetMax = Vector2.zero;
        m_tabPages[ i ] = page;
      }

      BuildPerceptionPage( m_tabPages[ (int)DemoTab.PerceptionMapping ] );
      BuildTrajectoryPlanningPage( m_tabPages[ (int)DemoTab.TrajectoryPlanning ] );
      BuildTrajectoryTrackingPage( m_tabPages[ (int)DemoTab.TrajectoryTracking ] );
      BuildManualControlPage( m_tabPages[ (int)DemoTab.ManualControl ] );
    }

    private void BuildPerceptionPage( RectTransform page )
    {
      AddMenuButton( page, "启动感知", 0, null, false );
      AddMenuButton( page, "加载点云", 1, null, false );
      AddMenuButton( page, "生成地图", 2, null, false );
      AddMenuButton( page, "保存地图", 3, null, false );
      AddStatusBlock( page, "模块状态：待接入雷达 / SLAM 输出", 5 );
    }

    private void BuildTrajectoryPlanningPage( RectTransform page )
    {
      AddMenuButton( page, "赴挖掘点模式", 0, taskManager.SetModeMoveToDig, true );
      AddMenuButton( page, "运土保持模式", 1, taskManager.SetModeCarryMaterial, true );
      AddMenuButton( page, "选起点后点画面", 2, taskManager.SelectPlanStartPoint, true );
      AddMenuButton( page, "选终点后点画面", 3, taskManager.SelectPlanEndPoint, true );
      AddMenuButton( page, "控制点前移", 4, taskManager.MoveSelectedPlanPointForward, true );
      AddMenuButton( page, "控制点后移", 5, taskManager.MoveSelectedPlanPointBackward, true );
      AddMenuButton( page, "控制点左移", 6, taskManager.MoveSelectedPlanPointLeft, true );
      AddMenuButton( page, "控制点右移", 7, taskManager.MoveSelectedPlanPointRight, true );
      AddMenuButton( page, "控制点上移", 8, taskManager.MoveSelectedPlanPointUp, true );
      AddMenuButton( page, "控制点下移", 9, taskManager.MoveSelectedPlanPointDown, true );
      AddMenuButton( page, "生成轨迹", 10, taskManager.PlanPath, true );
      AddMenuButton( page, "生成并追踪", 11, taskManager.PlanAndStartTracking, true );
      AddStatusBlock( page, "状态：-\n模式：-\n行为模式：-\n控制输入：-\n进度：-\n编辑点：-\n起点：-\n终点：-\n规划：-\n点选：-\n可达区域：-\n铲斗尖局部：-", 13 );
    }

    private void BuildTrajectoryTrackingPage( RectTransform page )
    {
      AddMenuButton( page, "赴挖掘点模式", 0, taskManager.SetModeMoveToDig, true );
      AddMenuButton( page, "运土保持模式", 1, taskManager.SetModeCarryMaterial, true );
      AddMenuButton( page, "生成轨迹", 2, taskManager.PlanPath, true );
      AddMenuButton( page, "开始追踪", 3, taskManager.StartTracking, true );
      AddMenuButton( page, "停止追踪", 4, taskManager.StopTracking, true );
      AddMenuButton( page, "生成并追踪", 5, taskManager.PlanAndStartTracking, true );
      AddMenuButton( page, "重置演示", 6, taskManager.ResetDemo, true );
      AddStatusBlock( page, "状态：-\n模式：-\n行为模式：-\n控制输入：-\n进度：-\n编辑点：-\n起点：-\n终点：-\n规划：-\n点选：-\n可达区域：-\n铲斗尖局部：-", 8 );
    }

    private void BuildManualControlPage( RectTransform page )
    {
      AddMenuButton( page, "编辑挖掘点区域", 0, taskManager.EditMoveToDigReachableWorkspace, true );
      AddMenuButton( page, "编辑运土区域", 1, taskManager.EditCarryMaterialReachableWorkspace, true );
      AddMenuButton( page, "隐藏可达区域", 2, taskManager.HideReachableWorkspaceForManualEdit, true );
      AddMenuButton( page, "停止关节运动", 3, taskManager.StopManualMotion, true );
      AddMenuButton( page, "上一个边界点", 4, taskManager.SelectPreviousWorkspaceBoundaryPoint, true );
      AddMenuButton( page, "下一个边界点", 5, taskManager.SelectNextWorkspaceBoundaryPoint, true );
      AddMenuButton( page, "应用铲斗到边界点", 6, taskManager.ApplyBucketTipToSelectedWorkspaceBoundaryPoint, true );
      AddStatusBlock( page, "状态：-\n模式：-\n行为模式：-\n控制输入：-\n手动输入：-\n可达区域：-\n边界点：-\n键盘：I/K 大臂，T/G 小臂，J/L 铲斗，F/H 回转\n铲斗尖局部：-", 7 );
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

      RectTransform feed = CreateRect( viewport, "CameraFeed", AnchorPreset.Stretch, Vector2.zero, Vector2.zero );
      feed.offsetMin = Vector2.zero;
      feed.offsetMax = Vector2.zero;
      m_minimapFeed = feed.gameObject.AddComponent<RawImage>();
      m_minimapFeed.color = Color.white;
      m_minimapFitter = feed.gameObject.AddComponent<AspectRatioFitter>();
      m_minimapFitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
      m_minimapFitter.aspectRatio = 16f / 9f;
    }

    private void ConfigureMinimapFeed()
    {
      if ( m_minimapFeed == null )
        return;

      Texture texture = minimapFallbackTexture;
      if ( texture == null && Application.isPlaying && minimapCamera != null )
        texture = CreateMinimapRenderTexture();

      m_minimapFeed.texture = texture;
      m_minimapFeed.color = texture != null ? Color.white : new Color( 1f, 1f, 1f, 0.0f );

      if ( m_minimapFitter != null )
        m_minimapFitter.aspectRatio = RawImageAspectRatio( texture );
    }

    private Texture CreateMinimapRenderTexture()
    {
      if ( minimapCamera == null )
        return null;

      int width = Mathf.Max( minimapTextureSize.x, 64 );
      int height = Mathf.Max( minimapTextureSize.y, 64 );
      m_minimapRenderTexture = new RenderTexture( width, height, 16, RenderTextureFormat.ARGB32 ) {
        name = "ExcavationDemoMinimapRT"
      };
      minimapCamera.targetTexture = m_minimapRenderTexture;
      return m_minimapRenderTexture;
    }

    private static float RawImageAspectRatio( Texture texture )
    {
      if ( texture == null || texture.height <= 0 )
        return 16f / 9f;

      return (float)texture.width / texture.height;
    }

    private void WireRuntimeButtons()
    {
      for ( int i = 0; i < m_tabButtons.Length; i++ ) {
        int index = i;
        if ( m_tabButtons[ i ] == null )
          continue;

        m_tabButtons[ i ].onClick.RemoveAllListeners();
        m_tabButtons[ i ].onClick.AddListener( () => SwitchTab( (DemoTab)index ) );
      }

      WirePageButton( DemoTab.TrajectoryPlanning, "Button_赴挖掘点模式", taskManager.SetModeMoveToDig );
      WirePageButton( DemoTab.TrajectoryPlanning, "Button_运土保持模式", taskManager.SetModeCarryMaterial );
      WirePageButton( DemoTab.TrajectoryPlanning, "Button_选起点后点画面", taskManager.SelectPlanStartPoint );
      WirePageButton( DemoTab.TrajectoryPlanning, "Button_选终点后点画面", taskManager.SelectPlanEndPoint );
      WirePageButton( DemoTab.TrajectoryPlanning, "Button_控制点前移", taskManager.MoveSelectedPlanPointForward );
      WirePageButton( DemoTab.TrajectoryPlanning, "Button_控制点后移", taskManager.MoveSelectedPlanPointBackward );
      WirePageButton( DemoTab.TrajectoryPlanning, "Button_控制点左移", taskManager.MoveSelectedPlanPointLeft );
      WirePageButton( DemoTab.TrajectoryPlanning, "Button_控制点右移", taskManager.MoveSelectedPlanPointRight );
      WirePageButton( DemoTab.TrajectoryPlanning, "Button_控制点上移", taskManager.MoveSelectedPlanPointUp );
      WirePageButton( DemoTab.TrajectoryPlanning, "Button_控制点下移", taskManager.MoveSelectedPlanPointDown );
      WirePageButton( DemoTab.TrajectoryPlanning, "Button_生成轨迹", taskManager.PlanPath );
      WirePageButton( DemoTab.TrajectoryPlanning, "Button_生成并追踪", taskManager.PlanAndStartTracking );

      WirePageButton( DemoTab.TrajectoryTracking, "Button_赴挖掘点模式", taskManager.SetModeMoveToDig );
      WirePageButton( DemoTab.TrajectoryTracking, "Button_运土保持模式", taskManager.SetModeCarryMaterial );
      WirePageButton( DemoTab.TrajectoryTracking, "Button_生成轨迹", taskManager.PlanPath );
      WirePageButton( DemoTab.TrajectoryTracking, "Button_开始追踪", taskManager.StartTracking );
      WirePageButton( DemoTab.TrajectoryTracking, "Button_停止追踪", taskManager.StopTracking );
      WirePageButton( DemoTab.TrajectoryTracking, "Button_生成并追踪", taskManager.PlanAndStartTracking );
      WirePageButton( DemoTab.TrajectoryTracking, "Button_重置演示", taskManager.ResetDemo );

      WirePageButton( DemoTab.ManualControl, "Button_编辑挖掘点区域", taskManager.EditMoveToDigReachableWorkspace );
      WirePageButton( DemoTab.ManualControl, "Button_编辑运土区域", taskManager.EditCarryMaterialReachableWorkspace );
      WirePageButton( DemoTab.ManualControl, "Button_隐藏可达区域", taskManager.HideReachableWorkspaceForManualEdit );
      WirePageButton( DemoTab.ManualControl, "Button_停止关节运动", taskManager.StopManualMotion );
      WirePageButton( DemoTab.ManualControl, "Button_上一个边界点", taskManager.SelectPreviousWorkspaceBoundaryPoint );
      WirePageButton( DemoTab.ManualControl, "Button_下一个边界点", taskManager.SelectNextWorkspaceBoundaryPoint );
      WirePageButton( DemoTab.ManualControl, "Button_应用铲斗到边界点", taskManager.ApplyBucketTipToSelectedWorkspaceBoundaryPoint );
    }

    private void WirePageButton( DemoTab tab, string buttonName, UnityEngine.Events.UnityAction action )
    {
      Button button = FindPageButton( tab, buttonName );
      if ( button == null )
        return;

      button.onClick.RemoveAllListeners();
      button.interactable = action != null;
      if ( action != null )
        button.onClick.AddListener( action );
    }

    private Button FindPageButton( DemoTab tab, string buttonName )
    {
      RectTransform page = PageForTab( tab );
      Transform buttonTransform = page != null ? page.Find( buttonName ) : null;
      return buttonTransform != null ? buttonTransform.GetComponent<Button>() : null;
    }

    private void SwitchTab( DemoTab tab )
    {
      m_activeTab = tab;
      if ( taskManager != null ) {
        if ( tab == DemoTab.ManualControl ) {
          taskManager.StartManualControl();
          taskManager.ShowReachableWorkspaceForManualEdit();
        }
        else {
          taskManager.HideAllReachableWorkspaces();
          if ( taskManager.state == ExcavationDemoTaskManager.DemoState.ManualControl )
            taskManager.StopManualControl();
        }
      }

      for ( int i = 0; i < m_tabPages.Length; i++ ) {
        if ( m_tabPages[ i ] != null )
          m_tabPages[ i ].gameObject.SetActive( i == (int)m_activeTab );
      }

      if ( m_leftMenuTitle != null )
        m_leftMenuTitle.text = s_menuTitles[ (int)m_activeTab ];

      m_statusText = FindStatusText( m_activeTab );
      if ( assignStatusTextToTaskManager && taskManager != null )
        taskManager.statusText = m_statusText;

      UpdateTabVisuals();
      FitLeftMenuHeightForTab( m_activeTab );
    }

    private Text FindStatusText( DemoTab tab )
    {
      RectTransform page = PageForTab( tab );
      return page != null ? FindText( page, "StatusBlock/StatusText" ) : null;
    }

    private RectTransform PageForTab( DemoTab tab )
    {
      int index = (int)tab;
      return index >= 0 && index < m_tabPages.Length ? m_tabPages[ index ] : null;
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

    private void AddMenuButton( RectTransform parent, string label, int row, UnityEngine.Events.UnityAction action, bool interactable )
    {
      RectTransform rect = CreatePanel( parent, $"Button_{label}", AnchorPreset.TopStretch, new Vector2( 0f, -row * kMenuRowStep ), new Vector2( 0f, kMenuButtonHeight ), interactable ? s_buttonColor : s_buttonDisabledColor );
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

    private void AddStatusBlock( RectTransform parent, string content, int row )
    {
      RectTransform rect = CreatePanel( parent, "StatusBlock", AnchorPreset.TopStretch, new Vector2( 0f, -row * kMenuRowStep ), new Vector2( 0f, kMenuStatusHeight ), new Color( 0.015f, 0.055f, 0.09f, 0.78f ) );
      AddOutline( rect.gameObject, new Color( 0.25f, 0.54f, 0.75f, 0.45f ), new Vector2( 1f, -1f ) );
      Text statusText = CreateText( rect, "StatusText", content, 14, FontStyle.Normal, TextAnchor.UpperLeft );
      statusText.color = s_mutedTextColor;
      statusText.rectTransform.anchorMin = Vector2.zero;
      statusText.rectTransform.anchorMax = Vector2.one;
      statusText.rectTransform.offsetMin = new Vector2( 10f, 8f );
      statusText.rectTransform.offsetMax = new Vector2( -10f, -8f );
    }

    private void FitLeftMenuHeightForTab( DemoTab tab )
    {
      switch ( tab ) {
        case DemoTab.PerceptionMapping:
          FitLeftMenuHeight( 4, 5 );
          break;
        case DemoTab.TrajectoryPlanning:
          FitLeftMenuHeight( 12, 13 );
          break;
        case DemoTab.TrajectoryTracking:
          FitLeftMenuHeight( 7, 8 );
          break;
        case DemoTab.ManualControl:
          FitLeftMenuHeight( 8, 8 );
          break;
      }
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

    private static RectTransform FindRect( Transform root, string path )
    {
      Transform child = FindChild( root, path );
      return child != null ? child.GetComponent<RectTransform>() : null;
    }

    private static Text FindText( Transform root, string path )
    {
      return FindComponent<Text>( root, path );
    }

    private static T FindComponent<T>( Transform root, string path ) where T : Component
    {
      Transform child = FindChild( root, path );
      return child != null ? child.GetComponent<T>() : null;
    }

    private static Transform FindChild( Transform root, string path )
    {
      return root != null ? root.Find( path ) : null;
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
