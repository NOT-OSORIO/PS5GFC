namespace ProsperoPkgTool.Containers;

public sealed record VerificationCheck(string Name, VerificationState State, string Detail, bool Required = true);
