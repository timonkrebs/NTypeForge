using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace NTypeForge.Generator.Tests;

// A minimal hand-rolled snapshot assertion - no external Verify dependency, in the same
// self-contained spirit as GeneratorTestHarness. A baseline lives next to its test under
// Snapshots/<TestMethod>.verified.txt and is compared with line endings normalized (so the same
// baseline holds whether the generator ran on Windows or Linux).
//
// Creating or updating a baseline:
//   * With a .NET SDK: run the tests with NTF_UPDATE_SNAPSHOTS=1 to (re)write the baselines from the
//     current generator output, review the diff, and commit.
//   * Without an SDK: a missing baseline fails the test and emits the expected content base64-encoded
//     between <<<SNAPSHOT:name:BEGIN>>> / <<<SNAPSHOT:name:END>>> markers, so it can be recovered
//     from the test log and committed verbatim.
internal static class Snapshot
{
    private static bool UpdateRequested =>
        Environment.GetEnvironmentVariable("NTF_UPDATE_SNAPSHOTS") is "1" or "true";

    public static void Match(
        string actual,
        [CallerFilePath] string testFilePath = "",
        [CallerMemberName] string name = "")
    {
        var normalized = Normalize(actual);
        var baseline = Path.Combine(Path.GetDirectoryName(testFilePath)!, "Snapshots", name + ".verified.txt");

        if (UpdateRequested)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
            File.WriteAllText(baseline, normalized);
        }

        if (!File.Exists(baseline))
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(normalized));
            Assert.Fail(
                $"Missing snapshot baseline for '{name}'. Decode the payload below into " +
                $"Snapshots/{name}.verified.txt and commit it (or re-run with NTF_UPDATE_SNAPSHOTS=1).\n" +
                $"<<<SNAPSHOT:{name}:BEGIN>>>{b64}<<<SNAPSHOT:{name}:END>>>");
        }

        Assert.Equal(Normalize(File.ReadAllText(baseline)), normalized);
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n");
}
