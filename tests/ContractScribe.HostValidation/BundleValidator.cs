using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace ContractScribe.HostValidation;

public sealed record BundleContext(
    string Root,
    ProtocolManifest Protocol,
    VectorCatalog Vectors,
    ArtifactLock Lock);

public static class BundleValidator
{
    public const string ProtocolRelativePath = "tests/fixtures/m1-host-validation/v1/protocol.json";
    public const string VectorsRelativePath = "tests/fixtures/m1-host-validation/v1/vectors.json";
    public const string CrosswalkRelativePath = "tests/fixtures/m1-host-validation/v1/requirements-crosswalk.json";
    public const string AuthoritativeSourcesRelativePath = "tests/fixtures/m1-host-validation/v1/authoritative-source-keys.json";
    public const string ProtectedInputsRelativePath = "tests/fixtures/m1-host-validation/v1/protected-inputs.json";
    public const string LockRelativePath = "tests/fixtures/m1-host-validation/v1/artifact-lock.json";
    public const string ReviewRelativePath = "tests/fixtures/m1-host-validation/v1/independent-review.json";

    private const int ManifestLimit = 4 * 1024 * 1024;

    public static BundleContext Validate(
        string root,
        bool requireReview = false,
        string? reviewPath = null,
        bool allowProtectedInputDrift = false)
    {
        root = RepositoryPaths.NormalizeRoot(root);
        var protocolPath = RepositoryPaths.ResolveConfined(root, ProtocolRelativePath);
        var vectorsPath = RepositoryPaths.ResolveConfined(root, VectorsRelativePath);
        var crosswalkPath = RepositoryPaths.ResolveConfined(root, CrosswalkRelativePath);
        var lockPath = RepositoryPaths.ResolveConfined(root, LockRelativePath);

        SchemaValidation.Validate(
            protocolPath,
            RepositoryPaths.ResolveConfined(root, "schemas/validation/m1-host-validation-protocol-v1.schema.json"));
        SchemaValidation.Validate(
            vectorsPath,
            RepositoryPaths.ResolveConfined(root, "schemas/validation/m1-host-validation-vectors-v1.schema.json"));

        var protocol = CanonicalJson.DeserializeStrict<ProtocolManifest>(protocolPath, ManifestLimit);
        var vectors = CanonicalJson.DeserializeStrict<VectorCatalog>(vectorsPath, ManifestLimit);
        var artifactLock = CanonicalJson.DeserializeStrict<ArtifactLock>(lockPath, ManifestLimit, requireCanonical: true);

        ValidateProtocolSemantics(root, protocol, vectors);
        StaticValidatorRegistry.ValidateRegistry(protocol.RequiredValidators, vectors.Vectors);
        ValidateLock(root, protocol, artifactLock);
        ValidateCrosswalk(root, protocol, vectors, crosswalkPath);
        if (!allowProtectedInputDrift)
        {
            ValidateProtectedInputs(root);
        }
        ValidateSubjectTemplates(root);
        ValidateProjectBoundary(root);
        ValidateProtocolCorpusSafety(root);

        if (requireReview)
        {
            ValidateReview(
                root,
                reviewPath ?? ReviewRelativePath,
                artifactLock.BundleId);
        }

        return new BundleContext(root, protocol, vectors, artifactLock);
    }

    public static ArtifactLock CreateLock(string root)
    {
        root = RepositoryPaths.NormalizeRoot(root);
        var protocolPath = RepositoryPaths.ResolveConfined(root, ProtocolRelativePath);
        SchemaValidation.Validate(
            protocolPath,
            RepositoryPaths.ResolveConfined(root, "schemas/validation/m1-host-validation-protocol-v1.schema.json"));
        var protocol = CanonicalJson.DeserializeStrict<ProtocolManifest>(protocolPath, ManifestLimit);
        ValidateInventoryPaths(protocol.ArtifactInventory);

        var entries = protocol.ArtifactInventory
            .Order(StringComparer.Ordinal)
            .Select(path => new ArtifactIdentity(
                path,
                CanonicalJson.Sha256File(RepositoryPaths.ResolveConfined(root, path))))
            .ToArray();
        var bundleId = ComputeBundleId(entries);
        var artifactLock = new ArtifactLock("contractscribe-m1-host-validation-artifact-lock-v1", bundleId, entries);
        CanonicalJson.WriteCanonical(Path.Join(root, LockRelativePath.Replace('/', Path.DirectorySeparatorChar)), artifactLock);
        return artifactLock;
    }

