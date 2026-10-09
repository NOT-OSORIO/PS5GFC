namespace ProsperoPkgTool.Containers;

public sealed record VerificationIssue(string Code, string Message, bool IsError);
