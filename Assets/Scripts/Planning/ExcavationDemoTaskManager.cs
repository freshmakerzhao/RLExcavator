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
      Completed
    }

    [Header("References")]
    public ExcavatorTrajectoryAgent agent;
    public ExcavatorController controller;
    public SimpleTrajectoryPlanner planner;
    public TrajectoryProvider trajectory;
    public DecisionRequester decisionRequester;
    public Text statusText;

    [Header("Calibration")]
    [Tooltip("用于显示铲斗尖局部坐标的参考系。建议绑定 TrajectoryEnvironment 或 RLExcavatorTraj。为空时自动使用 agent.environmentRoot 或 TrajectoryProvider。")]
    public Transform bucketTipLocalFrame;

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

    [Header("Demo path")]
    [Tooltip("Demo 启动时隐藏 TrajectoryProvider 中残留的默认路径，等待用户点击生成轨迹后再显示。")]
    public bool hideTrajectoryUntilPlannedOnStart = true;

    [Tooltip("开启后必须先点击生成轨迹，再允许开始追踪。")]
    public bool requirePathBeforeTracking = true;

    [Header("Runtime")]
    public DemoState state = DemoState.Idle;

    private void Awake()
    {
      ResolveMissingReferences();
    }

    private void Start()
    {
      if ( CanRunDemoControl() && agent != null && disableRandomTaskModeOnStart )
        agent.randomizeTaskModeOnEpisodeBegin = false;

      if ( CanRunDemoControl() && controller != null && disableCommandInputOnStart )
        controller.SetCommandInputEnabled( false );

      if ( CanRunDemoControl() && disableDecisionRequesterWhenStopped )
        SetDecisionRequesterEnabled( false );

      if ( CanRunDemoControl() && trajectory != null && hideTrajectoryUntilPlannedOnStart )
        trajectory.SetPathVisualsVisible( false );

      UpdateStatus();
    }

    private void Update()
    {
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
      if ( agent == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing trajectory agent reference." );
        return;
      }

      agent.taskMode = mode;
      agent.randomizeTaskModeOnEpisodeBegin = false;
      UpdateStatus();
    }

    public void SelectPlanStartPoint()
    {
      if ( planner == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing planner reference." );
        return;
      }

      planner.SelectStartPoint();
      UpdateStatus();
    }

    public void SelectPlanEndPoint()
    {
      if ( planner == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing planner reference." );
        return;
      }

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

      if ( trajectory != null )
        trajectory.SetPathVisualsVisible( true );
      state = DemoState.PathPlanned;
      UpdateStatus();
    }

    [ContextMenu("开始追踪")]
    public void StartTracking()
    {
      if ( agent == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing trajectory agent reference." );
        return;
      }

      if ( !CanRunDemoControl() )
        return;

      if ( requirePathBeforeTracking && state != DemoState.PathPlanned ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Please plan a path before starting tracking." );
        return;
      }

      if ( controller == null ) {
        Debug.LogWarning( "[ExcavationDemoTaskManager] Missing controller reference." );
        return;
      }

      controller.SetCommandInputEnabled( true );
      SetDecisionRequesterEnabled( true );
      state = DemoState.Tracking;
      UpdateStatus();
    }

    [ContextMenu("停止追踪")]
    public void StopTracking()
    {
      if ( CanRunDemoControl() ) {
        if ( controller != null )
          controller.SetCommandInputEnabled( false );

        if ( disableDecisionRequesterWhenStopped )
          SetDecisionRequesterEnabled( false );
      }

      if ( state == DemoState.Tracking )
        state = DemoState.Idle;

      UpdateStatus();
    }

    public void PlanAndStartTracking()
    {
      PlanPath();
      StartTracking();
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
      if ( controller != null ) {
        controller.SetCommandInputEnabled( false );
        controller.StopAll();
      }

      if ( disableDecisionRequesterWhenStopped )
        SetDecisionRequesterEnabled( false );

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
      if ( agent == null )
        agent = GetComponentInChildren<ExcavatorTrajectoryAgent>( true );

      if ( planner == null )
        planner = GetComponentInChildren<SimpleTrajectoryPlanner>( true );

      if ( decisionRequester == null && agent != null )
        decisionRequester = agent.GetComponent<DecisionRequester>();

      if ( controller == null ) {
        if ( agent != null && agent.controller != null )
          controller = agent.controller;
        else
          controller = GetComponentInChildren<ExcavatorController>( true );
      }

      if ( trajectory == null ) {
        if ( planner != null && planner.trajectory != null )
          trajectory = planner.trajectory;
        else
          trajectory = GetComponentInChildren<TrajectoryProvider>( true );
      }

      if ( bucketTipLocalFrame == null ) {
        if ( agent != null && agent.environmentRoot != null )
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

    private void UpdateStatus()
    {
      if ( statusText == null )
        return;

      string mode = agent != null ? TaskModeLabel( agent.CurrentTaskMode ) : "未绑定";
      string control = controller != null && controller.CommandInputEnabled ? "开启" : "关闭";
      string progress = agent != null ? agent.CurrentPathProgress.ToString( "P0" ) : "0%";
      string editable = planner != null ? EditablePointLabel( planner.editableControlPoint ) : "未绑定";
      string startpoint = planner != null && planner.TryGetStartPoint( out Vector3 start )
        ? FormatVector( start )
        : "未绑定";
      string endpoint = planner != null && planner.TryGetEndPoint( out Vector3 end )
        ? FormatVector( end )
        : "未绑定";
      string bucketTipLocal = TryGetBucketTipLocalPosition( out Vector3 tipLocal )
        ? FormatVector( tipLocal )
        : "未绑定";
      statusText.text =
        $"状态：{StateLabel( state )}\n" +
        $"模式：{mode}\n" +
        $"控制输入：{control}\n" +
        $"进度：{progress}\n" +
        $"编辑点：{editable}\n" +
        $"起点：{startpoint}\n" +
        $"终点：{endpoint}\n" +
        $"铲斗尖局部：{bucketTipLocal}";
    }

    private static string FormatVector( Vector3 value )
    {
      return $"({value.x:0.00}, {value.y:0.00}, {value.z:0.00})";
    }

    private bool TryGetBucketTipLocalPosition( out Vector3 localPosition )
    {
      localPosition = Vector3.zero;

      if ( agent == null || agent.bucketTip == null )
        return false;

      Transform frame = BucketTipLocalFrame();
      if ( frame == null )
        return false;

      localPosition = frame.InverseTransformPoint( agent.bucketTip.position );
      return true;
    }

    private Transform BucketTipLocalFrame()
    {
      if ( bucketTipLocalFrame != null )
        return bucketTipLocalFrame;

      if ( agent != null && agent.environmentRoot != null )
        return agent.environmentRoot;

      return trajectory != null ? trajectory.transform : null;
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
      return value == SimpleTrajectoryPlanner.EditableControlPoint.Start ? "起点" : "终点";
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
  }
}
