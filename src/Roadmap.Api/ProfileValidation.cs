using Roadmap.Agent.Models;

namespace Roadmap.Api;

/// <summary>
/// Validates the questionnaire before spending money on the model.
/// Length limits also protect against huge prompts.
/// </summary>
public static class ProfileValidation
{
    public static Dictionary<string, string[]> Validate(UserProfile p)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(p.Goal) || p.Goal.Length > 200)
        {
            errors["goal"] = ["Enter a goal (up to 200 characters)."];
        }

        if (p.Skills.Count > 50 ||
            p.Skills.Any(s => string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 100 || s.Level is < 0 or > 3))
        {
            errors["skills"] = ["Up to 50 skills, names up to 100 characters, level from 0 to 3."];
        }

        if (p.Background?.Length > 2000)
        {
            errors["background"] = ["Up to 2000 characters."];
        }

        if (p.HoursPerWeek is < 1 or > 80)
        {
            errors["hours_per_week"] = ["From 1 to 80 hours per week."];
        }

        if (p.DeadlineWeeks is < 1 or > 260)
        {
            errors["deadline_weeks"] = ["From 1 to 260 weeks."];
        }

        if (!SupportedLanguages.IsSupported(p.Language))
        {
            errors["language"] = [$"Supported: {string.Join(", ", SupportedLanguages.Names.Keys)}."];
        }

        return errors;
    }
}
