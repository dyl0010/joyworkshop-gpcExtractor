using System.Collections.Generic;

namespace GpcSessionBrowser;

public sealed record GpcLaunchOptions(
    string WorkingDirectory,
    bool Yolo,
    bool SkipTrust,
    bool Sandbox,
    bool Debug,
    string? Model,
    string? WorktreeName,
    string? IncludeDirectories)
{
    public IReadOnlyList<string> BuildArguments()
    {
        var arguments = new List<string>();

        if (Yolo)
        {
            arguments.Add("--skip-trust");
            arguments.Add("--approval-mode");
            arguments.Add("yolo");
        }
        else if (SkipTrust)
        {
            arguments.Add("--skip-trust");
        }

        if (Sandbox)
        {
            arguments.Add("--sandbox");
        }

        if (Debug)
        {
            arguments.Add("--debug");
        }

        AddOption(arguments, "--model", Model);
        AddOption(arguments, "--worktree", WorktreeName);
        AddOption(arguments, "--include-directories", IncludeDirectories);

        return arguments;
    }

    private static void AddOption(List<string> arguments, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        arguments.Add(name);
        arguments.Add(value.Trim());
    }
}
