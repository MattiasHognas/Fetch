using System.Text.Json;

namespace Fetch.Tools;

public sealed class PhaseCompleteTool : ITool, INativeTool
{
    public string Name => "phase_complete";
    public string Description => "Request completion of the current phase after satisfying its exit conditions. Call this instead of replying with PHASE_DONE text.";
    public ApprovalMode Approval => ApprovalMode.Auto;

    public Task<string> RunAsync(string input)
    {
        var summary = string.IsNullOrWhiteSpace(input) ? "" : input.Trim();
        return Task.FromResult(string.IsNullOrWhiteSpace(summary)
            ? "Phase completion requested."
            : $"Phase completion requested. Summary: {summary}");
    }

    public object GetParametersSchema() => NativeToolJson.ObjectSchema(new Dictionary<string, object?>
    {
        ["summary"] = NativeToolJson.StringProperty("Optional one-sentence note about why the current phase is complete.")
    });

    public string ConvertArguments(JsonElement arguments)
    {
        return NativeToolJson.TryGetString(arguments, "summary", out var summary, allowEmpty: true)
            ? summary.Trim()
            : "";
    }
}