    public static ProtectedInputManifest CreateProtectedInputs(string root)
    {
        root = RepositoryPaths.NormalizeRoot(root);
        var path = RepositoryPaths.ResolveConfined(root, ProtectedInputsRelativePath);
        var manifest = CanonicalJson.DeserializeStrict<ProtectedInputManifest>(path, ManifestLimit);
        if (manifest.FormatVersion != "contractscribe-m1-host-validation-protected-inputs-v1")
        {
            throw new ProtocolException("HV163_PROTECTED_INPUT_VERSION");
        }

        var entries = ExpandProtectedInputPaths(root, manifest.Roots)
            .Select(relativePath => new ArtifactIdentity(
                relativePath,
                CanonicalJson.Sha256File(RepositoryPaths.ResolveConfined(root, relativePath))))
            .ToArray();
        var updated = manifest with { Entries = entries };
        CanonicalJson.WriteCanonical(path, updated);
        return updated;
    }

    public static ReviewRecord ValidateReview(string root, string reviewPath, string bundleId)
    {
        root = RepositoryPaths.NormalizeRoot(root);
        var relativeReviewPath = Path.IsPathRooted(reviewPath)
            ? RepositoryPaths.ToRepositoryRelative(root, reviewPath)
            : reviewPath;
        var fullReviewPath = RepositoryPaths.ResolveConfined(root, relativeReviewPath);
        SchemaValidation.Validate(
            fullReviewPath,
            RepositoryPaths.ResolveConfined(root, "schemas/validation/m1-host-validation-review-v1.schema.json"),
            requireCanonical: true);
        var review = CanonicalJson.DeserializeStrict<ReviewRecord>(fullReviewPath, ManifestLimit, requireCanonical: true);
        if (review.BundleId != bundleId
            || review.Verdict != "accepted"
            || review.BlockingFindingIds.Count != 0
            || !review.ReviewedHead.All(Uri.IsHexDigit)
            || review.ReviewedHead.Length != 40)
        {
            throw new ProtocolException("HV121_REVIEW_NOT_ACCEPTED");
        }
        PublicSafetyScanner.EnsureSafeBytes(File.ReadAllBytes(fullReviewPath));
        if (review.ReviewId != ComputeReviewId(review))
        {
            throw new ProtocolException("HV166_REVIEW_ID_MISMATCH");
        }
        ValidateReviewedCommit(root, review, bundleId);

        return review;
    }

    public static string ComputeSourceConfigurationId(SubjectSourceConfiguration source)
    {
        var identity = new
        {
            source.HostRevision,
            source.SourceRoots,
            source.SourceAndBuildInputs,
            source.FailureRegistry,
            source.CalibratedBounds,
            source.BuildRecipe,
            source.CommandContract,
            source.ContractBaseline,
            source.EnvironmentPolicy,
            source.Workflow
        };
        return $"source.{CanonicalJson.Sha256(CanonicalJson.SerializeCanonical(identity))}";
    }

    public static string ComputeReviewId(ReviewRecord review)
    {
        var identity = new
        {
            review.FormatVersion,
            review.BundleId,
            review.ReviewedHead,
            review.ReviewerKind,
            review.RelaySessionId,
            review.RelayTaskId,
            review.Verdict,
            review.BlockingFindingIds,
            review.ReviewedAtUtc
        };
        return $"review.{CanonicalJson.Sha256(CanonicalJson.SerializeCanonical(identity))}";
    }

