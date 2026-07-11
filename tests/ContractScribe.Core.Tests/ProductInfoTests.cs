using ContractScribe.Core;

namespace ContractScribe.Core.Tests;

public sealed class ProductInfoTests
{
    [Fact]
    public void Name_IsContractScribe()
    {
        Assert.Equal("ContractScribe", ProductInfo.Name);
    }
}
