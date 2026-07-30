namespace Fixtures.Core;

/// <summary>The open-generic case: callers reference <c>Box&lt;int&gt;</c>, which has to reduce to
/// <c>Box&lt;T&gt;</c> before the graph can find it.</summary>
public class Box<T>
{
    private T? _value;

    public void Put(T value) => _value = value;

    public T? Take() => _value;
}
