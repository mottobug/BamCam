using System.Reflection;

namespace BamCam.App.Controls;

/// <summary>
/// Shows a string property as a drop-down list in the <see cref="PropertyGrid"/>. The named static method of the
/// declaring type returns the entries as (stored value, display text) pairs.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ChoicesAttribute(string method) : Attribute
{
    public string Method { get; } = method;

    public IReadOnlyList<KeyValuePair<string, string>> GetChoices(Type owner)
    {
        var m = owner.GetMethod(Method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException($"{owner.Name}.{Method} not found");
        return ((IEnumerable<KeyValuePair<string, string>>)m.Invoke(null, null)!).ToList();
    }
}