<#
Usage: Run this script where `git` is available to stage and commit the current workspace.

From the repo root (where this script lives):
  powershell -ExecutionPolicy Bypass -File .\\scripts\\commit_changes.ps1

What it does:
- Reads `commit_message.txt` (repo root)
- If `git` is not on PATH, prints the exact `git` commands for you to run manually
- If `git` is available, runs `git add -A` and `git commit -F commit_message.txt`
#>

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path | Split-Path -Parent
$commitMsgFile = Join-Path $repoRoot "commit_message.txt"

if (-not (Test-Path $commitMsgFile)) {
    Write-Error "Commit message file not found: $commitMsgFile"
    exit 1
}

$git = Get-Command git -ErrorAction SilentlyContinue
if (-not $git) {
    Write-Output "git not found in PATH. The commit message has been saved to: $commitMsgFile"
    Write-Output "Run these commands where git is available (copy-paste):"
    Write-Output ""
    Write-Output "git add -A"
    Write-Output "git commit -F \"$commitMsgFile\""
    Write-Output "git status --porcelain"
    exit 0
}

Write-Output "Staging all changes..."
& git add -A

Write-Output "Committing with message from: $commitMsgFile"
& git commit -F "$commitMsgFile"

if ($LASTEXITCODE -eq 0) {
    Write-Output "Commit successful. Latest commit:"
    & git --no-pager log -1 --pretty=format:"%h %ad %s" --date=short
} else {
    Write-Error "git commit failed with exit code $LASTEXITCODE"
    & git status --porcelain
}
