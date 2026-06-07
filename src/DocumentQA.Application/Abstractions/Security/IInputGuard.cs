namespace DocumentQA.Application.Abstractions.Security;

public interface IInputGuard
{
    InputValidationResult Validate(string input);
}

public sealed record InputValidationResult(bool IsAllowed, string? Reason);
