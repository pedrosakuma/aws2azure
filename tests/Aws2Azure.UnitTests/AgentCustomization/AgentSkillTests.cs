using System.Text.RegularExpressions;

namespace Aws2Azure.UnitTests.AgentCustomization;

public sealed class AgentSkillTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly Regex SkillNamePattern = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex ContractSkillReferencePattern = new(
        @"`(?<name>aws2azure-[a-z0-9]+(?:-[a-z0-9]+)*)`",
        RegexOptions.CultureInvariant);

    [Fact]
    public void Repository_skills_have_valid_unique_metadata()
    {
        var skillsRoot = Path.Combine(RepoRoot, ".github", "skills");
        Assert.True(Directory.Exists(skillsRoot), "Repository skills directory is missing.");

        var errors = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skillDirectories = Directory.EnumerateDirectories(skillsRoot)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(skillDirectories);

        foreach (var skillDirectory in skillDirectories)
        {
            var directoryName = Path.GetFileName(skillDirectory);
            var skillPath = Path.Combine(skillDirectory, "SKILL.md");
            if (!File.Exists(skillPath))
            {
                errors.Add($"{directoryName}: missing SKILL.md.");
                continue;
            }

            var metadata = ReadMetadata(skillPath, errors);
            if (metadata is null)
            {
                continue;
            }

            if (!SkillNamePattern.IsMatch(metadata.Name))
            {
                errors.Add($"{directoryName}: name '{metadata.Name}' must use lowercase kebab-case.");
            }
            if (!string.Equals(directoryName, metadata.Name, StringComparison.Ordinal))
            {
                errors.Add($"{directoryName}: frontmatter name is '{metadata.Name}'.");
            }
            if (!names.Add(metadata.Name))
            {
                errors.Add($"{directoryName}: duplicate skill name '{metadata.Name}'.");
            }
            if (string.IsNullOrWhiteSpace(metadata.Description))
            {
                errors.Add($"{directoryName}: description must not be empty.");
            }
        }

        Assert.True(errors.Count == 0, "Skill validation errors:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void Agent_contract_references_existing_repository_skills()
    {
        var agentsPath = Path.Combine(RepoRoot, "AGENTS.md");
        var agentsText = File.ReadAllText(agentsPath);
        const string sectionHeading = "## Agent customization layers";
        var sectionStart = agentsText.IndexOf(sectionHeading, StringComparison.Ordinal);
        Assert.True(sectionStart >= 0, $"AGENTS.md is missing the '{sectionHeading}' section.");

        var nextSection = agentsText.IndexOf(
            "\n## ",
            sectionStart + sectionHeading.Length,
            StringComparison.Ordinal);
        var section = nextSection >= 0
            ? agentsText[sectionStart..nextSection]
            : agentsText[sectionStart..];
        var references = ContractSkillReferencePattern.Matches(section)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(references);

        foreach (var reference in references)
        {
            var skillPath = Path.Combine(RepoRoot, ".github", "skills", reference, "SKILL.md");
            Assert.True(
                File.Exists(skillPath),
                $"AGENTS.md references repository skill '{reference}', but '{skillPath}' does not exist.");
        }
    }

    [Fact]
    public void Gap_yaml_workflow_preserves_all_canonical_regeneration_steps()
    {
        var requiredFragments = new[]
        {
            "docs/workloads/certification/authority.yaml",
            "expected_canonical_inputs_revision",
            "dotnet run --project tools/Aws2Azure.GapDocs",
            "dotnet run --project tools/Aws2Azure.Documentation",
        };
        var agentsText = File.ReadAllText(Path.Combine(RepoRoot, "AGENTS.md"));
        var validationSkillText = File.ReadAllText(Path.Combine(
            RepoRoot,
            ".github",
            "skills",
            "aws2azure-validation-plan",
            "SKILL.md"));

        foreach (var fragment in requiredFragments)
        {
            Assert.Contains(fragment, agentsText, StringComparison.Ordinal);
            Assert.Contains(fragment, validationSkillText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Validation_skill_distinguishes_forecast_from_completion()
    {
        var validationSkillText = File.ReadAllText(Path.Combine(
            RepoRoot,
            ".github",
            "skills",
            "aws2azure-validation-plan",
            "SKILL.md"));

        Assert.Contains("**Forecast:**", validationSkillText, StringComparison.Ordinal);
        Assert.Contains("**Completion:**", validationSkillText, StringComparison.Ordinal);
        Assert.Contains(
            "In completion mode, after fetching `main`, run:",
            validationSkillText,
            StringComparison.Ordinal);
        Assert.Contains(
            "do not claim required",
            validationSkillText,
            StringComparison.Ordinal);
        Assert.Contains(
            "labels or successful validation",
            validationSkillText,
            StringComparison.Ordinal);
    }

    private static SkillMetadata? ReadMetadata(string skillPath, List<string> errors)
    {
        var lines = File.ReadAllLines(skillPath);
        var displayPath = Path.GetRelativePath(RepoRoot, skillPath);
        if (lines.Length < 3 || !string.Equals(lines[0], "---", StringComparison.Ordinal))
        {
            errors.Add($"{displayPath}: missing opening frontmatter delimiter.");
            return null;
        }

        var end = Array.FindIndex(lines, 1, line => string.Equals(line, "---", StringComparison.Ordinal));
        if (end < 0)
        {
            errors.Add($"{displayPath}: missing closing frontmatter delimiter.");
            return null;
        }

        string? name = null;
        string? description = null;
        var hasDuplicateRequiredField = false;
        for (var index = 1; index < end; index++)
        {
            var separator = lines[index].IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = lines[index][..separator].Trim();
            var value = TrimOptionalQuotes(lines[index][(separator + 1)..].Trim());
            if (string.Equals(key, "name", StringComparison.Ordinal))
            {
                if (name is not null)
                {
                    errors.Add($"{displayPath}: duplicate frontmatter name.");
                    hasDuplicateRequiredField = true;
                }
                name = value;
            }
            else if (string.Equals(key, "description", StringComparison.Ordinal))
            {
                if (description is not null)
                {
                    errors.Add($"{displayPath}: duplicate frontmatter description.");
                    hasDuplicateRequiredField = true;
                }
                description = value;
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add($"{displayPath}: frontmatter name must not be empty.");
        }
        if (string.IsNullOrWhiteSpace(description))
        {
            errors.Add($"{displayPath}: frontmatter description must not be empty.");
        }
        else if (description[0] is '|' or '>')
        {
            errors.Add($"{displayPath}: frontmatter description must use a single-line scalar.");
        }

        return hasDuplicateRequiredField ||
               string.IsNullOrWhiteSpace(name) ||
               string.IsNullOrWhiteSpace(description) ||
               description[0] is '|' or '>'
            ? null
            : new SkillMetadata(name, description);
    }

    private static string TrimOptionalQuotes(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aws2azure.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed record SkillMetadata(string Name, string Description);
}
