using Tia.Core.Analysis;
using Tia.Core.Diff;
using Tia.Core.Model;
using Tia.Core.Safety;

namespace Tia.Workspace;

// Verify snapshots: a changed one selects the test its name denotes (#172).
internal sealed partial class ChangeResolver
{
    /// <summary>
    /// The solution-wide facts that decide whether a snapshot's name can be trusted, read once and
    /// only when a snapshot is in the diff.
    /// </summary>
    private sealed class SnapshotGuard
    {
        private readonly Lazy<(bool OverrideSeen, HashSet<string> Named)> _scan;

        public SnapshotGuard(LoadedWorkspace workspace, IEnumerable<string> snapshotFileNames, CancellationToken cancellationToken)
        {
            var names = snapshotFileNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            _scan = new Lazy<(bool, HashSet<string>)>(() =>
            {
                var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var overrideSeen = false;

                foreach (var context in workspace.Projects)
                {
                    foreach (var document in context.Project.Documents)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var content = document.GetTextAsync(cancellationToken).GetAwaiter().GetResult().ToString();
                        overrideSeen |= VerifiedSnapshots.NamingOverrides.Any(o => content.Contains(o, StringComparison.Ordinal));
                        foreach (var name in names.Where(n => content.Contains(n, StringComparison.OrdinalIgnoreCase)))
                        {
                            named.Add(name);
                        }
                    }
                }

                return (overrideSeen, named);
            });
        }

        /// <summary>True when nothing in the solution renames, relocates or hand-reads a snapshot.</summary>
        public bool NameIsTheOwner(string fileName) => !_scan.Value.OverrideSeen && !_scan.Value.Named.Contains(fileName);
    }

    /// <summary>
    /// Seeds the tests a Verify snapshot's name denotes in place of widening the project that holds
    /// it. False leaves the file to <see cref="WidenContent"/>, which is what every case that is not
    /// plainly a convention-named snapshot gets.
    /// </summary>
    /// <remarks>
    /// Why this cannot cause a miss: it narrows only when the owner of the file is the Verify call of
    /// the test its name denotes. That needs the project to reference Verify, nothing in the solution
    /// to rename, relocate or read a snapshot (<see cref="VerifiedSnapshots.NamingOverrides"/>), no
    /// other project to link the file in, and every name the change involves - the old path of a
    /// rename too - to resolve to a method in the graph. Otherwise it returns false and the project
    /// is widened as before. The methods taken are a superset of the one meant, see
    /// <see cref="VerifiedSnapshots"/>.
    /// </remarks>
    private static bool TryNarrowSnapshot(
        LoadedWorkspace workspace,
        LinkedItems linked,
        ImpactGraph graph,
        SnapshotGuard guard,
        ChangedFile file,
        string absolutePath,
        SymbolChangeSet changes,
        Dictionary<string, bool> changedProjects)
    {
        var paths = new[] { file.Path, file.OldPath }.OfType<string>().ToList();

        if (paths.Exists(p => !VerifiedSnapshots.TryParse(p, out _, out _)) ||
            FindOwningProject(workspace, absolutePath) is not { } owner ||
            linked.InWorkspace(absolutePath).Any() ||
            !workspace.Projects.Single(c => c.Descriptor.Name == owner.Name).Project.MetadataReferences
                .OfType<Microsoft.CodeAnalysis.PortableExecutableReference>()
                .Any(r => Path.GetFileName(r.FilePath)?.StartsWith("Verify", StringComparison.OrdinalIgnoreCase) == true) ||
            paths.Exists(p => !guard.NameIsTheOwner(Path.GetFileName(p))))
        {
            return false;
        }

        var tests = new List<string>();
        foreach (var path in paths)
        {
            if (VerifiedSnapshots.TestsNamedBy(path, graph) is not { } named)
            {
                return false;
            }

            tests.AddRange(named);
        }

        foreach (var test in tests)
        {
            changes.Add(test);
        }

        changedProjects[owner.Name] = false;
        changes.AddProjectWide(
            owner.Name,
            ProjectWideCause.ContentFile,
            $"{file.Path} is a Verify snapshot named for {string.Join(", ", tests.Select(SymbolKeys.SimpleNameOf).Distinct(StringComparer.Ordinal))}; " +
            "nothing in the solution renames or reads snapshots, so only those tests are impacted",
            widensProject: false);
        return true;
    }
}
