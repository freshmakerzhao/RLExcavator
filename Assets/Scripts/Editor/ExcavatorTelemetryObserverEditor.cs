using System.Text;
using UnityEditor;
using UnityEngine;

namespace RLExcavator.Editor
{
  [CustomEditor( typeof( ExcavatorTelemetryObserver ) )]
  public class ExcavatorTelemetryObserverEditor : UnityEditor.Editor
  {
    private SerializedProperty m_excavator;
    private SerializedProperty m_frameRegistry;
    private SerializedProperty m_bucketTip;
    private SerializedProperty m_bucketAttitudeReference;
    private SerializedProperty m_bucketPitchFrame;
    private SerializedProperty m_bucketPitchAxisLocal;
    private SerializedProperty m_observedTransforms;
    private SerializedProperty m_autoFindReferences;
    private SerializedProperty m_refreshInFixedUpdate;
    private SerializedProperty m_moveToDigTargetPitchDeg;
    private SerializedProperty m_carryMaterialTargetPitchDeg;

    private bool m_showAdvanced;
    private bool m_showActuatorDetails;
    private bool m_showExtraPoints;

    private void OnEnable()
    {
      m_excavator = serializedObject.FindProperty( "excavator" );
      m_frameRegistry = serializedObject.FindProperty( "frameRegistry" );
      m_bucketTip = serializedObject.FindProperty( "bucketTip" );
      m_bucketAttitudeReference = serializedObject.FindProperty( "bucketAttitudeReference" );
      m_bucketPitchFrame = serializedObject.FindProperty( "bucketPitchFrame" );
      m_bucketPitchAxisLocal = serializedObject.FindProperty( "bucketPitchAxisLocal" );
      m_observedTransforms = serializedObject.FindProperty( "observedTransforms" );
      m_autoFindReferences = serializedObject.FindProperty( "autoFindReferences" );
      m_refreshInFixedUpdate = serializedObject.FindProperty( "refreshInFixedUpdate" );
      m_moveToDigTargetPitchDeg = serializedObject.FindProperty( "moveToDigTargetPitchDeg" );
      m_carryMaterialTargetPitchDeg = serializedObject.FindProperty( "carryMaterialTargetPitchDeg" );
    }

    public override void OnInspectorGUI()
    {
      serializedObject.Update();

      var observer = (ExcavatorTelemetryObserver)target;

      DrawBindings();
      DrawAdvancedSettings();

      serializedObject.ApplyModifiedProperties();

      EditorGUILayout.Space( 8f );
      DrawRuntimeState( observer );
    }

    public override bool RequiresConstantRepaint()
    {
      return Application.isPlaying;
    }

    private void DrawBindings()
    {
      EditorGUILayout.LabelField( "必要绑定", EditorStyles.boldLabel );
      EditorGUILayout.PropertyField( m_frameRegistry, new GUIContent( "Frame Registry" ) );
      EditorGUILayout.PropertyField( m_excavator, new GUIContent( "Excavator" ) );
      EditorGUILayout.PropertyField( m_bucketTip, new GUIContent( "Bucket Tip" ) );
      EditorGUILayout.PropertyField( m_bucketAttitudeReference, new GUIContent( "Bucket Attitude Reference" ) );
      EditorGUILayout.PropertyField( m_bucketPitchFrame, new GUIContent( "Bucket Pitch Frame" ) );
      EditorGUILayout.PropertyField( m_bucketPitchAxisLocal, new GUIContent( "Bucket Pitch Axis Local" ) );
    }

    private void DrawAdvancedSettings()
    {
      EditorGUILayout.Space( 4f );
      m_showAdvanced = EditorGUILayout.Foldout( m_showAdvanced, "高级设置", true );
      if ( !m_showAdvanced )
        return;

      EditorGUI.indentLevel++;
      EditorGUILayout.PropertyField( m_autoFindReferences, new GUIContent( "自动查找引用" ) );
      EditorGUILayout.PropertyField( m_refreshInFixedUpdate, new GUIContent( "物理步刷新" ) );
      EditorGUILayout.PropertyField( m_moveToDigTargetPitchDeg, new GUIContent( "MoveToDig 目标 Pitch" ) );
      EditorGUILayout.PropertyField( m_carryMaterialTargetPitchDeg, new GUIContent( "CarryMaterial 目标 Pitch" ) );
      EditorGUILayout.PropertyField( m_observedTransforms, new GUIContent( "额外观测点" ), true );
      EditorGUI.indentLevel--;
    }

    private void DrawRuntimeState( ExcavatorTelemetryObserver observer )
    {
      EditorGUILayout.LabelField( "只读状态", EditorStyles.boldLabel );

      using ( new EditorGUI.DisabledScope( true ) ) {
        EditorGUILayout.Toggle( "已绑定", observer.targetAssigned );
        EditorGUILayout.TextField( "目标", observer.targetName ?? "" );
        EditorGUILayout.FloatField( "刷新时间", observer.refreshTime );
        EditorGUILayout.Toggle( "控制输入", observer.commandInputEnabled );
        EditorGUILayout.Toggle( "回家复位", observer.isHoming );
        EditorGUILayout.Vector3Field( "根节点位置", observer.rootPosition );
        EditorGUILayout.Vector3Field( "根节点欧拉角", observer.rootEulerAngles );

        EditorGUILayout.Space( 6f );
        EditorGUILayout.LabelField( "铲斗 Pitch", EditorStyles.boldLabel );
        EditorGUILayout.Toggle( "铲尖已绑定", observer.bucketTipAssigned );
        EditorGUILayout.TextField( "铲尖", observer.bucketTipName ?? "" );
        EditorGUILayout.TextField( "Pitch 坐标系", observer.bucketPitchFrameName ?? "" );
        EditorGUILayout.FloatField( "Pitch", observer.bucketPitchDeg );
        EditorGUILayout.FloatField( "Pitch 角速度", observer.bucketPitchVelocityDegPerSec );
        EditorGUILayout.FloatField( "MoveToDig 目标", observer.moveToDigTargetPitchDeg );
        EditorGUILayout.FloatField( "CarryMaterial 目标", observer.carryMaterialTargetPitchDeg );
        EditorGUILayout.FloatField( "MoveToDig 误差", observer.moveToDigPitchErrorDeg );
        EditorGUILayout.FloatField( "CarryMaterial 误差", observer.carryMaterialPitchErrorDeg );

        DrawActuatorOverview( observer );
      }

      DrawOptionalDetails( observer );
    }

