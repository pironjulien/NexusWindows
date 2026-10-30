using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

static partial class McpServer
{
    static JsonObject FullScreenNative(SessionState state, JsonObject args)
    {
        var capture = ObjectOf(JsonSerializer.SerializeToElement(Capture.Screen(Path.Combine(NativeStateRoot(state.Agent), "captures"))));
        var path = Str(capture, "path");
        if (args["output_path"] is JsonValue)
        {
            var destination = Path.GetFullPath(Str(args, "output_path")); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(path, destination, true); capture["output_path"] = destination;
        }
        else capture["output_path"] = path;
        return new JsonObject { ["content"] = new JsonArray { TextBlock(capture), new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(File.ReadAllBytes(path)), ["mimeType"] = "image/webp" } }, ["structuredContent"] = capture.DeepClone() };
    }
}
