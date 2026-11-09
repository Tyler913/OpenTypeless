using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TypelessCore.Tests;

/// <summary>What each key, click and system event does to a dictation: the decisions the app's SessionController carries out.</summary>
public class DictationSessionTests
{
    /// <summary>testdata/session-cases.json, shared with the macOS tests so both apps decide the same way.</summary>
    private static JsonObject Cases([CallerFilePath] string source = "") =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "testdata", "session-cases.json")))!.AsObject();

    /// <summary>One effect of every kind, by the name the cases know it by.</summary>
    private static List<string> EveryEffect()
    {
        var effects = new List<SessionEffect> { new SessionEffect.StartRecording(), new SessionEffect.FinishRecording() };
        foreach (var mode in RecordingModes.All)
        {
            if (mode != RecordingMode.Dictation) effects.Add(new SessionEffect.SwitchMode(mode));
        }
        effects.Add(new SessionEffect.Cancel(false));
        effects.Add(new SessionEffect.Cancel(true));
        effects.Add(new SessionEffect.KeepCancelled());
        effects.Add(new SessionEffect.PasteLastDropped());
        foreach (var action in ShortcutActions.All) effects.Add(new SessionEffect.Perform(action));
        return effects.Select(e => e.Name).ToList();
    }

    private static List<string> Strings(JsonNode? node) => node!.AsArray().Select(n => (string)n!).ToList();

    private static RecordingMode Mode(string raw) => RecordingModes.All.Single(m => m.RawValue() == raw);

    private static string Flags(IEnumerable<bool> flags) => string.Join(",", flags.Select(f => f ? "yes" : "no"));

    /// <summary>Runs one step, and returns what was wrong with it (nothing, when it went as the case says).</summary>
    private static List<string> Run(JsonObject step, DictationSession session)
    {
        var problems = new List<string>();
        var name = (string)step["do"]!;
        double Time() => (double)step["at"]!;
        double Seconds() => (double)step["seconds"]!;

        IReadOnlyList<SessionEffect>? effects = null;
        var answers = false;
        object? returned = null;
        switch (name)
        {
            case "hotkeyDown": effects = session.HotkeyDown(); break;
            case "hotkeyUp": effects = session.HotkeyUp(Time()); break;
            case "otherKey": effects = session.OtherKey((int)step["key"]!, Time()); break;
            case "finishRequested": effects = session.FinishRequested(); break;
            case "sendKeyDown": effects = session.SendKeyDown(SendKeys.All.Single(k => k.RawValue() == (string)step["sendKey"]!)); break;
            case "escape": effects = session.Escape(Seconds()); break;
            case "locked": effects = session.Locked(Seconds()); break;
            case "action": effects = session.ActionPressed(ShortcutActions.All.Single(a => a.RawValue() == (string)step["action"]!)); break;
            case "selectionRefused": effects = session.SelectionRefused((int)step["generation"]!); break;
            case "unlocked":
                answers = true;
                returned = session.Unlocked();
                break;
            case "beginPasteLast":
                answers = true;
                returned = session.BeginPasteLast();
                break;
            case "endPasteLast": session.EndPasteLast((int)step["id"]!); break;
            case "began": session.Began(Time()); break;
            case "switched": session.Switched(Mode((string)step["mode"]!)); break;
            case "processing": session.Processing(); break;
            case "retrying": session.Retrying((bool)step["paste"]!, Mode((string)step["mode"]!)); break;
            case "deliverToClipboard":
                session.DeliverToClipboard(ScratchpadCapture.AllOutcomes.Single(o => o.RawValue() == (string)step["reason"]!));
                break;
            case "ended": session.Ended(); break;
            default: problems.Add("unknown step " + name); break;
        }

        if (effects != null)
        {
            // Every deciding step says what it expects, even when that's nothing.
            if (!step.ContainsKey("effects")) problems.Add("no \"effects\"");
            else if (!effects.Select(e => e.Name).SequenceEqual(Strings(step["effects"]))) problems.Add("effects " + string.Join(", ", effects.Select(e => e.Name)));
        }
        else if (step.ContainsKey("effects"))
        {
            problems.Add("\"effects\" on a step that decides nothing");
        }

        if (step.ContainsKey("returns"))
        {
            var expected = step["returns"];
            bool matches;
            if (returned is bool flag) matches = expected != null && expected.GetValueKind() == (flag ? JsonValueKind.True : JsonValueKind.False);
            else if (returned is int number) matches = expected != null && expected.GetValueKind() == JsonValueKind.Number && (int)expected == number;
            else matches = expected == null;
            if (!answers || !matches) problems.Add("returned " + (returned?.ToString() ?? "nothing"));
        }
        else if (answers)
        {
            problems.Add("no \"returns\"");
        }

        if (step["then"] is JsonObject then)
        {
            foreach (var check in then)
            {
                var value = check.Value;
                string actual, wanted;
                switch (check.Key)
                {
                    case "phase": actual = session.Phase.RawValue(); wanted = (string)value!; break;
                    case "mode": actual = session.Mode.RawValue(); wanted = (string)value!; break;
                    case "sendKey": actual = session.SendKey.RawValue(); wanted = (string)value!; break;
                    case "noPasteReason": actual = session.NoPasteReason.RawValue(); wanted = (string)value!; break;
                    case "generation": actual = session.Generation.ToString(); wanted = ((int)value!).ToString(); break;
                    case "handsFree": actual = Flags(new[] { session.HandsFree }); wanted = Flags(new[] { (bool)value! }); break;
                    case "locked": actual = Flags(new[] { session.IsLocked }); wanted = Flags(new[] { (bool)value! }); break;
                    case "byPaste": actual = Flags(new[] { session.DeliversByPaste }); wanted = Flags(new[] { (bool)value! }); break;
                    case "pasteLast":
                        actual = session.PendingPasteLast?.ToString() ?? "none";
                        wanted = value == null ? "none" : ((int)value).ToString();
                        break;
                    case "isCurrent":
                    {
                        var asked = value!.AsObject().OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
                        actual = Flags(asked.Select(p => session.IsCurrent(int.Parse(p.Key))));
                        wanted = Flags(asked.Select(p => (bool)p.Value!));
                        break;
                    }
                    case "pasteLastIsCurrent":
                    {
                        var asked = value!.AsObject().OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
                        actual = Flags(asked.Select(p => session.PasteLastIsCurrent(int.Parse(p.Key))));
                        wanted = Flags(asked.Select(p => (bool)p.Value!));
                        break;
                    }
                    default: actual = "?"; wanted = "unknown check " + check.Key; break;
                }
                if (actual != wanted) problems.Add($"{check.Key} is {actual}, not {wanted}");
            }
        }
        return problems;
    }

    [Fact]
    public void SessionsFollowSharedCases()
    {
        var cases = Cases()["cases"]!.AsArray();
        Assert.True(cases.Count >= 30);
        foreach (var node in cases)
        {
            var item = node!.AsObject();
            var session = new DictationSession();
            if (item["chords"] is JsonObject chords)
            {
                foreach (var chord in chords) session.Chords.Register(int.Parse(chord.Key), Mode((string)chord.Value!));
            }
            var name = (string)item["name"]!;
            var index = 0;
            foreach (var node2 in item["steps"]!.AsArray())
            {
                index++;
                var step = node2!.AsObject();
                var problems = Run(step, session);
                var what = (string)step["do"]!;
                Assert.True(problems.Count == 0, name + ", step " + index + " (" + what + "): " + string.Join("; ", problems));
            }
        }
    }

    /// <summary>The cases name every phase, mode and effect as both apps do, and try every effect at least once.</summary>
    [Fact]
    public void TheCasesCoverEveryNameAndEffect()
    {
        var cases = Cases();
        Assert.Equal(SessionPhases.All.Select(p => p.RawValue()).ToList(), Strings(cases["phases"]));
        Assert.Equal(RecordingModes.All.Select(m => m.RawValue()).ToList(), Strings(cases["modes"]));
        Assert.Equal(EveryEffect(), Strings(cases["effects"]));
        var seen = cases["cases"]!.AsArray()
            .SelectMany(c => c!["steps"]!.AsArray())
            .Where(s => s!.AsObject().ContainsKey("effects"))
            .SelectMany(s => Strings(s!["effects"]))
            .Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.Equal(EveryEffect().Order(StringComparer.Ordinal).ToList(), seen);
    }

    [Fact]
    public void AnAccidentalTapFollowsSharedCases()
    {
        var cases = Cases();
        Assert.Equal(DictationSession.AccidentalSeconds, (double)cases["accidentalSeconds"]!);
        foreach (var node in cases["accidental"]!.AsArray())
        {
            Assert.True(DictationSession.IsAccidental((double)node!["seconds"]!) == (bool)node["accidental"]!, node.ToJsonString());
        }
    }

    /// <summary>A new session is between dictations, pastes, and knows no chords.</summary>
    [Fact]
    public void ANewSessionIsIdle()
    {
        var session = new DictationSession();
        Assert.Equal(SessionPhase.Idle, session.Phase);
        Assert.Equal(0, session.Generation);
        Assert.Null(session.PressedAt);
        Assert.False(session.HandsFree);
        Assert.False(session.IsLocked);
        Assert.True(session.DeliversByPaste);
        Assert.Equal(RecordingMode.Dictation, session.Mode);
        Assert.Equal(SendKey.Off, session.SendKey);
        Assert.Null(session.PendingPasteLast);
        Assert.True(session.Chords.IsEmpty);
    }

    /// <summary>The press's own time decides tap or hold, whenever the app got round to reporting that the microphone opened.</summary>
    [Fact]
    public void ThePressTimeIsTheKeyEventsOwn()
    {
        var session = new DictationSession();
        session.HotkeyDown();
        // A Bluetooth microphone took a second to open: the key, pressed at 100, was let go of at 100.2 meanwhile.
        session.Began(100);
        Assert.Equal(100.0, session.PressedAt!.Value);
        Assert.Empty(session.HotkeyUp(100.2));
        Assert.True(session.HandsFree);
        session.Ended();
        Assert.Null(session.PressedAt);
    }

    /// <summary>
    /// The app's views follow the phase: told once per change, after everything that changes with it (the tray icon reads
    /// the state it is told about), and not at all when a report leaves the phase as it was.
    /// </summary>
    [Fact]
    public void PhaseChangesAreAnnouncedOnceAndLast()
    {
        var session = new DictationSession();
        var seen = new List<string>();
        session.PhaseChanged += () => seen.Add(
            session.Phase.RawValue() + " " + session.Generation + " " + session.Mode.RawValue() + " "
            + Flags(new[] { session.HandsFree, session.DeliversByPaste }));

        session.HotkeyDown();
        Assert.Empty(seen);
        session.Began(100);
        session.HotkeyUp(100.2);
        session.Switched(RecordingMode.Translate);
        session.Processing();
        session.Processing();
        session.Ended();
        session.Ended();
        session.Retrying(false, RecordingMode.Translate);
        Assert.Equal(
            new List<string> { "recording 0 dictation no,yes", "processing 0 translate yes,yes", "idle 1 dictation no,yes", "processing 2 translate no,no" },
            seen);
    }
}
