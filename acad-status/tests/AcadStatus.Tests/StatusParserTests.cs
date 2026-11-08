using System;
using AcadStatus.Core;

namespace AcadStatus.Tests;

public class StatusParserTests
{
    [Fact]
    public void ReadsAValidFile()
    {
        var json = TestData.Json(@short: "Suite 3/7", since: "2026-11-05T10:13:40Z", owner: "COM");
        var r = StatusParser.Parse("retrace-4812.json", json);
        Assert.True(r.Ok);
        var e = r.Entry!;
        Assert.Equal("retrace", e.Source);
        Assert.Equal("Retrace 0.6.2", e.Writer);
        Assert.Equal(4812, e.Pid);
        Assert.Equal(15, e.TtlSec);
        Assert.Equal("running", e.State);
        Assert.Equal("Suite 3/7", e.Summary);
        Assert.Equal("Suite 3/7", e.Short);
        Assert.Equal("COM", e.Owner);
        Assert.Equal(new DateTime(2026, 11, 5, 10, 13, 40, DateTimeKind.Utc), e.Since);
        Assert.Equal(TestData.Now, e.Updated);
        Assert.Equal(DateTimeKind.Utc, e.Updated.Kind);
        Assert.Equal("retrace-4812", e.Id);
        Assert.Equal(TestData.Now.AddSeconds(15), e.StaleAfter);
        Assert.Equal(json, e.Raw);
    }

    [Theory]
    [InlineData("v")]
    [InlineData("source")]
    [InlineData("writer")]
    [InlineData("pid")]
    [InlineData("updated")]
    [InlineData("ttlSec")]
    [InlineData("state")]
    [InlineData("summary")]
    public void ASkippedFileLacksARequiredField(string field)
    {
        var json = TestData.Json();
        // drop one field: rename it to something unknown
        var dropped = json.Replace("\"" + field + "\":", "\"x-" + field + "\":");
        var r = StatusParser.Parse("retrace-4812.json", dropped);
        Assert.False(r.Ok);
        Assert.Contains(field, r.Error);
    }

    [Theory]
    [InlineData("\"v\": 1", "\"v\": 0")]
    [InlineData("\"v\": 1", "\"v\": -3")]
    [InlineData("\"v\": 1", "\"v\": \"1\"")]
    [InlineData("\"v\": 1", "\"v\": 1.5")]
    [InlineData("\"v\": 1", "\"v\": null")]
    [InlineData("\"v\": 1", "\"v\": true")]
    public void VMustBeAnIntegerOfAtLeastOne(string from, string to)
    {
        var r = StatusParser.Parse("x.json", TestData.Json().Replace(from, to));
        Assert.False(r.Ok);
    }

    [Theory]
    [InlineData("\"v\": 1", "\"v\": 1")]
    [InlineData("\"v\": 1", "\"v\": 1.0")]
    [InlineData("\"v\": 1", "\"v\": 2")]
    [InlineData("\"v\": 1", "\"v\": 17")]
    public void AnIntegerVOfAtLeastOneIsFine(string from, string to)
    {
        Assert.True(StatusParser.Parse("x.json", TestData.Json().Replace(from, to)).Ok);
    }

