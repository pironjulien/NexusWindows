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
            var destination = ExportPath(Str(args, "output_path"), path); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(path, destination, true); capture["output_path"] = destination;
        }
        else capture["output_path"] = path;
        return new JsonObject { ["content"] = new JsonArray { TextBlock(capture), new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(File.ReadAllBytes(path)), ["mimeType"] = Str(capture, "mimeType") } }, ["structuredContent"] = capture.DeepClone() };
    }

    // An exported capture keeps the requested folder and name, with the extension of the format actually sent (WebP, or
    // JPEG for the lossy fallback of the shared image rule): never JPEG bytes under a .webp name.
    internal static string ExportPath(string requested, string capture)
    {
        var full = Path.GetFullPath(requested);
        var extension = Path.GetExtension(capture);
        var asked = Path.GetExtension(full).ToLowerInvariant();
        if (asked == extension || (extension == ".jpg" && asked == ".jpeg")) return full;
        return asked is ".webp" or ".jpg" or ".jpeg" or ".png" ? Path.ChangeExtension(full, extension) : full + extension;
    }
}
