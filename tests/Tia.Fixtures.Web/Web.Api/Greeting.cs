namespace Web.Api;

public interface IGreeting
{
    string Text { get; }
}

/// <summary>
/// A service only the container constructs. Its text is decided in the constructor, so a
/// mutation there changes what <c>/greeting</c> returns, and only the registration in
/// <c>Program.cs</c> connects it to a test (#40).
/// </summary>
public sealed class Greeting : IGreeting
{
    public Greeting()
    {
        var audience = "contributors";
        Text = "hello, " + audience;
    }

    public string Text { get; }
}
