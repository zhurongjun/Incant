using System.Globalization;
using Incant.Base.Log;
using LogRecorder = Incant.Base.Log.Log;

namespace Incant.UnitTest.Base.Log;

[Collection(LogCollection.Name)]
public sealed class LogRenderingTests : LogTestBase
{
    [Fact]
    public void ZeroThroughFourAndParamsOverloadsRenderInOccurrenceOrder()
    {
        var sink = Start();

        LogRecorder.Info("zero");
        LogRecorder.Info("one {A}", 1);
        LogRecorder.Info("two {A} {B}", 1, 2);
        LogRecorder.Info("three {A} {B} {C}", 1, 2, 3);
        LogRecorder.Info("four {A} {B} {C} {D}", 1, 2, 3, 4);
        LogRecorder.Info("five {A} {B} {C} {D} {E}", 1, 2, 3, 4, Param.Label(5));
        LogRecorder.Stop();

        Assert.Equal(
            ["zero", "one 1", "two 1 2", "three 1 2 3", "four 1 2 3 4", "five 1 2 3 4 5"],
            sink.Events.Select(logEvent => logEvent.Message));
        Assert.Equal(["A", "B", "C", "D", "E"], sink.Events[^1].Properties.Select(property => property.Name));
        var role = Assert.IsType<ParamDecoratorRole>(sink.Events[^1].Properties[^1].Decorator);
        Assert.Equal(Role.Label, role.Role);
        Assert.Equal(5, role.Next);
    }

    [Fact]
    public void EveryLevelUsesItsDeclaredLevelAndDefaultCategory()
    {
        var sink = Start();

        LogRecorder.Trace("trace");
        LogRecorder.Debug("debug");
        LogRecorder.Info("info");
        LogRecorder.Warning("warning");
        LogRecorder.Error("error");
        LogRecorder.Fatal("fatal");
        LogRecorder.Stop();

        Assert.Equal(
            [
                LogLevel.Trace,
                LogLevel.Debug,
                LogLevel.Info,
                LogLevel.Warning,
                LogLevel.Error,
                LogLevel.Fatal,
            ],
            sink.Events.Select(logEvent => logEvent.Level));
        Assert.All(sink.Events, logEvent => Assert.Equal(LogCategory.General, logEvent.Category));
        Assert.Equal(
            ["trace", "debug", "info", "warning", "error", "fatal"],
            sink.Events.Select(logEvent => logEvent.Message));
    }

    [Fact]
    public void EscapingAlignmentFormattingAndDuplicateNamesUseInvariantRules()
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var sink = Start();

            LogRecorder.Info("Value {{ {Value,8:N2} }} {Value:X4}", 12.5, 15);
            LogRecorder.Stop();

