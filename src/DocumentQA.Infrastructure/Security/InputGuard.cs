using System.Text.RegularExpressions;
using DocumentQA.Application.Abstractions.Security;

namespace DocumentQA.Infrastructure.Security;

public sealed class InputGuard : IInputGuard
{
    private const int MaxLength = 4000;

    private static readonly (Regex Pattern, string Label)[] InjectionPatterns =
    [
        // Matches "ignore instructions" with any stack of qualifiers in between,
        // e.g. "ignore all previous instructions", "disregard the prior prompts".
        (new Regex(@"(ignore|disregard|forget)\s+((all|the|any|your|my|prior|previous|earlier|above|preceding)\s+)*(instructions?|prompts?|rules?|directions?)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "ignore-instructions"),
        (new Regex(@"system\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled), "system-role-injection"),
        (new Regex(@"<\|im_start\|>", RegexOptions.Compiled), "im_start-token"),
        (new Regex(@"<\|im_end\|>", RegexOptions.Compiled), "im_end-token"),
        (new Regex(@"\[INST\]", RegexOptions.Compiled), "inst-token"),
        (new Regex(@"you\s+are\s+now\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "persona-takeover"),
        (new Regex(@"forget\s+(everything|your|all)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "forget-instructions"),
        (new Regex(@"(reveal|print|show|display|output)\s+(your\s+)?(system\s+)?prompt", RegexOptions.IgnoreCase | RegexOptions.Compiled), "reveal-prompt"),
        (new Regex(@"</s>", RegexOptions.Compiled), "eos-token"),
        (new Regex(@"act\s+as\s+(if\s+you\s+are|a\s+different)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "act-as"),
    ];

    public InputValidationResult Validate(string input)
    {
        if (input.Length > MaxLength)
            return new(false, $"Input exceeds {MaxLength} characters ({input.Length})");

        foreach (var (pattern, label) in InjectionPatterns)
        {
            if (pattern.IsMatch(input))
                return new(false, $"Suspicious pattern: {label}");
        }

        return new(true, null);
    }
}
