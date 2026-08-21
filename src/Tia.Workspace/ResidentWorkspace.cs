using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Tia.Core.Model;
using Tia.Core.Reporting;

namespace Tia.Workspace;

/// <param name="Workspace">The bound snapshot to analyse.</param>
/// <param name="ChangedFiles">
/// How many <em>files</em> had moved on disk, counted once each. Zero means the previous snapshot
/// was handed back unchanged and nothing was rebound. Distinct paths rather than documents,
/// because a multi-targeted project holds one document per framework for the same file and
/// reporting "2 files changed" for one save is a small untruth in the one number a watch loop
/// prints on every iteration.
/// </param>
public sealed record RefreshOutcome(LoadedWorkspace Workspace, int ChangedFiles);

/// <summary>
/// One open MSBuild workspace, refreshed from disk instead of reopened.
/// </summary>
/// <remarks>
/// <para>
/// The measurement this exists for: on this repository the run a developer actually makes costs
/// 7.4 s, and 3.3 s of that is <see cref="MSBuildWorkspace"/> evaluating eleven projects. The
/// two cache phases the plan had queued against it were both measured and declined, because a
/// cache can only ever save the work that a run did not need - and this work is needed on every
/// run. It is only ever needed *once per process*, which is the thing a cache cannot exploit and
/// a resident process gets by construction.
/// </para>
/// <para>
/// Refreshing reads every document back off disk and compares it, rather than trusting a file
/// watcher's event stream. Watchers drop events under load, coalesce renames, and report a
/// directory when an editor writes through a temporary file; a missed event here would not be a
/// slow run but a wrong one, because a document left stale keeps its project's content hash
/// matching and so keeps a fragment that describes code that no longer exists. Reading is the
/// cheap half of parsing and it is measured in <c>docs/benchmarks.md</c>; guessing is not worth
/// what it saves.
/// </para>
/// <para>
/// What refreshing deliberately does not do is decide project membership. A file that appears, or
/// a project file, props or targets that changes, moves MSBuild's own item globs, and only MSBuild
/// knows the answer - so those reopen the workspace and pay the 3.3 s again. Editing a file is the
/// common case and adding one is not.
/// </para>
/// </remarks>
public sealed class ResidentWorkspace : IDisposable
{
    private readonly string _repositoryRoot;
    private readonly Action<string>? _log;

    private MSBuildWorkspace _workspace;
    private IReadOnlyList<string> _diagnostics;
    private Dictionary<ProjectId, ProjectDescriptor> _descriptors;
    private Solution _solution;
    private LoadedWorkspace _bound;

    private ResidentWorkspace(
        string solutionPath,
        string repositoryRoot,
        Action<string>? log,
        MSBuildWorkspace workspace,
        IReadOnlyList<string> diagnostics,
        LoadedWorkspace bound)
    {
        SolutionPath = solutionPath;
        _repositoryRoot = repositoryRoot;
        _log = log;
        _workspace = workspace;
        _diagnostics = diagnostics;
        _bound = bound;
        _solution = bound.Solution;
        _descriptors = Descriptors(bound);
    }

    public string SolutionPath { get; }

    /// <summary>The snapshot as it currently stands. Valid until the next refresh.</summary>
    public LoadedWorkspace Current => _bound;

    public static async Task<ResidentWorkspace> OpenAsync(
        string solutionPath,
        string repositoryRoot,
        Action<string>? log = null,
        PhaseClock? clock = null,
        CancellationToken cancellationToken = default)
    {
        var opened = await WorkspaceLoader.OpenAsync(solutionPath, log, clock, cancellationToken).ConfigureAwait(false);

        // Null owner: this class disposes the workspace, and the bound snapshot is handed to an
        // analysis that disposes what it is given.
        var bound = WorkspaceLoader.Bind(
            opened.Workspace.CurrentSolution, repositoryRoot, opened.Diagnostics, null, log, clock, null, cancellationToken);

        return new ResidentWorkspace(solutionPath, repositoryRoot, log, opened.Workspace, opened.Diagnostics, bound);
    }

