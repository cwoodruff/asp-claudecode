using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Intelligently.Core.Agents.Entities;
using Intelligently.Infrastructure.Agents.Intelligence;

namespace Intelligently.UnitTests.Agents;

public class AgentJsonSchemaTests
{
    [Fact]
    public void Plan_schema_is_closed_with_every_property_required_and_no_persistence_fields()
    {
        var schema = AgentJsonSchema.ForNode<AgentPlan>().AsObject();

        schema.ContainsKey("$schema").Should().BeFalse();
        schema["type"]!.GetValue<string>().Should().Be("object");
        schema["additionalProperties"]!.GetValue<bool>().Should().BeFalse();
        schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).Should().BeEquivalentTo("goal", "steps");

        var properties = schema["properties"]!.AsObject();
        properties.Select(p => p.Key).Should().BeEquivalentTo("goal", "steps");

        var step = properties["steps"]!["items"]!.AsObject();
        step["additionalProperties"]!.GetValue<bool>().Should().BeFalse();
        step["required"]!.AsArray().Select(n => n!.GetValue<string>()).Should().BeEquivalentTo("index", "description", "tool", "success_criteria");
        step["properties"]!["tool"]!["type"]!.AsArray().Select(n => n!.GetValue<string>()).Should().BeEquivalentTo("string", "null");
        step["properties"]!["index"]!["type"]!.GetValue<string>().Should().Be("integer");
    }

    [Fact]
    public void Plan_round_trips_through_the_same_serializer_options()
    {
        var json = """{"goal":"g","steps":[{"index":0,"description":"d","tool":null,"success_criteria":"s"}]}""";

        var plan = JsonSerializer.Deserialize<AgentPlan>(json, AgentJsonSchema.SerializerOptions)!;

        plan.Goal.Should().Be("g");
        plan.Steps.Should().ContainSingle().Which.Tool.Should().BeNull();
        JsonSerializer.Serialize(plan).Should().Be(json, "persistence fields are JsonIgnore'd");
    }
}
