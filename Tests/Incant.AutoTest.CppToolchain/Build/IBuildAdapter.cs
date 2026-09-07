namespace Incant.AutoTest.CppToolchain;

internal interface IBuildAdapter
{
    BuildPlan CreatePlan(AutoTestContext context, ResolvedToolchain toolchain);
}
