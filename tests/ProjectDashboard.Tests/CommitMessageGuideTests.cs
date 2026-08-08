using ProjectDashboard.Models;

namespace ProjectDashboard.Tests;

/// <summary>
/// The commit box's guidance (X-05). Every rule here is advisory: git accepts all of these
/// messages, so what has to hold is that the counters describe the message accurately and
/// that the one warning fires on a real structural mistake, never on ordinary typing.
/// </summary>
public class CommitMessageGuideTests
{
    [Fact]
    public void AnEmptyBox_IsCountedButNeverScolded()
    {
        var guide = CommitMessageGuide.For("");

        Assert.Equal(0, guide.SubjectLength);
        Assert.False(guide.SubjectOverLimit);
        Assert.False(guide.HasWarning);
        Assert.Equal("subject 0/50", guide.CounterText);
    }

    [Fact]
    public void ASubjectPastFifty_IsReportedAndNotTruncated()
    {
        var subject = new string('x', 51);
        var guide = CommitMessageGuide.For(subject);

        Assert.Equal(51, guide.SubjectLength);
        Assert.True(guide.SubjectOverLimit);
        Assert.Equal("subject 51/50", guide.CounterText);
    }

    [Fact]
    public void ASubjectOfExactlyFifty_IsWithinTheGuide()
    {
        Assert.False(CommitMessageGuide.For(new string('x', 50)).SubjectOverLimit);
    }

    [Fact]
    public void TheBodyCounter_ReportsItsLongestLine()
    {
        var guide = CommitMessageGuide.For($"subject\n\nshort\n{new string('y', 73)}\nshort");

        Assert.Equal(73, guide.LongestBodyLine);
        Assert.True(guide.BodyOverLimit);
        Assert.Equal("subject 7/50 · body 73/72", guide.CounterText);
    }

    [Fact]
    public void ABodyOfExactlySeventyTwo_IsWithinTheGuide()
    {
        Assert.False(CommitMessageGuide.For($"subject\n\n{new string('y', 72)}").BodyOverLimit);
    }

    [Fact]
    public void ABlankFirstLine_IsWarnedAbout()
    {
        var guide = CommitMessageGuide.For("\nthe real subject");

        Assert.True(guide.HasWarning);
        Assert.Contains("first line", guide.Warning);
    }

    /// <summary>
    /// Line 2 is the separator git reads the body after. Text on it makes the whole message
    /// one long subject in every log the reader will later scan.
    /// </summary>
    [Fact]
    public void ABodyStartedWithoutABlankLine_IsWarnedAbout()
    {
        var guide = CommitMessageGuide.For("subject\nbody starts here");

        Assert.True(guide.HasWarning);
        Assert.Contains("line 2", guide.Warning);
    }

    [Fact]
    public void AWellFormedMessage_CarriesNoWarning()
    {
        Assert.False(CommitMessageGuide.For("subject\n\nbody\nmore body").HasWarning);
    }

    /// <summary>Whitespace alone is an empty box, not a blank-subject mistake.</summary>
    [Fact]
    public void AWhitespaceOnlyBox_CarriesNoWarning()
    {
        Assert.False(CommitMessageGuide.For("   \n  \n").HasWarning);
    }

    [Fact]
    public void WindowsLineEndings_AreCountedTheSameWay()
    {
        var guide = CommitMessageGuide.For("subject\r\n\r\nbody");

        Assert.Equal(7, guide.SubjectLength);
        Assert.False(guide.HasWarning);
    }

    [Theory]
    [InlineData("", "picked", "picked")]
    [InlineData("old subject", "picked", "picked")]
    [InlineData("old subject\n\nbody kept", "picked", "picked\n\nbody kept")]
    [InlineData("old\r\n\r\nbody", "picked", "picked\n\nbody")]
    public void PickingASubject_ReplacesOnlyTheFirstLine(string message, string subject, string expected)
    {
        Assert.Equal(expected, CommitMessageGuide.WithSubject(message, subject));
    }
}
