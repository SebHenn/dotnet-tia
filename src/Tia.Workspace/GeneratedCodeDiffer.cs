using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Tia.Workspace;

/// <summary>The base revision's content for one changed source file. Null content means the file
/// did not exist at the base revision.</summary>
public sealed record BaseSource(string AbsolutePath, string? Content);

public sealed record GeneratedCodeComparison
{
    /// <summary>Generated documents whose text differs between the two revisions.</summary>
    public IReadOnlyList<GeneratedDocument> Changed { get; init; } = [];

    /// <summary>False when the comparison could not be made, in which case every generated
    /// document has to be treated as changed.</summary>
    public required bool Exact { get; init; }

    public required string Reason { get; init; }
}

/// <summary>
/// Works out which generated documents a diff actually changed, by running the project's
/// generators twice.
/// </summary>
/// <remarks>
/// The blunt alternative - treat every generated document as changed whenever anything in the
/// project changes - is what caps selection on a repository whose core library runs a generator:
/// one line touched anywhere selects every test that depends on any generated code, which on
/// FluentValidation is all of them.
///
/// Both sides are recomputed with the same driver, rather than comparing a recomputed base
/// against the build's own output. That matters: this driver cannot reproduce every analyzer
/// config option the real build passes, so comparing against real output would report differences
/// that are artefacts of the setup rather than of the diff. Recomputing both sides makes those
/// artefacts cancel.
/// </remarks>
public static class GeneratedCodeDiffer
{
    public static GeneratedCodeComparison Compare(
        ProjectContext context,
        IReadOnlyList<BaseSource> baseSources,
        CancellationToken cancellationToken = default)
    {
        if (context.GeneratedDocuments.Count == 0)
        {
            return new GeneratedCodeComparison { Exact = false, Reason = "no generated documents are in the compilation" };
        }

        var generators = CollectGenerators(context.Project);
        if (generators.Count == 0)
        {
            return new GeneratedCodeComparison { Exact = false, Reason = "the project's generators could not be loaded" };
        }

        try
        {
            var parseOptions = context.Project.ParseOptions as CSharpParseOptions ?? CSharpParseOptions.Default;

            // Generated trees have to come out before the generators run again, or their output
            // would be fed back in as if it were handwritten source.
            var withoutGenerated = context.Compilation.RemoveSyntaxTrees(context.GeneratedDocuments.Select(d => d.Tree));

            var current = Run(generators, withoutGenerated, parseOptions, cancellationToken);
            var atBase = Run(generators, ApplyBaseSources(withoutGenerated, baseSources, parseOptions, cancellationToken), parseOptions, cancellationToken);

            var changed = new List<GeneratedDocument>();

            foreach (var document in context.GeneratedDocuments)
            {
                var currentText = current.GetValueOrDefault(document.HintName);
                var baseText = atBase.GetValueOrDefault(document.HintName);

                if (currentText is null)
                {
                    // The recomputation did not produce this document at all, so the two runs are
                    // not comparable for it.
                    return new GeneratedCodeComparison
                    {
                        Exact = false,
                        Reason = $"'{document.HintName}' could not be reproduced outside the build",
                    };
                }

                if (!string.Equals(currentText, baseText, StringComparison.Ordinal))
                {
                    changed.Add(document);
                }
            }

            return new GeneratedCodeComparison
            {
                Changed = changed,
                Exact = true,
                Reason = $"{changed.Count} of {context.GeneratedDocuments.Count} generated document(s) differ from the base revision",
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new GeneratedCodeComparison { Exact = false, Reason = $"generators could not be re-run ({ex.GetType().Name})" };
        }
    }

    private static Dictionary<string, string> Run(
        IReadOnlyList<ISourceGenerator> generators,
        Compilation compilation,
        CSharpParseOptions parseOptions,
        CancellationToken cancellationToken)
    {
        var driver = CSharpGeneratorDriver.Create(generators, parseOptions: parseOptions);
        var result = driver.RunGenerators(compilation, cancellationToken).GetRunResult();

        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var generated in result.Results.SelectMany(r => r.GeneratedSources))
        {
            outputs[generated.HintName] = generated.SourceText.ToString();
        }

        return outputs;
    }

    private static Compilation ApplyBaseSources(
        Compilation compilation,
        IReadOnlyList<BaseSource> baseSources,
        CSharpParseOptions parseOptions,
        CancellationToken cancellationToken)
    {
        var result = compilation;

        foreach (var source in baseSources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var existing = result.SyntaxTrees.FirstOrDefault(t => string.Equals(t.FilePath, source.AbsolutePath, StringComparison.Ordinal));

            if (source.Content is null)
            {
                // Added since the base revision, so it was not there to generate from.
                if (existing is not null)
                {
                    result = result.RemoveSyntaxTrees(existing);
                }

                continue;
            }

            var atBase = CSharpSyntaxTree.ParseText(source.Content, parseOptions, source.AbsolutePath, cancellationToken: cancellationToken);
            result = existing is null ? result.AddSyntaxTrees(atBase) : result.ReplaceSyntaxTree(existing, atBase);
        }

        return result;
    }

    private static List<ISourceGenerator> CollectGenerators(Project project)
    {
        var generators = new List<ISourceGenerator>();

        foreach (var reference in project.AnalyzerReferences)
        {
            try
            {
                generators.AddRange(reference.GetGenerators(LanguageNames.CSharp));
            }
            catch (Exception)
            {
                // An analyzer reference that will not load cannot be reproduced, and the caller
                // falls back to treating every generated document as changed.
            }
        }

        return generators;
    }
}
