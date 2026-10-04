using ContractScribe.Core;

namespace ContractScribe.Tests;

internal static class CampaignTestBatchFixtures
{
    internal static CampaignFixedBatch Singleton(string commitment, string workItemKey)
    {
        var symbol = new SymbolRef("context.synthetic", "M:Synthetic.Widget.Run");
        var key = CampaignPlanner.ComputeTargetKey(commitment, workItemKey, symbol);
        return new(commitment, 100,
            [new(key, workItemKey, symbol, true, true, new('a', 64), new('b', 64))], [key]);
    }
}
