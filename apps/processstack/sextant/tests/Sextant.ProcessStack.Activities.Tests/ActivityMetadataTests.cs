using System.Reflection;
using ProcessStack.Abstractions;

namespace Sextant.ProcessStack.Activities.Tests;

/// <summary>The registration metadata the host reads: names, descriptions, inputs and outputs.</summary>
[TestClass]
public sealed class ActivityMetadataTests
{
    private static readonly Type[] ActivityTypes =
    [
        typeof(SextantNormalizeRepositoryActivity),
        typeof(SextantPlanRepositoryChangeActivity),
        typeof(SextantInterpretEnsureResultActivity),
        typeof(SextantPlanReconcileActivity),
        typeof(SextantParseWatchCommandActivity),
        typeof(SextantPlanLegacyImportActivity),
    ];

    // The Sextant activities that ship built into the platform today (v1). A bundled activity may not
    // shadow a built-in, so none of these may be reused.
    private static readonly string[] BuiltInSextantNames =
    [
        "SextantResolveBranch", "SextantListWatchedRepos", "SextantGetJobStatus", "SextantUnwatchRepo",
        "SextantResolveGitHead", "SextantWatchRepo", "SextantEnsureSnapshot", "SextantEvaluateBranchAdvance",
        "SextantRecordEnrollment", "SextantReconcileEnrolledRepos", "SextantReconcileWatchedRepos",
        "SextantCommitBranchHead",
    ];

    [TestMethod]
    public void Names_are_the_documented_ones_and_unique()
    {
        var names = ActivityTypes.Select(t => t.GetCustomAttribute<ActivityNameAttribute>()!.Name).ToList();

        CollectionAssert.AreEqual(
            new[]
            {
                "SextantNormalizeRepository", "SextantPlanRepositoryChange", "SextantInterpretEnsureResult",
                "SextantPlanReconcile", "SextantParseWatchCommand", "SextantPlanLegacyImport",
            },
            names);
        Assert.HasCount(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase));
        foreach (var name in names)
            CollectionAssert.DoesNotContain(BuiltInSextantNames, name);
    }

    [TestMethod]
    public void Each_activity_has_a_parameterless_constructor_that_sets_its_name_and_a_description()
    {
        foreach (var type in ActivityTypes)
        {
            Assert.IsTrue(type.IsSealed && type.IsPublic, type.Name);
            var activity = (AbstractActivity)Activator.CreateInstance(type)!;
            Assert.AreEqual(type.GetCustomAttribute<ActivityNameAttribute>()!.Name, activity.Name, type.Name);
            Assert.IsFalse(string.IsNullOrWhiteSpace(type.GetCustomAttribute<ActivityDescriptionAttribute>()?.Description), type.Name);
        }
    }

    [TestMethod]
    public void Inputs_and_outputs_are_unique_described_and_never_share_a_name()
    {
        foreach (var type in ActivityTypes)
        {
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var inputs = properties.Select(p => p.GetCustomAttribute<ActivityInputAttribute>()).OfType<ActivityInputAttribute>().ToList();
            var outputs = properties.Select(p => p.GetCustomAttribute<ActivityOutputAttribute>()).OfType<ActivityOutputAttribute>().ToList();

            Assert.IsNotEmpty(inputs, type.Name);
            Assert.IsNotEmpty(outputs, type.Name);
            Assert.HasCount(inputs.Count, inputs.Select(i => i.Name).Distinct(StringComparer.Ordinal), type.Name);
            Assert.HasCount(outputs.Count, outputs.Select(o => o.Name).Distinct(StringComparer.Ordinal), type.Name);
            var shared = inputs.Select(i => i.Name).Intersect(outputs.Select(o => o.Name), StringComparer.Ordinal).ToList();
            Assert.IsEmpty(shared, $"{type.Name}: {string.Join(", ", shared)}");
            foreach (var attribute in inputs.Select(i => (i.Name, i.Description)).Concat(outputs.Select(o => (o.Name, o.Description))))
                Assert.IsFalse(string.IsNullOrWhiteSpace(attribute.Description), $"{type.Name}.{attribute.Name}");
        }
    }

    [TestMethod]
    public void Input_properties_are_nullable_reference_types_so_the_host_never_converts_into_a_primitive()
    {
        // The host converts a bound value into the property's type; a lenient input ("", "true", 7, a JSON
        // element) must never fail that conversion, so every input is `string?` or `object?` and the activity
        // interprets it.
        foreach (var type in ActivityTypes)
        {
            foreach (var property in type.GetProperties().Where(p => p.GetCustomAttribute<ActivityInputAttribute>() is not null))
            {
                Assert.IsTrue(property.PropertyType == typeof(string) || property.PropertyType == typeof(object),
                    $"{type.Name}.{property.Name} is {property.PropertyType.Name}");
                Assert.IsTrue(property.CanRead && property.CanWrite, $"{type.Name}.{property.Name}");
            }
        }
    }

    [TestMethod]
    public void The_activities_assembly_references_no_ProcessStack_assembly_but_the_SDK()
    {
        var references = typeof(SextantNormalizeRepositoryActivity).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToList();

        CollectionAssert.Contains(references, "ProcessStack.Abstractions");
        CollectionAssert.Contains(references, "Sextant.Core");
        Assert.IsEmpty(references.Where(r => r.StartsWith("ProcessStack.", StringComparison.Ordinal) && r != "ProcessStack.Abstractions"));
        Assert.IsEmpty(references.Where(r => r.StartsWith("Sextant.", StringComparison.Ordinal) && r != "Sextant.Core"));
    }
}
