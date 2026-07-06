using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace RLExcavator
{
  public class MachineProfileLoader : MonoBehaviour
  {
    [Tooltip("启动时读取 shared/machine_profile.json。关闭后仍可通过静态 LoadFromPath/LoadFromJson 使用。")]
    public bool loadOnStart = true;

    [Tooltip("启动读取成功后打印 action_order、正反向速度与 obs 维度，作为场景侧接线自检。")]
    public bool logProfileOnStart = true;

    public MachineProfile Profile { get; private set; }

    private void Start()
    {
      if ( !loadOnStart )
        return;

      try {
        Profile = Load();
        if ( logProfileOnStart )
          Debug.Log( FormatStartupLog( Profile ), this );
      }
      catch ( Exception exception ) {
        Debug.LogError( "[MachineProfile] Load failed: " + exception.Message, this );
      }
    }

    public static MachineProfile Load()
    {
      return LoadFromPath( DefaultProfilePath() );
    }

    public static MachineProfile LoadFromPath( string path )
    {
      if ( string.IsNullOrEmpty( path ) )
        throw new ArgumentException( "Profile path must not be empty.", nameof( path ) );
      if ( !File.Exists( path ) )
        throw new FileNotFoundException( "Machine profile not found.", path );

      return LoadFromJson( File.ReadAllText( path ) );
    }

    public static MachineProfile LoadFromJson( string json )
    {
      if ( string.IsNullOrEmpty( json ) )
        throw new ArgumentException( "Profile JSON must not be empty.", nameof( json ) );

      string[] actionOrder = ExtractStringArray( json, "action_order" );
      string actuatorsJson = ExtractObject( json, "actuators" );
      var actuators = new List<MachineActuatorProfile>();

      for ( int i = 0; i < actionOrder.Length; i++ ) {
        string actuatorName = actionOrder[ i ];
        string actuatorJson = ExtractObject( actuatorsJson, actuatorName );
        actuators.Add( ParseActuator( actuatorName, actuatorJson ) );
      }

      return new MachineProfile(
        ExtractString( json, "schema_version" ),
        ExtractString( json, "machine_id" ),
        ExtractString( json, "machine_type" ),
        ExtractString( json, "reachable_workspace_file" ),
        actionOrder,
        actuators.ToArray(),
        ParseObservationSchema( ExtractObject( json, "observation_schema" ) ),
        ParseTaskProfile( ExtractObject( json, "task_profile" ) ) );
    }

    public static string DefaultProfilePath()
    {
      string projectSharedPath = Path.GetFullPath( Path.Combine( Application.dataPath, "../../shared/machine_profile.json" ) );
      if ( File.Exists( projectSharedPath ) )
        return projectSharedPath;

      return Path.Combine( Application.streamingAssetsPath, "machine_profile.json" );
    }

    public static string FormatStartupLog( MachineProfile profile )
    {
      if ( profile == null )
        throw new ArgumentNullException( nameof( profile ) );

      string[] actionOrder = profile.ActionOrder;
      var speeds = new List<string>();
      for ( int i = 0; i < actionOrder.Length; i++ ) {
        MachineActuatorProfile actuator = profile.GetActuator( actionOrder[ i ] );
        speeds.Add( actuator.Name
          + "=+" + actuator.MaxSpeedPositive.ToString( "0.###", CultureInfo.InvariantCulture )
          + "/-" + actuator.MaxSpeedNegative.ToString( "0.###", CultureInfo.InvariantCulture ) );
      }

      return "[MachineProfile] machine_id=" + profile.MachineId
        + " action_order=[" + string.Join( ",", actionOrder ) + "]"
        + " max_speed_signed=[" + string.Join( ",", speeds.ToArray() ) + "]"
        + " reachable_workspace_file=" + profile.ReachableWorkspaceFile
        + " obs_id=" + profile.ObservationSchema.Id
        + " total_dim=" + profile.ObservationSchema.TotalDim
        + " action_dim=" + profile.ObservationSchema.ActionDim;
    }

    private static MachineActuatorProfile ParseActuator( string name, string actuatorJson )
    {
      float rangeMin = 0f;
      float rangeMax = 0f;
      bool hasFiniteRange = TryExtractRange( actuatorJson, "range", out rangeMin, out rangeMax );

      return new MachineActuatorProfile(
        name,
        ExtractInt( actuatorJson, "action_index" ),
        ExtractString( actuatorJson, "joint_type" ),
        ExtractFloat( actuatorJson, "max_speed_positive" ),
        ExtractFloat( actuatorJson, "max_speed_negative" ),
        hasFiniteRange,
        rangeMin,
        rangeMax,
        ExtractBool( actuatorJson, "range_enabled_required" ),
        ExtractInt( actuatorJson, "sign" ),
        ExtractInt( actuatorJson, "deploy_sign" ),
        ExtractInt( actuatorJson, "deploy_pca_channel" ),
        ExtractString( actuatorJson, "deploy_drive" ) );
    }

    private static MachineObservationSchema ParseObservationSchema( string schemaJson )
    {
      string normalizersJson = ExtractObject( schemaJson, "normalizers" );

      return new MachineObservationSchema(
        ExtractString( schemaJson, "id" ),
        ExtractInt( schemaJson, "total_dim" ),
        ExtractInt( schemaJson, "action_dim" ),
        ExtractInt( schemaJson, "waypoint_lookahead" ),
        ExtractInt( schemaJson, "num_task_modes" ),
        new MachineObservationNormalizers(
          ExtractFloat( normalizersJson, "position_normalizer" ),
          ExtractFloat( normalizersJson, "tip_velocity_scale" ),
          ExtractFloat( normalizersJson, "distance_normalizer" ),
          ExtractFloat( normalizersJson, "tube_radius" ),
          ExtractFloat( normalizersJson, "target_threshold" ),
          ExtractFloat( normalizersJson, "pitch_norm_deg" ) ) );
    }

    private static MachineTaskProfile ParseTaskProfile( string taskProfileJson )
    {
      string bucketPitchTargetsJson = ExtractObject( taskProfileJson, "bucket_pitch_targets_deg" );

      return new MachineTaskProfile(
        ExtractFloat( bucketPitchTargetsJson, "MoveToDig" ),
        ExtractFloat( bucketPitchTargetsJson, "CarryMaterial" ) );
    }

    private static string ExtractString( string json, string key )
    {
      int valueStart = FindValueStart( json, key );
      if ( json[ valueStart ] != '"' )
        throw new FormatException( "Expected JSON string for key: " + key );

      int endIndex;
      return ParseJsonStringAt( json, valueStart, out endIndex );
    }

    private static string[] ExtractStringArray( string json, string key )
    {
      string arrayJson = ExtractArray( json, key );
      var values = new List<string>();
      int index = 1;

      while ( true ) {
        index = SkipWhitespace( arrayJson, index );
        if ( index >= arrayJson.Length )
          throw new FormatException( "Unterminated JSON array for key: " + key );
        if ( arrayJson[ index ] == ']' )
          break;
        if ( arrayJson[ index ] != '"' )
          throw new FormatException( "Expected string item in JSON array for key: " + key );

        int endIndex;
        values.Add( ParseJsonStringAt( arrayJson, index, out endIndex ) );
        index = SkipWhitespace( arrayJson, endIndex );

        if ( index < arrayJson.Length && arrayJson[ index ] == ',' ) {
          index++;
          continue;
        }
        if ( index < arrayJson.Length && arrayJson[ index ] == ']' )
          break;

        throw new FormatException( "Expected comma or array end for key: " + key );
      }

      return values.ToArray();
    }

    private static bool TryExtractRange( string json, string key, out float min, out float max )
    {
      min = 0f;
      max = 0f;

      string arrayJson = ExtractArray( json, key );
      string[] tokens = ExtractPrimitiveArray( arrayJson );
      if ( tokens.Length != 2 )
        throw new FormatException( "Expected range array with two items for key: " + key );

      if ( IsNullToken( tokens[ 0 ] ) || IsNullToken( tokens[ 1 ] ) )
        return false;

      min = ParseFloatToken( tokens[ 0 ], key );
      max = ParseFloatToken( tokens[ 1 ], key );
      return true;
    }

    private static int ExtractInt( string json, string key )
    {
      return int.Parse( ExtractPrimitive( json, key ), NumberStyles.Integer, CultureInfo.InvariantCulture );
    }

    private static float ExtractFloat( string json, string key )
    {
      return ParseFloatToken( ExtractPrimitive( json, key ), key );
    }

    private static bool ExtractBool( string json, string key )
    {
      string value = ExtractPrimitive( json, key );
      if ( value.Equals( "true", StringComparison.OrdinalIgnoreCase ) )
        return true;
      if ( value.Equals( "false", StringComparison.OrdinalIgnoreCase ) )
        return false;

      throw new FormatException( "Expected JSON bool for key: " + key );
    }

    private static string ExtractObject( string json, string key )
    {
      int valueStart = FindValueStart( json, key );
      if ( json[ valueStart ] != '{' )
        throw new FormatException( "Expected JSON object for key: " + key );

      return SliceBalanced( json, valueStart, '{', '}' );
    }

    private static string ExtractArray( string json, string key )
    {
      int valueStart = FindValueStart( json, key );
      if ( json[ valueStart ] != '[' )
        throw new FormatException( "Expected JSON array for key: " + key );

      return SliceBalanced( json, valueStart, '[', ']' );
    }

    private static string ExtractPrimitive( string json, string key )
    {
      int valueStart = FindValueStart( json, key );
      int valueEnd = FindPrimitiveEnd( json, valueStart );
      return json.Substring( valueStart, valueEnd - valueStart ).Trim();
    }

    private static string[] ExtractPrimitiveArray( string arrayJson )
    {
      var values = new List<string>();
      int index = 1;

      while ( true ) {
        index = SkipWhitespace( arrayJson, index );
        if ( index >= arrayJson.Length )
          throw new FormatException( "Unterminated primitive array." );
        if ( arrayJson[ index ] == ']' )
          break;

        int start = index;
        while ( index < arrayJson.Length && arrayJson[ index ] != ',' && arrayJson[ index ] != ']' )
          index++;

        values.Add( arrayJson.Substring( start, index - start ).Trim() );

        if ( index < arrayJson.Length && arrayJson[ index ] == ',' ) {
          index++;
          continue;
        }
        if ( index < arrayJson.Length && arrayJson[ index ] == ']' )
          break;
      }

      return values.ToArray();
    }

    private static int FindValueStart( string json, string key )
    {
      string token = "\"" + key + "\"";
      int keyIndex = json.IndexOf( token, StringComparison.Ordinal );
      if ( keyIndex < 0 )
        throw new FormatException( "Missing JSON key: " + key );

      int colonIndex = json.IndexOf( ':', keyIndex + token.Length );
      if ( colonIndex < 0 )
        throw new FormatException( "Missing ':' after JSON key: " + key );

      int valueStart = SkipWhitespace( json, colonIndex + 1 );
      if ( valueStart >= json.Length )
        throw new FormatException( "Missing JSON value for key: " + key );

      return valueStart;
    }

    private static int FindPrimitiveEnd( string json, int startIndex )
    {
      int index = startIndex;
      while ( index < json.Length && json[ index ] != ',' && json[ index ] != '}' && json[ index ] != ']' )
        index++;

      return index;
    }

    private static int SkipWhitespace( string json, int startIndex )
    {
      int index = startIndex;
      while ( index < json.Length && char.IsWhiteSpace( json[ index ] ) )
        index++;

      return index;
    }

    private static string SliceBalanced( string json, int startIndex, char openChar, char closeChar )
    {
      bool inString = false;
      bool escaped = false;
      int depth = 0;

      for ( int index = startIndex; index < json.Length; index++ ) {
        char current = json[ index ];

        if ( inString ) {
          if ( escaped ) {
            escaped = false;
            continue;
          }
          if ( current == '\\' ) {
            escaped = true;
            continue;
          }
          if ( current == '"' )
            inString = false;
          continue;
        }

        if ( current == '"' ) {
          inString = true;
          continue;
        }
        if ( current == openChar ) {
          depth++;
          continue;
        }
        if ( current == closeChar ) {
          depth--;
          if ( depth == 0 )
            return json.Substring( startIndex, index - startIndex + 1 );
        }
      }

      throw new FormatException( "Unbalanced JSON while reading " + openChar + closeChar + " block." );
    }

    private static string ParseJsonStringAt( string json, int startIndex, out int endIndex )
    {
      if ( json[ startIndex ] != '"' )
        throw new FormatException( "Expected JSON string." );

      var builder = new StringBuilder();

      for ( int index = startIndex + 1; index < json.Length; index++ ) {
        char current = json[ index ];
        if ( current == '"' ) {
          endIndex = index + 1;
          return builder.ToString();
        }
        if ( current != '\\' ) {
          builder.Append( current );
          continue;
        }

        if ( index + 1 >= json.Length )
          throw new FormatException( "Invalid JSON string escape." );

        index++;
        char escaped = json[ index ];
        switch ( escaped ) {
          case '"':
          case '\\':
          case '/':
            builder.Append( escaped );
            break;
          case 'b':
            builder.Append( '\b' );
            break;
          case 'f':
            builder.Append( '\f' );
            break;
          case 'n':
            builder.Append( '\n' );
            break;
          case 'r':
            builder.Append( '\r' );
            break;
          case 't':
            builder.Append( '\t' );
            break;
          case 'u':
            if ( index + 4 >= json.Length )
              throw new FormatException( "Invalid JSON unicode escape." );
            string hex = json.Substring( index + 1, 4 );
            builder.Append( (char)Convert.ToInt32( hex, 16 ) );
            index += 4;
            break;
          default:
            throw new FormatException( "Unsupported JSON string escape: \\" + escaped );
        }
      }

      throw new FormatException( "Unterminated JSON string." );
    }

    private static float ParseFloatToken( string token, string key )
    {
      return float.Parse( token, NumberStyles.Float, CultureInfo.InvariantCulture );
    }

    private static bool IsNullToken( string token )
    {
      return token.Equals( "null", StringComparison.OrdinalIgnoreCase );
    }
  }
}
