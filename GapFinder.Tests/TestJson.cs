using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using MediaBrowser.Model.Serialization;

namespace GapFinder.Tests
{
    /// <summary>
    /// IJsonSerializer on top of System.Text.Json. A DispatchProxy, because the interface has many members unused
    /// here; the plugin only needs SerializeToString/DeserializeFromString and the file variants.
    /// </summary>
    public class TestJson : DispatchProxy
    {
        private static readonly System.Text.Json.JsonSerializerOptions Options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        public static IJsonSerializer Create() => Create<IJsonSerializer, TestJson>();

        protected override object Invoke(MethodInfo method, object[] args)
        {
            switch (method.Name)
            {
                case "SerializeToString":
                    return Serialize(args[0]);
                case "SerializeToFile":
                    File.WriteAllText((string)args[1], Serialize(args[0]));
                    return null;
                case "DeserializeFromString":
                    return Deserialize((string)args[0], method.IsGenericMethod ? method.GetGenericArguments()[0] : (Type)args[1]);
                case "DeserializeFromFile":
                    return Deserialize(File.ReadAllText((string)args[method.IsGenericMethod ? 0 : 1]), method.IsGenericMethod ? method.GetGenericArguments()[0] : (Type)args[0]);
                default:
                    throw new NotSupportedException(method.Name);
            }
        }

        private static string Serialize(object obj) => JsonSerializer.Serialize(obj, obj?.GetType() ?? typeof(object), Options);

        private static object Deserialize(string text, Type type) => JsonSerializer.Deserialize(text, type, Options);
    }
}
