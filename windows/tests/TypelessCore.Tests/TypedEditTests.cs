using static TypelessCore.TypedEdit;

namespace TypelessCore.Tests;

public class TypedEditTests
{
    private static (string Text, bool Followed) Edit(string pasted, IEnumerable<Key> keys)
    {
        var edit = new TypedEdit(pasted);
        foreach (var key in keys)
        {
            if (!edit.Apply(key)) return (edit.Text, false);
        }
        return (edit.Text, true);
    }

    private static IEnumerable<Key> Type(string text)
    {
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) yield return new Key.Insert((string)e.Current);
    }

    private static Key Back(Unit unit = Unit.Character) => new Key.DeleteBackward(unit);
    private static Key Left(Unit unit = Unit.Character, bool extend = false) => new Key.Left(unit, extend);
    private static Key Right(Unit unit = Unit.Character, bool extend = false) => new Key.Right(unit, extend);

    [Fact]
    public void BackspacingTheLastWordAndRetypingIt() =>
        Assert.Equal(("deploy it to Kwazara", true), Edit("deploy it to Quasera", Enumerable.Repeat(Back(), 7).Concat(Type("Kwazara"))));

    [Fact]
    public void CtrlBackspaceDeletesTheLastWord()
    {
        Assert.Equal(("deploy it to Kwazara", true), Edit("deploy it to Quasera", Type("Kwazara").Prepend(Back(Unit.Word))));
        Assert.Equal(("ping Drovik.", true), Edit("ping Dravik.", Type("Drovik.").Prepend(Back(Unit.Word))));
    }

    [Fact]
    public void FixingAWordInTheMiddleWithTheArrows()
    {
        Key[] keys = [Left(Unit.Word), Left(Unit.Word), Left(), Left(), Left(), Left(), Back(), new Key.Insert("o")];
        Assert.Equal(("ping Drovik about it", true), Edit("ping Dravik about it", keys));
    }

    [Fact]
    public void SelectingAWordAndTypingOverIt()
    {
        // Shift+Ctrl+Left from the end selects the last word.
        Assert.Equal(("ping Drovik", true), Edit("ping Dravik", Type("Drovik").Prepend(Left(Unit.Word, extend: true))));
    }

    [Fact]
    public void ChineseIsFollowedCharacterByCharacter() =>
        Assert.Equal(("买了罗技鼠标", true), Edit("买了逻辑鼠标", new[] { Left(), Left(), Back(), Back() }.Concat(Type("罗技"))));

    [Fact]
    public void WhatCantBeFollowedStopsIt()
    {
        Assert.Equal(("", false), Edit("ab", [Back(), Back(), Back()]));
        Assert.False(Edit("ab", [Right()]).Followed);
        Assert.False(Edit("我们用罗技", [Left(Unit.Word)]).Followed);
        Assert.False(Edit("Quasera", [Back(Unit.Word)]).Followed);
        Assert.False(Edit("x", [new Key.Insert("")]).Followed);
    }

    [Fact]
    public void ADoubleClickedWordIsFoundAgain()
    {
        var edit = new TypedEdit("please ping Dravik about the release");
        edit.CursorLost();
        Assert.False(edit.Apply(new Key.Insert("x")));
        Assert.True(edit.Select("Dravik"));
        Assert.All(Type("Drovik").ToList(), key => Assert.True(edit.Apply(key)));
        Assert.Equal("please ping Drovik about the release", edit.Text);
    }

    [Fact]
    public void AClickedSelectionThatOccursTwiceIsAmbiguous()
    {
        var edit = new TypedEdit("the cat and the dog");
        edit.CursorLost();
        Assert.False(edit.Select("the"));
        Assert.False(edit.Select("bird"));
        Assert.False(edit.KnowsCursor);
        Assert.True(edit.Select("dog"));
        Assert.True(edit.Apply(new Key.Insert("fox")));
        Assert.Equal("the cat and the fox", edit.Text);
    }

    [Fact]
    public void TheStateBeforeAnUnfollowableKeyIsKept() =>
        Assert.Equal(("to Kwazara", false), Edit("to Quasera", Enumerable.Repeat(Back(), 7).Concat(Type("Kwazara")).Append(Right())));
}
