using System.Xml.Linq;

namespace BamCam.Core.Cam;

/// <summary>
/// Whether a machining parameter uses the inherited default (from the CAM style / machining
/// options) or an explicit value set by the user. Matches the <c>state</c> attribute in .cb files.
/// </summary>
public enum ParamState { Default, Value, Auto }

public interface ICamParam
{
    ParamState State { get; set; }
    Type ValueType { get; }
    object? BoxedValue { get; set; }
    /// <summary>Complex values (e.g. lead moves, holding tabs) stored as XML when not modelled.</summary>
    XElement? RawXml { get; set; }
    /// <summary>
    /// The text of a value in a drawing or style that BamCam does not know (such as a CamBam option BamCam lacks); null
    /// otherwise. The value then is a stand-in, so operations with such a value generate no toolpath. Setting a value
    /// clears it.
    /// </summary>
    string? UnknownText { get => null; set { } }
    ICamParam CloneParam();
}

/// <summary>A machining parameter with its inheritance state.</summary>
public sealed class CamParam<T> : ICamParam
{
    public CamParam(T value, ParamState state = ParamState.Default)
    {
        Value = value;
        State = state;
    }

    private T _value = default!;

    public T Value
    {
        get => _value;
        set
        {
            _value = value;
            UnknownText = null;
        }
    }

    public ParamState State { get; set; }
    public XElement? RawXml { get; set; }
    [System.ComponentModel.Browsable(false)] public string? UnknownText { get; set; }

    public bool IsDefault => State == ParamState.Default;

    public Type ValueType => typeof(T);

    public object? BoxedValue
    {
        get => Value;
        set => Value = (T)value!;
    }

    /// <summary>Sets an explicit user value.</summary>
    public void Set(T value)
    {
        Value = value;
        State = ParamState.Value;
    }

    /// <summary>Updates the inherited value without changing an explicit one.</summary>
    public void SetDefault(T value)
    {
        if (State == ParamState.Default) Value = value;
    }

    public ICamParam CloneParam() =>
        new CamParam<T>(Value, State) { RawXml = RawXml == null ? null : new XElement(RawXml), UnknownText = UnknownText };

    public static implicit operator T(CamParam<T> p) => p.Value;

    public override string ToString() => $"{Value} ({State})";
}
