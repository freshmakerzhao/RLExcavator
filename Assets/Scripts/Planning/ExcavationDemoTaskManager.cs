using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Policies;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace RLExcavator
{
  /// <summary>
  /// 交互 demo 的最小任务管理器。
  /// 它不直接控制关节，只负责设置任务模式、调用 planner、启动/停止 RL tracker。
  /// UI Button 可直接绑定本组件的 public 方法。
  /// </summary>
  public class ExcavationDemoTaskManager : MonoBehaviour
  {
    public enum DemoState
    {
      Idle,
      PathPlanned,
      Tracking,
      ManualControl,
      AutoTask,
      Completed
    }

    [Header("References")]
    [Tooltip("挖机语义坐标注册器。手动标定和状态显示统一使用 MachineRoot。")]
    public ExcavatorFrameRegistry frameRegistry;

    public ExcavatorTrajectoryAgent agent;
    public ExcavatorController controller;
    public SimpleTrajectoryPlanner planner;
    public DemoPointPicker pointPicker;
    public TrajectoryProvider trajectory;
    [Tooltip("当前激活的可达空间。Demo 会根据任务模式自动切换该引用。")]
    public ReachableWorkspaceVisualizer reachableWorkspace;
    [Tooltip("赴挖掘点模式使用的可达空间。")]
    public ReachableWorkspaceVisualizer moveToDigReachableWorkspace;
    [Tooltip("运土保持模式使用的可达空间。")]
    public ReachableWorkspaceVisualizer carryMaterialReachableWorkspace;
    public DecisionRequester decisionRequester;
    public Text statusText;
    [Tooltip("完整挖掘-运输-倾倒任务编排。UI 只调用它，不在 TaskManager 内部写任务流程。")]
    public ExcavationCycleTask excavationCycleTask;

    [Header("Calibration")]
    [Tooltip("用于显示铲斗尖局部坐标的参考系。建议绑定 TrajectoryEnvironment 或 RLExcavatorTraj。为空时自动使用 agent.environmentRoot 或 TrajectoryProvider。")]
    public Transform bucketTipLocalFrame;

    [Tooltip("Game UI 每次移动可达空间边界点的距离，单位是 workspaceFrame 的世界米。")]
    public float workspaceAnchorMoveStep = 0.15f;

    [Header("Startup")]
    [Tooltip("开启后仅在 Agent 的 Behavior Type 为 Inference Only 时执行 demo 启停，避免误放到训练场景后影响训练。")]
    public bool requireInferenceOnlyBehavior = true;

    [Tooltip("Play 后先禁止控制命令写入，等待用户点击开始追踪。")]
    public bool disableCommandInputOnStart = true;

    [Tooltip("启动时关闭随机任务模式，由按钮/状态机显式设置 taskMode。")]
    public bool disableRandomTaskModeOnStart = true;

    [Tooltip("轨迹完成后自动停止控制命令写入。")]
    public bool stopWhenPathCompleted = true;

    [Tooltip("Demo 空闲或停止时禁用 DecisionRequester，开始追踪时再启用，避免策略持续推理。")]
    public bool disableDecisionRequesterWhenStopped = true;

    [Tooltip("Demo 空闲/手动时禁用 ML-Agents Agent，只在真正开始轨迹追踪时启用，避免算法验证场景默认跑训练回合。")]
    public bool disableAgentWhenStopped = true;

    [Header("Demo path")]
    [Tooltip("Demo 启动时隐藏 TrajectoryProvider 中残留的默认路径，等待用户点击生成轨迹后再显示。")]
    public bool hideTrajectoryUntilPlannedOnStart = true;

    [Tooltip("开启后必须先点击生成轨迹，再允许开始追踪。")]
    public bool requirePathBeforeTracking = true;

    [Tooltip("Demo 场景中缺少 DemoPointPicker 时自动创建，用于鼠标点击/拖拽设置起点终点。")]
    public bool createPointPickerIfMissing = true;

    [Tooltip("切换 MoveToDig/CarryMaterial 时隐藏非当前模式的可达空间编辑显示。")]
    public bool hideInactiveReachableWorkspacesOnModeSwitch = true;

    [Header("Init joint pose")]
    [Tooltip("Play 启动时用关节位置伺服回到初始关节姿态。")]
    public bool applyInitJointPoseOnStart = false;

    [Tooltip("完整任务倾倒结束后，用关节位置伺服回到初始关节姿态。手动停止任务时不会自动回位。")]
    public bool returnToInitJointPoseAfterCycle = true;

    [Tooltip("初始关节姿态，归一化顺序为 boom、stick、bucket、swing。")]
    public ExcavationCycleTask.NormalizedJointDelta initNormalizedJointPose;

    [Tooltip("回到初始关节姿态的最长时间。")]
    [Min( 0.1f )]
    public float returnToInitJointPoseTimeoutSeconds = 8f;

    [Header("Trajectory display")]
    [Tooltip("执行任务和生成轨迹时是否显示 waypoint 轨迹点。关闭后仍会正常规划和跟踪，只隐藏可视化。")]
    public bool showTrajectoryPath = false;

    [Header("Runtime")]
    public DemoState state = DemoState.Idle;

    private ExcavatorKeyboardTester m_manualKeyboardTester;
    private ExcavatorTrajectoryAgent.TrajectoryTaskMode m_taskModeWithoutAgent = ExcavatorTrajectoryAgent.TrajectoryTaskMode.MoveToDig;
    private bool m_cycleTaskEventsBound = false;

    private void Awake()
    {
      ResolveMissingReferences();
      EnsureCycleTaskReferences();
      BindCycleTaskEvents();

      if ( disableAgentWhenStopped )
        SetAgentEnabled( false );
    }

    private void Start()
    {
      EnsureCycleTaskReferences();
      ApplyInitJointPoseIfEnabled();

      if ( agent != null && disableRandomTaskModeOnStart )
        agent.randomizeTaskModeOnEpisodeBegin = false;

      if ( controller != null && disableCommandInputOnStart )
        controller.SetCommandInputEnabled( false );

      if ( disableDecisionRequesterWhenStopped )
        SetDecisionRequesterEnabled( false );

      if ( disableAgentWhenStopped )
        SetAgentEnabled( false );

      if ( trajectory != null && hideTrajectoryUntilPlannedOnStart )
        trajectory.SetPathVisualsVisible( false );

      HideAllReachableWorkspaces();
      UpdateStatus();
    }

    private void OnDestroy()
    {
      UnbindCycleTaskEvents();
    }

    private void Update()
    {
      SyncStateFromCycleTask();

      if ( stopWhenPathCompleted && state == DemoState.Tracking && trajectory != null && trajectory.PathCompleted ) {
        StopTracking();
        state = DemoState.Completed;
      }

      UpdateStatus();
    }

    [ContextMenu("Set Mode MoveToDig")]
    public void SetModeMoveToDig()
    {
      SetTaskMode( ExcavatorTrajectoryAgent.TrajectoryTaskMode.MoveToDig );
    }

    [ContextMenu("Set Mode CarryMaterial")]
    public void SetModeCarryMaterial()
    {
      SetTaskMode( ExcavatorTrajectoryAgent.TrajectoryTaskMode.CarryMaterial );
    }

    public void SetTaskMode( ExcavatorTrajectoryAgent.TrajectoryTaskMode mode )
    {
      m_taskModeWithoutAgent = mode;

      if ( agent != null ) {
        agent.taskMode = mode;
        agent.randomizeTaskModeOnEpisodeBegin = false;
      }

      ApplyReachableWorkspaceForMode( mode );
      UpdateStatus();
    }

    public void SelectPlanStartPoint()
    {
      SelectDigPoint();
    }

    public void SelectPlanEndPoint()
    {
      SelectDumpPoint();
    }

    public void SelectDigPoint()
    {
      if ( planner == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing planner reference." );
        return;
      }

      SetTaskMode( ExcavatorTrajectoryAgent.TrajectoryTaskMode.MoveToDig );
      planner.SelectStartPoint();
      UpdateStatus();
    }

    public void SelectDumpPoint()
    {
      if ( planner == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing planner reference." );
        return;
      }

      SetTaskMode( ExcavatorTrajectoryAgent.TrajectoryTaskMode.CarryMaterial );
      planner.SelectEndPoint();
      UpdateStatus();
    }

    public void MoveSelectedPlanPointForward()
    {
      MoveSelectedPlanPoint( Vector3.forward );
    }

    public void MoveSelectedPlanPointBackward()
    {
      MoveSelectedPlanPoint( Vector3.back );
    }

    public void MoveSelectedPlanPointLeft()
    {
      MoveSelectedPlanPoint( Vector3.left );
    }

    public void MoveSelectedPlanPointRight()
    {
      MoveSelectedPlanPoint( Vector3.right );
    }

    public void MoveSelectedPlanPointUp()
    {
      MoveSelectedPlanPoint( Vector3.up );
    }

    public void MoveSelectedPlanPointDown()
    {
      MoveSelectedPlanPoint( Vector3.down );
    }

    private void MoveSelectedPlanPoint( Vector3 localDirection )
    {
      if ( planner == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing planner reference." );
        return;
      }

      planner.MoveSelectedPoint( localDirection );
      UpdateStatus();
    }

    [ContextMenu("生成轨迹")]
    public void PlanPath()
    {
      if ( planner == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing planner reference." );
        return;
      }

      if ( !planner.TryPlanConfiguredPath() )
        return;

      ApplyTrajectoryPathVisibility();
      state = DemoState.PathPlanned;
      UpdateStatus();
    }

    public void ToggleTrajectoryPathVisible()
    {
      showTrajectoryPath = !showTrajectoryPath;
      ApplyTrajectoryPathVisibility();
      UpdateStatus();
    }

    public void ShowTrajectoryPath()
    {
      showTrajectoryPath = true;
      ApplyTrajectoryPathVisibility();
      UpdateStatus();
    }

    public void HideTrajectoryPath()
    {
      showTrajectoryPath = false;
      ApplyTrajectoryPathVisibility();
      UpdateStatus();
    }

    [ContextMenu("Capture Current Joint Pose As Init")]
    public void CaptureCurrentJointPoseAsInit()
    {
      if ( controller == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing controller reference." );
        return;
      }

      initNormalizedJointPose = new ExcavationCycleTask.NormalizedJointDelta(
        controller.BoomNormalizedPosition,
        controller.StickNormalizedPosition,
        controller.BucketNormalizedPosition,
        controller.SwingNormalizedPosition );
      UpdateStatus();
    }

    [ContextMenu("开始追踪")]
    public void StartTracking()
    {
      TryStartTracking( DemoState.Tracking );
    }

    private bool TryStartTracking( DemoState trackingState )
    {
      StopFixedAction( false );

      if ( agent == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing trajectory agent reference." );
        return false;
      }

      if ( !CanUseInferenceControlMode() )
        return false;

      SetInferenceControlMode();

      if ( !CanRunDemoControl() )
        return false;

      if ( requirePathBeforeTracking && state != DemoState.PathPlanned ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Please plan a path before starting tracking." );
        return false;
      }

      if ( controller == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing controller reference." );
        return false;
      }

      SetAgentEnabled( true );
      SetManualKeyboardTesterEnabled( false );
      controller.SetCommandInputEnabled( true );
      SetDecisionRequesterEnabled( true );
      state = trackingState;
      UpdateStatus();
      return true;
    }

    [ContextMenu("停止追踪")]
    public void StopTracking()
    {
      if ( IsCycleTaskRunning() ) {
        StopCycleTask( true );
        return;
      }

      StopTrackingControls();

      if ( state == DemoState.Tracking || state == DemoState.AutoTask )
        state = DemoState.Idle;

      UpdateStatus();
    }

    private void StopTrackingControls()
    {
      if ( CanRunDemoControl() ) {
        if ( controller != null )
          controller.SetCommandInputEnabled( false );

        if ( disableDecisionRequesterWhenStopped )
          SetDecisionRequesterEnabled( false );

        if ( disableAgentWhenStopped )
          SetAgentEnabled( false );
      }
    }

    public void PlanAndStartTracking()
    {
      PlanPath();
      StartTracking();
    }

    [ContextMenu("执行")]
    public void ExecuteDigDumpTask()
    {
      EnsureCycleTaskReferences();

      if ( excavationCycleTask == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing ExcavationCycleTask reference." );
        return;
      }

      if ( excavationCycleTask.IsCycleRunning ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Auto task is already running." );
        return;
      }

      ApplyTrajectoryPathVisibility();
      SetManualKeyboardTesterEnabled( false );
      excavationCycleTask.ExecuteDigTransportDump();
      state = DemoState.AutoTask;
      UpdateStatus();
    }

    // 开始手动控制，禁用决策请求器和 Agent，启用键盘测试器
    public void StartManualControl()
    {
      StopCycleTask( true );

      if ( controller == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing controller reference." );
        return;
      }

      controller.CancelHomePoseReset();
      SetDecisionRequesterEnabled( false );
      SetAgentEnabled( false );
      SetManualKeyboardTesterEnabled( true );
      controller.SetCommandInputEnabled( true );
      controller.StopAll();
      state = DemoState.ManualControl;
      UpdateStatus();
    }

    public void StopManualControl()
    {
      StopCycleTask( true );
      StopFixedAction( false );

      if ( controller != null ) {
        controller.StopAll();
        controller.SetCommandInputEnabled( false );
      }

      SetManualKeyboardTesterEnabled( false );
      if ( disableDecisionRequesterWhenStopped )
        SetDecisionRequesterEnabled( false );
      if ( disableAgentWhenStopped )
        SetAgentEnabled( false );
      HideAllReachableWorkspaces();
      state = DemoState.Idle;
      UpdateStatus();
    }

    public void SetHeuristicControlMode()
    {
      SetBehaviorType( BehaviorType.HeuristicOnly );
      UpdateStatus();
    }

    public void SetInferenceControlMode()
    {
      SetBehaviorType( BehaviorType.InferenceOnly );
      UpdateStatus();
    }

    public void StopManualMotion()
    {
      StopCycleTask( true );
      StopFixedAction( true );
      if ( controller != null )
        controller.StopAll();
      UpdateStatus();
    }

    [ContextMenu("挖掘测试")]
    public void StartDigTest()
    {
      if ( controller == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing controller reference." );
        return;
      }

      EnsureCycleTaskReferences();
      if ( excavationCycleTask == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing ExcavationCycleTask reference." );
        return;
      }

      StopFixedAction( false );
      StartManualControl();
      SetManualKeyboardTesterEnabled( false );
      controller.SetCommandInputEnabled( true );
      excavationCycleTask.StartDigTest();
      UpdateStatus();
    }

    [ContextMenu("倾倒测试")]
    public void StartDumpTest()
    {
      if ( controller == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing controller reference." );
        return;
      }

      EnsureCycleTaskReferences();
      if ( excavationCycleTask == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing ExcavationCycleTask reference." );
        return;
      }

      StopFixedAction( false );
      StartManualControl();
      SetManualKeyboardTesterEnabled( false );
      controller.SetCommandInputEnabled( true );
      excavationCycleTask.StartDumpTest();
      UpdateStatus();
    }

    public void CaptureManualPoseAsHome()
    {
      if ( controller == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing controller reference." );
        return;
      }

      controller.CaptureCurrentPoseAsInspectorHome();
      UpdateStatus();
    }

    public void ShowReachableWorkspaceForManualEdit()
    {
      ReachableWorkspaceVisualizer workspace = ActiveReachableWorkspace();
      if ( workspace == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing reachable workspace reference." );
        return;
      }

      workspace.ShowReachableVolumeAndBoundaryPoints();
      UpdateStatus();
    }

    public void HideReachableWorkspaceForManualEdit()
    {
      HideAllReachableWorkspaces();
      UpdateStatus();
    }

    public void EditMoveToDigReachableWorkspace()
    {
      EditReachableWorkspaceForMode( ExcavatorTrajectoryAgent.TrajectoryTaskMode.MoveToDig );
    }

    public void EditCarryMaterialReachableWorkspace()
    {
      EditReachableWorkspaceForMode( ExcavatorTrajectoryAgent.TrajectoryTaskMode.CarryMaterial );
    }

    public void HideAllReachableWorkspaces()
    {
      ReachableWorkspaceVisualizer workspace = ActiveReachableWorkspace();
      HideWorkspaceIfInactive( moveToDigReachableWorkspace, null );
      HideWorkspaceIfInactive( carryMaterialReachableWorkspace, null );

      if ( workspace != moveToDigReachableWorkspace && workspace != carryMaterialReachableWorkspace )
        HideWorkspaceIfInactive( workspace, null );
    }

    public void SelectPreviousWorkspaceBoundaryPoint()
    {
      ReachableWorkspaceVisualizer workspace = ActiveReachableWorkspace();
      if ( workspace == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing reachable workspace reference." );
        return;
      }

      workspace.SelectPreviousVolumeAnchor();
      UpdateStatus();
    }

    public void SelectNextWorkspaceBoundaryPoint()
    {
      ReachableWorkspaceVisualizer workspace = ActiveReachableWorkspace();
      if ( workspace == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing reachable workspace reference." );
        return;
      }

      workspace.SelectNextVolumeAnchor();
      UpdateStatus();
    }

    public void MoveSelectedWorkspaceBoundaryPointForward()
    {
      MoveSelectedWorkspaceBoundaryPoint( Vector3.forward );
    }

    public void MoveSelectedWorkspaceBoundaryPointBackward()
    {
      MoveSelectedWorkspaceBoundaryPoint( Vector3.back );
    }

    public void MoveSelectedWorkspaceBoundaryPointLeft()
    {
      MoveSelectedWorkspaceBoundaryPoint( Vector3.left );
    }

    public void MoveSelectedWorkspaceBoundaryPointRight()
    {
      MoveSelectedWorkspaceBoundaryPoint( Vector3.right );
    }

    public void MoveSelectedWorkspaceBoundaryPointUp()
    {
      MoveSelectedWorkspaceBoundaryPoint( Vector3.up );
    }

    public void MoveSelectedWorkspaceBoundaryPointDown()
    {
      MoveSelectedWorkspaceBoundaryPoint( Vector3.down );
    }

    public void RecordSelectedWorkspaceBoundaryPoint()
    {
      ReachableWorkspaceVisualizer workspace = ActiveReachableWorkspace();
      if ( workspace == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing reachable workspace reference." );
        return;
      }

      workspace.LogSelectedVolumeAnchorCoordinate();
      UpdateStatus();
    }

    public void ApplyBucketTipToSelectedWorkspaceBoundaryPoint()
    {
      ReachableWorkspaceVisualizer workspace = ActiveReachableWorkspace();
      if ( workspace == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing reachable workspace reference." );
        return;
      }

      Transform bucketTip = ActiveBucketTip();
      if ( bucketTip == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing bucket tip reference." );
        return;
      }

      int selectedAnchor = workspace.selectedVolumeAnchorIndex;
      Vector3 bucketTipWorld = bucketTip.position;
      Vector3 bucketTipWorkspaceLocal = workspace.WorldPointToLocal( bucketTipWorld );
      Transform workspaceFrame = workspace.EffectiveWorkspaceFrame;
      string beforeSummary = workspace.SelectedVolumeAnchorLocalSummary();

      workspace.SetSelectedVolumeAnchorWorldPosition( bucketTipWorld );
      MarkWorkspaceDirty( workspace );
      Debug.Log(
        "[ExcavationDemoTaskManager] 应用铲斗尖到可达区域边界点\n" +
        $"  模式：{TaskModeLabel( CurrentDemoTaskMode() )}\n" +
        $"  可达区域：{workspace.name}，边界点索引：{selectedAnchor}\n" +
        $"  坐标系：{workspaceFrame.name}\n" +
        $"  铲斗尖世界坐标：{FormatPreciseVector( bucketTipWorld )}\n" +
        $"  将写入的局部坐标：{FormatPreciseVector( bucketTipWorkspaceLocal )}\n" +
        $"  应用前：{beforeSummary}\n" +
        $"  应用后：{workspace.SelectedVolumeAnchorCoordinateSummary()}\n" +
        $"  可达区域文件：{WorkspaceProfilePathLabel( workspace )}",
        this );
      UpdateStatus();
    }

    [ContextMenu("Create CarryMaterial Workspace Copy")]
    public void CreateCarryMaterialWorkspaceCopy()
    {
      ReachableWorkspaceVisualizer source = moveToDigReachableWorkspace != null ? moveToDigReachableWorkspace : reachableWorkspace;
      if ( source == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing source MoveToDig reachable workspace." );
        return;
      }

      var copyObject = new GameObject( "CarryMaterial" );
      copyObject.transform.SetParent( source.transform.parent, false );
      copyObject.transform.position = Vector3.zero;
      copyObject.transform.rotation = Quaternion.identity;
      copyObject.transform.localScale = Vector3.one;

      var copy = copyObject.AddComponent<ReachableWorkspaceVisualizer>();
      copy.CopyCalibrationFrom( source );
      copy.volumeColor = new Color( 0.30f, 0.62f, 0.42f, 0.08f );
      copy.volumeBoundaryColor = new Color( 0.45f, 0.85f, 0.58f, 0.85f );
      copy.HideReachableVolumeAndBoundaryPoints();

      carryMaterialReachableWorkspace = copy;
      if ( planner != null )
        planner.carryMaterialReachableWorkspace = copy;

#if UNITY_EDITOR
      if ( !Application.isPlaying )
        UnityEditor.Undo.RegisterCreatedObjectUndo( copyObject, "Create CarryMaterial Workspace Copy" );
      UnityEditor.EditorUtility.SetDirty( this );
      if ( planner != null )
        UnityEditor.EditorUtility.SetDirty( planner );
      if ( !Application.isPlaying )
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty( gameObject.scene );
#endif

      UpdateStatus();
    }

    public void PlanMoveToDigAndStart()
    {
      SetModeMoveToDig();
      PlanAndStartTracking();
    }

    public void PlanCarryMaterialAndStart()
    {
      SetModeCarryMaterial();
      PlanAndStartTracking();
    }

    [ContextMenu("重置演示")]
    public void ResetDemo()
    {
      StopCycleTask( false );
      StopFixedAction( false );

      if ( controller != null ) {
        controller.SetCommandInputEnabled( false );
        controller.StopAll();
      }

      if ( disableDecisionRequesterWhenStopped )
        SetDecisionRequesterEnabled( false );
      if ( disableAgentWhenStopped )
        SetAgentEnabled( false );

      state = DemoState.Idle;

      Scene activeScene = SceneManager.GetActiveScene();
#if UNITY_EDITOR
      if ( !string.IsNullOrEmpty( activeScene.path ) ) {
        UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
          activeScene.path,
          new LoadSceneParameters( LoadSceneMode.Single ) );
        return;
      }
#endif
      SceneManager.LoadScene( activeScene.name );
    }

    private void ResolveMissingReferences()
    {
      if ( frameRegistry == null )
        frameRegistry = ExcavatorFrameRegistry.FindNearest( this );

      if ( agent == null )
        agent = GetComponentInChildren<ExcavatorTrajectoryAgent>( true );

      if ( frameRegistry == null && agent != null )
        frameRegistry = agent.frameRegistry;

      if ( planner == null )
        planner = GetComponentInChildren<SimpleTrajectoryPlanner>( true );

      if ( pointPicker == null )
        pointPicker = GetComponentInChildren<DemoPointPicker>( true );

      if ( pointPicker == null && createPointPickerIfMissing && planner != null ) {
        pointPicker = gameObject.AddComponent<DemoPointPicker>();
        pointPicker.planner = planner;
      }

      if ( decisionRequester == null && agent != null )
        decisionRequester = agent.GetComponent<DecisionRequester>();

      if ( controller == null ) {
        if ( agent != null && agent.controller != null )
          controller = agent.controller;
        else
          controller = GetComponentInChildren<ExcavatorController>( true );
      }

      if ( frameRegistry == null && controller != null )
        frameRegistry = controller.GetComponent<ExcavatorFrameRegistry>();

      if ( trajectory == null ) {
        if ( planner != null && planner.trajectory != null )
          trajectory = planner.trajectory;
        else
          trajectory = GetComponentInChildren<TrajectoryProvider>( true );
      }

      if ( reachableWorkspace == null ) {
        if ( planner != null && planner.reachableWorkspace != null )
          reachableWorkspace = planner.reachableWorkspace;
        else
          reachableWorkspace = GetComponentInChildren<ReachableWorkspaceVisualizer>( true );
      }

      if ( moveToDigReachableWorkspace == null ) {
        if ( planner != null && planner.moveToDigReachableWorkspace != null )
          moveToDigReachableWorkspace = planner.moveToDigReachableWorkspace;
        else
          moveToDigReachableWorkspace = reachableWorkspace;
      }

      if ( carryMaterialReachableWorkspace == null && planner != null && planner.carryMaterialReachableWorkspace != null )
        carryMaterialReachableWorkspace = planner.carryMaterialReachableWorkspace;

      if ( planner != null ) {
        if ( planner.moveToDigReachableWorkspace == null )
          planner.moveToDigReachableWorkspace = moveToDigReachableWorkspace;
        if ( planner.carryMaterialReachableWorkspace == null )
          planner.carryMaterialReachableWorkspace = carryMaterialReachableWorkspace;
      }

      if ( trajectory != null && trajectory.frameRegistry == null )
        trajectory.frameRegistry = frameRegistry;

      if ( agent != null && agent.frameRegistry == null )
        agent.frameRegistry = frameRegistry;

      if ( moveToDigReachableWorkspace != null && moveToDigReachableWorkspace.frameRegistry == null )
        moveToDigReachableWorkspace.frameRegistry = frameRegistry;
      if ( carryMaterialReachableWorkspace != null && carryMaterialReachableWorkspace.frameRegistry == null )
        carryMaterialReachableWorkspace.frameRegistry = frameRegistry;

      ApplyReachableWorkspaceForMode( agent != null ? agent.CurrentTaskMode : ExcavatorTrajectoryAgent.TrajectoryTaskMode.MoveToDig );

      if ( bucketTipLocalFrame == null ) {
        if ( frameRegistry != null && frameRegistry.MachineRoot != null )
          bucketTipLocalFrame = frameRegistry.MachineRoot;
        else if ( agent != null && agent.environmentRoot != null )
          bucketTipLocalFrame = agent.environmentRoot;
        else if ( trajectory != null )
          bucketTipLocalFrame = trajectory.transform;
      }
    }

    private void SetDecisionRequesterEnabled( bool enabled )
    {
      if ( decisionRequester != null )
        decisionRequester.enabled = enabled;
    }

    private void SetAgentEnabled( bool enabled )
    {
      if ( agent != null )
        agent.enabled = enabled;
    }

    private void SetBehaviorType( BehaviorType behaviorType )
    {
      if ( agent == null )
        return;

      var behavior = agent.GetComponent<BehaviorParameters>();
      if ( behavior != null )
        behavior.BehaviorType = behaviorType;
    }

    private void UpdateStatus()
    {
      if ( statusText == null )
        return;

      string mode = TaskModeLabel( CurrentDemoTaskMode() );
      string behaviorMode = BehaviorModeLabel();
      string control = controller != null && controller.CommandInputEnabled ? "开启" : "关闭";
      string manualInput = excavationCycleTask != null && excavationCycleTask.IsFixedActionRunning ? "固定动作接管" : state == DemoState.ManualControl ? "键盘接管" : "关闭";
      string autoTaskStatus = excavationCycleTask != null ? excavationCycleTask.AutoTaskStatus : "未绑定";
      string fixedActionStatus = excavationCycleTask != null ? excavationCycleTask.FixedActionStatus : "未绑定";
      string trajectoryVisibility = showTrajectoryPath ? "显示" : "隐藏";
      string progress = agent != null ? agent.CurrentPathProgress.ToString( "P0" ) : "0%";
      string editable = planner != null ? EditablePointLabel( planner.editableControlPoint ) : "未绑定";
      string digPoint = planner != null && planner.TryGetStartPoint( out Vector3 start )
        ? FormatVector( start )
        : "未绑定";
      string dumpPoint = planner != null && planner.TryGetEndPoint( out Vector3 end )
        ? FormatVector( end )
        : "未绑定";
      string planning = planner != null ? planner.LastPlanningSummary : "未绑定";
      string picking = pointPicker != null ? pointPicker.LastPickSummary : "未绑定";
      ReachableWorkspaceVisualizer workspace = ActiveReachableWorkspace();
      string workspaceName = workspace != null ? workspace.name : "未绑定";
      string workspacePoint = workspace != null
        ? workspace.SelectedVolumeAnchorLocalSummary()
        : "未绑定";
      string bucketTipLocal = TryGetBucketTipLocalPosition( out Vector3 tipLocal )
        ? FormatVector( tipLocal )
        : "未绑定";
      statusText.text =
        $"状态：{StateLabel( state )}\n" +
        $"模式：{mode}\n" +
        $"行为模式：{behaviorMode}\n" +
        $"控制输入：{control}\n" +
        $"手动输入：{manualInput}\n" +
        $"自动作业：{autoTaskStatus}\n" +
        $"固定动作：{fixedActionStatus}\n" +
        $"轨迹显示：{trajectoryVisibility}\n" +
        $"进度：{progress}\n" +
        $"编辑点：{editable}\n" +
        $"挖掘点：{digPoint}\n" +
        $"倾倒点：{dumpPoint}\n" +
        $"规划：{planning}\n" +
        $"点选：{picking}\n" +
        $"可达区域：{workspaceName}\n" +
        $"边界点：{workspacePoint}\n" +
        $"铲斗尖局部：{bucketTipLocal}";
    }

    private void EnsureCycleTaskReferences()
    {
      if ( excavationCycleTask == null )
        excavationCycleTask = GetComponent<ExcavationCycleTask>();

      if ( excavationCycleTask == null )
        return;

      excavationCycleTask.agent = agent;
      excavationCycleTask.controller = controller;
      excavationCycleTask.planner = planner;
      excavationCycleTask.trajectory = trajectory;
      excavationCycleTask.decisionRequester = decisionRequester;
      excavationCycleTask.requireInferenceOnlyBehavior = requireInferenceOnlyBehavior;
      excavationCycleTask.disableDecisionRequesterWhenStopped = disableDecisionRequesterWhenStopped;
      excavationCycleTask.disableAgentWhenStopped = disableAgentWhenStopped;
      excavationCycleTask.showTrajectoryPathDuringExecution = showTrajectoryPath;
      excavationCycleTask.returnToInitJointPoseAfterCycle = returnToInitJointPoseAfterCycle;
      excavationCycleTask.initNormalizedJointPose = initNormalizedJointPose;
      excavationCycleTask.returnToInitJointPoseTimeoutSeconds = returnToInitJointPoseTimeoutSeconds;
      BindCycleTaskEvents();
    }

    private void BindCycleTaskEvents()
    {
      if ( excavationCycleTask == null || m_cycleTaskEventsBound )
        return;

      excavationCycleTask.FixedActionCompleted += OnCycleTaskFixedActionCompleted;
      excavationCycleTask.CycleFinished += OnCycleTaskFinished;
      m_cycleTaskEventsBound = true;
    }

    private void UnbindCycleTaskEvents()
    {
      if ( excavationCycleTask != null && m_cycleTaskEventsBound ) {
        excavationCycleTask.FixedActionCompleted -= OnCycleTaskFixedActionCompleted;
        excavationCycleTask.CycleFinished -= OnCycleTaskFinished;
      }

      m_cycleTaskEventsBound = false;
    }

    private void OnCycleTaskFixedActionCompleted()
    {
      if ( state == DemoState.ManualControl )
        SetManualKeyboardTesterEnabled( true );
      UpdateStatus();
    }

    private void OnCycleTaskFinished( bool success, string reason )
    {
      ApplyTrajectoryPathVisibility();
      UpdateStatus();
    }

    private bool IsCycleTaskRunning()
    {
      return excavationCycleTask != null && excavationCycleTask.IsCycleRunning;
    }

    private void StopCycleTask( bool stopMotion )
    {
      if ( excavationCycleTask != null )
        excavationCycleTask.StopTask( stopMotion );

      if ( state == DemoState.AutoTask )
        state = DemoState.Idle;
      UpdateStatus();
    }

    private void StopFixedAction( bool notifyWhenCompleted )
    {
      if ( excavationCycleTask != null )
        excavationCycleTask.StopFixedAction( notifyWhenCompleted );
    }

    private void SyncStateFromCycleTask()
    {
      if ( excavationCycleTask == null )
        return;

      if ( excavationCycleTask.IsCycleRunning ) {
        state = DemoState.AutoTask;
        return;
      }

      if ( state != DemoState.AutoTask )
        return;

      state = excavationCycleTask.LastRunSucceeded ? DemoState.Completed : DemoState.Idle;
    }

    private void ApplyTrajectoryPathVisibility()
    {
      if ( trajectory != null )
        trajectory.SetPathVisualsVisible( showTrajectoryPath );

      if ( excavationCycleTask != null )
        excavationCycleTask.showTrajectoryPathDuringExecution = showTrajectoryPath;
    }

    private void ApplyInitJointPoseIfEnabled()
    {
      if ( !applyInitJointPoseOnStart )
        return;

      EnsureCycleTaskReferences();
      if ( excavationCycleTask == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing ExcavationCycleTask reference." );
        return;
      }

      SetManualKeyboardTesterEnabled( false );
      excavationCycleTask.StartReturnToNormalizedJointPose( "回初始姿态", initNormalizedJointPose.ToVector4(), false );
    }

    private void MoveSelectedWorkspaceBoundaryPoint( Vector3 localDirection )
    {
      ReachableWorkspaceVisualizer workspace = ActiveReachableWorkspace();
      if ( workspace == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing reachable workspace reference." );
        return;
      }

      workspace.MoveSelectedVolumeAnchor( localDirection, WorkspaceMoveFrame(), Mathf.Max( workspaceAnchorMoveStep, 0.01f ) );
      UpdateStatus();
    }

    private static string FormatVector( Vector3 value )
    {
      return $"({value.x:0.00}, {value.y:0.00}, {value.z:0.00})";
    }

    private static string FormatPreciseVector( Vector3 value )
    {
      return $"({value.x:0.0000}, {value.y:0.0000}, {value.z:0.0000})";
    }

    private static string WorkspaceProfilePathLabel( ReachableWorkspaceVisualizer workspace )
    {
      if ( workspace == null )
        return "未绑定";

      if ( !workspace.useWorkspaceProfile )
        return "未启用 profile 写入";

      try {
        return ReachableWorkspaceProfileLoader.DefaultWorkspacePath();
      }
      catch ( System.Exception exception ) {
        return "无法解析：" + exception.Message;
      }
    }

    private bool TryGetBucketTipLocalPosition( out Vector3 localPosition )
    {
      localPosition = Vector3.zero;

      Transform bucketTip = ActiveBucketTip();
      if ( bucketTip == null )
        return false;

      Transform frame = BucketTipLocalFrame();
      if ( frame == null )
        return false;

      localPosition = frame.InverseTransformPoint( bucketTip.position );
      return true;
    }

    private Transform ActiveBucketTip()
    {
      if ( agent != null && agent.bucketTip != null )
        return agent.bucketTip;

      if ( frameRegistry == null && controller != null )
        frameRegistry = controller.GetComponent<ExcavatorFrameRegistry>();

      if ( frameRegistry != null ) {
        frameRegistry.ResolveMissingReferences();
        if ( frameRegistry.BucketTip != null )
          return frameRegistry.BucketTip;
      }

      return controller != null ? FindChildByName( controller.transform, "BucketTip" ) : null;
    }

    private Transform BucketTipLocalFrame()
    {
      if ( bucketTipLocalFrame != null )
        return bucketTipLocalFrame;

      if ( frameRegistry != null && frameRegistry.MachineRoot != null )
        return frameRegistry.MachineRoot;

      if ( agent != null && agent.environmentRoot != null )
        return agent.environmentRoot;

      return trajectory != null ? trajectory.transform : null;
    }

    private Transform WorkspaceMoveFrame()
    {
      ReachableWorkspaceVisualizer workspace = ActiveReachableWorkspace();
      if ( workspace != null && workspace.EffectiveWorkspaceFrame != null )
        return workspace.EffectiveWorkspaceFrame;

      return BucketTipLocalFrame();
    }

    private ReachableWorkspaceVisualizer ActiveReachableWorkspace()
    {
      return ReachableWorkspaceForMode( CurrentDemoTaskMode() );
    }

    private ExcavatorTrajectoryAgent.TrajectoryTaskMode CurrentDemoTaskMode()
    {
      return agent != null ? agent.CurrentTaskMode : m_taskModeWithoutAgent;
    }

    private static Transform FindChildByName( Transform root, string childName )
    {
      if ( root == null || string.IsNullOrEmpty( childName ) )
        return null;

      if ( root.name == childName )
        return root;

      for ( int i = 0; i < root.childCount; i++ ) {
        Transform match = FindChildByName( root.GetChild( i ), childName );
        if ( match != null )
          return match;
      }

      return null;
    }

    private ReachableWorkspaceVisualizer ReachableWorkspaceForMode( ExcavatorTrajectoryAgent.TrajectoryTaskMode mode )
    {
      switch ( mode ) {
        case ExcavatorTrajectoryAgent.TrajectoryTaskMode.CarryMaterial:
          return carryMaterialReachableWorkspace != null ? carryMaterialReachableWorkspace : reachableWorkspace;
        case ExcavatorTrajectoryAgent.TrajectoryTaskMode.MoveToDig:
        default:
          return moveToDigReachableWorkspace != null ? moveToDigReachableWorkspace : reachableWorkspace;
      }
    }

    private void ApplyReachableWorkspaceForMode( ExcavatorTrajectoryAgent.TrajectoryTaskMode mode )
    {
      ReachableWorkspaceVisualizer workspace = ReachableWorkspaceForMode( mode );
      if ( workspace == null )
        return;

      if ( hideInactiveReachableWorkspacesOnModeSwitch ) {
        HideWorkspaceIfInactive( moveToDigReachableWorkspace, workspace );
        HideWorkspaceIfInactive( carryMaterialReachableWorkspace, workspace );
      }

      reachableWorkspace = workspace;
      if ( planner != null ) {
        if ( moveToDigReachableWorkspace != null )
          planner.moveToDigReachableWorkspace = moveToDigReachableWorkspace;
        if ( carryMaterialReachableWorkspace != null )
          planner.carryMaterialReachableWorkspace = carryMaterialReachableWorkspace;
        planner.SetActiveReachableWorkspace( workspace );
      }
    }

    private static void HideWorkspaceIfInactive( ReachableWorkspaceVisualizer candidate, ReachableWorkspaceVisualizer active )
    {
      if ( candidate == null || candidate == active )
        return;

      candidate.HideReachableVolumeAndBoundaryPoints();
    }

    private static void MarkWorkspaceDirty( ReachableWorkspaceVisualizer workspace )
    {
#if UNITY_EDITOR
      if ( workspace == null )
        return;

      if ( Application.isPlaying ) {
        ReachableWorkspacePlayModeSaver.Queue( workspace );
        return;
      }

      UnityEditor.EditorUtility.SetDirty( workspace );
      if ( workspace.gameObject.scene.IsValid() )
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty( workspace.gameObject.scene );
#endif
    }

    private void EditReachableWorkspaceForMode( ExcavatorTrajectoryAgent.TrajectoryTaskMode mode )
    {
      SetTaskMode( mode );

      ReachableWorkspaceVisualizer workspace = ActiveReachableWorkspace();
      if ( workspace == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing reachable workspace reference." );
        return;
      }

      HideAllReachableWorkspaces();
      workspace.ShowReachableVolumeAndBoundaryPoints();
      UpdateStatus();
    }

    private static string StateLabel( DemoState value )
    {
      switch ( value ) {
        case DemoState.Idle:
          return "空闲";
        case DemoState.PathPlanned:
          return "已生成轨迹";
        case DemoState.Tracking:
          return "追踪中";
        case DemoState.ManualControl:
          return "手动控制";
        case DemoState.AutoTask:
          return "自动作业中";
        case DemoState.Completed:
          return "已完成";
        default:
          return value.ToString();
      }
    }

    private static string TaskModeLabel( ExcavatorTrajectoryAgent.TrajectoryTaskMode value )
    {
      switch ( value ) {
        case ExcavatorTrajectoryAgent.TrajectoryTaskMode.MoveToDig:
          return "赴挖掘点";
        case ExcavatorTrajectoryAgent.TrajectoryTaskMode.CarryMaterial:
          return "运土保持";
        default:
          return value.ToString();
      }
    }

    private static string EditablePointLabel( SimpleTrajectoryPlanner.EditableControlPoint value )
    {
      return value == SimpleTrajectoryPlanner.EditableControlPoint.Start ? "挖掘点" : "倾倒点";
    }

    private string BehaviorModeLabel()
    {
      if ( agent == null )
        return "未绑定";

      var behavior = agent.GetComponent<BehaviorParameters>();
      return behavior != null ? behavior.BehaviorType.ToString() : "未绑定";
    }

    private bool CanRunDemoControl()
    {
      if ( !requireInferenceOnlyBehavior )
        return true;

      if ( agent == null )
        return false;

      var behavior = agent.GetComponent<BehaviorParameters>();
      if ( behavior == null )
        return true;

      return behavior.BehaviorType == BehaviorType.InferenceOnly;
    }

    private bool CanUseInferenceControlMode()
    {
      if ( agent == null )
        return false;

      var behavior = agent.GetComponent<BehaviorParameters>();
      if ( behavior == null || behavior.Model != null )
        return true;

      Debug.LogWarning( "[ExcavationDemoTaskManager] Cannot start inference tracking without an assigned model." );
      return false;
    }

    // 启用或禁用手动键盘测试器
    private void SetManualKeyboardTesterEnabled( bool enabled )
    {
      ExcavatorKeyboardTester tester = ManualKeyboardTester();
      if ( tester != null )
        tester.enabled = enabled;
    }

    private ExcavatorKeyboardTester ManualKeyboardTester()
    {
      if ( m_manualKeyboardTester != null )
        return m_manualKeyboardTester;

      if ( controller == null )
        return null;

      m_manualKeyboardTester = controller.GetComponent<ExcavatorKeyboardTester>();
      if ( m_manualKeyboardTester == null )
        m_manualKeyboardTester = controller.gameObject.AddComponent<ExcavatorKeyboardTester>();

      return m_manualKeyboardTester;
    }
  }

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
