using System.Reflection;

namespace BamCam.Core.Model;

/// <summary>
/// Shows a string property as a drop-down list in the <c>PropertyGrid</c>. The named static method of the
/// declaring type returns the entries as (stored value, display text) pairs.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ChoicesAttribute(string method) : Attribute
{
    public string Method { get; } = method;

    /// <summary>Shows a [...] button next to the list that opens the place where its entries are edited.</summary>
    public bool Browse { get; init; }

    public IReadOnlyList<KeyValuePair<string, string>> GetChoices(Type owner)
    {
        var m = owner.GetMethod(Method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException($"{owner.Name}.{Method} not found");
        return ((IEnumerable<KeyValuePair<string, string>>)m.Invoke(null, null)!).ToList();
    }
}