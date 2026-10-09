using System.Collections.Generic;

namespace ProsperoPkgTool.Containers;

public sealed record VerificationResult(bool IsValid, IReadOnlyList<VerificationIssue> Issues);
