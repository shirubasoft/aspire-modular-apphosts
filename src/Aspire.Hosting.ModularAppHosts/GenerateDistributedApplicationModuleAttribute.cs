namespace Aspire.Hosting.ModularAppHosts;

/// <summary>
/// Generates a typed module wrapper for the resources declared by an exported module.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateDistributedApplicationModuleAttribute(string name) : Attribute
{
    /// <summary>Gets the name passed to <c>ExportModule</c> and <c>ImportModule</c>.</summary>
    public string Name { get; } = name;

    /// <summary>Gets or sets the module contract version.</summary>
    public string Version { get; set; } = "1";
}
