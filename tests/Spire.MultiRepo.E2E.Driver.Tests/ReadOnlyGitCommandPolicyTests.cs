using Spire.MultiRepo.E2E.Driver;
using Xunit;

namespace Spire.MultiRepo.E2E.Driver.Tests;

public sealed class ReadOnlyGitCommandPolicyTests
{
    public static TheoryData<string[]> AllowedCommands
    {
        get
        {
            var data = new TheoryData<string[]>();
            data.Add(["branch", "--show-current"]);
            data.Add(["-C", "/repository", "rev-parse", "--show-toplevel"]);
            data.Add(["status", "--porcelain", "--untracked-files=normal"]);
            data.Add(["-C", "/repository", "status", "--porcelain=v1", "--untracked-files=all"]);
            data.Add(["diff", "--cached", "--name-only", "-z", "--no-ext-diff", "HEAD", "--"]);
            data.Add(["ls-files", "--others", "--exclude-standard", "-z", "--"]);
            data.Add(["rev-parse", "0123456789abcdef^{commit}"]);
            return data;
        }
    }

    public static TheoryData<string[]> DeniedCommands
    {
        get
        {
            var data = new TheoryData<string[]>();
            data.Add(["clean", "-fdx"]);
            data.Add(["restore", "."]);
            data.Add(["stash", "push"]);
            data.Add(["update-ref", "refs/heads/main", "HEAD"]);
            data.Add(["config", "user.name", "attacker"]);
            data.Add(["remote", "set-url", "origin", "https://example.test/repository.git"]);
            data.Add(["rev-parse", "--exec-path"]);
            data.Add(["unknown-command"]);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllowedCommands))]
    public void Allows_only_documented_inspection_shapes(string[] arguments)
    {
        Assert.True(ReadOnlyGitCommandPolicy.IsAllowed(arguments));
    }

    [Theory]
    [MemberData(nameof(DeniedCommands))]
    public void Denies_mutations_and_unknown_shapes(string[] arguments)
    {
        Assert.False(ReadOnlyGitCommandPolicy.IsAllowed(arguments));
    }

    [Theory]
    [InlineData("clean")]
    [InlineData("restore")]
    [InlineData("stash")]
    [InlineData("update-ref")]
    public void Classifies_previously_missed_mutations(string operation)
    {
        Assert.True(ReadOnlyGitCommandPolicy.IsNetworkOrMutation(operation));
        Assert.Equal(operation, ReadOnlyGitCommandPolicy.FindOperation([operation, "argument"]));
    }
}