            RenderedLogEvent logEvent = Assert.Single(sink.Events);
            Assert.Equal("Value {    12.50 } 000F", logEvent.Message);
            Assert.Equal(["Value", "Value"], logEvent.Properties.Select(property => property.Name));
            Assert.Equal(12.5, logEvent.Properties[0].Value.Value);
            Assert.Equal(15L, logEvent.Properties[1].Value.Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void MutableObjectsAndStructuredValuesAreSnapshottedAtTheCallSite()
    {
        var sink = Start();
        var text = new MutableText("before");
        var structured = new MutableStructure { Name = "before", Count = 3 };

        ParamDecorator decoratedText = Param.Label(text);
        ParamDecorator decoratedStructure = Param.Important(Param.Structured(structured));
        LogRecorder.Info("Text {Text} Structure {Structure}", decoratedText, decoratedStructure);
        text.Value = "after";
        structured.Name = "after";
        structured.Count = 9;
        LogRecorder.Stop();

        RenderedLogEvent logEvent = Assert.Single(sink.Events);
        Assert.Equal("before", logEvent.Properties[0].Value.Value);
        Assert.Same(decoratedText, logEvent.Properties[0].Decorator);
        Assert.Same(text, decoratedText.Next);
        Assert.Equal(LogValueKind.Structure, logEvent.Properties[1].Value.Kind);
        var members = Assert.IsAssignableFrom<IReadOnlyList<LogStructureProperty>>(
            logEvent.Properties[1].Value.Value);
        Assert.Equal("before", Assert.Single(members, member => member.Name == "Name").Value.Value);
        Assert.Equal(3L, Assert.Single(members, member => member.Name == "Count").Value.Value);
        Assert.Same(decoratedStructure, logEvent.Properties[1].Decorator);
        var structuredDecorator = Assert.IsType<StructuredParamDecorator>(decoratedStructure.Next);
        Assert.Same(structured, structuredDecorator.Next);
    }

    [Fact]
    public void FrameworkPreservesRoleAndCustomDecoratorsInTheRenderedTree()
    {
        var sink = Start();
        var rootCustom = new CustomTextDecorator("root custom", null);
        TextDecoratorRole rootDecorator = Text.Warning(rootCustom);
        TextDecoratorRole labelDecorator = Text.Label();
        var customText = new CustomTextDecorator("custom text", labelDecorator);
        var parameterRole = new ParamDecoratorRole(Role.Important, "value");
        var customParam = new CustomParamDecorator("custom parameter", parameterRole);

        LogRecorder.Info(
            rootDecorator,
            "{#Outer}outer {Value}{/Outer}",
            customText,
            customParam);
        LogRecorder.Stop();

        RenderedLogEvent logEvent = Assert.Single(sink.Events);
        var root = Assert.IsType<DecoratedTextScope>(logEvent.Root);
        Assert.Same(rootDecorator, root.Decorator);
        Assert.Same(rootCustom, rootDecorator.Next);
        Assert.Equal("root custom", rootCustom.Name);
        var outer = Assert.IsType<DecoratedTextScope>(Assert.Single(root.Children));
        Assert.Same(customText, outer.Decorator);
        Assert.Same(labelDecorator, customText.Next);
        Assert.Equal("custom text", customText.Name);
        var parameter = Assert.IsType<ParamText>(outer.Children[1]);
        Assert.Same(customParam, parameter.Property.Decorator);
        Assert.Same(parameterRole, customParam.Next);
        Assert.Equal("value", parameterRole.Next);
        Assert.Equal("custom parameter", customParam.Name);
        Assert.Equal(Role.Important, parameterRole.Role);
        Assert.Equal("outer value", logEvent.Message);
    }

    [Fact]
    public void RoleDecoratorsRejectUndefinedRoles()
    {
        const Role InvalidRole = (Role)int.MaxValue;

        Assert.Throws<ArgumentOutOfRangeException>(() => new TextDecoratorRole(InvalidRole));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParamDecoratorRole(InvalidRole, "value"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Param.Role(InvalidRole, "value"));
    }

    [Fact]
    public void ParameterDecoratorChainEndsAtTheFirstNonDecoratorValue()
    {
        var sink = Start();
        var innerDecorator = new CustomParamDecorator("inner", "value");
        var outerDecorator = new CustomParamDecorator("outer", innerDecorator);

        LogRecorder.Info("{Value}", outerDecorator);
        LogRecorder.Stop();

        RenderedLogEvent logEvent = Assert.Single(sink.Events);
        LogProperty property = Assert.Single(logEvent.Properties);
        Assert.Equal("value", property.FormattedText);
        Assert.Same(outerDecorator, property.Decorator);
        Assert.Same(innerDecorator, outerDecorator.Next);
        Assert.Equal("value", innerDecorator.Next);
    }

    [Fact]
    public void ParameterDecoratorCanTerminateInNull()
    {
        var sink = Start();
        ParamDecorator decorator = Param.Muted(null);

        LogRecorder.Info("{Value}", decorator);
        LogRecorder.Stop();

        LogProperty property = Assert.Single(Assert.Single(sink.Events).Properties);
        Assert.Equal(LogValueKind.Null, property.Value.Kind);
        Assert.Same(decorator, property.Decorator);
        Assert.Null(decorator.Next);
    }

    [Fact]
    public void InvalidTemplatesAndFormattingBecomeStableFallbackEvents()
    {
        var sink = Start();

        LogRecorder.Info("unclosed {Value", 1);
        LogRecorder.Info("missing {A} {B}", 1);
        LogRecorder.Info("{#A}{#B}{/A}{/B}", Text.Important(), Text.Error());
        LogRecorder.Info("bad {Date:Q}", DateTime.UnixEpoch);
        LogRecorder.Stop();

        Assert.Equal(4, sink.Events.Count);
        Assert.All(sink.Events, logEvent => Assert.False(string.IsNullOrWhiteSpace(logEvent.TemplateError)));
        Assert.Equal("unclosed {Value", sink.Events[0].Message);
        Assert.Equal("missing {A} {B}", sink.Events[1].Message);
        Assert.Equal("bad {Date:Q}", sink.Events[3].Message);
    }

    [Fact]
    public void NullTemplateIsEmittedAsAFallbackInsteadOfBeingDropped()
    {
        var sink = Start();

        LogRecorder.Info(null!);
        LogRecorder.Stop();

        RenderedLogEvent logEvent = Assert.Single(sink.Events);
        Assert.Equal(string.Empty, logEvent.Message);
        Assert.Equal(string.Empty, logEvent.MessageTemplate);
        Assert.Contains("cannot be null", logEvent.TemplateError, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureAndExceptionFailuresDoNotEscapeTheLoggingCall()
    {
        var sink = Start();
        var exception = new InvalidOperationException("operation failed");

        LogRecorder.Info("Broken {Value}", new ThrowingStringValue());
        LogRecorder.Error(exception, "Error in {Target}", "build");
        LogRecorder.Error(new ThrowingSnapshotException(), "Undescribable exception");
        LogRecorder.Stop();

        Assert.Equal(3, sink.Events.Count);
        Assert.Equal(LogValueKind.CaptureError, sink.Events[0].Properties[0].Value.Kind);
        Assert.Contains(nameof(InvalidOperationException), sink.Events[0].Message, StringComparison.Ordinal);
        Assert.Contains("operation failed", sink.Events[1].ExceptionText, StringComparison.Ordinal);
        Assert.Equal(LogLevel.Error, sink.Events[1].Level);
        Assert.Contains("snapshot failed", sink.Events[2].ExceptionText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RootAndExplicitCategoryOverloadsPreserveBothValues()
    {
        var sink = Start();
        var category = new LogCategory("Custom");
        TextDecoratorRole rootDecorator = Text.Error();

        LogRecorder.Warning(
            category,
            rootDecorator,
            "failed {Target}",
            Param.Label("sample"));
        LogRecorder.Stop();

        RenderedLogEvent logEvent = Assert.Single(sink.Events);
        Assert.Equal(category, logEvent.Category);
        Assert.Equal(LogLevel.Warning, logEvent.Level);
        var root = Assert.IsType<DecoratedTextScope>(logEvent.Root);
        Assert.Same(rootDecorator, root.Decorator);
        var parameterRole = Assert.IsType<ParamDecoratorRole>(logEvent.Properties[0].Decorator);
        Assert.Equal(Role.Label, parameterRole.Role);
        Assert.Equal("sample", parameterRole.Next);
    }

    private static CollectingLogSink Start()
    {
        var sink = new CollectingLogSink();
        LogRecorder.MinimumLevel = LogLevel.Trace;
        LogRecorder.AddSink(sink);
        LogRecorder.Start(new LogOptions());
        return sink;
    }

    private sealed class MutableText(string value)
    {
        internal string Value { get; set; } = value;

        public override string ToString() => Value;
    }

    private sealed class MutableStructure
    {
        public required string Name { get; set; }

        public int Count { get; set; }
    }

    private sealed class ThrowingStringValue
    {
        public override string ToString()
        {
            throw new InvalidOperationException("snapshot failed");
        }
    }

    private sealed class ThrowingSnapshotException : Exception
    {
        public override string Message => throw new InvalidOperationException("message failed");

        public override string ToString()
        {
            throw new InvalidOperationException("snapshot failed");
        }
    }

    private sealed class CustomTextDecorator(string name, TextDecorator? next) : TextDecorator(next)
    {
        internal string Name { get; } = name;
    }

    private sealed class CustomParamDecorator(string name, object? next) : ParamDecorator(next)
    {
        internal string Name { get; } = name;
    }
}
