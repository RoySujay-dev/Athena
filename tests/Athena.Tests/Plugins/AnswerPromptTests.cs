using Microsoft.SemanticKernel;

namespace Athena.Tests.Plugins;

/// <summary>
/// prompts/answer.yaml is configuration the model never compiles — these tests are the only
/// thing standing between a YAML typo and a runtime failure in answer_question.
/// </summary>
public sealed class AnswerPromptTests
{
    private static string LocatePrompt()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "prompts", "answer.yaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("prompts/answer.yaml not found above test base directory.");
    }

    [Fact]
    public void AnswerYaml_ParsesIntoAKernelFunction_WithTheExpectedContract()
    {
        KernelFunction function = KernelFunctionYaml.FromPromptYaml(File.ReadAllText(LocatePrompt()));

        Assert.Equal("answer_question", function.Name);
        Assert.Equal(["question", "context"],
            function.Metadata.Parameters.Select(p => p.Name));
    }

    [Fact]
    public void AnswerYaml_StatesTheThreeBindingRules()
    {
        string template = File.ReadAllText(LocatePrompt());

        // The three §8 rules the function is bound by; failing this means someone edited the
        // prompt out from under Part C.
        Assert.Contains("[Title, p.N]", template);
        Assert.Contains("INSUFFICIENT_CONTEXT", template);
        Assert.Contains("two different documents", template);
    }
}