    [Theory]
    [InlineData("\"pid\": 4812", "\"pid\": \"4812\"")]
    [InlineData("\"pid\": 4812", "\"pid\": 4812.5")]
    [InlineData("\"ttlSec\": 15", "\"ttlSec\": \"15\"")]
    [InlineData("\"source\": \"retrace\"", "\"source\": 7")]
    [InlineData("\"summary\": \"Suite 3/7\"", "\"summary\": null")]
    [InlineData("\"state\": \"running\"", "\"state\": \"\"")]
    [InlineData("\"state\": \"running\"", "\"state\": \"   \"")]
    [InlineData("\"updated\": \"2026-11-05T10:14:03Z\"", "\"updated\": \"yesterday-ish\"")]
    [InlineData("\"updated\": \"2026-11-05T10:14:03Z\"", "\"updated\": 12345")]
    public void AWrongTypeOrValueCountsAsMissing(string from, string to)
    {
        Assert.False(StatusParser.Parse("x.json", TestData.Json().Replace(from, to)).Ok);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    [InlineData("{")]
    [InlineData("{ \"v\": 1, ")]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"a string\"")]
    [InlineData("null")]
    [InlineData("42")]
    public void BadJsonIsSkippedNotThrown(string text)
    {
        var r = StatusParser.Parse("x.json", text);
        Assert.False(r.Ok);
        Assert.False(string.IsNullOrEmpty(r.Error));
    }

    [Fact]
    public void AHalfWrittenFileIsSkipped()
    {
        var json = TestData.Json(extra: ", \"detail\": { \"doc\": \"a.dwg\" }");
        for (int cut = 1; cut < json.Length - 1; cut += 7)
            Assert.False(StatusParser.Parse("x.json", json[..cut]).Ok, "cut at " + cut);
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        var json = TestData.Json(extra: ", \"future\": { \"a\": [1, 2, { \"b\": null }] }, \"alsoNew\": 7");
        Assert.True(StatusParser.Parse("x.json", json).Ok);
    }

    [Fact]
    public void AnUnknownStateIsKeptAsItsText()
    {
        var e = StatusParser.Parse("x.json", TestData.Json(state: "Compiling")).Entry!;
        Assert.Equal("Compiling", e.State);
        Assert.Equal("compiling", e.StateKey);
        Assert.False(States.IsKnown(e.StateKey));
    }

    [Fact]
    public void AKnownStateIsMatchedIgnoringCaseAndSpaces()
    {
        var e = StatusParser.Parse("x.json", TestData.Json(state: " Blocked ")).Entry!;
        Assert.Equal("blocked", e.StateKey);
    }

    [Fact]
    public void ABomAndTrailingCommasAndCommentsAreTolerated()
    {
        var json = "﻿" + TestData.Json().Replace("\"ttlSec\"", "/* c */ \"ttlSec\"").TrimEnd('}') + ", }";
        Assert.True(StatusParser.Parse("x.json", json).Ok);
    }

    [Fact]
    public void ASummaryIsOneLineOfAtMostTwoHundredCharacters()
    {
        var long1 = new string('x', 500);
        var e = StatusParser.Parse("x.json", TestData.Json(summary: long1)).Entry!;
        Assert.Equal(200, e.Summary.Length);
        var multi = StatusParser.Parse("x.json", TestData.Json(summary: "line one\\nline two\\r\\n\\tline three")).Entry!;
        Assert.Equal("line one line two line three", multi.Summary);
    }

    [Fact]
    public void OptionalFieldsMayBeAbsentOrBlank()
    {
        var e = StatusParser.Parse("x.json", TestData.Json(@short: "  ", owner: "")).Entry!;
        Assert.Null(e.Short);
        Assert.Null(e.Owner);
        Assert.Null(e.Since);
        Assert.Null(e.DetailJson);
    }

    [Fact]
    public void ABadSinceIsAbsentNotAnError()
    {
        var e = StatusParser.Parse("x.json", TestData.Json(since: "soon")).Entry!;
        Assert.Null(e.Since);
    }

    [Fact]
    public void UpdatedWithAnOffsetIsConvertedToUtc()
    {
        var json = TestData.Json().Replace("2026-11-05T10:14:03Z", "2026-11-05T11:14:03+01:00");
        Assert.Equal(TestData.Now, StatusParser.Parse("x.json", json).Entry!.Updated);
    }

    [Fact]
    public void UpdatedWithoutAZoneIsTakenAsUtc()
    {
        var json = TestData.Json().Replace("2026-11-05T10:14:03Z", "2026-11-05T10:14:03");
        Assert.Equal(TestData.Now, StatusParser.Parse("x.json", json).Entry!.Updated);
    }

    [Fact]
    public void TheDetailOfAnotherSourceIsKeptAsText()
    {
        var e = StatusParser.Parse("retrace-1.json", TestData.Json(extra: ", \"detail\": { \"mode\": \"suite\", \"suite\": { \"index\": 3 } }")).Entry!;
        Assert.Null(e.Acad);
        Assert.Contains("\"mode\":\"suite\"", e.DetailJson);
    }

    [Fact]
    public void ADetailThatIsNotAnObjectIsIgnored()
    {
        var e = StatusParser.Parse("retrace-1.json", TestData.Json(extra: ", \"detail\": [1, 2]")).Entry!;
        Assert.Null(e.DetailJson);
        Assert.Null(e.Acad);
    }

    const string AcadDetailJson = @", ""detail"": { ""doc"": ""fx-all.dwg"", ""docs"": 2, ""cmdActive"": 1, ""cmdNames"": ""OFFSET"",
        ""prompt"": ""Specify point on side to offset or [Exit/Multiple/Undo] <Exit>:"",
        ""dialog"": { ""title"": ""AutoCAD Message"", ""class"": ""#32770"", ""modal"": true,
                    ""texts"": [""; error: bad argument type: numberp: nil""], ""buttons"": [""OK""], ""since"": ""2026-11-05T10:12:01Z"" } }";

    [Fact]
    public void TheAcadDetailIsParsed()
    {
        var a = StatusParser.Parse("acad-4812.json", TestData.Json("acad", extra: AcadDetailJson)).Entry!.Acad!;
        Assert.Equal("fx-all.dwg", a.Doc);
        Assert.Equal(2, a.Docs);
        Assert.Equal(1, a.CmdActive);
        Assert.Equal("OFFSET", a.CmdNames);
        Assert.Equal("Specify point on side to offset or [Exit/Multiple/Undo] <Exit>:", a.Prompt);
        var d = a.Dialog!;
        Assert.Equal("AutoCAD Message", d.Title);
        Assert.Equal("#32770", d.Class);
        Assert.True(d.Modal);
        Assert.Equal(new[] { "; error: bad argument type: numberp: nil" }, d.Texts);
        Assert.Equal(new[] { "OK" }, d.Buttons);
        Assert.Equal(new DateTime(2026, 11, 5, 10, 12, 1, DateTimeKind.Utc), d.Since);
    }

    [Fact]
    public void TheAcadDetailToleratesWrongTypesAndMissingParts()
    {
        var detail = ", \"detail\": { \"doc\": 5, \"docs\": \"two\", \"cmdActive\": null, \"prompt\": [], \"dialog\": { \"texts\": [\"a\", 3, null, \"b\"], \"buttons\": \"OK\", \"modal\": \"yes\" } }";
        var a = StatusParser.Parse("acad-1.json", TestData.Json("acad", extra: detail)).Entry!.Acad!;
        Assert.Null(a.Doc);
        Assert.Null(a.Docs);
        Assert.Null(a.CmdActive);
        Assert.Null(a.Prompt);
        Assert.NotNull(a.Dialog);
        Assert.Equal(new[] { "a", "b" }, a.Dialog!.Texts);
        Assert.Empty(a.Dialog.Buttons);
        Assert.False(a.Dialog.Modal);
        Assert.Equal("", a.Dialog.Title);
    }

    [Fact]
    public void WithoutADialogOrPromptTheDetailHasNone()
    {
        var a = StatusParser.Parse("acad-1.json", TestData.Json("acad", extra: ", \"detail\": { \"doc\": \"a.dwg\", \"docs\": 1, \"cmdActive\": 0, \"cmdNames\": \"\" }")).Entry!.Acad!;
        Assert.Null(a.Dialog);
        Assert.Null(a.Prompt);
        Assert.Null(a.CmdNames);
    }

    [Fact]
    public void CleanMakesOneLineAndCaps()
    {
        Assert.Equal("a b c", TextUtil.Clean("  a\n b\t\tc  ", 50));
        Assert.Equal("abc", TextUtil.Clean("abcdef", 3));
        Assert.Equal("", TextUtil.Clean(null, 3));
        Assert.Equal("", TextUtil.Clean("  \n ", 3));
    }
}
