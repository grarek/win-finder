# Prints the version for the next release: one more than the highest vX.Y.Z tag
# (v1.0.8 -> 1.0.9), or 1.0.1 if there are no release tags yet. Used by the
# "Build and release" workflow so release numbers go up by one with no gaps.
#
#   -ReuseExisting: if the commit being built already carries a vX.Y.Z tag (a re-run of a
#                   release that already happened), print that version instead of a new one.
param(
    [string]$Ref = "HEAD",
    [switch]$ReuseExisting
)
$ErrorActionPreference = "Stop"

function Get-ReleaseVersions([string[]]$tags) {
    foreach ($tag in $tags) {
        if ($tag -match '^v(\d+)\.(\d+)\.(\d+)$') {
            [version]::new([int]$Matches[1], [int]$Matches[2], [int]$Matches[3])
        }
    }
}

if ($ReuseExisting) {
    $onThisCommit = @(Get-ReleaseVersions (git tag --points-at $Ref --list "v*")) | Sort-Object -Descending
    if ($onThisCommit.Count -gt 0) {
        $v = $onThisCommit[0]
        return "{0}.{1}.{2}" -f $v.Major, $v.Minor, $v.Build
    }
}

$all = @(Get-ReleaseVersions (git tag --list "v*")) | Sort-Object -Descending
if ($all.Count -eq 0) { return "1.0.1" }   # 1.0.0 is what local source builds use
$latest = $all[0]
"{0}.{1}.{2}" -f $latest.Major, $latest.Minor, ($latest.Build + 1)
