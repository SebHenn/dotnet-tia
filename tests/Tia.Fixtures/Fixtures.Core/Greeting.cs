namespace Fixtures.Core;

/// <summary>
/// The interface-dispatch case, which is also the dependency-injection case: nothing in
/// <see cref="GreeterService"/> names a concrete greeter, so only the interface edge connects a
/// change in an implementation to the code that will run it.
/// </summary>
public interface IGreeter
{
    string Greet(string name);
}

public class EnglishGreeter : IGreeter
{
    public string Greet(string name) => $"Hello, {name}";
}

public class GermanGreeter : IGreeter
{
    public string Greet(string name) => $"Hallo, {name}";
}

public class GreeterService(IGreeter greeter)
{
    public string Welcome(string name) => greeter.Greet(name) + "!";
}
