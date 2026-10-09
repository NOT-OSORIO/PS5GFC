using System.Collections.Generic;
using System.Linq;

namespace ProsperoPkgTool.Containers;

public sealed record ProsperoPackageVerificationReport(IReadOnlyList<VerificationCheck> Checks)
{
	public bool HasFailure => Checks.Any((VerificationCheck c) =>
	{
		VerificationState state = c.State;
		return (state == VerificationState.Fail || state == VerificationState.Error) ? true : false;
	});

	public bool HasError => Checks.Any((VerificationCheck c) => c.State == VerificationState.Error);

	public bool IsComplete => !Checks.Any((VerificationCheck c) =>
	{
		bool flag = c.Required;
		if (flag)
		{
			VerificationState state = c.State;
			bool flag2 = (uint)(state - 2) <= 1u;
			flag = flag2;
		}
		return flag;
	});

	public VerificationOutcome Outcome
	{
		get
		{
			if (!HasFailure)
			{
				if (!IsComplete)
				{
					return VerificationOutcome.Incomplete;
				}
				return VerificationOutcome.Passed;
			}
			return VerificationOutcome.Failed;
		}
	}
}
