namespace Intelligently.IntegrationTests.Agents;

/// <summary>Task specs posted to <c>/agent/runs</c> by the Web host tests.</summary>
internal static class TestSpecs
{
    public const string BrokenProgram = "using System;\nusing Newtonsoft.Json;\n\nConsole.WriteLine(JsonConvert.SerializeObject(new { ok = true }));\n";
    public const string FixedProgram = "using System;\nusing System.Text.Json;\n\nConsole.WriteLine(JsonSerializer.Serialize(new { ok = true }));\n";
    public const string ErrorText = "error CS0246: The type or namespace name 'Newtonsoft' could not be found";

    /// <summary>A valid <c>LabErrorDiagnosis</c> spec for <paramref name="courseId"/>.</summary>
    public static object LabSpec(int courseId = 7, string? expectedOutput = null, string learnerLevel = "Beginner", object[]? courseFiles = null) => new
    {
        course_id = courseId,
        module_id = 3,
        language = "csharp",
        learner_files = new[] { new { path = "Program.cs", content = BrokenProgram } },
        course_files = courseFiles,
        entrypoint = "Program.cs",
        error_text = ErrorText,
        expected_output = expectedOutput,
        learner_level = learnerLevel,
    };
}
