namespace ai_assistant_service.Services.Tools;

/// <summary>
/// A tool whose parameter schema can be narrowed at runtime, for example by
/// constraining a parameter to the live list of legal values.
/// </summary>
public interface IParameterEnumConstrainable
{
    /// <summary>
    /// Replaces the <c>enum</c> constraint on <paramref name="parameterName"/>.
    /// Implementations must be safe to call while other threads read the schema.
    /// </summary>
    void SetParameterEnum(string parameterName, IReadOnlyList<string> values);
}
