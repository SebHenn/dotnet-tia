using Tia.Core.Configuration;

namespace Tia.Workspace;

/// <summary>
/// A <c>tia.json</c> that cannot be read (#170). A <see cref="SetupException"/>, so every command
/// refuses with exit 2 rather than analysing under rules it had to guess at, or falling back to a
/// full run that would hide the typo for good.
/// </summary>
public sealed class ConfigException(string message) : SetupException(message)
{
    /// <summary>The config in <paramref name="directory"/>, or <see cref="TiaConfig.None"/> when there is none.</summary>
    /// <exception cref="ConfigException">There is one, and it is not valid.</exception>
    public static TiaConfig Load(string directory)
    {
        try
        {
            return TiaConfig.Load(directory);
        }
        catch (ConfigFileException ex)
        {
            throw new ConfigException(ex.Message);
        }
    }
}