    private static void DrawActuatorOverview( ExcavatorTelemetryObserver observer )
    {
      EditorGUILayout.Space( 6f );
      EditorGUILayout.LabelField( "执行器状态", EditorStyles.boldLabel );
      DrawActuatorOverviewRow( observer.boom );
      DrawActuatorOverviewRow( observer.stick );
      DrawActuatorOverviewRow( observer.bucket );
      DrawActuatorOverviewRow( observer.swing );
    }

    private static void DrawActuatorOverviewRow( ActuatorTelemetry telemetry )
    {
      if ( telemetry == null ) {
        EditorGUILayout.LabelField( "未初始化" );
        return;
      }

      string label = string.IsNullOrEmpty( telemetry.label ) ? "Actuator" : telemetry.label;
      string text =
        $"{label}: 约束 {telemetry.boundConstraintCount}, 归一化 {FormatFloat( telemetry.normalizedPosition )}, " +
        $"原始 {FormatFloatArray( telemetry.rawPositions )}, 最大速度 {FormatFloat( telemetry.maxSpeed )}";
      EditorGUILayout.LabelField( text, EditorStyles.wordWrappedMiniLabel );
    }

    private void DrawOptionalDetails( ExcavatorTelemetryObserver observer )
    {
      m_showActuatorDetails = EditorGUILayout.Foldout( m_showActuatorDetails, "执行器约束明细", true );
      if ( m_showActuatorDetails ) {
        EditorGUI.indentLevel++;
        DrawConstraintDetails( observer.boom );
        DrawConstraintDetails( observer.stick );
        DrawConstraintDetails( observer.bucket );
        DrawConstraintDetails( observer.swing );
        EditorGUI.indentLevel--;
      }

      m_showExtraPoints = EditorGUILayout.Foldout( m_showExtraPoints, "额外点明细", true );
      if ( m_showExtraPoints ) {
        EditorGUI.indentLevel++;
        DrawTransformDetails( observer.observedTransformState );
        EditorGUI.indentLevel--;
      }
    }

    private static void DrawConstraintDetails( ActuatorTelemetry actuator )
    {
      if ( actuator == null )
        return;

      EditorGUILayout.LabelField( string.IsNullOrEmpty( actuator.label ) ? "Actuator" : actuator.label, EditorStyles.miniBoldLabel );
      ConstraintTelemetry[] constraints = actuator.constraints;
      if ( constraints == null || constraints.Length == 0 ) {
        EditorGUILayout.LabelField( "无约束。" );
        return;
      }

      for ( int i = 0; i < constraints.Length; i++ ) {
        ConstraintTelemetry item = constraints[ i ];
        if ( item == null ) {
          EditorGUILayout.LabelField( $"[{i}]", "未初始化" );
          continue;
        }

        string name = item.bound ? item.constraintName : "未绑定";
        string text =
          $"[{i}] {name}: 位置 {FormatFloat( item.position )}, 目标速度 {FormatFloat( item.targetSpeed )}, " +
          $"速度控制 {( item.targetSpeedEnabled ? "开" : "关" )}, 行程 {( item.rangeEnabled ? "开" : "关" )} " +
          $"[{FormatFloat( item.rangeMin )}, {FormatFloat( item.rangeMax )}]";
        EditorGUILayout.LabelField( text, EditorStyles.wordWrappedMiniLabel );
      }
    }

    private static void DrawTransformDetails( TransformTelemetry[] transforms )
    {
      if ( transforms == null || transforms.Length == 0 ) {
        EditorGUILayout.LabelField( "无额外点。" );
        return;
      }

      for ( int i = 0; i < transforms.Length; i++ ) {
        TransformTelemetry item = transforms[ i ];
        if ( item == null ) {
          EditorGUILayout.LabelField( $"[{i}]", "未初始化" );
          continue;
        }

        string name = item.bound ? item.transformName : "未绑定";
        EditorGUILayout.LabelField(
          $"[{i}] {name}: 世界 {FormatVector3( item.position )}, 本地 {FormatVector3( item.localPosition )}",
          EditorStyles.wordWrappedMiniLabel );
      }
    }

    private static string FormatFloatArray( float[] values )
    {
      if ( values == null || values.Length == 0 )
        return "[]";

      var builder = new StringBuilder();
      builder.Append( '[' );
      for ( int i = 0; i < values.Length; i++ ) {
        if ( i > 0 )
          builder.Append( ", " );
        builder.Append( FormatFloat( values[ i ] ) );
      }
      builder.Append( ']' );
      return builder.ToString();
    }

    private static string FormatVector3( Vector3 value )
    {
      return $"({FormatFloat( value.x )}, {FormatFloat( value.y )}, {FormatFloat( value.z )})";
    }

    private static string FormatFloat( float value )
    {
      return float.IsNaN( value ) ? "NaN" : value.ToString( "0.###" );
    }
  }
}
