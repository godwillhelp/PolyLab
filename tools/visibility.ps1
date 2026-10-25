param(
    [ValidateSet("pbog", "pfx")][string]$Id,
    [switch]$Hide,
    [switch]$Show,
    [string]$Repo = "godwillhelp/PolyLab"
)
# Hides a tool from every launcher, or shows it again. Hidden = every release of the tool is a draft: drafts are not in
# the anonymous release list the launcher reads, so the launcher shows no card for it (installed or not), older
# launchers cannot install it, and the GitHub releases page and the news drop it too. Needs gh logged in with write
# access to the repo. Without -Hide / -Show it returns each tool's state; the release workflow reads it to create a new
# release of a hidden tool as a draft.
$ErrorActionPreference = "Stop"
if ($Hide -and $Show) { throw "-Hide or -Show, not both" }
if (($Hide -or $Show) -and -not $Id) { throw "-Hide and -Show need -Id" }

function Get-Releases {
    $json = gh release list -R $Repo --limit 1000 --json tagName,isDraft
    if ($LASTEXITCODE -ne 0) { throw "gh release list failed" }
    @($json | ConvertFrom-Json)
}

$all = Get-Releases
if ($Hide -or $Show) {
    foreach ($r in $all | Where-Object { $_.tagName -like "$Id-v*" -and $_.isDraft -ne [bool]$Hide }) {
        if ($Hide) { gh release edit $r.tagName -R $Repo --draft=true | Out-Null }
        else { gh release edit $r.tagName -R $Repo --draft=false --latest=false | Out-Null }
        if ($LASTEXITCODE -ne 0) { throw "gh release edit $($r.tagName) failed" }
    }
    $all = Get-Releases
}

foreach ($t in "pbog", "pfx") {
    $mine = @($all | Where-Object { $_.tagName -like "$t-v*" })
    [pscustomobject]@{
        Id       = $t
        Hidden   = $mine.Count -gt 0 -and @($mine | Where-Object { -not $_.isDraft }).Count -eq 0
        Releases = $mine.Count
        Drafts   = @($mine | Where-Object { $_.isDraft }).Count
    }
}
