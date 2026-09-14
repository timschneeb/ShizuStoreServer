namespace ShizuAppStoreServer.Core.Data;

using System.ComponentModel;
using System.Reflection;

/// <summary>Reads the <see cref="DescriptionAttribute"/> friendly names off enum members.</summary>
public static class EnumDescriptions
{
    /// <summary>
    /// The <c>Description</c> of an enum member, falling back to the member
    /// name when no attribute is present (so un-annotated enums keep working).
    /// </summary>
    public static string GetDescription<T>(this T value)
        where T : struct, Enum
    {
        var field = typeof(T).GetField(value.ToString());
        var attribute = field?.GetCustomAttribute<DescriptionAttribute>();
        return attribute?.Description ?? value.ToString();
    }
}
