using System.ComponentModel;

/// <summary>
/// Build configuration
/// </summary>
[TypeConverter(typeof(TypeConverter<Configuration>))]
public class Configuration : Enumeration
{
    /// <summary>
    /// Debug configuration
    /// </summary>
    public static Configuration Debug = new() { Value = nameof(Debug) };
    
    /// <summary>
    /// Release configuration
    /// </summary>
    public static Configuration Release = new() { Value = nameof(Release) };

    /// <summary>
    /// Implicit conversion from Configuration to string
    /// </summary>
    /// <param name="configuration">The configuration</param>
    /// <returns></returns>
    public static implicit operator string(Configuration configuration)
    {
        return configuration.Value;
    }
}
