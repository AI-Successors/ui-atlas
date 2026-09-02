using System.Text.Json;
using System.Text.Json.Nodes;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class UiAtlasCompatibilityTests
{
    [Fact]
    public void HumanReadablePublicationNestsWorldWindowsVariantsAndControlsWithLineage()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var first = Path.Combine(temp.Path, "human-first.json");
        var second = Path.Combine(temp.Path, "human-second.json");

        var firstHash = HumanReadableMapExporter.Publish(graph, first, true);
        var secondHash = HumanReadableMapExporter.Publish(graph, second, true);
        using var document = JsonDocument.Parse(File.ReadAllBytes(first));
        var root = document.RootElement;

        Assert.Equal(firstHash, secondHash);
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        Assert.True(HumanReadableMapExporter.ValidateFile(first).IsValid);
        Assert.Equal(HumanReadableMapExporter.FormatVersion, root.GetProperty("formatVersion").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("interactionTrace").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("routeGraph").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("affordances").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("negativeExamples").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("app").GetProperty("name").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("process").GetProperty("name").GetString()));
        foreach (var worldName in new[] { "rawDataStreams", "rawWorld", "semanticWorld" })
        {
            var windows = root.GetProperty(worldName).GetProperty("windows").EnumerateArray().ToArray();
            Assert.NotEmpty(windows);
            Assert.All(windows, window =>
            {
                Assert.True(window.GetProperty("lineage").TryGetProperty("sourceWindowIds", out _));
                Assert.NotEmpty(window.GetProperty("variants").EnumerateArray());
                Assert.All(window.GetProperty("variants").EnumerateArray(), variant =>
                {
                    Assert.True(variant.GetProperty("lineage").TryGetProperty("sourceWindowIds", out _));
                    Assert.Equal(JsonValueKind.Array, variant.GetProperty("controls").ValueKind);
                });
            });
        }
        Assert.Contains(root.GetProperty("semanticWorld").GetProperty("windows").EnumerateArray(),
            window => window.GetProperty("variants").EnumerateArray().Any(variant => variant.GetProperty("reason").GetString() == "semantic_popup_variant"));
    }

    [Fact]
    public void HumanReadableV2GroupsMultiSurfaceResultsAndSeparatesNegativeExamples()
    {
        using var temp = new TempDirectory();
        var success = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(
            temp.Path, "success.mlrec", sessionId: "success", interactionTrace: true));
        var successPath = Path.Combine(temp.Path, "success.json");
        HumanReadableMapExporter.Publish(success, successPath, true);
        using var successDocument = JsonDocument.Parse(File.ReadAllBytes(successPath));
        var successRoot = successDocument.RootElement;
        var step = Assert.Single(Assert.Single(successRoot.GetProperty("interactionTrace").EnumerateArray())
            .GetProperty("steps").EnumerateArray());
        Assert.True(step.GetProperty("targetStateIds").GetArrayLength() >= 1);
        Assert.Empty(successRoot.GetProperty("negativeExamples").EnumerateArray());

        var noChange = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(
            temp.Path, "no-change.mlrec", sessionId: "no-change", interactionTrace: true,
            interactionOutcome: InteractionOutcome.NoChange));
        var noChangePath = Path.Combine(temp.Path, "no-change.json");
        HumanReadableMapExporter.Publish(noChange, noChangePath, true);
        using var noChangeDocument = JsonDocument.Parse(File.ReadAllBytes(noChangePath));
        var negative = Assert.Single(noChangeDocument.RootElement.GetProperty("negativeExamples").EnumerateArray());
        Assert.Equal("NoChange", negative.GetProperty("outcome").GetString());
        Assert.Empty(noChangeDocument.RootElement.GetProperty("routeGraph").EnumerateArray());
        Assert.All(noChange.Edges.Where(edge => edge.Kind == "interaction"),
            edge => Assert.Equal(edge.FromId, edge.ToId));
    }

    [Fact]
    public void HumanReadableValidatorStillAcceptsVersionOneWithoutInteractionAreas()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var path = Path.Combine(temp.Path, "v2.json");
        HumanReadableMapExporter.Publish(graph, path, true);
        var legacy = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        legacy["formatVersion"] = HumanReadableMapExporter.LegacyFormatVersion;
        legacy.Remove("interactionTrace");
        legacy.Remove("routeGraph");
        legacy.Remove("affordances");
        legacy.Remove("negativeExamples");

        Assert.True(HumanReadableMapExporter.Validate(JsonSerializer.SerializeToUtf8Bytes(legacy)).IsValid);
    }

    [Fact]
    public void SqliteMapPublicationHasValidatedChecksumAndLosslessGraph()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var path = Path.Combine(temp.Path, "map.db");

        SqliteMapExporter.Publish(graph, path);

        Assert.True(SqliteMapExporter.ValidateFile(path).IsValid);
        Assert.Equal(graph.Metadata.SemanticHash, SqliteGraphStore.Load(path).Metadata.SemanticHash);
    }

    [Fact]
    public void CompatibilityPublicationIsDeterministicAndReferenceComplete()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var first = Path.Combine(temp.Path, "first.json");
        var second = Path.Combine(temp.Path, "second.json");

        var firstHash = UiAtlasVNextCompatibilityExporter.Publish(graph, first, "stable-project", true);
        var secondHash = UiAtlasVNextCompatibilityExporter.Publish(graph, second, "stable-project", true);
        using var document = JsonDocument.Parse(File.ReadAllBytes(first));
        var root = document.RootElement;
        var authoring = root.GetProperty("authoring");
        var windows = authoring.GetProperty("windows").EnumerateArray().ToArray();
        var controls = authoring.GetProperty("controls").EnumerateArray().ToArray();

        Assert.Equal(firstHash, secondHash);
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        Assert.True(UiAtlasVNextCompatibilityValidator.ValidateFile(first).IsValid);
        Assert.True(File.Exists(first + ".sha256"));
        Assert.Equal(5, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("uikg/vnext", root.GetProperty("kind").GetString());
        Assert.Single(authoring.GetProperty("applications").EnumerateArray());
        Assert.Equal(2, windows.Length);
        Assert.Equal(5, controls.Length);
        Assert.Equal(2, root.GetProperty("observationPackages").GetArrayLength());
        Assert.Equal(2, root.GetProperty("buildRevision").GetProperty("sourcePackageCount").GetInt32());
        Assert.All(root.GetProperty("observationPackages").EnumerateArray(), package =>
        {
            Assert.Contains(package.GetProperty("streams").EnumerateArray(), stream => stream.GetProperty("kind").GetString() == "stream.win32");
            Assert.Contains(package.GetProperty("streams").EnumerateArray(), stream => stream.GetProperty("kind").GetString() == "stream.uia");
            var payload = package.GetProperty("artifacts")[0].GetProperty("payloadJson").GetString();
            using var observation = JsonDocument.Parse(payload!);
            Assert.NotEmpty(observation.RootElement.GetProperty("windows").EnumerateArray());
            Assert.NotEmpty(observation.RootElement.GetProperty("automationControls").EnumerateArray());
        });
        Assert.Equal(["applications", "windows", "controls"], authoring.EnumerateObject().Select(property => property.Name));
        Assert.All(controls, control => Assert.Contains(windows, window => window.GetProperty("windowId").GetString() == control.GetProperty("windowId").GetString()));
    }

    [Fact]
    public void CompatibilityPublicationRequiresFullIdentityAcknowledgement()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var path = Path.Combine(temp.Path, "export.json");

        Assert.Throws<InvalidOperationException>(() => UiAtlasVNextCompatibilityExporter.Publish(graph, path, "stable-project", false));
        var safe = GraphExport.ApplyProfile(graph, false);
        Assert.Throws<InvalidOperationException>(() => UiAtlasVNextCompatibilityExporter.Publish(safe, path, "stable-project", true));
    }

    [Fact]
    public void CompatibilityPublicationOmitsMachineAndSessionLinkage()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var path = Path.Combine(temp.Path, "export.json");
        UiAtlasVNextCompatibilityExporter.Publish(graph, path, "stable-project", true);
        var json = File.ReadAllText(path);

        Assert.DoesNotContain("golden", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sessionDir", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("executablePath", json, StringComparison.OrdinalIgnoreCase);
        using var publication = JsonDocument.Parse(json);
        foreach (var package in publication.RootElement.GetProperty("observationPackages").EnumerateArray())
        {
            var payload = package.GetProperty("artifacts")[0].GetProperty("payloadJson").GetString();
            using var observation = JsonDocument.Parse(payload!);
            Assert.All(observation.RootElement.GetProperty("windows").EnumerateArray(),
                window => Assert.Equal(string.Empty, window.GetProperty("hwndHex").GetString()));
            Assert.All(observation.RootElement.GetProperty("automationControls").EnumerateArray(),
                control => Assert.Equal(string.Empty, control.GetProperty("windowHwndHex").GetString()));
        }
    }

    [Fact]
    public void CompatibilityValidatorRejectsCaseCollisionAndDanglingReference()
    {
        var bytes = """
            {"schemaVersion":5,"kind":"uikg/vnext","projectId":"p","generatedUtc":"1970-01-01T00:00:00Z","UiAtlasCore":{"adapterVersion":"ui-atlas-vnext-compat/1","privacyProfile":"sensitive-identities/1","sourceFormatVersion":"ui-atlas.uikg/3","sourceSemanticHash":"0000000000000000000000000000000000000000000000000000000000000000"},"buildRevision":{"buildId":"build_000000000000000000000000","projectId":"p","generatedUtc":"1970-01-01T00:00:00Z","sourcePackageCount":0},"authoring":{"applications":[{"applicationId":"App","name":"one","inTwinDefault":true},{"applicationId":"app","name":"two","inTwinDefault":true}],"windows":[{"windowId":"window","sceneId":0,"applicationId":"missing","kind":"Window","title":"w","mode":"normal","chromeMode":"normal","x":0,"y":0,"width":1,"height":1,"isModal":false,"showInTaskbar":true,"alwaysOnTop":false,"defaultZIndex":0,"semanticSurfaceId":"window","semanticSurfaceKind":"Window","semanticSurfaceDisplay":"w"}],"controls":[]}}
            """u8.ToArray();

        var report = UiAtlasVNextCompatibilityValidator.Validate(bytes);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "compat.duplicate-id");
        Assert.Contains(report.Issues, issue => issue.Code == "compat.reference");
    }

    [Fact]
    public void CompatibilityValidatorFailsClosedOnNullCollections()
    {
        var bytes = """
            {"schemaVersion":5,"kind":"uikg/vnext","projectId":"p","generatedUtc":"1970-01-01T00:00:00Z","UiAtlasCore":{"adapterVersion":"ui-atlas-vnext-compat/1","privacyProfile":"sensitive-identities/1","sourceFormatVersion":"ui-atlas.uikg/3","sourceSemanticHash":"0000000000000000000000000000000000000000000000000000000000000000"},"buildRevision":{"buildId":"build_000000000000000000000000","projectId":"p","generatedUtc":"1970-01-01T00:00:00Z","sourcePackageCount":0},"authoring":{"applications":null,"windows":[],"controls":[]}}
            """u8.ToArray();

        var report = UiAtlasVNextCompatibilityValidator.Validate(bytes);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "compat.count-limit");
    }

    [Fact]
    public void CompatibilityValidatorRejectsUnknownMembersAndSourceContractDrift()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var path = Path.Combine(temp.Path, "export.json");
        UiAtlasVNextCompatibilityExporter.Publish(graph, path, "stable-project", true);
        var json = File.ReadAllText(path);

        var unknown = json.Replace("\"schemaVersion\": 5", "\"unexpected\": true,\"schemaVersion\": 5", StringComparison.Ordinal);
        var driftedRoot = JsonNode.Parse(json)!.AsObject();
        driftedRoot["UiAtlasCore"]!["sourceFormatVersion"] = "ui-atlas.uikg/99";
        driftedRoot["buildRevision"]!["sourcePackageCount"] = 99;
        var drifted = driftedRoot.ToJsonString();

        Assert.Contains(UiAtlasVNextCompatibilityValidator.Validate(System.Text.Encoding.UTF8.GetBytes(unknown)).Issues,
            issue => issue.Code == "compat.member");
        var driftReport = UiAtlasVNextCompatibilityValidator.Validate(System.Text.Encoding.UTF8.GetBytes(drifted));
        Assert.Contains(driftReport.Issues, issue => issue.Code == "compat.profile");
        Assert.Contains(driftReport.Issues, issue => issue.Code == "compat.build");
    }

    [Fact]
    public void CompatibilityFileValidationRequiresMatchingChecksum()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var path = Path.Combine(temp.Path, "export.json");
        UiAtlasVNextCompatibilityExporter.Publish(graph, path, "stable-project", true);
        File.WriteAllText(path + ".sha256", new string('0', 64) + "  export.json\n");

        var report = UiAtlasVNextCompatibilityValidator.ValidateFile(path);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "compat.hash");
    }

    [Fact]
    public void CompatibilityValidatorRejectsSchemaInvalidPrimitiveValues()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var path = Path.Combine(temp.Path, "export.json");
        UiAtlasVNextCompatibilityExporter.Publish(graph, path, "stable-project", true);
        var root = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        var authoring = root["authoring"]!.AsObject();
        authoring["applications"]!.AsArray()[0]!["inTwinDefault"] = "true";
        authoring["windows"]!.AsArray()[0]!["sceneId"] = 7;
        authoring["controls"]!.AsArray()[0]!["sceneId"] = 7;

        var report = UiAtlasVNextCompatibilityValidator.Validate(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString()));

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "compat.type");
    }

    [Fact]
    public void CompatibilityFileValidationBoundsChecksumSidecar()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var path = Path.Combine(temp.Path, "export.json");
        UiAtlasVNextCompatibilityExporter.Publish(graph, path, "stable-project", true);
        File.WriteAllText(path + ".sha256", new string('0', 1_025));

        var report = UiAtlasVNextCompatibilityValidator.ValidateFile(path);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "compat.hash");
    }

    [Fact]
    public void CompatibilityFileValidationRejectsLinkedChecksum()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var path = Path.Combine(temp.Path, "export.json");
        UiAtlasVNextCompatibilityExporter.Publish(graph, path, "stable-project", true);
        var checksum = path + ".sha256";
        var actual = Path.Combine(temp.Path, "actual.sha256");
        File.Move(checksum, actual);
        try { File.CreateSymbolicLink(checksum, actual); }
        catch (IOException ex) when (ex.Message.Contains("privilege", StringComparison.OrdinalIgnoreCase)) { return; }

        var report = UiAtlasVNextCompatibilityValidator.ValidateFile(path);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "compat.read");
    }

    [Fact]
    public void CompatibilityValidatorRejectsControlParentAcrossWindows()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var path = Path.Combine(temp.Path, "export.json");
        UiAtlasVNextCompatibilityExporter.Publish(graph, path, "stable-project", true);
        var root = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        var controls = root["authoring"]!["controls"]!.AsArray();
        var first = controls[0]!.AsObject();
        var other = controls.Select(node => node!.AsObject()).First(control => control["windowId"]!.GetValue<string>() != first["windowId"]!.GetValue<string>());
        first["structure"]!["parentControlId"] = other["controlId"]!.GetValue<string>();

        var report = UiAtlasVNextCompatibilityValidator.Validate(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString()));

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "compat.reference");
    }

    [Fact]
    public void CompatibilityValidatorRejectsUnsafeNestedObservationContracts()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var path = Path.Combine(temp.Path, "export.json");
        UiAtlasVNextCompatibilityExporter.Publish(graph, path, "stable-project", true);

        var absolute = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        absolute["observationPackages"]!.AsArray()[0]!["artifacts"]!.AsArray()[0]!["path"] = "C:\\synthetic\\observation.json";
        Assert.False(Validate(absolute).IsValid);

        var traversal = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        traversal["observationPackages"]!.AsArray()[0]!["artifacts"]!.AsArray()[0]!["path"] = "../observation.json";
        Assert.False(Validate(traversal).IsValid);

        var emptyPayload = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        emptyPayload["observationPackages"]!.AsArray()[0]!["artifacts"]!.AsArray()[0]!["payloadJson"] = "{}";
        Assert.False(Validate(emptyPayload).IsValid);

        var duplicateArtifact = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        var artifacts = duplicateArtifact["observationPackages"]!.AsArray()[0]!["artifacts"]!.AsArray();
        artifacts.Add(artifacts[0]!.DeepClone());
        Assert.Contains(Validate(duplicateArtifact).Issues, issue => issue.Code == "compat.duplicate-id");

        var danglingStream = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        danglingStream["observationPackages"]!.AsArray()[0]!["streams"]!.AsArray()[0]!["artifactIds"] =
            new JsonArray("artifact_000000000000000000000000");
        Assert.Contains(Validate(danglingStream).Issues, issue => issue.Code == "compat.reference");

        var oversized = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        var artifact = oversized["observationPackages"]!.AsArray()[0]!["artifacts"]!.AsArray()[0]!.AsObject();
        var payload = JsonNode.Parse(artifact["payloadJson"]!.GetValue<string>())!.AsObject();
        var controls = payload["automationControls"]!.AsArray();
        while (controls.Count <= 12_000) controls.Add(new JsonObject());
        artifact["payloadJson"] = payload.ToJsonString();
        Assert.Contains(Validate(oversized).Issues, issue => issue.Code == "compat.count-limit");
    }

    [Fact]
    public void CompatibilityValidatorRejectsInconsistentObservationLineage()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var path = Path.Combine(temp.Path, "export.json");
        UiAtlasVNextCompatibilityExporter.Publish(graph, path, "stable-project", true);

        var session = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        session["observationPackages"]!.AsArray()[0]!["sourceSessionId"] = Guid.NewGuid().ToString("D");
        Assert.Contains(Validate(session).Issues, issue => issue.Code == "compat.observation");

        var revision = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        revision["observationPackages"]!.AsArray()[0]!["buildRevisionId"] = "build_000000000000000000000000";
        Assert.Contains(Validate(revision).Issues, issue => issue.Code == "compat.lineage");

        var scene = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        scene["observationPackages"]!.AsArray()[0]!["sceneId"] = 999;
        Assert.Contains(Validate(scene).Issues, issue => issue.Code == "compat.observation");

        var profile = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        profile["observationPackages"]!.AsArray()[0]!["source"] = "arbitrary";
        Assert.Contains(Validate(profile).Issues, issue => issue.Code == "compat.lineage");
    }

    private static ValidationReport Validate(JsonObject value) =>
        UiAtlasVNextCompatibilityValidator.Validate(System.Text.Encoding.UTF8.GetBytes(value.ToJsonString()));
}
