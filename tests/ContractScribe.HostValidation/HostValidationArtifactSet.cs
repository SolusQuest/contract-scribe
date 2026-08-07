namespace ContractScribe.HostValidation;

public sealed record TerminalArtifact(
    string CellId,
    string CellManifestPath,
    string TerminalKind,
    string TerminalPath);

public sealed record HostValidationArtifactSet(
    string Root,
    string CommonManifestPath,
    IReadOnlyList<TerminalArtifact> Cells)
{
    public IEnumerable<string> InputPaths() =>
        new[] { Root, CommonManifestPath }
            .Concat(Cells.SelectMany(cell =>
                new[] { cell.CellManifestPath, cell.TerminalPath }));

    public static HostValidationArtifactSet Load(BundleContext context, string artifactRoot)
    {
        var root = Path.GetFullPath(artifactRoot);
        if (!Directory.Exists(root) || IsReparse(root))
        {
            throw new ProtocolException("HV250_ARTIFACT_SET_INVALID");
        }
        var commonPath = Path.Join(root, SubjectManifestMaterializer.CommonFileName);
        var requiredCells = context.Protocol.RequiredCells
            .Select(cell => cell.CellId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expectedTopLevel = requiredCells
            .Select(cell => Path.Join(root, cell))
            .Append(commonPath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualTopLevel = Directory.EnumerateFileSystemEntries(root)
            .Select(Path.GetFullPath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!actualTopLevel.SequenceEqual(expectedTopLevel, StringComparer.Ordinal)
            || !File.Exists(commonPath)
            || IsReparse(commonPath))
        {
            throw new ProtocolException("HV250_ARTIFACT_SET_INVALID");
        }

        var cells = new List<TerminalArtifact>();
        foreach (var cellId in requiredCells)
        {
            var directory = Path.Join(root, cellId);
            if (!Directory.Exists(directory) || IsReparse(directory))
            {
                throw new ProtocolException("HV250_ARTIFACT_SET_INVALID");
            }
            var manifest = Path.Join(directory, SubjectManifestMaterializer.CellFileName);
            var cellEvidence = Path.Join(directory, "cell-evidence.json");
            var incomplete = Path.Join(directory, "incomplete-evidence.json");
            var terminal = File.Exists(cellEvidence) == File.Exists(incomplete)
                ? null
                : File.Exists(cellEvidence) ? cellEvidence : incomplete;
            var expected = terminal is null
                ? []
                : new[] { manifest, terminal }.Order(StringComparer.Ordinal).ToArray();
            var actual = Directory.EnumerateFileSystemEntries(directory)
                .Select(Path.GetFullPath)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (terminal is null
                || !File.Exists(manifest)
                || IsReparse(manifest)
                || IsReparse(terminal)
                || !actual.SequenceEqual(expected, StringComparer.Ordinal))
            {
                throw new ProtocolException("HV250_ARTIFACT_SET_INVALID");
            }
            cells.Add(new TerminalArtifact(
                cellId,
                manifest,
                terminal == cellEvidence ? "cell-evidence" : "incomplete-evidence",
                terminal));
        }
        return new HostValidationArtifactSet(root, commonPath, cells);
    }

    private static bool IsReparse(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
