using System;
using System.Text.Json;
using InstaDesktop.Services;

namespace InstaDesktop.Diagnostics;

// Offline checks of the release parser and version comparison. The JSON mirrors
// the real GitHub "latest release" response for this repository.
internal static class UpdateTests
{
    private const string Hash = "1eba815e787717cc4b36944d43346edfe4060b849c5082fcc2aa189bbb79c407";

    private static string Release(string tag, string name = AutoUpdateService.InstallerName,
        string url = "https://github.com/kurtxtb/InstaDesktop/releases/download/{0}/InstaDesktop-Setup.exe",
        string? digest = "sha256:" + Hash, bool draft = false, bool prerelease = false) => JsonSerializer.Serialize(new
    {
        url = "https://api.github.com/repos/kurtxtb/InstaDesktop/releases/1", tag_name = tag, name = tag, draft, prerelease,
        assets = new object[]
        {
            new { name = "SHA256SUMS.txt", browser_download_url = "https://github.com/kurtxtb/InstaDesktop/releases/download/" + tag + "/SHA256SUMS.txt", digest = "sha256:" + new string('0', 64) },
            new { name, browser_download_url = string.Format(url, tag), digest, content_type = "application/x-msdownload" }
        }
    });

    internal static void Run(Action<bool, string> check)
    {
        var info = AutoUpdateService.Parse(Release("v1.1.0"), requireGitHubHost: true);
        check(info is not null && info.Version == new Version(1, 1, 0) && info.Sha256 == Hash &&
            info.InstallerUri.AbsoluteUri == "https://github.com/kurtxtb/InstaDesktop/releases/download/v1.1.0/InstaDesktop-Setup.exe",
            "update: GitHub release tag, installer asset and digest are read");
        check(AutoUpdateService.IsNewer(new Version(1, 1, 0), new Version(1, 0, 0, 0)), "update: v1.1.0 is newer than file version 1.0.0.0");
        check(!AutoUpdateService.IsNewer(new Version(1, 1, 0), new Version(1, 1, 0, 0)), "update: same version with revision is not newer");
        check(!AutoUpdateService.IsNewer(new Version(1, 0, 0), new Version(1, 1, 0, 0)), "update: older release never downgrades");
        check(AutoUpdateService.IsNewer(new Version(1, 10, 0), new Version(1, 9, 3, 0)), "update: numeric not lexical comparison");
        check(AutoUpdateService.Parse(Release("v1.1.0", digest: null), true) is null, "update: installer without SHA-256 is refused");
        check(AutoUpdateService.Parse(Release("v1.1.0", name: "Other.exe"), true) is null, "update: release without installer asset is ignored");
        check(AutoUpdateService.Parse(Release("v1.1.0", url: "https://evil.test/{0}/InstaDesktop-Setup.exe"), true) is null,
            "update: GitHub release asset must be hosted by this repository");
        check(AutoUpdateService.Parse(Release("v1.1.0", url: "http://github.com/kurtxtb/InstaDesktop/releases/download/{0}/InstaDesktop-Setup.exe"), true) is null,
            "update: plain HTTP installer is refused");
        check(AutoUpdateService.Parse(Release("v1.1.0", draft: true), true) is null && AutoUpdateService.Parse(Release("v1.1.0", prerelease: true), true) is null,
            "update: drafts and prereleases are ignored");
        check(AutoUpdateService.Parse(Release("latest"), true) is null && AutoUpdateService.Parse("{\"message\":\"API rate limit exceeded\"}", true) is null &&
            AutoUpdateService.Parse("not json", true) is null, "update: unusable responses are ignored");
        var simple = AutoUpdateService.Parse(JsonSerializer.Serialize(new { version = "2.0.1", installerUrl = "https://example.test/setup.exe", sha256 = Hash }), false);
        check(simple is not null && simple.Version == new Version(2, 0, 1), "update: custom manifest still supported");
    }
}
