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

        void LocalHelper()
        {
            _ = new Runner().Execute("local-test");
        }

        LocalHelper();
    }
}
