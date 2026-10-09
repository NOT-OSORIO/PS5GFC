namespace ProsperoPkgTool.Gp5;

public sealed record ValidationIssue(ValidationSeverity Severity, string Code, string Message, string? Path = null);
