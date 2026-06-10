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
    public void ForModel_clamps_to_column_limits_for_node_rich_dag()
    {
        // A model whose DAG carries many distinct node types makes the humanised signal list — and
        // thus the prose — long. The output must still fit the SimpleDescription varchar(500) /
        // TechnicalDescription varchar(1000) columns, otherwise the deterministic backfill's batch
        // save aborts and every entity's descriptions stay null (the real bug this guards).
        var nodes = string.Join(",", Enumerable.Range(0, 60)
            .Select(i => $"{{\"id\":\"n{i}\",\"type\":\"feature-indicator-number-{i}\"}}"));
        var dag = $"{{\"definitionKind\":\"model\",\"nodes\":[{nodes}]}}";

        var (simple, technical) = DescriptionTemplater.ForModel(
            "FeaturePack Heavy", "deterministic", true, dag);

        simple.Length.Should().BeLessThanOrEqualTo(500);
        technical.Length.Should().BeLessThanOrEqualTo(1000);
        simple.Should().NotBeNullOrWhiteSpace();
        technical.Should().NotBeNullOrWhiteSpace();
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
