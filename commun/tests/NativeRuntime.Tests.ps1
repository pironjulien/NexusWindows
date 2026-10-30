BeforeAll {
    . (Join-Path (Split-Path -Parent $PSScriptRoot) 'native-runtime.ps1')
    function New-RuntimeFixture([string]$Hash) {
        $directory = Join-Path $nexusWindowsRuntimeHome "native\$Hash"
        [void][IO.Directory]::CreateDirectory($directory)
        $artifacts = [ordered]@{}
        foreach ($name in 'NexusWindows.exe', 'NexusWindows.dll', 'NexusWindows.deps.json', 'NexusWindows.runtimeconfig.json', 'garde-fous.json') {
            $file = Join-Path $directory $name
            [IO.File]::WriteAllText($file, "fixture $Hash $name")
            $artifacts[$name] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        }
        $manifest = [pscustomobject]@{ Schema=2; SourceRoot='fixture'; NativeHash=$Hash; NativeExecutable=(Join-Path $directory 'NexusWindows.exe'); Version='3.8.0'; NativeArtifacts=[pscustomobject]$artifacts }
        Write-NexusWindowsJson (Join-Path $directory 'runtime.json') $manifest
        return $manifest
    }
}

Describe 'Common native runtime immutable version acceptance, without any build' {
    BeforeEach {
        $nexusWindowsRuntimeHome = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $nexusWindowsManifest = Join-Path $nexusWindowsRuntimeHome 'installation.json'
        $script:fixtureHash = 'a' * 64
        Mock Get-NexusWindowsNativeHash { 'a' * 64 }
        Mock dotnet { throw 'A fixture test must never build or execute dotnet.' }
    }
    AfterEach { Should -Invoke dotnet -Times 0 -Exactly }

    It 'accepts the matching version only after validating every real fixture artifact' {
        $runtime = New-RuntimeFixture $script:fixtureHash
        Write-NexusWindowsJson $nexusWindowsManifest $runtime
        $before = [Convert]::ToBase64String([IO.File]::ReadAllBytes($nexusWindowsManifest))
        (Install-NexusWindowsNativeRuntime -CheckOnly).NativeHash | Should -BeExactly $script:fixtureHash
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($nexusWindowsManifest)) | Should -BeExactly $before
    }

    It 'rejects a valid other version copied into the current target in CheckOnly' {
        $current = New-RuntimeFixture $script:fixtureHash
        $other = New-RuntimeFixture ('b' * 64)
        Test-NexusWindowsRuntime $other | Should -BeTrue
        Write-NexusWindowsJson (Join-Path (Split-Path -Parent $current.NativeExecutable) 'runtime.json') $other
        Write-NexusWindowsJson $nexusWindowsManifest $current
        { Install-NexusWindowsNativeRuntime -CheckOnly } | Should -Throw '*différent des sources*'
        (Get-NexusWindowsNativeRuntime).NativeHash | Should -BeExactly $script:fixtureHash
    }

    It 'never publishes a foreign manifest or rebuilds an existing immutable current target' {
        $current = New-RuntimeFixture $script:fixtureHash
        $other = New-RuntimeFixture ('b' * 64)
        Write-NexusWindowsJson (Join-Path (Split-Path -Parent $current.NativeExecutable) 'runtime.json') $other
        Write-NexusWindowsJson $nexusWindowsManifest $current
        $before = [Convert]::ToBase64String([IO.File]::ReadAllBytes($nexusWindowsManifest))
        { Install-NexusWindowsNativeRuntime } | Should -Throw '*immutable incomplet ou altéré*'
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($nexusWindowsManifest)) | Should -BeExactly $before
    }

    It 'rejects a changed artifact despite a matching source version' {
        $runtime = New-RuntimeFixture $script:fixtureHash
        [IO.File]::AppendAllText($runtime.NativeExecutable, 'changed')
        Test-NexusWindowsRuntime $runtime | Should -BeFalse
        { Install-NexusWindowsNativeRuntime -CheckOnly } | Should -Throw '*différent des sources*'
    }

    It 'rejects a missing required artifact' {
        $runtime = New-RuntimeFixture $script:fixtureHash
        [IO.File]::Delete((Join-Path (Split-Path -Parent $runtime.NativeExecutable) 'NexusWindows.dll'))
        Test-NexusWindowsRuntime $runtime | Should -BeFalse
    }

    It 'rejects a manifest executable that belongs to another version directory' {
        $runtime = New-RuntimeFixture $script:fixtureHash
        $other = New-RuntimeFixture ('b' * 64)
        $runtime.NativeExecutable = $other.NativeExecutable
        Test-NexusWindowsRuntime $runtime | Should -BeFalse
    }

    It 'rejects malformed version JSON before any build or installation write' {
        $runtime = New-RuntimeFixture $script:fixtureHash
        [IO.File]::WriteAllText((Join-Path (Split-Path -Parent $runtime.NativeExecutable) 'runtime.json'), '{invalid')
        { Install-NexusWindowsNativeRuntime -CheckOnly } | Should -Throw
        Test-Path -LiteralPath $nexusWindowsManifest | Should -BeFalse
    }

    It 'also requires the installed manifest to match the current qualified version' {
        $null = New-RuntimeFixture $script:fixtureHash
        $other = New-RuntimeFixture ('b' * 64)
        Write-NexusWindowsJson $nexusWindowsManifest $other
        { Install-NexusWindowsNativeRuntime -CheckOnly } | Should -Throw '*Manifest commun non aligné*'
        (Get-NexusWindowsNativeRuntime).NativeHash | Should -BeExactly ('b' * 64)
    }
}