    public static void ValidateCommitBoundArtifacts(
        string root,
        string revision,
        IEnumerable<ArtifactIdentity> identities)
    {
        root = RepositoryPaths.NormalizeRoot(root);
        if (revision.Length != 40
            || !revision.All(Uri.IsHexDigit)
            || RunGit(root, ["cat-file", "-e", $"{revision}^{{commit}}"], captureOutput: false).ExitCode != 0)
        {
            throw new ProtocolException("HV225_SOURCE_REVISION_INVALID");
        }
        foreach (var identity in identities)
        {
            var result = RunGit(root, ["show", $"{revision}:{identity.Path}"], captureOutput: true);
            if (result.ExitCode != 0 || CanonicalJson.Sha256(result.Output) != identity.Sha256)
            {
                throw new ProtocolException("HV226_SOURCE_REVISION_MISMATCH");
            }
        }
    }

    public static void ValidateCommitAncestry(string root, string ancestor, string descendant)
    {
        root = RepositoryPaths.NormalizeRoot(root);
        if (ancestor.Length != 40
            || descendant.Length != 40
            || !ancestor.All(Uri.IsHexDigit)
            || !descendant.All(Uri.IsHexDigit)
            || RunGit(root, ["cat-file", "-e", $"{descendant}^{{commit}}"], captureOutput: false).ExitCode != 0
            || RunGit(root, ["merge-base", "--is-ancestor", ancestor, descendant], captureOutput: false).ExitCode != 0)
        {
            throw new ProtocolException("HV225_SOURCE_REVISION_INVALID");
        }
    }

    public static string ComputeBundleId(IEnumerable<ArtifactIdentity> entries)
    {
        using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries.OrderBy(entry => entry.Path, StringComparer.Ordinal))
        {
            incrementalHash.AppendData(Encoding.UTF8.GetBytes(entry.Path));
            incrementalHash.AppendData([0]);
            incrementalHash.AppendData(Encoding.ASCII.GetBytes(entry.Sha256));
            incrementalHash.AppendData([0]);
        }

