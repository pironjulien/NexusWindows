# Adaptateur historique de l'installeur : tous les agents partagent le runtime immutable.
. (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'commun\native-runtime.ps1')
function Get-NexusAgyNativeHash { Get-NexusWindowsNativeHash }
function Get-NexusAgyNativeRuntime { Get-NexusWindowsNativeRuntime }
function Install-NexusAgyNativeRuntime { $null = Install-NexusWindowsNativeRuntime }
