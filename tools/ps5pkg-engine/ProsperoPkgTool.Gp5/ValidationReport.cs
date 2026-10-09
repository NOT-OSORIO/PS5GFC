using System.Collections.Generic;
using System.Linq;

namespace ProsperoPkgTool.Gp5;

public sealed class ValidationReport
{
	private readonly List<ValidationIssue> _issues = new List<ValidationIssue>();

	public IReadOnlyList<ValidationIssue> Issues => _issues;

	public bool IsValid => _issues.All((ValidationIssue issue) => issue.Severity != ValidationSeverity.Error);

	public int ErrorCount => _issues.Count((ValidationIssue issue) => issue.Severity == ValidationSeverity.Error);

	public int WarningCount => _issues.Count((ValidationIssue issue) => issue.Severity == ValidationSeverity.Warning);

	public void Error(string code, string message, string? path = null)
	{
		_issues.Add(new ValidationIssue(ValidationSeverity.Error, code, message, path));
	}

	public void Warning(string code, string message, string? path = null)
	{
		_issues.Add(new ValidationIssue(ValidationSeverity.Warning, code, message, path));
	}

	public void AddRange(IEnumerable<ValidationIssue> issues)
	{
		_issues.AddRange(issues);
	}
}
