using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class CommandBoundaryTests
{
    [TestCase("serverinfo", "serverinfo", false)]
    [TestCase("/serverinfo", "serverinfo", false)]
    [TestCase(".serverinfo", "serverinfo", true)]
    public void ParsesConsoleAndPlayerCommandForms(string input, string expectedName, bool expectedSilent)
    {
        bool parsed = CommandTextParser.TryParse(input, out ParsedCommandText command);

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.True);
            Assert.That(command.Name, Is.EqualTo(expectedName));
            Assert.That(command.Silent, Is.EqualTo(expectedSilent));
        });
    }

    [Test]
    public void SplitsSimpleArguments()
    {
        bool parsed = CommandTextParser.TryParse("/alpha one\ttwo three", out ParsedCommandText command);

        Assert.That(parsed, Is.True);
        Assert.That(command.Arguments, Is.EqualTo(new[] { "one", "two", "three" }));
    }

    [Test]
    public void PreservesQuotedArgumentsForPlayerNamesAndReasons()
    {
        bool parsed = CommandTextParser.TryParse(
            "/kick \"Player With Space\" \"griefing spawn\"",
            out ParsedCommandText command);

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.True);
            Assert.That(command.Name, Is.EqualTo("kick"));
            Assert.That(command.Arguments, Is.EqualTo(new[] { "Player With Space", "griefing spawn" }));
        });
    }

    [Test]
    public void PreservesLegacyEscapedQuoteSpaceAndBackslashSemantics()
    {
        bool parsed = CommandTextParser.TryParse(
            "/alpha one\\ two \"three\\\"four\" five\\\\six",
            out ParsedCommandText command);

        Assert.That(parsed, Is.True);
        Assert.That(command.Arguments, Is.EqualTo(new[] { "one two", "three\"four", "five\\six" }));
    }

    [Test]
    public void FormatsSourceFallbackText()
    {
        var message = MessageDescriptor.Info("command.example", 42)
            .WithDefaultText("Value: {0}");

        Assert.That(MessageFallbackFormatter.Format(message), Is.EqualTo("Value: 42"));
    }

    [Test]
    public void BadFallbackTemplateDegradesToStableKey()
    {
        var message = MessageDescriptor.Info("command.example", 42)
            .WithDefaultText("Value: {1}");

        Assert.That(MessageFallbackFormatter.Format(message), Is.EqualTo("command.example: 42"));
    }
}
