$token = $env:GITHUB_TOKEN
if ([string]::IsNullOrWhiteSpace($token)) {
    throw "Set GITHUB_TOKEN before running this script."
}
$owner = "wertyy111"
$repo = "Updates-Vesper"
$branch = "main"

$files = @(
    "MinecraftCheatLauncher/PhotinoHost/LauncherFallbackBackendHost.cs",
    "MinecraftCheatLauncher/UserInterface/src/styles.css",
    "MinecraftCheatLauncher/Program.cs",
    "MinecraftCheatLauncher/VesperLauncher.csproj",
    ".github/workflows/cross-platform-release.yml"
)

$headers = @{
    "Authorization" = "token $token"
    "Accept" = "application/vnd.github.v3+json"
}

# Get latest commit
$refUrl = "https://api.github.com/repos/$owner/$repo/git/refs/heads/$branch"
$ref = Invoke-RestMethod -Uri $refUrl -Headers $headers
$commitSha = $ref.object.sha

# Get base tree
$commitUrl = "https://api.github.com/repos/$owner/$repo/git/commits/$commitSha"
$commit = Invoke-RestMethod -Uri $commitUrl -Headers $headers
$baseTreeSha = $commit.tree.sha

# Create tree with new files
$treeUrl = "https://api.github.com/repos/$owner/$repo/git/trees"
$treeBody = @{
    base_tree = $baseTreeSha
    tree = @()
}

foreach ($file in $files) {
    if (Test-Path "../$file") {
        $content = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes((Resolve-Path "../$file")))
        
        $blobUrl = "https://api.github.com/repos/$owner/$repo/git/blobs"
        $blobBody = @{
            content = $content
            encoding = "base64"
        }
        $blob = Invoke-RestMethod -Method Post -Uri $blobUrl -Headers $headers -Body ($blobBody | ConvertTo-Json)
        
        $treeItem = @{
            path = $file
            mode = "100644"
            type = "blob"
            sha = $blob.sha
        }
        $treeBody.tree += $treeItem
        Write-Host "Prepared $file"
    } else {
        Write-Host "File not found: ../$file"
    }
}

# Create the tree
$newTree = Invoke-RestMethod -Method Post -Uri $treeUrl -Headers $headers -Body ($treeBody | ConvertTo-Json -Depth 10)

# Create the commit
$newCommitUrl = "https://api.github.com/repos/$owner/$repo/git/commits"
$newCommitBody = @{
    message = "Fix macOS login logic, slider UI, old Linux AppImage, Windows Setup 404, and add WPF frosted glass buttons"
    tree = $newTree.sha
    parents = @($commitSha)
}
$newCommit = Invoke-RestMethod -Method Post -Uri $newCommitUrl -Headers $headers -Body ($newCommitBody | ConvertTo-Json)

# Update the ref
$updateRefUrl = "https://api.github.com/repos/$owner/$repo/git/refs/heads/$branch"
$updateRefBody = @{
    sha = $newCommit.sha
    force = $false
}
Invoke-RestMethod -Method Patch -Uri $updateRefUrl -Headers $headers -Body ($updateRefBody | ConvertTo-Json)

Write-Host "Successfully pushed fixes to GitHub!"
