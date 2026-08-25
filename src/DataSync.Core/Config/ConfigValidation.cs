namespace DataSync.Core.Config;

public sealed class ConfigValidationException(string message) : Exception(message);

internal static class ConfigValidation
{
    public static void ValidateName(string name, string paramName)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ConfigValidationException($"{paramName} must not be empty.");

        if (name.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ConfigValidationException(
                $"{paramName} '{name}' may only contain letters, digits, '-', and '_' (it becomes a file/directory name).");
    }
}
