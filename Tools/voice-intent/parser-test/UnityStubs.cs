// Just enough of UnityEngine for FloorPlan.cs, VoiceIntent.cs and VoiceParser.cs to compile outside Unity.
using System.Text.Json;
namespace UnityEngine
{
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
    }
    public class TextAsset { public string text = ""; public string name = ""; }
    public static class JsonUtility
    {
        public static T FromJson<T>(string json) =>
            JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { IncludeFields = true });
    }
}
