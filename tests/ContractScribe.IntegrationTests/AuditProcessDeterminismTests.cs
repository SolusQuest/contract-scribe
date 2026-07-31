using System.Diagnostics;
using System.Security;
using System.Text;

namespace ContractScribe.IntegrationTests;

public sealed class AuditProcessDeterminismTests
{
    [Fact]
    public async Task AggregatedCanonicalBytes_AreStableAcrossFreshProcessesAndInputOrders()
    {
        var repositoryRoot = FindRepositoryRoot();
        var temporaryRoot = Path.Join(
            Path.GetTempPath(),
            "contractscribe-audit-result-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var coreProject = Path.Join(
                repositoryRoot,
                "src",
                "ContractScribe.Core",
                "ContractScribe.Core.csproj");
            File.WriteAllText(
                Path.Join(temporaryRoot, "Probe.csproj"),
                $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{{SecurityElement.Escape(coreProject)}}" />
                  </ItemGroup>
                </Project>
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(
                Path.Join(temporaryRoot, "Program.cs"),
                ProbeSource,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            await RunDotnet(
                temporaryRoot,
                TimeSpan.FromSeconds(90),
                "build",
                "Probe.csproj",
                "-c",
                "Release",
                "--nologo");

            var first = await RunProbe(
                temporaryRoot,
                "tr-TR",
                "Pacific/Kiritimati",
                "forward");
            var reversed = await RunProbe(
                temporaryRoot,
                "tr-TR",
                "Pacific/Kiritimati",
                "reverse");
            var otherEnvironment = await RunProbe(
                temporaryRoot,
                "fr-FR",
                "America/Los_Angeles",
                "forward");
            var repeated = await RunProbe(
                temporaryRoot,
                "fr-FR",
                "America/Los_Angeles",
                "reverse");

            Assert.Equal(first, reversed);
            Assert.Equal(first, otherEnvironment);
            Assert.Equal(first, repeated);
            Assert.NotEmpty(Convert.FromBase64String(first));
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    private static async Task<string> RunProbe(
        string projectRoot,
        string culture,
        string timeZone,
        string permutation)
    {
        var result = await RunDotnet(
            projectRoot,
            TimeSpan.FromSeconds(60),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TZ"] = timeZone,
            },
            "run",
            "--project",
            "Probe.csproj",
            "-c",
            "Release",
            "--no-build",
            "--no-restore",
            "--",
            culture,
            permutation,
            timeZone);
        return result;
    }

    private static async Task<string> RunDotnet(
        string workingDirectory,
        TimeSpan timeout,
        params string[] arguments)
        => await RunDotnet(workingDirectory, timeout, null, arguments);

    private static async Task<string> RunDotnet(
        string workingDirectory,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment,
        params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        process.StartInfo.Environment["DOTNET_NOLOGO"] = "1";
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                process.StartInfo.Environment[pair.Key] = pair.Value;
            }
        }
        Assert.True(process.Start());
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        var output = await stdout;
        var error = await stderr;
        Assert.True(process.ExitCode == 0, error + Environment.NewLine + output);
        return output;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Join(directory.FullName, "ContractScribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private const string ProbeSource = """"
        using System.Collections.Immutable;
        using System.Globalization;
        using System.Security.Cryptography;
        using System.Text;
        using ContractScribe.Core;

        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(args[0]);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(args[0]);
        var reverse = args[1] == "reverse";
        Environment.SetEnvironmentVariable("TZ", args[2]);

        const string context = "probe.v1";
        const string typeId = "T:Probe.ΩWidget";
        const string methodId = "M:Probe.ΩWidget.Run(System.String)";
        var sourceProducer = "sgp." + new string('a', 64);
        var sourceOutput = "sgo." + new string('b', 64);
        var toolProducer = "tgp." + new string('c', 64);
        var toolOutput = "tgo." + new string('d', 64);
        var classificationBuffer = new ClassificationCandidateBuffer();
        var typeLocators = new CandidateLocator[]
        {
            ClassificationInput.RepositoryLocator("src/ΩWidget.Part1.cs", 100, 140),
            ClassificationInput.RepositoryLocator("src/ΩWidget.Part2.cs", 500, 540),
        };
        classificationBuffer.AddTarget(
            context,
            typeId,
            PrimarySymbolKind.Class,
            reverse
                ? ImmutableArray.Create(SymbolTrait.Partial, SymbolTrait.Generic)
                : ImmutableArray.Create(SymbolTrait.Generic, SymbolTrait.Partial),
            ClassificationOrigin.Source,
            Ordered(typeLocators, reverse));
        classificationBuffer.AddTarget(
            context,
            methodId,
            PrimarySymbolKind.Method,
            ImmutableArray.Create(SymbolTrait.Generic),
            ClassificationOrigin.SourceGenerator,
            [ClassificationInput.GeneratedSourceLocator(
                sourceProducer,
                sourceOutput,
                700,
                760)]);
        classificationBuffer.AddComponent(
            context,
            methodId,
            ComponentKind.Parameter,
            "parameter/0",
            ClassificationOrigin.SourceGenerator);
        var unresolvedLocators = new CandidateLocator[]
        {
            ClassificationInput.RepositoryLocator("src/Missing.cs", 900, 930),
            ClassificationInput.GeneratedSourceLocator(
                sourceProducer,
                sourceOutput,
                1000,
                1030),
            ClassificationInput.ToolGeneratedLocator(
                toolProducer,
                toolOutput,
                1100,
                1130),
        };
        classificationBuffer.AddUnresolvedDocumentationCandidate(
            context,
            ClassificationOrigin.Source,
            Ordered(unresolvedLocators, reverse));
        var classifications = classificationBuffer.Normalize(TargetProfile.ExternalApi)
            .ClassificationSet ?? throw new InvalidOperationException("classification");

        const string policyJson = """
            {"schemaVersion":1,"targetProfile":"profile.external-api","defaultDecision":"required"}
            """;
        var policy = PolicyConfigurationEvaluator.Parse(Encoding.UTF8.GetBytes(policyJson))
            .Document ?? throw new InvalidOperationException("policy");
        var policyInputs = new PolicyContributionInput[]
        {
            PolicyConfigurationInput.Repository("src/Probe.csproj", "src/ΩWidget.Part1.cs"),
            PolicyConfigurationInput.Repository("src/Probe.csproj", "src/ΩWidget.Part2.cs"),
            PolicyConfigurationInput.Generated(
                "src/Probe.csproj",
                "source-generator",
                sourceProducer,
                sourceOutput),
            PolicyConfigurationInput.Generated(
                "src/Probe.csproj",
                "tool-generated",
                toolProducer,
                toolOutput),
        };
        var allContributions = PolicyConfigurationEvaluator.Evaluate(
            policy,
            Ordered(policyInputs, reverse)).ContributionSet
            ?? throw new InvalidOperationException("contributions");
        var emptyContributions = PolicyConfigurationEvaluator.Evaluate(
            policy,
            Array.Empty<PolicyContributionInput>()).ContributionSet
            ?? throw new InvalidOperationException("empty contributions");
        var generatedContributions = PolicyConfigurationEvaluator.Evaluate(
            policy,
            Ordered(policyInputs.Skip(2).ToArray(), reverse)).ContributionSet
            ?? throw new InvalidOperationException("generated contributions");

        var target = classifications.Targets.Single(value =>
            value.SymbolRef.DocumentationCommentId == typeId);
        var method = classifications.Targets.Single(value =>
            value.SymbolRef.DocumentationCommentId == methodId);
        var component = AssertSingle(classifications.Components);
        var boundEvidence = CreateBoundEvidence(
            classifications,
            target,
            method,
            sourceProducer,
            sourceOutput,
            reverse);

        var inputs = new List<AuditRecordInput>
        {
            AuditInput.Target(target, allContributions, boundEvidence),
            AuditInput.Target(method, emptyContributions),
            AuditInput.Component(component, emptyContributions),
        };
        inputs.AddRange(classifications.Unresolved.Select((record, index) =>
            AuditInput.Unresolved(
                record,
                index % 2 == 0 ? generatedContributions : emptyContributions)));
        var document = AuditAggregator.Aggregate(
            TargetProfile.ExternalApi,
            classifications,
            policy,
            Ordered(inputs.ToArray(), reverse));
        Console.Write(Convert.ToBase64String(AuditJson.Write(document)));

        static BoundObservationEvidence CreateBoundEvidence(
            ClassificationSet classifications,
            TargetClassification target,
            TargetClassification otherTarget,
            string sourceProducer,
            string sourceOutput,
            bool reverse)
        {
            const string documentation = "/// <summary>Documented.</summary>\n";
            const string firstBody = "public partial class Widget { }\n";
            const string secondBody = "public partial class Widget { }\n";
            var firstText = documentation + firstBody;
            var first = DocumentationObservationInput.RepositoryDeclaration(
                "decl." + new string('1', 64),
                DocumentationAuthorityRole.PartialTypePart,
                "project." + new string('a', 64),
                "src/Widget.Part1.cs",
                Sha256(firstText),
                DocumentationObservationInput.Span(0, firstText.Length),
                firstText,
                DocumentationObservationInput.Span(0, documentation.Length),
                documentation,
                DocumentationObservationInput.Span(0, documentation.Length),
                documentation,
                DocumentationBlockState.WellFormed,
                parentSubstantive: true);
            var generatedSha = Sha256(secondBody);
            var second = DocumentationObservationInput.RepositoryDeclaration(
                "decl." + new string('2', 64),
                DocumentationAuthorityRole.PartialTypePart,
                "src/Probe.csproj",
                "src/Widget.Part2.cs",
                generatedSha,
                DocumentationObservationInput.Span(0, secondBody.Length),
                secondBody,
                DocumentationObservationInput.Span(0, 0),
                string.Empty,
                documentationSpan: null,
                documentationText: null,
                DocumentationBlockState.NoBlock,
                parentSubstantive: false);
            var observationBuffer = new DocumentationObservationCandidateBuffer(classifications);
            observationBuffer.AddTarget(target, true, Ordered(new[] { first, second }, reverse));
            foreach (var other in classifications.Targets.Where(value => value != target))
            {
                observationBuffer.AddTarget(other, false, []);
            }
            foreach (var component in classifications.Components)
            {
                observationBuffer.AddComponent(component, false, []);
            }
            var observationOutcome = observationBuffer.Normalize();
            var observation = observationOutcome.ObservationSet?.Observations
                .Single(value => value.Subject.ParentSymbolRef == target.SymbolRef)
                ?? throw new InvalidOperationException(
                    $"observation:{observationOutcome.Status}:{target.Origin}:{string.Join(',', target.Traits)}");

            var evidenceCandidates = new EvidenceCandidateInput[]
            {
                EvidenceInput.Candidate(
                    "evidence.z-doc",
                    EvidenceInput.TargetSubject("probe.v1", "T:Probe.ΩWidget"),
                    EvidenceKind.SourceXmlDocumentation,
                    EvidenceRelation.Documents,
                    documentation,
                    EvidenceInput.RepositoryLocator(
                        "src/Widget.Part1.cs",
                        0,
                        documentation.Length)),
                EvidenceInput.Candidate(
                    "evidence.a-generated-decl",
                    EvidenceInput.TargetSubject("probe.v1", "T:Probe.ΩWidget"),
                    EvidenceKind.SourceDeclaration,
                    EvidenceRelation.Declares,
                    secondBody,
                    EvidenceInput.RepositoryLocator(
                        "src/Widget.Part2.cs",
                        0,
                        secondBody.Length)),
                EvidenceInput.Candidate(
                    "evidence.m-extra",
                    EvidenceInput.TargetSubject(
                        otherTarget.SymbolRef.CompilationContextRef,
                        otherTarget.SymbolRef.DocumentationCommentId),
                    EvidenceKind.PublicContract,
                    EvidenceRelation.Constrains,
                    "契約 😀",
                    EvidenceInput.GeneratedOutputLocator(
                        GeneratedOutputKind.SourceGenerator,
                        sourceProducer,
                        sourceOutput,
                        Sha256("契約 😀"),
                        3000,
                        3000 + "契約 😀".Length)),
            };
            var bundle = EvidenceNormalizer.Normalize(Ordered(evidenceCandidates, reverse)).Bundle
                ?? throw new InvalidOperationException("evidence");
            var bindings = new EvidenceDeclarationBindingInput[]
            {
                EvidenceBindingInput.Declaration(
                    first.DeclarationId,
                    declarationEvidenceId: null,
                    documentationEvidenceId: "evidence.z-doc"),
                EvidenceBindingInput.Declaration(
                    second.DeclarationId,
                    declarationEvidenceId: "evidence.a-generated-decl",
                    documentationEvidenceId: null),
            };
            return EvidenceObservationBinder.Bind(
                observation,
                bundle,
                Ordered(bindings, reverse)).Binding
                ?? throw new InvalidOperationException("binding");
        }

        static IEnumerable<T> Ordered<T>(IReadOnlyList<T> values, bool reverse) =>
            reverse ? values.Reverse() : values;

        static T AssertSingle<T>(IEnumerable<T> values) => values.Single();

        static string Sha256(string value) => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        """";
}
