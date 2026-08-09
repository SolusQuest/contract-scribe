using System.ComponentModel;

namespace ContractScribe.Roslyn.IntegrationTests;

public sealed class LoaderExecutionTraceTests
{
    [Fact]
    public void SnapshotContainsOnlyBoundedClosedExceptionEvidence()
    {
        var trace = new LoaderExecutionTrace();
        trace.Enter(LoaderExecutionPhase.WorkspaceOpen);
        trace.MarkPathsResolved();
        trace.MarkBaselineCaptured();
        trace.MarkToolchainSelected();
        trace.MarkWorkspaceCreated();
        trace.MarkWorkspaceOpenStarted();
        var exception = NestedException(8);
        exception.Data["C:\\sensitive\\repository"] = "credential-value";

        for (var index = 0; index < LoaderExecutionTrace.MaximumExceptionRecords + 3; index++)
        {
            trace.RecordPrimary(LoaderExceptionBoundary.RepositoryLoad, exception);
        }

        var snapshot = trace.Snapshot();

        Assert.Equal(LoaderExecutionPhase.WorkspaceOpen, snapshot.Phase);
        Assert.Equal(LoaderExecutionTrace.MaximumExceptionRecords, snapshot.Exceptions.Count);
        var record = snapshot.Exceptions[0];
        Assert.Equal(LoaderExceptionRole.Primary, record.Role);
        Assert.Equal(LoaderExceptionBoundary.RepositoryLoad, record.Boundary);
        Assert.Equal(LoaderExecutionTrace.MaximumTypeDepth, record.TypeChain.Count);
        Assert.All(record.TypeChain, name =>
        {
            Assert.True(name.Length <= LoaderExecutionTrace.MaximumTypeNameLength);
            Assert.DoesNotContain("sensitive", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("credential", name, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Null(record.NativeErrorCode);
        Assert.True(record.Lifecycle.PathsResolved);
        Assert.True(record.Lifecycle.BaselineCaptured);
        Assert.True(record.Lifecycle.ToolchainSelected);
        Assert.True(record.Lifecycle.WorkspaceCreated);
        Assert.True(record.Lifecycle.WorkspaceOpenStarted);
        Assert.False(record.Lifecycle.WorkspaceOpenCompleted);
        Assert.False(record.Lifecycle.SessionConstructed);
        Assert.False(record.Lifecycle.SessionTransferred);
        Assert.False(record.Lifecycle.CleanupStarted);
        Assert.False(record.Lifecycle.CleanupCompleted);
    }

    [Fact]
    public void PrimaryAndCleanupEvidenceRetainOrderingAndNumericErrors()
    {
        var trace = new LoaderExecutionTrace();
        trace.Enter(LoaderExecutionPhase.WorkspaceOpen);
        trace.RecordPrimary(
            LoaderExceptionBoundary.PostRegistrationLoad,
            new Win32Exception(5, "machine-local sensitive text"));
        trace.MarkCleanupStarted();
        trace.RecordCleanup(
            LoaderExceptionBoundary.WorkspaceCleanup,
            new FixedHResultException(unchecked((int)0x81234567)));
        trace.MarkCleanupCompleted();

        var snapshot = trace.Snapshot();

        Assert.Collection(
            snapshot.Exceptions,
            primary =>
            {
                Assert.Equal(LoaderExceptionRole.Primary, primary.Role);
                Assert.Equal(LoaderExceptionBoundary.PostRegistrationLoad, primary.Boundary);
                Assert.Equal(5, primary.NativeErrorCode);
                Assert.False(primary.Lifecycle.CleanupStarted);
            },
            cleanup =>
            {
                Assert.Equal(LoaderExceptionRole.Cleanup, cleanup.Role);
                Assert.Equal(LoaderExceptionBoundary.WorkspaceCleanup, cleanup.Boundary);
                Assert.Equal(unchecked((int)0x81234567), cleanup.HResult);
                Assert.True(cleanup.Lifecycle.CleanupStarted);
                Assert.False(cleanup.Lifecycle.CleanupCompleted);
            });
        Assert.True(snapshot.Lifecycle.CleanupCompleted);
    }

    [Fact]
    public void ConcurrentObservationRemainsBounded()
    {
        var trace = new LoaderExecutionTrace();

        Parallel.For(0, 64, index =>
        {
            trace.Enter((index & 1) == 0
                ? LoaderExecutionPhase.WorkspaceOpen
                : LoaderExecutionPhase.Compilation);
            trace.RecordPrimary(
                LoaderExceptionBoundary.RepositoryLoad,
                new InvalidOperationException("not retained"));
        });

        var snapshot = trace.Snapshot();
        Assert.Equal(LoaderExecutionTrace.MaximumExceptionRecords, snapshot.Exceptions.Count);
        Assert.All(snapshot.Exceptions, record =>
            Assert.Equal(typeof(InvalidOperationException).FullName, Assert.Single(record.TypeChain)));
    }

    private static Exception NestedException(int depth) => depth == 0
        ? new FixedHResultException(unchecked((int)0x80123456))
        : new InvalidOperationException(
            "sensitive message that must not be retained",
            NestedException(depth - 1));

    private sealed class FixedHResultException : Exception
    {
        public FixedHResultException(int hResult)
            : base("sensitive message that must not be retained")
        {
            HResult = hResult;
        }
    }
}
