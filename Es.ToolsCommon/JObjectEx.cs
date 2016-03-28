using Newtonsoft.Json.Linq;

namespace Es.ToolsCommon
{
    public static class JObjectEx
    {
        public static T GetValue<T>(this JObject jObject, string path, T defaultValue)
        {
            var temp = jObject.SelectToken(path, false);
            return temp == null ? defaultValue : temp.ToObject<T>();
        }
    }
}