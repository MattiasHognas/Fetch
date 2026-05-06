namespace Fetch.Core;

public enum ApprovalMode
{
    Auto, Ask, Deny
}

public interface ITool
{
    string Name
    {
        get;
    }
    string Description
    {
        get;
    }
    ApprovalMode Approval
    {
        get;
    }
    Task<string> RunAsync(string input);
}

public interface IPreviewableTool
{
    Task<string> PreviewAsync(string input);
}
