using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace TypelessCore.Tests;

public class HudPlacementTests
{
    /// <summary>testdata/hud-placement-cases.json, shared with the macOS tests so both apps place the capsule the same way.</summary>
    private static JsonNode Cases([CallerFilePath] string source = "") =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "testdata", "hud-placement-cases.json")))!;

    private static HudRect? Rect(JsonNode? node) =>
        node is JsonArray a ? new HudRect((double)a[0]!, (double)a[1]!, (double)a[2]!, (double)a[3]!) : null;

    [Fact]
    public void SharedAnchorCases()
    {
        var file = Cases();
        var cases = file["anchor"]!.AsArray();
        Assert.True(cases.Count >= 15);
        foreach (var node in cases)
        {
            var id = (string?)node!["id"];
            var metrics = file["metrics"]![(string)node["metrics"]!]!;
            var extent = node["extent"]!.AsArray();
            var anchor = HudPlacement.Anchor(
                HudPositions.Parse((string?)node["position"]),
                Rect(node["area"])!.Value,
                new HudSize((double)extent[0]!, (double)extent[1]!),
                Rect(node["caret"]),
                new HudPlacement.Metrics((double)metrics["inset"]!, (double)metrics["gap"]!, (double)metrics["margin"]!));
            var expect = node["expect"]!;
            var expected = new HudAnchor((double)expect["centerX"]!, (double)expect["edgeY"]!, (bool)expect["growsDown"]!);
            Assert.True(expected == anchor, $"{id}: expected {expected}, got {anchor}");
        }
    }

    [Fact]
    public void SharedScreenCases()
    {
        var cases = Cases()["screen"]!.AsArray();
        Assert.True(cases.Count >= 10);
        foreach (var node in cases)
        {
            var screens = node!["screens"]!.AsArray().Select(screen => Rect(screen)!.Value).ToList();
            HudPoint? mouse = node["mouse"] is JsonArray m ? new HudPoint((double)m[0]!, (double)m[1]!) : null;
            var index = HudPlacement.Screen(screens, Rect(node["caret"]), Rect(node["window"]), mouse);
            var expected = (int?)node["expect"];
            Assert.True(expected == index, $"{node["id"]}: expected {expected?.ToString() ?? "null"}, got {index?.ToString() ?? "null"}");
        }
    }

    [Fact]
    public void SharedSavedValues()
    {
        foreach (var node in Cases()["saved"]!.AsArray())
        {
            var position = HudPositions.Parse((string?)node!["saved"]);
            Assert.Equal((string)node["expect"]!, position.RawValue());
        }
    }

    [Fact]
    public void EveryPositionKeepsItsName()
    {
        Assert.Equal(["bottom", "top", "caret"], HudPositions.All.Select(p => p.RawValue()));
        foreach (var position in HudPositions.All) Assert.Equal(position, HudPositions.Parse(position.RawValue()));
    }

    [Fact]
    public void TheFrameGrowsAwayFromTheEdgeThatStaysPut()
    {
        Assert.Equal(new HudRect(80, 50, 40, 20), new HudAnchor(100, 50, GrowsDown: true).Frame(40, 20));
        Assert.Equal(new HudRect(80, 30, 40, 20), new HudAnchor(100, 50, GrowsDown: false).Frame(40, 20));
        // Taller (the preview, a notice that wraps): the edge stays where it was.
        Assert.Equal(50, new HudAnchor(100, 50, GrowsDown: true).Frame(40, 70).MinY);
        Assert.Equal(50, new HudAnchor(100, 50, GrowsDown: false).Frame(40, 70).MaxY);
    }

    /// <summary>Wherever the cursor is on the screen, the capsule at its largest stays on it and off the line typed into.</summary>
    [Fact]
    public void NearTheCursorItNeverCoversTheLineAndStaysOnScreen()
    {
        var area = new HudRect(-1280, 40, 1280, 984);
        var extent = new HudSize(420, 80);
        var metrics = new HudPlacement.Metrics(28, 8, 8);
        for (var y = area.MinY; y < area.MaxY - 20; y += 7)
        {
            for (var x = area.MinX; x < area.MaxX; x += 53)
            {
                var line = new HudRect(x, y, 2, 20);
                var anchor = HudPlacement.Anchor(HudPosition.Caret, area, extent, line, metrics);
                var frame = anchor.Frame(extent.Width, extent.Height);
                Assert.True(frame.MinX >= area.MinX && frame.MaxX <= area.MaxX && frame.MinY >= area.MinY && frame.MaxY <= area.MaxY,
                            $"{line} → {frame}");
                Assert.True(frame.MaxY <= line.MinY || frame.MinY >= line.MaxY, $"{line} → {frame}");
            }
        }
    }
}
