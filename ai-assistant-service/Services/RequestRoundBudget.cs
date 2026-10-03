namespace ai_assistant_service.Services;

/// <summary>One request-wide limit shared by category, filter, repair and conversational model calls.</summary>
public sealed class RequestRoundBudget(int maximumRounds)
{
    private int _spent;
    private int _repairClaimed;

    public bool TrySpend() =>
        Interlocked.Increment(ref _spent) <= maximumRounds;

    public bool TryClaimRepair() =>
        Interlocked.CompareExchange(ref _repairClaimed, 1, 0) == 0;

    public void Spend()
    {
        if (!TrySpend())
            throw new RoundBudgetExceededException();
    }
}

/// <summary>Signals that another decision or model call cannot fit within the request budget.</summary>
public sealed class RoundBudgetExceededException()
    : Exception("The request-wide decision and model round budget was exhausted.");
