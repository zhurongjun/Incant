namespace Incant.AutoTest.CXToolchain;

internal interface IBuildAdapter
{
    BuildPlan CreatePlan(AutoTestContext context, ResolvedToolchain toolchain);
}
