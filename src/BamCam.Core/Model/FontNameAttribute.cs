namespace BamCam.Core.Model;

/// <summary>Marks a string property that holds a font family name (the property grid offers the installed fonts).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class FontNameAttribute : Attribute;