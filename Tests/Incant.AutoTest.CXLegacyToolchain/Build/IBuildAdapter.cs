namespace Incant.AutoTest.CXLegacyToolchain;

internal interface IBuildAdapter
{
    BuildPlan CreatePlan(AutoTestContext context, ResolvedToolchain toolchain);
}
