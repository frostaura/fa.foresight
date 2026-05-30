using FluentAssertions;
using FrostAura.Foresight.Domain.Descriptions;
using Xunit;

namespace FrostAura.Foresight.Tests.Domain;

/// <summary>
/// Unit tests for the deterministic <see cref="DescriptionTemplater"/> that replaced the LLM
/// describers. Descriptions must be non-empty, mention the entity, reflect its structure, and be
/// stable (same input ⇒ same output).
/// </summary>
public class DescriptionTemplaterTests
{
    private const string ModelDag =
        "{\"definitionKind\":\"model\",\"nodes\":[{\"id\":\"a\",\"type\":\"rsi\"},{\"id\":\"b\",\"type\":\"logistic\"}]}";

    [Fact]
    public void ForModel_produces_nonempty_descriptions_mentioning_name_and_nodes()
    {
        var (simple, technical) = DescriptionTemplater.ForModel("BTC 15m Logistic", "deterministic", true, ModelDag);

        simple.Should().NotBeNullOrWhiteSpace();
        technical.Should().NotBeNullOrWhiteSpace();
        simple.Should().Contain("BTC 15m Logistic");
        // Node types are humanised into the prose.
        technical.Should().Contain("RSI");
        technical.Should().Contain("Logistic");
    }

    [Fact]
    public void ForModel_is_deterministic()
    {
        var a = DescriptionTemplater.ForModel("M", "deterministic", true, ModelDag);
        var b = DescriptionTemplater.ForModel("M", "deterministic", true, ModelDag);
        a.Should().Be(b);
    }

    [Fact]
    public void ForStrategy_builtin_reuses_curated_description()
    {
        // Built-in (no DAG definition): the curated Description is reused as the technical text.
        var (simple, technical) = DescriptionTemplater.ForStrategy(
            "Flat", "Bet the initial size every step. The honest baseline.", null);

        simple.Should().NotBeNullOrWhiteSpace();
        technical.Should().Contain("honest baseline");
    }

    [Fact]
    public void ForStrategy_dag_describes_nodes()
    {
        var dag = "{\"definitionKind\":\"strategy\",\"nodes\":[{\"id\":\"k\",\"type\":\"edge-aware-kelly\"},{\"id\":\"o\",\"type\":\"output-stake\"}]}";
        var (simple, technical) = DescriptionTemplater.ForStrategy("My EAK", null, dag);

        simple.Should().Contain("My EAK");
        technical.Should().NotBeNullOrWhiteSpace();
    }
}