        return $"m1hvp1.{Convert.ToHexString(incrementalHash.GetHashAndReset()).ToLowerInvariant()}";
    }

    private static void ValidateProtocolSemantics(string root, ProtocolManifest protocol, VectorCatalog catalog)
    {
        if (protocol.FormatVersion != "contractscribe-m1-host-validation-protocol-v1"
            || catalog.FormatVersion != "contractscribe-m1-host-validation-vectors-v1"
            || protocol.Baseline.MergeCommit != "bb4654edc180e2953dda6b89a29211b18778b78e")
        {
            throw new ProtocolException("HV122_PROTOCOL_VERSION_OR_BASELINE");
        }

        ValidateInventoryPaths(protocol.ArtifactInventory);
        if (protocol.Baseline.ContractManifestSha256
            != CanonicalJson.Sha256File(RepositoryPaths.ResolveConfined(root, protocol.Baseline.ContractManifest)))
        {
            throw new ProtocolException("HV123_CONTRACT_BASELINE_DRIFT");
        }

        var expectedCells = new[] { "ubuntu-x64", "windows-x64" };
        if (!protocol.RequiredCells.Select(cell => cell.CellId).SequenceEqual(expectedCells, StringComparer.Ordinal)
            || protocol.RequiredCells.Any(cell => cell.Architecture != "X64"))
        {
            throw new ProtocolException("HV124_MATRIX_INVALID");
        }

        EnsureUnique(catalog.Vectors.Select(vector => vector.VectorId), "HV125_DUPLICATE_VECTOR");
        var cellIds = protocol.RequiredCells.Select(cell => cell.CellId).ToHashSet(StringComparer.Ordinal);
        foreach (var vector in catalog.Vectors)
        {
            if (vector.InvocationCount != vector.RunIds.Count
                || vector.InvocationCount < 1
                || vector.Cells.Count == 0
                || vector.Cells.Any(cell => !cellIds.Contains(cell)))
            {
                throw new ProtocolException("HV126_VECTOR_CARDINALITY");
            }

            EnsureUnique(vector.RunIds, "HV127_DUPLICATE_RUN_ID");
            if (vector.VectorId.Contains("determinism", StringComparison.Ordinal)
                && (vector.InvocationCount != 2
                    || !vector.FreshProcessPerInvocation
                    || !vector.RunIds.SequenceEqual(["run-1", "run-2"], StringComparer.Ordinal)))
            {
                throw new ProtocolException("HV128_DETERMINISM_NOT_FRESH");
            }
        }

        var expanded = catalog.ExpandExpectedRuns().ToArray();
        EnsureUnique(
            expanded.Select(run => $"{run.CellId}\0{run.VectorId}\0{run.RunId}"),
            "HV129_DUPLICATE_EXPECTED_EXECUTION");
        if (protocol.ExecutionContract.RetryPolicy != "unrecorded-retries-forbidden-new-attempt-required")
        {
            throw new ProtocolException("HV130_RETRY_POLICY_INVALID");
        }
    }

    private static void ValidateLock(string root, ProtocolManifest protocol, ArtifactLock artifactLock)
    {
        if (artifactLock.FormatVersion != "contractscribe-m1-host-validation-artifact-lock-v1")
        {
            throw new ProtocolException("HV131_LOCK_VERSION");
        }

        var inventory = protocol.ArtifactInventory.Order(StringComparer.Ordinal).ToArray();
        var locked = artifactLock.Entries.Select(entry => entry.Path).ToArray();
        if (!locked.SequenceEqual(inventory, StringComparer.Ordinal))
        {
            throw new ProtocolException("HV132_LOCK_INVENTORY_MISMATCH");
        }

        EnsureUnique(locked, "HV133_DUPLICATE_LOCK_PATH");
        foreach (var entry in artifactLock.Entries)
        {
            if (entry.Sha256 != CanonicalJson.Sha256File(RepositoryPaths.ResolveConfined(root, entry.Path)))
            {
                throw new ProtocolException("HV134_ARTIFACT_HASH_MISMATCH");
            }
        }

        if (artifactLock.BundleId != ComputeBundleId(artifactLock.Entries))
        {
            throw new ProtocolException("HV135_BUNDLE_ID_MISMATCH");
        }

        foreach (var entry in artifactLock.Entries)
        {
            var content = File.ReadAllText(RepositoryPaths.ResolveConfined(root, entry.Path));
            if (content.Contains(artifactLock.BundleId, StringComparison.Ordinal))
            {
                throw new ProtocolException("HV136_SELF_EMBEDDED_BUNDLE_ID");
            }
        }
    }

    private static void ValidateCrosswalk(
        string root,
        ProtocolManifest protocol,
        VectorCatalog catalog,
        string crosswalkPath)
    {
        using var document = CanonicalJson.ReadStrict(crosswalkPath, ManifestLimit);
        var rootElement = document.RootElement;
        if (rootElement.GetProperty("formatVersion").GetString() != "contractscribe-m1-host-validation-crosswalk-v1")
        {
            throw new ProtocolException("HV137_CROSSWALK_VERSION");
        }

        var rows = rootElement.GetProperty("rows").EnumerateArray().ToArray();
        EnsureUnique(rows.Select(row => row.GetProperty("sourceKey").GetString()!), "HV138_DUPLICATE_SOURCE_KEY");
        using var authoritativeDocument = CanonicalJson.ReadStrict(
            RepositoryPaths.ResolveConfined(root, AuthoritativeSourcesRelativePath),
            ManifestLimit);
        if (authoritativeDocument.RootElement.GetProperty("formatVersion").GetString()
                != "contractscribe-m1-host-validation-authoritative-sources-v1")
        {
            throw new ProtocolException("HV219_AUTHORITATIVE_SOURCE_VERSION");
        }
        var authoritativeKeys = authoritativeDocument.RootElement.GetProperty("sourceKeys")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();
        var rowKeys = rows.Select(row => row.GetProperty("sourceKey").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!rowKeys.SequenceEqual(authoritativeKeys, StringComparer.Ordinal)
            || authoritativeKeys.Distinct(StringComparer.Ordinal).Count() != authoritativeKeys.Length)
        {
            throw new ProtocolException("HV220_AUTHORITATIVE_SOURCE_SET");
        }
        var vectorIds = catalog.Vectors.Select(vector => vector.VectorId).ToHashSet(StringComparer.Ordinal);
        var validatorIds = protocol.RequiredValidators.ToHashSet(StringComparer.Ordinal);
        var referencedVectors = new HashSet<string>(StringComparer.Ordinal);
        var referencedValidators = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var disposition = row.GetProperty("disposition").GetString();
            if (disposition is not ("executable" or "deferred-owned" or "not-applicable"))
            {
                throw new ProtocolException("HV139_CROSSWALK_DISPOSITION");
            }

            var rowVectors = row.GetProperty("vectorIds").EnumerateArray().Select(value => value.GetString()!).ToArray();
            var rowValidators = row.GetProperty("validatorIds").EnumerateArray().Select(value => value.GetString()!).ToArray();
            if (rowVectors.Any(vector => !vectorIds.Contains(vector))
                || rowValidators.Any(validator => !validatorIds.Contains(validator)))
            {
                throw new ProtocolException("HV140_CROSSWALK_UNKNOWN_TARGET");
            }

            referencedVectors.UnionWith(rowVectors);
            referencedValidators.UnionWith(rowValidators);
            if (disposition == "deferred-owned"
                && string.IsNullOrWhiteSpace(row.GetProperty("deferredOwner").GetString()))
            {
                throw new ProtocolException("HV141_DEFERRED_OWNER_MISSING");
            }
        }

        if (!referencedVectors.SetEquals(vectorIds) || !referencedValidators.SetEquals(validatorIds))
        {
            throw new ProtocolException("HV142_CROSSWALK_NOT_BIDIRECTIONAL");
        }
    }

    private static void ValidateSubjectTemplates(string root)
    {
        var schema = RepositoryPaths.ResolveConfined(root, "schemas/validation/m1-host-validation-subject-v1.schema.json");
        SchemaValidation.Validate(
            RepositoryPaths.ResolveConfined(root, "tests/fixtures/m1-host-validation/v1/production-subject.template.json"),
            schema);
        SchemaValidation.Validate(
            RepositoryPaths.ResolveConfined(root, "tests/fixtures/m1-host-validation/v1/self-test-subject.json"),
            schema);
        SchemaValidation.Validate(
            RepositoryPaths.ResolveConfined(root, "tests/fixtures/m1-host-validation/v1/execution-subject.template.json"),
            schema);
    }

    private static void ValidateProtectedInputs(string root)
    {
        var path = RepositoryPaths.ResolveConfined(root, ProtectedInputsRelativePath);
        var manifest = CanonicalJson.DeserializeStrict<ProtectedInputManifest>(
            path,
            ManifestLimit,
            requireCanonical: true);
        if (manifest.FormatVersion != "contractscribe-m1-host-validation-protected-inputs-v1")
        {
            throw new ProtocolException("HV163_PROTECTED_INPUT_VERSION");
        }

        var expectedPaths = ExpandProtectedInputPaths(root, manifest.Roots);
        var actualPaths = manifest.Entries.Select(entry => entry.Path).ToArray();
        if (!actualPaths.SequenceEqual(expectedPaths, StringComparer.Ordinal)
            || actualPaths.Distinct(StringComparer.Ordinal).Count() != actualPaths.Length)
        {
            throw new ProtocolException("HV164_PROTECTED_INPUT_SET");
        }

        foreach (var entry in manifest.Entries)
        {
            if (entry.Sha256 != CanonicalJson.Sha256File(RepositoryPaths.ResolveConfined(root, entry.Path)))
            {
                throw new ProtocolException("HV165_PROTECTED_INPUT_DRIFT");
            }
        }
    }

    internal static string[] ExpandProtectedInputPaths(string root, IReadOnlyList<string> roots)
    {
        ValidateInventoryPaths(roots);
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var relativeRoot in roots)
        {
            var fullPath = RepositoryPaths.ResolveConfined(root, relativeRoot);
            if (File.Exists(fullPath))
            {
                paths.Add(relativeRoot);
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(fullPath, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            }))
            {
                var relativePath = RepositoryPaths.ToRepositoryRelative(root, file);
                var segments = relativePath.Split('/');
                if (segments.Any(segment => segment is "bin" or "obj" or "TestResults"))
                {
                    continue;
                }
                paths.Add(relativePath);
            }
        }
        return paths.ToArray();
    }

    internal static void ValidateProjectBoundary(string root)
    {
        var harnessProject = XDocument.Load(RepositoryPaths.ResolveConfined(
            root,
            "tests/ContractScribe.HostValidation/ContractScribe.HostValidation.csproj"));
        var harnessReferences = harnessProject.Descendants("ProjectReference").ToArray();
        var harnessPackages = harnessProject.Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => value is not null)
            .ToArray();
        if (harnessReferences.Length != 0
            || !harnessPackages.SequenceEqual(["JsonSchema.Net"], StringComparer.Ordinal))
        {
            throw new ProtocolException("HV143_HARNESS_DEPENDENCY_BOUNDARY");
        }
        var harnessProperties = harnessProject.Descendants("PropertyGroup").Elements().ToArray();
        if (!harnessProperties.Any(element => element.Name.LocalName == "OutputType" && element.Value == "Exe")
            || !harnessProperties.Any(element => element.Name.LocalName == "TargetFramework" && element.Value == "net10.0")
            || !harnessProperties.Any(element => element.Name.LocalName == "IsPackable" && element.Value == "false"))
        {
            throw new ProtocolException("HV143_HARNESS_DEPENDENCY_BOUNDARY");
        }

        var testProject = XDocument.Load(RepositoryPaths.ResolveConfined(
            root,
            "tests/ContractScribe.Tests/ContractScribe.Tests.csproj"));
        if (testProject.Descendants("ProjectReference").Count(reference =>
                (reference.Attribute("Include")?.Value ?? string.Empty).EndsWith(
                    @"\ContractScribe.HostValidation\ContractScribe.HostValidation.csproj",
                    StringComparison.OrdinalIgnoreCase)) != 1)
        {
            throw new ProtocolException("HV167_TEST_REFERENCE_DIRECTION");
        }

        var harnessFileName = "ContractScribe.HostValidation.csproj";
        var prohibitedNetworkPackageMarkers = new[] { "OpenAI", "Octokit", "GitHub", "Azure.AI" };
        foreach (var projectPath in Directory.EnumerateFiles(Path.Join(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var project = XDocument.Load(projectPath);
            if (project.Descendants("ProjectReference").Any(reference =>
                    (reference.Attribute("Include")?.Value ?? string.Empty).Contains(harnessFileName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ProtocolException("HV144_PRODUCTION_REFERENCES_HARNESS");
            }

            if (project.Descendants("ProjectReference").Any(reference =>
                    (reference.Attribute("Include")?.Value ?? string.Empty).Contains(".Experiment", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ProtocolException("HV145_PRODUCTION_REFERENCES_EXPERIMENT");
            }
            if (project.Descendants("PackageReference").Any(reference =>
                    prohibitedNetworkPackageMarkers.Any(marker =>
                        (reference.Attribute("Include")?.Value ?? string.Empty).Contains(marker, StringComparison.OrdinalIgnoreCase))))
            {
                throw new ProtocolException("HV168_DECLARED_NETWORK_PACKAGE");
            }
        }
    }

    internal static void ValidateProhibitedClaims(string root)
    {
        var protocol = CanonicalJson.DeserializeStrict<ProtocolManifest>(
            RepositoryPaths.ResolveConfined(root, ProtocolRelativePath),
            ManifestLimit);
        var expected = new[]
        {
            "network-isolation-enforced",
            "offline-sandbox",
            "untrusted-msbuild-sandboxed",
            "transient-writes-prevented"
        };
        if (!protocol.PublicSafety.ProhibitedClaims.SequenceEqual(expected, StringComparer.Ordinal)
            || protocol.PublicSafety.NetworkClaim
                != "no-declared-network-dependency-and-no-contractscribe-initiated-network-operation")
        {
            throw new ProtocolException("HV201_PUBLIC_CLAIM_POLICY");
        }

        PublicSafetyScanner.EnsureNoUnsupportedClaims(File.ReadAllText(
            RepositoryPaths.ResolveConfined(root, "docs/20_architecture/validation/m1-host-validation-protocol.md")));
    }

    private static void ValidateProtocolCorpusSafety(string root)
    {
        var paths = new[]
        {
            ProtocolRelativePath,
            VectorsRelativePath,
            CrosswalkRelativePath,
            AuthoritativeSourcesRelativePath,
            "tests/fixtures/m1-host-validation/v1/evidence-mutation-corpus.json",
            "tests/fixtures/m1-host-validation/v1/production-subject.template.json",
            "tests/fixtures/m1-host-validation/v1/self-test-subject.json",
            "tests/fixtures/m1-host-validation/v1/execution-subject.template.json"
        };
        foreach (var path in paths)
        {
            PublicSafetyScanner.EnsureSafeBytes(File.ReadAllBytes(RepositoryPaths.ResolveConfined(root, path)));
        }
        ValidateProhibitedClaims(root);
    }

    private static void ValidateReviewedCommit(string root, ReviewRecord review, string bundleId)
    {
        var lockPath = RepositoryPaths.ResolveConfined(root, LockRelativePath);
        var artifactLock = CanonicalJson.DeserializeStrict<ArtifactLock>(
            lockPath,
            ManifestLimit,
            requireCanonical: true);
        if (artifactLock.BundleId != bundleId
            || RunGit(root, ["cat-file", "-e", $"{review.ReviewedHead}^{{commit}}"], captureOutput: false).ExitCode != 0
            || RunGit(root, ["merge-base", "--is-ancestor", review.ReviewedHead, "HEAD"], captureOutput: false).ExitCode != 0)
        {
            throw new ProtocolException("HV202_REVIEWED_COMMIT_INVALID");
        }

        foreach (var entry in artifactLock.Entries)
        {
            var result = RunGit(root, ["show", $"{review.ReviewedHead}:{entry.Path}"], captureOutput: true);
            if (result.ExitCode != 0 || CanonicalJson.Sha256(result.Output) != entry.Sha256)
            {
                throw new ProtocolException("HV203_REVIEWED_BUNDLE_MISMATCH");
            }
        }
    }

    private static (int ExitCode, byte[] Output) RunGit(
        string root,
        IReadOnlyList<string> arguments,
        bool captureOutput)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new ProtocolException("HV202_REVIEWED_COMMIT_INVALID");
        using var output = new MemoryStream();
        if (captureOutput)
        {
            process.StandardOutput.BaseStream.CopyTo(output);
        }
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (error.Length > 4096)
        {
            throw new ProtocolException("HV202_REVIEWED_COMMIT_INVALID");
        }
        return (process.ExitCode, output.ToArray());
    }

    private static void ValidateInventoryPaths(IReadOnlyList<string> inventory)
    {
        if (inventory.Count == 0
            || inventory.Any(path => string.IsNullOrWhiteSpace(path)
                || Path.IsPathRooted(path)
                || path.Contains('\\', StringComparison.Ordinal)
                || path.Split('/').Any(segment => segment is "" or "." or ".."))
            || inventory.Contains(LockRelativePath, StringComparer.Ordinal)
            || inventory.Contains(ReviewRelativePath, StringComparer.Ordinal))
        {
            throw new ProtocolException("HV146_INVENTORY_INVALID");
        }

        EnsureUnique(inventory, "HV147_DUPLICATE_INVENTORY_PATH");
    }

    private static void EnsureUnique(IEnumerable<string> values, string code)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (values.Any(value => !seen.Add(value)))
        {
            throw new ProtocolException(code);
        }
    }
}
