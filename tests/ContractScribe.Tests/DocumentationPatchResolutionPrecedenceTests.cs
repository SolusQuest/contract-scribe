using ContractScribe.Patching.Resolution;

namespace ContractScribe.Tests;

public sealed class DocumentationPatchResolutionPrecedenceTests
{
    [Fact]
    public void SelectsFrozenFailurePrecedence()
    {
        var vectors = new[]
        {
            new
            {
                Failures = new[]
                {
                    Failure(0, "block-0", "patch.rejected.unsupported-target"),
                    Failure(1, "block-1", "patch.stale.compilation-context"),
                },
                ExpectedCode = "patch.stale.compilation-context",
                ExpectedBlockId = "block-1",
            },
            new
            {
                Failures = new[]
                {
                    Failure(0, "block-0", "patch.stale.source-bytes"),
                    Failure(1, "block-1", "patch.stale.compilation-context"),
                },
                ExpectedCode = "patch.stale.source-bytes",
                ExpectedBlockId = "block-0",
            },
            new
            {
                Failures = new[]
                {
                    Failure(0, "block-0", "patch.stale.source-span"),
                    Failure(1, "block-1", "patch.stale.compilation-context"),
                },
                ExpectedCode = "patch.stale.source-span",
                ExpectedBlockId = "block-0",
            },
            new
            {
                Failures = new[]
                {
                    Failure(0, "block-0", "patch.stale.source-bytes"),
                    Failure(0, "block-0", "patch.stale.compilation-context"),
                },
                ExpectedCode = "patch.stale.compilation-context",
                ExpectedBlockId = "block-0",
            },
            new
            {
                Failures = new[]
                {
                    Failure(0, "block-0", "patch.rejected.non-writable-target"),
                    Failure(0, "block-0", "patch.stale.source-bytes"),
                },
                ExpectedCode = "patch.stale.source-bytes",
                ExpectedBlockId = "block-0",
            },
            new
            {
                Failures = new[]
                {
                    Failure(0, "block-0", "patch.rejected.ambiguous-target"),
                    Failure(0, "block-0", "patch.rejected.unsupported-target"),
                },
                ExpectedCode = "patch.rejected.unsupported-target",
                ExpectedBlockId = "block-0",
            },
        };

        foreach (var vector in vectors)
        {
            var primary = DocumentationPatchResolver.SelectPrimary(vector.Failures);

            Assert.Equal(vector.ExpectedCode, primary.Code);
            Assert.Equal(vector.ExpectedBlockId, primary.BlockId);
        }
    }

    private static DocumentationPatchResolutionFailure Failure(
        int blockIndex,
        string blockId,
        string code) =>
        new(blockIndex, blockId, code);
}
