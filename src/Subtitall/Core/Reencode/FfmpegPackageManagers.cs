namespace Subtitall.Core.Reencode;

/// <summary>
/// A package manager that can install ffmpeg: what to run, and the commands that must exist for
/// it to be an option. <see cref="Elevated"/> ones need administrator rights — the system asks
/// the user to confirm.
/// </summary>
public sealed record PackageInstaller(
    string Name, string Label, IReadOnlyList<string> Requires, IReadOnlyList<string> Arguments, bool Elevated = false)
{
    /// <summary>The program to start (the first required command).</summary>
    public string Command => Requires[0];
}

/// <summary>
/// The other ways of getting ffmpeg when the app's own download does not work — typically on a
/// company computer whose security policy refuses to start a freshly downloaded program, while
/// one installed by the system's package manager is accepted.
/// </summary>
public static class FfmpegPackageManagers
{
    /// <summary>The package managers worth trying on an operating system, in order of preference.</summary>
    public static IReadOnlyList<PackageInstaller> For(string os) => os switch
    {
        "windows" =>
        [
            new("winget", "winget (Windows' package manager)", ["winget"],
                ["install", "--id", "Gyan.FFmpeg", "--exact", "--source", "winget",
                 "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity"]),
            new("scoop", "Scoop", ["scoop"], ["install", "ffmpeg"]),
            new("choco", "Chocolatey", ["choco"], ["install", "ffmpeg", "-y"], Elevated: true),
        ],
        "mac" => [new("brew", "Homebrew", ["brew"], ["install", "ffmpeg"])],
        // pkexec shows the desktop's own password prompt.
        "linux" =>
        [
            new("apt", "apt", ["pkexec", "apt-get"], ["apt-get", "install", "-y", "ffmpeg"]),
            new("dnf", "dnf", ["pkexec", "dnf"], ["dnf", "install", "-y", "ffmpeg", "--allowerasing"]),
            new("pacman", "pacman", ["pkexec", "pacman"], ["pacman", "-S", "--noconfirm", "ffmpeg"]),
            new("zypper", "zypper", ["pkexec", "zypper"], ["zypper", "--non-interactive", "install", "ffmpeg"]),
        ],
        _ => [],
    };

    /// <summary>
    /// The installers of <see cref="For"/> whose commands exist in the given folders, each with
    /// the full path of the program to start.
    /// </summary>
    public static IReadOnlyList<(PackageInstaller Installer, string CommandPath)> Available(
        string os, IReadOnlyList<string> directories)
    {
        var found = new List<(PackageInstaller, string)>();
        foreach (var installer in For(os))
        {
            var paths = installer.Requires.Select(c => FindCommand(c, directories, os == "windows")).ToList();
            if (paths.All(p => p is not null))
                found.Add((installer, paths[0]!));
        }
        return found;
    }

    /// <summary>Full path of a command, as a shell would find it; null when it is not installed.</summary>
    public static string? FindCommand(string command, IEnumerable<string> directories, bool windows)
    {
        string[] names = windows ? [command + ".exe", command + ".cmd", command + ".bat"] : [command];
        foreach (var directory in directories)
        {
            foreach (var name in names)
            {
                try
                {
                    var path = Path.Combine(directory, name);
                    if (File.Exists(path))
                        return path;
                }
                catch (ArgumentException)
                {
                    // a PATH entry with characters no path can have
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Where package managers put ffmpeg. Looked at on top of the PATH: an install made while the
    /// app runs is not on the app's PATH yet, and an app started from a desktop icon does not
    /// always get the PATH a terminal has.
    /// </summary>
    public static IReadOnlyList<string> KnownFolders(string os, string home, string localAppData, string programData)
    {
        var folders = new List<string>();
        if (os == "windows")
        {
            var winget = Path.Combine(localAppData, "Microsoft", "WinGet");
            folders.Add(Path.Combine(winget, "Links"));
            // winget unpacks portable packages there: Packages\Gyan.FFmpeg_…\ffmpeg-7.1-full_build\bin
            try
            {
                var packages = Path.Combine(winget, "Packages");
                if (Directory.Exists(packages))
                    foreach (var package in Directory.EnumerateDirectories(packages, "Gyan.FFmpeg*"))
                        foreach (var build in Directory.EnumerateDirectories(package))
                            folders.Add(Path.Combine(build, "bin"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // unreadable — the Links folder and the PATH remain
            }
            folders.Add(Path.Combine(home, "scoop", "apps", "ffmpeg", "current", "bin"));
            folders.Add(Path.Combine(home, "scoop", "shims"));
            folders.Add(Path.Combine(programData, "chocolatey", "bin"));
        }
        else
        {
            if (os == "mac")
                folders.Add("/opt/homebrew/bin");
            folders.AddRange(["/usr/local/bin", "/usr/bin", Path.Combine(home, ".local", "bin")]);
        }
        return folders;
    }
}
