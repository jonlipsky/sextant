using System.Text.Json.Nodes;

namespace Sextant.ProcessStack.Activities.Tests;

[TestClass]
public sealed class ActivityValuesTests
{
    [TestMethod]
    public void A_parameter_wins_over_the_property_only_when_it_is_not_null()
    {
        var activity = new SextantNormalizeRepositoryActivity();
        activity.Definition.Parameters["cloneUrl"] = null!;

        Assert.AreEqual("property", ActivityValues.Input(activity, "cloneUrl", "property"));
        activity.Definition.Parameters["cloneUrl"] = "parameter";
        Assert.AreEqual("parameter", ActivityValues.Input(activity, "cloneUrl", "property"));
        Assert.AreEqual("property", ActivityValues.Input(activity, "other", "property"));
    }

    [TestMethod]
    public void Json_wrappers_unwrap_to_clr_values()
    {
        var element = ActivityHarness.Json("""{"a":1,"b":[true,"x",1.5],"c":null}""");
        var map = ActivityValues.AsMap(element)!;

        Assert.AreEqual(1L, map["A"]);
        CollectionAssert.AreEqual(new object?[] { true, "x", 1.5 }, ActivityValues.AsList(map["b"])!.ToArray());
        Assert.IsNull(map["c"]);
        Assert.AreEqual("v", ActivityValues.AsString(JsonNode.Parse("\"v\"")));
    }

    [TestMethod]
    public void Json_integers_stay_exact()
    {
        var map = ActivityValues.AsMap("""{"big":9007199254740993}""")!;

        Assert.AreEqual(9007199254740993L, map["big"]);
        Assert.AreEqual(9007199254740993L, ActivityValues.AsLong(map["big"]));
    }

    [TestMethod]
    [DataRow(true, true)]
    [DataRow("TRUE", true)]
    [DataRow(" false ", false)]
    [DataRow("1", true)]
    [DataRow("0", false)]
    [DataRow(2, true)]
    [DataRow(0L, false)]
    [DataRow("", null)]
    [DataRow("yes", null)]
    [DataRow(1.5, null)]
    public void AsBool_is_lenient_but_never_guesses(object value, bool? expected)
    {
        Assert.AreEqual(expected, ActivityValues.AsBool(value));
    }

    [TestMethod]
    [DataRow(7, 7L)]
    [DataRow((byte)7, 7L)]
    [DataRow(7.0, 7L)]
    [DataRow(7.5, null)]
    [DataRow(double.NaN, null)]
    [DataRow(" -3 ", -3L)]
    [DataRow("3.0", null)]
    [DataRow(ulong.MaxValue, null)]
    [DataRow(true, null)]
    public void AsLong_accepts_whole_numbers_only(object value, long? expected)
    {
        Assert.AreEqual(expected, ActivityValues.AsLong(value));
    }

    [TestMethod]
    public void AsLong_accepts_json_numbers_and_decimals()
    {
        Assert.AreEqual(42L, ActivityValues.AsLong(ActivityHarness.Json("42")));
        Assert.AreEqual(42L, ActivityValues.AsLong(42m));
        Assert.IsNull(ActivityValues.AsLong(42.5m));
        Assert.AreEqual(3L, ActivityValues.AsLong(3f));
    }

    [TestMethod]
    public void Lists_and_maps_are_never_confused()
    {
        Assert.IsNull(ActivityValues.AsList(ActivityHarness.Map(("a", 1))));
        Assert.IsNull(ActivityValues.AsList(new[] { new KeyValuePair<string, object?>("a", 1) }.ToDictionary()));
        Assert.IsNull(ActivityValues.AsMap(new List<object?> { 1 }));
        Assert.IsNull(ActivityValues.AsList("{}"));
        Assert.IsNull(ActivityValues.AsMap("[]"));
        Assert.IsNull(ActivityValues.AsMap("{not json"));
        Assert.IsNull(ActivityValues.AsList(42));
        Assert.HasCount(2, ActivityValues.AsList("[1, 2]")!);
        Assert.HasCount(1, ActivityValues.AsMap(new Dictionary<string, int> { ["k"] = 1 })!);
    }

    [TestMethod]
    public void Map_keys_are_case_insensitive_and_the_first_spelling_wins()
    {
        var map = ActivityValues.AsMap("""{"Key":1,"key":2}""")!;

        Assert.HasCount(1, map);
        Assert.AreEqual(1L, ActivityValues.Get(map, "KEY"));
        Assert.AreEqual(1L, ActivityValues.Get(map, "missing", "key"));
        Assert.IsNull(ActivityValues.Get(null, "key"));
    }

    [TestMethod]
    public void Text_trims_and_stringifies_invariantly()
    {
        Assert.AreEqual("x", ActivityValues.Text("  x "));
        Assert.AreEqual(string.Empty, ActivityValues.Text(null));
        Assert.AreEqual("1.5", ActivityValues.Text(1.5));
        Assert.AreEqual("true", ActivityValues.Text(true));
    }
}
