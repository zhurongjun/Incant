using Incant.Core.Cpp.Arguments;

namespace Incant.AutoTest.CppToolchain;

internal static class BuildCommandPreparer
{
    internal static async Task<IReadOnlyList<string>> PrepareAsync(
        BuildAction action, string logDirectory, CancellationToken cancellationToken)
    {
        foreach (string output in action.RecreatedArtifacts)
        {
            if (!action.ExpectedArtifacts.Contains(output, PathIdentity.Comparer)
                || !PathIdentity.Contains(action.WorkingDirectory, output)
                || PathIdentity.AreEqual(action.WorkingDirectory, output))
            {
                throw new InvalidOperationException($"Recreated artifact '{output}' is outside this action's declared outputs.");
            }

            File.Delete(output);
        }

        if (action.ResponseDialect is not ResponseFileDialect dialect
            || action.Arguments.Sum(value => (long)value.Length + 3) < 24000)
        {
            return action.Arguments;
        }

        string responsePath = Path.Combine(logDirectory, action.Id + ".rsp");
        byte[] bytes = ResponseFileEncoder.Encode(action.Arguments.Skip(action.ResponseArgumentOffset), dialect);
        await File.WriteAllBytesAsync(responsePath, bytes, cancellationToken).ConfigureAwait(false);
        return action.Arguments.Take(action.ResponseArgumentOffset).Append("@" + responsePath).ToArray();
    }
}