    /// <summary>
    /// Re-reads the documents this solution holds and rebinds if any of them moved.
    /// </summary>
    /// <remarks>
    /// A document whose file has been deleted is removed. Nothing is added: see the class remarks
    /// for why that is <see cref="ReopenAsync"/>'s job.
    /// </remarks>
    public RefreshOutcome Refresh(PhaseClock? clock = null, CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        // The kind travels with the document because Roslyn's own subclasses for the other two
        // are internal, and the fork method differs per kind.
        var documents = new List<(TextDocument Document, DocumentKind Kind)>();
        foreach (var project in _solution.Projects)
        {
            documents.AddRange(project.Documents.Select(d => ((TextDocument)d, DocumentKind.Source)));
            documents.AddRange(project.AdditionalDocuments.Select(d => ((TextDocument)d, DocumentKind.Additional)));
            documents.AddRange(project.AnalyzerConfigDocuments.Select(d => ((TextDocument)d, DocumentKind.AnalyzerConfig)));
        }

        var replacements = new SourceText?[documents.Count];
        var moved = new bool[documents.Count];

        Parallel.For(0, documents.Count, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Environment.ProcessorCount,
        }, index =>
        {
            var document = documents[index].Document;
            if (document.FilePath is not { Length: > 0 } path)
            {
                return;
            }

            var current = document.GetTextAsync(CancellationToken.None).GetAwaiter().GetResult();

            if (!File.Exists(path))
            {
                moved[index] = true;
                return;
            }

            string disk;
            try
            {
                disk = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Mid-write, most likely, and the watcher will fire again when it lands. Leaving
                // the document alone is the safe half of the choice: the analysis that follows
                // reports the state it can actually read.
                return;
            }

            var text = SourceText.From(disk, current.Encoding ?? System.Text.Encoding.UTF8);
            if (current.ContentEquals(text))
            {
                return;
            }

            moved[index] = true;
            replacements[index] = text;
        });

        var solution = _solution;
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < documents.Count; index++)
        {
            if (!moved[index])
            {
                continue;
            }

            var (document, kind) = documents[index];
            changed.Add(document.FilePath ?? string.Empty);

            solution = replacements[index] is { } text
                ? WithText(solution, document.Id, kind, text)
                : Remove(solution, document.Id, kind);
        }

        clock?.Record(nameof(PhaseTimings.RefreshSeconds), started);

        if (changed.Count == 0)
        {
            return new RefreshOutcome(_bound, 0);
        }

        _solution = solution;
        _bound = WorkspaceLoader.Bind(solution, _repositoryRoot, _diagnostics, _descriptors, _log, clock, null, cancellationToken);
        return new RefreshOutcome(_bound, changed.Count);
    }

    /// <summary>
    /// Throws the whole workspace away and evaluates it again, for the changes a refresh cannot
    /// model: a project file, a props or targets file, or a source file appearing or being renamed.
    /// </summary>
    public async Task ReopenAsync(PhaseClock? clock = null, CancellationToken cancellationToken = default)
    {
        var opened = await WorkspaceLoader.OpenAsync(SolutionPath, _log, clock, cancellationToken).ConfigureAwait(false);
        var previous = _workspace;

        _workspace = opened.Workspace;
        _diagnostics = opened.Diagnostics;
        _bound = WorkspaceLoader.Bind(
            opened.Workspace.CurrentSolution, _repositoryRoot, opened.Diagnostics, null, _log, clock, null, cancellationToken);
        _solution = _bound.Solution;
        _descriptors = Descriptors(_bound);

        previous.Dispose();
    }

    /// <summary>
    /// Whether a changed path is one only MSBuild can interpret, so that a refresh would produce a
    /// snapshot that disagrees with the build.
    /// </summary>
    public static bool NeedsReopen(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.EndsWith("proj", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".slnf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether the solution already holds a document for this file.</summary>
    public bool Knows(string absolutePath) => _solution.GetDocumentIdsWithFilePath(absolutePath).Length > 0;

    public void Dispose() => _workspace.Dispose();

    /// <remarks>
    /// By <see cref="ProjectId"/>, because a multi-targeted project is several Roslyn projects
    /// sharing one file path and one descriptor each. Keying this by path collapsed them, so every
    /// framework of `Tia.Cli` was handed `net10.0`'s descriptor and the second analysis died on a
    /// duplicate key. Ids survive the fork a refresh makes; they do not survive a reopen, which
    /// rebuilds this from scratch.
    /// </remarks>
    private static Dictionary<ProjectId, ProjectDescriptor> Descriptors(LoadedWorkspace bound)
    {
        var descriptors = new Dictionary<ProjectId, ProjectDescriptor>();
        foreach (var context in bound.Projects)
        {
            descriptors[context.Project.Id] = context.Descriptor;
        }

        return descriptors;
    }

    private enum DocumentKind
    {
        Source,
        Additional,
        AnalyzerConfig,
    }

    private static Solution WithText(Solution solution, DocumentId id, DocumentKind kind, SourceText text) => kind switch
    {
        DocumentKind.Source => solution.WithDocumentText(id, text, PreservationMode.PreserveIdentity),
        DocumentKind.Additional => solution.WithAdditionalDocumentText(id, text, PreservationMode.PreserveIdentity),
        _ => solution.WithAnalyzerConfigDocumentText(id, text, PreservationMode.PreserveIdentity),
    };

    private static Solution Remove(Solution solution, DocumentId id, DocumentKind kind) => kind switch
    {
        DocumentKind.Source => solution.RemoveDocument(id),
        DocumentKind.Additional => solution.RemoveAdditionalDocument(id),
        _ => solution.RemoveAnalyzerConfigDocument(id),
    };
}
