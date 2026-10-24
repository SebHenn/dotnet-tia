namespace Tia.Cli.Commands;

/// <summary>
/// Where a project's run writes: straight to the console, or into a buffer printed whole when the
/// project finishes, for <c>run --parallel</c> (#74).
/// </summary>
public sealed class RunOutput
{
    private readonly List<(bool Error, string Text)>? _lines;

    private readonly Action<string> _line;

    private readonly Action<string> _error;

    private RunOutput(Action<string> line, Action<string> error, List<(bool, string)>? lines)
    {
        _line = line;
        _error = error;
        _lines = lines;
    }

    public static RunOutput Console() => new(System.Console.Out.WriteLine, System.Console.Error.WriteLine, null);

    public static RunOutput Buffered()
    {
        var lines = new List<(bool, string)>();

        // A runner's two streams arrive on two threads, so the one list they share is locked.
        return new RunOutput(
            text => { lock (lines) { lines.Add((false, text)); } },
            text => { lock (lines) { lines.Add((true, text)); } },
            lines);
    }

    public void Line(string text) => _line(text);

    public void Error(string text) => _error(text);

    /// <summary>Prints what was buffered, each line to the stream it came from. Nothing for the console form.</summary>
    public void WriteTo(TextWriter output, TextWriter error)
    {
        if (_lines is null)
        {
            return;
        }

        lock (_lines)
        {
            foreach (var (isError, text) in _lines)
            {
                (isError ? error : output).WriteLine(text);
            }
        }
    }
}
