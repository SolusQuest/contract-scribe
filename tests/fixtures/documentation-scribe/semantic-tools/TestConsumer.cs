#nullable enable
using SemanticFixture;
using Xunit;

namespace SemanticConsumer;

public sealed class TestConsumer
{
    [Fact]
    public void Execute_is_callable()
    {
        _ = new Runner().Execute("test");
    }
}
