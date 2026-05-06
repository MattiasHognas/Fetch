namespace Fetch.Approval;

public enum ApprovalDecision
{
    Allow, Ask, Deny, DryRun
}

public sealed class ApprovalPolicy(AgentConfig config)
{
    private readonly AgentConfig _config = config;

    public ApprovalDecision Decide(ITool tool)
    {
        return tool.Approval == ApprovalMode.Deny
            ? ApprovalDecision.Deny
            : _config.ApprovalMode switch
            {
                "read-only" => tool.Approval == ApprovalMode.Auto ? ApprovalDecision.Allow : ApprovalDecision.Deny,
                "dry-run" => IsMutationTool(tool) ? ApprovalDecision.DryRun : ApprovalDecision.Allow,
                "auto-safe" => IsMutationTool(tool) ? ApprovalDecision.Ask : ApprovalDecision.Allow,
                "ask" => tool.Approval == ApprovalMode.Ask ? ApprovalDecision.Ask : ApprovalDecision.Allow,
                "yolo" => ApprovalDecision.Allow,
                _ => tool.Approval == ApprovalMode.Ask ? ApprovalDecision.Ask : ApprovalDecision.Allow
            };
    }

    private static bool IsMutationTool(ITool tool) => tool.Name is "apply_diff" or "apply_patch" or "create_file" or "delete_file" or "rename_file" or "run_command";
}
