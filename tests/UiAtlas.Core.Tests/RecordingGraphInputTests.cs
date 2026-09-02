using System.Text.Json;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Tests;

public sealed class RecordingGraphInputTests
{
    [Fact]
    public void LiveInputProducesTheSameCanonicalGraphAsItsSealedBundle()
    {
        using var temp = new TempDirectory();
        var path = SyntheticBundleFactory.Create(temp.Path, includeScreenshot: true, interactionTrace: true);
        var input = ReadInput(path);
        var builder = new RecordingGraphBuilder();

        var fromBundle = builder.Build(path);
        var fromLiveInput = builder.Build(input);

        Assert.Equal(JsonSerializer.Serialize(fromBundle.Metadata, JsonDefaults.Options),
            JsonSerializer.Serialize(fromLiveInput.Metadata, JsonDefaults.Options));
        Assert.Equal(JsonSerializer.Serialize(fromBundle.Nodes, JsonDefaults.Options),
            JsonSerializer.Serialize(fromLiveInput.Nodes, JsonDefaults.Options));
        Assert.Equal(JsonSerializer.Serialize(fromBundle.Edges, JsonDefaults.Options),
            JsonSerializer.Serialize(fromLiveInput.Edges, JsonDefaults.Options));
    }

    [Fact]
    public void LiveInputRejectsOutOfOrderRepresentativeFrames()
    {
        using var temp = new TempDirectory();
        var input = ReadInput(SyntheticBundleFactory.Create(temp.Path));
        var reversed = input with { Observations = input.Observations.Reverse().ToArray() };

        var exception = Assert.Throws<InvalidDataException>(() => new RecordingGraphBuilder().Build(reversed));

        Assert.Contains("live.sequence", exception.Message);
    }

    private static RecordingGraphInput ReadInput(string path)
    {
        using var bundle = RecordingBundle.Open(path);
        var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
        var statebook = bundle.ReadJson<DerivedStatebook>("derived/statebook.json");
        var interactions = bundle.Entries.Contains("raw/interactions.jsonl")
            ? bundle.ReadText("raw/interactions.jsonl")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize<InteractionObservation>(line, JsonDefaults.Options)!)
                .OrderBy(interaction => interaction.Sequence)
                .ToArray()
            : [];
        var representativeFrames = statebook.RepresentativeFrames
            .Concat(interactions.Select(interaction => interaction.SourceFrameSequence))
            .Concat(interactions.SelectMany(interaction => interaction.ResultFrameSequences))
            .ToHashSet();
        var observations = bundle.Entries
            .Where(entry => entry.StartsWith("raw/observations/frame-", StringComparison.Ordinal) &&
                            entry.EndsWith(".json", StringComparison.Ordinal))
            .Select(bundle.ReadJson<FrameObservation>)
            .Where(frame => representativeFrames.Contains(frame.Sequence))
            .OrderBy(frame => frame.Sequence)
            .ToArray();
        return new(manifest, observations, interactions);
    }
}
